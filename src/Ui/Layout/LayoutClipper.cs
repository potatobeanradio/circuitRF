// The single Clipper2 conversion point (docs/design/layout-view.md §6.1): booleans, offsets, DRC
// (L5b), the mesher (L6), and export (L4) all call ToClipperPaths/FromClipperTree, so the flattening
// tolerance is never chosen twice with two different answers. Built on LayoutFlattener — this file
// must NOT re-implement curve flattening.
//
// Our DBU integers go straight in: Clipper2's Path64/Point64 are long-based, exactly §1.1's storage
// type. No scaling to a working integer grid, no float conversion, no precision loss anywhere in this
// pipeline — the tempting "scale to a working integer grid" step other clipping libraries need is
// simply absent here. Coordinate magnitudes (<= ~10^9) sit far inside Clipper2's safe range.
//
// Fill rule: FillRule.NonZero everywhere, stated once here and never varied per call site — it is
// what makes self-intersection repair (LayoutBooleans.Repair) produce the outer region rather than a
// checkerboard.

using Clipper2Lib;

namespace CircuitRF.Ui.Layout;

public static class LayoutClipper
{
    public const FillRule Rule = FillRule.NonZero;

    /// <summary>Converts one shape's geometry to Clipper2's integer path form, at
    /// <paramref name="tolDbu"/> flattening tolerance. <c>PathShape</c> gets its geometry OUTLINE
    /// here, via <c>InflatePaths</c> on the flattened centerline at <c>Width/2</c> with the join/cap
    /// matching its <c>End</c> style — this is NOT the display outline (R-L1e-1): the renderer's
    /// <c>LayoutRenderer.BuildPathOutline</c> keeps using the Skia stroker + Simplify so a curved
    /// trace still renders with adaptive, zoom-correct curves. Two outlines, two purposes; do not
    /// unify them.</summary>
    public static Paths64 ToClipperPaths(LayoutShape shape, long tolDbu)
    {
        if (shape is PathShape path)
            return PathOutlinePaths(path, tolDbu);

        var rings = LayoutFlattener.Flatten(shape, tolDbu);
        var paths = new Paths64(rings.Count);
        foreach (var ring in rings)
            paths.Add(RingToPath64(ring));
        return paths;
    }

    /// <summary>Wraps rings a caller already flattened itself (e.g. <c>LayoutTextFlatten</c>'s glyph
    /// contours, each flattened individually via <see cref="LayoutFlattener.Flatten"/> since they are
    /// not one shape's own rings) into Clipper2 <see cref="Paths64"/> — for callers that need the
    /// DBU-to-Clipper2 conversion without also re-flattening through <see cref="ToClipperPaths"/>'s
    /// single-shape path.</summary>
    public static Paths64 RingsToClipperPaths(IEnumerable<long[]> rings)
    {
        var paths = new Paths64();
        foreach (var ring in rings) paths.Add(RingToPath64(ring));
        return paths;
    }

    /// <summary>Rebuilds shapes from a Clipper2 boolean/offset result, preserving the hole structure
    /// (§3.1a) the tree already encodes: every non-hole node becomes one <see cref="PolygonShape"/>
    /// whose immediate hole children become its <c>Holes</c>; islands nested inside a hole recurse as
    /// further top-level shapes. Deterministic — a fixed walk order over Clipper2's own (deterministic)
    /// tree, no dictionaries, no parallelism (R-L1c-1's determinism discipline, extended here).</summary>
    public static IReadOnlyList<LayoutShape> FromClipperTree(PolyTree64 tree, LayerKey layer, string? net)
    {
        var results = new List<LayoutShape>();
        CollectSolids(tree, results, layer, net);
        return results;
    }

    private static void CollectSolids(PolyPath64 node, List<LayoutShape> results, LayerKey layer, string? net)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var solid = node[i];   // IsHole == false at this recursion level
            List<long[]>? holes = null;
            for (int j = 0; j < solid.Count; j++)
            {
                var hole = solid[j];   // IsHole == true
                (holes ??= []).Add(Path64ToRing(hole.Polygon));
                CollectSolids(hole, results, layer, net);   // islands nested inside this hole
            }
            results.Add(new PolygonShape { Layer = layer, Net = net, Xy = Path64ToRing(solid.Polygon), Holes = holes });
        }
    }

    // ── DBU <-> Clipper2 ─────────────────────────────────────────────────────

    private static Path64 RingToPath64(long[] ring)
    {
        int n = ring.Length / 2;
        var path = new Path64(n);
        for (int i = 0; i < n; i++)
            path.Add(new Point64(ring[2 * i], ring[2 * i + 1]));
        return path;
    }

    private static long[] Path64ToRing(Path64? path)
    {
        if (path is null) return [];
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++)
        {
            xy[2 * i] = path[i].X;
            xy[2 * i + 1] = path[i].Y;
        }
        return xy;
    }

    // ── PathShape -> geometry outline via InflatePaths ────────────────────────

    private static Paths64 PathOutlinePaths(PathShape path, long tolDbu)
    {
        var centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tolDbu);
        if (centerline.Length < 4) return [];   // fewer than 2 points — no outline to build

        var subject = new Paths64 { RingToPath64(centerline) };
        double delta = path.Width / 2.0;
        var endType = path.End switch
        {
            PathEndStyle.Round  => EndType.Round,
            PathEndStyle.Square => EndType.Square,
            PathEndStyle.Extended => EndType.Square,   // same offset amount as Square — see LayoutRenderer.ExtendedCenterline
            _                    => EndType.Butt,       // Flush
        };
        return Clipper.InflatePaths(subject, delta, JoinType.Round, endType);
    }

    // ── R-L1e-0 / §3.1a R10b: enforce hole validity on any non-Clipper2 construction path ─────────

    /// <summary>A hole must lie inside its outer ring and intersect neither that ring nor another
    /// hole. Clipper2's own <see cref="PolyTree64"/> output (every boolean/offset/repair in
    /// <c>LayoutBooleans</c>) already satisfies this by construction. This is the enforcement point
    /// for any OTHER way holes can enter the model — today that is a hand-edited <c>.clay</c> file
    /// (<c>LayoutPersistence</c> calls this for every loaded shape); a future paste (L1f) or import
    /// (L4) should call it too. Cheap no-op when the shape is already valid — only a genuinely invalid
    /// hole triggers the Clipper2 <c>Union</c> re-derivation, which may reorder vertices/holes (a
    /// normally-constructed shape never observes that, since it is already valid).</summary>
    public static IReadOnlyList<LayoutShape> EnsureValidHoles(LayoutShape shape)
    {
        var holes = shape switch
        {
            PolygonShape p => p.Holes,
            CurveShape c    => c.Holes,
            _               => null,
        };
        if (holes is not { Count: > 0 }) return [shape];

        long tol = LayoutFlattener.ResolveTolDbu(shape, null);
        var rings = LayoutFlattener.Flatten(shape, tol);
        if (HolesAreValid(rings)) return [shape];

        var paths = ToClipperPaths(shape, tol);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, Rule);
        return FromClipperTree(tree, shape.Layer, shape.Net);
    }

    private static bool HolesAreValid(IReadOnlyList<long[]> rings)
    {
        var outer = rings[0];
        for (int i = 1; i < rings.Count; i++)
        {
            var hole = rings[i];
            foreach (var v in EnumeratePoints(hole))
                if (!PointInOrOnRing(outer, v.X, v.Y)) return false;
            if (RingsIntersect(hole, outer)) return false;

            for (int j = i + 1; j < rings.Count; j++)
                if (RingsIntersect(hole, rings[j])) return false;
        }
        return true;
    }

    private static IEnumerable<(long X, long Y)> EnumeratePoints(long[] xy)
    {
        for (int i = 0; i < xy.Length; i += 2)
            yield return (xy[i], xy[i + 1]);
    }

    private static bool PointInOrOnRing(long[] ring, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            if (OnSegment(px, py, xi, yi, xj, yj)) return true;
            bool crosses = (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        if (Math.Abs(cross) > 1e-6) return false;
        double dot = (px - ax) * (bx - ax) + (py - ay) * (by - ay);
        double lenSq = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        return dot >= 0 && dot <= lenSq;
    }

    private static bool RingsIntersect(long[] a, long[] b)
    {
        int na = a.Length / 2, nb = b.Length / 2;
        for (int i = 0; i < na; i++)
        {
            var (ax0, ay0, ax1, ay1) = RingSegment(a, i, na);
            for (int j = 0; j < nb; j++)
            {
                var (bx0, by0, bx1, by1) = RingSegment(b, j, nb);
                if (SegmentsIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1)) return true;
            }
        }
        return false;
    }

    private static (double, double, double, double) RingSegment(long[] xy, int i, int n)
    {
        int j = (i + 1) % n;
        return (xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
    }

    private static bool SegmentsIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        // Collinear/touching cases — conservative (treat touching as intersecting, per R10b).
        if (d1 == 0 && OnSegment(ax0, ay0, bx0, by0, bx1, by1)) return true;
        if (d2 == 0 && OnSegment(ax1, ay1, bx0, by0, bx1, by1)) return true;
        if (d3 == 0 && OnSegment(bx0, by0, ax0, ay0, ax1, ay1)) return true;
        if (d4 == 0 && OnSegment(bx1, by1, ax0, ay0, ax1, ay1)) return true;
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
