// §2A.1 — correct extents describe what was ACTUALLY WRITTEN, not what the layout contains
// (R-L4b-5: bitmaps are omitted from export, so they must never widen the extents). Walks the
// in-memory structure dictionary built by DxfExport's own hierarchy collection — a different traversal
// shape than CellHierarchy's (which resolves CellRef against on-disk cell folders); here every
// LayoutInstance.CellRef is already a STRUCTURE NAME key into the same in-memory dictionary, so a
// small, local bbox-transform helper is warranted rather than forcing this through the disk-based
// CellHierarchy machinery.

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfExtents
{
    private const int MaxDepth = 64;

    public static Bbox ComputeStructureBbox(string rootName, IReadOnlyDictionary<string, InterchangeStructure> byName)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Recurse(rootName, byName, visiting, 0);
    }

    private static Bbox Recurse(string name, IReadOnlyDictionary<string, InterchangeStructure> byName, HashSet<string> visiting, int depth)
    {
        if (depth > MaxDepth || !visiting.Add(name)) return Bbox.Empty;
        if (!byName.TryGetValue(name, out var s)) { visiting.Remove(name); return Bbox.Empty; }

        var bb = Bbox.Empty;
        foreach (var shape in s.Shapes)
        {
            if (shape is BitmapShape) continue; // R-L4b-5 — never contributes
            bb = bb.Union(LayoutGeometry.BboxOf(shape));
        }

        foreach (var inst in s.Instances)
        {
            var subBbox = Recurse(inst.CellRef, byName, visiting, depth + 1);
            if (subBbox.IsEmpty) continue;
            var transformed = TransformBboxToParent(subBbox, inst);
            bb = bb.Union(ArrayExpand(transformed, inst));
        }

        visiting.Remove(name);
        return bb;
    }

    private static Bbox TransformBboxToParent(Bbox localBbox, LayoutInstance inst)
    {
        Span<(long X, long Y)> corners =
        [
            (localBbox.MinX, localBbox.MinY), (localBbox.MaxX, localBbox.MinY),
            (localBbox.MinX, localBbox.MaxY), (localBbox.MaxX, localBbox.MaxY),
        ];

        var bb = Bbox.Empty;
        foreach (var (x, y) in corners)
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(x, y, inst, 0, 0);
            bb = bb.Union(new Bbox(wx, wy, wx, wy));
        }
        return bb;
    }

    private static Bbox ArrayExpand(Bbox baseBbox, LayoutInstance inst)
    {
        if (baseBbox.IsEmpty) return baseBbox;
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        if (rows == 1 && cols == 1) return baseBbox;

        long maxDx = (cols - 1) * inst.PitchX, maxDy = (rows - 1) * inst.PitchY;
        long minDx = Math.Min(0, maxDx), minDy = Math.Min(0, maxDy);
        maxDx = Math.Max(0, maxDx); maxDy = Math.Max(0, maxDy);

        return new Bbox(
            baseBbox.MinX + minDx, baseBbox.MinY + minDy,
            baseBbox.MaxX + maxDx, baseBbox.MaxY + maxDy);
    }
}
