// "Flatten to Polygon" (docs/design/layout-view.md §3.2 R9d) — replaces a curved primitive with the
// polygon export would have produced. Deliberately NOT built on LayoutClipper/LayoutBooleans: this is
// a pure LayoutFlattener consumer (no Clipper2 involved — there is nothing to union, intersect, or
// re-derive winding for; the flattener's own outer-ring-then-holes order already matches
// PolygonShape's contract exactly).

namespace CircuitRF.Ui.Layout;

public static class LayoutFlattenToPolygon
{
    /// <summary>True when <paramref name="shape"/> has at least one non-<c>Line</c> edge (or, for
    /// <c>Circle</c>/<c>RoundedRect</c>, is inherently curved) — the "has nothing to flatten" test a
    /// multi-selection uses to silently skip shapes.</summary>
    public static bool HasCurvedGeometry(LayoutShape shape) => shape switch
    {
        CircleShape c       => c.R > 0,
        RoundedRectShape rr => rr.CornerRadius > 0,
        CurveShape curve    => HasAnyCurvedEdge(curve.Xy, curve.Edges, closed: true),
        PathShape path      => HasAnyCurvedEdge(path.Xy, path.Edges, closed: false),
        _                   => false,
    };

    private static bool HasAnyCurvedEdge(long[] xy, List<LayoutEdge>? edges, bool closed)
    {
        if (edges is null) return false;
        int n = xy.Length / 2;
        int edgeCount = closed ? n : Math.Max(0, n - 1);
        for (int i = 0; i < edgeCount && i < edges.Count; i++)
            if (edges[i].Kind != EdgeKind.Line) return true;
        return false;
    }

    /// <summary>
    /// Flattens one shape at <paramref name="tolDbu"/>. <c>Circle</c>/<c>RoundedRect</c>/<c>Curve</c>
    /// become an ordinary <see cref="PolygonShape"/> (holes carried through unchanged — they are
    /// already flat rings, needing no flattening of their own). A <c>Path</c> with curved edges
    /// flattens its centerline edges to <c>Line</c> and STAYS a <see cref="PathShape"/> — its width
    /// and end style survive; converting a trace's centerline into a filled outline is a different
    /// (and lossy) operation, and users flattening a trace expect to keep editing its width. Returns
    /// null when <paramref name="shape"/> has nothing to flatten (<see cref="HasCurvedGeometry"/> is
    /// false) — callers use that to silently skip in a multi-selection.
    /// </summary>
    public static LayoutShape? FlattenToPolygon(LayoutShape shape, long tolDbu)
    {
        if (!HasCurvedGeometry(shape)) return null;

        if (shape is PathShape path)
        {
            var flatXy = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tolDbu);
            return new PathShape
            {
                Layer = path.Layer, Net = path.Net, Xy = flatXy, Edges = null,
                Width = path.Width, End = path.End, FlattenTolDbu = path.FlattenTolDbu,
            };
        }

        var rings = LayoutFlattener.Flatten(shape, tolDbu);
        var outer = (long[])rings[0].Clone();
        List<long[]>? holes = rings.Count > 1 ? rings.Skip(1).Select(r => (long[])r.Clone()).ToList() : null;
        return new PolygonShape { Layer = shape.Layer, Net = shape.Net, Xy = outer, Holes = holes };
    }

    /// <summary>Live vertex-count preview for the "Flatten to Polygon…" tolerance prompt — the same
    /// count the resulting shape's <c>Xy</c> would have (outer ring only; a Path's flattened
    /// centerline point count for a Path).</summary>
    public static int PreviewVertexCount(LayoutShape shape, long tolDbu) => FlattenToPolygon(shape, tolDbu) switch
    {
        PolygonShape p => p.Xy.Length / 2,
        PathShape p    => p.Xy.Length / 2,
        _              => 0,
    };
}
