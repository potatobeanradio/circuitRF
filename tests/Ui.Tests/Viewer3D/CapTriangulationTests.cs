// brief-em3d-28 gate 6 — the triangulator behind an extruded polygon's caps (R-em3d28-2a).
//
// On every fixture shape: the triangles' area equals the polygon's area minus its holes to 1e-12
// relative, every triangle is wound counter-clockwise, and none lies outside the outline or inside a
// hole (its centroid and its edge midpoints are all in the polygon). The shapes are the ones a layout
// produces: slivers, collinear runs (exact, and a diagonal one from DBU that is only nearly collinear), a
// hole touching the outline at one vertex, holes touching each other, an outline touching itself, and a
// 10,000-vertex board outline.

using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class CapTriangulationTests
{
    private static List<Point2> R(params double[] xy) => [.. Enumerable.Range(0, xy.Length / 2).Select(i => new Point2(xy[2 * i], xy[2 * i + 1]))];

    private static List<Point2> Circle(double cx, double cy, double r, int n, bool cw = false)
        => [.. Enumerable.Range(0, n).Select(k => (cw ? -1 : 1) * 2 * Math.PI * k / n).Select(t => new Point2(cx + r * Math.Cos(t), cy + r * Math.Sin(t)))];

    /// <summary>A rectangle with <paramref name="n"/> points on every edge — a collinear run.</summary>
    private static List<Point2> DenseRect(double w, double h, int n)
    {
        var p = new List<Point2>();
        for (int i = 0; i < n; i++) p.Add(new(w * i / n, 0));
        for (int i = 0; i < n; i++) p.Add(new(w, h * i / n));
        for (int i = 0; i < n; i++) p.Add(new(w - w * i / n, h));
        for (int i = 0; i < n; i++) p.Add(new(0, h - h * i / n));
        return p;
    }

    public static TheoryData<string> Shapes => [.. Fixtures.Keys];

    private static readonly Dictionary<string, (List<Point2> Outline, List<List<Point2>> Holes)> Fixtures = new()
    {
        ["rectangle"]            = (R(0, 0, 2e-3, 0, 2e-3, 1e-3, 0, 1e-3), []),
        ["L, clockwise"]         = (R(0, 0, 0, 2, 1, 2, 1, 1, 2, 1, 2, 0), []),
        ["comb"]                 = (R(0, 0, 10, 0, 10, 3, 9, 3, 9, 1, 7, 1, 7, 3, 6, 3, 6, 1, 4, 1, 4, 3, 3, 3, 3, 1, 1, 1, 1, 3, 0, 3), []),
        ["sliver"]               = (R(0, 0, 1e-2, 0, 1e-2, 1e-9, 0, 2e-9), []),
        ["sliver triangle"]      = (R(0, 0, 1, 0, 0.5, 1e-7), []),
        ["collinear runs"]       = (DenseRect(3e-3, 1e-3, 50), [DenseRect(1e-3, 0.2e-3, 7).Select(p => new Point2(p.X + 1e-3, p.Y + 0.4e-3)).ToList()]),
        ["closing repeat"]       = (R(0, 0, 4, 0, 4, 4, 0, 4, 0, 0), [R(1, 1, 2, 1, 2, 2, 1, 2, 1, 1)]),
        ["hole at a corner"]     = (R(0, 0, 10, 0, 10, 10, 0, 10), [R(0, 0, 3, 1, 1, 3)]),
        ["hole on an edge"]      = (R(0, 0, 10, 0, 10, 10, 0, 10), [R(5, 0, 6, 2, 4, 2)]),
        ["three holes"]          = (R(0, 0, 10, 0, 10, 6, 0, 6), [R(1, 1, 3, 1, 3, 3, 1, 3), Circle(6, 3, 1.5, 24, cw: true), R(8, 1, 9, 1, 8.5, 5)]),
        ["diagonal run in DBU"]  = (DiagonalRun(), []),
        ["pinched at one vertex"] = (R(0, 0, 1, 0, 1, 1, 2, 1, 2, 2, 1, 2, 1, 1, 0, 1), []),
        ["two holes touching"]   = (R(0, 0, 10, 0, 10, 10, 0, 10), [R(2, 2, 5, 2, 5, 5, 2, 5), R(5, 5, 8, 5, 8, 8, 5, 8)]),
        ["board, 10,000 vertices"] = (Board(), [.. Enumerable.Range(0, 12).Select(k => Circle(-0.06 + 0.024 * (k % 6), -0.015 + 0.03 * (k / 6), 4e-3, 32))]),
    };

    /// <summary>A 45° edge sampled at integer DBU and scaled to metres, as a layout hands it over: only
    /// NEARLY collinear in doubles, so no point is filtered and each is a tiny reflex or convex one.</summary>
    private static List<Point2> DiagonalRun()
    {
        const double dbu = 1e-9;
        var p = new List<Point2> { new(0, 0), new(3000 * dbu, 0) };
        for (int k = 1; k <= 997; k++) p.Add(new((3000 + 7 * k) * dbu, 7 * k * dbu));
        p.Add(new(0, 7 * 997 * dbu));
        return p;
    }

    /// <summary>A board-sized wavy outline, 10,000 vertices, 0.2 × 0.1 m.</summary>
    private static List<Point2> Board()
    {
        const int n = 10_000;
        return [.. Enumerable.Range(0, n).Select(k =>
        {
            double t = 2 * Math.PI * k / n, r = 1 + 0.04 * Math.Sin(37 * t);
            return new Point2(0.1 * r * Math.Cos(t), 0.05 * r * Math.Sin(t));
        })];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Gate6_AreaIsThePolygonsMinusItsHoles_AndNoTriangleLiesOutside(string shape)
    {
        var (outline, holes) = Fixtures[shape];
        var rings = new List<IReadOnlyList<Point2>> { outline };
        rings.AddRange(holes);
        var all = rings.SelectMany(r => r).ToList();

        var tris = Em3dPolygonTriangulation.Triangulate(outline, [.. holes]);
        double expected = Math.Abs(Em3dPolygonTriangulation.SignedArea2(outline)) / 2
                        - holes.Sum(h => Math.Abs(Em3dPolygonTriangulation.SignedArea2(h)) / 2);

        double sum = 0;
        foreach (var t in tris)
        {
            var (a, b, c) = (all[t.A], all[t.B], all[t.C]);
            double area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            Assert.True(area2 >= 0, $"{shape}: triangle ({t.A},{t.B},{t.C}) is wound clockwise");
            sum += area2 / 2;
            if (area2 == 0) continue;
            foreach (var p in new[] { Mid(a, b, c), Mid(a, b), Mid(b, c), Mid(a, c) })
                Assert.True(InsideOrOn(p, rings), $"{shape}: triangle ({t.A},{t.B},{t.C}) reaches outside the polygon at ({p.X}, {p.Y})");
        }
        Assert.True(Math.Abs(sum - expected) <= 1e-12 * expected, $"{shape}: area {sum:R} vs {expected:R}");
        Assert.Equal(tris, Em3dPolygonTriangulation.Triangulate(outline, [.. holes]));     // deterministic
    }

    [Fact]
    public void AnExtrudedPolygon_IsAClosedSolid_WhoseVolumeIsItsAreaTimesItsHeight()
    {
        var (outline, holes) = Fixtures["three holes"];
        var solid = new Em3dSolid("p", "m", Em3dRole.Conductor,
                                  new Em3dExtrudedPolygon(outline, [.. holes], 0.5, 2.0), 1);
        var mesh = Em3dTessellation.Of(solid);
        double area = Math.Abs(Em3dPolygonTriangulation.SignedArea2(outline)) / 2
                    - holes.Sum(h => Math.Abs(Em3dPolygonTriangulation.SignedArea2(h)) / 2);
        Assert.Equal(area * 1.5, Em3dSizeEstimate.MeshVolume(mesh), 12);
        Assert.Equal(area * 1.5, Em3dSizeEstimate.Volume(solid.Primitive), 12);
    }

    private static Point2 Mid(params Point2[] p) => new(p.Average(q => q.X), p.Average(q => q.Y));

    /// <summary>Inside by even-odd over every ring, or within a hair of an edge.</summary>
    private static bool InsideOrOn(Point2 p, IReadOnlyList<IReadOnlyList<Point2>> rings)
    {
        bool inside = false;
        double scale = rings[0].Max(q => Math.Max(Math.Abs(q.X), Math.Abs(q.Y)));
        foreach (var r in rings)
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            {
                var (a, b) = (r[j], r[i]);
                if (OnSegment(p, a, b, 1e-12 * scale)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    private static bool OnSegment(Point2 p, Point2 a, Point2 b, double tol)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 == 0) return Math.Abs(p.X - a.X) <= tol && Math.Abs(p.Y - a.Y) <= tol;
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return ex * ex + ey * ey <= tol * tol;
    }
}
