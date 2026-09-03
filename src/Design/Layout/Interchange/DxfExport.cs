// DXF export orchestrator (docs/sonnet-briefs/brief-L4b-dxf-interchange.md). Mirrors GdsiiExport's own
// shape exactly (§2.3-equivalent): walks a design's cell hierarchy from a root cell folder into
// InterchangeStructures, then runs the SAME DxfWriter.Write path (a dry run into TextWriter.Null) to
// produce the pre-flight fidelity plan the export dialog shows BEFORE any bytes are written.
//
// Unlike GDSII, DXF coordinates are plain doubles with no 32-bit integer ceiling — there is no
// coordinate-overflow block here; ExportPlan.CanWrite exists anyway, always true, purely so the UI
// dialog shape stays identical across both formats (never a special-cased "DXF has no CanWrite" path).

using CircuitRF.WBond;

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Interchange;

public static class DxfExport
{
    /// <summary>What the export dialog shows before writing: curve/hole/bitmap counts, the
    /// block-name mapping, and any instance whose <c>CellRef</c> does not resolve within this design
    /// (it still exports — as a dangling reference to a BLOCK name absent from the file — but never
    /// silently).</summary>
    public sealed record ExportPlan(
        IReadOnlyList<string> UnresolvedInstanceReferences,
        IReadOnlyDictionary<string, string> BlockNameByCellName,
        IReadOnlyList<InterchangeStructure> Structures,
        string RootStructureName,
        Technology? Tech,
        int DbuPerMicron,
        /// <summary>docs/design/layout-view.md §9B.10 — the ROOT cell's ruler annotations. Root-only:
        /// rulers are cell-local (§9B.7) and do not render through an instance placement, so a
        /// sub-cell's working notes are not scattered across every design that reuses it.</summary>
        IReadOnlyList<RulerAnnotation>? Rulers = null,
        LayoutUnit DisplayUnit = LayoutUnit.Um)
    {
        public bool CanWrite => true;

        /// <summary>R-via-10: any via at all means this export is geometry-only, never a manufacturable
        /// PCB deliverable (DXF carries no drill table) — mirrors <c>GdsiiExport.ExportPlan.HasVias</c>.</summary>
        public bool HasVias => Structures.Any(s => s.Shapes.Any(sh => sh is ViaShape));
    }

    /// <summary>Walks <paramref name="rootCellDir"/>'s hierarchy — no bytes written yet. Identical
    /// hierarchy-collection shape to <c>GdsiiExport.Analyze</c> (same BFS over <c>CellFolder.
    /// ResolvePrimary</c> + <c>LayoutInstance.CellRef</c>), duplicated here rather than shared because
    /// the brief's own guardrail forbids widening GDSII's file beyond wiring, and the two walks now
    /// produce format-specific <see cref="InterchangeStructure"/> naming (DXF block names, not GDSII
    /// structure names).</summary>
    /// <summary><paramref name="rootView"/> (brief-layout-testing-fixes.md item 5/R-fix-4): when the
    /// root cell is open in the editor, pass its live, possibly-unsaved <c>LayoutView</c> here so the
    /// export reflects what is on screen rather than the last save — mirrors <c>GdsiiExport.Analyze</c>'s
    /// own parameter exactly. Null (the project-tree/no-open-document path) reads from disk as before.</summary>
    public static ExportPlan Analyze(string rootCellDir, Technology? tech, int dbuPerMicron, LayoutView? rootView = null)
    {
        var (structures, nameByCellName, unresolvedRefs, rootName, rootRulers, displayUnit) =
            CollectHierarchy(rootCellDir, rootView);
        return new ExportPlan(unresolvedRefs, nameByCellName, structures, rootName, tech, dbuPerMicron,
                              rootRulers, displayUnit);
    }

    /// <summary>
    /// Dry-runs the write into <see cref="TextWriter.Null"/> — the SAME code path the real export
    /// takes, so the fidelity dialog can never disagree with what actually gets written.
    /// </summary>
    /// <param name="wires">
    /// Bond wires to include (wbond.md §9.4). Null — the Layout Editor's own case — writes exactly
    /// what it always did.
    /// </param>
    public static DxfExportSummary Preview(ExportPlan plan, DxfExportOptions options, WBondDesign? wires = null) =>
        DxfWriter.Write(TextWriter.Null, plan.Structures, plan.RootStructureName, plan.Tech, plan.DbuPerMicron,
                        options, wires, plan.Rulers, plan.DisplayUnit);

    /// <inheritdoc cref="Preview(ExportPlan, DxfExportOptions, WBondDesign?)"/>
    public static DxfExportSummary Write(string filePath, ExportPlan plan, DxfExportOptions options, WBondDesign? wires = null)
    {
        using var stream = new StreamWriter(filePath, append: false);
        return DxfWriter.Write(stream, plan.Structures, plan.RootStructureName, plan.Tech, plan.DbuPerMicron,
                               options, wires, plan.Rulers, plan.DisplayUnit);
    }

    private static (List<InterchangeStructure> Structures, IReadOnlyDictionary<string, string> NameByCellName,
        IReadOnlyList<string> UnresolvedRefs, string RootName,
        IReadOnlyList<RulerAnnotation> RootRulers, LayoutUnit DisplayUnit) CollectHierarchy(string rootCellDir, LayoutView? rootView)
    {
        var rootAbs = Path.GetFullPath(rootCellDir);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootAbs };
        var order = new List<string> { rootAbs };
        var viewByDir = new Dictionary<string, LayoutView>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootAbs);

        while (queue.Count > 0)
        {
            var cellDir = queue.Dequeue();
            // item 5/R-fix-4: mirrors GdsiiExport.CollectHierarchy's own root-view substitution exactly.
            var view = string.Equals(cellDir, rootAbs, StringComparison.OrdinalIgnoreCase) && rootView is not null
                ? rootView
                : LoadPrimaryLayout(cellDir);
            viewByDir[cellDir] = view;

            var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            foreach (var inst in view.Instances)
            {
                string targetDir;
                try { targetDir = Path.GetFullPath(Path.Combine(layoutDir, inst.CellRef)); }
                catch { continue; }
                if (!Directory.Exists(targetDir)) continue;
                if (visited.Add(targetDir))
                {
                    order.Add(targetDir);
                    queue.Enqueue(targetDir);
                }
            }
        }

        var cellNames = order.Select(d => Path.GetFileName(d)).ToList();
        var blockNameByCellName = DxfNaming.MangleForExport(cellNames);

        var dirToBlockName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in order)
            dirToBlockName[dir] = blockNameByCellName[Path.GetFileName(dir)];

        var unresolvedRefs = new List<string>();
        var structures = new List<InterchangeStructure>(order.Count);
        foreach (var dir in order)
        {
            var view = viewByDir[dir];
            var cellName = Path.GetFileName(dir);
            var layoutDir = CellFolder.SubFolderPath(dir, ViewType.Layout);
            var instances = view.Instances.Select(inst =>
            {
                string targetDir;
                try { targetDir = Path.GetFullPath(Path.Combine(layoutDir, inst.CellRef)); }
                catch { targetDir = ""; }
                if (!dirToBlockName.TryGetValue(targetDir, out var targetBlockName))
                {
                    targetBlockName = inst.CellRef;
                    unresolvedRefs.Add($"{cellName}: instance referencing \"{inst.CellRef}\" does not resolve.");
                }
                return new LayoutInstance
                {
                    CellRef = targetBlockName,
                    X = inst.X, Y = inst.Y, RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = inst.Mag,
                    Rows = inst.Rows, Cols = inst.Cols, PitchX = inst.PitchX, PitchY = inst.PitchY,
                };
            }).ToList();

            structures.Add(new InterchangeStructure(dirToBlockName[dir], [.. view.Shapes], instances));
        }

        // §9B.7: cell-LOCAL. Only the root's own rulers travel; a sub-cell's stay with that cell.
        var rootLayout = viewByDir[rootAbs];
        return (structures, blockNameByCellName, unresolvedRefs, dirToBlockName[rootAbs],
                [.. rootLayout.Rulers], rootLayout.DisplayUnit);
    }

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        var res = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || res.ResolvedName is null)
            return new LayoutView();

        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        try
        {
            return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, res.ResolvedName));
        }
        catch
        {
            return new LayoutView();
        }
    }
}
