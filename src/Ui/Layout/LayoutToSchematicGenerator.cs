using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §3A — "Update Schematic from Layout," the
/// mechanical inverse of <see cref="SchematicToLayoutGenerator"/>. Walks a layout's own
/// <see cref="LayoutInstance"/>s (never the schematic — the schematic is the thing being written TO)
/// and computes the schematic component creates/edits needed to match, plus the same shape of change
/// report R-L5-22 asks for ("on the same terms as §2.2").
///
/// <b>Scope, stated plainly (R-L5-19's own "place or update component instances. No wiring"):</b>
/// PCell-backed instances (<see cref="PCellOrigin"/> non-null) push their PARAMETERS back as well as
/// creating a component; an ORDINARY hierarchical instance — a hand-drawn cell placed in this layout —
/// is created as a plain cell-reference component and nothing more, because it has no parameter the
/// layout could have moved.
///
/// <para><b>The ordinary-cell half was missing entirely until 2026-08-17</b> (owner: "I performed an
/// Update Schematic from Layout, but my cell instance was not placed in the schematic — even though it
/// has a symbol"). The original scope note here reasoned that such an instance had "no existing symbol
/// this command could safely fabricate", and that premise is simply wrong: a schematic component
/// references a cell by <see cref="EditableComponent.CellRef"/> and resolves that cell's own primary
/// <c>.csym</c> at render time — exactly what dropping the cell from the Library palette produces
/// (<c>SchematicViewModel.CommitCellPlacementAsync</c>). Nothing is fabricated, so nothing had to be
/// guessed. The instance was skipped by a <c>continue</c> that also said NOTHING, so the command
/// reported success having quietly ignored the one instance in the layout.</para>
///
/// Framework-free except for the <see cref="IUiCommand"/>s it returns (never executes them itself —
/// same contract as <see cref="SchematicToLayoutGenerator.Run"/>).
/// </summary>
public static class LayoutToSchematicGenerator
{
    public sealed record GenerationResult(
        IUiCommand? Command,
        IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines,
        int CreatedCount,
        int UpdatedCount,
        int UnchangedCount,
        int OverwrittenParameterCount,
        IReadOnlyList<string>? CellsWithoutSymbols = null,
        bool OrientationLinksRecorded = false)
    {
        /// <summary>True when this run wrote the orientation baseline onto a LAYOUT instance — the
        /// other document, which the caller must mark modified or the link is lost at close
        /// (<see cref="LayoutInstance.OrientationLink"/>).</summary>
        public bool OrientationLinksRecorded { get; init; } = OrientationLinksRecorded;

        /// <summary>Absolute cell directories that were placed but have NO symbol view at all
        /// (<see cref="PrimaryState.NoView"/>) — so the component renders as a bare placeholder with no
        /// pins. Reported rather than resolved here because generating a symbol writes a file into
        /// ANOTHER cell's folder, which is the caller's decision to ask about, not this framework-free
        /// generator's to take. A cell that HAS symbols but no primary chosen is not in this list — that
        /// one is a warning, since picking which of several is primary is not something to guess.</summary>
        public IReadOnlyList<string> CellsWithoutSymbols { get; init; } = CellsWithoutSymbols ?? [];

        public bool NothingChanged => Command is null;
    }

    /// <summary>The kind a reference designator's letter prefix names — C, R or L and nothing else — or
    /// null. Used ONLY for a placement whose linked component the schematic no longer has.</summary>
    internal static SymbolKind? GuessKindFromName(string name)
    {
        int n = 0;
        while (n < name.Length && char.IsAsciiLetter(name[n])) n++;
        if (n == 0 || n == name.Length || !char.IsAsciiDigit(name[n])) return null;
        return name[..n].ToUpperInvariant() switch
        {
            "C" => SymbolKind.Capacitor,
            "R" => SymbolKind.Resistor,
            "L" => SymbolKind.Inductor,
            _   => null,
        };
    }

    /// <summary>Public lookup for the same generator-id → SymbolKind map §5's Properties Inspector
    /// parameter list uses to order a PCell instance's parameters the same way the schematic's own
    /// symbol declares them (<c>ComponentTypeRegistry.DefaultParameters</c>), rather than an arbitrary
    /// dictionary order.</summary>
    /// <remarks><b>The map moved to <see cref="CircuitRF.Design.Layout.Lvs.DeviceTypes"/> and this
    /// calls it</b> (R-lvs4-5d, done early by brief 3 because that brief needed the same answer
    /// below the firewall). Two maps with one meaning drift, which is this repository's recurring
    /// scar — so there is one, and it is the one LVS matches on.</remarks>
    public static bool TryGetSymbolKind(string generatorId, out SymbolKind kind) =>
        CircuitRF.Design.Layout.Lvs.DeviceTypes.TryGetSymbolKind(generatorId, out kind);

    private const int GridCols = 8;
    private const double GridPitchSchematic = 400; // schematic world units — a comfortable non-overlapping spacing

    /// <param name="technology">
    /// The resolved technology governing <paramref name="source"/> (its own <c>LayoutEditorViewModel.
    /// Technology</c>) — used ONLY for a freshly-CREATED schematic component's Length-dimensioned
    /// parameters (docs/sonnet-briefs/brief-misc-termg-units-technologies.md §2, R-misc-3/4): a
    /// layout-first PCell instance has no existing schematic parameter to read a unit from, so one
    /// must be chosen, and R-misc-4's answer is the technology's own <c>DefaultDisplayUnit</c> (mil
    /// on a PCB, µm on an MMIC die) via the SAME <see cref="MicrostripSubstrateInjection.LengthUnitFor"/>
    /// helper the schematic-side placement path (<c>SchematicViewModel.CommitPlacement</c> →
    /// <c>ApplyTechnologyLengthUnit</c>) and the MKlopf entry-mode toggle already use — never a
    /// second conversion. Null (no technology resolved) falls back to "mm", matching every other
    /// technology-absent case in this codebase. An ALREADY-LINKED component's parameter keeps
    /// whatever unit it was authored with — the schematic side is never silently rewritten to the
    /// technology default on every push, only a brand-new field picks one.
    /// </param>
    public static GenerationResult Run(LayoutView source, SchematicEditModel schematic, string layoutBaseDir,
                                       Technology? technology = null, Action? layoutChanged = null)
    {
        var linksBefore = CaptureLinks(source);
        var lines = new List<SchematicLayoutPlanner.ReportLine>();
        var noSymbol = new List<string>();

        // Which workspace's kits this run resolves against (MW1 R-mw1-5): the one the documents
        // themselves belong to, never "whichever workspace is in front". The schematic answers when
        // it has been saved; the layout it was generated from answers for a scratch one.
        string? wsRoot = WorkspaceRootFinder.WorkspaceDirOf(schematic.SchematicDirectory)
                      ?? WorkspaceRootFinder.WorkspaceDirOf(layoutBaseDir);
        IUiCommand? chain = null;
        int created = 0, updated = 0, unchanged = 0, overwritten = 0;
        bool linksRecorded = false;

        // A PLACEMENT ANGLE IS NOT A SCHEMATIC ORIENTATION (field report, 2026-09-27). A part is
        // turned on a board to fit the copper; the same part is turned on a sheet to make the drawing
        // read. Neither says anything about the other, so this command never turns a symbol: an
        // existing component keeps the rotation and mirror it was drawn with, and a component this
        // run CREATES is placed at the symbol's default facing, the way dropping it from the palette
        // places it. Until then a placement turned on the board turned its symbol on the next run,
        // and re-pointing a part at another case (which can turn it) scrambled the drawing.
        //
        // The orientation link is still maintained — Update Layout from Schematic carries a symbol
        // turned since the last sync, and it reads that baseline. What this run advances is only the
        // LAYOUT half, to where the placement is now: the board's own turn is then the baseline a later
        // schematic turn is applied to, rather than something that turn would overwrite. The schematic
        // half is left alone, so a symbol turned but not yet pushed is still pushed.
        void LinkAtDefaultFacing(EditableComponent c, LayoutInstance li)
        {
            c.Rotation = SymbolRotation.R0;
            c.MirrorX  = false;
            li.OrientationLink = SchematicLayoutOrientation.Link(0, false, SchematicLayoutOrientation.FromLayout(li));
            linksRecorded = true;
        }

        void AdvanceLayoutBaseline(LayoutInstance li, EditableComponent c)
        {
            var lNow = SchematicLayoutOrientation.FromLayout(li);
            var link = li.OrientationLink is { } b
                ? SchematicLayoutOrientation.Link(b.SchematicDeg, b.SchematicMirror, lNow)
                : SchematicLayoutOrientation.Link((int)c.Rotation, c.MirrorX, lNow);
            if (Equals(li.OrientationLink, link)) return;
            li.OrientationLink = link;
            linksRecorded = true;
        }

        // brief-footprint-6 R-fp6-1b: placements that ARE bare land patterns. Counted rather than
        // listed — a hand-authored board can hold a hundred of them, and a hundred identical lines is
        // a report nobody reads.
        int landPatterns = 0;
        var danglingLinks = new List<string>();

        // A land pattern re-pointed on the board (the Footprint picker) is a different PART SIZE, and
        // the schematic's Footprint parameter is where the part size is stated — so it follows. It was
        // left behind before, and the next Update Layout from Schematic then put the old case back.
        // Only a built-in land pattern is compared: its generator id IS the canonical Footprint
        // spelling (R-fp1-2b), so the two are equal exactly when they name the same artwork, and a
        // stored reference that already means it is never rewritten (R-fp2-1c). True when changed.
        bool PushFootprintBack(EditableComponent c, string generatorId)
        {
            if (!FootprintRef.IsBuiltInReference(generatorId)) return false;
            string? stated = c.Footprint;
            if (stated is not null && FootprintRef.TryParse(stated, out var parsed, out _)
                && string.Equals(parsed!.ToString(), generatorId, StringComparison.OrdinalIgnoreCase))
                return false;

            // No Footprint row: the component reaches its artwork another way (a cell of its own), and
            // adding one beside it would give it two.
            if (c.Parameters.FirstOrDefault(p =>
                    p.Name.Equals(ArtworkParameters.FootprintName, StringComparison.OrdinalIgnoreCase)) is not { } param)
                return false;
            chain = Chain(chain, new Commands.Schematic.EditParameterCommand(schematic, param, generatorId, param.Unit));
            lines.Add(new SchematicLayoutPlanner.ReportLine(c.InstanceName,
                $"{c.InstanceName} — footprint changed from {EditableComponent.FootprintDisplayName(stated)} " +
                $"to {EditableComponent.FootprintDisplayName(generatorId)} (from layout)",
                SchematicLayoutPlanner.ReportSeverity.Info));
            return true;
        }

        var bySchematicId = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in schematic.Components)
            if (!string.IsNullOrEmpty(c.InstanceName) && !bySchematicId.ContainsKey(c.InstanceName))
                bySchematicId[c.InstanceName] = c;

        var scope = BuildVariableScope(schematic);
        var evaluator = new Evaluator();

        // Names CLAIMED by this run's queued creates. Load-bearing, not tidy: nothing here executes a
        // command (the caller does, once, as one undoable action), so `schematic.Components` still holds
        // only what was there before the run — and `NextAvailableName` scanning it alone hands the SAME
        // name to every instance created in the same pass. Two new instances both became "X1", the second
        // PlaceComponentCommand overwrote the first's identity, and the layout's two SchematicIds then
        // pointed at one component. Found while fixing the ordinary-cell path below; it was always latent
        // on the PCell/kit path, which is why the claim list is shared rather than local to either.
        var claimed = new List<string>();
        string ClaimName(string prefix)
        {
            string name = SchematicEditModel.NextAvailableName(
                schematic.Components.Select(c => c.InstanceName).Concat(claimed), prefix);
            claimed.Add(name);
            return name;
        }

        // brief-artsch-6 R-as6-7 (D5): a placement naming a component that MODELS EXISTING ARTWORK — by its
        // link or by its own designator — is that component's copper. It creates and edits nothing; creating
        // would put a second C6 beside the recognised one.
        var fromArtwork = new HashSet<string>(
            schematic.Components.Where(c => c.FromArtwork && c.InstanceName.Length > 0).Select(c => c.InstanceName),
            StringComparer.Ordinal);

        int newSlot = 0;
        foreach (var inst in source.Instances)
        {
            if (fromArtwork.Count > 0
                && (inst.SchematicId is { Length: > 0 } sidA && fromArtwork.Contains(sidA) ? sidA
                    : inst.RefDes is { Length: > 0 } rdA && fromArtwork.Contains(rdA) ? rdA : null) is { } modelled)
            {
                lines.Add(new SchematicLayoutPlanner.ReportLine(modelled,
                    $"{modelled} is modelled from artwork — unchanged", SchematicLayoutPlanner.ReportSeverity.Info));
                unchanged++;
                continue;
            }

            var res = CellLayoutResolver.Resolve(inst, layoutBaseDir, technology);
            if (res.State != CellLayoutState.Resolved)
                continue; // broken instance — the layout editor already marks it; nothing to write

            if (res.View!.PCellOrigin is null)
            {
                // An ordinary hierarchical instance. It carries no PCell parameters, so the only thing
                // to do is the create half — and the only thing to check is whether it is already
                // linked to a component.
                if (inst.SchematicId is { Length: > 0 } psid && bySchematicId.TryGetValue(psid, out var plainComp))
                {
                    AdvanceLayoutBaseline(inst, plainComp);
                    unchanged++;
                    continue;
                }

                if (CreatePlainCellComponent(inst, res, schematic, lines, noSymbol, ClaimName) is not { } placed)
                    continue;

                placed.X = (newSlot % GridCols) * GridPitchSchematic;
                placed.Y = (newSlot / GridCols) * GridPitchSchematic;
                newSlot++;
                LinkAtDefaultFacing(placed, inst);

                chain = Chain(chain, new Commands.Schematic.PlaceComponentCommand(schematic, placed));
                inst.SchematicId = placed.InstanceName;
                bySchematicId[placed.InstanceName] = placed;
                created++;
                lines.Add(new SchematicLayoutPlanner.ReportLine(placed.InstanceName,
                    $"{placed.InstanceName} — created from layout", SchematicLayoutPlanner.ReportSeverity.Info));
                continue;
            }

            var origin = res.View!.PCellOrigin!;

            // Which component this generated cell IS, on the schematic side. A built-in answers with
            // its SymbolKind; a KIT's cell answers with the part reference the palette settled it
            // draws (KitLayoutGenerators, read in reverse). Before this, a kit generator matched
            // neither and every PDK component in a layout was silently passed over — no create, and
            // no push-back onto one already linked.
            bool builtIn = TryGetSymbolKind(origin.GeneratorId, out var kind);
            string? kitRef = builtIn ? null : KitLayoutGenerators.PartRefFor(wsRoot, origin.GeneratorId);

            // brief-footprint-6 §2/§4. A generated cell that neither a built-in nor a kit claims is
            // one of three things, and the three answers are genuinely different:
            //
            //   - artwork this schematic already owns — every SMT part Update Layout has ever placed
            //     is an instance of a bare land-pattern cell, and a land pattern has no parameters, so
            //     there is nothing to push back and the instance is UNCHANGED;
            //   - a PART this placement declares itself to be (R-fp6-2d) — a component dropped into
            //     the board from the Library palette, which is what this brief creates;
            //   - a bare land pattern nothing claims — artwork the user drew, exactly as a drawn
            //     polygon is, and Update Schematic from Layout has never invented a component for a
            //     polygon (R-fp6-1a). Nothing is created; it is COUNTED and said once (R-fp6-1b).
            //
            // Until this brief all three fell through one silent `continue`, so the command reported
            // success having ignored every one of them — the same failure the ordinary-cell path above
            // was fixed for in 2026-08-17.
            SymbolKind? layoutFirstPart = null;
            string? guessedName = null;
            if (!builtIn && kitRef is null)
            {
                if (inst.SchematicId is { Length: > 0 } landSid && bySchematicId.TryGetValue(landSid, out var landComp))
                {
                    AdvanceLayoutBaseline(inst, landComp);
                    if (PushFootprintBack(landComp, origin.GeneratorId)) updated++;
                    else unchanged++;
                    continue;
                }

                if (LayoutPartKind.Of(inst) is not { } declared)
                {
                    // A placement LINKED to a component this schematic does not have is not bare
                    // artwork — the board draws that component's name, and to the user it is that
                    // part. What the component WAS is not recorded on a linked placement (the
                    // schematic knew), so the kind is GUESSED from the name's prefix (owner decision,
                    // 2026-09-27) — here and only here: a placement whose component still exists keeps
                    // the no-guess rule that protects a renamed part (R1 -> Rin). A prefix other than
                    // C, R or L (FB1, U3) cannot be guessed, and the placement is named instead.
                    if (inst.SchematicId is not { Length: > 0 } danglingSid) { landPatterns++; continue; }
                    if (GuessKindFromName(danglingSid) is not { } guessed) { danglingLinks.Add(danglingSid); continue; }
                    declared = guessed;
                    guessedName = danglingSid;
                }

                layoutFirstPart = declared;
                kind = declared;
            }

            bool linked = inst.SchematicId is { Length: > 0 } sid0 && bySchematicId.TryGetValue(sid0, out _);
            var comp = linked ? bySchematicId[inst.SchematicId!] : null;

            if (comp is null)
            {
                // A kit part cannot be created without its kit loaded: its symbol and its parameter
                // interface both live in memory, in the kit, and a component referencing a kit that
                // is not here would place as an unresolved box with no parameters at all.
                if (kitRef is not null && PdkKitRegistry.Find(kitRef, wsRoot) is null)
                {
                    PdkKitRegistry.TryParse(kitRef, out string kitName, out string partId);
                    lines.Add(new SchematicLayoutPlanner.ReportLine(inst.CellRef,
                        $"\"{partId}\" was left alone — the kit \"{kitName}\" is not loaded in this " +
                        "workspace, so there is no part to create.",
                        SchematicLayoutPlanner.ReportSeverity.Warning));
                    continue;
                }

                // R-fp6-3a: a layout-first PART is named by its OWN RefDes, not by ClaimName. The
                // board already draws that name on silkscreen, and a back-annotation that renumbers it
                // produces a schematic that disagrees with copper the user is looking at. Keeping the
                // name free is R-fp6-4's job; if it was not free anyway, the collision is reported and
                // the instance is LEFT ALONE rather than renamed.
                string instanceName;
                if (layoutFirstPart is not null && (guessedName ?? inst.RefDes) is { Length: > 0 } ownName)
                {
                    if (bySchematicId.ContainsKey(ownName) || claimed.Contains(ownName))
                    {
                        lines.Add(new SchematicLayoutPlanner.ReportLine(ownName,
                            $"{ownName} is already a component in this schematic, so the part drawn as " +
                            $"{ownName} on the board was left alone — nothing was created and nothing " +
                            "was renamed. Rename one of the two and run this again.",
                            SchematicLayoutPlanner.ReportSeverity.Warning));
                        continue;
                    }
                    claimed.Add(ownName);
                    instanceName = ownName;
                }
                else
                {
                    instanceName = ClaimName(kitRef is not null ? "X" : ComponentTypeRegistry.InstancePrefix(kind));
                }

                // R-L5-20: create half — writes SchematicId as it goes.
                comp = kitRef is not null
                    ? NewCellComponent(kitRef, schematic, instanceName)
                    : new EditableComponent
                      {
                          Symbol       = kind,
                          InstanceName = instanceName,
                      };
                comp.X = (newSlot % GridCols) * GridPitchSchematic;
                comp.Y = (newSlot / GridCols) * GridPitchSchematic;
                newSlot++;
                LinkAtDefaultFacing(comp, inst);
                if (kitRef is null)
                    foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
                        comp.Parameters.Add(new EditableParameter
                        {
                            Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                            ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
                        });
                ApplyPCellParamsToComponent(comp, origin.Parameters, technology);

                // R-fp6-3c: the artwork it already has. Without it the very next Update Layout from
                // Schematic would report the brand-new component as having no artwork — while its
                // artwork sits on the board. The value is the land pattern's own generator id, which
                // is exactly the spelling a Footprint parameter takes.
                if (layoutFirstPart is not null)
                    comp.Parameters.Add(new EditableParameter
                    {
                        Name            = ArtworkParameters.FootprintName,
                        Expression      = origin.GeneratorId,
                        ShowOnSchematic = false,
                    });

                chain = Chain(chain, new Commands.Schematic.PlaceComponentCommand(schematic, comp));
                inst.SchematicId = comp.InstanceName; // written now; LayoutLinkCommand puts it in the undo
                                                       // entry, with SchematicPCellSnapshots below.
                // R-fp6-3d: linking TRANSFERS the name, it does not copy it. An instance with a
                // SchematicId stores no RefDes (R-fp4b-1a) — two fields with one meaning drift — and
                // DisplayRefDes ALREADY prefers SchematicId, so clearing this changes nothing that is
                // drawn and only removes data that could later disagree. Unconditional, because a
                // PASTED instance now arrives carrying a seeded RefDes whatever kind it is.
                inst.RefDes = null;
                // PartKind goes with it on a part, for the same reason: the schematic now knows.
                if (layoutFirstPart is not null) inst.PartKind = null;
                created++;
                lines.Add(guessedName is null
                    ? new SchematicLayoutPlanner.ReportLine(comp.InstanceName,
                          $"{comp.InstanceName} — created from layout", SchematicLayoutPlanner.ReportSeverity.Info)
                    : new SchematicLayoutPlanner.ReportLine(comp.InstanceName,
                          $"{comp.InstanceName} — created from layout as a {kind.ToString().ToLowerInvariant()}, " +
                          "guessed from its name, because the schematic had lost the part it was linked to. " +
                          "Its value is the default; set it.", SchematicLayoutPlanner.ReportSeverity.Warning));
                source.SchematicPCellSnapshots[comp.InstanceName] = new Dictionary<string, PCellValue>(origin.Parameters);
                continue;
            }

            // R-L5-19/22: linked — push parameters back, classified the same way §2.2 classifies the
            // forward direction, roles reversed: a SCHEMATIC value that has diverged from the snapshot
            // is about to be discarded (warning); a value moving purely because the LAYOUT changed is
            // the expected case (informational).
            source.SchematicPCellSnapshots.TryGetValue(comp.InstanceName, out var snapshot);
            bool reportedThisInstance = false;
            bool anyChanged = false;

            foreach (var (name, layoutVal) in origin.Parameters)
            {
                var param = comp.Parameters.FirstOrDefault(p => p.Name == name);
                if (param is null && name == "L" && layoutVal.Kind == PCellValueKind.Real
                    && LineEntryConversion.AngleDegreesFor(comp, technology, layoutVal.AsReal()) is { } eDeg
                    && comp.Parameters.FirstOrDefault(p => p.Name == "E") is { } eRow
                    && eRow.Expression.Trim() != eDeg)
                {
                    // A line stated electrically takes the layout's length as its angle at its own F — writing L
                    // beside the E it already states would leave the two disagreeing (designer feedback round 21).
                    chain = Chain(chain, new Commands.Schematic.EditParameterCommand(schematic, eRow, eDeg, "deg"));
                    anyChanged = true;
                    lines.Add(new SchematicLayoutPlanner.ReportLine(comp.InstanceName,
                        $"{comp.InstanceName} — E changed from {eRow.Expression} {eRow.Unit} to {eDeg} deg (from layout)",
                        SchematicLayoutPlanner.ReportSeverity.Info));
                    continue;
                }
                if (param is null) continue;
                if (!SchematicLayoutPlanner.TryResolveSiValue(param.Expression, param.Unit, scope, evaluator, out var schematicVal, out _))
                    continue;

                // The schematic side can only ever produce a number (a parameter is an expression),
                // so its value enters the comparison as a Real — a layout parameter of some other
                // kind therefore always reads as changed, which is correct: it cannot be expressed by
                // the schematic value it is being compared against.
                PCellValue schematicValue = schematicVal;
                if (SchematicLayoutPlanner.SameParamValue(schematicValue, layoutVal)) continue;

                bool hadSnapshot    = snapshot is not null && snapshot.ContainsKey(name);
                PCellValue snap     = hadSnapshot ? snapshot![name] : schematicValue;
                bool schematicMoved = hadSnapshot && !SchematicLayoutPlanner.SameParamValue(snap, schematicValue);
                bool layoutMoved    = !hadSnapshot || !SchematicLayoutPlanner.SameParamValue(snap, layoutVal);

                bool isWarning = schematicMoved; // the schematic's own edit is what's about to be lost
                if (!schematicMoved && !layoutMoved) continue;

                string displayExpr = ToDisplayExpression(param.Unit, layoutVal);
                chain = Chain(chain, new Commands.Schematic.EditParameterCommand(schematic, param, displayExpr, param.Unit));
                anyChanged = true;

                string unitSuffix = string.IsNullOrEmpty(param.Unit) ? "" : $" {param.Unit}";
                lines.Add(new SchematicLayoutPlanner.ReportLine(comp.InstanceName,
                    $"{comp.InstanceName} — {name} changed from {SchematicLayoutPlanner.FormatParamValue(param.Unit, schematicValue)}{unitSuffix} " +
                    $"to {SchematicLayoutPlanner.FormatParamValue(param.Unit, layoutVal)}{unitSuffix}" +
                    (isWarning ? " (a schematic edit is being overwritten)" : " (from layout)"),
                    isWarning ? SchematicLayoutPlanner.ReportSeverity.Warning : SchematicLayoutPlanner.ReportSeverity.Info));
                reportedThisInstance = true;
                if (isWarning) overwritten++;
            }

            source.SchematicPCellSnapshots[comp.InstanceName] = new Dictionary<string, PCellValue>(origin.Parameters);

            AdvanceLayoutBaseline(inst, comp);

            if (anyChanged)
            {
                updated++;
                if (!reportedThisInstance)
                    lines.Add(new SchematicLayoutPlanner.ReportLine(comp.InstanceName, $"{comp.InstanceName} — updated", SchematicLayoutPlanner.ReportSeverity.Info));
            }
            else
            {
                unchanged++;
            }
        }

        // R-fp6-1b/1c: ONE aggregate line, at Info. Nothing is wrong — the user placed artwork and got
        // artwork — but "nothing happened and nothing was said" is indistinguishable from a broken
        // command, which is the failure the forward direction's own skip report already exists to
        // prevent.
        if (landPatterns > 0)
            lines.Add(new SchematicLayoutPlanner.ReportLine("",
                $"{landPatterns} placement{(landPatterns == 1 ? " is a land pattern" : "s are land patterns")} " +
                "with no part behind them, so no components were created for them. Drop a component from " +
                "the Library palette to place a part that has one.",
                SchematicLayoutPlanner.ReportSeverity.Info));

        // Named, and a Warning: unlike a bare land pattern, this is two documents disagreeing. The
        // usual cause is a schematic closed without saving while the layout was saved; a C/R/L name is
        // recreated above, so what reaches this list is a name whose kind cannot be guessed.
        if (danglingLinks.Count > 0)
        {
            const int shown = 12;
            string names = string.Join(", ", danglingLinks.Take(shown))
                         + (danglingLinks.Count > shown ? $" and {danglingLinks.Count - shown} more" : "");
            lines.Add(new SchematicLayoutPlanner.ReportLine("",
                $"{names} {(danglingLinks.Count == 1 ? "is" : "are")} linked to " +
                $"{(danglingLinks.Count == 1 ? "a component" : "components")} this schematic does not have, " +
                "so nothing was created for " + (danglingLinks.Count == 1 ? "it" : "them") +
                ": the layout does not record what kind of part each one was. Place " +
                (danglingLinks.Count == 1 ? "it" : "them") + " in the schematic under the same name " +
                "and run this again to relink — or, if the schematic was not saved, reopen the version " +
                "that has them.",
                SchematicLayoutPlanner.ReportSeverity.Warning));
        }

        // R-fp6-3f: R-L5-19 stands — this command places and updates components and draws no wires, so
        // a layout-first board back-annotates to correctly named, correctly footprinted, UNCONNECTED
        // parts. That is a BOM round trip, not yet a design flow, and the user is told rather than left
        // to discover it. Deriving nets from copper belongs to brief-authored-board-2.
        if (created > 0)
            lines.Add(new SchematicLayoutPlanner.ReportLine("",
                $"{created} component{(created == 1 ? " was" : "s were")} created from this layout. " +
                "No nets were derived from the copper — this command places and updates components and " +
                "draws no wires, so the parts it created are unconnected.",
                SchematicLayoutPlanner.ReportSeverity.Info));

        // Only when there IS a schematic edit to undo: a run that merely advanced orientation baselines
        // changed nothing a user would undo, and stays NothingChanged.
        if (chain is not null)
            chain = Chain(new LayoutLinkCommand(source, linksBefore, CaptureLinks(source), layoutChanged), chain);

        return new GenerationResult(chain, lines, created, updated, unchanged, overwritten, noSymbol, linksRecorded);
    }

    /// <summary>
    /// The component for an ordinary hierarchical instance — a cell the user drew and placed in this
    /// layout, which is the case that used to be skipped in silence.
    ///
    /// <para>Returns null only when the reference cannot be EXPRESSED: an unsaved schematic has no
    /// directory to make a cell path relative to. That is reported rather than skipped, because
    /// "nothing happened and nothing was said" is precisely the failure this method exists to end.</para>
    ///
    /// <para>A cell with no primary symbol is still created — refusing would reintroduce the silence in
    /// a narrower form, and the component is the thing that makes the missing symbol visible at all.
    /// <b>Which KIND of "no primary" it is decides what happens next, and the two are not the same
    /// question</b> (owner, 2026-08-17: an instance placed by this command "does not render the pins").
    /// A cell with NO symbol view is collected into <paramref name="noSymbol"/> for the caller to offer
    /// to generate one — the same offer <c>SchematicViewModel.CommitCellPlacementAsync</c> makes when
    /// the very same cell is dropped from the Library palette, and the reason the two paths disagreed
    /// was that this one treated every "no primary" as the palette's OTHER branch. A cell that has
    /// symbols but no primary chosen stays a plain warning: which of several is primary is a question
    /// only the user can answer, and generating a further one would answer it by adding to the pile.</para>
    /// </summary>
    private static EditableComponent? CreatePlainCellComponent(
        LayoutInstance inst, CellLayoutResolution res, SchematicEditModel schematic,
        List<SchematicLayoutPlanner.ReportLine> lines, List<string> noSymbol,
        Func<string, string> claimName)
    {
        if (schematic.SchematicDirectory is not { Length: > 0 } schematicDir)
        {
            lines.Add(new SchematicLayoutPlanner.ReportLine(inst.CellRef,
                $"\"{Path.GetFileName(res.ResolvedCellDir)}\" was left alone — save the schematic first, " +
                "so a cell reference has somewhere to be relative to.",
                SchematicLayoutPlanner.ReportSeverity.Warning));
            return null;
        }

        string cellRef = RefPath.ToStored(Path.GetRelativePath(schematicDir, res.ResolvedCellDir!));

        var symbol = CellSymbolResolver.Resolve(cellRef, schematicDir);
        if (symbol.State == CellSymbolState.PrimaryMissing)
        {
            string cellName = Path.GetFileName(res.ResolvedCellDir)!;
            if (CellFolder.ResolvePrimary(res.ResolvedCellDir!, ViewType.Symbol).State == PrimaryState.NoView)
                noSymbol.Add(res.ResolvedCellDir!);
            else
                lines.Add(new SchematicLayoutPlanner.ReportLine(inst.CellRef,
                    $"\"{cellName}\" has several symbols and no primary chosen — placed with a placeholder " +
                    "until one is made primary.",
                    SchematicLayoutPlanner.ReportSeverity.Warning));
        }

        return NewCellComponent(cellRef, schematic, claimName("X"));
    }

    /// <summary>
    /// A brand-new schematic component for a cell reference — a kit part or an ordinary cell folder —
    /// seeded exactly as PLACING it seeds one (<c>SchematicViewModel.CommitCellPlacementAsync</c>): the
    /// placeholder kind every cell reference shares, the reference that resolves its symbol, an "X"
    /// instance name, and the cell's own published parameter interface read through the one accessor.
    ///
    /// <para>Not a second seeding rule — a component created from a layout and one dropped from the
    /// palette have to be the same component, or the same part means two different things depending
    /// on which end of the flow it entered from. The two callers differ only in how the reference is
    /// spelled (a kit's is virtual and needs no base directory; a cell's is a relative path), which is
    /// resolved before this point.</para>
    /// </summary>
    private static EditableComponent NewCellComponent(string cellRef, SchematicEditModel schematic, string instanceName)
    {
        var comp = new EditableComponent
        {
            InstanceName     = instanceName,
            Symbol           = SymbolKind.Generic,   // placeholder; rendering uses CellRef when set
            CellRef          = cellRef,
            ShowTypeLabel    = true,
            ShowInstanceName = true,
        };

        if (CellSymbolResolver.ResolveCcell(cellRef, schematic.SchematicDirectory ?? "") is { } ccell)
            foreach (var cp in ccell.Parameters)
                comp.Parameters.Add(new EditableParameter
                {
                    Name            = cp.Name,
                    Expression      = cp.DefaultExpression,
                    Unit            = cp.Unit,
                    Dimension       = cp.Dimension,
                    ShowOnSchematic = cp.ShowOnSchematic,
                });

        return comp;
    }

    /// <summary>R-misc-3/4: writes coefficient AND unit for a freshly-created component's parameters
    /// — a Length-dimensioned field's <see cref="EditableParameter.Unit"/> is rewritten from
    /// <c>DefaultParameters</c>' hardcoded "mm" baseline to <paramref name="technology"/>'s own
    /// <see cref="MicrostripSubstrateInjection.LengthUnitFor"/> BEFORE the coefficient is computed
    /// through it, so the two can never disagree (unlike writing the number first and the unit
    /// separately). Non-Length fields (Ω, dimensionless) keep whatever unit <c>DefaultParameters</c>
    /// already gave them — only length physically differs by workspace convention.</summary>
    private static void ApplyPCellParamsToComponent(EditableComponent comp, IReadOnlyDictionary<string, PCellValue> layoutParams, Technology? technology)
    {
        string lengthUnit = MicrostripSubstrateInjection.LengthUnitFor(technology);
        foreach (var param in comp.Parameters)
        {
            if (!layoutParams.TryGetValue(param.Name, out var v)) continue;
            if (param.Dimension == UnitDimension.Length) param.Unit = lengthUnit;
            param.Expression = ToDisplayExpression(param.Unit, v);
        }
    }

    /// <summary>Schematic Expression string for a PCell-SI value — <see cref="SchematicLayoutPlanner.ToDisplayValue"/>
    /// (the shared inverse conversion) formatted the way an <see cref="EditableParameter.Expression"/>
    /// is stored: a bare number in the parameter's own unit.</summary>
    /// <summary>
    /// A layout value as the schematic <c>Expression</c> that means the same thing.
    ///
    /// <para>Anything that IS a number goes back through the unit the schematic row is edited in —
    /// <b>including a number spelled as text</b>, which is how a vendor cell states a dimension. That
    /// clause is load-bearing rather than tidy: without it a cell reporting <c>3E-05</c> (metres, its
    /// own declared kind) would be written verbatim into a row whose unit is µm, and the schematic
    /// would read 30,000 µm on the way back — a silent factor of a million, from a command whose whole
    /// purpose is to keep the two views agreeing.</para>
    ///
    /// <para>A value that is not a number is written as its own text: a schematic <c>Expression</c> is
    /// free-form, so a model name pushes back as that name.</para>
    /// </summary>
    private static string ToDisplayExpression(string? unit, PCellValue value)
    {
        if (!SchematicLayoutPlanner.TryAsNumber(value, out double n)) return value.AsText();

        double display = SchematicLayoutPlanner.ToDisplayValue(unit, n);
        string rounded = display.ToString("0.######", CultureInfo.InvariantCulture);

        // Six decimal places is a readable number in the unit a row is normally edited in — 42 mil,
        // 1.5 mm. A kit part's row carries NO unit, so the whole value sits after the decimal point
        // and six places cannot say 6.99 µm: it becomes 0.000007, and the schematic quietly disagrees
        // with the artwork it was just generated from. So the readable form is used only when it
        // still means the same number, and the value itself is written when it does not.
        return double.TryParse(rounded, NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
            && SchematicLayoutPlanner.NearlyEqual(back, display)
                ? rounded
                : display.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Scope BuildVariableScope(SchematicEditModel schematic)
    {
        var scope = new Scope("global");
        foreach (var comp in schematic.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol != SymbolKind.Var) continue;
            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
                scope.Bind(p.Name.Trim(), p.Expression, unit);
            }
        }
        return scope;
    }

    private static IUiCommand Chain(IUiCommand? existing, IUiCommand next)
        => existing is null ? next : new CompositeCommand(existing, next);

    /// <summary>One placement's link to the schematic — everything this command writes on the LAYOUT.</summary>
    private readonly record struct LinkState(string? SchematicId, string? RefDes, string? PartKind, OrientationLink? Orientation);

    private static (Dictionary<LayoutInstance, LinkState> Links, Dictionary<string, Dictionary<string, PCellValue>> Snapshots)
        CaptureLinks(LayoutView source)
        => (source.Instances.ToDictionary(i => i, i => new LinkState(i.SchematicId, i.RefDes, i.PartKind, i.OrientationLink)),
            new Dictionary<string, Dictionary<string, PCellValue>>(source.SchematicPCellSnapshots, StringComparer.Ordinal));

    /// <summary>
    /// Puts the layout's links into the SCHEMATIC's undo entry (owner decision, 2026-09-27). The run
    /// writes them on the layout as it goes, so undoing only the schematic half left placements linked
    /// to components the undo had just removed — the orphans a later sync could only name. Undo puts the
    /// links back as they were before the run and Redo as they were after it; both act on the open
    /// layout's in-memory model and mark it dirty, so nothing is written to a file behind anyone's back.
    /// A layout closed in between holds its own copy, which this never reaches — and a sync on that copy
    /// recovers the orphans by their names.
    /// </summary>
    private sealed class LayoutLinkCommand(
        LayoutView source,
        (Dictionary<LayoutInstance, LinkState> Links, Dictionary<string, Dictionary<string, PCellValue>> Snapshots) before,
        (Dictionary<LayoutInstance, LinkState> Links, Dictionary<string, Dictionary<string, PCellValue>> Snapshots) after,
        Action? layoutChanged) : IUiCommand
    {
        public string Description => "Update Schematic from Layout";
        public void Execute() => Apply(after);
        public void Undo() => Apply(before);

        private void Apply((Dictionary<LayoutInstance, LinkState> Links, Dictionary<string, Dictionary<string, PCellValue>> Snapshots) state)
        {
            foreach (var (inst, s) in state.Links)
                (inst.SchematicId, inst.RefDes, inst.PartKind, inst.OrientationLink) = (s.SchematicId, s.RefDes, s.PartKind, s.Orientation);
            source.SchematicPCellSnapshots.Clear();
            foreach (var (k, v) in state.Snapshots) source.SchematicPCellSnapshots[k] = v;
            layoutChanged?.Invoke();
        }
    }
}
