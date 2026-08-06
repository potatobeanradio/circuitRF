namespace CircuitRF.Engine.Mom;

/// <summary>
/// The small amount of polygon geometry kernel A needs: winding, containment, horizontal-line
/// footprint (for R-mom-9's interface exclusion) and the tiny inward offset Wheeler's rule
/// requires (R-mom-12).  Deliberately self-contained — <c>src/Engine</c> has no geometry library
/// and must not acquire a UI one.
/// </summary>
public static class Polygon2D
{
    /// <summary>Twice the signed area; positive for a counter-clockwise winding.</summary>
    public static double SignedArea2(IReadOnlyList<EmPoint> poly)
    {
        double s = 0;
        for (int i = 0, n = poly.Count; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s;
    }

    public static double Area(IReadOnlyList<EmPoint> poly) => 0.5 * Math.Abs(SignedArea2(poly));

    /// <summary>Returns the outline wound counter-clockwise, so the right normal is outward.</summary>
    public static IReadOnlyList<EmPoint> AsCcw(IReadOnlyList<EmPoint> poly)
    {
        if (SignedArea2(poly) >= 0) return poly;
        var r = new EmPoint[poly.Count];
        for (int i = 0; i < poly.Count; i++) r[i] = poly[poly.Count - 1 - i];
        return r;
    }

    public static (double X0, double Y0, double X1, double Y1) Bounds(IReadOnlyList<EmPoint> poly)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in poly)
        {
            if (p.X < x0) x0 = p.X;
            if (p.Y < y0) y0 = p.Y;
            if (p.X > x1) x1 = p.X;
            if (p.Y > y1) y1 = p.Y;
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>Distance from <paramref name="p"/> to the polygon boundary.</summary>
    public static double DistanceToBoundary(IReadOnlyList<EmPoint> poly, EmPoint p)
    {
        double best = double.MaxValue;
        for (int i = 0, n = poly.Count; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            var d = b - a;
            double len2 = d.X * d.X + d.Y * d.Y;
            double t = len2 <= 0 ? 0 : Math.Clamp(((p - a).Dot(d)) / len2, 0, 1);
            var q = a + d * t;
            best = Math.Min(best, (p - q).Norm);
        }
        return best;
    }

    /// <summary>Even-odd containment, boundary excluded (use <paramref name="tol"/> via
    /// <see cref="ContainsOrOn"/> when the boundary must count).</summary>
    public static bool ContainsStrict(IReadOnlyList<EmPoint> poly, EmPoint p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Containment with the boundary counted as inside, to <paramref name="tol"/> metres.</summary>
    public static bool ContainsOrOn(IReadOnlyList<EmPoint> poly, EmPoint p, double tol)
        => ContainsStrict(poly, p) || DistanceToBoundary(poly, p) <= tol;

    /// <summary>
    /// The x-intervals over which the horizontal line y = <paramref name="y"/> lies inside or on
    /// the polygon — the footprint that R-mom-9 excludes from the dielectric-interface mesh. Works
    /// for the degenerate-but-normal case where a conductor <i>sits on</i> the interface, which a
    /// plain crossing count gets wrong.
    /// </summary>
    public static List<(double X0, double X1)> HorizontalFootprint(
        IReadOnlyList<EmPoint> poly, double y, double tol)
    {
        var (bx0, by0, bx1, by1) = Bounds(poly);
        if (y < by0 - tol || y > by1 + tol) return [];

        // Candidate breakpoints: every vertex x, and every edge crossing of y = const.
        var xs = new List<double>();
        for (int i = 0, n = poly.Count; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            xs.Add(a.X);
            if (Math.Abs(b.Y - a.Y) > 0)
            {
                double t = (y - a.Y) / (b.Y - a.Y);
                if (t >= 0 && t <= 1) xs.Add(a.X + t * (b.X - a.X));
            }
        }
        xs.Add(bx0);
        xs.Add(bx1);
        xs.Sort();

        var spans = new List<(double, double)>();
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            double x0 = xs[i], x1 = xs[i + 1];
            if (x1 - x0 <= tol) continue;
            var mid = new EmPoint(0.5 * (x0 + x1), y);
            if (!ContainsOrOn(poly, mid, tol)) continue;
            if (spans.Count > 0 && x0 - spans[^1].Item2 <= tol)
                spans[^1] = (spans[^1].Item1, x1);
            else
                spans.Add((x0, x1));
        }
        return spans;
    }

    /// <summary>
    /// Offset a simple polygon inward (for CCW input) by <paramref name="delta"/> metres — the
    /// R-mom-12 recession. Every edge is displaced along its inward normal and adjacent offset
    /// edge lines are intersected, so the vertex count is preserved; that is what lets the receded
    /// geometry be re-meshed with a topologically identical mesh and the finite difference stay
    /// clean. Valid only for a δ small against the local feature size, which is exactly the regime
    /// Wheeler's rule is used in.
    /// </summary>
    public static IReadOnlyList<EmPoint> OffsetInward(IReadOnlyList<EmPoint> polyCcw, double delta)
    {
        int n = polyCcw.Count;
        var result = new EmPoint[n];

        for (int i = 0; i < n; i++)
        {
            var prev = polyCcw[(i - 1 + n) % n];
            var cur  = polyCcw[i];
            var next = polyCcw[(i + 1) % n];

            var u0 = Unit(cur - prev);
            var u1 = Unit(next - cur);
            // CCW ⇒ interior on the left ⇒ inward normal is the LEFT normal.
            var n0 = u0.LeftNormal;
            var n1 = u1.LeftNormal;

            var p0 = cur + n0 * delta;     // a point on the offset line of edge (prev→cur)
            var p1 = cur + n1 * delta;     // a point on the offset line of edge (cur→next)

            double cross = u0.X * u1.Y - u0.Y * u1.X;
            if (Math.Abs(cross) < 1e-12)
            {
                // Collinear (or reversed) adjacent edges — the two offset lines coincide; the
                // vertex simply translates along the shared normal.
                result[i] = p0;
                continue;
            }
            // Intersect p0 + s·u0 with p1 + t·u1.
            var d = p1 - p0;
            double s = (d.X * u1.Y - d.Y * u1.X) / cross;
            result[i] = p0 + u0 * s;
        }
        return result;
    }

    private static EmPoint Unit(EmPoint v)
    {
        double len = v.Norm;
        return len <= 0 ? new EmPoint(1, 0) : v * (1.0 / len);
    }

    /// <summary>
    /// Returns the (i, j) indices of the first pair of non-adjacent edges that properly cross, or
    /// null when the outline is simple. Used only by <c>CanSolve</c>, where the refusal has to name
    /// which edges are at fault.
    /// </summary>
    public static (int I, int J)? FindSelfIntersection(IReadOnlyList<EmPoint> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
            if (SegmentsProperlyCross(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n]))
                return (i, j);
        }
        return null;
    }

    private static bool SegmentsProperlyCross(EmPoint a, EmPoint b, EmPoint c, EmPoint d)
    {
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b);
        double d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(EmPoint o, EmPoint a, EmPoint b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
}
