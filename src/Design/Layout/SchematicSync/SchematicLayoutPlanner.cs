using System.Globalization;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// Which layout generator a kit part places — the palette's answer (<c>KitLayoutGenerators.For</c> in
/// <c>src/Ui</c>), handed to the planner because the palette that settles it is a UI object. Null from it,
/// or no lookup at all, means the part id.
/// </summary>
public delegate string? KitLayoutGeneratorLookup(string? workspaceRootDir, string kitName, string partId);

/// <summary>
/// docs/design/layout-view.md §9 + docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2, planned here since
/// brief-sdl-0 R-sdl0-1. Walks a schematic's own component instances (via <see cref="NetExtractor"/> — never
/// re-traversed independently, per §9 step 1) and decides the <see cref="LayoutInstance"/> adds, replacements
/// and deletions a target <see cref="LayoutView"/> needs to match it, the ground artwork it draws, and a
/// human-readable change report (R-L5-5/§2.2) — all returned as a <see cref="SchematicLayoutPlan"/>.
///
/// <para><b>Pure with respect to the target.</b> Nothing here mutates <c>target</c>, any instance in it or any
/// of its side tables: what a run changes is in the plan, and the caller applies it — the GUI as one undoable
/// command (<c>SchematicToLayoutGenerator.Run</c>, R-L5-12: "the whole re-run is one undoable action"), a
/// headless caller with <see cref="SchematicLayoutPlan.ApplyTo"/>. The one side effect it does have is on
/// disk: resolving a PCell component creates or reuses its generated cell folder through
/// <see cref="GeneratedCellStore"/> (R-L5-1), because the cell's geometry is what placement measures.</para>
///
/// <para>Framework-free, and side-effect-free on the SCHEMATIC side — it reads <c>model</c> but never
/// mutates it.</para>
/// </summary>
public static partial class SchematicLayoutPlanner
{
    public enum ReportSeverity { Info, Warning }

    /// <summary>
    /// One line of a change report. <paramref name="InstanceName"/> names the instance the line is
    /// about, and the caller groups and caps on it.
    ///
    /// <para><b>An EMPTY <paramref name="InstanceName"/> means the line is about the RUN, not about
    /// an instance</b> (brief-footprint-6 R-fp6-1b) — an aggregate count, or the closing statement of
    /// what the command did and did not do. A run-level line is posted whether or not anything
    /// changed, and is outside the per-instance cap: R-L5-14's "nothing changed, say nothing" is a
    /// rule about per-instance noise, and a run that deliberately created nothing and said so is the
    /// one case where silence is indistinguishable from a broken command.</para>
    /// </summary>
    public sealed record ReportLine(string InstanceName, string Text, ReportSeverity Severity)
    {
        /// <summary>Which group of the connected-placement report the line belongs to (brief-sdl-3 R-sdl3-2), or
        /// <see cref="ReportKind.General"/> for every other line, which is posted as it always was.</summary>
        public ReportKind Kind { get; init; }

        /// <summary>The schematic ids the line is about — what a click on it selects in both views. Empty for a
        /// line that names no part.</summary>
        public IReadOnlyList<string> Ids { get; init; } = [];

        // Value equality over the ids too: the plan-equivalence gate compares two runs' lines.
        public bool Equals(ReportLine? other) =>
            other is not null && InstanceName == other.InstanceName && Text == other.Text && Severity == other.Severity
            && Kind == other.Kind && Ids.SequenceEqual(other.Ids, StringComparer.Ordinal);

        public override int GetHashCode() => HashCode.Combine(InstanceName, Text, Severity, Kind, Ids.Count);
    }

    /// <summary>The groups the connected-placement report is posted in (brief-sdl-3 R-sdl3-2), in that order.</summary>
    public enum ReportKind
    {
        /// <summary>Any other line: added, updated, renamed, the run's summary.</summary>
        General,
        /// <summary>A loop, joint, bridging part or side-on pad that does not close (S1).</summary>
        LoopGap,
        /// <summary>A net or connection the placement cannot join, and why.</summary>
        Unjoined,
        /// <summary>Copper of different nets that overlaps or touches (<see cref="PlacementOverlaps"/>).</summary>
        Overlap,
        /// <summary>A note about how one part's pins were read: a pad with nothing to face away from, a pin with no
        /// copper under it or no stated width, a third pad at one joint.</summary>
        FootprintNote,
        /// <summary>brief-sdl-5 D2: a schematic Port the run could not put on the layout, to be placed by hand.</summary>
        PortByHand,
    }

    /// <summary>One group of the connected-placement report: its heading, its severity (the worst of its lines)
    /// and its lines.</summary>
    public sealed record ReportGroup(ReportKind Kind, string Heading, ReportSeverity Severity, IReadOnlyList<ReportLine> Lines);

    /// <summary>
    /// brief-sdl-3 R-sdl3-2: the connected-placement lines of a run, grouped by kind so a large design's report is
    /// readable — one group per kind that has lines, in <see cref="ReportKind"/>'s order, each line in the order the
    /// run produced it. <see cref="ReportKind.General"/> lines are not grouped.
    /// </summary>
    public static IReadOnlyList<ReportGroup> GroupConnectionReport(IEnumerable<ReportLine> lines)
    {
        var groups = new List<ReportGroup>();
        foreach (var g in lines.Where(l => l.Kind != ReportKind.General).GroupBy(l => l.Kind).OrderBy(g => g.Key))
        {
            var members = g.ToList();
            int n = members.Count;
            string heading = g.Key switch
            {
                ReportKind.LoopGap => n == 1 ? "1 joint does not close" : $"{n} joints do not close",
                ReportKind.Unjoined => n == 1 ? "1 connection is not joined" : $"{n} connections are not joined",
                ReportKind.Overlap => n == 1 ? "1 overlap of unconnected copper" : $"{n} overlaps of unconnected copper",
                ReportKind.PortByHand => n == 1 ? "1 port to place by hand" : $"{n} ports to place by hand",
                _ => n == 1 ? "1 footprint note" : $"{n} footprint notes",
            };
            var severity = members.Any(l => l.Severity == ReportSeverity.Warning) ? ReportSeverity.Warning : ReportSeverity.Info;
            groups.Add(new ReportGroup(g.Key, heading, severity, members));
        }
        return groups;
    }

    // Excluded from the parameter dict handed to a PCell generator — layer-selection inputs
    // (consumed separately, below) and non-numeric/UI-only fields, mirroring NetExtractor.EmitInstance's
    // own exclusion set exactly so the artwork and electrical sides never disagree about "the one list."
    //
    // ModelLibrary is circuitRF's OWN routing parameter — "evaluate this instance with a different
    // model library" — and it is a FILE PATH, on every kit part, blank by default. It has nothing to
    // do with artwork, so a generator must never be shown it. Feeding it to the numeric resolver is
    // what produced "parameter 'ModelLibrary': no value set — skipped" for every placed kit part, and
    // then "Parse error at position 0: Unexpected token '/'" for anyone who tried to make that message
    // go away by filling the row in. NetExtractor.EmitInstance skips it for the same reason on the
    // electrical side ("rides in the provider name"), which is the exclusion set this one mirrors.
    private static readonly HashSet<string> NonPCellParamNames =
        new(StringComparer.Ordinal)
        {
            "SignalLayer", "GroundReference", "CvData", "ShowBias",
            PdkPartInstaller.ModelLibraryParameter,
            // `Footprint` is artwork, but it is not a DIMENSION — it names which land pattern this
            // instance is, which is this generator's own business and never a generator's input
            // (brief-footprint-2 R-fp2-6c). Without this an MLIN carrying a stray footprint would
            // hand `smt:0402@N` to MlinPCell as a parameter, which reads it as a real and gets zero.
            ArtworkParameters.FootprintName,
        };

    private const int GridCols = 8;

    /// <summary>
    /// The placement pitch used only when NOTHING being placed can be measured — 10 mm at the
    /// app-wide 1 DBU = 1 nm resolution.
    ///
    /// <para><b>OWNER REPORT (2026-09-15): three kit cells written into a layout and only one of them
    /// visible.</b> All three were there and all three resolved; they were at x = 0, 10 mm and 20 mm,
    /// because this was the pitch for every placement regardless of what was being placed. These
    /// parts measure 84 and 250 µm, so the command scattered the design across 20 mm of empty wafer —
    /// forty cell-widths between neighbours — and a view framed on the first one contains none of the
    /// others. The original note here called the constant "crude and non-overlapping for realistic
    /// microstrip parts", and on a BOARD it is; the assumption that a part is millimetres across is
    /// what does not survive contact with a MMIC.</para>
    ///
    /// <para>A fixed pitch cannot be right, because this command places whatever the schematic names
    /// and circuitRF spans four orders of magnitude of part size. <see cref="PlaceNewInstances"/>
    /// measures the cells instead. This remains for the case where there is nothing to measure — a
    /// reference that resolves to no geometry — where a crude answer is still better than stacking
    /// everything on the origin.</para>
    /// </summary>
    private const long GridPitchDbu = 10_000_000;

    /// <summary>Clear space between neighbours, as a fraction of the median cell placed. Half a cell
    /// reads as "laid out, not touching" at any scale, which is the whole point of measuring — and the
    /// MEDIAN, not the largest, so one long line does not push every part out of view.</summary>
    private const long GridGapNumerator = 1, GridGapDenominator = 2;

    /// <summary>
    /// What a run would do to <paramref name="target"/> — see the class note. Takes what the GUI command
    /// takes; <paramref name="linkOrientation"/> and <paramref name="pourMarginHeights"/> are preferences and
    /// therefore arguments (<c>src/Design/CLAUDE.md</c>), and <paramref name="kitLayoutGenerator"/> is the
    /// palette's kit-part answer (<see cref="KitLayoutGeneratorLookup"/>). <paramref name="connectParts"/> is the
    /// Connect parts setting (brief-sdl-1 R-sdl1-4): on, parts the schematic joins are placed so their pins meet
    /// (<see cref="ConnectChains"/>); off, every new part goes into the rows exactly as before the setting existed.
    ///
    /// <para>brief-sdl-4 R-sdl4-2: <paramref name="resnap"/> names linked parts to take the schematic's parameter values
    /// and be re-joined to their neighbours by the chain rule (Re-snap), closing joints the user opened;
    /// <paramref name="restoreRemoved"/> puts back the linked parts the user deleted (Restore Removed Parts). Neither
    /// is a placement decision of its own — both are this plan with a different starting point.</para>
    /// </summary>
    public static SchematicLayoutPlan Plan(
        SchematicEditModel model,
        LayoutView target,
        string schematicDir,
        string workspaceRootDir,
        string targetLayoutBaseDir,
        Technology? technology,
        string? techIdentity,
        ICellResolver? cellResolver,
        string? targetLayoutPath = null,
        bool linkOrientation = true,
        double pourMarginHeights = GroundPourPlanner.DefaultMarginHeights,
        KitLayoutGeneratorLookup? kitLayoutGenerator = null,
        bool connectParts = true,
        IReadOnlySet<string>? resnap = null,
        bool restoreRemoved = false)
    {
        resnap ??= new HashSet<string>(StringComparer.Ordinal);
        // Re-snap never moves a locked part: it stays where it is, and the parts re-snapped beside it join it there.
        var lockedResnap = target.Instances.Where(i => i.Locked && i.SchematicId is { } id && resnap.Contains(id))
                                           .Select(i => i.SchematicId!).Order(StringComparer.Ordinal).ToList();
        if (lockedResnap.Count > 0) resnap = resnap.Except(lockedResnap, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var extraction = NetExtractor.Extract(model, "tb", cellResolver);
        var ledger = new Ledger(target, kitLayoutGenerator);

        var scope = new Scope("global");
        foreach (var v in extraction.TestBench.GlobalVariables)
            scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();

        // brief-artsch-6 R-as6-7 (D5): a component that MODELS EXISTING ARTWORK — Create Schematic from Artwork
        // made it — is skipped before resolution: no add, no update, no orphan report or deletion, no ground
        // drawn for it. Without this a recognised schematic in the artwork's own cell would have its MLINs
        // generated as PCell copper on top of the imported board. Clearing the flag hands it back.
        //
        // ONLY IN THAT LAYOUT (designer report, round 15). The artwork exists in the layout the schematic was
        // recognised from — the provenance names it — and nowhere else, so updating any OTHER layout (the new
        // recognised cell's own, empty one) generates them like any component; skipping them there left an empty
        // layout and the note "48 components model existing artwork — not generated". A schematic with no stated
        // provenance, or a run with no target path, keeps the skip: generating copper over a board is the worse error.
        bool overArtwork = !GeneratesArtworkModels(model, schematicDir, targetLayoutPath);
        var fromArtwork = overArtwork ? model.Components.Where(c => c.FromArtwork && IsPhysical(c)).ToList() : [];
        // brief-sdl-5: with Connect parts on, a Port is where the circuit is driven, not a part — it becomes an EM port
        // label (PortPlacement) once every part is placed, instead of "no layout view — skipped". Off, the plan is the
        // one from before the series, which reported it.
        var physical = model.Components.Where(c => IsPhysical(c) && !(overArtwork && c.FromArtwork)
                                                   && !(connectParts && PortPlacement.IsPort(c))).ToList();

        var existingBySchematicId = new Dictionary<string, (int Index, LayoutInstance Instance)>(StringComparer.Ordinal);
        for (int i = 0; i < target.Instances.Count; i++)
            if (target.Instances[i].SchematicId is { Length: > 0 } sid && !existingBySchematicId.ContainsKey(sid))
                existingBySchematicId[sid] = (i, target.Instances[i]);

        // Every component resolves once, here or in the loop — the rename pairing below needs a
        // newcomer's cell before the loop reaches it, and resolving generates PCell artwork.
        var resolved = new Dictionary<EditableComponent, Resolution>(ReferenceEqualityComparer.Instance);
        Resolution ResolveOnce(EditableComponent comp)
        {
            if (resolved.TryGetValue(comp, out var r)) return r;
            string? cellRef = ResolveComponentLayout(
                comp, model, schematicDir, workspaceRootDir, targetLayoutBaseDir, ledger, technology, techIdentity,
                scope, evaluator, out var pp, out var gid, out var warn, out var diags, out var refusal);
            return resolved[comp] = new Resolution(cellRef, pp, gid, warn, diags, refusal);
        }

        var renamedFrom = PairRenames(physical, existingBySchematicId, ResolveOnce);

        var seenSchematicIds = new HashSet<string>(
            fromArtwork.Select(c => c.InstanceName).Where(n => n.Length > 0), StringComparer.Ordinal);
        var newInstances = new List<(int Slot, LayoutInstance Instance)>();
        var deleteIndices = new List<int>();
        var lines = new List<ReportLine>();
        foreach (string id in lockedResnap)
            // Run-level (no instance name), so it is said even when the run changes nothing: it answers the user's own Re-snap.
            lines.Add(new ReportLine("", $"{id} — locked, so Re-snap left it as it is (unlock it to re-snap it)", ReportSeverity.Info) { Ids = [id] });
        var layoutKept = new List<(string SchematicId, string Parameter)>();   // brief-sdl-6 D4
        var noLayoutWarnings = new List<string>();
        var noLayout = new List<EditableComponent>();   // brief-sdl-3: parts joined only through one of these are placed apart
        var replacedAt = new Dictionary<int, LayoutInstance>();
        // In the order the GUI's command chain applies them: adds and replacements in schematic order, then the
        // deletions, then the ground ties. Placement records are attached at the end, once every part is where
        // it finally goes.
        var steps = new List<Step>();
        // The facing each NEW part's symbol gives it, whether or not the orientation link places it so — the chain
        // takes its mirror from here (brief-sdl-1 D2), because which way a bend turns is the drawing's to say.
        var schematicFacing = new Dictionary<LayoutInstance, VisualOrientation>(ReferenceEqualityComparer.Instance);
        int added = 0, updated = 0, unchanged = 0, overwritten = 0;
        int unlinkedDiffering = 0;
        bool linksRecorded = false;

        for (int slot = 0; slot < physical.Count; slot++)
        {
            var comp = physical[slot];
            string schematicId = comp.InstanceName;
            if (string.IsNullOrEmpty(schematicId)) continue; // can't track an unnamed instance idempotently
            seenSchematicIds.Add(schematicId);

            var (resolvedCellRef, pcellParams, generatorId, resolveWarning, pcellDiagnostics, footprintRefusal) =
                ResolveOnce(comp);

            if (resolvedCellRef is null)
            {
                noLayout.Add(comp);
                // R-fp3-5c: a pad-count refusal is a COMPLETE sentence naming both numbers, not a
                // fragment to wrap in the "(type): reason — skipped." shape the resolution failures
                // use. Both land in NoLayoutWarnings, which is reported unconditionally.
                if (footprintRefusal is not null) noLayoutWarnings.Add(footprintRefusal);
                else
                {
                    string label = ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount);
                    string reason = resolveWarning ?? "no layout view";
                    noLayoutWarnings.Add($"{comp.InstanceName} ({label}): {reason} — skipped.");
                }

                // R-fp3-4b: a footprint set back to None takes its artwork with it. Scoped to a
                // component that has NO cell reference of its own and resolves to nothing at all —
                // the only way such a component came to hold an instance is a footprint, so there is
                // nothing else this could be deleting. A kit part whose kit is not loaded, or a
                // CellRef that stopped resolving, keeps its artwork: those are transient conditions
                // and deleting board artwork over one would be the destructive reading.
                if (comp.Footprint is null && comp.CellRef is null
                    && existingBySchematicId.TryGetValue(schematicId, out var orphaned))
                {
                    deleteIndices.Add(orphaned.Index);
                    // The run removed it, not the user: its record goes too, or a footprint set back would read as a
                    // part "removed by hand" and never be put back.
                    if (target.GeneratedPlacements.ContainsKey(schematicId))
                        steps.Add(Step.Record(schematicId, null));
                    lines.Add(new ReportLine(schematicId,
                        $"{schematicId} — footprint set to None; its artwork was removed",
                        ReportSeverity.Warning));
                }
                continue;
            }

            // brief-L5-followups-2.md §2.2: a PCell generator's own diagnostics (e.g. R-klp-10's
            // curvature warning) had nowhere to surface before this fix — report them here, alongside
            // this instance's own add/update line, rather than dropping them silently.
            if (pcellDiagnostics is { Count: > 0 })
                foreach (var d in pcellDiagnostics)
                    lines.Add(new ReportLine(schematicId, $"{schematicId} — {d}",
                        LandPatternLayers.IsInformational(d) ? ReportSeverity.Info : ReportSeverity.Warning));

            bool hasExisting = existingBySchematicId.TryGetValue(schematicId, out var existing);

            // A component renamed in the schematic: its placement is still linked under the OLD name.
            // Adopting it is what keeps a rename from leaving the old part behind and adding a copy.
            string? oldName = null;
            if (!hasExisting && renamedFrom.TryGetValue(schematicId, out oldName))
            {
                existing = existingBySchematicId[oldName];
                hasExisting = true;
                seenSchematicIds.Add(oldName);
                ledger.RenameSchematicSnapshot(oldName, schematicId);
            }

            // brief-sdl-4 §2: a part the last run placed and the user has since deleted is not put back; its joints
            // count as open (D6). Restore Removed Parts asks for it back.
            if (!hasExisting && !restoreRemoved && EditState.WasRemoved(target, schematicId))
            {
                lines.Add(new ReportLine(schematicId,
                    $"{schematicId} — removed from the layout by hand, so not put back (Layout ▸ Restore Removed Parts " +
                    "puts it back)", ReportSeverity.Info) { Ids = [schematicId] });
                if (target.GeneratedPlacements[schematicId] is { Removed: false } record)
                    steps.Add(Step.Record(schematicId, record with { Removed = true }));
                continue;
            }

            if (!hasExisting)
            {
                // Placed at the origin and moved once the whole set is known — the pitch is a function
                // of what is being placed, and that is not known until the last one is resolved. The
                // plan holds this instance by reference and nothing has applied it yet, so moving it now
                // is moving it before it exists. See PlaceNewInstances.
                var inst = new LayoutInstance { CellRef = resolvedCellRef, X = 0, Y = 0, Mag = 1.0, SchematicId = schematicId };
                // Placed facing the way the symbol faces, and linked, so a rotation made on either side
                // from here on is carried across by the next run.
                // With the orientations unlinked (Settings, designer report round 15) it is placed as the cell is
                // drawn, and the link records that facing so turning the setting back on carries nothing.
                VisualOrientation FromSymbol() => SchematicLayoutOrientation.FromSchematic((int)comp.Rotation, comp.MirrorX)
                    .Compose(PinAlignment(comp, schematicDir, resolvedCellRef, targetLayoutBaseDir, technology));
                var facing = linkOrientation ? FromSymbol() : new VisualOrientation(false, 0);
                if (connectParts) schematicFacing[inst] = linkOrientation ? facing : FromSymbol();
                inst.MirrorX = facing.Mirror;
                inst.RotationDegrees = facing.Deg;
                inst.OrientationLink = SchematicLayoutOrientation.Link((int)comp.Rotation, comp.MirrorX, facing);
                newInstances.Add((slot, inst));
                steps.Add(Step.Add(inst));
                added++;
                lines.Add(new ReportLine(schematicId, $"{schematicId} — added", ReportSeverity.Info));

                if (generatorId is not null && pcellParams is not null)
                    ledger.SetSchematicSnapshot(schematicId, new Dictionary<string, PCellValue>(pcellParams));

                continue;
            }

            var before = existing.Instance;
            bool cellRefChanged = !string.Equals(before.CellRef, resolvedCellRef, StringComparison.OrdinalIgnoreCase);

            // brief-sdl-4 §2 (replacing R-L5-9/10/11's overwrite): each parameter classified against the baseline the
            // last run wrote — the schematic's change applied, the layout's own edit KEPT (EditState). Re-snap is the one
            // case that takes the schematic's value over a layout edit, because that is what it was asked to do.
            if (generatorId is not null && pcellParams is not null)
            {
                var snapshot = ledger.SchematicSnapshot(schematicId);
                var currentLayoutParams =
                    CellLayoutResolver.Resolve(before, targetLayoutBaseDir, technology) is
                        { State: CellLayoutState.Resolved, View.PCellOrigin: { } curOrigin }
                        ? curOrigin.Parameters
                        : null;
                bool resnapThis = resnap.Contains(schematicId);
                var classified = EditState.Classify(pcellParams, currentLayoutParams, snapshot);

                bool reportedThisInstance = false;
                foreach (var p in classified)
                {
                    if (p.State == EditState.ParamState.Same) continue;
                    var param = comp.Parameters.FirstOrDefault(q => q.Name == p.Name);
                    string? unit = param?.Unit;
                    // A length written as an expression of unit-carrying variables (L = L90-w50/2) has no unit of its own;
                    // its value is metres, said in the layout's display unit rather than as a bare SI number.
                    if (string.IsNullOrEmpty(unit) && param?.Dimension == UnitDimension.Length)
                        unit = LayoutUnits.Suffix(target.DisplayUnit);
                    string unitSuffix = string.IsNullOrEmpty(unit) ? "" : $" {unit}";
                    string V(PCellValue v) => FormatParamValue(unit, v) + unitSuffix;
                    reportedThisInstance = true;
                    if (!resnapThis && p.State is EditState.ParamState.Layout or EditState.ParamState.Conflict)
                        layoutKept.Add((schematicId, p.Name));
                    if (resnapThis || p.State == EditState.ParamState.Schematic)
                        lines.Add(new ReportLine(schematicId,
                            $"{schematicId} — {p.Name} changed from {V(p.Layout)} to {V(p.Schematic)} " +
                            (resnapThis && p.State != EditState.ParamState.Schematic ? "(Re-snap)" : "(from schematic)"),
                            ReportSeverity.Info) { Ids = [schematicId] });
                    else if (p.State == EditState.ParamState.Layout)
                        lines.Add(new ReportLine(schematicId,
                            $"{schematicId} — {p.Name} is {V(p.Layout)} in the layout; the schematic says {V(p.Schematic)}. " +
                            "The layout's value is kept", ReportSeverity.Info) { Ids = [schematicId] });
                    else
                    {
                        lines.Add(new ReportLine(schematicId,
                            $"{schematicId} — {p.Name} was changed in both: the layout's {V(p.Layout)} is kept; the schematic " +
                            $"changed it from {V(p.Baseline!.Value)} to {V(p.Schematic)}", ReportSeverity.Warning) { Ids = [schematicId] });
                        // Not counted as overwritten: the layout's value is the one kept (S7 D-conflict).
                    }
                }

                if (!resnapThis && EditState.Merge(pcellParams, classified) is { } merged)
                {
                    // Every difference the layout's own: its cell already IS the merged set, and stays.
                    bool layoutOnly = currentLayoutParams is not null
                                      && merged.All(kv => currentLayoutParams.TryGetValue(kv.Key, out var v) && SameParamValue(v, kv.Value));
                    string? keptRef = layoutOnly ? before.CellRef
                        : RegeneratePCell(comp, generatorId, merged, workspaceRootDir, targetLayoutBaseDir, ledger, technology, techIdentity);
                    if (keptRef is not null)
                    {
                        resolvedCellRef = keptRef;
                        cellRefChanged = !string.Equals(before.CellRef, resolvedCellRef, StringComparison.OrdinalIgnoreCase);
                    }
                }

                ledger.SetSchematicSnapshot(schematicId, new Dictionary<string, PCellValue>(pcellParams));
                UpdateExisting(reportedThisInstance);
                continue;
            }

            // Non-PCell (hierarchical cell-ref, or an unresolved reference that happens to still match) —
            // plain CellRef tracking, no parameter concept to overwrite.
            UpdateExisting(reportedThisInstance: false);

            // The one tail both branches share: a new cell reference, a rotation carried from the
            // schematic, or both, as ONE replacement — so the orientation link rides inside the undoable
            // command whenever there is one, and an Undo takes the baseline back with the rotation.
            void UpdateExisting(bool reportedThisInstance)
            {
                var rot = CarryRotation(comp, before, schematicId,
                    () => PinAlignment(comp, schematicDir, before.CellRef, targetLayoutBaseDir, technology), linkOrientation);
                if (rot.Unlinked) unlinkedDiffering++;
                if (cellRefChanged)
                    rot = Realign(rot, comp, before, schematicId, resolvedCellRef, schematicDir, targetLayoutBaseDir, technology);
                // brief-sdl-4: a locked part never moves, and a turn about its origin moves its pins. The link keeps its
                // old baseline, so the turn is carried once the part is unlocked.
                if (before.Locked && rot.Changes)
                {
                    // Run-level, so it is said even when nothing else changed (a run that changes nothing says nothing).
                    lines.Add(new ReportLine("",
                        $"{schematicId} — locked, so the schematic's turn was not carried (unlock it and run again to turn it)",
                        ReportSeverity.Info) { Ids = [schematicId] });
                    rot = new RotationCarry(false, SchematicLayoutOrientation.FromLayout(before),
                                            before.OrientationLink ?? rot.Link, null, false, rot.Unlinked);
                }

                // A part being re-snapped is replaced whatever else changed: the chain step writes its pose (R-sdl4-2).
                if (!cellRefChanged && !rot.Changes && oldName is null && !resnap.Contains(schematicId))
                {
                    // Nothing to replace, but the baseline may be new (first sync of this pair) or have
                    // advanced (both sides were already turned alike). Bookkeeping, like the snapshots.
                    if (!Equals(before.OrientationLink, rot.Link))
                    {
                        ledger.SetOrientationLink(existing.Index, rot.Link);
                        linksRecorded = true;
                    }
                    unchanged++;
                    return;
                }

                var after = LayoutGeometry.Clone(before);
                after.CellRef = resolvedCellRef;
                after.SchematicId = schematicId;
                after.OrientationLink = rot.Link;
                if (rot.Changes) TurnInPlace(before, after, rot.Target, targetLayoutBaseDir);
                replacedAt[existing.Index] = after;
                steps.Add(Step.Replace(existing.Index, before, after, renamedFrom: oldName));
                updated++;

                if (oldName is not null)
                {
                    lines.Add(new ReportLine(schematicId, $"{oldName} — renamed to {schematicId} (from schematic)",
                        ReportSeverity.Info));
                    reportedThisInstance = true;
                }

                if (rot.Line is { } rotLine)
                {
                    lines.Add(new ReportLine(schematicId, rotLine,
                        rot.Overwrote ? ReportSeverity.Warning : ReportSeverity.Info));
                    if (rot.Overwrote) overwritten++;
                }
                else if (!reportedThisInstance)
                    lines.Add(new ReportLine(schematicId, $"{schematicId} — updated", ReportSeverity.Info));
            }
        }

        lines.AddRange(ledger.Lines);   // brief-sdl-10 D3
        if (unlinkedDiffering > 0)
            lines.Add(new ReportLine("", UnlinkedRotationNote(unlinkedDiffering), ReportSeverity.Info));
        if (fromArtwork.Count > 0)
            lines.Add(new ReportLine("", FromArtworkNote(fromArtwork.Count), ReportSeverity.Info));

        // R-L5-4: report, never auto-delete, an instance whose schematic component is gone.
        int removed = 0;
        foreach (var (sid, _) in existingBySchematicId)
        {
            if (seenSchematicIds.Contains(sid)) continue;
            removed++;
            lines.Add(new ReportLine(sid,
                $"{sid} — no longer in the schematic (left in place; remove it by hand if intended)",
                ReportSeverity.Warning));
        }

        // A record whose part is gone from the schematic AND the layout says nothing any more. Left, it would make the
        // next part given that name (NextAvailableName reuses the lowest free number) a part "removed by hand", never
        // placed. Its port label counts as carrying it, as an instance does.
        var schematicNames = new HashSet<string>(model.Components.Select(c => c.InstanceName), StringComparer.Ordinal);
        foreach (string sid in target.GeneratedPlacements.Keys.Order(StringComparer.Ordinal).ToList())
            if (!schematicNames.Contains(sid) && !renamedFrom.ContainsValue(sid)
                && !target.Instances.Any(i => i.SchematicId == sid)
                && !target.Shapes.Any(sh => sh is LabelShape { SchematicId: { } l } && l == sid))
                steps.Add(Step.Record(sid, null));

        // AFTER every add and replacement, deliberately. Every add APPENDS and every replacement edits in
        // place, so the indices captured above are still correct at this point; a delete applied earlier
        // would shift every later index by one and the wrong instance would be replaced. Deletion removes
        // in descending order and restores at the original indices on undo, so the whole re-run stays one
        // undoable action (R-L5-12).
        if (deleteIndices.Count > 0)
            steps.Add(Step.Delete(deleteIndices));

        // brief-sdl-4 §1: the layout as left, plus the schematic's change — every part joined beyond a part whose pins
        // moved is carried with it, so the copper stays joined. Before the chain step, so parts added or re-snapped this
        // run join their neighbours where they now are. Off with Connect parts, which leaves the plan the pre-series one.
        var opened = new HashSet<string>(StringComparer.Ordinal);
        if (connectParts)
        {
            var propagation = PropagateChanges(target, physical, extraction, model, existingBySchematicId, replacedAt,
                                               deleteIndices, resnap, schematicDir, targetLayoutBaseDir, technology);
            foreach (var (index, moved) in propagation.Instances)
            {
                if (replacedAt.TryGetValue(index, out var after))
                {
                    (after.X, after.Y, after.RotationDegrees, after.OrientationLink) = (moved.X, moved.Y, moved.RotationDegrees, moved.OrientationLink);
                    continue;
                }
                // A part the run did not otherwise change: a baseline recorded for it this run rides on the replacement.
                var before = target.Instances[index];
                if (ledger.OrientationLinkUpdates.LastOrDefault(e => e.Key == index) is { Value: { } link })
                    moved.OrientationLink = link with
                    {
                        LayoutDeg = LayoutAngle.Normalize(link.LayoutDeg + SchematicLayoutOrientation.FromLayout(moved).Deg
                                                          - SchematicLayoutOrientation.FromLayout(before).Deg),
                    };
                replacedAt[index] = moved;
                steps.Add(Step.Replace(index, before, moved, renamedFrom: null));
                updated++;
            }
            foreach (var (index, moved) in propagation.Shapes)
                steps.Add(Step.ReplaceShape(index, target.Shapes[index], moved));
            lines.AddRange(propagation.Lines);
            opened.UnionWith(propagation.Opened);
        }

        // A VIAGND added this run is placed beside the pad it grounds, tied to it, rather than in the row — it is the
        // via that pad's ground runs through, and a via a row-width away from its part grounds nothing.
        var beside = ViaGndPartners(newInstances, physical, extraction, target, existingBySchematicId, replacedAt,
                                    schematicDir, targetLayoutBaseDir, technology);
        var chained = connectParts
            ? ConnectChains(extraction, model, physical, newInstances, beside, schematicFacing, target, existingBySchematicId,
                            replacedAt, new HashSet<int>(deleteIndices), schematicDir, targetLayoutBaseDir, technology, lines,
                            noLayout, resnap, opened)
            : ChainOutcome.None;
        var unplaced = newInstances.Where(n => !beside.ContainsKey(n.Instance) && !chained.Placed.Contains(n.Instance)).ToList();
        var atArtwork = overArtwork ? Bbox.Empty
            : PlaceAtArtwork(unplaced, physical, model, schematicDir, target, targetLayoutBaseDir, technology);
        if (!overArtwork && chained.Placed.Count > 0)
            ReportChainedOverArtwork(newInstances, physical, chained, model, lines);
        // A chain joined to nothing already placed is one rigid group, packed into the rows by its bounding box at
        // the place its first part would have taken (R-sdl1-3); parts in no chain are packed exactly as before.
        var rowItems = unplaced.Select(n => (n.Slot, Members: (IReadOnlyList<LayoutInstance>)[n.Instance]))
                               .Concat(chained.Groups).OrderBy(g => g.Slot).ToList();
        // R-dfr34-5-3: a layout already holding artwork — a cell cropped from a board keeps the board's coordinates,
        // 160 mm from the origin — takes its new parts in rows under that artwork, not at the origin. An empty layout,
        // or one holding only what earlier runs placed, keeps the slot rows.
        var addedRegion = PlaceNewInstances(rowItems, targetLayoutBaseDir,
                                            below: atArtwork.Union(DrawnArtworkExtent(target, targetLayoutBaseDir)),
                                            out var belowShift)
                              .Union(atArtwork)
                              .Union(chained.FixedRegion);
        foreach (var (via, partner) in beside)
        {
            var origin = CellHierarchy.InstanceBbox(via, targetLayoutBaseDir);
            long pad = Math.Max(origin.MaxX - origin.MinX, origin.MaxY - origin.MinY);
            var (vx, vy, tie) = GroundArtwork.Beside(partner.Instance, partner.Cell, partner.Pin, pad, target.DbuPerMicron);
            via.X = vx - (origin.MinX + origin.MaxX) / 2;
            via.Y = vy - (origin.MinY + origin.MaxY) / 2;
            steps.Add(Step.Shape(tie));
            addedRegion = addedRegion.Union(CellHierarchy.InstanceBbox(via, targetLayoutBaseDir))
                                     .Union(CellHierarchy.InstanceBbox(partner.Instance, targetLayoutBaseDir));
            lines.Add(new ReportLine(via.SchematicId ?? "",
                $"{via.SchematicId} — placed beside {partner.Instance.SchematicId} pin {partner.Pin.Name} and tied to its pad",
                ReportSeverity.Info));
        }
        ReportPlacementOntoDrawnArtwork(target, newInstances, targetLayoutBaseDir, lines,
                                        new HashSet<LayoutInstance>(rowItems.SelectMany(g => g.Members), ReferenceEqualityComparer.Instance),
                                        belowShift);

        // brief-sdl-5 R-sdl5-1: the schematic's Ports become EM port labels at the pins they connect to — after every
        // part is where the run leaves it, so a port lands on its pin's final place and follows a pin that moved.
        if (connectParts)
            PlacePorts(model, extraction, target, physical, existingBySchematicId, replacedAt, deleteIndices, newInstances,
                       schematicDir, targetLayoutBaseDir, targetLayoutPath, technology, resnap, restoreRemoved, steps, lines);

        // Designer feedback round 11: the ground the schematic states, drawn — a via at every ground-symbol pin and
        // the pour under the lines and those vias — planned against the instances as they WILL be once the plan is
        // applied, and applied as part of it, so the whole update stays one undoable action (R-L5-12). Read against
        // the regeneration records as they will be too: the cells this run generated are not in the view's table yet.
        var deleted = new HashSet<int>(deleteIndices);
        var future = target.Instances.Select((inst, i) => replacedAt.GetValueOrDefault(i, inst))
                                     .Where((_, i) => !deleted.Contains(i))
                                     .Concat(newInstances.Select(n => n.Instance)).ToList();
        var ground = GroundArtwork.Plan(target, technology, targetLayoutBaseDir, future,
                                        GroundedPins(physical, extraction, schematicDir),
                                        new HashSet<string>(physical.Select(c => c.InstanceName), StringComparer.Ordinal),
                                        pourMarginHeights, ledger.PCellSnapshotsAfter);

        // R-sdl0-2c: the placement record, for every part this run adds and every part whose transform it changes
        // (or whose name it carries over a rename). Taken last, from where each part finally is.
        var edits = steps.Select(s => s.ToEdit(chained.Anchors, target.GeneratedPlacements)).ToList();

        var plan = new SchematicLayoutPlan
        {
            Edits = edits,
            Ground = ground,
            GroundShapeRemovals = GroundRemovalIndices(target, ground),
            SchematicSnapshotChanges = ledger.SchematicSnapshotChanges,
            PCellSnapshotWrites = ledger.PCellSnapshotWrites,
            OrientationLinkUpdates = ledger.OrientationLinkUpdates,
            Lines = lines,
            NoLayoutWarnings = noLayoutWarnings,
            AddedCount = added,
            UpdatedCount = updated,
            UnchangedCount = unchanged,
            RemovedCount = removed,
            OverwrittenParameterCount = overwritten,
            AddedRegion = addedRegion,
            DeletedCount = deleteIndices.Count,
            OrientationLinksRecorded = linksRecorded,
            LayoutKept = layoutKept,
        };

        // brief-sdl-3 R-sdl3-1: copper of different nets that overlaps or touches once the plan is applied — read off
        // the finished plan, because it is the whole layout as the run leaves it that can cross itself. Appended to the
        // plan's own line list (Lines IS `lines`). Off with Connect parts, which leaves the plan the pre-series one.
        if (connectParts)
            lines.AddRange(PlacementOverlaps.Find(plan, target, targetLayoutBaseDir, technology, extraction)
                                            .Select(o => o.ToReportLine()));
        return plan;
    }

    /// <summary>brief-sdl-5: <see cref="PortPlacement"/> over every placed part as the run leaves it, its labels and
    /// records added to the run's steps, and — D4 — the numbering check the EM run makes, quoted when it refuses.</summary>
    private static void PlacePorts(
        SchematicEditModel model, NetExtractor.ExtractionResult extraction, LayoutView target, List<EditableComponent> physical,
        Dictionary<string, (int Index, LayoutInstance Instance)> existingBySchematicId, Dictionary<int, LayoutInstance> replacedAt,
        List<int> deleteIndices, List<(int Slot, LayoutInstance Instance)> newInstances, string schematicDir,
        string targetLayoutBaseDir, string? targetLayoutPath, Technology? technology, IReadOnlySet<string> resnap,
        bool restoreRemoved, List<Step> steps, List<ReportLine> lines)
    {
        if (!model.Components.Any(PortPlacement.IsPort)
            && !target.Shapes.Any(s => s is LabelShape { IsPort: true, SchematicId: { Length: > 0 } })) return;

        var compById = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in physical) if (c.InstanceName is { Length: > 0 } n) compById.TryAdd(n, c);
        var deleted = new HashSet<int>(deleteIndices);
        var parts = new List<PortPlacement.PlacedPart>();
        for (int i = 0; i < target.Instances.Count; i++)
        {
            if (deleted.Contains(i)) continue;
            var after = replacedAt.GetValueOrDefault(i, target.Instances[i]);
            if (after.SchematicId is { Length: > 0 } sid && compById.TryGetValue(sid, out var comp))
                parts.Add(new PortPlacement.PlacedPart(comp, after, target.Instances[i]));
        }
        foreach (var (_, inst) in newInstances)
            if (inst.SchematicId is { Length: > 0 } sid && compById.TryGetValue(sid, out var comp))
                parts.Add(new PortPlacement.PlacedPart(comp, inst, null));

        var outcome = PortPlacement.Plan(new PortPlacement.Input(model, extraction, target, parts, schematicDir,
                                                                 targetLayoutBaseDir, technology, resnap, restoreRemoved));
        foreach (var (index, before, after) in outcome.Replaced) steps.Add(Step.ReplaceShape(index, before, after));
        foreach (var label in outcome.Added) steps.Add(Step.Shape(label));
        foreach (var (id, record) in outcome.Records) steps.Add(Step.Record(id, record));
        lines.AddRange(outcome.Lines);

        // D4: the layout as the run leaves it, numbered as an EM run numbers it.
        var future = new LayoutView { DbuPerMicron = target.DbuPerMicron, DisplayUnit = target.DisplayUnit, TechRef = target.TechRef };
        future.Instances.AddRange(parts.Select(p => p.After));
        var replacedShapes = outcome.Replaced.ToDictionary(r => r.Index, r => (LayoutShape)r.After);
        future.Shapes.AddRange(target.Shapes.Select((s, i) => replacedShapes.GetValueOrDefault(i, s)));
        future.Shapes.AddRange(outcome.Added);
        string clayPath = targetLayoutPath ?? Path.Combine(targetLayoutBaseDir, "layout.clay");
        if (PortPlacement.DuplicateRefusal(future, clayPath, technology) is { } refusal)
            lines.Add(new ReportLine("", $"Port numbers: {refusal}", ReportSeverity.Warning) { Kind = ReportKind.PortByHand });
    }

    /// <summary>brief-sdl-4: the run's existing linked parts — as it replaces them so far — handed to
    /// <see cref="DeltaPropagation"/>. A part being re-snapped, or deleted this run, takes no part.</summary>
    private static DeltaPropagation.Outcome PropagateChanges(
        LayoutView target, List<EditableComponent> physical, NetExtractor.ExtractionResult extraction, SchematicEditModel model,
        Dictionary<string, (int Index, LayoutInstance Instance)> existingBySchematicId, Dictionary<int, LayoutInstance> replacedAt,
        List<int> deleteIndices, IReadOnlySet<string> resnap, string schematicDir, string targetLayoutBaseDir, Technology? technology)
    {
        var compById = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in physical) if (c.InstanceName is { Length: > 0 } n) compById.TryAdd(n, c);
        var skip = new HashSet<int>(deleteIndices);
        var parts = new List<DeltaPropagation.LinkedPart>();
        foreach (var (sid, e) in existingBySchematicId)
        {
            if (skip.Contains(e.Index)) continue;
            var after = replacedAt.GetValueOrDefault(e.Index, e.Instance);
            string id = after.SchematicId is { Length: > 0 } now ? now : sid;
            if (!compById.TryGetValue(id, out var comp)) continue;   // an orphan: copper with no schematic link
            if (resnap.Contains(id)) { skip.Add(e.Index); continue; }
            parts.Add(new DeltaPropagation.LinkedPart(e.Index, id, comp, e.Instance, after));
        }
        if (parts.Count == 0) return DeltaPropagation.Outcome.None;

        var ports = PortNumbersByNet(model, extraction);
        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, TerminalNets.Of(inst));
        int? PortOf(string id) => bindings.TryGetValue(id, out var nets)
            ? nets.Select(n => ports.TryGetValue(n, out int k) ? k : (int?)null).Where(k => k is not null).Min()
            : null;

        return DeltaPropagation.Run(new DeltaPropagation.Input(target, parts, skip, schematicDir, targetLayoutBaseDir,
                                                               technology, extraction, PortOf));
    }

    /// <summary>The indices in <paramref name="view"/>'s shapes of what <paramref name="plan"/> takes out — the
    /// one reading of <see cref="GroundArtworkPlan.Remove"/> (it holds the shapes themselves) every applier
    /// shares.</summary>
    public static IReadOnlyList<int> GroundRemovalIndices(LayoutView view, GroundArtworkPlan plan)
    {
        if (plan.Remove.Count == 0) return [];
        var removing = new HashSet<LayoutShape>(plan.Remove, ReferenceEqualityComparer.Instance);
        return Enumerable.Range(0, view.Shapes.Count).Where(i => removing.Contains(view.Shapes[i])).ToList();
    }

    /// <summary>One planned change, in application order, before its placement record is known.</summary>
    private sealed class Step
    {
        private int _kind;   // 0 add, 1 replace, 2 delete, 3 shape, 4 shape replaced, 5 placement record
        private LayoutInstance? _instance, _before;
        private int _index;
        private string? _renamedFrom;
        private IReadOnlyList<int>? _indices;
        private LayoutShape? _shape, _shapeBefore;
        private GeneratedPlacement? _record;

        public static Step Add(LayoutInstance inst) => new() { _kind = 0, _instance = inst };
        public static Step Replace(int index, LayoutInstance before, LayoutInstance after, string? renamedFrom) =>
            new() { _kind = 1, _index = index, _before = before, _instance = after, _renamedFrom = renamedFrom };
        public static Step Delete(IReadOnlyList<int> indices) => new() { _kind = 2, _indices = [.. indices] };
        public static Step Shape(LayoutShape shape) => new() { _kind = 3, _shape = shape };
        public static Step ReplaceShape(int index, LayoutShape before, LayoutShape after) =>
            new() { _kind = 4, _index = index, _shapeBefore = before, _shape = after };
        public static Step Record(string schematicId, GeneratedPlacement? record) =>
            new() { _kind = 5, _renamedFrom = schematicId, _record = record };

        /// <summary>The edit, with its placement record. A replaced part keeps the anchor mark its record had (brief-sdl-4:
        /// a part moved to keep the copper joined still anchors its chain).</summary>
        public SchematicLayoutEdit ToEdit(IReadOnlySet<LayoutInstance> chainAnchors,
                                          IReadOnlyDictionary<string, GeneratedPlacement> records) => _kind switch
        {
            0 => new SchematicLayoutEdit.AddInstance(_instance!, GeneratedPlacement.Of(_instance!, chainAnchors.Contains(_instance!))),
            1 => new SchematicLayoutEdit.ReplaceInstance(_index, _before!, _instance!,
                     TransformChanged(_before!, _instance!) || _renamedFrom is not null
                         ? GeneratedPlacement.Of(_instance!, records.GetValueOrDefault(_renamedFrom ?? _before!.SchematicId ?? "")?.ChainAnchor == true)
                         : null,
                     _renamedFrom),
            2 => new SchematicLayoutEdit.DeleteInstances(_indices!),
            4 => new SchematicLayoutEdit.ReplaceShape(_index, _shapeBefore!, _shape!),
            5 => new SchematicLayoutEdit.PlacementRecord(_renamedFrom!, _record),
            _ => new SchematicLayoutEdit.AddShape(_shape!),
        };

        private static bool TransformChanged(LayoutInstance a, LayoutInstance b) =>
            a.X != b.X || a.Y != b.Y || a.RotationDegrees != b.RotationDegrees || a.MirrorX != b.MirrorX || a.Side != b.Side;
    }

    /// <summary>
    /// The run's bookkeeping, kept here rather than written into the target: the side tables as this run reads
    /// them (its own writes over the target's), and the writes themselves in the order they were made, so an
    /// applier replays them into the same dictionary order the old in-place writes produced — a <c>.clay</c>
    /// writes those tables in enumeration order.
    /// </summary>
    private sealed class Ledger(LayoutView target, KitLayoutGeneratorLookup? kitLayoutGenerator)
    {
        public KitLayoutGeneratorLookup? KitLayoutGenerator { get; } = kitLayoutGenerator;

        /// <summary>brief-sdl-10 D3 — what resolving a part had to say (a variant placed, or why not): reported with the run.</summary>
        public List<ReportLine> Lines { get; } = [];

        private readonly Dictionary<string, Dictionary<string, PCellValue>> _schematic =
            new(target.SchematicPCellSnapshots, StringComparer.Ordinal);
        private readonly Dictionary<string, PCellSnapshot> _pcell = new(target.PCellSnapshots, StringComparer.Ordinal);

        public List<SchematicSnapshotChange> SchematicSnapshotChanges { get; } = [];
        public List<KeyValuePair<string, PCellSnapshot>> PCellSnapshotWrites { get; } = [];
        public List<KeyValuePair<int, OrientationLink>> OrientationLinkUpdates { get; } = [];

        public IReadOnlyDictionary<string, PCellSnapshot> PCellSnapshotsAfter => _pcell;

        public Dictionary<string, PCellValue>? SchematicSnapshot(string schematicId) =>
            _schematic.GetValueOrDefault(schematicId);

        public void SetSchematicSnapshot(string schematicId, Dictionary<string, PCellValue> parameters)
        {
            _schematic[schematicId] = parameters;
            SchematicSnapshotChanges.Add(new(schematicId, parameters));
        }

        public void RenameSchematicSnapshot(string oldName, string newName)
        {
            if (!_schematic.Remove(oldName, out var carried)) return;
            SchematicSnapshotChanges.Add(new(oldName, null));
            SetSchematicSnapshot(newName, carried);
        }

        public void RecordPCellSnapshot((string CellName, PCellSnapshot Snapshot) record)
        {
            _pcell[record.CellName] = record.Snapshot;
            PCellSnapshotWrites.Add(new(record.CellName, record.Snapshot));
        }

        public void SetOrientationLink(int index, OrientationLink link) => OrientationLinkUpdates.Add(new(index, link));
    }

    /// <summary>Every schematic terminal on the ground net ("0", which a ground symbol names), as the extraction
    /// bound it. Only a terminal the symbol DRAWS counts: a port block's hidden reference is also bound to "0" and
    /// has no pad.</summary>
    internal static List<GroundPin> GroundedPins(IEnumerable<EditableComponent> physical,
                                                 NetExtractor.ExtractionResult extraction, string? schematicDir)
    {
        var byName = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) byName.TryAdd(inst.InstanceName, inst);
        var pins = new List<GroundPin>();
        foreach (var comp in physical)
        {
            if (string.IsNullOrEmpty(comp.InstanceName) || !byName.TryGetValue(comp.InstanceName, out var inst)) continue;
            var symbol = comp.ExternalSymbolRef is { } symRef ? CellSymbolResolver.Resolve(symRef, schematicDir) : null;
            int drawn = comp.ToRenderComponent(null, symbol).Ports.Count;
            var nets = TerminalNets.Of(inst);   // an SnP's reference terminal is drawn last, and may be grounded
            for (int k = 0; k < Math.Min(drawn, nets.Count); k++)
                if (nets[k] == "0") pins.Add(new GroundPin(comp.InstanceName, k + 1));
        }
        return pins;
    }

    /// <summary>
    /// What a ground plan did, as the Messages say it — one line per kind of change, and a warning for each via the
    /// designer took over or a layer left alone. Shared by Update Layout and Draw Ground Pour.
    /// </summary>
    internal static List<ReportLine> GroundArtworkReport(GroundArtworkPlan plan, LayoutView view, LayoutUnit unit)
    {
        var lines = new List<ReportLine>();
        void Info(string t) => lines.Add(new ReportLine("", t, ReportSeverity.Info));
        void Warn(string t) => lines.Add(new ReportLine("", t, ReportSeverity.Warning));

        if (plan.ViasPlaced > 0)
            Info($"Placed {plan.ViasPlaced} ground via(s), one beside every pad whose schematic pin is on a ground symbol, " +
                 "tied to its pad and drilled to the ground plane.");
        if (plan.ViasFollowed > 0)
            Info($"Moved {plan.ViasFollowed} ground via(s) to follow their parts.");
        if (plan.ViasRemoved > 0)
            Info($"Removed {plan.ViasRemoved} ground via(s) whose pin is no longer on ground in the schematic.");
        if (plan.ViasLeftMoved > 0)
            Info($"{plan.ViasLeftMoved} ground via(s) you moved were left where you put them; they no longer follow their parts.");
        if (plan.ViasLeftDeleted > 0)
            Info($"{plan.ViasLeftDeleted} ground via(s) you deleted were not put back.");

        string u = LayoutUnits.Suffix(unit);
        foreach (var p in plan.Pours)
        {
            string extent = p.BoundedByOutline
                ? "filling the board outline"
                : $"reaching {LayoutUnits.Format(p.MarginDbu, unit, view.DbuPerMicron)} {u} " +
                  $"({p.MarginHeights:0.##} substrate heights) beyond what it covers";
            string what = (p.Lines, p.GroundVias) switch
            {
                (> 0, > 0) => $"under {p.Lines} microstrip line(s) and {p.GroundVias} ground via(s)",
                (> 0, _)   => $"under {p.Lines} microstrip line(s)",
                _          => $"under {p.GroundVias} ground via(s)",
            };
            string verb = p.Replaces is { Count: > 0 } ? "Redrew" : "Drew";
            Info($"{verb} the ground pour on '{p.Ground.Name}' {what}, {extent}" +
                 (p.ViaClearances > 0 ? $", with a clearance round each of the {p.ViaClearances} via(s) passing through it" : "") +
                 ". Update Layout and Design ▸ Draw Ground Pour redraw it as the parts move; edit it and it is yours." +
                 (p.ViasJoined > 0 ? $" {p.ViasJoined} via(s) you drew end on '{p.Ground.Name}' and are joined to it — " +
                                     "a signal via among them needs a clearance cut by hand." : ""));
        }
        foreach (var layer in plan.PoursLeftAlone)
            Info($"'{layer.Name}' already carries copper you drew, so no pour was drawn over it.");
        foreach (string note in plan.Notes) Warn(note);
        return lines;
    }

    private readonly record struct Resolution(
        string? CellRef, IReadOnlyDictionary<string, PCellValue>? PCellParams, string? GeneratorId,
        string? Warning, IReadOnlyList<string>? Diagnostics, string? FootprintRefusal);

    /// <summary>
    /// Which schematic components are placements renamed since the last run, as new name → old name.
    ///
    /// <para>The link between a placement and its component is the instance name (<see
    /// cref="LayoutInstance.SchematicId"/>), and nothing in a <c>.csch</c> survives a rename to say
    /// otherwise. So a rename looked like a deletion plus a new part: the old placement was left
    /// behind as "no longer in the schematic" and an identical one was added beside it — one part in
    /// the schematic, two on the board.</para>
    ///
    /// <para>A rename is recognised as a placement whose name has gone and a component with no
    /// placement that resolve to the <b>same cell</b> — and only when that cell has exactly one of
    /// each. Two renamed 0402s share one land-pattern cell, and pairing them in either order could
    /// swap which placement carries which designator; that case keeps the old answer (added, and the
    /// orphan reported) rather than guess.</para>
    /// </summary>
    private static Dictionary<string, string> PairRenames(
        List<EditableComponent> physical,
        Dictionary<string, (int Index, LayoutInstance Instance)> existingBySchematicId,
        Func<EditableComponent, Resolution> resolve)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(physical.Select(c => c.InstanceName), StringComparer.Ordinal);
        var orphans = existingBySchematicId.Where(e => !names.Contains(e.Key)).ToList();
        if (orphans.Count == 0) return pairs;

        var newcomers = physical
            .Where(c => c.InstanceName is { Length: > 0 } n && !existingBySchematicId.ContainsKey(n))
            .Select(c => (Name: c.InstanceName, resolve(c).CellRef))
            .Where(c => c.CellRef is not null)
            .ToList();

        foreach (var group in newcomers.GroupBy(c => c.CellRef!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1) continue;
            var sameCell = orphans.Where(o => string.Equals(o.Value.Instance.CellRef, group.Key,
                                                            StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameCell.Count == 1) pairs[group.Single().Name] = sameCell[0].Key;
        }
        return pairs;
    }

    // ── Rotation (Update Layout from Schematic carries a symbol's turn to its placement) ──────────

    internal readonly record struct RotationCarry(
        bool Changes, VisualOrientation Target, OrientationLink Link, string? Line, bool Overwrote, bool Unlinked);

    /// <summary>
    /// Whether the schematic side turned since the last sync, and if so where the placement goes.
    /// See <see cref="SchematicLayoutOrientation"/> for why a CHANGE is carried rather than an angle.
    ///
    /// <para>The rules mirror the parameter table (R-L5-11) with one deliberate difference: a
    /// placement turned only in the LAYOUT is left alone here rather than turned back. A parameter is
    /// one value the two views must agree on; a rotation is also the layout's own arrangement, and
    /// Update Layout is run for many reasons besides this one — reverting a routing decision every
    /// time someone pushes a width change would make the command unusable on a routed board. The
    /// layout's turn stays pending in the link, and Update Schematic from Layout carries it back.</para>
    ///
    /// <para>No link yet (a placement from before this existed): the pair is recorded and nothing
    /// turns, because nothing says which side's orientation is the intended one.</para>
    /// </summary>
    /// <param name="alignment">The pin alignment (<see cref="SchematicLayoutOrientation.PinAlignment"/>),
    /// asked for only when the pair has no link yet — it resolves the cell's pins.</param>
    internal static RotationCarry CarryRotation(EditableComponent comp, LayoutInstance inst, string schematicId,
                                                Func<VisualOrientation> alignment, bool linked = true)
    {
        int sDeg = (int)comp.Rotation;
        var s = SchematicLayoutOrientation.FromSchematic(sDeg, comp.MirrorX);
        var l = SchematicLayoutOrientation.FromLayout(inst);

        // Unlinked: nothing is carried, and the baseline moves to where both sides are now, so turning the link back on
        // later carries only what is turned after that.
        if (!linked) return new(false, l, SchematicLayoutOrientation.Link(sDeg, comp.MirrorX, l), null, false, false);

        if (inst.OrientationLink is not { } b)
            return new(false, l, SchematicLayoutOrientation.Link(sDeg, comp.MirrorX, l), null, false,
                       Unlinked: !s.Compose(alignment()).SameAs(l));

        var sb = SchematicLayoutOrientation.FromSchematic(b.SchematicDeg, b.SchematicMirror);
        var lb = new VisualOrientation(b.LayoutMirror, b.LayoutDeg);
        if (s.SameAs(sb)) return new(false, l, b, null, false, false);

        var target = SchematicLayoutOrientation.Carry(s, sb, lb);
        var link = SchematicLayoutOrientation.Link(sDeg, comp.MirrorX, target);
        if (target.SameAs(l)) return new(false, l, link, null, false, false);

        bool layoutMoved = !l.SameAs(lb);
        string line = $"{schematicId} — rotation changed from {SchematicLayoutOrientation.Describe(l)} to " +
                      $"{SchematicLayoutOrientation.Describe(target)}" +
                      (layoutMoved ? " (a layout rotation is being overwritten)" : " (from schematic)");
        return new(true, target, link, line, layoutMoved, false);
    }

    /// <summary>
    /// A regenerated cell whose pins are arranged differently from the one it replaces — a bend whose
    /// <c>Angle</c> changed sign turns the other way, so its alignment gains or loses a mirror — is
    /// turned by exactly that difference: <c>placement = symbol ∘ alignment</c>, so the new placement
    /// is <c>current ∘ old⁻¹ ∘ new</c>, which keeps any turn the layout made of its own. Nothing moves
    /// when either cell does not resolve, because an unresolved alignment is the identity and the
    /// difference against it would be a turn nobody asked for.
    /// </summary>
    private static RotationCarry Realign(RotationCarry rot, EditableComponent comp, LayoutInstance before,
                                         string schematicId, string newCellRef, string? schematicDir,
                                         string layoutBaseDir, Technology? technology)
    {
        if (CellLayoutResolver.Resolve(before, layoutBaseDir, technology) is not { State: CellLayoutState.Resolved, View: { } oldCell } ||
            CellLayoutResolver.Resolve(newCellRef, layoutBaseDir) is not { State: CellLayoutState.Resolved, View: { } newCell })
            return rot;
        var oldAlign = PinAlignment(comp, schematicDir, oldCell, technology);
        var newAlign = PinAlignment(comp, schematicDir, newCell, technology);
        if (oldAlign.SameAs(newAlign)) return rot;

        var from = rot.Changes ? rot.Target : SchematicLayoutOrientation.FromLayout(before);
        var target = from.Compose(oldAlign.Inverse()).Compose(newAlign);
        var link = SchematicLayoutOrientation.Link((int)comp.Rotation, comp.MirrorX, target);
        string line = $"{schematicId} — turned from {SchematicLayoutOrientation.Describe(from)} to " +
                      $"{SchematicLayoutOrientation.Describe(target)} so its pins still face the way the symbol's do";
        return new(true, target, link, rot.Line is { } carried ? carried + "; " + line : line, rot.Overwrote, rot.Unlinked);
    }

    /// <summary>
    /// <see cref="SchematicLayoutOrientation.PinAlignment"/> for this component and this cell — the
    /// symbol's ports as the schematic draws them, and the cell's pins as the layout draws them.
    /// </summary>
    internal static VisualOrientation PinAlignment(EditableComponent comp, string? schematicDir,
                                                   string cellRef, string layoutBaseDir, Technology? technology)
    {
        var cell = CellLayoutResolver.Resolve(cellRef, layoutBaseDir);
        return cell.State == CellLayoutState.Resolved
            ? PinAlignment(comp, schematicDir, cell.View!, technology)
            : new VisualOrientation(false, 0);
    }

    internal static VisualOrientation PinAlignment(EditableComponent comp, string? schematicDir,
                                                   LayoutView cell, Technology? technology)
    {
        var symbol = comp.ExternalSymbolRef is { } symRef ? CellSymbolResolver.Resolve(symRef, schematicDir) : null;
        var ports = comp.ToRenderComponent(null, symbol).Ports.Select(p => ((double)p.LocalX, (double)p.LocalY)).ToList();
        return SchematicLayoutOrientation.PinAlignment(ports, CellPins.Resolve(cell, technology));
    }

    /// <summary>The run-level note for placements with no link whose two orientations disagree —
    /// shared by both directions, which record the link the same way.</summary>
    internal static string UnlinkedRotationNote(int count) =>
        $"{count} component{(count == 1 ? "'s" : "s'")} schematic and layout rotations differ and had never " +
        "been synced, so neither was turned. They are linked now: a symbol turned in the schematic from " +
        "here on turns its placement on the next Update Layout from Schematic. A placement turned on the " +
        "board never turns its symbol — a board angle is not a drawing orientation.";

    /// <summary>
    /// Sets <paramref name="after"/>'s orientation and moves its origin so the part turns about the
    /// centre of its own artwork, which is what rotating one part means to whoever is looking at it.
    /// An instance's origin is wherever its cell's (0,0) happens to be, often a corner; turning about
    /// that would swing the part across its neighbours.
    /// </summary>
    private static void TurnInPlace(LayoutInstance before, LayoutInstance after, VisualOrientation target,
                                    string targetLayoutBaseDir)
    {
        var was = CellHierarchy.InstanceBbox(before, targetLayoutBaseDir);
        // The target is a facing on the part's own face (SchematicLayoutOrientation.FromLayout), so a
        // bottom part is turned and mirrored on the bottom and stays there.
        var placed = SchematicLayoutOrientation.ToLayout(target, after.Side);
        after.MirrorX = placed.Mirror;
        after.RotationDegrees = placed.Deg;
        var now = CellHierarchy.InstanceBbox(after, targetLayoutBaseDir);
        if (was.IsEmpty || now.IsEmpty) return;
        after.X += (was.MinX + was.MaxX) / 2 - (now.MinX + now.MaxX) / 2;
        after.Y += (was.MinY + was.MaxY) / 2 - (now.MinY + now.MaxY) / 2;
    }

    /// <summary>
    /// Says so when a newly placed instance lands on top of artwork that was already DRAWN in this
    /// layout.
    ///
    /// <para><b>Owner report, 2026-09-17: opening the Klopfenstein Taper example's schematic and
    /// running this command twice left the <c>.clay</c> with two tapers, one exactly over the
    /// other.</b> Both were correct and the command was working: this generator tracks the instances
    /// it PLACES, by <c>SchematicId</c>, and that example's layout is hand-drawn artwork — a polygon
    /// and two port labels, which is what an EM example has to ship so it runs from a clone with no
    /// generated cells on disk. Hand-drawn metal carries no <c>SchematicId</c> and never will, so
    /// there is no match to find and the component is placed as new. (The second run then matched
    /// its own instance and changed nothing, which is why the count stops at two.)</para>
    ///
    /// <para><b>Reported rather than prevented, and reported rather than tolerated.</b> Nothing here
    /// can tell drawn metal that IS this component from drawn metal that merely sits where it was
    /// put — refusing would block a legitimate gesture on a guess, and staying silent leaves a design
    /// with two copies of one part, which reads as one part at every zoom and simulates as neither.
    /// So the placement stands, the undo is one keystroke, and the user is told which instance it
    /// was.</para>
    ///
    /// <para><b>The test is MUTUAL coverage, not mere intersection</b> — at least half of the drawn
    /// shape inside the instance's footprint and at least half of the footprint inside the drawn
    /// shape. "Overlaps at all" would fire on every part placed over a ground pour or inside a board
    /// outline, which is ordinary board work and would make this line noise within a day. Two
    /// drawings of one component cover each other almost exactly.</para>
    ///
    /// <para><b>A row moved under the artwork is checked where its slot was too</b> (R-dfr34-5-3). Rows now go below a
    /// layout's drawn artwork, so a hand-drawn copy of a part sitting at the part's slot is no longer covered by it —
    /// but it is still the same part twice, so the slot is where that check looks.</para>
    /// </summary>
    private static void ReportPlacementOntoDrawnArtwork(
        LayoutView target, List<(int Slot, LayoutInstance Instance)> placed,
        string targetLayoutBaseDir, List<ReportLine> lines,
        IReadOnlySet<LayoutInstance>? inRows = null, (long Dx, long Dy) rowShift = default)
    {
        if (placed.Count == 0 || target.Shapes.Count == 0) return;

        foreach (var (_, inst) in placed)
        {
            var footprint = CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir);
            if (footprint.IsEmpty) continue;

            // Only the layers this cell actually draws on. A silkscreen outline over a copper part is
            // not the same part twice, and saying it is would be wrong rather than merely noisy.
            var layers = CellLayers(inst, targetLayoutBaseDir);

            int Overlapping(Bbox at)
            {
                int n = 0;
                foreach (var shape in target.Shapes)
                {
                    if (shape is LabelShape) continue;               // annotation, not metal
                    if (layers is not null && !layers.Contains(shape.Layer)) continue;
                    if (CoverEachOther(LayoutGeometry.BboxOf(shape), at)) n++;
                }
                return n;
            }

            const string tail = " This command tracks the instances it places, not artwork drawn by hand, so a " +
                                "component that was already drawn is now in the layout twice. Undo, or delete whichever " +
                                "copy you do not want.";
            int overlapping = Overlapping(footprint);
            if (overlapping > 0)
            {
                string what = overlapping == 1 ? "a shape" : $"{overlapping} shapes";
                lines.Add(new ReportLine(inst.SchematicId ?? "",
                    $"{inst.SchematicId} — placed on top of {what} already drawn in this layout." + tail,
                    ReportSeverity.Warning));
                continue;
            }
            if (rowShift == default || inRows is null || !inRows.Contains(inst)) continue;
            var slot = new Bbox(footprint.MinX - rowShift.Dx, footprint.MinY - rowShift.Dy,
                                footprint.MaxX - rowShift.Dx, footprint.MaxY - rowShift.Dy);
            overlapping = Overlapping(slot);
            if (overlapping == 0) continue;
            string covers = overlapping == 1 ? "a shape" : $"{overlapping} shapes";
            lines.Add(new ReportLine(inst.SchematicId ?? "",
                $"{inst.SchematicId} — {covers} already drawn in this layout cover the place this command gives it, " +
                "so it was placed below that artwork instead." + tail,
                ReportSeverity.Warning));
        }
    }

    /// <summary>The layer keys a placed cell's own artwork uses, or null when the cell does not
    /// resolve — in which case the caller compares footprints alone rather than dropping the check,
    /// since an unresolvable reference is already reported on its own account.</summary>
    private static HashSet<LayerKey>? CellLayers(LayoutInstance inst, string targetLayoutBaseDir)
    {
        if (inst.CellRef is not { Length: > 0 } cellRef) return null;
        if (CellLayoutResolver.Resolve(cellRef, targetLayoutBaseDir) is not
            { State: CellLayoutState.Resolved, View: { } view }) return null;

        var keys = new HashSet<LayerKey>();
        foreach (var s in view.Shapes)
            if (s is not LabelShape) keys.Add(s.Layer);
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>True when each box holds at least half of the other — "these two are drawings of the
    /// same thing", as opposed to "these two touch". See <see cref="ReportPlacementOntoDrawnArtwork"/>
    /// for why the weaker test is the wrong one.</summary>
    private static bool CoverEachOther(Bbox a, Bbox b)
    {
        if (a.IsEmpty || b.IsEmpty || !a.Intersects(b)) return false;

        double overlap = (double)(Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX))
                       * (Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        double areaA = (double)(a.MaxX - a.MinX) * (a.MaxY - a.MinY);
        double areaB = (double)(b.MaxX - b.MinX) * (b.MaxY - b.MinY);

        // A zero-area box (a horizontal line, a point) is degenerate for a coverage fraction: it is
        // fully covered whenever it intersects at all, which is the right answer for it.
        return (areaA <= 0 || overlap >= 0.5 * areaA)
            && (areaB <= 0 || overlap >= 0.5 * areaB);
    }

    /// <summary>
    /// Lays the newly-created instances out on a grid whose pitch is <b>measured from the cells
    /// themselves</b>, and returns the region they ended up in.
    ///
    /// <para>Measuring is the whole point (see <see cref="GridPitchDbu"/> for what a fixed pitch did).
    /// The cells exist by the time this runs — a PCell's artwork was generated while its component was
    /// resolved — so the largest of them is an ordinary question to ask, and one-and-a-half times it
    /// puts half a cell of clear space between neighbours at any scale.</para>
    ///
    /// <para>The SLOT is still the component's own index in the schematic, not a running count of
    /// placements: a slot has to name the same square every run, or a second run adding one more part
    /// would drop it on top of something placed by the first.</para>
    ///
    /// <para>The returned region is what the caller brings on screen. Instances are the only thing a
    /// user cannot find by looking — they land wherever this puts them rather than where the user
    /// clicked — so saying where they went is part of writing them.</para>
    ///
    /// <para><b>An item is a part or a chain</b> (brief-sdl-1 R-sdl1-3). A chain is placed as one rigid group by its
    /// members' joint bounding box, and every member moves by the same whole-DBU offset, so the joints the chain
    /// made stay made. A lone part is a group of one, and is placed exactly as it was before chains existed.</para>
    /// </summary>
    private static Bbox PlaceNewInstances(
        List<(int Slot, IReadOnlyList<LayoutInstance> Members)> placed, string targetLayoutBaseDir, Bbox? below = null)
        => PlaceNewInstances(placed, targetLayoutBaseDir, below, out _);

    /// <param name="shifted">How far the rows were moved to sit under <paramref name="below"/>; zero when they were not.</param>
    private static Bbox PlaceNewInstances(
        List<(int Slot, IReadOnlyList<LayoutInstance> Members)> placed, string targetLayoutBaseDir, Bbox? below,
        out (long Dx, long Dy) shifted)
    {
        shifted = (0, 0);
        if (placed.Count == 0) return Bbox.Empty;
        static void Shift((int Slot, IReadOnlyList<LayoutInstance> Members) item, long dx, long dy)
        {
            foreach (var inst in item.Members) { inst.X += dx; inst.Y += dy; }
        }
        if (below is { IsEmpty: false } board)
        {
            // What the artwork placed is the board; the rest is laid out in rows under it, never over it.
            var rows = PlaceNewInstances(placed, targetLayoutBaseDir);
            if (rows.IsEmpty) return rows;
            long clear = Math.Max(1, (board.MaxY - board.MinY) / 10);
            long dx = board.MinX - rows.MinX, dy = board.MinY - clear - rows.MaxY;
            foreach (var item in placed) Shift(item, dx, dy);
            shifted = (dx, dy);
            return new Bbox(rows.MinX + dx, rows.MinY + dy, rows.MaxX + dx, rows.MaxY + dy);
        }

        // Measured where they are — a lone part still at the origin, a chain where its anchor at the origin put it:
        // an instance's box includes its own placement — the rotation Run gave it included — so every offset below
        // is exact, and it is applied as a SHIFT so a chain's members keep where they are relative to each other.
        var extents = placed.Select(p => p.Members.Aggregate(Bbox.Empty,
                                         (b, inst) => b.Union(CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir)))).ToList();
        var sizes = extents.Where(b => !b.IsEmpty).Select(b => Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY))
                           .Where(d => d > 0).Order().ToList();

        if (sizes.Count == 0)
        {
            // Nothing measurable: the fixed grid, which is still better than stacking everything on the origin.
            for (int i = 0; i < placed.Count; i++)
                Shift(placed[i], (i % GridCols) * GridPitchDbu, -(i / GridCols) * GridPitchDbu);
            return Bbox.Empty;
        }

        // DESIGNER REPORT (round 12): a 400 mm line and two SMD parts, and only the line on screen. A uniform pitch
        // of one and a half times the LARGEST cell put the 1.5 mm parts 600 mm and 1.2 m away. So each part takes
        // its own width, and the clear space between neighbours is half a TYPICAL part (the median), which reads as
        // "laid out, not touching" for the parts and does not let one long line push everything else out of view.
        long gap = Math.Max(1, sizes[sizes.Count / 2] * GridGapNumerator / GridGapDenominator);

        var region = Bbox.Empty;
        long rowCentre = 0;
        for (int first = 0; first < placed.Count; first += GridCols)
        {
            int last = Math.Min(first + GridCols, placed.Count);
            long rowHeight = 0;
            for (int i = first; i < last; i++)
                if (!extents[i].IsEmpty) rowHeight = Math.Max(rowHeight, extents[i].MaxY - extents[i].MinY);
            if (first > 0) rowCentre -= rowHeight / 2;

            long cursor = 0;
            for (int i = first; i < last; i++)
            {
                var bb = extents[i];
                if (bb.IsEmpty)
                {
                    Shift(placed[i], cursor, rowCentre);
                    cursor += gap;
                    continue;
                }
                long dx = cursor - bb.MinX, dy = rowCentre - (bb.MinY + bb.MaxY) / 2;
                Shift(placed[i], dx, dy);
                cursor += bb.MaxX - bb.MinX + gap;
                region = region.Union(new Bbox(bb.MinX + dx, bb.MinY + dy, bb.MaxX + dx, bb.MaxY + dy));
            }
            rowCentre -= rowHeight / 2 + gap;
        }
        return region;
    }

    /// <summary>
    /// Designer report (round 16): a schematic recognised from a board, modelled, and synced back into a new layout
    /// came out as a grid of parts. A component that models artwork carries where it was on the board
    /// (<see cref="EditableComponent.ArtworkAnchor"/>, the source layout's DBU), so in any layout but the board's own
    /// it is placed there: a line with pin 1 on its first anchor point and running toward the next, a part centred
    /// on its anchor, facing as the run already turned it. Those placed are taken out of <paramref name="placed"/>;
    /// the region they cover is returned.
    /// </summary>
    private static Bbox PlaceAtArtwork(
        List<(int Slot, LayoutInstance Instance)> placed, List<EditableComponent> physical, SchematicEditModel model,
        string schematicDir, LayoutView target, string targetLayoutBaseDir, Technology? technology)
    {
        if (model.ArtworkSource is not { Layout: { Length: > 0 } source }) return Bbox.Empty;
        string sourcePath = CircuitRF.Core.RefPath.Resolve(schematicDir, source);
        int sourceDbu = (File.Exists(sourcePath) ? LayoutPersistence.TryReadUnits(sourcePath)?.DbuPerMicron : null)
                        ?? LayoutUnits.DefaultDbuPerMicron;
        double scale = (double)target.DbuPerMicron / sourceDbu;
        (long X, long Y) At((long X, long Y) p) => ((long)Math.Round(p.X * scale), (long)Math.Round(p.Y * scale));

        var region = Bbox.Empty;
        foreach (var (slot, inst) in placed.ToList())
        {
            var comp = physical[slot];
            if (!comp.FromArtwork || comp.ArtworkAnchor.Count == 0) continue;
            var anchor = comp.ArtworkAnchor;
            if (IsStraightLine(comp.Symbol) && anchor.Count >= 2 && anchor[1] != anchor[0])
            {
                var (a, b) = (At(anchor[0]), At(anchor[1]));
                double deg = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
                var facing = new VisualOrientation(false, Math.Round(deg, 6));
                inst.MirrorX = false;
                inst.RotationDegrees = facing.Deg;
                inst.OrientationLink = SchematicLayoutOrientation.Link((int)comp.Rotation, comp.MirrorX, facing);
                (inst.X, inst.Y) = a;
            }
            else
            {
                if (comp.ArtworkMeasured.TryGetValue(RecognitionEmit.PadAxisKey, out double padAxis)
                    && PinAxisDeg(inst, targetLayoutBaseDir, technology) is { } cellAxis)
                {
                    var facing = new VisualOrientation(false, Math.Round(padAxis - cellAxis, 6));
                    inst.MirrorX = false;
                    inst.RotationDegrees = facing.Deg;
                    inst.OrientationLink = SchematicLayoutOrientation.Link((int)comp.Rotation, comp.MirrorX, facing);
                }
                var c = At(ArtworkAnchors.Centre(anchor)!.Value);
                var bb = CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir);
                if (bb.IsEmpty) (inst.X, inst.Y) = c;
                else (inst.X, inst.Y) = (c.X - (bb.MinX + bb.MaxX) / 2, c.Y - (bb.MinY + bb.MaxY) / 2);
            }
            placed.Remove((slot, inst));
            var box = CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir);
            if (!box.IsEmpty) region = region.Union(box);
        }
        return region;
    }

    /// <summary>
    /// What a layout holds that this command did not put there (R-dfr34-5-3): every shape but labels, pictures and
    /// generated artwork, and every placement with no <see cref="LayoutInstance.SchematicId"/>. Parts and ground artwork
    /// an earlier run placed are not counted, so a re-run into a layout this command built keeps its slot rows.
    /// </summary>
    private static Bbox DrawnArtworkExtent(LayoutView target, string targetLayoutBaseDir)
    {
        var box = Bbox.Empty;
        foreach (var shape in target.Shapes)
            if (shape is not (LabelShape or BitmapShape) && shape.Generated is null)
                box = box.Union(LayoutGeometry.BboxOf(shape));
        foreach (var inst in target.Instances)
            if (inst.SchematicId is not { Length: > 0 })
                box = box.Union(CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir));
        return box;
    }

    /// <summary>The direction from a cell's pin 1 to its pin 2 as the cell is drawn, degrees; null without both.</summary>
    private static double? PinAxisDeg(LayoutInstance inst, string targetLayoutBaseDir, Technology? technology)
    {
        var cell = CellLayoutResolver.Resolve(inst, targetLayoutBaseDir, technology);
        if (cell.State != CellLayoutState.Resolved) return null;
        var pins = CellPins.Resolve(cell.View!, technology);
        if (pins.FirstOrDefault(p => p.Name == "1") is not { } p1 || pins.FirstOrDefault(p => p.Name == "2") is not { } p2
            || (p1.X == p2.X && p1.Y == p2.Y))
            return null;
        return Math.Atan2(p2.Y - p1.Y, p2.X - p1.X) * 180.0 / Math.PI;
    }

    /// <summary>The lines whose cell starts at pin 1 on the origin and runs along +X to pin 2.</summary>
    private static bool IsStraightLine(SymbolKind kind) =>
        kind is SymbolKind.Mlin or SymbolKind.Cpwg or SymbolKind.Slin or SymbolKind.Mtaper;

    /// <summary>A VIAGND added this run, and the pad it grounds: the first other part's drawn pin on the VIAGND's own
    /// net whose placement (added this run or already there) and pad resolve. A VIAGND whose net reaches no such pad
    /// is placed in the row like any other part.</summary>
    private static Dictionary<LayoutInstance, (LayoutInstance Instance, LayoutView Cell, LayoutPin Pin)> ViaGndPartners(
        List<(int Slot, LayoutInstance Instance)> added, List<EditableComponent> physical,
        NetExtractor.ExtractionResult extraction, LayoutView target,
        Dictionary<string, (int Index, LayoutInstance Instance)> existing, Dictionary<int, LayoutInstance> replacedAt,
        string schematicDir, string targetLayoutBaseDir, Technology? technology)
    {
        var result = new Dictionary<LayoutInstance, (LayoutInstance, LayoutView, LayoutPin)>(ReferenceEqualityComparer.Instance);
        if (technology is null) return result;

        var nets = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) nets.TryAdd(inst.InstanceName, inst);
        var placedById = new Dictionary<string, LayoutInstance>(StringComparer.Ordinal);
        foreach (var (sid, e) in existing) placedById[sid] = replacedAt.GetValueOrDefault(e.Index, e.Instance);
        foreach (var (_, inst) in added)
            if (inst.SchematicId is { Length: > 0 } sid) placedById[sid] = inst;

        foreach (var (slot, via) in added)
        {
            var comp = physical[slot];
            if (comp.Symbol != SymbolKind.ViaGnd || !nets.TryGetValue(comp.InstanceName, out var viaNets) ||
                viaNets.NetBindings.Count == 0 || viaNets.NetBindings[0] is not { } net || net == "0") continue;

            foreach (var other in physical)
            {
                if (ReferenceEquals(other, comp) || ViaSubstrateInjection.IsViaKind(other.Symbol)) continue;
                if (!nets.TryGetValue(other.InstanceName, out var otherNets) ||
                    !placedById.TryGetValue(other.InstanceName, out var partner)) continue;
                var symbol = other.ExternalSymbolRef is { } symRef ? CellSymbolResolver.Resolve(symRef, schematicDir) : null;
                int drawn = Math.Min(other.ToRenderComponent(null, symbol).Ports.Count, otherNets.NetBindings.Count);
                int k = Enumerable.Range(0, drawn).FirstOrDefault(i => otherNets.NetBindings[i] == net, -1);
                if (k < 0) continue;
                if (CellLayoutResolver.Resolve(partner, targetLayoutBaseDir, technology) is not
                        { State: CellLayoutState.Resolved, View: { } cell }) continue;
                if (SchematicLayoutOrientation.CellPinFor(k + 1, CellPins.Resolve(cell, technology)) is not { } pin) continue;
                result[via] = (partner, cell, pin);
                break;
            }
        }
        return result;
    }

    // ── Shared PCell-eligibility helpers (also used by the palette→layout drag path, §3) ──────────

    /// <summary>True when <paramref name="kind"/> has a registered PCell generator (R-L5-8's
    /// droppability gate: "only components that HAVE a layout generator are droppable"). Mirrors the
    /// same <c>EngineReference</c> → <see cref="PCellRegistry"/> lookup <see cref="ResolveComponentLayout"/>
    /// uses for a schematic component, so the two entry points can never disagree about which
    /// components have layout artwork.</summary>
    public static bool HasPCellGenerator(SymbolKind kind, int portCount, out string generatorId)
    {
        generatorId = ComponentTypeRegistry.EngineReference(kind, portCount);
        return PCellRegistry.TryGet(generatorId, out _);
    }

    /// <summary>Resolves a freshly-placed component's DEFAULT parameters (R-L5-7's drag ghost: "the
    /// component's real generated artwork at its default parameters") to the same SI values
    /// <see cref="ResolveComponentLayout"/> would compute for a schematic instance — default
    /// expressions are always plain literals (never variable references), so an empty <see cref="Scope"/>
    /// is exact, not an approximation.
    ///
    /// <para><b>OWNER REPORT — a microstrip dropped from the palette into a LAYOUT ignored the
    /// technology.</b> The registry's own microstrip widths are a fixed 2.9 mm baseline, rewritten for
    /// the placing workspace's substrate (50 Ω synthesis, round lengths, the technology's own unit,
    /// rounded there) by <see cref="MicrostripSubstrateInjection.ApplyTechnologyDefaults"/> — which
    /// <c>SchematicViewModel.CommitPlacement</c> has always called and this path never did. So the
    /// same MLIN read 42 mil placed on a schematic and 114.1732 mil dropped on a layout.</para>
    ///
    /// <para>Fixed by routing the registry defaults through that SAME method rather than adding a
    /// second synthesis — there is one rule for what a freshly-placed microstrip's width is, and both
    /// editors now read it from one place. <paramref name="technology"/> null (no technology resolves,
    /// or a non-microstrip kind) leaves the mm baseline exactly as before.</para></summary>
    public static IReadOnlyDictionary<string, PCellValue> ResolveDefaultParameters(
        SymbolKind kind, int portCount, Technology? technology = null)
    {
        // CLONED, never the registry's own instances: DefaultParameters splices in the shared static
        // SignalGroundLayerParams array, and ApplyTechnologyDefaults writes Expression/Unit in place.
        // Mirrors CommitPlacement's own clone-then-rewrite for the same reason.
        var defaults = new List<EditableParameter>();
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, portCount))
            defaults.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });

        if (MicrostripSubstrateInjection.IsMicrostripKind(kind))
            MicrostripSubstrateInjection.ApplyTechnologyDefaults(defaults, technology, kind);

        var scope = new Scope("global");
        var evaluator = new Evaluator();
        var resolved = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
        foreach (var dp in defaults)
        {
            if (NonPCellParamNames.Contains(dp.Name)) continue;
            if (TryResolveSiValue(dp.Expression, dp.Unit, scope, evaluator, out var value, out _))
                resolved[dp.Name] = value;
        }
        return resolved;
    }

    // ── Per-component layout resolution ──────────────────────────────────────

    /// <summary>Resolves what <paramref name="comp"/> should instance in the target layout: an
    /// existing cell (hierarchical CellRef component) or a generated PCell cell (a built-in with a
    /// registered generator). Returns null (with <paramref name="resolveWarning"/> naming why) when
    /// there is no layout view at all — §9 step 2's "reported and skipped" case.</summary>
    private static string? ResolveComponentLayout(
        EditableComponent comp, SchematicEditModel model, string schematicDir,
        string workspaceRootDir, string targetLayoutBaseDir, Ledger ledger,
        Technology? technology, string? techIdentity,
        Scope scope, Evaluator evaluator,
        out IReadOnlyDictionary<string, PCellValue>? pcellParams, out string? generatorId,
        out string? resolveWarning, out IReadOnlyList<string>? pcellDiagnostics,
        out string? footprintRefusal)
    {
        pcellParams = null;
        generatorId = null;
        resolveWarning = null;
        pcellDiagnostics = null;
        footprintRefusal = null;

        // R-fp3-1: a STATED footprint is consulted FIRST, ahead of the kit / CellRef / PCell chain.
        // First and not last because a stated footprint is an explicit choice somebody made and the
        // other three are inferences. The one case this reorders in practice is a component carrying
        // BOTH a CellRef and a Footprint, which R-fp2-3c never produces by default — it can only
        // arise from a deliberate act, and honouring the deliberate act is right.
        //
        // R-fp3-1d: None (no Footprint parameter at all) does not enter here, so a component with no
        // footprint resolves exactly as it did before this brief.
        if (comp.Footprint is { Length: > 0 } footprint)
            return ResolveFootprintLayout(
                comp, footprint, schematicDir, workspaceRootDir, targetLayoutBaseDir, ledger,
                technology, techIdentity,
                out pcellParams, out generatorId, out resolveWarning, out pcellDiagnostics,
                out footprintRefusal);

        // An imported kit's part is a VIRTUAL reference, and treating it as a path is what made this
        // report "referenced cell not found" for every one of them — which is false and sends the
        // user looking for a missing folder. The part IS loaded; what it may lack is a layout
        // generator, and that is a different sentence with a different answer.
        string? kitGeneratorId = null;
        if (comp.CellRef is { } kitRef && PdkKitRegistry.IsKitRef(kitRef))
        {
            if (!PdkKitRegistry.TryParse(kitRef, out string kitName, out string partId))
            {
                resolveWarning = "kit reference could not be read";
                return null;
            }

            if (PdkKitRegistry.Find(kitRef, workspaceRootDir) is null)
            {
                resolveWarning = $"the kit \"{kitName}\" is not loaded in this workspace";
                return null;
            }

            // Which of a kit's layout cells is THIS part's is settled once, by the palette, and read
            // here — never worked out a second time. A kit names its schematic part and its layout
            // cell independently, so the answer is not always the part id, and two derivations of it
            // are how a tile and a design come to disagree about a part's artwork.
            // See KitPaletteMerge for the rules and KitLayoutGenerators for why it is published. The
            // palette lives in src/Ui, so its answer arrives as an argument (KitLayoutGeneratorLookup);
            // headless, with no palette, the part id is the answer, as it was before a palette had read
            // the kit.
            string kitGenerator = ledger.KitLayoutGenerator?.Invoke(workspaceRootDir, kitName, partId) ?? partId;

            if (!PCellRegistry.TryGet(kitGenerator, out _))
            {
                // Short, because by the time this is reached it is an ordinary fact about the kit
                // rather than something to fix: a model-only part (a parasitic capacitance, a
                // technology include) has no artwork to place, and the earlier long explanation was
                // written for a period when the pairing itself was routinely failing. If a kit's
                // cells are not being paired at all, that is KitPaletteMerge's business, not a
                // sentence to put in front of a user once per placed part.
                resolveWarning = $"the kit \"{kitName}\" has no layout cell for \"{partId}\"";
                return null;
            }

            kitGeneratorId = kitGenerator;
        }
        else if (comp.CellRef is not null)
        {
            if (ExternalCellRef.ResolveCellDir(comp.CellRef, schematicDir) is not { } cellAbsDir)
            { resolveWarning = "cell reference could not be resolved"; return null; }

            if (!Directory.Exists(cellAbsDir)) { resolveWarning = "referenced cell not found"; return null; }

            var primary = CellFolder.ResolvePrimary(cellAbsDir, ViewType.Layout);
            if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
            {
                resolveWarning = "referenced cell has no layout view";
                return null;
            }

            // brief-sdl-10 D3 — an instance whose own values give the cell other copper is placed as a variant of it.
            if (Em.ParametricEm.EmVariantCells.For(comp, cellAbsDir, workspaceRootDir, scope, evaluator, out string? variantNote)
                    is { } variant)
            {
                ledger.Lines.Add(new ReportLine(comp.InstanceName,
                    $"{comp.InstanceName} — placed as a variant of {Path.GetFileName(Path.TrimEndingDirectorySeparator(cellAbsDir))} " +
                    $"at its own values ({variant.At}); the cell's own layout is unchanged", ReportSeverity.Info) { Ids = [comp.InstanceName] });
                return ToRelative(targetLayoutBaseDir, variant.CellDir);
            }
            if (variantNote is not null)
                ledger.Lines.Add(new ReportLine(comp.InstanceName, variantNote, ReportSeverity.Info) { Ids = [comp.InstanceName] });

            return ToRelative(targetLayoutBaseDir, cellAbsDir);
        }

        string reference = kitGeneratorId
                        ?? ComponentTypeRegistry.EngineReference(comp.Symbol, comp.PortCount);
        if (!PCellRegistry.TryGet(reference, out var generator))
            return null; // no PCell generator and no CellRef — an ordinary electrical-only component

        // What THIS generator says its parameters are, and what KIND each one is. Null for a built-in,
        // which declares its interface in code and is left exactly as it was. See DeclaredInterface.
        var declared = PCellRegistry.DeclaredDefaults(reference);

        // Seeded from the declaration, so an instance that states three of a cell's fourteen
        // parameters produces the SAME cell as dropping that cell from the palette and editing those
        // three — one artwork per parameter set, not two identical ones under different names.
        var resolved = declared is null
            ? new Dictionary<string, PCellValue>(StringComparer.Ordinal)
            : new Dictionary<string, PCellValue>(declared, StringComparer.Ordinal);

        List<string>? paramNotes = null;

        foreach (var p in comp.Parameters)
        {
            if (NonPCellParamNames.Contains(p.Name)) continue;
            if (IsInactiveMklopfEntryParam(comp, p.Name)) continue; // R-L5f-3

            // A via's layer choices are conductor NAMES, handed to its generator as text; a dimension
            // left empty follows the technology, which the generator resolves itself; and the two
            // capacitance controls are the model's alone (brief-via-component.md R-viac-4).
            if (ViaSubstrateInjection.IsViaKind(comp.Symbol))
            {
                if (ViaSubstrateInjection.LayerParams.Contains(p.Name))
                {
                    if (p.Expression.Trim() is { Length: > 0 } layerName) resolved[p.Name] = PCellValue.Text(layerName);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(p.Expression) || p.Name is "IncludeC" or "C") continue;
            }

            // A test pad's layer rows, feed side, centre and port number are WORDS for its generator (brief-dfr26-7);
            // a dimension left empty follows the technology, which the generator resolves itself.
            if (comp.Symbol == SymbolKind.TestPad)
            {
                if (p.Name is not ("D" or "FeedW" or "Drill" or "ClearD" or "InnerPadD" or "AntipadD"))
                {
                    if (p.Expression.Trim() is { Length: > 0 } word) resolved[p.Name] = PCellValue.Text(word);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(p.Expression)) continue;
            }

            // A TFR's film is a drawing layer's NAME for its generator, and empty states nothing, so a part stating no film
            // hashes to the cell it always did.
            if (comp.Symbol == SymbolKind.Tfr && p.Name == MmicPassiveInjection.FilmParam)
            {
                if (p.Expression.Trim() is { Length: > 0 } film) resolved[p.Name] = PCellValue.Text(film);
                continue;
            }

            // A broadside pair's (BCLIN/MBCLIN) layer choices are conductor NAMES for its generator, as a
            // via's are; its cross-section rows are the MODEL's alone (the artwork is W, O and L on the
            // two layers), and an empty one follows the technology.
            if (BroadsideSubstrateInjection.IsBroadsideKind(comp.Symbol))
            {
                if (BroadsideSubstrateInjection.LayerParams.Contains(p.Name))
                {
                    if (p.Expression.Trim() is { Length: > 0 } layerName) resolved[p.Name] = PCellValue.Text(layerName);
                    continue;
                }
                if (p.Name is not ("W" or "O" or "L")) continue;
            }

            // A parameter this generator does not declare is not its parameter. A kit part carries
            // circuitRF's own routing rows and the kit's model-selection rows alongside the dimensions,
            // and every one of them used to be pushed through the numeric resolver — where the first
            // that is not a number takes the whole instance's artwork down with it.
            if (declared is not null && !declared.ContainsKey(p.Name)) continue;

            if (declared is not null && declared[p.Name].Kind == PCellValueKind.String)
            {
                resolved[p.Name] = PCellValue.Text(TextForDeclaredString(p, scope, evaluator, out string? note));
                if (note is not null) (paramNotes ??= []).Add($"{p.Name}: {note}");
                continue;
            }

            if (!TryResolveSiValue(p.Expression, p.Unit, scope, evaluator, out var value, out var error))
            {
                resolveWarning = $"parameter '{p.Name}': {error}";
                return null;
            }
            resolved[p.Name] = value;
        }

        string? signalOverride = NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "SignalLayer")?.Expression);
        string? groundOverride = NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "GroundReference")?.Expression);
        var layerSelection = new PCellLayerSelection(signalOverride, groundOverride);

        // A layer row naming no conductor falls back to the default inside every generator, which drops the resolver's
        // warning — so a line meant for the bottom copper was drawn on the top with nothing said. Said here, once per
        // part, naming the conductors the row can take: they are the stackup's names, not the drawing layers'.
        if (technology is not null)
        {
            var conductors = technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name).ToList();
            foreach (var (row, name, fallback) in new[]
                     {
                         ("SignalLayer", signalOverride, "it is drawn on the topmost conductor"),
                         ("GroundReference", groundOverride, "the default ground reference is used"),
                     })
                if (name is not null && conductors.Count > 0
                    && !conductors.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                    (paramNotes ??= []).Add($"{row}: no conductor is named '{name}', so {fallback}; the conductors " +
                                            $"are {string.Join(", ", conductors.Select(c => $"'{c}'"))}");
        }

        // R-L5f-3: MKlopfPCell.Generate only ever reads Z1/Z2/GammaMax/L/Offset/SmoothSteps — it has
        // no notion of the alternate W1/W2 or F3db entry routes. When one of those is the active
        // route, convert it to the canonical keys here, the SAME conversion (and the SAME substrate
        // resolution) ComponentModelFactory.CreateMicrostripKlopfModel already uses to make "a
        // schematic in W1/W2 mode simulates correctly today" true — skipping the inactive route's
        // raw expression is not enough on its own; the ACTIVE alternate route must still reach the
        // generator as Z1/Z2/L, or the artwork silently reverts to the 50 Ω / 100 Ω / 20 mm defaults
        // regardless of what W1/W2/F3db actually say.
        if (reference == "MKLOPF")
            ResolveMklopfCanonicalParams(resolved, technology, layerSelection);

        // The same for a line stating its length electrically (designer feedback round 21): the generator draws
        // L, so E at F becomes the length the run simulates — LineEntryConversion, through the model's own εeff.
        if (LineEntryConversion.Applies(comp.Symbol) && !resolved.ContainsKey("L") && resolved.ContainsKey("E"))
        {
            var si = resolved.Where(kv => kv.Key is "W" or "G" or "E" or "F")
                             .ToDictionary(kv => kv.Key, kv => resolved.Real(kv.Key), StringComparer.Ordinal);
            // TryResolveSiValue hands an angle stated in degrees back IN DEGREES — the unit PCell generators take
            // a bend in — while LengthMeters reads E in radians. Passed through as it was, a 90° line at 10 GHz
            // was drawn 57× too long (designer report, 2026-10-20: a quarter-wave stub came out 252 mm).
            if (string.Equals(UnitNormalizer.ToEngineUnit(comp.Parameters.FirstOrDefault(p => p.Name == "E")?.Unit),
                              "deg", StringComparison.Ordinal))
                si["E"] *= Math.PI / 180.0;
            if (LineEntryConversion.LengthMeters(comp.Symbol, si, signalOverride, groundOverride, technology) is { } lengthM)
                resolved["L"] = lengthM;
            resolved.Remove("E");
            resolved.Remove("F");
        }

        string cellDir;
        try
        {
            cellDir = GeneratedCellStore.GetOrCreate(
                workspaceRootDir, reference, resolved, technology, techIdentity, layerSelection, out pcellDiagnostics);
            ledger.RecordPCellSnapshot(GeneratedCellStore.SnapshotFor(
                cellDir, reference, resolved, techIdentity, layerSelection, workspaceRootDir));
        }
        catch (Exception ex)
        {
            resolveWarning = $"PCell generation failed: {ex.Message}";
            return null;
        }

        // Said alongside the generator's own diagnostics, on the instance that has the row — not as a
        // failure, because the artwork was still produced. See TextForDeclaredString for why an
        // unevaluable row is worth a line rather than silence.
        if (paramNotes is { Count: > 0 })
            pcellDiagnostics = pcellDiagnostics is null ? paramNotes : [.. paramNotes, .. pcellDiagnostics];

        pcellParams = resolved;
        generatorId = reference;
        return ToRelative(targetLayoutBaseDir, cellDir);
    }

    /// <summary>
    /// brief-sdl-4 §2: the cell for a parameter set that is neither side's alone — the schematic's change applied over a
    /// value the layout kept. Generated exactly as <see cref="ResolveComponentLayout"/> generates the schematic's own
    /// set, through <see cref="GeneratedCellStore"/>, so the same set is the same cell. Null when generation fails, and
    /// the caller then keeps the schematic's cell.
    /// </summary>
    private static string? RegeneratePCell(EditableComponent comp, string generatorId, Dictionary<string, PCellValue> parameters,
                                           string workspaceRootDir, string targetLayoutBaseDir, Ledger ledger,
                                           Technology? technology, string? techIdentity)
    {
        var layerSelection = new PCellLayerSelection(
            NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "SignalLayer")?.Expression),
            NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "GroundReference")?.Expression));
        try
        {
            string cellDir = GeneratedCellStore.GetOrCreate(workspaceRootDir, generatorId, parameters, technology, techIdentity,
                                                            layerSelection, out _);
            ledger.RecordPCellSnapshot(GeneratedCellStore.SnapshotFor(cellDir, generatorId, parameters, techIdentity,
                                                                      layerSelection, workspaceRootDir));
            return ToRelative(targetLayoutBaseDir, cellDir);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The footprint branch of <see cref="ResolveComponentLayout"/> — brief-footprint-3 R-fp3-1.
    ///
    /// <para><b>Two forms, one discriminator.</b> A reference that parses as <c>smt:</c> is one of
    /// circuitRF's built-in land patterns and resolves through <c>ChipLandPatternGenerator</c> into
    /// the content-addressed <see cref="GeneratedCellStore"/>, exactly as a microstrip PCell does —
    /// one generated cell per (case, density, technology) triple, nothing new invented for storage
    /// (R-fp3-1b). ANYTHING else is a path, resolved through <c>ExternalCellRef.ResolveCellDir</c>
    /// and <c>CellFolder.ResolvePrimary</c> with the same refusals and the same sentences a
    /// <c>CellRef</c> gets (R-fp3-1c).</para>
    ///
    /// <para><b>The technology is the LAYOUT's</b> (R-fp3-2) — the same <paramref name="technology"/>
    /// every other branch is handed, which is the layout's own <c>TechRef</c> first and the workspace
    /// default only as a fallback. Which is exactly why the divergence report gained a second trigger:
    /// a schematic of thirteen capacitors laid into a layout on another technology would otherwise
    /// place its lands on the wrong layers in silence.</para>
    /// </summary>
    private static string? ResolveFootprintLayout(
        EditableComponent comp, string footprint, string schematicDir, string workspaceRootDir,
        string targetLayoutBaseDir, Ledger ledger, Technology? technology, string? techIdentity,
        out IReadOnlyDictionary<string, PCellValue>? pcellParams, out string? generatorId,
        out string? resolveWarning, out IReadOnlyList<string>? pcellDiagnostics,
        out string? footprintRefusal)
    {
        pcellParams = null;
        generatorId = null;
        resolveWarning = null;
        pcellDiagnostics = null;
        footprintRefusal = null;

        string cellRef;
        string cellAbsDir;

        if (FootprintRef.IsBuiltInReference(footprint))
        {
            if (!FootprintRef.TryParse(footprint, out var reference, out string? refusal))
            {
                // The parser's own sentence, which already lists the case sizes circuitRF knows —
                // re-wording it here would give the same mistake two different answers depending on
                // whether it was made in the picker or read off a file.
                resolveWarning = refusal;
                return null;
            }

            // The canonical spelling IS the generator id (R-fp1-2b), so two densities of one case are
            // two generated cells rather than one that silently serves both. The parameter set is
            // empty by construction: a land pattern's case and density are its IDENTITY, not its
            // parameters (FootprintGeneratorResolver.DeclaredDefaults).
            string generator = reference!.ToString();
            var parameters = new Dictionary<string, PCellValue>(StringComparer.Ordinal);

            string generatedDir;
            try
            {
                generatedDir = GeneratedCellStore.GetOrCreate(
                    workspaceRootDir, generator, parameters, technology, techIdentity,
                    PCellLayerSelection.Default, out pcellDiagnostics);
                ledger.RecordPCellSnapshot(GeneratedCellStore.SnapshotFor(
                    generatedDir, generator, parameters, techIdentity, PCellLayerSelection.Default, workspaceRootDir));
            }
            catch (Exception ex)
            {
                resolveWarning = $"footprint '{footprint}' could not be generated: {ex.Message}";
                return null;
            }

            generatorId = generator;
            pcellParams = parameters;
            cellAbsDir  = generatedDir;
            cellRef     = ToRelative(targetLayoutBaseDir, generatedDir);
        }
        else
        {
            if (ResolveFootprintPath(footprint, schematicDir, out cellAbsDir, out string? pathRefusal) is false)
            {
                resolveWarning = pathRefusal;
                return null;
            }
            cellRef = ToRelative(targetLayoutBaseDir, cellAbsDir);
        }

        // R-fp3-5b: the pad count is the RESOLVED view's pin count — a generated pattern's declared
        // pins and an imported cell's own pins land in the same place, so nothing here needs to know
        // which produced them.
        var resolution = CellLayoutResolver.Resolve(cellRef, targetLayoutBaseDir);
        if (resolution is not { State: CellLayoutState.Resolved, View: { } view })
        {
            resolveWarning = $"footprint '{footprint}' has no layout view";
            return null;
        }

        int pads = view.Pins.Count;

        // A generator that refused (R-fp1-3b: a technology with no copper role has no land pattern to
        // draw) produces an empty result carrying one sentence, and its OWN sentence is the useful
        // one — reported as "0 pads" it would name a symptom and hide the cause.
        if (pads == 0 && pcellDiagnostics is { Count: > 0 })
        {
            resolveWarning = string.Join(" ", pcellDiagnostics);
            return null;
        }

        int ports = comp.EffectivePortCount;
        if (pads != ports)
        {
            // R-fp3-5d: reported and skipped, never placed-and-flagged. Artwork on the board with the
            // wrong pad count is artwork somebody routes to.
            footprintRefusal =
                $"{comp.InstanceName} states footprint {EditableComponent.FootprintDisplayName(footprint)}, " +
                $"which has {pads} pad{(pads == 1 ? "" : "s")}, and the component has " +
                $"{ports} port{(ports == 1 ? "" : "s")}. Nothing was placed.";
            return null;
        }

        return cellRef;
    }

    /// <summary>
    /// A footprint stated as a PATH — R-fp3-1c. A cell folder resolves exactly as a <c>CellRef</c>
    /// does, with the same refusals.
    ///
    /// <para><b>A <c>.clay</c> FILE is accepted, and is the reason this is not simply the
    /// <c>CellRef</c> branch called twice.</b> Custom means "this file". But a
    /// <see cref="LayoutInstance"/> names a cell FOLDER and draws that folder's PRIMARY layout view —
    /// there is no per-view reference in this format — so the only honest answer for a file that is
    /// not the primary is to say so and name what the primary actually is. Quietly placing the
    /// primary instead would put different artwork on the board than the one the user pointed at,
    /// which is precisely the silent substitution the density suffix exists to make visible.</para>
    /// </summary>
    private static bool ResolveFootprintPath(
        string footprint, string schematicDir, out string cellAbsDir, out string? refusal)
    {
        cellAbsDir = "";
        refusal = null;

        if (ExternalCellRef.ResolveCellDir(footprint, schematicDir) is not { } resolved)
        { refusal = $"footprint '{footprint}' could not be resolved"; return false; }

        if (footprint.EndsWith(CellFolder.ViewExtension(ViewType.Layout), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(resolved)) { refusal = $"footprint '{footprint}' not found"; return false; }

            // <cell>/layout/<name>.clay — the cell folder is two levels up.
            string layoutDir = Path.GetDirectoryName(resolved) ?? "";
            string owningCell = Path.GetDirectoryName(layoutDir) ?? "";
            if (owningCell.Length == 0 || !Directory.Exists(owningCell))
            {
                refusal = $"footprint '{footprint}' is a layout file that does not belong to a cell";
                return false;
            }

            var filePrimary = CellFolder.ResolvePrimary(owningCell, ViewType.Layout);
            if (filePrimary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
            {
                refusal = $"footprint '{footprint}' is a layout file that does not belong to a cell";
                return false;
            }

            if (!string.Equals(filePrimary.ResolvedName, Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase))
            {
                refusal = $"footprint '{footprint}' is not its cell's primary layout view — " +
                          $"'{filePrimary.ResolvedName}' is, and an instance draws the primary. " +
                          "Make it primary, or point the footprint at a cell of its own";
                return false;
            }

            cellAbsDir = owningCell;
            return true;
        }

        if (!Directory.Exists(resolved)) { refusal = $"footprint '{footprint}' not found"; return false; }

        var primary = CellFolder.ResolvePrimary(resolved, ViewType.Layout);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
        { refusal = $"footprint '{footprint}' has no layout view"; return false; }

        cellAbsDir = resolved;
        return true;
    }

    /// <summary>Ground/Pin/Var/Meas (and Open/Short-disabled components) are never physical — mirrors
    /// <c>NetExtractor.ExtractModel</c>'s own instance-emission skip set exactly, so "reported as
    /// missing a layout view" never fires for the schematic's own meta-components (a VAR row or a
    /// Ground symbol has no layout existence to report as missing).</summary>
    /// <summary>Whether the components that model existing artwork are generated into <paramref name="targetLayoutPath"/>:
    /// yes when the schematic's provenance names a different layout as its artwork, so there is no artwork here.</summary>
    internal static bool GeneratesArtworkModels(SchematicEditModel model, string schematicDir, string? targetLayoutPath)
    {
        if (targetLayoutPath is null || model.ArtworkSource is not { Layout: { Length: > 0 } source }) return false;
        string artwork = Path.GetFullPath(CircuitRF.Core.RefPath.Resolve(schematicDir, source));
        return !string.Equals(artwork, Path.GetFullPath(targetLayoutPath),
                              OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>R-as6-7's one run-level line.</summary>
    internal static string FromArtworkNote(int count) =>
        count == 1 ? "1 component models existing artwork — not generated"
                   : $"{count} components model existing artwork — not generated";

    internal static bool IsPhysical(EditableComponent comp) =>
        comp.Disable is not (DisableState.Open or DisableState.Short)
        && comp.Symbol is not (SymbolKind.Ground or SymbolKind.Pin or SymbolKind.Var or SymbolKind.Meas
                            or SymbolKind.VProbe   // a name, not a part: no artwork to place
                            // wbond.md §9.5/WB41: a wBond is emitted as the CELL's own `.wBond`
                            // sidecar (WBondCellSeeding), not as a placed instance — WB23 is explicit
                            // that no wire ever enters a `.clay`. Left in this set it resolved no
                            // layout view and reported "no layout view — skipped", which is a true
                            // statement about a mechanism the user has no reason to know about
                            // (owner, 2026-08-17).
                            or SymbolKind.WBond);

    /// <summary>Resolves a schematic parameter's raw expression to the SI value a PCell generator
    /// expects: metres for a length unit, Ohms for a resistance unit, and DEGREES (not radians) for
    /// "deg" — the one deliberate divergence from the engine's own radian convention, because every
    /// PCell generator in <c>src/Ui/Layout/PCells/</c> reads Angle-kind parameters in degrees (see
    /// e.g. <c>MBendPCell.Generate</c>'s own <c>angleDeg</c>). <see cref="Evaluator.Eval"/> already
    /// applies the correct var-unit-wins SI scaling (Core CLAUDE.md) — always to the engine's base
    /// unit, i.e. RADIANS for angle — so converting that result back to degrees here, once, in one
    /// place, is simpler and safer than trying to suppress the engine's own radian conversion
    /// upstream.
    ///
    /// R-L5f-1/2: <paramref name="unit"/> is normalized via <see cref="UnitNormalizer.ToEngineUnit"/>
    /// BEFORE evaluation — the editor stores a glyph unit ("Ω", "µm"), but <see cref="Units.Scale"/>
    /// is ASCII-only ("Ohm", "um"). <see cref="NetExtractor.EmitInstance"/> already normalizes at
    /// exactly this boundary before handing overrides to the elaborator (the reason a schematic with
    /// default MKlopf parameters — Z1/Z2 in Ω — already simulates correctly); this generator must
    /// normalize the identical way or it disagrees with the path that actually works.
    ///
    /// <b>The empty-string trap, found via a failing test, not just by reading the code:</b>
    /// <see cref="Evaluator.Eval"/>'s internal <c>ApplyUnit</c> treats ONLY a null unit as "no unit" —
    /// an empty STRING (a dimensionless parameter's stored <c>Unit</c>, e.g. MKlopf's <c>GammaMax</c>
    /// or MBend's <c>Miter</c>) falls through to <c>Units.Scale("")</c>, which is unrecognized, and
    /// throws <c>"Unknown unit ''"</c>. <see cref="NetExtractor.EmitInstance"/> already guards this
    /// exact trap (<c>unit.Length &gt; 0 ? unit : null</c>) — this is that same guard, applied here.
    /// R-L5f-4: the real exception message is returned via <paramref name="error"/> rather than
    /// swallowed, so a genuine failure (a blank/malformed expression, an unresolvable variable) is
    /// diagnosable instead of a uniform "could not be resolved."</summary>
    internal static bool TryResolveSiValue(string expression, string? unit, Scope scope, Evaluator evaluator, out double value, out string? error)
    {
        string normalized = UnitNormalizer.ToEngineUnit(unit);
        string? engineUnit = normalized.Length > 0 ? normalized : null;
        try
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                value = 0;
                error = "no value set";
                return false;
            }

            var v = evaluator.Eval(expression, scope, engineUnit);
            double raw = v.Kind switch
            {
                ValueKind.Real => v.AsReal(),
                ValueKind.Bool => v.AsBool() ? 1.0 : 0.0,
                _ => double.NaN,
            };
            if (double.IsNaN(raw))
            {
                value = 0;
                error = $"expected a numeric value, got {v.Kind}";
                return false;
            }
            value = string.Equals(engineUnit, "deg", StringComparison.Ordinal) ? raw * 180.0 / Math.PI : raw;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            value = 0;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// A schematic parameter as a generator that declared it a STRING has to receive it.
    ///
    /// <para><b>Why this is not a detail, measured.</b> A vendor cell library
    /// routinely declares every parameter as text — the kit's own defaults are written <c>6.99u</c>,
    /// <c>600n</c>, <c>1</c> — and a NUMBER sent to such a parameter is <b>silently ignored</b>: the
    /// generator falls back to its own default, emits no diagnostic, and draws perfectly. Measured on
    /// the owner's kit: a capacitor cell asked for 30 µm × 30 µm came back as the 6.99 µm default
    /// (28 shapes, 8,190 DBU across) when the value went as a Real, and as the size actually asked
    /// for (532 shapes, 31,200 DBU) when it went as text. Same for a transistor cell — w = 5 µm with
    /// 4 gate fingers drew the 0.6 µm single-finger default. So "resolve everything to a double" does
    /// not merely fail loudly on the odd parameter; it quietly produces a layout that does not match
    /// the schematic it was generated from.</para>
    ///
    /// <para><b>The expression is still EVALUATED, and the result is still SI.</b> A schematic value
    /// may be <c>2*Wg</c> or carry a unit glyph, and a generator must not be handed the source text
    /// of an expression to parse. So this evaluates exactly as <see cref="TryResolveSiValue"/> does —
    /// same scope, same unit normalization, same engine base units — and formats the answer as a
    /// round-trippable invariant decimal. Metres are what such a kit reads a bare number as: the same
    /// capacitor cell given the text <c>7e-06</c> reproduces its own <c>6.99u</c> default artwork.</para>
    ///
    /// <para>Falls back to the raw text when the expression is not numeric at all, which is what a
    /// genuine word-valued parameter (a display mode, a model name, a calculation route) is.</para>
    ///
    /// <para><b><paramref name="note"/> is set for the third case, and it is the one worth saying out
    /// loud.</b> A kit spells its values the way its own simulator does — <c>60u</c>, <c>1.5p</c> — and
    /// since brief-units-in-expressions those ARE circuitRF unit literals: they evaluate above, like any
    /// expression, and reach the cell in metres. What is left is a suffix circuitRF does not know
    /// (<c>60meg</c>, <c>1.5mils</c>). Such a row still reaches the cell verbatim and the artwork still
    /// comes out right, because the kit's own cell parses its own spelling — so this is not a failure and
    /// must not cost the instance its layout. But nothing else in circuitRF can read it: the same row goes
    /// to the simulator as an expression and fails there, a long way from here. Saying it at the point the
    /// value is used is the difference between a fixable row and a mystery at Run.</para>
    /// </summary>
    internal static string TextForDeclaredString(
        EditableParameter p, Scope scope, Evaluator evaluator, out string? note)
    {
        note = null;
        if (TryResolveSiValue(p.Expression, p.Unit, scope, evaluator, out double v, out _))
            return v.ToString("R", CultureInfo.InvariantCulture);

        string raw = p.Expression.Trim();
        if (LooksLikeASuffixedNumber(raw))
            note = $"\"{raw}\" was passed to the kit's cell as written — circuitRF cannot evaluate it, " +
                   "because the letters after the number are not a unit it knows. The artwork is correct; " +
                   "the same value will fail when this design is simulated. Enter it with a unit circuitRF " +
                   "knows (60u, 60um), or as a number with the unit in the row's unit field.";
        return raw;
    }

    /// <summary>A number with a suffix stuck to it that the expression engine did not read — a
    /// SPICE-dialect spelling such as <c>60meg</c>. Deliberately shape-based rather than a
    /// list of suffixes: the point is to tell a MISTYPED DIMENSION apart from a word-valued parameter
    /// ("Selected", a model name), not to decode the suffix.</summary>
    private static bool LooksLikeASuffixedNumber(string text)
    {
        if (text.Length < 2 || !char.IsAsciiDigit(text[0])) return false;
        int i = 0;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.')) i++;
        return i > 0 && i < text.Length && char.IsAsciiLetter(text[i]);
    }

    /// <summary>R-L5f-3: MKlopf's alternate entry routes (Z1/Z2 ⇄ W1/W2 impedance entry, L ⇄ F3db
    /// length entry — <c>ParameterEditorViewModel</c>'s own toggle) mean an instance may carry a
    /// parameter that is not the CURRENTLY active route for its pair — resolving it is meaningless and
    /// fails the moment it is blank or stale. Active route is read the same way the toggle UI reads it
    /// (presence of the OTHER route's own name), so this can never disagree with what the user sees.</summary>
    private static bool IsInactiveMklopfEntryParam(EditableComponent comp, string paramName)
    {
        if (comp.Symbol != SymbolKind.Mklopf) return false;

        bool usesWidthEntry = comp.Parameters.Any(p => p.Name == "W1");
        if (paramName is "Z1" or "Z2") return usesWidthEntry;
        if (paramName is "W1" or "W2") return !usesWidthEntry;

        bool usesF3dbEntry = comp.Parameters.Any(p => p.Name == "F3db");
        if (paramName == "L") return usesF3dbEntry;
        if (paramName == "F3db") return !usesF3dbEntry;

        return false;
    }

    /// <summary>Converts whichever alternate entry route is present (W1/W2 → Z1/Z2, F3db → L) into
    /// <c>MKlopfPCell.Generate</c>'s own canonical parameter set, in place — the same conversion
    /// (<see cref="MicrostripKlopfEntryConversion"/>) and the same substrate fallback constants
    /// <c>MKlopfPCell.Generate</c> itself uses when no technology resolves (1.6 mm / 35 µm / 4.4), so
    /// this can never compute a different answer than the generator it is feeding.</summary>
    private static void ResolveMklopfCanonicalParams(
        Dictionary<string, PCellValue> resolved, Technology? technology, PCellLayerSelection layerSelection)
    {
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
        double h = substrate?.HeightMeters ?? 1.6e-3;
        double t = substrate?.ThicknessMeters ?? 35e-6;
        double er = substrate?.RelativePermittivity ?? 4.4;
        var quiet = new MicrostripValidityReporter("(MKLOPF entry-route resolution, not reported)");

        if (!resolved.ContainsKey("Z1") && resolved.ContainsKey("W1") && resolved.ContainsKey("W2"))
        {
            var (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(
                resolved.Real("W1"), resolved.Real("W2"), h, t, er, quiet);
            resolved["Z1"] = z1;
            resolved["Z2"] = z2;
            resolved.Remove("W1");
            resolved.Remove("W2");
        }

        if (!resolved.ContainsKey("L") && resolved.ContainsKey("F3db"))
        {
            double gammaMax = resolved.Real("GammaMax", 0.05);
            double z1 = resolved.Real("Z1", 50.0);
            double z2 = resolved.Real("Z2", 100.0);
            resolved["L"] = MicrostripKlopfEntryConversion.F3dbToLength(
                z1, z2, gammaMax, resolved.Real("F3db"), h, t, er, quiet);
            resolved.Remove("F3db");
        }
    }

    internal static bool NearlyEqual(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>
    /// "Did this parameter change" for the R-L5-9/10/11 overwrite classification, across kinds.
    ///
    /// <para>Anything that IS a number compares as one, with <see cref="NearlyEqual"/>'s tolerance — a
    /// value that has been through a unit conversion and back is not bit-identical and must not read
    /// as an edit. <b>A number spelled as text counts</b>, because a vendor cell library declares its
    /// dimensions as text (see <see cref="TextForDeclaredString"/>) and the schematic side can only
    /// ever produce a Real: without this, every parameter of every kit part reads as changed on every
    /// push, in both directions, forever.</para>
    ///
    /// <para>A value that is not a number compares exactly: there is no rounding to absorb in a model
    /// name or a display mode, and a tolerance there would only ever hide a real difference.</para>
    /// </summary>
    internal static bool SameParamValue(PCellValue a, PCellValue b)
    {
        if (a.Kind == b.Kind && a.Equals(b)) return true;
        return TryAsNumber(a, out double x) && TryAsNumber(b, out double y) && NearlyEqual(x, y);
    }

    /// <summary>A parameter value as a number, whether it was sent as one or spelled as one.
    /// False for text that is not a number, which is what a model name or a display mode is.</summary>
    internal static bool TryAsNumber(PCellValue v, out double number)
    {
        if (v.Kind != PCellValueKind.String) { number = v.AsReal(); return true; }
        return double.TryParse(v.AsText(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>A parameter value as a change report shows it: a number through the same unit
    /// conversion the schematic edits it in — INCLUDING one spelled as text, which is how a vendor
    /// cell states a dimension — and anything else as its own text (a model name is not a number and
    /// must not be formatted as one).</summary>
    internal static string FormatParamValue(string? unit, PCellValue v)
        => TryAsNumber(v, out double n) ? Fmt(ToDisplayValue(unit, n)) : v.AsText();

    internal static string Fmt(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Inverse of the SI conversion <see cref="TryResolveSiValue"/> applies: a length unit's
    /// stored SI (metres) value divides back by that unit's scale so a change report reads in the same
    /// unit the parameter is actually edited in (mm, not raw metres); "deg" (already literal degrees)
    /// and any resistance/dimensionless unit (scale 1.0) pass straight through. Shared by both
    /// directions' change reports and by <see cref="LayoutToSchematicGenerator"/>'s push-back
    /// formatting, so the two can never disagree about how a value is displayed.</summary>
    internal static double ToDisplayValue(string? unit, double siValue)
    {
        // NORMALIZED FIRST, exactly as TryResolveSiValue normalizes on the way in — this is supposed
        // to be that function's inverse, and it was not. The editor stores a GLYPH ("µm", "Ω") while
        // Units.Scale is ASCII-only ("um", "Ohm"), so an unnormalized glyph found no scale, fell
        // through, and returned raw metres. Silent, and worst exactly where it matters most: "µm" is
        // the length unit an MMIC technology hands a freshly-created row, so pushing a layout back to
        // a schematic on a die wrote metres into a micron field — a factor of a million, from the one
        // command whose purpose is to keep the two views agreeing. R-L5f-1/2's trap, on the way out.
        string normalized = UnitNormalizer.ToEngineUnit(unit);
        if (normalized.Length > 0 && !string.Equals(normalized, "deg", StringComparison.Ordinal))
        {
            double? scale = Units.Scale(normalized);
            if (scale is > 0) return siValue / scale.Value;
        }
        return siValue;
    }

    private static string? NonEmptyOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string ToRelative(string baseDir, string targetAbsDir)
    {
        try { return RefPath.ToStored(Path.GetRelativePath(baseDir, targetAbsDir)); }
        catch { return targetAbsDir; }
    }
}
