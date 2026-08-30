// ================================================================
//  StabilityAndPassivityTests.cs
//
//  brief-stability-passivity-touchstone.md:
//   • R-stb-2 — every stability entry point normalizes to a UNIFORM REAL reference first.
//     This repairs a real defect: NormalizedS2Port's `if (forceZ0Real) { if (forceZ0Real) …
//     else … }` left the else unreachable, so the `false` callers (μ, μ′, both circle
//     functions) renormalized NOTHING while StabilityK/MaxGain used the `true` default.
//     On a complex-Z0 network the two groups therefore disagreed.
//   • R-stb-6 — passivity as σ_max(S), defined for any N (not 2-port-limited).
//
//  NOTE: this project is NOT in circuitRF.slnx, so the repo's plain `dotnet test` gate does
//  not run it. Run it explicitly when touching RfCore:
//      dotnet test RfCore/tests/RfCore.Tests/RfCore.Tests.csproj
// ================================================================

using System;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public class StabilityAndPassivityTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A conditionally-stable-ish 2-port with real gain — concrete, not degenerate.</summary>
    private static Mat<Complex> Amp2Port()
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.60, -0.30);
        m[0, 1] = new Complex(0.05,  0.02);
        m[1, 0] = new Complex(3.20,  1.10);
        m[1, 1] = new Complex(0.45, -0.25);
        return m;
    }

    private static SNP Snp2Port(Mat<Complex> m, Complex z0) =>
        new([1e9], [m], MatrixType.S, MatrixFormat.RI, z0);

    /// <summary>Hand-renormalize to a uniform REAL reference, then call the per-matrix overload —
    /// the independent oracle for what the SNP entry points must now agree with.</summary>
    private static Mat<Complex> ToUniformReal(Mat<Complex> m, Complex z0)
    {
        int n = m.RowCount;
        var oldZ = new Complex[n]; var newZ = new Complex[n];
        for (int i = 0; i < n; i++) { oldZ[i] = z0; newZ[i] = new Complex(z0.Real, 0.0); }
        return RFNetwork.SToS(m, oldZ, newZ);
    }

    // ── R-stb-2: complex Z0 — μ/μ′ now agree with the hand-renormalized reference ────────

    [Fact]
    public void ComplexZ0_MuAndMuPrime_MatchHandRenormalizedUniformRealReference()
    {
        var m  = Amp2Port();
        var z0 = new Complex(50.0, 12.0);          // complex reference — the case that was broken
        var snp = Snp2Port(m, z0);

        var expected = ToUniformReal(m, z0);

        Assert.Equal(RFNetwork.StabilityMu(expected),      RFNetwork.StabilityMu(snp)[0],      9);
        Assert.Equal(RFNetwork.StabilityMuPrime(expected), RFNetwork.StabilityMuPrime(snp)[0], 9);
    }

    /// <summary>
    /// The defect's signature: against a COMPLEX reference the un-renormalized matrix gives a
    /// materially different μ. Pins that the renormalization is doing real work here, so a future
    /// "simplification" back to no-renorm cannot pass silently.
    /// </summary>
    [Fact]
    public void ComplexZ0_RenormalizationMateriallyChangesMu_NotANoOp()
    {
        var m  = Amp2Port();
        var z0 = new Complex(50.0, 12.0);

        double raw   = RFNetwork.StabilityMu(m);                    // pre-fix behaviour
        double fixed_ = RFNetwork.StabilityMu(Snp2Port(m, z0))[0];  // post-fix behaviour

        Assert.True(Math.Abs(raw - fixed_) > 1e-6,
            $"renormalization must change μ for a complex reference (raw={raw}, renormalized={fixed_})");
    }

    // ── R-stb-2: real Z0 stays bit-identical (the no-regression guarantee) ───────────────

    [Theory]
    [InlineData(50.0)]
    [InlineData(75.0)]
    public void RealZ0_IsAnExactIdentity_SnpPathEqualsRawMatrixPath(double z0Real)
    {
        var m   = Amp2Port();
        var snp = Snp2Port(m, new Complex(z0Real, 0.0));

        // No renormalization is performed for a real reference, so these must agree EXACTLY —
        // this is what keeps every pre-existing real-Z0 result unchanged by the repair.
        Assert.Equal(RFNetwork.StabilityMu(m),      RFNetwork.StabilityMu(snp)[0]);
        Assert.Equal(RFNetwork.StabilityMuPrime(m), RFNetwork.StabilityMuPrime(snp)[0]);
        Assert.Equal(RFNetwork.MaxGain(m),          RFNetwork.MaxGain(snp)[0]);
    }

    // ── R-stb-2: μ/μ′ and K/MaxGain now share ONE reference convention ───────────────────

    [Fact]
    public void ComplexZ0_AllStabilityEntryPoints_UseTheSameUniformRealReference()
    {
        var m   = Amp2Port();
        var z0  = new Complex(50.0, -20.0);
        var snp = Snp2Port(m, z0);
        var expected = ToUniformReal(m, z0);

        // Before the repair μ/μ′/circles used the raw complex-referenced matrix while K/MaxGain
        // used the renormalized one — two conventions inside one "shared" implementation.
        var (kExp, _, _, dExp, _) = RFNetwork.StabilityK(expected);
        var (kAct, _, _, dAct, _) = RFNetwork.StabilityK(snp);

        Assert.Equal(kExp, kAct[0], 9);
        Assert.Equal(dExp, dAct[0], 9);
        Assert.Equal(RFNetwork.MaxGain(expected), RFNetwork.MaxGain(snp)[0], 9);

        // Circles exist only in SNP form; assert they moved onto the renormalized matrix by
        // comparing against an SNP built directly FROM that matrix at a real reference.
        var (cAct, rAct) = RFNetwork.StabilityCirclesLoad(snp);
        var (cRef, rRef) = RFNetwork.StabilityCirclesLoad(
            Snp2Port(expected, new Complex(z0.Real, 0.0)));
        Assert.Equal(cRef[0].Real,      cAct[0].Real,      9);
        Assert.Equal(cRef[0].Imaginary, cAct[0].Imaginary, 9);
        Assert.Equal(rRef[0],           rAct[0],           9);
    }

    // ── R-stb-6: passivity = σ_max(S) ───────────────────────────────────────────────────

    [Fact]
    public void Passivity_PassiveNetwork_SigmaMaxAtMostOne()
    {
        // Ideal matched 3 dB attenuator: reciprocal, lossy, unambiguously passive.
        double a = Math.Pow(10.0, -3.0 / 20.0);
        var m = new Mat<Complex>(2, 2);
        m[0, 1] = new Complex(a, 0); m[1, 0] = new Complex(a, 0);

        Assert.True(RFNetwork.Passivity(m) <= 1.0 + 1e-12);
    }

    [Fact]
    public void Passivity_ActiveNetwork_SigmaMaxExceedsOne()
    {
        Assert.True(RFNetwork.Passivity(Amp2Port()) > 1.0);   // |S21| ≈ 3.4 — clearly active
    }

    [Fact]
    public void Passivity_IdentityMatrix_IsExactlyOne_TheBoundary()
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = Complex.One; m[1, 1] = Complex.One;
        Assert.Equal(1.0, RFNetwork.Passivity(m), 12);
    }

    /// <summary>Not 2-port-limited — the whole point of R-stb-6 versus μ/μ′/K.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    public void Passivity_IsDefinedForAnyN_NotJustTwoPort(int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = new Complex(0.5, 0.0);
        Assert.Equal(0.5, RFNetwork.Passivity(m), 12);
    }

    [Fact]
    public void Passivity_OnePort_DegeneratesToReflectionMagnitude()
    {
        var m = new Mat<Complex>(1, 1);
        m[0, 0] = new Complex(0.6, -0.8);          // |S11| = 1.0 exactly
        Assert.Equal(1.0, RFNetwork.Passivity(m), 12);
    }

    [Fact]
    public void Passivity_NonSquare_Throws()
    {
        Assert.Throws<ArgumentException>(() => RFNetwork.Passivity(new Mat<Complex>(2, 3)));
    }

    // ── R-stb-6 + R-stb-2: passivity also needs the uniform-real reference ───────────────

    [Fact]
    public void Passivity_ComplexZ0Snp_RenormalizesBeforeTesting()
    {
        var m   = Amp2Port();
        var z0  = new Complex(50.0, 15.0);
        var expected = ToUniformReal(m, z0);

        Assert.Equal(RFNetwork.Passivity(expected), RFNetwork.Passivity(Snp2Port(m, z0))[0], 9);
    }

    [Fact]
    public void Passivity_RealZ0Snp_IsExactIdentity()
    {
        var m = Amp2Port();
        Assert.Equal(RFNetwork.Passivity(m), RFNetwork.Passivity(Snp2Port(m, new Complex(50, 0)))[0]);
    }

    // ── MAG/MSG is a POWER gain: dB is 10·log10, not 20 ──────────────────────────────────
    //
    //  Nothing pinned the ABSOLUTE dB scale until this test: every other MaxGain assertion in
    //  the repo compares MaxGain to MaxGain (SNP path vs matrix path, renormalized vs not), and
    //  a factor that is wrong in both halves cancels. It was wrong in both halves — MaxGain
    //  returned 20·log10 of a power ratio, so every MAG/MSG shown was exactly 2× its true dB
    //  value. The oracle below is independent of the formula under test.

    /// <summary>
    /// The unilateral limit is an exact, external oracle. With S11 = S22 = 0 and S12 → 0 the
    /// two-port is matched at both ends and unilateral, so its maximum available gain IS its
    /// transducer power gain |S21|² — a quantity with no reference to K, to |S21/S12|, or to
    /// anything else MaxGain computes. MAG in dB must therefore equal 10·log10(|S21|²).
    /// </summary>
    [Theory]
    [InlineData(3.0, 1e-4)]
    [InlineData(3.0, 1e-5)]
    [InlineData(0.5, 1e-5)]   // |S21| < 1: a lossy unilateral two-port, MAG negative in dB
    public void MaxGain_UnilateralMatchedTwoPort_IsTransducerPowerGainInDb(double s21, double s12)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = Complex.Zero;
        m[0, 1] = new Complex(s12, 0.0);
        m[1, 0] = new Complex(s21, 0.0);
        m[1, 1] = Complex.Zero;

        double expectedDb = 10.0 * Math.Log10(s21 * s21);   // = 20·log10(|S21|)

        // Tolerance is set by how far S12 is from zero (and, at S12 = 1e-5, by the cancellation
        // in K − √(K²−1) at K ≈ 1.7e4) — not by MaxGain. It is not a tight limit: 4 places still
        // sits three orders of magnitude inside the 2× error this guards against, which would
        // read ~19.08 dB against an expected 9.54.
        Assert.Equal(expectedDb, RFNetwork.MaxGain(m), 4);
    }

    /// <summary>
    /// The same scale check on a realistic conditionally-stable amplifier, so the guard is not
    /// only anchored at a degenerate unilateral corner: MAG/MSG in dB is 10·log10 of the linear
    /// value the header formula defines.
    /// </summary>
    [Fact]
    public void MaxGain_ConditionallyStableAmp_IsTenLog10OfTheLinearFormula()
    {
        var m = Amp2Port();
        var (k, _, _, _, _) = RFNetwork.StabilityK(m);

        double ratio  = m[1, 0].Magnitude / m[0, 1].Magnitude;
        double linear = k >= 1.0 ? ratio * (k - Math.Sqrt(k * k - 1.0)) : ratio;

        Assert.Equal(10.0 * Math.Log10(linear), RFNetwork.MaxGain(m), 9);
    }
}
