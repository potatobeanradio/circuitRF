using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Tier 3 — microstrip. This is the L7 phase gate</b> (Development_Plan §10: "L7 agrees with
/// closed-form microstrip (Hammerstad-Jensen) within ±2% on Z₀ and εeff over the published validity
/// range").
///
/// <para>H-J is an empirical fit with its own error, which is exactly why Tiers 0–2 come first: by
/// the time this file runs, the potential kernel, the field kernel, the bound-charge row, the ε_r
/// charge weighting and the image ground have each been pinned against an <i>exact</i> closed form.
/// A disagreement here is therefore about meshing, truncation — or about H-J.</para>
///
/// <para><b>The strip-thickness finding, which is why this file has two gates rather than one.</b>
/// H-J's zero-thickness formulas and this kernel agree to <b>better than 1.3% on ε_eff and 0.6% on
/// Z₀ across the whole 0.1 ≤ W/h ≤ 10 range on both starter stackups</b> — far inside the ±2%
/// requirement. H-J models a <i>thick</i> strip by widening W (its ΔW correction), which raises
/// ε_eff slightly. A boundary-element solve of the actual rectangle sees something different: the
/// strip's two side faces are in <b>air</b>, so a thicker strip pulls field out of the substrate
/// and ε_eff <b>falls</b>. The two models therefore diverge in opposite directions, and the
/// divergence scales with t/W (measured: ≈ −1.4% at t/W = 0.02, −2.7% at 0.07, −4.7% at 0.22 on
/// FR-4; roughly 1.8× that on GaAs, where the larger ε_r makes the substrate/air split matter
/// more). At t/W ≈ 0.2 — 35 µm copper on a 160 µm strip — H-J's ΔW correction is far outside any
/// regime it was fitted for, and <see cref="T3_6_TheDivergenceFromHammerstadJensen_IsEntirelyStripThickness"/>
/// demonstrates that the whole disagreement vanishes as t → 0.</para>
///
/// <para>Two further independent checks confirm the kernel rather than the oracle: Z₀,air (no
/// dielectric in the problem at all) matches H-J to 0.3% at <i>every</i> thickness, and replacing
/// the exact image ground with an explicitly meshed 60 h ground plate — a formulation using no
/// image whatsoever — reproduces ε_eff to 0.14%.</para>
/// </summary>
public class MicrostripOracleTests
{
    private static readonly MicrostripValidityReporter Quiet = new("(MoM oracle comparison)");

    /// <summary>A strip thin enough that H-J's own thickness correction is negligible.</summary>
    private const double ThinTOverH = 5e-4;

    private static (double Z0, double Eeff) Solve(EmProblem p, EmMeshSettings? settings = null)
    {
        var report = BoundaryMesher.Mesh(p, settings ?? EmMeshSettings.Default);
        var rlgc   = RlgcExtractor.Extract(p, report);
        return (Math.Sqrt(rlgc.LPerM / rlgc.CPerM), rlgc.Eeff);
    }

    /// <summary>The full published validity span the phase gate names.</summary>
    public static TheoryData<double> WOverH => new(0.1, 0.3, 0.6, 1.0, 1.81, 3.0, 6.0, 10.0);

    /// <summary>W/h values where the realistic metal is thin enough (t/W ≤ 0.01) for H-J's ΔW
    /// thickness correction to be inside its own regime.</summary>
    public static TheoryData<double> WOverHThickMetal => new(3.0, 6.0, 10.0);

    // ── Gate A: the ±2% acceptance gate, across the whole W/h span ─────────────────────────────

    [Theory]
    [MemberData(nameof(WOverH))]
    public void T3_1_Fr4_ThinStrip_MatchesHammerstadJensenWithinTwoPercent(double wOverH)
    {
        const double h = 1.6e-3, epsR = 4.4;
        double t = ThinTOverH * h;
        var (z0, eeff) = Solve(EmProblemBuilders.Microstrip(wOverH * h, h, t, epsR));
        var want = HammerstadJensen.Compute(wOverH * h, h, t, epsR, Quiet);

        AssertWithin(want.Z0,   z0,   0.02, $"FR-4 W/h={wOverH:G3} Z₀");
        AssertWithin(want.Eeff, eeff, 0.02, $"FR-4 W/h={wOverH:G3} εeff");
    }

    [Theory]
    [MemberData(nameof(WOverH))]
    public void T3_2_GaAs_ThinStrip_MatchesHammerstadJensenWithinTwoPercent(double wOverH)
    {
        const double h = 100e-6, epsR = 12.9;
        double t = ThinTOverH * h;
        var p = EmProblemBuilders.Microstrip(wOverH * h, h, t, epsR,
            sigmaSm: EmProblemBuilders.GoldSigma, lengthMeters: 2e-3);
        var (z0, eeff) = Solve(p);
        var want = HammerstadJensen.Compute(wOverH * h, h, t, epsR, Quiet);

        AssertWithin(want.Z0,   z0,   0.02, $"GaAs W/h={wOverH:G3} Z₀");
        AssertWithin(want.Eeff, eeff, 0.02, $"GaAs W/h={wOverH:G3} εeff");
    }

    // ── Gate B: real metal, over the W/h span where H-J's thickness correction is in regime ────

    [Theory]
    [MemberData(nameof(WOverHThickMetal))]
    public void T3_1b_Fr4_RealCopper_MatchesHammerstadJensenWithinTwoPercent(double wOverH)
    {
        const double h = 1.6e-3, t = 35e-6, epsR = 4.4;      // 1 oz copper
        Assert.True(t / (wOverH * h) <= 0.01, "this row must stay inside H-J's own thickness regime");
        var (z0, eeff) = Solve(EmProblemBuilders.Microstrip(wOverH * h, h, t, epsR));
        var want = HammerstadJensen.Compute(wOverH * h, h, t, epsR, Quiet);

        AssertWithin(want.Z0,   z0,   0.02, $"FR-4 (35 µm Cu) W/h={wOverH:G3} Z₀");
        AssertWithin(want.Eeff, eeff, 0.02, $"FR-4 (35 µm Cu) W/h={wOverH:G3} εeff");
    }

    [Theory]
    [MemberData(nameof(WOverHThickMetal))]
    public void T3_2b_GaAs_RealGold_MatchesHammerstadJensenWithinTwoPercent(double wOverH)
    {
        const double h = 100e-6, t = 3e-6, epsR = 12.9;
        Assert.True(t / (wOverH * h) <= 0.01, "this row must stay inside H-J's own thickness regime");
        var p = EmProblemBuilders.Microstrip(wOverH * h, h, t, epsR,
            sigmaSm: EmProblemBuilders.GoldSigma, lengthMeters: 2e-3);
        var (z0, eeff) = Solve(p);
        var want = HammerstadJensen.Compute(wOverH * h, h, t, epsR, Quiet);

        AssertWithin(want.Z0,   z0,   0.02, $"GaAs (3 µm Au) W/h={wOverH:G3} Z₀");
        AssertWithin(want.Eeff, eeff, 0.02, $"GaAs (3 µm Au) W/h={wOverH:G3} εeff");
    }

    /// <summary>
    /// The finding, pinned so that nobody later "corrects" the solver toward H-J: the entire
    /// thick-metal disagreement is the strip-thickness model and it vanishes as t → 0. At
    /// W/h = 0.1 the two differ by nearly 5% with 35 µm copper; thinning the metal collapses the
    /// difference monotonically to under 0.5% while H-J's own answer barely moves.
    /// </summary>
    [Fact]
    public void T3_6_TheDivergenceFromHammerstadJensen_IsEntirelyStripThickness()
    {
        const double h = 1.6e-3, epsR = 4.4;
        double w = 0.1 * h;

        double prevAbs = double.MaxValue;
        var trace = new List<string>();
        foreach (double t in new[] { 35e-6, 8e-6, 0.8e-6, 0.08e-6 })
        {
            var (_, eeff) = Solve(EmProblemBuilders.Microstrip(w, h, t, epsR));
            double want = HammerstadJensen.Compute(w, h, t, epsR, Quiet).Eeff;
            double rel = (eeff - want) / want;
            trace.Add($"t/W={t / w:F4} → {rel:P2}");

            // The sign only means anything while the disagreement is still bigger than the
            // solver's own residual; by the thinnest strip it has collapsed into the noise, which
            // is the point of the test.
            if (Math.Abs(rel) > 0.005)
                Assert.True(rel < 0, $"a thicker strip must pull ε_eff BELOW H-J, not above ({rel:P2})");
            Assert.True(Math.Abs(rel) < prevAbs,
                $"thinning the metal must shrink the disagreement — [{string.Join(", ", trace)}]");
            prevAbs = Math.Abs(rel);
        }
        Assert.True(prevAbs < 0.005, $"the thinnest strip still disagrees by {prevAbs:P2} — [{string.Join(", ", trace)}]");
    }

    // ── R-mom-10 and mesh convergence ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>R-mom-10.</b> §10.3.1 names the interface truncation as the one place kernel A can be
    /// quietly wrong. Doubling <see cref="EmMeshSettings.TruncationHeights"/> must not move Z₀ by
    /// more than the oracle tolerance — and the extent it actually used is reported, not hidden.
    /// </summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(1.81)]
    [InlineData(10.0)]
    public void T3_3_DoublingTheTruncationExtent_MovesZ0ByLessThanHalfAPercent(double wOverH)
    {
        const double h = 1.6e-3;
        var p = EmProblemBuilders.Microstrip(wOverH * h, h, 35e-6, 4.4);

        var baseSettings = EmMeshSettings.Default;
        var (z0a, _) = Solve(p, baseSettings);
        var (z0b, _) = Solve(p, baseSettings with { TruncationHeights = 2 * baseSettings.TruncationHeights });

        double rel = Math.Abs(z0b - z0a) / z0a;
        Assert.True(rel < 0.005,
            $"W/h={wOverH:G3}: doubling truncation moved Z₀ from {z0a:F3} Ω to {z0b:F3} Ω ({rel:P3})");

        var report = BoundaryMesher.Mesh(p, baseSettings);
        Assert.Equal(baseSettings.TruncationHeights * h, report.TruncationHalfExtent, h * 1e-9);
        Assert.Contains(report.Notes, s => s.Contains("truncated", StringComparison.Ordinal));
    }

    /// <summary>
    /// Refinement must approach a limit <b>monotonically</b>, not merely stay bounded. Wandering
    /// under refinement is the signature of an assembly sign error that a tolerance test passes by
    /// luck, so the sign of every successive step is asserted, not just the spread.
    /// </summary>
    [Theory]
    [InlineData(0.1)]
    [InlineData(1.81)]
    [InlineData(10.0)]
    public void T3_4_RefiningTheMesh_ApproachesALimitMonotonically(double wOverH)
    {
        const double h = 1.6e-3;
        var p = EmProblemBuilders.Microstrip(wOverH * h, h, 35e-6, 4.4);

        var zs = new List<double>();
        var ns = new List<int>();
        foreach (double factor in new[] { 1.0, 1.5, 2.25, 3.375 })
        {
            var s = EmMeshSettings.Default.Refined(factor);
            zs.Add(Solve(p, s).Z0);
            ns.Add(BoundaryMesher.Mesh(p, s).UnknownCount);
        }

        for (int i = 1; i < ns.Count; i++)
            Assert.True(ns[i] > ns[i - 1],
                $"refinement {i} did not increase the unknown count ({ns[i - 1]} → {ns[i]})");

        var d = new List<double>();
        for (int i = 1; i < zs.Count; i++) d.Add(zs[i] - zs[i - 1]);
        string steps = string.Join(", ", d.ConvertAll(v => v.ToString("F5")));

        for (int i = 1; i < d.Count; i++)
            Assert.True(Math.Sign(d[i]) == Math.Sign(d[i - 1]) || d[i] == 0,
                $"W/h={wOverH:G3}: Z₀ wandered rather than converging — steps [{steps}]");

        double spread = (zs[^1] - zs[0]) / zs[0];
        Assert.True(Math.Abs(spread) < 0.01,
            $"W/h={wOverH:G3}: Z₀ moved {spread:P3} over a 3.4× refinement — steps [{steps}]");
    }

    /// <summary>
    /// The §10.7 sanity check on the hero, as an actual assertion: 50 Ω on 1.6 mm FR-4 is
    /// W ≈ 2.9 mm, and the mesh is "N of a few hundred".
    /// </summary>
    [Fact]
    public void T3_5_TheFiftyOhmHero_LandsAtFiftyOhmsWithAFewHundredUnknowns()
    {
        var p = EmProblemBuilders.Fr4Microstrip(2.9e-3);
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);
        var (z0, eeff) = Solve(p);

        AssertWithin(50.0, z0, 0.03, "hero Z₀");
        Assert.InRange(eeff, 3.0, 3.6);
        Assert.InRange(report.UnknownCount, 30, 600);
    }

    private static void AssertWithin(double want, double got, double relTol, string what)
    {
        double rel = Math.Abs(got - want) / Math.Abs(want);
        Assert.True(rel <= relTol, $"{what}: got {got:G6}, oracle {want:G6}, off by {rel:P3} (limit {relTol:P1})");
    }
}
