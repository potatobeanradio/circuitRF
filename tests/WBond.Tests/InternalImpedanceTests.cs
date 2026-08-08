using System.Numerics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tier 6 of brief-wbond-wba §5 — the internal impedance table.
///
/// <para><b>Tier 6 gates each asymptotic series only where that series is valid</b>, and covers the
/// q ≈ 1–4 gap with an independent evaluation. Neither series works there: at q = 2 the exact value
/// is 1.264643 while small-q gives 1.333 and large-q gives 1.296875. Tuning one fit to straddle the
/// gap and calling it validated is exactly what this tier exists to prevent.</para>
/// </summary>
public class InternalImpedanceTests
{
    // Reference values of Re(Z_int/R_dc), computed independently from the ascending series for
    // I0 and I1 of complex argument at high precision. These are the anchor for the middle band
    // where NEITHER asymptotic series is usable.
    public static TheoryData<double, double, double> ExactReferences => new()
    {
        //   q       Re(Z/Rdc)        Im(Z/Rdc)
        { 0.1,   1.0000020833,    0.0024999974 },
        { 0.5,   1.0013007286,    0.0624593558 },
        { 1.0,   1.0204923889,    0.2474419983 },
        { 2.0,   1.2646429063,    0.8704825956 },
        { 3.0,   1.7681316525,    1.4640456207 },
        { 5.0,   2.7681076007,    2.4767247881 },
        { 10.0,  5.2593018575,    4.9896275249 },
        { 20.0, 10.2546791147,    9.9950704761 },
        { 30.0, 15.2531225862,   14.9967685600 },
        { 50.0, 25.2518749443,   24.9980873573 },
    };

    /// <summary>
    /// TIER 6 — the exact evaluation against independently computed reference values, across all
    /// three regimes and both crossovers.
    /// </summary>
    [Theory, MemberData(nameof(ExactReferences))]
    public void Tier6_NormalizedZ_MatchesIndependentReferenceValues(double q, double expectedRe, double expectedIm)
    {
        var z = InternalImpedance.NormalizedZ(q);

        // 5e-7 covers the worst regime (the asymptotic band at q = 30, measured 1.6e-7) and is far
        // tighter everywhere else. The references are 10-digit values from an independent ascending
        // series for I0 and I1 of complex argument.
        Assert.Equal(expectedRe, z.Real, Math.Abs(expectedRe) * 5e-7);
        Assert.Equal(expectedIm, z.Imaginary, Math.Abs(expectedIm) * 5e-7);
    }

    /// <summary>
    /// TIER 6 — the fast regime-switching path against the branch-free continued fraction, over the
    /// whole range and across both crossovers.
    ///
    /// <para><b>This is the test that makes the large-q tier non-vacuous.</b> Above q = 25
    /// <see cref="InternalImpedance.NormalizedZ"/> IS the asymptotic expansion, so comparing it to
    /// that expansion would compare a value with itself. The continued fraction shares none of its
    /// branches.</para>
    /// </summary>
    [Theory]
    [InlineData(0.2)]
    [InlineData(0.4)]
    [InlineData(1.5)]
    [InlineData(12.0)]
    [InlineData(25.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    public void Tier6_FastPath_MatchesTheBranchFreeContinuedFraction(double q)
    {
        var fast = InternalImpedance.NormalizedZ(q);
        var exact = InternalImpedance.NormalizedZExact(q);

        double relative = Complex.Abs(fast - exact) / Complex.Abs(exact);
        Assert.True(relative < 1e-6,
            $"At q={q} the fast path and the continued fraction differ by {relative:E3} " +
            $"(fast {fast}, exact {exact}).");
    }

    /// <summary>
    /// TIER 6 — the small-q asymptote <c>1 + q⁴/48</c>, gated only where it is valid (q ≤ 0.5).
    /// </summary>
    /// <remarks>
    /// Tolerances are the MEASURED validity of the asymptote itself, which falls off as q^8:
    /// 3.5e-12 at q = 0.1, 2.3e-8 at 0.3, 1.4e-6 at 0.5. A single uniform tolerance would either
    /// hide a real error at small q or fail spuriously at 0.5.
    /// </remarks>
    [Theory]
    [InlineData(0.1, 1e-10)]
    [InlineData(0.3, 1e-7)]
    [InlineData(0.5, 3e-6)]
    public void Tier6_SmallQ_MatchesTheQuarticAsymptote(double q, double tolerance)
    {
        double actual = InternalImpedance.NormalizedZ(q).Real;
        double asymptote = InternalImpedance.SmallQResistanceAsymptote(q);

        double relative = Math.Abs(asymptote - actual) / asymptote;
        Assert.True(relative < tolerance,
            $"At q={q} the 1 + q^4/48 asymptote should hold to {tolerance:E0}; got {relative:E3}.");
    }

    /// <summary>
    /// TIER 6 — the small-q reactive asymptote <c>X/R_dc = q²/4</c>, which is the statement that the
    /// internal inductance tends to μ₀/8π per unit length.
    /// </summary>
    /// <remarks>
    /// The reactive asymptote converges far more slowly than the resistive one — as q^4 rather than
    /// q^8 — so its measured validity is 1.0e-6 at q = 0.1 against 8.4e-5 at 0.3 and 6.5e-4 at 0.5.
    /// </remarks>
    [Theory]
    [InlineData(0.1, 3e-6)]
    [InlineData(0.3, 2e-4)]
    [InlineData(0.5, 1e-3)]
    public void Tier6_SmallQ_ReactanceTendsToMu0Over8Pi(double q, double tolerance)
    {
        double actual = InternalImpedance.NormalizedZ(q).Imaginary;
        double asymptote = q * q / 4.0;

        double relative = Math.Abs(asymptote - actual) / asymptote;
        Assert.True(relative < tolerance,
            $"At q={q} the q^2/4 reactive asymptote should hold to {tolerance:E0}; got {relative:E3}.");
    }

    /// <summary>
    /// TIER 6 — the large-q asymptote <c>q/2 + ¼ + 3/(32q)</c>, gated only where it is valid (q ≥ 5).
    /// </summary>
    /// <remarks>
    /// Compared against <see cref="InternalImpedance.NormalizedZExact"/>, not against
    /// <see cref="InternalImpedance.NormalizedZ"/>: above q = 25 the latter IS this asymptote, so
    /// the test would compare a value with itself. Tolerances are the measured convergence of the
    /// series — 2.3e-4 at q = 5, 1.4e-5 at 10, 1.7e-6 at 20, 1.6e-7 at 30.
    /// </remarks>
    [Theory]
    [InlineData(5.0, 3e-4)]
    [InlineData(10.0, 2e-5)]
    [InlineData(20.0, 2e-6)]
    [InlineData(30.0, 3e-7)]
    public void Tier6_LargeQ_MatchesTheSkinAsymptote(double q, double tolerance)
    {
        double actual = InternalImpedance.NormalizedZExact(q).Real;
        double asymptote = InternalImpedance.LargeQResistanceAsymptote(q);

        double relative = Math.Abs(actual - asymptote) / asymptote;
        Assert.True(relative < tolerance,
            $"At q={q} the skin asymptote should hold to {tolerance:E0}; got {relative:E3} " +
            $"(exact {actual:F6}, asymptote {asymptote:F6}).");
    }

    /// <summary>
    /// TIER 6 — <b>the gap is real</b>: at q ≈ 1–4 neither asymptotic series is usable, which is why
    /// the exact evaluation must cover the band rather than a fit being stretched across it.
    ///
    /// <para>This test asserts that the series <i>disagree</i> there. It fails if someone widens a
    /// series' validity range on the assumption that it was merely conservative.</para>
    /// </summary>
    [Fact]
    public void Tier6_TheMiddleBand_IsCoveredByNeitherAsymptoticSeries()
    {
        const double q = 2.0;
        double exact = InternalImpedance.NormalizedZ(q).Real;

        double smallQ = InternalImpedance.SmallQResistanceAsymptote(q);
        double largeQ = InternalImpedance.LargeQResistanceAsymptote(q);

        Assert.True(Math.Abs(smallQ / exact - 1.0) > 0.04,
            $"The small-q series should be visibly wrong at q=2 (~+5.4 %); got {smallQ / exact - 1.0:P2}.");
        Assert.True(Math.Abs(largeQ / exact - 1.0) > 0.015,
            $"The large-q series should be visibly wrong at q=2 (~+2.5 %); got {largeQ / exact - 1.0:P2}.");
    }

    /// <summary>
    /// TIER 6 — continuity across both internal regime boundaries. A discontinuity at a crossover
    /// would show up in a swept simulation as a kink in R(f) that no oracle above would catch.
    /// </summary>
    [Theory]
    [InlineData(0.4)]
    [InlineData(25.0)]
    public void Tier6_RegimeCrossovers_AreContinuous(double q)
    {
        var below = InternalImpedance.NormalizedZ(q * (1.0 - 1e-9));
        var above = InternalImpedance.NormalizedZ(q * (1.0 + 1e-9));

        Assert.Equal(below.Real, above.Real, Math.Abs(below.Real) * 1e-6);
        Assert.Equal(below.Imaginary, above.Imaginary, Math.Abs(below.Imaginary) * 1e-6);
    }

    /// <summary>
    /// TIER 6 — R(f) is monotonically increasing and X_int/ω (i.e. L_int) monotonically decreasing.
    /// Both are physical requirements of skin effect and neither is implied by any point check above.
    /// </summary>
    [Fact]
    public void Tier6_ResistanceRisesAndInternalInductanceFalls_Monotonically()
    {
        double previousR = 0.0, previousL = double.MaxValue;

        for (double q = 0.05; q < 60.0; q *= 1.3)
        {
            var z = InternalImpedance.NormalizedZ(q);
            double r = z.Real;
            double lInt = q > 0 ? z.Imaginary / (q * q) : 0.25;   // X/(R_dc·ω) scales as Im/q²

            Assert.True(r > previousR, $"R/R_dc must rise with q; at q={q:F3} it was {r:F6} after {previousR:F6}.");
            Assert.True(lInt < previousL + 1e-12,
                $"Internal inductance must fall with q; at q={q:F3} the L proxy was {lInt:E4} after {previousL:E4}.");

            previousR = r;
            previousL = lInt;
        }
    }

    // ---------------------------------------------------------------- physical wiring

    /// <summary>
    /// The DC limits: R → R_dc and L_int → μ₀/8π = 50 nH/m, the classic internal inductance of a
    /// round wire.
    /// </summary>
    [Fact]
    public void PerMetre_AtDc_GivesRdcAndMu0Over8Pi()
    {
        const double radius = 12.7e-6;
        double sigma = WireMaterials.Gold.SigmaAt(85.0);

        var (r, l) = InternalImpedance.PerMetre(0.0, radius, sigma);

        Assert.Equal(InternalImpedance.DcResistancePerMetre(radius, sigma), r, 1e-9);
        Assert.Equal(InternalImpedance.Mu0 / (8.0 * Math.PI), l, 1e-15);
        Assert.Equal(5.0e-8, l, 1e-10);   // 50 nH/m
    }

    /// <summary>
    /// The q values a 0.5 mil gold wire at 85 °C actually sees across the tool's frequency range —
    /// the numbers that justify tabulating q ∈ [0, 60] (wbond.md §3.5).
    /// </summary>
    [Theory]
    [InlineData(1e8, 1.46)]
    [InlineData(1e9, 4.62)]
    [InlineData(1e10, 14.62)]
    [InlineData(4e10, 29.25)]
    public void QParameter_ForAHalfMilGoldWireAt85C_LandsInTheTabulatedRange(double frequency, double expectedQ)
    {
        const double radius = 12.7e-6;
        double sigma = WireMaterials.Gold.SigmaAt(85.0);

        double q = InternalImpedance.QParameter(frequency, radius, sigma);
        Assert.Equal(expectedQ, q, 0.02);
    }

    /// <summary>
    /// WB4b — <b>the 85 °C temperature penalty is about half as large at RF as at DC</b>, because
    /// R_ac ∝ 1/√σ once the current is confined to a skin, against R_dc ∝ 1/σ.
    ///
    /// <para>Gold: 22.1 % at DC, ~10.5 % deep in the skin regime. This is worth pinning because a
    /// user comparing against a room-temperature hand calculation gets two different answers
    /// depending on where they look, and the design note promises the tool knows the difference.</para>
    /// </summary>
    [Fact]
    public void Wb4b_TemperaturePenalty_IsHalvedInTheSkinRegime()
    {
        const double radius = 12.7e-6;
        double sigma20 = WireMaterials.Gold.SigmaAt(20.0);
        double sigma85 = WireMaterials.Gold.SigmaAt(85.0);

        double dcPenalty = InternalImpedance.DcResistancePerMetre(radius, sigma85)
                         / InternalImpedance.DcResistancePerMetre(radius, sigma20) - 1.0;

        var (r85, _) = InternalImpedance.PerMetre(40e9, radius, sigma85);
        var (r20, _) = InternalImpedance.PerMetre(40e9, radius, sigma20);
        double rfPenalty = r85 / r20 - 1.0;

        Assert.Equal(0.221, dcPenalty, 0.002);
        Assert.True(rfPenalty is > 0.095 and < 0.115,
            $"The RF penalty should be ~10.5 %, about half the DC penalty of {dcPenalty:P1}; got {rfPenalty:P2}.");

        // And it must be the sqrt relationship, not a coincidence of this frequency.
        Assert.Equal(Math.Sqrt(1.0 + dcPenalty) - 1.0, rfPenalty, 0.01);
    }

    /// <summary>
    /// A sanity check against the rule of thumb, at the scale a packaging engineer would recognise:
    /// a 0.5 mil gold wire has ~15x its DC resistance at 40 GHz.
    /// </summary>
    [Fact]
    public void PerMetre_AtFortyGigahertz_ShowsTheExpectedSkinRatio()
    {
        const double radius = 12.7e-6;
        double sigma = WireMaterials.Gold.SigmaAt(85.0);

        var (rAc, _) = InternalImpedance.PerMetre(40e9, radius, sigma);
        double rDc = InternalImpedance.DcResistancePerMetre(radius, sigma);

        double ratio = rAc / rDc;
        Assert.True(ratio is > 14.0 and < 16.0,
            $"A 0.5 mil gold wire at 40 GHz should carry ~15x its DC resistance; got {ratio:F2}x.");
    }
}
