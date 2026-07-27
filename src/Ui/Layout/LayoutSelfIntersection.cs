// Framework-free self-intersection detection (docs/design/layout-view.md L1d brief §5). Flags, never
// blocks or repairs — auto-repair via a Clipper2 union is L1e. Reuses LayoutFlattener's output so a
// curved edge is tested against its actual (flattened) geometry, then does a simple O(n^2)
// segment-intersection sweep — no spatial index, per this phase's scope guardrails.

namespace CircuitRF.Ui.Layout;

public static class LayoutSelfIntersection
{
    /// <summary>True if <paramref name="shape"/>'s outline crosses itself. Only meaningful for
    /// shapes with more than one edge (Rect/Circle can never self-intersect by construction).</summary>
    public static bool Test(LayoutShape shape, Technology? tech)
    {
        long tol = LayoutFlattener.ResolveTolDbu(shape, tech);

        switch (shape)
        {
            case RectShape:
            case CircleShape:
                return false; // structurally simple — never self-intersecting

            case PolygonShape or RoundedRectShape or CurveShape:
            {
                IReadOnlyList<long[]> rings;
                try { rings = LayoutFlattener.Flatten(shape, tol); }
                catch { return false; }
                foreach (var ring in rings)
                    if (HasSelfIntersection(ring, closed: true)) return true;
                return false;
            }

            case PathShape path:
            {
                long[] centerline;
                try { centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tol); }
                catch { return false; }
                return HasSelfIntersection(centerline, closed: false);
            }

            default:
                return false;
        }
    }

    private static bool HasSelfIntersection(long[] xy, bool closed)
    {
        int n = xy.Length / 2;
        int segCount = closed ? n : n - 1;
        if (segCount < 3) return false; // fewer than 3 segments can never cross themselves

        for (int i = 0; i < segCount; i++)
        {
            var (ax0, ay0, ax1, ay1) = Segment(xy, i, closed, n);
            for (int j = i + 1; j < segCount; j++)
            {
                if (AreAdjacent(i, j, segCount, closed)) continue;
                var (bx0, by0, bx1, by1) = Segment(xy, j, closed, n);
                if (SegmentsProperlyIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1))
                    return true;
            }
        }
        return false;
    }

    private static (long X0, long Y0, long X1, long Y1) Segment(long[] xy, int i, bool closed, int n)
    {
        int j = closed ? (i + 1) % n : i + 1;
        return (xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
    }

    private static bool AreAdjacent(int i, int j, int segCount, bool closed)
    {
        int d = Math.Abs(i - j);
        if (d <= 1) return true;
        if (closed && d == segCount - 1) return true; // wraps (segment 0 and the last segment share a vertex)
        return false;
    }

    /// <summary>Standard proper-crossing test via orientation (cross-product) signs — does not
    /// special-case collinear overlaps or touching endpoints; good enough for a "flag it"
    /// heuristic, not a precise CAD-grade predicate.</summary>
    private static bool SegmentsProperlyIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
