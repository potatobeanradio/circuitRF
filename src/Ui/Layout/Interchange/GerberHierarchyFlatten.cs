// Whole-design flatten for Gerber export (docs/sonnet-briefs/brief-L4c-gerber-export.md §4, R-L4c-6).
// Gerber has no hierarchy at all — every instance and array must be flattened before writing. Reuses
// L3c's existing machinery (LayoutFlatten.FlattenAllLevels, its affine coordinate walk, R-L3c-2) rather
// than writing a second flattener: this file is only the DRIVING loop that applies FlattenAllLevels to
// every one of the root design's own instances (L3c's own VM entry point applies it to a single
// user-selected instance; export needs the whole design). Cross-technology reconciliation (R-L3c-3)
// reuses LayoutLayerMapping/LayoutFragment.ApplyReconciliation exactly as L3c's own
// LayoutEditorViewModel.Flatten.cs does, with the SAME stated scope narrowing L3c itself uses:
// checked only against each TOP-LEVEL instance's own DIRECT sub-cell, not re-checked at every deeper
// nesting level (LayoutEditorViewModel.Flatten.cs's CommitFlattenAllLevels doc comment states this
// narrowing explicitly for the identical reason).

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GerberHierarchyFlatten
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
    {
        var shapes = new List<LayoutShape>(rootView.Shapes.Count);
        foreach (var s in rootView.Shapes) shapes.Add(LayoutGeometry.Clone(s));

        string rootLayoutDir = CellFolder.SubFolderPath(rootCellDir, ViewType.Layout);
        var unresolved = new List<string>();
        var pending = new Dictionary<string, IReadOnlyList<LayerMappingRow>>();
        int flattenedCount = 0;
        long totalShapeEstimate = 0;
        bool exceedsCeiling = false;

        foreach (var inst in rootView.Instances)
        {
            long estimate = LayoutFlatten.CountResultingShapes(inst, rootLayoutDir, HardCeiling - totalShapeEstimate);
            if (estimate < 0) { exceedsCeiling = true; continue; }
            totalShapeEstimate += estimate;
            if (totalShapeEstimate > HardCeiling) { exceedsCeiling = true; continue; }
        }

        if (exceedsCeiling)
            return new FlattenResult([], 0, 0, [], pending, ExceedsCeiling: true);

        foreach (var inst in rootView.Instances)
        {
            var res = CellLayoutResolver.Resolve(inst.CellRef, rootLayoutDir);
            if (res.State != CellLayoutState.Resolved)
            {
                unresolved.Add($"Instance referencing \"{inst.CellRef}\" does not resolve — skipped, no geometry exported for it.");
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

            foreach (var surviving in allLevels.SurvivingInstances)
                unresolved.Add($"Instance referencing \"{surviving.CellRef}\" (nested under \"{inst.CellRef}\") could not be resolved — skipped, no geometry exported for it.");

            flattenedCount++;
        }

        int contributed = shapes.Count - rootView.Shapes.Count;
        return new FlattenResult(shapes, flattenedCount, contributed, unresolved, pending, ExceedsCeiling: false);
    }

    private static IReadOnlyList<LayoutShape> ApplyCrossTechMapping(
        Technology? subTech, IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayerMappingRow>? resolvedRows)
    {
        if (resolvedRows is null || resolvedRows.Count == 0 || subTech is null || shapes.Count == 0) return shapes;
        var choices = LayoutLayerMapping.BuildChoices(resolvedRows);
        return LayoutFragment.ApplyReconciliation(shapes, subTech.Layers, choices).Shapes;
    }
}
