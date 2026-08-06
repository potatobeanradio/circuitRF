using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Tier 5 — loss.</b> Dielectric loss against the closed form, conductor loss against
/// <see cref="MicrostripLoss"/> (a genuinely independent model — the classic R_s/(Z₀·W) form, not
/// this kernel's Wheeler recession), and the R-mom-12/13 guards on ∂L/∂n itself.
/// </summary>
public class LossTests
{
    private const double W = 2.9e-3, H = 1.6e-3, T = 35e-6, EpsR = 4.4;

    private static EmSolveResult Solve(EmProblem p, double[] freqs)
        => new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, freqs);

    private static double[] Decade(double f0, int n, double step)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 * Math.Pow(step, i);
        return f;
    }

    /// <summary>
    /// Pozar's α_d = (k₀/2)·ε_r(ε_eff−1)·tanδ / (√ε_eff·(ε_r−1)), which is exactly what
    /// <see cref="MicrostripLoss.DielectricLossNpPerM"/> implements.
    ///
    /// <para><i>Note on the brief's transcription:</i> §8 Tier 5 writes this as
    /// <c>(π/λ_g)·ε_r(ε_eff−1)·tanδ/(√ε_eff·(ε_r−1))</c>. With λ_g = λ₀/√ε_eff that is √ε_eff times
    /// the standard result; read with λ₀ in place of λ_g it is Pozar's form exactly. The in-tree
    /// implementation is the reference used here.</para>
    ///
    /// <para>The agreement is not a coincidence of two fits: this kernel gets C″ from the exact
    /// complex-ε* solve, so its α_d is (k₀/2)·ε_r·tanδ·(∂ε_eff/∂ε_r)/√ε_eff, and the filling-factor
    /// form is the same expression with ∂ε_eff/∂ε_r replaced by (ε_eff−1)/(ε_r−1) — equal whenever
    /// ε_eff is affine in ε_r, which it very nearly is.</para>
    /// </summary>
    [Theory]
    [InlineData(0.001)]
    [InlineData(0.02)]
    [InlineData(0.05)]
    public void T5_1_DielectricAttenuation_MatchesTheClosedForm(double tanD)
    {
        var freqs = Decade(1e9, 5, 2.0);
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR, tanD,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity);
        var res = Solve(p, freqs);

        var alpha = res.Data["tline.Gamma"].ComplexValues;
        for (int i = 0; i < freqs.Length; i++)
        {
            double want = MicrostripLoss.DielectricLossNpPerM(freqs[i], EpsR, res.Rlgc.Eeff, tanD);
            double got = alpha[i].Real;
            double rel = Math.Abs(got - want) / want;
            Assert.True(rel < 0.05,
                $"tanδ={tanD}, f={freqs[i]:G4}: α_d = {got:E4} Np/m, closed form {want:E4} ({rel:P2})");
        }
    }

    [Fact]
    public void T5_2_DielectricAttenuationIsProportionalToFrequency()
    {
        // G = ω·C″ with a constant tanδ, so α_d ∝ f exactly — it falls out of R-mom-6 rather than
        // being asserted anywhere in the code.
        var freqs = Decade(1e9, 5, 2.0);
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR, 0.02,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity);
        var res = Solve(p, freqs);
        var g = res.Data["tline.Gpul"].RealValues;
        var alpha = res.Data["tline.Gamma"].ComplexValues;

        for (int i = 1; i < freqs.Length; i++)
        {
            Assert.Equal(2.0, g[i] / g[i - 1], 1e-9);
            Assert.Equal(2.0, alpha[i].Real / alpha[i - 1].Real, 1e-3);
        }
    }

    /// <summary>
    /// <b>The √f slope is the part that must be exact, and it is — everywhere, for every geometry.</b>
    /// A disagreement in the slope would be a bug regardless of magnitude, because it would mean one
    /// of the two models is not modelling skin effect at all.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(1.81)]
    [InlineData(20.0)]
    public void T5_3_ConductorAttenuationFollowsRootF_Exactly(double wOverH)
    {
        var freqs = Decade(1e9, 6, 2.0);
        var p = EmProblemBuilders.Microstrip(wOverH * H, H, T, EpsR, tanD: 0);
        var alpha = Solve(p, freqs).Data["tline.Gamma"].ComplexValues;

        // Every octave multiplies α_c by exactly √2 — well above the R-mom-13 crossover, where the
        // DC floor contributes nothing.
        for (int i = 1; i < freqs.Length; i++)
            Assert.Equal(Math.Sqrt(2.0), alpha[i].Real / alpha[i - 1].Real, 2e-3);
    }

    /// <summary>
    /// The level, against <see cref="MicrostripLoss.ConductorLossNpPerM"/> — the classic
    /// α_c = R_s/(Z₀·W). <b>That form is the wide-strip asymptote</b>: it assumes the strip current
    /// is uniform across exactly W <i>and</i> that the ground plane contributes an equal R_s/W. The
    /// second assumption is the weak one — a real ground-plane current spreads over roughly
    /// ±(W/2 + h), so for a narrow strip it contributes far less. Wheeler's incremental-inductance
    /// rule, applied to the actual geometry, weights every surface by its own |H|² and gets this
    /// right; the crude form is therefore an upper bound that the kernel must approach from below,
    /// exactly, as W/h → ∞. Gated in the regime where the oracle is valid.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(50.0)]
    public void T5_3b_ConductorAttenuation_MatchesMicrostripLossInTheWideStripLimit(double wOverH)
    {
        double w = wOverH * H;
        var res = Solve(EmProblemBuilders.Microstrip(w, H, T, EpsR, tanD: 0), [1e9]);
        double z0 = Math.Sqrt(res.Rlgc.LPerM / res.Rlgc.CPerM);

        double got = res.Data["tline.Gamma"].ComplexValues[0].Real;
        double want = MicrostripLoss.ConductorLossNpPerM(1e9, EmProblemBuilders.CopperSigma, w, z0);
        double rel = Math.Abs(got - want) / want;
        Assert.True(rel < 0.20,
            $"W/h={wOverH:G3}: Wheeler α_c = {got:E4} Np/m, MicrostripLoss {want:E4} ({rel:P1})");
    }

    /// <summary>
    /// The trend behind <see cref="T5_3b_ConductorAttenuation_MatchesMicrostripLossInTheWideStripLimit"/>,
    /// pinned — a far stronger statement than any single tolerance band. Two independent loss models
    /// converging on each other monotonically, in the limit where one of them is derived, is what
    /// actually validates the Wheeler path.
    /// </summary>
    [Fact]
    public void T5_3c_WheelerApproachesTheWideStripFormulaMonotonicallyFromBelow()
    {
        double prev = 0;
        var trace = new List<string>();
        foreach (double wOverH in new[] { 0.3, 1.0, 1.81, 3.0, 6.0, 10.0, 20.0, 50.0 })
        {
            double w = wOverH * H;
            var res = Solve(EmProblemBuilders.Microstrip(w, H, T, EpsR, tanD: 0), [1e9]);
            double z0 = Math.Sqrt(res.Rlgc.LPerM / res.Rlgc.CPerM);
            double ratio = res.Data["tline.Gamma"].ComplexValues[0].Real
                         / MicrostripLoss.ConductorLossNpPerM(1e9, EmProblemBuilders.CopperSigma, w, z0);
            trace.Add($"W/h={wOverH:G3}→{ratio:F3}");

            Assert.True(ratio < 1.0, $"the wide-strip form must be an upper bound — [{string.Join(" ", trace)}]");
            Assert.True(ratio > prev, $"the ratio must rise with W/h — [{string.Join(" ", trace)}]");
            prev = ratio;
        }
        Assert.True(prev > 0.95, $"the parallel-plate limit was not reached — [{string.Join(" ", trace)}]");
    }

    /// <summary>
    /// <b>R-mom-12.</b> Both perturbations increase L, so both ∂L/∂n are positive; a negative one
    /// is a bug and asserting the sign is a cheap guard. <b>Omitting the ground-plane term is the
    /// common error and it under-reports microstrip loss noticeably</b> — so its size is checked
    /// too, not just its presence.
    /// </summary>
    [Fact]
    public void T5_4_BothWheelerSurfacesContributeAPositiveDerivative()
    {
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR);
        var res = Solve(p, [1e9]);

        Assert.Equal(2, res.Rlgc.LossSurfaces.Count);
        var cond = res.Rlgc.LossSurfaces[0];
        var gnd  = res.Rlgc.LossSurfaces[1];

        Assert.Equal("conductor:strip", cond.Name);
        Assert.Equal("ground", gnd.Name);
        // R-cpl-2: ∂L/∂n is a MATRIX now (one per receded surface). With one conductor the
        // single [0,0] entry is the whole of it, and its value is unchanged from kernel A.
        Assert.True(cond.DLdn[0, 0] > 0, $"receding the metal must increase L, got ∂L/∂n = {cond.DLdn[0, 0]:E4}");
        Assert.True(gnd.DLdn[0, 0] > 0, $"lowering the ground plane must increase L, got ∂L/∂n = {gnd.DLdn[0, 0]:E4}");

        // The ground plane is not a rounding correction — it is the same order as the strip.
        double share = gnd.DLdn[0, 0] / (cond.DLdn[0, 0] + gnd.DLdn[0, 0]);
        Assert.InRange(share, 0.15, 0.85);
    }

    /// <summary>R-mom-12: Δ is a numerical parameter, so the answer must not depend on it.</summary>
    [Fact]
    public void T5_5_HalvingTheRecessionDoesNotMoveTheAnswer()
    {
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR);
        var report = BoundaryMesher.Mesh(p, EmMeshSettings.Default);

        var a = RlgcExtractor.Extract(p, report, RlgcExtractor.DefaultRecessionFraction);
        var b = RlgcExtractor.Extract(p, report, RlgcExtractor.DefaultRecessionFraction / 2);
        var c = RlgcExtractor.Extract(p, report, RlgcExtractor.DefaultRecessionFraction / 4);

        double omega = 2 * Math.PI * 1e9;
        foreach (var other in new[] { b, c })
        {
            double rel = Math.Abs(other.RPerM(omega) - a.RPerM(omega)) / a.RPerM(omega);
            Assert.True(rel < 0.01, $"halving Δ moved R(1 GHz) by {rel:P3}");
        }
    }

    /// <summary>
    /// <b>R-mom-13.</b> Wheeler assumes δ ≪ t. Below the reported crossover the DC floor is what is
    /// actually being reported, and the crossover itself is surfaced so a user sweeping down into
    /// the invalid region is told rather than quietly misled.
    /// </summary>
    [Fact]
    public void T5_6_BelowTheWheelerCrossover_RIsCarriedByTheDcFloor()
    {
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR);
        var res = Solve(p, [1e9]);

        double rdcWant = 1.0 / (EmProblemBuilders.CopperSigma * W * T);
        Assert.Equal(rdcWant, res.Rlgc.RdcPerM[0, 0], rdcWant * 1e-9);   // R-cpl-3: diagonal matrix now

        double fx = res.Rlgc.WheelerValidAboveHz;
        Assert.InRange(fx, 1e7, 2e7);

        // Three decades below the crossover, R is the DC value to within a fraction of a percent.
        // (Two decades is not enough: the skin term is still ~24% of R_dc at f_x/100, which the
        // √(R_dc² + R_w²) blend turns into a 3% lift — the blend is a smooth interpolation between
        // asymptotes, not a switch.)
        double rLow = res.Rlgc.RPerM(2 * Math.PI * fx / 1000);
        Assert.Equal(rdcWant, rLow, rdcWant * 0.01);

        // …two decades above it, the DC floor contributes nothing.
        double omegaHigh = 2 * Math.PI * fx * 100;
        double rHigh = res.Rlgc.RPerM(omegaHigh);
        Assert.True(rHigh > 20 * rdcWant, $"R({fx * 100:G4} Hz) = {rHigh:E4} is not clear of the DC floor {rdcWant:E4}");

        Assert.Contains(res.MeshReport.Notes, s => s.Contains("skin depth", StringComparison.Ordinal));
    }

    [Fact]
    public void T5_7_APerfectConductorHasNoLossAtAll()
    {
        var p = EmProblemBuilders.Microstrip(W, H, T, EpsR, tanD: 0,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity);
        var res = Solve(p, [1e9, 1e10]);

        Assert.Equal(0.0, res.Rlgc.RdcPerM[0, 0], 0.0);
        Assert.Equal(0.0, res.Rlgc.RPerM(2 * Math.PI * 1e9), 0.0);
        foreach (double a in res.Data["tline.AttenDbPerM"].RealValues) Assert.Equal(0.0, a, 1e-12);
    }

    /// <summary>
    /// Both loss mechanisms together, so nothing double-counts: total α must equal the sum of the
    /// two run separately, to the accuracy of the low-loss approximation that separation implies.
    /// </summary>
    [Fact]
    public void T5_8_ConductorAndDielectricLossAdd()
    {
        var freqs = new[] { 1e9, 5e9, 1e10 };
        var both = Solve(EmProblemBuilders.Microstrip(W, H, T, EpsR, 0.02), freqs);
        var condOnly = Solve(EmProblemBuilders.Microstrip(W, H, T, EpsR, 0), freqs);
        var dielOnly = Solve(EmProblemBuilders.Microstrip(W, H, T, EpsR, 0.02,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity), freqs);

        var ab = both.Data["tline.Gamma"].ComplexValues;
        var ac = condOnly.Data["tline.Gamma"].ComplexValues;
        var ad = dielOnly.Data["tline.Gamma"].ComplexValues;

        for (int i = 0; i < freqs.Length; i++)
        {
            double sum = ac[i].Real + ad[i].Real;
            Assert.Equal(sum, ab[i].Real, sum * 0.01);
        }
    }
}
