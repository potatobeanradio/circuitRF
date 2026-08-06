using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Tier 0 — the integrals, no solver.</b> R-mom-16: validate the charge solver's building blocks
/// against exact closed forms and independent quadrature <i>before</i> comparing anything to
/// Hammerstad-Jensen. H-J is itself an empirical fit with ~0.2–1% error, so a ±2% agreement against
/// it can hide a real defect and a disagreement tells you nothing about which of five stages is
/// wrong.
/// </summary>
public class Kernel2DIntegralTests
{
    private const double TwoPiEps0 = 2.0 * Math.PI * EmConstants.Eps0;

    /// <summary>Φ = ∫₀ᴸ ln r ds by quadrature; P = −Φ/(2πε₀).</summary>
    private static double PotentialByQuadrature(EmPoint a, EmPoint b, EmPoint p, int n = 200)
    {
        var d = b - a;
        double len = d.Norm;
        var u = d * (1.0 / len);
        double phi = Quadrature.Integrate(s =>
        {
            var q = a + u * s;
            return Math.Log((p - q).Norm);
        }, 0, len, n);
        return -phi / TwoPiEps0;
    }

    [Fact]
    public void T0_1_Potential_MatchesQuadrature_ForRandomSegmentObservationPairs()
    {
        var rng = new Random(20260804);
        double worst = 0;

        for (int trial = 0; trial < 300; trial++)
        {
            var a = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            var b = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            if ((b - a).Norm < 0.05) continue;

            var p = new EmPoint(rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3);
            // Stay off the segment itself: the self term has its own test below.
            if (DistanceToSegment(a, b, p) < 0.05 * (b - a).Norm) continue;

            double got = Kernel2D.Potential(a, b, p);
            double want = PotentialByQuadrature(a, b, p);
            double rel = Math.Abs(got - want) / Math.Max(Math.Abs(want), 1e-30);
            worst = Math.Max(worst, rel);
        }

        Assert.True(worst < 1e-10, $"worst relative error {worst:E3} exceeds 1e-10");
    }

    /// <summary>
    /// R-mom-5's guard. The field kernel is checked against a central finite difference of the
    /// potential kernel rather than trusted, because getting the self-field bookkeeping wrong
    /// double-counts it and the solver then converges smoothly to the wrong answer.
    /// </summary>
    [Fact]
    public void T0_2_Field_MatchesCentralDifferenceOfPotential()
    {
        var rng = new Random(4242);
        double worst = 0;

        for (int trial = 0; trial < 300; trial++)
        {
            var a = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            var b = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            double len = (b - a).Norm;
            if (len < 0.2) continue;

            var p = new EmPoint(rng.NextDouble() * 6 - 3, rng.NextDouble() * 6 - 3);
            double dist = DistanceToSegment(a, b, p);
            if (dist < 0.2 * len) continue;

            double hStep = 1e-5 * dist;
            // φ = −(σ/2πε₀)Φ and P = −Φ/(2πε₀), so E = −∇φ = −∇(σ·P) per unit σ.
            double dpdx = (Kernel2D.Potential(a, b, new EmPoint(p.X + hStep, p.Y)) -
                           Kernel2D.Potential(a, b, new EmPoint(p.X - hStep, p.Y))) / (2 * hStep);
            double dpdy = (Kernel2D.Potential(a, b, new EmPoint(p.X, p.Y + hStep)) -
                           Kernel2D.Potential(a, b, new EmPoint(p.X, p.Y - hStep))) / (2 * hStep);

            var e = Kernel2D.Field(a, b, p);
            double scale = Math.Max(Math.Sqrt(dpdx * dpdx + dpdy * dpdy), 1e-30);
            worst = Math.Max(worst, Math.Sqrt((e.X + dpdx) * (e.X + dpdx) + (e.Y + dpdy) * (e.Y + dpdy)) / scale);
        }

        Assert.True(worst < 1e-7, $"worst relative error {worst:E3} exceeds 1e-7");
    }

    [Fact]
    public void T0_3_FarField_TendsToLineChargeLimit()
    {
        var a = new EmPoint(-0.5, 0);
        var b = new EmPoint(+0.5, 0);
        double lambda = 1.0;   // σ = 1 C/m² × L = 1 m ⇒ λ = 1 C/m

        foreach (double d in new[] { 1e3, 1e4, 1e5, 1e6 })
        {
            var e = Kernel2D.Field(a, b, new EmPoint(0, d));
            double want = lambda / (TwoPiEps0 * d);
            double rel = Math.Abs(e.Norm - want) / want;
            Assert.True(rel < 10.0 / (d * d), $"d = {d:G3}: |E| = {e.Norm:E6}, want {want:E6} (rel {rel:E3})");
            Assert.True(e.Y > 0 && Math.Abs(e.X) < 1e-12 * want, "field of a symmetric segment must point straight out");
        }
    }

    [Fact]
    public void T0_4_SelfPotential_MatchesSingularQuadrature()
    {
        foreach (double len in new[] { 1e-6, 1e-3, 0.7, 3.0, 1200.0 })
        {
            // ∫₀ᴸ ln|L/2 − s| ds, split at the singularity and integrated with geometrically
            // graded panels toward it — analytic on every panel, so this converges to round-off.
            double half = 0.5 * len;
            double phi = Quadrature.IntegrateLogSingularAt(t => Math.Log(t), 0, half)
                       + Quadrature.IntegrateLogSingularAt(t => Math.Log(t), 0, half);
            double want = -phi / TwoPiEps0;

            double got = Kernel2D.SelfPotential(len);
            Assert.Equal(want, got, Math.Abs(want) * 1e-12);

            // And the general expression must reduce to it at the midpoint — no special case.
            var a = new EmPoint(0, 0);
            var b = new EmPoint(len, 0);
            double viaGeneral = Kernel2D.Potential(a, b, new EmPoint(half, 0));
            Assert.Equal(got, viaGeneral, Math.Abs(got) * 1e-12);
        }
    }

    [Fact]
    public void T0_5_Field_IsInvariantUnderReversingTheSegment()
    {
        var rng = new Random(99);
        for (int i = 0; i < 200; i++)
        {
            var a = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            var b = new EmPoint(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            if ((b - a).Norm < 0.1) continue;
            var p = new EmPoint(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            if (DistanceToSegment(a, b, p) < 0.1) continue;

            var e1 = Kernel2D.Field(a, b, p);
            var e2 = Kernel2D.Field(b, a, p);
            Assert.Equal(e1.X, e2.X, Math.Abs(e1.X) * 1e-12 + 1e-18);
            Assert.Equal(e1.Y, e2.Y, Math.Abs(e1.Y) * 1e-12 + 1e-18);
        }
    }

    /// <summary>
    /// The R-mom-5 convention, stated as a test so nobody "fixes" it later: on the segment itself
    /// the subtended angle is π, which is the σ/(2ε₀) self-field the dielectric row has already
    /// accounted for analytically — so the kernel returns zero normal field there, not π/(2πε₀).
    /// </summary>
    [Fact]
    public void T0_6_NormalFieldOnTheSegmentItself_IsExcluded()
    {
        var a = new EmPoint(0, 0);
        var b = new EmPoint(1, 0);
        var onIt = Kernel2D.Field(a, b, new EmPoint(0.5, 0));
        Assert.Equal(0.0, onIt.Y, 1e-30);

        // Approaching from above, the true one-sided field is +σ/(2ε₀).
        var above = Kernel2D.Field(a, b, new EmPoint(0.5, 1e-9));
        Assert.Equal(1.0 / (2.0 * EmConstants.Eps0), above.Y, 1e-9 / EmConstants.Eps0);

        // Off the segment, at y = 0, the subtended angle is 0.
        var beyond = Kernel2D.Field(a, b, new EmPoint(2.0, 0));
        Assert.Equal(0.0, beyond.Y, 1e-30);
        Assert.True(beyond.X > 0, "the tangential field beyond the +x end points away, along +x, for σ > 0");
    }

    [Fact]
    public void T0_7_ImageOfAGroundPlane_PutsPotentialExactlyZeroOnIt()
    {
        var a = new EmPoint(-0.3, 2.0);
        var b = new EmPoint(+0.4, 2.5);
        var ground = new EmGroundPlane(0.0, double.PositiveInfinity);

        foreach (double x in new[] { -50.0, -1.0, 0.0, 0.7, 13.0 })
        {
            double p = Kernel2D.PotentialWithImage(a, b, new EmPoint(x, 0), ground);
            Assert.Equal(0.0, p, 1e-24);
        }
    }

    private static double DistanceToSegment(EmPoint a, EmPoint b, EmPoint p)
    {
        var d = b - a;
        double len2 = d.X * d.X + d.Y * d.Y;
        double t = len2 <= 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
        return (p - (a + d * t)).Norm;
    }
}
