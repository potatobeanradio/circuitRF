// Conformal boundary cells — Tier 1 for the CUT cell: the six closed forms over a polygon.
//
// This is RectangleIntegralTests' own ladder, re-run for PolygonIntegrals, and it is the measurement
// §3 asks for before anything is concluded about route (a). Two references, deliberately different in
// kind:
//
//   * RectangleIntegrals itself, on a RECTANGLE. Two completely independent derivations — a corner
//     antiderivative summed over four corners, against an angular reduction summed over four edges —
//     agreeing to round-off is a stronger statement than either against a quadrature.
//
//   * An adaptive SLAB integrator, on a genuinely cut shape. Slabs are the one decomposition that is
//     not the fan: it integrates in x and y with grading toward the singular point, and shares no
//     idea with the closed form. **Additivity would NOT have been a test** — the edge sum makes an
//     interior split edge cancel identically, so a polygon partition agreeing with the whole is a
//     tautology rather than evidence. That is why the slab reference exists.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PolygonIntegralTests
{
    private const double Shrink = 0.1;
    private const int    Levels = 12;
    private const int    PanelN = 16;

    // ── T1: the polygon form reproduces the rectangle form ─────────────────────────────────────
    //
    // Every placement RectangleIntegralTests uses, and the rectangle expressed as a four-vertex
    // counter-clockwise ring. If the edge reduction had a sign, a normal or a branch wrong, this is
    // where it shows — at round-off, not at a tolerance.

    public static TheoryData<string, double, double, double, double> Placements() => new()
    {
        { "interior",     -0.37,  0.63, -0.21,  0.79 },
        { "edge",          0.0,   1.0,  -0.4,   0.6  },
        { "corner",        0.0,   1.3,   0.0,   0.7  },
        { "edge-top",     -0.5,   0.5,  -0.9,   0.0  },
        { "far",           7.0,   9.5,   4.0,   6.5  },
        { "sliver",        0.0,   1.0,   0.0,   1e-4 },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void T1_RectangleAgreesWithRectangleIntegrals(string name,
                                                         double x1, double x2, double y1, double y2)
    {
        var ring = Rect(x1, y1, x2, y2);
        double scale = Math.Abs(x2 - x1) * Math.Abs(y2 - y1);

        Near($"{name} area",   PolygonIntegrals.Area(ring, 0, 0),
                               RectangleIntegrals.Area(x1, x2, y1, y2), scale, 1e-13);
        Near($"{name} 1/R",    PolygonIntegrals.Inverse(ring, 0, 0),
                               RectangleIntegrals.Inverse(x1, x2, y1, y2), 1e-13);
        Near($"{name} ln r",   PolygonIntegrals.Log(ring, 0, 0),
                               RectangleIntegrals.Log(x1, x2, y1, y2), 1e-13);
        Near($"{name} r",      PolygonIntegrals.Radius(ring, 0, 0),
                               RectangleIntegrals.Radius(x1, x2, y1, y2), 1e-13);

        Near($"{name} u/R",    PolygonIntegrals.InverseMoment(ring, 0, 0, true),
                               RectangleIntegrals.InverseMomentU(x1, x2, y1, y2), 1e-12);
        Near($"{name} v/R",    PolygonIntegrals.InverseMoment(ring, 0, 0, false),
                               RectangleIntegrals.InverseMomentV(x1, x2, y1, y2), 1e-12);
        Near($"{name} u ln r", PolygonIntegrals.LogMoment(ring, 0, 0, true),
                               RectangleIntegrals.LogMomentU(x1, x2, y1, y2), 1e-12);
        Near($"{name} v ln r", PolygonIntegrals.LogMoment(ring, 0, 0, false),
                               RectangleIntegrals.LogMomentV(x1, x2, y1, y2), 1e-12);
        Near($"{name} u r",    PolygonIntegrals.RadiusMoment(ring, 0, 0, true),
                               RectangleIntegrals.RadiusMomentU(x1, x2, y1, y2), 1e-12);
        Near($"{name} v r",    PolygonIntegrals.RadiusMoment(ring, 0, 0, false),
                               RectangleIntegrals.RadiusMomentV(x1, x2, y1, y2), 1e-12);
        Near($"{name} u dS",   PolygonIntegrals.AreaMoment(ring, 0, 0, true),
                               RectangleIntegrals.AreaMomentU(x1, x2, y1, y2), scale, 1e-13);
        Near($"{name} v dS",   PolygonIntegrals.AreaMoment(ring, 0, 0, false),
                               RectangleIntegrals.AreaMomentV(x1, x2, y1, y2), scale, 1e-13);
    }

    // ── T2: a genuinely CUT cell, against the slab reference ───────────────────────────────────
    //
    // The shapes are what a cut cell actually is: a unit cell with a straight boundary through it,
    // clipping a corner, clipping a side, and leaving a thin sliver. The observation point is placed
    // inside, on the cut, on a vertex, and far outside — the four placements R-fil-4 names, asked of
    // the shape this phase introduces.

    public static TheoryData<string, double, double> CutPlacements() => new()
    {
        { "inside",     0.35,  0.30 },
        { "on the cut", 0.50,  0.50 },
        { "on a vertex", 1.0,  0.0  },
        { "outside",    2.30, -1.70 },
    };

    [Theory]
    [MemberData(nameof(CutPlacements))]
    public void T2_CornerCutAgreesWithSlabQuadrature(string name, double ox, double oy)
    {
        // The unit square with everything above x + y = 1 cut away — the corner-clipping cut, which
        // is the mitre's own shape.
        IReadOnlyList<EmPoint> ring =
            [new EmPoint(0, 0), new EmPoint(1, 0), new EmPoint(0, 1)];
        CheckAgainstSlabs(name + " triangle", ring, ox, oy);
    }

    [Theory]
    [MemberData(nameof(CutPlacements))]
    public void T2b_SideCutAgreesWithSlabQuadrature(string name, double ox, double oy)
    {
        // A trapezoid: the unit square with an oblique boundary crossing two opposite sides — the
        // commonest cut cell on a taper's flank.
        IReadOnlyList<EmPoint> ring =
            [new EmPoint(0, 0), new EmPoint(1, 0), new EmPoint(1, 0.62), new EmPoint(0, 0.23)];
        CheckAgainstSlabs(name + " trapezoid", ring, ox, oy);
    }

    [Fact]
    public void T2c_SliverAgreesWithSlabQuadrature()
    {
        // 1.5% of a cell — below R-cut-3's own merge threshold, which is exactly why the closed form
        // has to survive it: the threshold is a MEASUREMENT and the sweep that takes it has to be
        // able to run with merging off.
        IReadOnlyList<EmPoint> ring =
            [new EmPoint(0, 0), new EmPoint(1, 0), new EmPoint(1, 0.03)];
        CheckAgainstSlabs("sliver", ring, 0.30, 0.004);
        CheckAgainstSlabs("sliver, outside", ring, -0.4, 0.5);
    }

    // ── T3: the fan reproduces the shoelace, which is the reduction's own first check ──────────

    [Fact]
    public void T3_AreaIsTheShoelace()
    {
        IReadOnlyList<EmPoint> ring =
            [new EmPoint(0.1, 0.2), new EmPoint(1.3, -0.4), new EmPoint(1.9, 1.1), new EmPoint(0.4, 1.6)];
        // The shoelace, written out here rather than called, so the reference shares no line of code
        // with the thing it checks.
        double shoelace = 0;
        for (int i = 0, n = ring.Count, j = n - 1; i < n; j = i++)
            shoelace += 0.5 * (ring[j].X * ring[i].Y - ring[i].X * ring[j].Y);
        // From four different observation points: the fan is anchored at the observation point, so
        // independence of it is the statement that the signed fan covers the interior exactly once.
        foreach (var (ox, oy) in new[] { (0.0, 0.0), (1.0, 1.0), (-5.0, 3.0), (0.4, 1.6) })
            Assert.Equal(shoelace, PolygonIntegrals.Area(ring, ox, oy), 12);
    }

    [Fact]
    public void T3b_WindingSetsTheSign()
    {
        IReadOnlyList<EmPoint> ccw = [new EmPoint(0, 0), new EmPoint(1, 0), new EmPoint(1, 1)];
        IReadOnlyList<EmPoint> cw  = [new EmPoint(1, 1), new EmPoint(1, 0), new EmPoint(0, 0)];
        Assert.True(PolygonIntegrals.Inverse(ccw, 0.4, 0.2) > 0);
        Assert.Equal(-PolygonIntegrals.Inverse(ccw, 0.4, 0.2),
                      PolygonIntegrals.Inverse(cw, 0.4, 0.2), 12);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The slab reference — the one decomposition that is not the fan
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static void CheckAgainstSlabs(string name, IReadOnlyList<EmPoint> ring,
                                          double ox, double oy, double tol = 1e-9)
    {
        var c = PolygonIntegrals.Cores(ring, ox, oy, alongX: true, wantRadius: true);
        var d = PolygonIntegrals.Cores(ring, ox, oy, alongX: false, wantRadius: true);

        Check($"{name} 1/R",    c.Inverse,       (u, v) => 1.0 / Rho(u, v), ring, ox, oy, tol);
        Check($"{name} ln r",   c.Log,           (u, v) => Math.Log(Rho(u, v)), ring, ox, oy, tol);
        Check($"{name} r",      c.Radius,        (u, v) => Rho(u, v), ring, ox, oy, tol);
        Check($"{name} u/R",    c.InverseMoment, (u, v) => u / Rho(u, v), ring, ox, oy, tol);
        Check($"{name} v/R",    d.InverseMoment, (u, v) => v / Rho(u, v), ring, ox, oy, tol);
        Check($"{name} u ln r", c.LogMoment,     (u, v) => u * Math.Log(Rho(u, v)), ring, ox, oy, tol);
        Check($"{name} v ln r", d.LogMoment,     (u, v) => v * Math.Log(Rho(u, v)), ring, ox, oy, tol);
        Check($"{name} u r",    c.RadiusMoment,  (u, v) => u * Rho(u, v), ring, ox, oy, tol);
        Check($"{name} v r",    d.RadiusMoment,  (u, v) => v * Rho(u, v), ring, ox, oy, tol);

        static double Rho(double u, double v) => double.Hypot(u, v);
    }

    private static void Check(string name, double closed, Func<double, double, double> f,
                              IReadOnlyList<EmPoint> ring, double ox, double oy, double tol)
    {
        double r     = Slabs(f, ring, ox, oy);
        double scale = Slabs((u, v) => Math.Abs(f(u, v)), ring, ox, oy);
        Assert.True(Math.Abs(closed - r) <= tol * scale,
            $"{name}: closed {closed:G17} vs slabs {r:G17}, |Δ|/∫|f| {Math.Abs(closed - r) / scale:E3}");
    }

    /// <summary>
    /// ∫∫ f over the (convex) ring, in the observation point's own frame, by x-slabs with geometric
    /// grading toward the singular point in BOTH axes. The polygon enters only through the chord
    /// <see cref="YRange"/> returns, so this shares no expression with the closed form.
    /// </summary>
    private static double Slabs(Func<double, double, double> f,
                                IReadOnlyList<EmPoint> ring, double ox, double oy)
    {
        var xs = new List<double>();
        foreach (var p in ring) xs.Add(p.X - ox);
        double lo = xs.Min(), hi = xs.Max();
        if (lo < 0 && hi > 0) xs.Add(0.0);
        xs.Sort();

        double total = 0;
        for (int i = 0; i + 1 < xs.Count; i++)
        {
            double a = xs[i], b = xs[i + 1];
            if (!(b - a > 1e-15 * Math.Max(1.0, hi - lo))) continue;
            foreach (var (pa, pb) in Graded(a, b))
                total += SlabPanel(f, pa, pb, ring, ox, oy);
        }
        return total;
    }

    /// <summary>
    /// Panel edges over [a, b], graded geometrically toward whichever end is the singular point
    /// (coordinate 0) and one panel otherwise. The innermost strip — from 0 out to
    /// <c>|span|·Shrink^Levels</c> — is OMITTED rather than integrated: it contributes
    /// O(ε·ln(1/ε)) ≈ 1e-11 of the answer, while sampling 1/ρ inside it would contaminate the
    /// reference outright. RectangleIntegralTests' own reference makes the same trade.
    /// </summary>
    private static IEnumerable<(double, double)> Graded(double a, double b)
    {
        if (a != 0 && b != 0) { yield return (a, b); yield break; }
        if (a == 0 && b == 0) yield break;

        double span = a == 0 ? b : a;                  // the non-zero end
        double prev = span * Math.Pow(Shrink, Levels); // the innermost edge, not 0
        for (int k = Levels - 1; k >= 0; k--)
        {
            double e = span * Math.Pow(Shrink, k);
            yield return a == 0 ? (prev, e) : (e, prev);
            prev = e;
        }
    }

    private static double SlabPanel(Func<double, double, double> f, double a, double b,
                                    IReadOnlyList<EmPoint> ring, double ox, double oy)
    {
        if (!(Math.Abs(b - a) > 0)) return 0;
        var (nodes, w) = Quadrature.Nodes(PanelN);
        double h = 0.5 * (b - a), m = 0.5 * (a + b);
        double s = 0;
        for (int i = 0; i < PanelN; i++)
        {
            double u = m + h * nodes[i];
            var (y0, y1) = YRange(ring, u + ox, oy);
            if (!(y1 > y0)) continue;
            s += w[i] * Line(f, u, y0, y1);
        }
        return s * h;
    }

    /// <summary>
    /// <b>∫ f(u, v) dv over the chord, under the substitution v = |u|·sinh w.</b>
    ///
    /// <para>This replaced a graded rule, and the reason is worth the sentence because <b>the ORACLE
    /// was wrong first</b> — the tenth time in this area. Grading toward v = 0 resolves the
    /// singularity only when the chord CONTAINS it; on a triangle whose oblique side passes through
    /// the observation point the chord's near end approaches the singular point without ever reaching
    /// it, so the peak sat at a panel END and the reference read 4.7e-4 from a closed form that was
    /// right. Under v = |u|·sinh w the whole 1/ρ structure becomes ρ = |u|·cosh w and dv = |u|·cosh w
    /// dw, so ∫dv/ρ is exactly ∫dw — the integrand for the worst of the six is a CONSTANT, and the
    /// others are smooth in w at every u. No grading, no split, no panel end anywhere near the
    /// singularity.</para>
    /// </summary>
    private static double Line(Func<double, double, double> f, double u, double y0, double y1)
    {
        double a = Math.Abs(u);
        if (!(a > 0)) return 0;                      // the graded x panels never reach u = 0

        double w0 = Math.Asinh(y0 / a), w1 = Math.Asinh(y1 / a);
        int panels = Math.Max(1, (int)Math.Ceiling((w1 - w0) / 1.5));
        var (nodes, wt) = Quadrature.Nodes(PanelN);

        double total = 0;
        for (int p = 0; p < panels; p++)
        {
            double pa = w0 + (w1 - w0) * p / panels;
            double pb = w0 + (w1 - w0) * (p + 1) / panels;
            double h = 0.5 * (pb - pa), m = 0.5 * (pa + pb);
            double s = 0;
            for (int j = 0; j < PanelN; j++)
            {
                double w = m + h * nodes[j];
                s += wt[j] * f(u, a * Math.Sinh(w)) * a * Math.Cosh(w);
            }
            total += s * h;
        }
        return total;
    }

    /// <summary>The polygon's chord on the vertical line x = <paramref name="x"/>, in the observation
    /// point's frame. Convex, so there are exactly two crossings.</summary>
    private static (double Lo, double Hi) YRange(IReadOnlyList<EmPoint> ring, double x, double oy)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0, n = ring.Count, j = n - 1; i < n; j = i++)
        {
            double ax = ring[j].X, ay = ring[j].Y, bx = ring[i].X, by = ring[i].Y;
            if (ax > x == bx > x) continue;
            double t = (x - ax) / (bx - ax);
            double y = ay + t * (by - ay) - oy;
            lo = Math.Min(lo, y);
            hi = Math.Max(hi, y);
        }
        return (lo, hi);
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────────

    private static EmPoint[] Rect(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static void Near(string what, double a, double b, double rel)
        => Near(what, a, b, Math.Max(Math.Abs(a), Math.Abs(b)), rel);

    private static void Near(string what, double a, double b, double scale, double rel)
        => Assert.True(Math.Abs(a - b) <= rel * Math.Max(scale, 1e-300),
            $"{what}: polygon {a:G17} vs rectangle {b:G17}, |Δ|/scale {Math.Abs(a - b) / scale:E3}");
}
