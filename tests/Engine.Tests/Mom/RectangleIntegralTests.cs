// L8c — Tier 1: the closed-form inner integrals, against adaptive quadrature.
//
// R-fil-4 asks for three of them to 1e-12, at an interior point, an edge point, a corner and far
// outside — "the corner and edge cases are where a naive corner-summed antiderivative divides by
// zero". All SIX are checked here (the three pulse-weighted forms plus the three first moments the
// rooftop's linear weight needs), because the moments are exactly as easy to get subtly wrong and
// the same fixtures cover them.
//
// THE REFERENCE IS DELIBERATELY NOT THE ENGINE'S OWN QUADRATURE. Support/Quadrature.cs is a
// test-side Gauss-Legendre written for kernel A precisely so a closed form is checked against
// something that shares no code with it; the 1/R and ln r singularities are resolved by geometric
// panel refinement toward the singular line, which is exact on every panel because the integrand is
// analytic there.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class RectangleIntegralTests
{
    // ── The reference integrator ───────────────────────────────────────────────────────────────
    //
    // ∫∫ f(u,v) du dv over [x1,x2] x [y1,y2] with the observation point at the origin. The rectangle
    // is first split at u = 0 and v = 0 so that no panel STRADDLES the singular point, and each
    // resulting sub-rectangle is integrated with the singular corner (if any) at a known corner —
    // then refined geometrically toward it. Everything is done in the (u,v) frame, so "interior",
    // "edge" and "corner" observation points differ only in how many sub-rectangles survive.

    private const double Shrink = 0.1;   // decade-per-level grading toward the singular corner
    private const int    Levels = 18;    // 1e-18 of the rectangle is left unintegrated
    private const int    PanelN = 32;

    private static double Reference(Func<double, double, double> f,
                                    double x1, double x2, double y1, double y2)
    {
        double total = 0;
        foreach (var (a, b) in SplitAtZero(x1, x2))
            foreach (var (c, d) in SplitAtZero(y1, y2))
                total += Wedge(f, a, b, c, d);
        return total;
    }

    private static IEnumerable<(double, double)> SplitAtZero(double lo, double hi)
    {
        if (lo < 0 && hi > 0) { yield return (lo, 0.0); yield return (0.0, hi); }
        else if (hi > lo)     { yield return (lo, hi); }
    }

    /// <summary>
    /// One sub-rectangle that does not straddle the origin in either axis, so the only place the
    /// integrand can be singular is its corner nearest the origin. Both axes are graded toward that
    /// corner INDEPENDENTLY and the full tensor product of panels is integrated — grading them
    /// together (an L-shaped peel) is what a first attempt does and it fails on a high-aspect-ratio
    /// cell, where one axis reaches the singular scale a factor of w/h before the other. A 1 x 1e-4
    /// sliver is not a contrived fixture: it is exactly the shape of an edge-graded cell on a
    /// microstrip line, so the reference has to survive it.
    /// </summary>
    private static double Wedge(Func<double, double, double> f, double x1, double x2, double y1, double y2)
    {
        double sx = x2 <= 0 ? -1 : 1, sy = y2 <= 0 ? -1 : 1;
        double a0 = Math.Min(Math.Abs(x1), Math.Abs(x2)), a1 = Math.Max(Math.Abs(x1), Math.Abs(x2));
        double b0 = Math.Min(Math.Abs(y1), Math.Abs(y2)), b1 = Math.Max(Math.Abs(y1), Math.Abs(y2));

        double G(double u, double v) => f(sx * u, sy * v);

        var us = Breaks(a0, a1);
        var vs = Breaks(b0, b1);

        double total = 0;
        for (int i = 0; i + 1 < us.Length; i++)
            for (int j = 0; j + 1 < vs.Length; j++)
                total += Panel(G, us[i], us[i + 1], vs[j], vs[j + 1], PanelN);
        return total;
    }

    /// <summary>Panel edges from lo to hi, graded geometrically toward lo when lo is the singular
    /// point (lo = 0) and a single interval otherwise.</summary>
    private static double[] Breaks(double lo, double hi)
    {
        if (lo > 0 || !(hi > 0)) return [lo, hi];
        // The innermost edge stops at hi·1e-18 rather than at 0: the omitted strip contributes
        // O(ε·ln(hi/ε)) to ∫1/r, i.e. ~4e-17 of the answer, while INCLUDING it as a plain panel would
        // sample 1/r at ~1e19 against a weight of ~1e-18 and contaminate the result outright.
        var e = new double[Levels + 1];
        for (int k = 0; k <= Levels; k++) e[k] = hi * Math.Pow(Shrink, Levels - k);
        return e;
    }

    private static double Panel(Func<double, double, double> f,
                                double x1, double x2, double y1, double y2, int n)
    {
        if (!(x2 > x1) || !(y2 > y1)) return 0;
        var (nodes, w) = Quadrature.Nodes(n);
        double hx = 0.5 * (x2 - x1), mx = 0.5 * (x1 + x2);
        double hy = 0.5 * (y2 - y1), my = 0.5 * (y1 + y2);
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            double u = mx + hx * nodes[i], inner = 0;
            for (int j = 0; j < n; j++) inner += w[j] * f(u, my + hy * nodes[j]);
            s += w[i] * inner * hy;
        }
        return s * hx;
    }

    // ── The four observation-point placements R-fil-4 names ────────────────────────────────────
    //
    // Each entry is the rectangle in the frame whose origin IS the observation point.
    public static TheoryData<string, double, double, double, double> Placements() => new()
    {
        // interior, deliberately off-centre so a symmetry cannot hide a sign error
        { "interior",     -0.37,  0.63, -0.21,  0.79 },
        // exactly on an edge (u = 0 is the rectangle's left side)
        { "edge",          0.0,   1.0,  -0.4,   0.6  },
        // exactly at a corner
        { "corner",        0.0,   1.3,   0.0,   0.7  },
        // on an edge, with the observation point at the middle of the TOP side
        { "edge-top",     -0.5,   0.5,  -0.9,   0.0  },
        // far outside — the regime the far-field entries live in
        { "far",           7.0,   9.5,   4.0,   6.5  },
        // a sliver, to exercise the a ≪ b cancellation guards
        { "sliver",        0.0,   1.0,   0.0,   1e-4 },
    };

    private static void Check(string name,
                              Func<double, double, double, double, double> closed,
                              Func<double, double, double> integrand,
                              double x1, double x2, double y1, double y2,
                              double tol = 1e-12)
    {
        double c = closed(x1, x2, y1, y2);
        double r = Reference(integrand, x1, x2, y1, y2);

        // The scale is ∫|f|, not |∫f|. On a rectangle straddling the axis an odd moment integrates to
        // (nearly) zero and a relative tolerance against that would be a tolerance against round-off.
        double scale = Reference((u, v) => Math.Abs(integrand(u, v)), x1, x2, y1, y2);

        Assert.True(Math.Abs(c - r) <= tol * scale,
            $"{name}: closed {c:G17} vs reference {r:G17}, |Δ|/∫|f| {Math.Abs(c - r) / scale:E3}");
    }

    private static double R(double u, double v) => Math.Sqrt(u * u + v * v);

    [Theory, MemberData(nameof(Placements))]
    public void T1_1_ZerothMoment_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.Inverse, (u, v) => 1.0 / R(u, v), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_2_FirstMomentU_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.InverseMomentU, (u, v) => u / R(u, v), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_2b_FirstMomentV_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.InverseMomentV, (u, v) => v / R(u, v), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_3_LogForm_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.Log, (u, v) => Math.Log(R(u, v)), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_4_LogFirstMomentU_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.LogMomentU, (u, v) => u * Math.Log(R(u, v)), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_4b_LogFirstMomentV_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.LogMomentV, (u, v) => v * Math.Log(R(u, v)), x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_5_RadiusForm_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.Radius, R, x1, x2, y1, y2);

    [Theory, MemberData(nameof(Placements))]
    public void T1_5b_RadiusFirstMomentU_MatchesAdaptiveQuadrature(string name, double x1, double x2, double y1, double y2)
        => Check(name, RectangleIntegrals.RadiusMomentU, (u, v) => u * R(u, v), x1, x2, y1, y2);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The hand-checkable values — the ones a reader can verify without running anything
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T1_6_UnitSquareCentre_IsFourAsinhOne()
    {
        // The brief's own check: the zeroth moment at the centre of a unit square is 4·asinh(1).
        double got = RectangleIntegrals.Inverse(-0.5, 0.5, -0.5, 0.5);
        Assert.Equal(4.0 * Math.Asinh(1.0), got, 14);
        Assert.Equal(3.5254942, got, 6);
    }

    [Fact]
    public void T1_7_MeanCornerDistanceOfAUnitSquare_IsTheKnownConstant()
    {
        // ∫∫ r over the unit square from a corner, divided by the area, is the textbook mean corner
        // distance 0.765195… = [√2 + asinh(1)] / 3.
        double got = RectangleIntegrals.Radius(0, 1, 0, 1);
        Assert.Equal((Math.Sqrt(2.0) + Math.Asinh(1.0)) / 3.0, got, 14);
        Assert.Equal(0.7651957, got, 6);
    }

    [Fact]
    public void T1_8_ZerothMomentIsTranslationInvariantAndAdditive()
    {
        // Splitting a rectangle must not change the answer. This is what catches a corner-rule sign
        // error that happens to cancel at one particular placement.
        double whole = RectangleIntegrals.Inverse(-1.3, 2.1, -0.7, 1.9);
        double left  = RectangleIntegrals.Inverse(-1.3, 0.4, -0.7, 1.9);
        double right = RectangleIntegrals.Inverse(0.4, 2.1, -0.7, 1.9);
        Assert.Equal(whole, left + right, 12);
    }

    [Fact]
    public void T1_9_FirstMomentOfASymmetricRectangleIsExactlyZero()
    {
        // Odd integrand over a rectangle symmetric about u = 0. Exactly zero, not nearly: the corner
        // rule for the odd case drops the u-sign, so the two halves cancel term by term.
        Assert.Equal(0.0, RectangleIntegrals.InverseMomentU(-2.5, 2.5, 0.3, 4.1));
        Assert.Equal(0.0, RectangleIntegrals.LogMomentU(-2.5, 2.5, 0.3, 4.1));
        Assert.Equal(0.0, RectangleIntegrals.RadiusMomentU(-2.5, 2.5, 0.3, 4.1));
    }

    [Fact]
    public void T1_10_DegenerateRectanglesIntegrateToNothing()
    {
        Assert.Equal(0.0, RectangleIntegrals.Inverse(1.0, 1.0, 0.0, 3.0));
        Assert.Equal(0.0, RectangleIntegrals.Log(0.0, 2.0, 1.5, 1.5));
        Assert.Equal(0.0, RectangleIntegrals.InverseMomentU(0.0, 0.0, 0.0, 0.0));
        Assert.Equal(0.0, RectangleIntegrals.Radius(0.0, 0.0, -1.0, 1.0));
    }

    [Fact]
    public void T1_11_ScalesWithTheExpectedPowerOfLength()
    {
        // Dimensional analysis, as a test: 1/R integrates to a LENGTH, ln r to an area plus an area
        // times a log, r to a length cubed. Scaling by s multiplies them by s, s² (+ s²ln s) and s³.
        const double s = 7.3;
        Assert.Equal(s * RectangleIntegrals.Inverse(0.2, 1.1, -0.4, 0.9),
                     RectangleIntegrals.Inverse(0.2 * s, 1.1 * s, -0.4 * s, 0.9 * s), 12);

        double a  = RectangleIntegrals.Area(0.2, 1.1, -0.4, 0.9);
        Assert.Equal(s * s * (RectangleIntegrals.Log(0.2, 1.1, -0.4, 0.9) + a * Math.Log(s)),
                     RectangleIntegrals.Log(0.2 * s, 1.1 * s, -0.4 * s, 0.9 * s), 12);

        Assert.Equal(s * s * s * RectangleIntegrals.Radius(0.2, 1.1, -0.4, 0.9),
                     RectangleIntegrals.Radius(0.2 * s, 1.1 * s, -0.4 * s, 0.9 * s), 10);
    }
}
