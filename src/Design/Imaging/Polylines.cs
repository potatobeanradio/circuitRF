// Douglas–Peucker, open and closed — shared by the skeleton graph (R-im1-6) and the contours (R-im1-7).

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

internal static class Polylines
{
    /// <summary>The indices of <paramref name="pts"/> an open Douglas–Peucker at <paramref name="tol"/> keeps; the
    /// first and last always.</summary>
    public static List<int> SimplifyOpen(IReadOnlyList<PointD> pts, double tol)
    {
        var keep = new List<int>();
        if (pts.Count == 0) return keep;
        if (pts.Count <= 2)
        {
            for (int i = 0; i < pts.Count; i++) keep.Add(i);
            return keep;
        }
        var flag = new bool[pts.Count];
        flag[0] = flag[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        double tol2 = tol * tol;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            int far = -1;
            double fd = tol2;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegmentDistance2(pts[i], pts[a], pts[b]);
                if (d > fd) { fd = d; far = i; }
            }
            if (far < 0) continue;
            flag[far] = true;
            stack.Push((a, far));
            stack.Push((far, b));
        }
        for (int i = 0; i < flag.Length; i++) if (flag[i]) keep.Add(i);
        return keep;
    }

    /// <summary>The indices of the closed ring <paramref name="ring"/> a Douglas–Peucker keeps. The ring is cut at its
    /// first vertex and at the vertex farthest from it — both kept — so where the ring starts does not decide its
    /// corners.</summary>
    public static List<int> SimplifyClosed(IReadOnlyList<PointD> ring, double tol)
    {
        int n = ring.Count;
        if (n <= 3) return Enumerable.Range(0, n).ToList();
        int far = 0;
        double fd = -1;
        for (int i = 1; i < n; i++)
        {
            double dx = ring[i].x - ring[0].x, dy = ring[i].y - ring[0].y, d = dx * dx + dy * dy;
            if (d > fd) { fd = d; far = i; }
        }
        var first = new List<PointD>();
        for (int i = 0; i <= far; i++) first.Add(ring[i]);
        var second = new List<PointD>();
        for (int i = far; i <= n; i++) second.Add(ring[i % n]);
        var k1 = SimplifyOpen(first, tol);
        var k2 = SimplifyOpen(second, tol);
        var keep = new List<int>(k1);
        for (int i = 1; i < k2.Count - 1; i++) keep.Add(far + k2[i]);
        return keep;
    }

    public static double SegmentDistance2(PointD p, PointD a, PointD b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0, 1);
        double ex = a.x + t * dx - p.x, ey = a.y + t * dy - p.y;
        return ex * ex + ey * ey;
    }

    public static double Length(IReadOnlyList<PointD> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Math.Sqrt(Sq(pts[i].x - pts[i - 1].x) + Sq(pts[i].y - pts[i - 1].y));
        return s;
    }

    private static double Sq(double v) => v * v;
}
