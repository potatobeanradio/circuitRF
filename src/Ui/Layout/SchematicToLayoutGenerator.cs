using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// docs/design/layout-view.md §9 + docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2. Walks a
/// schematic's own component instances (via <see cref="NetExtractor"/> — never re-traversed
/// independently, per §9 step 1) and computes the <see cref="LayoutInstance"/> adds/updates a target
/// <see cref="LayoutView"/> needs to match it, PLUS a human-readable change report (R-L5-5/§2.2).
///
/// Framework-free (no Avalonia/Skia) and side-effect-free on the SCHEMATIC side — it reads
/// <paramref name="model"/> but never mutates it. It DOES create generated PCell cell folders on disk
/// via <see cref="GeneratedCellStore"/> (an unavoidable side effect of "creates or reuses a generated
/// cell," R-L5-1) and it DOES mutate <c>target.SchematicPCellSnapshots</c> directly (the R-L5-11
/// bookkeeping side-table — not itself undoable, matching the R-L5-13/14 report's convention that
/// what is reported is what happened). Everything else — every <see cref="LayoutInstance"/> add or
/// update — is returned as a single <see cref="IUiCommand"/> for the CALLER to execute through the
/// document's own undo stack (R-L5-12: "the whole re-run is one undoable action"), never executed
/// here directly.
/// </summary>
public static class SchematicToLayoutGenerator
{
    public enum ReportSeverity { Info, Warning }

    public sealed record ReportLine(string InstanceName, string Text, ReportSeverity Severity);

    /// <summary>
    /// <paramref name="Command"/> is null when nothing changed at all (R-L5-14: "say nothing when
    /// nothing changed" — the caller posts no Messages entry in that case). <paramref name="Lines"/>
    /// is per-instance, in schematic-component order; the caller applies R-L5-13's cap and trailing
    /// summary. <paramref name="NoLayoutWarnings"/> lists components with no resolvable layout view
    /// (§9 step 2), reported unconditionally (not counted against the cap — there is no realistic
    /// schematic with hundreds of un-laid-out component TYPES, only hundreds of instances).
    /// </summary>
    public sealed record GenerationResult(
        IUiCommand? Command,
        IReadOnlyList<ReportLine> Lines,
        int AddedCount,
        int UpdatedCount,
        int UnchangedCount,
        int RemovedCount,
        int OverwrittenParameterCount,
        IReadOnlyList<string> NoLayoutWarnings)
    {
        public bool NothingChanged => Command is null && NoLayoutWarnings.Count == 0;
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
        };

    private const int GridCols = 8;
    // 10 mm at the app-wide fixed 1 DBU = 1 nm resolution (LayoutUnits.DefaultDbuPerMicron) — crude
    // and non-overlapping for realistic microstrip parts, deliberately not tuned further (§9 step 3:
    // "no auto-routing or auto-placement quality... say so rather than pretending otherwise").
    private const long GridPitchDbu = 10_000_000;

    /// <summary>
    /// brief-L5-followups-3.md §2 (R-L5h-3): FORMERLY the reserved (Layer, Datatype) key a §9 step 4
    /// ratsnest pass emitted real, persisted <c>PathShape</c> geometry onto — a connectivity GUIDE
    /// treated as real artwork, the identical error already fixed once for pins (R-L5g-13/14). This
    /// generator no longer emits anything on this layer at all; the constant is kept SOLELY as the
    /// identity <see cref="RemoveRatsnestShapes"/> sweeps — no starter technology in this app uses
    /// layer 0 (both ship starting at 1, docs/design/layout-view.md §2.4), so sweeping it is safe:
    /// nothing legitimate is ever there.
    /// </summary>
    internal static readonly LayerKey RatsnestLayer = new(0, 900);

    /// <summary>
    /// brief-L5-followups-3.md §2 (R-L5h-4): strips any already-persisted ratsnest shapes from
    /// <paramref name="view"/> — this generator no longer emits them (R-L5h-3), so any that remain
    /// came from a <c>.clay</c> written before this fix. Returns the count removed (0 = nothing to
    /// clean). Pure/framework-free; the caller (<c>WorkspaceViewModel.GetOrCreateLayoutSession</c>,
    /// the ONE funnel every layout load — open-as-tab and push-in alike — goes through) decides what
    /// to do with a non-zero count (mark the session dirty so the cleanup actually persists on the
    /// next save, and report it).
    /// </summary>
    public static int RemoveRatsnestShapes(LayoutView view) => view.Shapes.RemoveAll(s => s.Layer == RatsnestLayer);

    public static GenerationResult Run(
        SchematicEditModel model,
        LayoutView target,
        string schematicDir,
        string workspaceRootDir,
        string targetLayoutBaseDir,
        Technology? technology,
        string? techIdentity,
        ICellResolver? cellResolver)
    {
        var extraction = NetExtractor.Extract(model, "tb", cellResolver);

        var scope = new Scope("global");
        foreach (var v in extraction.TestBench.GlobalVariables)
            scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();

        var physical = model.Components.Where(IsPhysical).ToList();

        var existingBySchematicId = new Dictionary<string, (int Index, LayoutInstance Instance)>(StringComparer.Ordinal);
        for (int i = 0; i < target.Instances.Count; i++)
            if (target.Instances[i].SchematicId is { Length: > 0 } sid && !existingBySchematicId.ContainsKey(sid))
                existingBySchematicId[sid] = (i, target.Instances[i]);

        var seenSchematicIds = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<ReportLine>();
        var noLayoutWarnings = new List<string>();
        IUiCommand? chain = null;
        int added = 0, updated = 0, unchanged = 0, overwritten = 0;

        for (int slot = 0; slot < physical.Count; slot++)
        {
            var comp = physical[slot];
            string schematicId = comp.InstanceName;
            if (string.IsNullOrEmpty(schematicId)) continue; // can't track an unnamed instance idempotently
            seenSchematicIds.Add(schematicId);

            string? resolvedCellRef = ResolveComponentLayout(
                comp, model, schematicDir, workspaceRootDir, targetLayoutBaseDir, target, technology, techIdentity,
                scope, evaluator, out var pcellParams, out var generatorId, out var resolveWarning, out var pcellDiagnostics);

            if (resolvedCellRef is null)
            {
                string label = ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount);
                string reason = resolveWarning ?? "no layout view";
                noLayoutWarnings.Add($"{comp.InstanceName} ({label}): {reason} — skipped.");
                continue;
            }

            // brief-L5-followups-2.md §2.2: a PCell generator's own diagnostics (e.g. R-klp-10's
            // curvature warning) had nowhere to surface before this fix — report them here, alongside
            // this instance's own add/update line, rather than dropping them silently.
            if (pcellDiagnostics is { Count: > 0 })
                foreach (var d in pcellDiagnostics)
                    lines.Add(new ReportLine(schematicId, $"{schematicId} — {d}", ReportSeverity.Warning));

            bool hasExisting = existingBySchematicId.TryGetValue(schematicId, out var existing);

            if (!hasExisting)
            {
                long x = (slot % GridCols) * GridPitchDbu;
                long y = (slot / GridCols) * GridPitchDbu;
                var inst = new LayoutInstance { CellRef = resolvedCellRef, X = x, Y = y, Mag = 1.0, SchematicId = schematicId };
                chain = Chain(chain, new AddInstanceCommand(target, inst));
                added++;
                lines.Add(new ReportLine(schematicId, $"{schematicId} — added", ReportSeverity.Info));

                if (generatorId is not null && pcellParams is not null)
                    target.SchematicPCellSnapshots[schematicId] = new Dictionary<string, PCellValue>(pcellParams);

                continue;
            }

            var before = existing.Instance;
            bool cellRefChanged = !string.Equals(before.CellRef, resolvedCellRef, StringComparison.OrdinalIgnoreCase);

            // R-L5-9/10/11: per-parameter overwrite classification — PCell instances only.
            if (generatorId is not null && pcellParams is not null)
            {
                target.SchematicPCellSnapshots.TryGetValue(schematicId, out var snapshot);
                var currentLayoutParams =
                    CellLayoutResolver.Resolve(before.CellRef, targetLayoutBaseDir) is
                        { State: CellLayoutState.Resolved, View.PCellOrigin: { } curOrigin }
                        ? curOrigin.Parameters
                        : null;

                bool reportedThisInstance = false;
                foreach (var (name, newVal) in pcellParams)
                {
                    if (currentLayoutParams is null || !currentLayoutParams.TryGetValue(name, out var layoutVal))
                        continue; // nothing on the existing cell to compare against (shouldn't happen once created, but never throw over it)

                    if (SameParamValue(layoutVal, newVal))
                        continue; // schematic and layout already agree — nothing changed for this parameter

                    bool hadSnapshot    = snapshot is not null && snapshot.ContainsKey(name);
                    PCellValue snap     = hadSnapshot ? snapshot![name] : layoutVal;
                    bool schematicMoved = !hadSnapshot || !SameParamValue(snap, newVal);
                    bool layoutMoved    = hadSnapshot && !SameParamValue(snap, layoutVal);

                    bool isWarning = layoutMoved; // R-L5-11 table: layout-diverged (alone or together with schematic) => warning
                    if (!schematicMoved && !layoutMoved) continue; // shouldn't happen given the NearlyEqual guard above, but stay honest

                    string? unit = comp.Parameters.FirstOrDefault(p => p.Name == name)?.Unit;
                    string unitSuffix = string.IsNullOrEmpty(unit) ? "" : $" {unit}";
                    lines.Add(new ReportLine(schematicId,
                        $"{schematicId} — {name} changed from {FormatParamValue(unit, layoutVal)}{unitSuffix} to {FormatParamValue(unit, newVal)}{unitSuffix}" +
                        (isWarning ? " (a layout edit is being overwritten)" : " (from schematic)"),
                        isWarning ? ReportSeverity.Warning : ReportSeverity.Info));
                    reportedThisInstance = true;
                    if (isWarning) overwritten++;
                }

                target.SchematicPCellSnapshots[schematicId] = new Dictionary<string, PCellValue>(pcellParams);

                if (cellRefChanged)
                {
                    var after = LayoutGeometry.Clone(before);
                    after.CellRef = resolvedCellRef;
                    after.SchematicId = schematicId;
                    chain = Chain(chain, new ReplaceInstanceCommand(target, existing.Index, before, after));
                    updated++;
                    if (!reportedThisInstance)
                        lines.Add(new ReportLine(schematicId, $"{schematicId} — updated", ReportSeverity.Info));
                }
                else
                {
                    unchanged++;
                }
                continue;
            }

            // Non-PCell (hierarchical cell-ref, or an unresolved reference that happens to still match) —
            // plain CellRef tracking, no parameter concept to overwrite.
            if (cellRefChanged)
            {
                var after = LayoutGeometry.Clone(before);
                after.CellRef = resolvedCellRef;
                after.SchematicId = schematicId;
                chain = Chain(chain, new ReplaceInstanceCommand(target, existing.Index, before, after));
                updated++;
                lines.Add(new ReportLine(schematicId, $"{schematicId} — updated", ReportSeverity.Info));
            }
            else
            {
                unchanged++;
            }
        }

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

        return new GenerationResult(chain, lines, added, updated, unchanged, removed, overwritten, noLayoutWarnings);
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
        string workspaceRootDir, string targetLayoutBaseDir, LayoutView target,
        Technology? technology, string? techIdentity,
        Scope scope, Evaluator evaluator,
        out IReadOnlyDictionary<string, PCellValue>? pcellParams, out string? generatorId,
        out string? resolveWarning, out IReadOnlyList<string>? pcellDiagnostics)
    {
        pcellParams = null;
        generatorId = null;
        resolveWarning = null;
        pcellDiagnostics = null;

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
            // See KitPaletteMerge for the rules and KitLayoutGenerators for why it is published.
            string kitGenerator = KitLayoutGenerators.For(workspaceRootDir, kitName, partId) ?? partId;

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

        string cellDir;
        try
        {
            cellDir = GeneratedCellStore.GetOrCreate(
                workspaceRootDir, reference, resolved, technology, techIdentity, layerSelection, out pcellDiagnostics);
            GeneratedCellStore.RecordSnapshot(target, cellDir, reference, resolved, techIdentity, layerSelection);
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

    /// <summary>Ground/Pin/Var/Meas (and Open/Short-disabled components) are never physical — mirrors
    /// <c>NetExtractor.ExtractModel</c>'s own instance-emission skip set exactly, so "reported as
    /// missing a layout view" never fires for the schematic's own meta-components (a VAR row or a
    /// Ground symbol has no layout existence to report as missing).</summary>
    private static bool IsPhysical(EditableComponent comp) =>
        comp.Disable is not (DisableState.Open or DisableState.Short)
        && comp.Symbol is not (SymbolKind.Ground or SymbolKind.Pin or SymbolKind.Var or SymbolKind.Meas
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
    /// loud.</b> A kit spells its values the way its own simulator does — <c>60u</c>, <c>1.5p</c> —
    /// and circuitRF's expression engine does not read engineering suffixes: a value's unit is a
    /// FIELD on the row, not a letter on the number (measured: <c>60u</c> is
    /// <c>Parse error at position 2</c>, while <c>60</c> with the unit µm resolves). Such a row still
    /// reaches the cell verbatim and the artwork still comes out right, because the kit's own cell
    /// parses its own spelling — so this is not a failure and must not cost the instance its layout.
    /// But nothing else in circuitRF can read it: the same row goes to the simulator as an expression
    /// and fails there, a long way from here, with a message about a token. Saying it at the point the
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
                   "because a unit belongs in the row's own unit field rather than as a letter after " +
                   "the number. The artwork is correct; the same value will fail when this design is " +
                   "simulated. Enter it as a number with a unit instead.";
        return raw;
    }

    /// <summary>A number with an engineering suffix stuck to it — the spelling a SPICE-dialect kit
    /// uses and circuitRF's expression engine does not read. Deliberately shape-based rather than a
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
        try { return Path.GetRelativePath(baseDir, targetAbsDir); }
        catch { return targetAbsDir; }
    }

    private static IUiCommand Chain(IUiCommand? existing, IUiCommand next)
        => existing is null ? next : new CompositeCommand(existing, next);
}
