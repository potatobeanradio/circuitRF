// brief-hzn-13 R-hzn13-2 — an ellipse sheet built as the polygon every consumer reads.
//
// ONE POLYGON, BUILT IN ONE PLACE. Em3dSheet is a polygon, and so is every reader below the lowering (the scene, a port's touch test,
// the sheet booleans, Gmsh, EMerge, openEMS), so an ellipse becomes one here and nothing downstream learns a new shape. The
// vertices lie ON the ellipse, rounded to the DBU, the first at the +u end and the rest counter-clockwise in (u, v). At the default
// 72 vertices the polygon's area is 0.13 % under the ellipse's and its edges come at most 0.095 % of the radius inside it.

namespace CircuitRF.Design.ThreeD;

public static class C3dEllipses
{
    /// <summary>The polygon's vertex count when <see cref="C3dEllipse.Segments"/> is omitted.</summary>
    public const int DefaultSegments = 72;

    /// <summary>The fewest and most vertices a stated <see cref="C3dEllipse.Segments"/> may ask for.</summary>
    public const int MinSegments = 8, MaxSegments = 1024;

    /// <summary>The vertex count the ellipse is built with.</summary>
    public static int SegmentsOf(C3dEllipse e) => e.Segments ?? DefaultSegments;

    /// <summary>The ellipse as a polygon in its plane's (u, v), integer DBU: <see cref="SegmentsOf"/> vertices on the ellipse, the first
    /// at the +u end, counter-clockwise. Empty when a radius is not positive or the count is out of range (validation says which).</summary>
    public static List<C3dPoint2> Ring(C3dEllipse e)
    {
        int n = SegmentsOf(e);
        if (e.Radii.U <= 0 || e.Radii.V <= 0 || n < MinSegments || n > MaxSegments) return [];
        var ring = new List<C3dPoint2>(n);
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / n;
            // Exact at the quarter points, so a circle's extremes are its radius to the DBU whatever the count.
            var (c, s) = (k * 4 % n == 0) ? QuarterCosSin(k * 4 / n) : (Math.Cos(a), Math.Sin(a));
            ring.Add(new C3dPoint2(e.Centre.U + (long)Math.Round(e.Radii.U * c, MidpointRounding.AwayFromZero),
                                   e.Centre.V + (long)Math.Round(e.Radii.V * s, MidpointRounding.AwayFromZero)));
        }
        return ring;

        static (double, double) QuarterCosSin(int q) => q switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
    }

    /// <summary>Why the ellipse cannot be built, or null.</summary>
    public static string? Fault(C3dEllipse e)
    {
        if (e.Radii.U <= 0 || e.Radii.V <= 0) return "is an ellipse whose radii are not both positive";
        if (e.Segments is { } n && (n < MinSegments || n > MaxSegments))
            return $"is an ellipse of {n} segments; Segments is a whole number from {MinSegments} to {MaxSegments}";
        return null;
    }

    /// <summary>
    /// A sheet's own polygon in its plane's (u, v), DBU, whichever shape it has: its rectangle's four corners, its outline and holes,
    /// or its ellipse's ring. The one place a reader that wants the polygon gets it, so a fourth shape would be added here once.
    /// </summary>
    public static (List<C3dPoint2> Outline, List<List<C3dPoint2>> Holes) Polygon(C3dSheet s)
    {
        if (s.Rect is { } r)
            return ([r.Min, new(r.Min.U + r.Size.U, r.Min.V), new(r.Min.U + r.Size.U, r.Min.V + r.Size.V), new(r.Min.U, r.Min.V + r.Size.V)], []);
        if (s.Ellipse is { } e) return (Ring(e), []);
        return (s.Outline, s.Holes);
    }
}
