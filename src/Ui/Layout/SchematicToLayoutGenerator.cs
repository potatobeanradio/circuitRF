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
        IReadOnlyList<string> NoLayoutWarnings,
        Bbox AddedRegion = default,
        int DeletedCount = 0,
        bool OrientationLinksRecorded = false)
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

    /// <summary>Clear space between neighbours, as a fraction of the largest cell placed. Half a cell
    /// reads as "laid out, not touching" at any scale, which is the whole point of measuring.</summary>
    private const long GridGapNumerator = 1, GridGapDenominator = 2;

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
        var newInstances = new List<(int Slot, LayoutInstance Instance)>();
        var deleteIndices = new List<int>();
        var lines = new List<ReportLine>();
        var noLayoutWarnings = new List<string>();
        IUiCommand? chain = null;
        int added = 0, updated = 0, unchanged = 0, overwritten = 0;
        int unlinkedDiffering = 0;
        bool linksRecorded = false;

        for (int slot = 0; slot < physical.Count; slot++)
        {
            var comp = physical[slot];
            string schematicId = comp.InstanceName;
            if (string.IsNullOrEmpty(schematicId)) continue; // can't track an unnamed instance idempotently
            seenSchematicIds.Add(schematicId);

            string? resolvedCellRef = ResolveComponentLayout(
                comp, model, schematicDir, workspaceRootDir, targetLayoutBaseDir, target, technology, techIdentity,
                scope, evaluator, out var pcellParams, out var generatorId, out var resolveWarning, out var pcellDiagnostics,
                out var footprintRefusal);

            if (resolvedCellRef is null)
            {
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

            if (!hasExisting)
            {
                // Placed at the origin and moved once the whole set is known — the pitch is a function
                // of what is being placed, and that is not known until the last one is resolved. The
                // command holds this instance by reference and has not run yet, so moving it now is
                // moving it before it exists. See PlaceNewInstances.
                var inst = new LayoutInstance { CellRef = resolvedCellRef, X = 0, Y = 0, Mag = 1.0, SchematicId = schematicId };
                // Placed facing the way the symbol faces, and linked, so a rotation made on either side
                // from here on is carried across by the next run.
                var facing = SchematicLayoutOrientation.FromSchematic((int)comp.Rotation, comp.MirrorX)
                    .Compose(PinAlignment(comp, schematicDir, resolvedCellRef, targetLayoutBaseDir, technology));
                inst.MirrorX = facing.Mirror;
                inst.RotationDegrees = facing.Deg;
                inst.OrientationLink = SchematicLayoutOrientation.Link((int)comp.Rotation, comp.MirrorX, facing);
                newInstances.Add((slot, inst));
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
                    () => PinAlignment(comp, schematicDir, before.CellRef, targetLayoutBaseDir, technology));
                if (rot.Unlinked) unlinkedDiffering++;

                if (!cellRefChanged && !rot.Changes)
                {
                    // Nothing to replace, but the baseline may be new (first sync of this pair) or have
                    // advanced (both sides were already turned alike). Bookkeeping, like the snapshots.
                    if (!Equals(before.OrientationLink, rot.Link))
                    {
                        before.OrientationLink = rot.Link;
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
                chain = Chain(chain, new ReplaceInstanceCommand(target, existing.Index, before, after));
                updated++;

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

        if (unlinkedDiffering > 0)
            lines.Add(new ReportLine("", UnlinkedRotationNote(unlinkedDiffering), ReportSeverity.Info));

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

        // LAST in the chain, deliberately. Every AddInstanceCommand APPENDS and every
        // ReplaceInstanceCommand edits in place, so the indices captured above are still correct at
        // this point; a delete run earlier would shift every later index by one and the chain would
        // replace the wrong instance. DeleteInstancesCommand removes in descending order and restores
        // at the original indices on undo, so the whole re-run stays one undoable action (R-L5-12).
        if (deleteIndices.Count > 0)
            chain = Chain(chain, new DeleteInstancesCommand(target, deleteIndices));

        var addedRegion = PlaceNewInstances(newInstances, targetLayoutBaseDir);
        ReportPlacementOntoDrawnArtwork(target, newInstances, targetLayoutBaseDir, lines);

        return new GenerationResult(chain, lines, added, updated, unchanged, removed, overwritten,
                                    noLayoutWarnings, addedRegion, deleteIndices.Count,
                                    linksRecorded);
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
                                                Func<VisualOrientation> alignment)
    {
        int sDeg = (int)comp.Rotation;
        var s = SchematicLayoutOrientation.FromSchematic(sDeg, comp.MirrorX);
        var l = SchematicLayoutOrientation.FromLayout(inst);

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
        "been synced, so neither was turned. They are linked now: a rotation made on either side from " +
        "here on is carried across by Update Layout from Schematic and Update Schematic from Layout.";

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
        after.MirrorX = target.Mirror;
        after.RotationDegrees = target.Deg;
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
    /// </summary>
    private static void ReportPlacementOntoDrawnArtwork(
        LayoutView target, List<(int Slot, LayoutInstance Instance)> placed,
        string targetLayoutBaseDir, List<ReportLine> lines)
    {
        if (placed.Count == 0 || target.Shapes.Count == 0) return;

        foreach (var (_, inst) in placed)
        {
            var footprint = CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir);
            if (footprint.IsEmpty) continue;

            // Only the layers this cell actually draws on. A silkscreen outline over a copper part is
            // not the same part twice, and saying it is would be wrong rather than merely noisy.
            var layers = CellLayers(inst, targetLayoutBaseDir);

            int overlapping = 0;
            foreach (var shape in target.Shapes)
            {
                if (shape is LabelShape) continue;               // annotation, not metal
                if (layers is not null && !layers.Contains(shape.Layer)) continue;

                var bb = LayoutGeometry.BboxOf(shape);
                if (CoverEachOther(bb, footprint)) overlapping++;
            }

            if (overlapping == 0) continue;

            string what = overlapping == 1 ? "a shape" : $"{overlapping} shapes";
            lines.Add(new ReportLine(inst.SchematicId ?? "",
                $"{inst.SchematicId} — placed on top of {what} already drawn in this layout. This " +
                "command tracks the instances it places, not artwork drawn by hand, so a component " +
                "that was already drawn is now in the layout twice. Undo, or delete whichever copy " +
                "you do not want.",
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
    /// </summary>
    private static Bbox PlaceNewInstances(
        List<(int Slot, LayoutInstance Instance)> placed, string targetLayoutBaseDir)
    {
        if (placed.Count == 0) return Bbox.Empty;

        // Measured at the origin, which is where they still are: an instance's box includes its own
        // placement — the rotation Run gave it included — so the offset below is exact. The pitch
        // takes the larger of the two sides, which a quarter turn does not change.
        var extents = new List<Bbox>(placed.Count);
        long largest = 0;
        foreach (var (_, inst) in placed)
        {
            var bb = CellHierarchy.InstanceBbox(inst, targetLayoutBaseDir);
            extents.Add(bb);
            if (!bb.IsEmpty)
                largest = Math.Max(largest, Math.Max(bb.MaxX - bb.MinX, bb.MaxY - bb.MinY));
        }

        long pitch = largest > 0
            ? largest + largest * GridGapNumerator / GridGapDenominator
            : GridPitchDbu;

        var region = Bbox.Empty;
        for (int i = 0; i < placed.Count; i++)
        {
            var (slot, inst) = placed[i];
            inst.X = (slot % GridCols) * pitch;
            inst.Y = (slot / GridCols) * pitch;

            if (extents[i].IsEmpty) continue;
            region = region.Union(new Bbox(extents[i].MinX + inst.X, extents[i].MinY + inst.Y,
                                           extents[i].MaxX + inst.X, extents[i].MaxY + inst.Y));
        }
        return region;
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
                comp, footprint, schematicDir, workspaceRootDir, targetLayoutBaseDir, target,
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
        string targetLayoutBaseDir, LayoutView target, Technology? technology, string? techIdentity,
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
                GeneratedCellStore.RecordSnapshot(
                    target, generatedDir, generator, parameters, techIdentity, PCellLayerSelection.Default);
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
    private static bool IsPhysical(EditableComponent comp) =>
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
        try { return RefPath.ToStored(Path.GetRelativePath(baseDir, targetAbsDir)); }
        catch { return targetAbsDir; }
    }

    private static IUiCommand Chain(IUiCommand? existing, IUiCommand next)
        => existing is null ? next : new CompositeCommand(existing, next);
}
