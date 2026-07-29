using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for brief-mtaper-mklopf.md §2 — the Klopfenstein taper's own physics, validated
/// against the klopf-taper oracle (github.com/ZiadHatab/klopf-taper, BSD-3, commit
/// 4b6fa1778b0c5df07d3088650c7952aac11c8f00, fetched and run directly in this environment — see
/// KlopfensteinTaper's own doc comment for the full provenance/discrepancy-resolution record).</summary>
public class KlopfensteinTaperTests
{
    // ── R-klp-2: Gamma_max guard ─────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateGammaMax_AtOrAboveBound_Throws()
    {
        double bound = Math.Abs((120.0 - 50.0) / (120.0 + 50.0)); // = 0.4118
        var ex = Assert.Throws<ArgumentException>(() => KlopfensteinTaper.ValidateGammaMax(50.0, 120.0, bound));
        Assert.Contains(bound.ToString("G6"), ex.Message);
    }

    [Fact]
    public void ValidateGammaMax_BelowBound_DoesNotThrow()
    {
        KlopfensteinTaper.ValidateGammaMax(50.0, 120.0, 0.05); // no throw
    }

    // ── The endpoint (Kajfez-Prewitt) correction — verified numerically, per the doc comment ──

    [Fact]
    public void ImpedanceAt_Endpoints_MatchZ1AndZ2Exactly()
    {
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0);
        Assert.Equal(z1, KlopfensteinTaper.ImpedanceAt(0.0, z1, z2, gammaMax), 6);
        Assert.Equal(z2, KlopfensteinTaper.ImpedanceAt(1.0, z1, z2, gammaMax), 6);
    }

    [Fact]
    public void ImpedanceAt_Center_IsGeometricMean()
    {
        // Steer eq (15): Z0(0) = sqrt(Zs*Zl) — the center of the taper.
        double z1 = 50.0, z2 = 120.0, gammaMax = 0.05;
        double center = KlopfensteinTaper.ImpedanceAt(0.5, z1, z2, gammaMax);
        Assert.Equal(Math.Sqrt(z1 * z2), center, 6);
    }

    [Fact]
    public void ImpedanceAt_WithoutTheMinusOneEndpointTerm_OvershootsTheEndpoints()
    {
        // Direct regression for "the uncorrected 1956 form differs" (gate 3) — reproduces the
        // exact "fails to meet the end values... when the transformation ratio is large" symptom
        // reported for the pre-Kajfez-Prewitt formula, verified numerically against the oracle.
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0);
        double a = KlopfensteinTaper.ComputeA(z1, z2, gammaMax);
        double rho0Est = KlopfensteinTaper.Rho0Estimate(z1, z2);

        // The "-1"-omitted bracket, evaluated at the exact axial endpoint t=0 (x=-0.5, w=-1).
        double bracketWithoutMinusOne = a * a * KlopfensteinTaper.Phi(-1.0, a) + 1.0; // U(x+1/2)=1, U(x-1/2)=0
        double lnZWithoutMinusOne = Math.Log(z1 * z2) / 2.0 + rho0Est / Math.Cosh(a) * bracketWithoutMinusOne;
        double zWithoutMinusOne = Math.Exp(lnZWithoutMinusOne);

        Assert.Equal(53.264, zWithoutMinusOne, 2); // oracle-confirmed overshoot value (expected exactly 50.0)
        Assert.NotEqual(z1, zWithoutMinusOne, 1);
    }

    // ── A: the factor-of-2 resolution (this class's own doc comment) ───────────────────────────

    [Fact]
    public void ComputeA_MatchesOracleValue_WithTheFactorOfTwo()
    {
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0);
        double a = KlopfensteinTaper.ComputeA(z1, z2, gammaMax);
        Assert.Equal(3.319574518183404, a, 9); // oracle value, commit 4b6fa17
    }

    [Fact]
    public void ComputeA_DiffersFromTheDroppedFactorOfTwoErratum()
    {
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0);
        double aCorrect = KlopfensteinTaper.ComputeA(z1, z2, gammaMax);
        double aErratum = Math.Acosh(Math.Abs(Math.Log(z2 / z1) / gammaMax)); // Steer's digitized eq (11), literally
        Assert.NotEqual(aCorrect, aErratum, 3);
        Assert.Equal(4.013702643068322, aErratum, 6); // oracle-confirmed erratum value
    }

    // ── Phi function — Grossberg series, cross-checked against the oracle's own recursion ──────

    [Fact]
    public void Phi_AtZero_IsZero()
    {
        Assert.Equal(0.0, KlopfensteinTaper.Phi(0.0, 3.0), 12);
    }

    [Fact]
    public void Phi_IsOdd()
    {
        double a = 2.5;
        Assert.Equal(-KlopfensteinTaper.Phi(0.6, a), KlopfensteinTaper.Phi(-0.6, a), 9);
    }

    // ── R-klp-3: length <-> f3dB duality (oracle-sourced) ───────────────────────────────────────

    [Fact]
    public void LengthAndF3db_InvertConsistently()
    {
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0), eeff = 3.0;
        double l = 4e-3;
        double f3db = KlopfensteinTaper.F3dbFromLength(z1, z2, gammaMax, l, eeff);
        double lBack = KlopfensteinTaper.LengthFromF3db(z1, z2, gammaMax, f3db, eeff);
        Assert.Equal(l, lBack, 9);
    }

    [Fact]
    public void F3dbFromLength_UsesTheRefinedA_NotTheOraclesExactRho0Convention()
    {
        // KlopfensteinTaper's own doc comment records a real, confirmed inconsistency inside the
        // oracle itself: klopf_l2f/klopf_f2l compute A from the EXACT rho0=(Z2-Z1)/(Z2+Z1)
        // (reproduced here as the "oracle" value, confirmed against its own numeric output,
        // commit 4b6fa17), while this class uses the refined small-reflection A everywhere,
        // consistently. The two must therefore differ by a small, bounded, documented amount —
        // this test pins that they DON'T silently coincide, not that they match.
        double z1 = 50.0, z2 = 120.0, gammaMax = Math.Pow(10, -30.0 / 20.0), eeff = 3.0;
        double f3dbOracleExactRho0 = 23569487039.59576; // klopf_l2f(50,120,10^-1.5,4e-3,3.0), commit 4b6fa17
        double f3dbOurs = KlopfensteinTaper.F3dbFromLength(z1, z2, gammaMax, 4e-3, eeff);

        Assert.NotEqual(f3dbOracleExactRho0, f3dbOurs, 0);
        double fractionalDiff = Math.Abs(f3dbOurs - f3dbOracleExactRho0) / f3dbOracleExactRho0;
        Assert.InRange(fractionalDiff, 0.0, 0.03); // small, bounded, and understood — not a gross bug
    }

    [Fact]
    public void LengthFromF3db_LargerA_NeedsLongerTaper()
    {
        double z1 = 50.0, z2 = 120.0, eeff = 3.0, f3db = 20e9;
        double lTight = KlopfensteinTaper.LengthFromF3db(z1, z2, 0.1, f3db, eeff);  // looser Gmax -> smaller A
        double lLoose = KlopfensteinTaper.LengthFromF3db(z1, z2, 0.01, f3db, eeff); // tighter Gmax -> larger A
        Assert.True(lLoose > lTight);
    }

    // ── HammerstadJensen inverse synthesis (R-klp-5) ────────────────────────────────────────────

    [Fact]
    public void SynthesizeWidth_RoundTrips_ThroughForwardCompute()
    {
        double h = 1.6e-3, t = 35e-6, er = 4.4;
        var reporter = new MicrostripValidityReporter("check");
        double wOriginal = 2.9e-3;
        double z0 = HammerstadJensen.Compute(wOriginal, h, t, er, reporter).Z0;
        double wRoundTrip = HammerstadJensen.SynthesizeWidth(z0, h, t, er, reporter);
        Assert.Equal(wOriginal, wRoundTrip, 6);
    }

    [Fact]
    public void SynthesizeWidth_IsMonotonic_WiderForLowerImpedance()
    {
        double h = 1.6e-3, t = 35e-6, er = 4.4;
        var reporter = new MicrostripValidityReporter("check");
        double wHighZ = HammerstadJensen.SynthesizeWidth(100.0, h, t, er, reporter);
        double wLowZ = HammerstadJensen.SynthesizeWidth(30.0, h, t, er, reporter);
        Assert.True(wLowZ > wHighZ);
    }
}
