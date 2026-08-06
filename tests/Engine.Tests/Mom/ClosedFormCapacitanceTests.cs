using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Tier 1 — conductors only, exact oracles</b>, and <b>Tier 2 — bound charge</b>. These are the
/// tests that earn §3.3; every tolerance here is a discretisation tolerance against an <i>exact</i>
/// closed form, never a modelling one. R-mom-16: they all pass before anything is compared to
/// Hammerstad-Jensen.
/// </summary>
public class ClosedFormCapacitanceTests
{
    private const double TwoPiEps0 = 2.0 * Math.PI * EmConstants.Eps0;

    // ── Tier 1 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1e-3, 3e-3)]
    [InlineData(0.5e-3, 5e-3)]
    [InlineData(2e-6, 2.4e-6)]      // a tight ratio, where ln(b/a) is small and errors amplify
    public void T1_1_Coax_MatchesTwoPiEps0OverLnBoverA(double a, double b)
    {
        var mesh = EmProblemBuilders.Coax(a, b, 240, Complex.One);
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        double want = TwoPiEps0 / Math.Log(b / a);
        Assert.Equal(want, c[0, 0].Real, want * 5e-3);
        Assert.Equal(0.0, c[0, 0].Imaginary, want * 1e-12);
        // A shielded pair: everything that leaves the inner lands on the outer.
        Assert.Equal(-want, c[1, 0].Real, want * 5e-3);
    }

    /// <summary>This is what tests R-mom-7's image: no second conductor is meshed at all.</summary>
    [Theory]
    [InlineData(0.5e-3, 5e-3)]
    [InlineData(0.1e-3, 10e-3)]
    [InlineData(1e-3, 1.5e-3)]      // close to the plane, where the image is doing the most work
    public void T1_2_WireOverGround_MatchesTwoPiEps0OverAcosh(double a, double h)
    {
        var mesh = EmProblemBuilders.WireOverGround(a, h, 240);
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        double want = TwoPiEps0 / Acosh(h / a);
        Assert.Equal(want, c[0, 0].Real, want * 5e-3);
    }

    [Theory]
    [InlineData(0.5e-3, 5e-3)]
    [InlineData(0.2e-3, 20e-3)]
    public void T1_3_TwoParallelWires_MatchesPiEps0OverAcosh(double a, double d)
    {
        var mesh = EmProblemBuilders.TwoWires(a, d, 240);
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        // Two isolated conductors in 2D have no well-defined capacitance to infinity — the log
        // kernel's reference length leaks into C₁₁ and C₁₂ individually. The DIFFERENTIAL
        // combination is what is physical and unit-independent: C_odd = ½(C₁₁ − C₁₂).
        double got = 0.5 * (c[0, 0].Real - c[0, 1].Real);
        double want = Math.PI * EmConstants.Eps0 / Acosh(0.5 * d / a);
        Assert.Equal(want, got, want * 5e-3);
    }

    // ── Tier C1 (L7b) — the EXACT off-diagonal oracle, extended ───────────────────────────────
    //
    // R-mom-16 in the coupled setting: validate the off-diagonals against an exact closed form
    // BEFORE anything is compared to an empirical coupled-microstrip fit. T1_3 above already
    // consumed c[0,1]; these extend the same geometry to the full symmetrised 2×2, which costs
    // almost nothing and is the only oracle in L7b's ladder that is exact.

    /// <summary>
    /// <b>Tier C1.</b> Two identical wires side by side are mirror-symmetric, so the exact [C] has
    /// C₁₁ = C₂₂ — and the SYMMETRISED off-diagonal reproduces the same exact C_odd the raw one
    /// does. Symmetrising must not move the physical answer; it only removes the collocation
    /// residual, which is what R-cpl-7 claims and this pins.
    /// </summary>
    [Theory]
    [InlineData(0.5e-3, 5e-3)]
    [InlineData(0.2e-3, 20e-3)]
    public void TC1_1_TwoParallelWires_SymmetrisedMatrixIsSymmetricAndReproducesCodd(double a, double d)
    {
        var c = ChargeSolver.MaxwellCapacitance(EmProblemBuilders.TwoWires(a, d, 240));

        // Geometric symmetry: identical wires ⇒ identical self-capacitance.
        Assert.Equal(c[0, 0].Real, c[1, 1].Real, Math.Abs(c[0, 0].Real) * 5e-3);

        var sym = ModalDecomposition.Symmetrise(c);
        Assert.Equal(sym[0, 1].Real, sym[1, 0].Real, Math.Abs(sym[0, 1].Real) * 1e-12);

        double want = Math.PI * EmConstants.Eps0 / Acosh(0.5 * d / a);
        double got  = 0.5 * (sym[0, 0].Real - sym[0, 1].Real);
        Assert.Equal(want, got, want * 5e-3);
    }

    /// <summary>
    /// <b>Tier C1, and a finding worth pinning.</b> On THIS geometry R-cpl-7's residual is
    /// essentially zero, and that is not luck: two identical circles discretised by the same uniform
    /// template are segment-for-segment mirror images, so the mutual block satisfies P_ij = P_ji
    /// exactly and collocation produces a symmetric matrix after all.
    ///
    /// <para>The residual the engine half measured at ~3% therefore comes from the <b>mesher</b>
    /// making the two conductors' discretisations differ (edge grading, interface segments), not
    /// from point collocation as such. Its behaviour under refinement is pinned on that path instead
    /// — see <c>CoupledLineTests.TC1_ResidualFallsUnderMeshRefinement</c>.</para>
    /// </summary>
    [Fact]
    public void TC1_2_IdenticallyDiscretisedConductors_LeaveNoCollocationResidual()
    {
        var c = ChargeSolver.MaxwellCapacitance(EmProblemBuilders.TwoWires(0.5e-3, 5e-3, 240));
        Assert.True(ModalDecomposition.AsymmetryResidual(c) < 1e-9,
            "a mirror-symmetric mesh over identical conductors should be symmetric to round-off");
    }

    [Fact]
    public void T1_4_CoaxConvergesMonotonicallyUnderRefinement()
    {
        const double a = 1e-3, b = 3e-3;
        double want = TwoPiEps0 / Math.Log(b / a);

        double prevErr = double.MaxValue;
        foreach (int n in new[] { 30, 60, 120, 240 })
        {
            var c = ChargeSolver.MaxwellCapacitance(EmProblemBuilders.Coax(a, b, n, Complex.One));
            double err = Math.Abs(c[0, 0].Real - want) / want;
            Assert.True(err < prevErr, $"n = {n}: error {err:E3} did not improve on {prevErr:E3}");
            prevErr = err;
        }
        Assert.True(prevErr < 1e-3, $"finest mesh still off by {prevErr:E3}");
    }

    // ── Tier 2 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2.2)]
    [InlineData(4.4)]
    [InlineData(12.9)]
    public void T2_1_FullyFilledCoax_ScalesExactlyWithEpsR(double epsR)
    {
        const double a = 1e-3, b = 3e-3;
        var mesh = EmProblemBuilders.Coax(a, b, 240, new Complex(epsR, 0));
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        double want = epsR * TwoPiEps0 / Math.Log(b / a);
        Assert.Equal(want, c[0, 0].Real, want * 5e-3);
    }

    /// <summary>
    /// The only cheap closed form that genuinely exercises a dielectric interface:
    /// C = 2πε₀ / [ ln(r_m/a)/ε₁ + ln(b/r_m)/ε₂ ].
    /// </summary>
    [Theory]
    [InlineData(4.4, 1.0)]
    [InlineData(1.0, 4.4)]
    [InlineData(9.8, 2.2)]
    [InlineData(2.2, 9.8)]
    public void T2_2_TwoLayerCoax_MatchesTheSeriesLogForm(double eps1, double eps2)
    {
        const double a = 1e-3, rm = 2e-3, b = 4e-3;
        var mesh = EmProblemBuilders.TwoLayerCoax(a, rm, b, new Complex(eps1, 0), new Complex(eps2, 0), 360);
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        double want = TwoPiEps0 / (Math.Log(rm / a) / eps1 + Math.Log(b / rm) / eps2);
        Assert.Equal(want, c[0, 0].Real, want * 1e-2);
        Assert.Equal(0.0, c[0, 0].Imaginary, want * 1e-12);
    }

    [Fact]
    public void T2_3_AirEverywhere_MakesCEqualC0AndTheImaginaryPartVanish()
    {
        const double a = 1e-3, rm = 2e-3, b = 4e-3;
        var mesh = EmProblemBuilders.TwoLayerCoax(a, rm, b, Complex.One, Complex.One, 180);

        var c  = ChargeSolver.MaxwellCapacitance(mesh);
        var c0 = ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh));

        Assert.Equal(c0[0, 0].Real, c[0, 0].Real, Math.Abs(c0[0, 0].Real) * 1e-12);
        Assert.Equal(0.0, c[0, 0].Imaginary, Math.Abs(c0[0, 0].Real) * 1e-12);
    }

    /// <summary>
    /// R-mom-6 validated <i>exactly</i> rather than by plausibility: a uniformly-filled line with
    /// ε* = ε_r(1 − j·tanδ) has C_complex = C·(1 − j·tanδ) in closed form, because the whole
    /// problem scales linearly with the fill permittivity.
    /// </summary>
    [Theory]
    [InlineData(4.4, 0.02)]
    [InlineData(9.8, 0.001)]
    [InlineData(2.2, 0.2)]
    public void T2_4_LossyFill_GivesCTimesOneMinusJTanD(double epsR, double tanD)
    {
        const double a = 1e-3, b = 3e-3;
        var eps = new EmMaterial(epsR, tanD).EpsComplex;
        var mesh = EmProblemBuilders.Coax(a, b, 240, eps);
        var c = ChargeSolver.MaxwellCapacitance(mesh);

        double cReal = epsR * TwoPiEps0 / Math.Log(b / a);
        Assert.Equal(cReal, c[0, 0].Real, cReal * 5e-3);
        Assert.Equal(-cReal * tanD, c[0, 0].Imaginary, cReal * tanD * 5e-3);

        // G = −ω·Im(C) and it is proportional to ω for a constant tanδ — falling out of the
        // formulation rather than being asserted.
        double omega = 2 * Math.PI * 1e9;
        double g = -omega * c[0, 0].Imaginary;
        Assert.Equal(omega * cReal * tanD, g, omega * cReal * tanD * 5e-3);
    }

    /// <summary>
    /// The sign check of §3.3, isolated: a dielectric half-space below an interface, driven by a
    /// positive charge above it, must acquire NEGATIVE bound charge — the dielectric is attracted,
    /// matching the textbook image q′ = −q(ε_r−1)/(ε_r+1). A sign error here converges smoothly to
    /// the wrong answer, so it is pinned on its own rather than only through a capacitance.
    /// </summary>
    [Fact]
    public void T2_5_BoundChargeSign_MatchesTheDielectricImage()
    {
        const double epsR = 4.0;
        var below = new Complex(epsR, 0);
        var above = Complex.One;
        var k = (below - above) / (below + above);
        Assert.True(k.Real > 0, "K = (ε₁−ε₂)/(ε₁+ε₂) is positive for a dielectric below air");

        // A unit positive line charge 1 m above the interface, at the origin's x.
        var src = new EmSegment(new EmPoint(-0.005, 1.0), new EmPoint(0.005, 1.0), new EmPoint(0, 1),
                                EmSegmentKind.Conductor, 0, -1, Complex.One, Complex.Zero);
        var eAvg = Kernel2D.Field(src.A, src.B, new EmPoint(0, 0)).Dot(new EmPoint(0, 1));
        Assert.True(eAvg < 0, "the field just below a positive charge points downward");

        var sigmaB = 2.0 * EmConstants.Eps0 * k * eAvg;
        Assert.True(sigmaB.Real < 0, $"bound charge must be negative, got {sigmaB.Real:E3}");
    }

    private static double Acosh(double x) => Math.Log(x + Math.Sqrt(x * x - 1));
}
