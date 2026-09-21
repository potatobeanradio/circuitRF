// Whole-design flatten — the elaborated, flat geometry a consumer that cannot express hierarchy needs.
// Two consumers today: Gerber export (which has no hierarchy at all) and DRC (docs/design/layout-view.md
// §9A.1: "v1 runs flat on the elaborated geometry, with a cell-count guard, and says so"). Originally
// written for the Gerber writer alone (brief-L4c-gerber-export.md §4, R-L4c-6) and generalised in place
// when DRC became the second caller — a format-agnostic flatten does not belong under Interchange, and a
// DRC stack trace naming a Gerber type would be a category error.
//
// Reuses L3c's existing machinery (LayoutFlatten.FlattenAllLevels, its affine coordinate walk, R-L3c-2)
// rather than writing a second flattener: this file is only the DRIVING loop that applies FlattenAllLevels
// to every one of the root design's own instances (L3c's own VM entry point applies it to a single
// user-selected instance; a whole-design consumer needs all of them). Cross-technology reconciliation
// (R-L3c-3) reuses LayoutLayerMapping/LayoutFragment.ApplyReconciliation exactly as L3c's own
// LayoutEditorViewModel.Flatten.cs does, with the SAME stated scope narrowing L3c itself uses:
// checked only against each TOP-LEVEL instance's own DIRECT sub-cell, not re-checked at every deeper
// nesting level (LayoutEditorViewModel.Flatten.cs's CommitFlattenAllLevels doc comment states this
// narrowing explicitly for the identical reason).

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

public static class LayoutDesignFlatten
{
    /// <summary>Same order-of-magnitude safety valve as R-L3c-4's own Flatten-All-Levels ceiling —
    /// reused directly (not re-derived) since a flattened WHOLE DESIGN is exactly the same class of
    /// "could balloon combinatorially" risk a single flattened instance already guards against.</summary>
    public const long HardCeiling = LayoutFlatten.FlattenAllLevelsHardCeiling;

    public sealed record FlattenResult(
        IReadOnlyList<LayoutShape> Shapes,
        int TopLevelInstancesFlattened,
        int ShapesContributedByInstances,
        IReadOnlyList<string> UnresolvedInstances,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>> PendingCrossTechMappings,
        bool ExceedsCeiling);

    /// <summary>
    /// One flattened shape and the breadcrumb <see cref="Flatten"/> throws away —
    /// <c>brief-lvs-3-layout-netlist.md</c> R-lvs3-2b.
    /// </summary>
    /// <param name="Shape">The same clone <see cref="Flatten"/> produces, in the root's frame.</param>
    /// <param name="InstancePath">The ROOT placement it came from, spelled as the report spells a
    /// device: its designator where it has one, else <c>@n</c> for its position in
    /// <c>LayoutView.Instances</c>. <b>Empty for the root's own shapes.</b>
    ///
    /// <para><b>The root's own placements and no deeper</b>, which is <see cref="PlacedPins"/>'
    /// rule (R-ab1-1a) and is all a FLAT reading can use: a land pattern nested three cells deep is
    /// that module's internal business until somebody places the module. Brief 9 is where a path
    /// gains a second segment, and it gains it by extracting each cell in its OWN frame rather than
    /// by threading a string through this walk.</para></param>
    /// <param name="SubCellPin">The sub-cell pad this shape realises — <see cref="LayoutShape.Pin"/>,
    /// carried through the flatten by <c>LayoutGeometry.Clone</c> — or null where it realises none.</param>
    public sealed record TaggedShape(LayoutShape Shape, string InstancePath, string? SubCellPin);

    /// <summary><see cref="Flatten"/>'s answer with R-lvs3-2b's breadcrumb on every shape. Every
    /// other member means exactly what it means there (R-lvs3-2c): same ceiling, same refusal, same
    /// cross-technology reconciliation, same <c>UnresolvedInstances</c>.</summary>
    public sealed record TaggedFlattenResult(
        IReadOnlyList<TaggedShape> Shapes,
        int TopLevelInstancesFlattened,
        IReadOnlyList<string> UnresolvedInstances,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>> PendingCrossTechMappings,
        bool ExceedsCeiling);

    /// <summary>
    /// Flattens <paramref name="rootView"/>'s entire instance tree into world-space shapes, in the
    /// root's own coordinate frame (the root's own <see cref="LayoutView.Shapes"/> need no transform at
    /// all). <paramref name="resolvedCrossTechMappings"/> — keyed by the resolved sub-cell's absolute
    /// cell directory — supplies the outcome of a prior confirmation round trip; any DIRECT sub-cell
    /// technology mismatch not already present there is reported via
    /// <see cref="FlattenResult.PendingCrossTechMappings"/> and that one instance's subtree is left
    /// UNFLATTENED (no shapes contributed) until the caller resolves it and calls again — mirroring how
    /// a coordinate overflow blocks <c>GdsiiExport.Write</c> rather than writing a partial result.
    /// </summary>
    public static FlattenResult Flatten(
        LayoutView rootView, string rootCellDir, Technology? rootTech,
        Func<string?, string, TechResolution>? resolveTechAt,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>>? resolvedCrossTechMappings)
        => FlattenCore(rootView, rootCellDir, rootTech, resolveTechAt, resolvedCrossTechMappings, tags: null);

    /// <summary>
    /// <see cref="Flatten"/>'s shapes with, per shape, the root placement it came from and the
    /// sub-cell pin it realises — R-lvs3-2b.
    /// </summary>
    /// <remarks>
    /// <b>It is a driving loop that keeps a breadcrumb, not a second flattener.</b> Both forms are
    /// the SAME function body, walking the same instances in the same order through the same
    /// <see cref="LayoutFlatten.FlattenAllLevels"/> and the same coordinate walk; what differs is
    /// one list. <see cref="Flatten"/>'s output is therefore unchanged shape for shape and byte for
    /// byte (R-lvs3-2a), which two shipped consumers — Gerber export and the DRC — depend on, and
    /// it pays nothing for this: with no tag list to fill there is no allocation and no branch
    /// taken per shape.
    /// </remarks>
    public static TaggedFlattenResult FlattenTagged(
        LayoutView rootView, string rootCellDir, Technology? rootTech,
        Func<string?, string, TechResolution>? resolveTechAt,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>>? resolvedCrossTechMappings)
    {
        var tags = new List<string>();
        var flat = FlattenCore(rootView, rootCellDir, rootTech, resolveTechAt, resolvedCrossTechMappings, tags);

        var tagged = new List<TaggedShape>(flat.Shapes.Count);
        for (int i = 0; i < flat.Shapes.Count; i++)
            tagged.Add(new TaggedShape(flat.Shapes[i], tags[i], flat.Shapes[i].Pin));

        return new TaggedFlattenResult(
            tagged, flat.TopLevelInstancesFlattened, flat.UnresolvedInstances,
            flat.PendingCrossTechMappings, flat.ExceedsCeiling);
    }

    /// <summary>How a root placement is spelled in a <see cref="TaggedShape.InstancePath"/> and in
    /// an <c>LvsDevice.Path</c> — its designator, else its position. <b>One rule, called from both
    /// sides</b>: a device the flatten calls <c>@3</c> and the netlist calls something else is a
    /// device nobody can cross-reference.</summary>
    public static string PathOf(LayoutInstance inst, int index)
        => inst.DisplayRefDes is { Length: > 0 } refdes ? refdes : "@" + index;

    private static FlattenResult FlattenCore(
        LayoutView rootView, string rootCellDir, Technology? rootTech,
        Func<string?, string, TechResolution>? resolveTechAt,
        IReadOnlyDictionary<string, IReadOnlyList<LayerMappingRow>>? resolvedCrossTechMappings,
        List<string>? tags)
    {
        var shapes = new List<LayoutShape>(rootView.Shapes.Count);
        foreach (var s in rootView.Shapes) { shapes.Add(LayoutGeometry.Clone(s)); tags?.Add(""); }

        string rootLayoutDir = CellFolder.SubFolderPath(rootCellDir, ViewType.Layout);
        var unresolved = new List<string>();
        var pending = new Dictionary<string, IReadOnlyList<LayerMappingRow>>();
        int flattenedCount = 0;

        if (ExceedsCeiling(rootView, rootLayoutDir))
            return new FlattenResult([], 0, 0, [], pending, ExceedsCeiling: true);

        for (int instIndex = 0; instIndex < rootView.Instances.Count; instIndex++)
        {
            var inst = rootView.Instances[instIndex];
            var res = CellLayoutResolver.Resolve(inst.CellRef, rootLayoutDir);
            if (res.State != CellLayoutState.Resolved)
            {
                unresolved.Add(UnresolvedNote(inst.CellRef));
                continue;
            }

            string subCellLayoutDir = CellHierarchy.LayoutBaseDirOf(res.ResolvedCellDir!);
            var subTech = resolveTechAt?.Invoke(res.View!.TechRef, subCellLayoutDir).Tech;

            IReadOnlyList<LayerMappingRow>? resolvedRows = null;
            if (subTech is not null)
            {
                var proposed = LayoutLayerMapping.Propose(res.View!.Shapes, subTech.Layers, rootTech);
                if (LayoutLayerMapping.RequiresConfirmation(proposed))
                {
                    if (resolvedCrossTechMappings is null ||
                        !resolvedCrossTechMappings.TryGetValue(res.ResolvedCellDir!, out resolvedRows))
                    {
                        pending[res.ResolvedCellDir!] = proposed;
                        continue; // this subtree is left unflattened until the mapping is resolved
                    }
                }
                else
                {
                    // No confirmation needed (same technology, or every row a confident name match) —
                    // still APPLY it: SameKeySameName/ExactName rows rewrite the shape's LayerKey onto
                    // the root technology's own numbering, which matters whenever the two technologies
                    // number the same-named layer differently.
                    resolvedRows = proposed.Count > 0 ? proposed : null;
                }
            }

            var allLevels = LayoutFlatten.FlattenAllLevels(inst, rootLayoutDir);
            var reconciled = ApplyCrossTechMapping(subTech, allLevels.Shapes, resolvedRows);
            shapes.AddRange(reconciled);
            if (tags is not null)
            {
                string path = PathOf(inst, instIndex);
                for (int i = 0; i < reconciled.Count; i++) tags.Add(path);
            }

            foreach (var surviving in allLevels.SurvivingInstances)
                unresolved.Add($"Instance referencing \"{surviving.CellRef}\" (nested under \"{inst.CellRef}\") could not be resolved — skipped, no geometry contributed for it.");

            flattenedCount++;
        }

        // ── THE REFERENCE DESIGNATORS ───────────────────────────────────────────────────────────
        // brief-footprint-4b R-fp4b-4b. Emitted HERE, from the ROOT's own placements, because the
        // designator belongs to the placement and not to the cell — which is what makes Gerber, DRC
        // and `circuitrf check` all see it at once, out of one function, rather than out of a second
        // copy per consumer. It is ordinary artwork on the technology's silkscreen role: not chrome,
        // not an overlay, and never relocated when the technology declares no silk (R-fp4b-4c).
        // Silk is not a conductor, so the EM path is indifferent to it by layer, exactly as it
        // already is to the land pattern's own body outline.
        var labels = Footprints.FootprintLabel.ShapesFor(
            rootView, rootLayoutDir, rootTech,
            inst => CellLayoutResolver.Resolve(inst.CellRef, rootLayoutDir).View);
        shapes.AddRange(labels);
        // Untagged on purpose. Silk is not a conductor, so the electrical reading never sees one;
        // tagging it with the placement it names would put a device's path on artwork that carries
        // no current, which is exactly the confusion a breadcrumb exists to prevent.
        if (tags is not null) for (int i = 0; i < labels.Count; i++) tags.Add("");

        int contributed = shapes.Count - rootView.Shapes.Count;
        return new FlattenResult(shapes, flattenedCount, contributed, unresolved, pending, ExceedsCeiling: false);
    }

    /// <summary>
    /// Whether flattening <paramref name="rootView"/>'s instance tree would exceed
    /// <see cref="HardCeiling"/> — R-L3c-4's safety valve, asked WITHOUT flattening.
    ///
    /// <para><b>Extracted so a second consumer can gate on the same answer</b> (R-ab1-5d):
    /// <c>PlacedPins</c> reads a part's pads off the very geometry this ceiling stops being read,
    /// and a board whose lands were never flattened must not come back with a confident pad list over
    /// them. A second copy of the estimate loop would be a second ceiling that drifts.</para>
    /// </summary>
    public static bool ExceedsCeiling(LayoutView rootView, string rootLayoutDir)
    {
        ArgumentNullException.ThrowIfNull(rootView);

        long total = 0;
        foreach (var inst in rootView.Instances)
        {
            long estimate = LayoutFlatten.CountResultingShapes(inst, rootLayoutDir, HardCeiling - total);
            if (estimate < 0) return true;
            total += estimate;
            if (total > HardCeiling) return true;
        }

        return false;
    }

    /// <summary>The sentence an instance that does not resolve is reported with. <b>One sentence,
    /// not two</b> (R-ab1-1c): the pad projection walks the same list and skips the same instances,
    /// and a reader comparing two reports of one board should not have to tell two wordings
    /// apart.</summary>
    public static string UnresolvedNote(string cellRef) =>
        $"Instance referencing \"{cellRef}\" does not resolve — skipped, no geometry contributed for it.";

    private static IReadOnlyList<LayoutShape> ApplyCrossTechMapping(
        Technology? subTech, IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayerMappingRow>? resolvedRows)
    {
        if (resolvedRows is null || resolvedRows.Count == 0 || subTech is null || shapes.Count == 0) return shapes;
        var choices = LayoutLayerMapping.BuildChoices(resolvedRows);
        return LayoutFragment.ApplyReconciliation(shapes, subTech.Layers, choices).Shapes;
    }
}
