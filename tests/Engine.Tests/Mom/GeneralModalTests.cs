using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>L7b-b Tiers G1–G4</b> — the general modal decomposition
/// (docs/sonnet-briefs/brief-L7b-b-general-modal-decomposition.md §6).
///
/// <para>Tier G5 (three-conductor co-simulation, end to end) lives in
/// <c>tests/Ui.Tests/Em/EmCoSimulationTests.cs</c> beside the rest of the run path.</para>
/// </summary>
public class GeneralModalTests(ITestOutputHelper output)
{
    private const double W = 1.4e-3, Gap = 0.3e-3;

    private static readonly Complex[] Z0_4 = [50, 50, 50, 50];

    private static RlgcModel Extract(EmProblem p, double refine = 2.0)
        => RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default.Refined(refine)));

    private static Mat<Complex> S(DataSet ds, int fi, int n)
    {
        var v = ds["S"].ComplexValues;
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            m[i, j] = v[fi * n * n + i * n + j];
        return m;
    }

    /// <summary>√ on the branch with Re ≥ 0 — the same rule production uses.</summary>
    private static Complex Root(Complex v)
    {
        var s = Complex.Sqrt(v);
        return s.Real < 0 ? -s : s;
    }

    private static double WorstEntry(Mat<Complex> a, Mat<Complex> b)
    {
        double w = 0;
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            w = Math.Max(w, (a[i, j] - b[i, j]).Magnitude);
        return w;
    }

    /// <summary>
    /// The sweep R-gen-5 asks for: DOWN through the Wheeler crossover (≈14 MHz for 35 µm copper),
    /// where loss matters most relative to reactance because R/(ωL) and G/(ωC) both grow as ω falls,
    /// and UP to 20 GHz. Measuring only at 1–20 GHz on a low-loss board would find nothing and prove
    /// nothing.
    /// </summary>
    private static readonly double[] StressSweep =
        [1e5, 1e6, 1e7, 1.4e7, 1e8, 1e9, 5e9, 1e10, 2e10];

    // ══ The NumFlat contract, verified rather than assumed (R-gen-1 / R-gen-3) ═════════════════

    /// <summary>
    /// <b>R-gen-1 and R-gen-3 both say to re-verify this against whatever NumFlat version is
    /// current rather than trusting the brief's quotation.</b> Two things are checked, because Route
    /// A rests on both: that <c>Gevd(a, b)</c> solves <c>A v = λ B v</c> (not the reciprocal
    /// problem, which would invert every velocity), and that it returns <c>V</c> B-orthonormal
    /// (<c>VᵀBV = I</c>) — the normalisation nobody chose and that R-gen-3a's ohms-versus-metres-
    /// per-second trap comes from.
    /// </summary>
    [Fact]
    public void NumFlatGevd_SolvesAvEqualsLambdaBv_AndReturnsBOrthonormalV()
    {
        var a = new Mat<double>(2, 2); a[0, 0] = 4; a[0, 1] = 1; a[1, 0] = 1; a[1, 1] = 3;
        var b = new Mat<double>(2, 2); b[0, 0] = 2; b[0, 1] = 0.5; b[1, 0] = 0.5; b[1, 1] = 1;

        var g = MatrixDecompositions.Gevd(a, b);

        for (int m = 0; m < 2; m++)
        for (int i = 0; i < 2; i++)
        {
            double av = 0, bv = 0;
            for (int k = 0; k < 2; k++) { av += a[i, k] * g.V[k, m]; bv += b[i, k] * g.V[k, m]; }
            Assert.Equal(av, g.D[m] * bv, 1e-9);
        }

        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
        {
            double s = 0;
            for (int k = 0; k < 2; k++)
            for (int l = 0; l < 2; l++)
                s += g.V[k, i] * b[k, l] * g.V[l, j];
            Assert.Equal(i == j ? 1.0 : 0.0, s, 1e-9);
        }
    }

    // ══ Tier G1 — the decision measurement ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>Tier G1's first finding, and it changes what G1 can measure: on a SYMMETRIC pair Route A
    /// is EXACT, so a symmetric pair cannot measure Route A's error at all.</b>
    ///
    /// <para><c>[1 1; 1 −1]</c> diagonalises any 2×2 of the form <c>[a b; b a]</c> whatever a and b
    /// are — so for a mirror-symmetric pair the lossless <c>[L][C]</c> and the lossy <c>[Z][Y]</c>
    /// have the SAME eigenvectors and Route A's perturbative step discards exactly nothing. That is
    /// why the accuracy measurement below is taken on an ASYMMETRIC pair against a closed-form 2×2
    /// oracle (<see cref="ExactTwoConductorOracle"/>) instead: it is the only fixture where the
    /// quantity G1 exists to measure is non-zero.</para>
    ///
    /// <para>Checked with ideal metal and zero tanδ, so "exact" means exact rather than
    /// small — the residual here is machine zero, not a tolerance.</para>
    /// </summary>
    [Fact]
    public void G1_ASymmetricLosslessPair_HasZeroModeCoupling_SoItCannotMeasureRouteAsError()
    {
        var rlgc = Extract(EmProblemBuilders.CoupledMicrostrip(
            W, Gap, 1.6e-3, 35e-6, 4.4, tanD: 0,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity));
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        foreach (double f in StressSweep)
        {
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, 2 * Math.PI * f);
            Assert.True(pt.ModeCouplingResidual < 1e-12,
                $"f = {f:G3} Hz: residual {pt.ModeCouplingResidual:E3} is not machine zero — a " +
                "mirror-symmetric lossless pair has NOTHING for Route A to discard");
        }
    }

    /// <summary>
    /// <b>Tier G1 — the measurement M1 exists for.</b> Route A against the exact closed-form 2×2
    /// modal decomposition of the same RLGC matrices, on an ASYMMETRIC pair, swept from six decades
    /// below the Wheeler crossover up to 20 GHz, at three loss tangents, with real and with perfect
    /// metal.
    ///
    /// <para>The oracle shares R-gen-2's block construction with production deliberately: the ONLY
    /// difference is which Tv is used — the exact, frequency-dependent one from <c>[Z][Y]</c> there,
    /// the frequency-independent lossless one here — so what is measured is precisely Route A's own
    /// approximation and nothing else.</para>
    ///
    /// <para><b>Measured on this fixture: worst |ΔS| ≈ 5e-4 across the whole matrix.</b> That is two
    /// orders of magnitude below the [C] solve's own discretisation error (Tier 3 lands at ≤ 1.3% on
    /// ε_eff), so on this class of structure Route A is not the limiting approximation.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0,  false)]
    [InlineData(0.02, false)]
    [InlineData(0.2,  false)]
    [InlineData(0.0,  true)]
    [InlineData(0.02, true)]
    [InlineData(0.2,  true)]
    public void G1_RouteAVersusTheExactClosedForm_OnAnAsymmetricPair(double tanD, bool perfectMetal)
    {
        double sigma = perfectMetal ? double.PositiveInfinity : EmProblemBuilders.CopperSigma;
        var p = EmProblemBuilders.MulticonductorMicrostrip(
            [1.4e-3, 0.35e-3], 0.3e-3, 1.6e-3, 35e-6, 4.4, tanD,
            sigmaSm: sigma, groundSigmaSm: sigma);

        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        double worst = 0;
        output.WriteLine($"tanδ = {tanD}, metal = {(perfectMetal ? "perfect" : "copper")} " +
                         $"(Wheeler valid above {rlgc.WheelerValidAboveHz:G3} Hz)");
        foreach (double f in StressSweep)
        {
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, 2 * Math.PI * f);
            var routeA = S(RlgcToSparams.Build(rlgc, p.LengthMeters, [f], Z0_4), 0, 4);
            var exact  = ExactTwoConductorOracle.S4(rlgc, p.LengthMeters, f, Z0_4);

            double e = WorstEntry(routeA, exact);
            worst = Math.Max(worst, e);
            output.WriteLine($"   f = {f,9:G3} Hz   |ΔS|max = {e:E3}   residual = {pt.ModeCouplingResidual:E3}");
        }

        output.WriteLine($"   worst over sweep: |ΔS| = {worst:E3}");
        Assert.True(worst < 2e-3,
            $"Route A's worst terminal error on this fixture is {worst:E3}; if it has grown past " +
            "2e-3 the perturbative treatment of loss has stopped being adequate here and D2's " +
            "Route B decision needs re-taking");
    }

    /// <summary>
    /// <b>Tier G1's second gate, and it FAILS as the brief words it — which is a finding, not a
    /// tolerance to loosen.</b> R-gen-5 claims the residual should PREDICT the error. It does not:
    /// the two are <i>anti-correlated in frequency</i>.
    ///
    /// <para>At 100 kHz the residual is ≈0.36 (R/(ωL) is enormous six decades below the Wheeler
    /// crossover) while the terminal error is ≈5e-5; at 20 GHz the residual has fallen to ≈2e-4
    /// while the error has RISEN to ≈5e-4 — past the residual itself, so it is not even a bound.
    /// The mechanism is straightforward once seen: the residual measures the error in the modal
    /// MATRICES, but how much of that reaches the terminals scales with the electrical length γℓ,
    /// and a line that is electrically short is insensitive to how its modes were split.</para>
    ///
    /// <para>The residual is still worth reporting — it is the honest measure of what the
    /// perturbative step discarded, and it is the number that says "this cross-section is being
    /// decomposed essentially exactly" versus "this one is not". It is simply not a predictor of
    /// terminal accuracy, and this test pins that so a later change cannot quietly assume the
    /// opposite.</para>
    /// </summary>
    [Fact]
    public void G1_TheResidualDoesNotPredictTheError_AndIsAntiCorrelatedInFrequency()
    {
        var p = EmProblemBuilders.MulticonductorMicrostrip(
            [1.4e-3, 0.35e-3], 0.3e-3, 1.6e-3, 35e-6, 4.4, tanD: 0);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        (double Res, double Err) At(double f)
        {
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, 2 * Math.PI * f);
            var a  = S(RlgcToSparams.Build(rlgc, p.LengthMeters, [f], Z0_4), 0, 4);
            var x  = ExactTwoConductorOracle.S4(rlgc, p.LengthMeters, f, Z0_4);
            return (pt.ModeCouplingResidual, WorstEntry(a, x));
        }

        var lo = At(1e5);
        var hi = At(2e10);
        output.WriteLine($"100 kHz: residual = {lo.Res:E3}, |ΔS| = {lo.Err:E3}");
        output.WriteLine($" 20 GHz: residual = {hi.Res:E3}, |ΔS| = {hi.Err:E3}");

        Assert.True(lo.Res > 100 * hi.Res,
            "the residual is supposed to be far LARGER at low frequency, where R/(ωL) dominates");
        Assert.True(hi.Err > lo.Err,
            "the terminal error is supposed to be larger at HIGH frequency, where the line is " +
            "electrically long enough for the modal split to matter");
        Assert.True(hi.Err > hi.Res,
            "at 20 GHz the terminal error exceeds the residual — the residual is not a bound, " +
            "which is exactly why R-gen-5's 'the residual must predict the error' does not hold");
    }

    /// <summary>
    /// <b>Tier G1 — where Route A is most stressed, deliberately taken past anything realistic.</b>
    /// A 100 mm line of 1 MS/m metal (60× worse than copper), a 10:1 width ratio and a 150 µm gap,
    /// evaluated at 100 kHz — four decades below THAT metal's own Wheeler crossover, where [R] is
    /// pinned at its DC floor and the incremental-inductance rule is already outside its own premise.
    ///
    /// <para><b>Measured: worst |ΔS| ≈ 1.7e-2.</b> That is the number D2's decision rests on: even
    /// in a regime constructed to break it, Route A is wrong by under two percent of |S|, and the
    /// residual there is 0.83 — i.e. the diagnostic does flag the case loudly. A hand-written complex
    /// QR eigensolver is a genuine numerical-methods commitment in a solo project; this measurement
    /// does not earn it.</para>
    /// </summary>
    [Fact]
    public void G1_WorstCaseStress_BoundsRouteAsErrorWellUnderTwoPercent()
    {
        var p = EmProblemBuilders.MulticonductorMicrostrip(
            [2.0e-3, 0.2e-3], 0.15e-3, 1.6e-3, 35e-6, 4.4, tanD: 0.2,
            sigmaSm: 1e6, lengthMeters: 0.1, groundSigmaSm: 1e6);

        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        double worst = 0, resAtWorst = 0, fAtWorst = 0;
        foreach (double f in StressSweep)
        {
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, 2 * Math.PI * f);
            var a  = S(RlgcToSparams.Build(rlgc, p.LengthMeters, [f], Z0_4), 0, 4);
            var x  = ExactTwoConductorOracle.S4(rlgc, p.LengthMeters, f, Z0_4);
            double e = WorstEntry(a, x);
            if (e > worst) { worst = e; resAtWorst = pt.ModeCouplingResidual; fAtWorst = f; }
        }

        output.WriteLine($"worst |ΔS| = {worst:E3} at f = {fAtWorst:G3} Hz, residual there = {resAtWorst:E3}");
        Assert.True(worst < 0.03,
            $"Route A's worst-case terminal error is {worst:E3} — past 3% the D2 decision that " +
            "Route B is not earned would have to be re-taken");
        Assert.True(resAtWorst > ModalDecomposition.ModeCouplingWarnThreshold,
            "the diagnostic must at least be loud where the error IS worst, even though it does not " +
            "predict its size");
    }

    // ══ Tier G2 — exact and self-consistency oracles ═══════════════════════════════════════════

    /// <summary>
    /// <b>Tier G2's continuity gate — the single most important test in this phase</b>, and the one
    /// place an exact answer exists for a genuinely coupled, genuinely lossy structure. A symmetric
    /// pair through the GENERAL path must land where L7b's fixed-matrix construction put it.
    ///
    /// <para><b>Not byte-identity, and the reason is worth stating.</b> L7b FORCES
    /// <c>Tv = [1 1; 1 −1]</c>, which is the exact modal matrix of an exactly persymmetric pair —
    /// but point collocation does not produce one: C₁₁ and C₂₂ differ by the mesh's own diagonal
    /// asymmetry (0.074% at default settings, 0.0005% at <c>Refined(4)</c>). The general path uses
    /// the true eigenvectors of the matrices actually solved, so the two answers differ by exactly
    /// that discretisation error — and both converge to the same limit as the mesh refines, which is
    /// what this gate asserts.</para>
    /// </summary>
    [Fact]
    public void G2_SymmetricPair_TheGeneralPathReproducesL7bsFixedMatrixAnswer_AndConverges()
    {
        double prev = double.MaxValue;
        foreach (double refine in new[] { 1.0, 2.0, 4.0 })
        {
            var p = EmProblemBuilders.CoupledMicrostrip(W, Gap, 1.6e-3, 35e-6, 4.4, tanD: 0.02);
            var rlgc = Extract(p, refine);

            double worst = 0;
            foreach (double f in new[] { 1e6, 1e8, 1e9, 5e9, 2e10 })
            {
                var general = S(RlgcToSparams.Build(rlgc, p.LengthMeters, [f], Z0_4), 0, 4);
                var l7b     = L7bSymmetricPairOracle.S4(rlgc, p.LengthMeters, f, Z0_4);
                worst = Math.Max(worst, WorstEntry(general, l7b));
            }

            double diag = ModalDecomposition.DiagonalAsymmetry(rlgc);
            output.WriteLine($"Refined({refine}): |general − L7b|max = {worst:E3}, " +
                             $"mesh diagonal asymmetry = {diag:P4}");

            Assert.True(worst < prev,
                $"Refined({refine}): the gap to L7b ({worst:E3}) did not improve on {prev:E3} — the " +
                "two constructions must converge to the same limit as the discretisation improves");
            prev = worst;
        }

        Assert.True(prev < 1e-4,
            $"at Refined(4) the general path is still {prev:E3} from L7b's answer — that is more " +
            "than the mesh's own diagonal asymmetry can explain");
    }

    /// <summary>
    /// <b>Tier G2 — and a finding that answers the brief's own question about whether L7b's closed
    /// form is worth keeping: on a discretised matrix the general path is not merely as good as
    /// L7b's, it is roughly THREE ORDERS OF MAGNITUDE closer to exact.</b>
    ///
    /// <para>L7b forces the modal matrix a perfectly symmetric pair would have; the general path uses
    /// the eigenvectors the actually-solved matrices actually have. Since those matrices carry the
    /// mesher's own diagonal asymmetry, forcing <c>[1 1; 1 −1]</c> is itself an approximation — and
    /// the larger one. Measured at default mesh settings: |RouteA − exact| ≈ 8e-6 against
    /// |L7b − exact| ≈ 8.9e-3.</para>
    ///
    /// <para>L7b's construction is therefore worth keeping as an ORACLE — it is exact for the
    /// idealised pair and is what makes the continuity gate above meaningful — but it was never the
    /// better answer for a real mesh, and nothing should be reinstated on the grounds that it was.</para>
    /// </summary>
    [Fact]
    public void G2_OnADiscretisedMesh_RouteAIsCloserToExactThanL7bsFixedMatrix()
    {
        var p = EmProblemBuilders.CoupledMicrostrip(W, Gap, 1.6e-3, 35e-6, 4.4, tanD: 0.02);
        var rlgc = Extract(p, refine: 1.0);

        double aErr = 0, l7Err = 0;
        foreach (double f in new[] { 1e6, 1e8, 1e9, 5e9, 2e10 })
        {
            var exact  = ExactTwoConductorOracle.S4(rlgc, p.LengthMeters, f, Z0_4);
            aErr  = Math.Max(aErr,  WorstEntry(S(RlgcToSparams.Build(rlgc, p.LengthMeters, [f], Z0_4), 0, 4), exact));
            l7Err = Math.Max(l7Err, WorstEntry(L7bSymmetricPairOracle.S4(rlgc, p.LengthMeters, f, Z0_4), exact));
        }

        output.WriteLine($"|RouteA − exact| = {aErr:E3}, |L7b − exact| = {l7Err:E3}");
        Assert.True(aErr < l7Err,
            $"the general path ({aErr:E3}) must not be further from exact than L7b's forced modal " +
            $"matrix ({l7Err:E3}) on the same discretised matrices");
    }

    /// <summary>
    /// <b>Tier G2 / R-gen-3a — the reported Zc must be in OHMS, and this is the test without which
    /// the naive implementation publishes phase velocities.</b>
    ///
    /// <para>Under NumFlat's B-orthonormal GEVD normalisation, <c>Zc_m = √(Zm_mm/Ym_mm)</c> comes out
    /// as the mode's own phase velocity (1.8×10⁸ for the brief's worked pair, exactly 1/√λ) rather
    /// than as 90.5 Ω / 47.3 Ω — and the ratios differ per mode (1.07 versus 1.91), so no single
    /// constant repairs it. The terminal s-parameters are perfectly correct throughout; this affects
    /// only what the tline group publishes, which is precisely the quantity a user reads off a plot
    /// and believes.</para>
    ///
    /// <para>The normalisation chosen is L7b's own — "each conductor carries the mode's own current",
    /// i.e. <c>Ti</c>'s column made equal to <c>Tv</c>'s where they are parallel — and this asserts
    /// that it reproduces L7b's Z_e and Z_o, statically and per frequency.</para>
    /// </summary>
    [Fact]
    public void G2_ReportedZc_IsInOhms_AndReproducesL7bsZeAndZo()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, Gap, tanD: 0.02);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        Assert.True(modes.TryIdentifyEvenOdd(out int even, out int odd));

        var (ze, zo) = L7bSymmetricPairOracle.StaticModalImpedances(rlgc);
        output.WriteLine($"L7b: Z_e = {ze:F3} Ω, Z_o = {zo:F3} Ω");
        output.WriteLine($"general: even = {modes.Z0(even):F3} Ω, odd = {modes.Z0(odd):F3} Ω " +
                         $"(phase velocities {modes.PhaseVelocity(even):E3}, {modes.PhaseVelocity(odd):E3} m/s)");

        Assert.Equal(ze, modes.Z0(even), ze * 1e-3);
        Assert.Equal(zo, modes.Z0(odd),  zo * 1e-3);

        // …and the trap this exists to catch: the raw B-orthonormal answer would be the phase
        // velocity, which is nine orders of magnitude away and is NOT ohms.
        Assert.True(modes.PhaseVelocity(even) > 1e6 * modes.Z0(even));

        var (fe, fo) = L7bSymmetricPairOracle.FrequencyZc(rlgc, 5e9);
        var ds = RlgcToSparams.Build(rlgc, p.LengthMeters, [5e9], Z0_4);
        var gotE = ds["tline.ZcEven"].ComplexValues[0];
        var gotO = ds["tline.ZcOdd"].ComplexValues[0];
        Assert.Equal(fe.Real, gotE.Real, Math.Abs(fe.Real) * 1e-3);
        Assert.Equal(fo.Real, gotO.Real, Math.Abs(fo.Real) * 1e-3);
    }

    /// <summary>
    /// <b>Tier G2 — the asymmetric case's exact oracle.</b> N conductors pushed far apart ARE N
    /// independent single lines, and with DIFFERENT widths each mode must reproduce kernel A's own
    /// single-line answer <i>for its own width</i>. That is what makes this the oracle worth having:
    /// nothing about it works if the modal decomposition mixes the conductors up.
    /// </summary>
    [Fact]
    public void G2_ThreeFarApartConductorsOfDifferentWidths_ReproduceThreeIndependentSingleLines()
    {
        double[] widths = [2.9e-3, 1.4e-3, 0.6e-3];
        var p = EmProblemBuilders.Fr4Multiconductor(widths, gap: 30e-3);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        Assert.Equal(3, modes.ModeCount);

        // Each mode is dominated by ONE conductor when they are uncoupled; match them up by that.
        foreach (double w in widths)
        {
            var single = Extract(EmProblemBuilders.Microstrip(w, 1.6e-3, 35e-6, 4.4));
            double z0Single = Math.Sqrt(single.LPerM / single.CPerM);

            double best = double.MaxValue;
            int bestMode = -1;
            for (int m = 0; m < 3; m++)
            {
                double d = Math.Abs(modes.Z0(m) - z0Single);
                if (d < best) { best = d; bestMode = m; }
            }

            output.WriteLine($"W = {w * 1e3:F2} mm: single line Z₀ = {z0Single:F2} Ω, ε_eff = {single.Eeff:F3}; " +
                             $"mode {bestMode} Z₀ = {modes.Z0(bestMode):F2} Ω, ε_eff = {modes.Eeff[bestMode]:F3}");

            Assert.Equal(z0Single,    modes.Z0(bestMode),   z0Single * 0.01);
            Assert.Equal(single.Eeff, modes.Eeff[bestMode], single.Eeff * 0.01);
        }
    }

    /// <summary>
    /// <b>Tier G2 — the same limit through the whole s-parameter path, at N = 3.</b> With the
    /// conductors far apart the 6-port degenerates into three uncoupled 2-ports, so nothing crosses
    /// between them. This is what pins the general <c>Tv·diag·Ti⁻¹</c> block assembly and the D3 port
    /// map together: a transposed map or a mis-scaled Ti would leave coupling behind here.
    /// </summary>
    [Fact]
    public void G2_ThreeFarApartConductors_HaveNoCrossCouplingInTheSMatrix()
    {
        var p = EmProblemBuilders.Fr4Multiconductor([1.4e-3, 1.4e-3, 1.4e-3], gap: 30e-3);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), [5e9],
                                               CancellationToken.None);
        var s = S(ds, 0, 6);

        for (int a = 0; a < 3; a++)
        {
            // D3: conductor a owns ports 2a and 2a+1 (zero-based).
            Assert.True(s[2 * a, 2 * a + 1].Magnitude > 0.5,
                $"conductor {a}: |S| through its own line is {s[2 * a, 2 * a + 1].Magnitude:F4}");
            for (int b = 0; b < 3; b++)
            {
                if (a == b) continue;
                Assert.True(s[2 * a, 2 * b].Magnitude < 0.03,
                    $"conductors {a}→{b} are still coupled at |S| = {s[2 * a, 2 * b].Magnitude:F4}");
            }
        }
    }

    /// <summary>
    /// <b>Tier G2 — the merged-strip limit generalises to three conductors.</b> As the gaps close,
    /// three strips of width W separated by S become one strip of width 3W + 2S. In the mode where
    /// every conductor sits at the same potential, the trio IS that single wide strip, so the total
    /// capacitance to ground must approach the wide strip's own — computed by the same solver on a
    /// geometry it knows nothing about.
    ///
    /// <para>The total is taken as <c>Σ_ij C_ij</c> over the raw Maxwell matrix, which is exactly
    /// "every conductor at 1 V" and needs no modal identification at all — the same combination
    /// L7b's two-conductor version used (2·C_even = C₁₁+C₁₂+C₂₁+C₂₂), stated for any N.</para>
    /// </summary>
    [Fact]
    public void G2_MergedStripLimit_GeneralisesToThreeConductors()
    {
        const double w = 1.0e-3, h = 1.6e-3, t = 35e-6, er = 4.4;
        double prevErr = double.MaxValue;

        foreach (double gap in new[] { 0.4e-3, 0.2e-3, 0.1e-3, 0.05e-3 })
        {
            var trio = Extract(EmProblemBuilders.MulticonductorMicrostrip([w, w, w], gap, h, t, er));
            double total = 0;
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                total += trio.CComplex[i, j].Real;

            double wide = Extract(EmProblemBuilders.Microstrip(3 * w + 2 * gap, h, t, er)).CPerM;

            double err = Math.Abs(total - wide) / wide;
            output.WriteLine($"S = {gap * 1e6,4:F0} µm: ΣC_ij = {total:E4}, " +
                             $"C(single {3 * w + 2 * gap:E2} m strip) = {wide:E4}, error {err:P3}");

            Assert.True(err < prevErr,
                $"S = {gap * 1e6:F0} µm: error {err:P3} did not improve on {prevErr:P3}");
            prevErr = err;
        }

        Assert.True(prevErr < 0.01, $"the tightest gap still misses the merged limit by {prevErr:P3}");
    }

    /// <summary>
    /// <b>Tier G2 — reciprocity, and the answer to the brief's own question about whether it
    /// survives as a STRUCTURAL property: it does, exactly, and here is why.</b>
    ///
    /// <para>R-gen-2 warns that a general <c>Tv·diag·Ti⁻¹</c> does not obviously preserve
    /// <c>S = Sᵀ</c>. It does, because <c>Ti</c> is the biorthogonal partner up to a per-mode scale:
    /// <c>Ti = (Tvᵀ)⁻¹·diag(e)</c> gives <c>Ti⁻¹ = diag(1/e)·Tvᵀ</c>, so every block is
    /// <c>Tv·diag(x/e)·Tvᵀ</c> — symmetric for ANY Tv, not just a symmetric one. Assembled as
    /// <c>Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]</c>, so the [i,j] and [j,i] entries come out
    /// <b>bit-identical</b> — asserted here as exact equality, not a tolerance.</para>
    ///
    /// <para><b>The precise strength of the claim, stated rather than left to be assumed.</b> The
    /// 2N-port <i>Z</i> is symmetric by construction, bit for bit. <i>S</i> is not: it comes from
    /// <see cref="RFNetwork.ZToS"/>, which inverts a matrix, so it is symmetric only to that
    /// routine's own numerical tolerance (measured well under 1e-12). <b>That is exactly the
    /// guarantee L7b had</b> — its block construction was structurally symmetric too and its own
    /// reciprocity gate likewise used 1e-12 on S — so nothing about reciprocity was weakened by
    /// generalising the transform. R-mom-14's "reciprocity is structural rather than hoped for"
    /// stands, at the same strength, at any N.</para>
    /// </summary>
    [Fact]
    public void G2_TheSixPortZIsStructurallySymmetric_AndSFollowsToSolverTolerance()
    {
        var p = EmProblemBuilders.Fr4Multiconductor([1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3, tanD: 0.02);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        // The structural half: the block transform itself, bit-identical either way round.
        foreach (double f in new[] { 1e8, 1e9, 5e9, 2e10 })
        {
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, 2 * Math.PI * f);
            var x = new Complex[3];
            for (int m = 0; m < 3; m++)
            {
                var g = Root(pt.Z[m] * pt.Y[m]) * p.LengthMeters;
                x[m] = Root(pt.Z[m] / pt.Y[m]) * Complex.Cosh(g) / Complex.Sinh(g);
            }
            var block = RlgcToSparams.ModalBlock(modes, x);
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(block[i, j].Real,      block[j, i].Real);
                Assert.Equal(block[i, j].Imaginary, block[j, i].Imaginary);
            }
        }

        // …and the numerical half, through the real ZToS path, at L7b's own tolerance.
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2),
                                               [1e8, 1e9, 5e9, 2e10], CancellationToken.None);
        for (int f = 0; f < 4; f++)
        {
            var s = S(ds, f, 6);
            for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
            {
                Assert.Equal(s[i, j].Real,      s[j, i].Real,      1e-12);
                Assert.Equal(s[i, j].Imaginary, s[j, i].Imaginary, 1e-12);
            }
        }
    }

    /// <summary><b>Tier G2 — passivity of the 2N-port, extended from L7b's 4-port version.</b></summary>
    [Fact]
    public void G2_TheSixPortIsPassive()
    {
        var p = EmProblemBuilders.Fr4Multiconductor([1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3, tanD: 0.02);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2),
                                               [1e8, 1e9, 5e9, 2e10], CancellationToken.None);
        for (int f = 0; f < 4; f++)
        {
            double sigmaMax = RfCore.RFNetwork.Passivity(S(ds, f, 6));
            Assert.True(sigmaMax <= 1.0 + 1e-9,
                $"σ_max(S) = {sigmaMax:F9} exceeds 1 — the 6-port is active");
        }
    }

    /// <summary>
    /// <b>Tier G2 — losslessness.</b> With perfect metal and zero tanδ every column of the 6-port S
    /// is unitary. This is what breaks first if the general superposition drops or double-counts a
    /// factor anywhere in <c>Tv·diag·Ti⁻¹</c>.
    /// </summary>
    [Fact]
    public void G2_TheSixPortIsLosslessWhenEveryLossIsIdeal()
    {
        var p = EmProblemBuilders.Fr4Multiconductor(
            [1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3,
            sigmaSm: double.PositiveInfinity, groundSigmaSm: double.PositiveInfinity);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2),
                                               [1e8, 1e9, 5e9, 2e10], CancellationToken.None);
        for (int f = 0; f < 4; f++)
        {
            var s = S(ds, f, 6);
            for (int j = 0; j < 6; j++)
            {
                double sum = 0;
                for (int i = 0; i < 6; i++) sum += s[i, j].Magnitude * s[i, j].Magnitude;
                Assert.Equal(1.0, sum, 1e-9);
            }
        }

        foreach (double a in ds["tline.AttenDbPerM"].RealValues) Assert.Equal(0.0, a, 1e-12);
        foreach (double r in ds["tline.Rpul"].RealValues)         Assert.Equal(0.0, r, 1e-15);
        foreach (double g in ds["tline.Gpul"].RealValues)         Assert.Equal(0.0, g, 1e-15);
    }

    /// <summary>
    /// <b>Tier G2 / R-gen-3 — normalisation invariance, the test that catches a wrong Ti.</b> An
    /// eigensolver returns eigenvectors of arbitrary scale; the terminal answer cannot depend on it.
    /// Scaling column m of Tv by c scales Ti's column by c too, so Ti⁻¹'s row scales by 1/c and
    /// <c>Zc_m</c> — which carries the compensating factor — leaves the block product unchanged.
    ///
    /// <para>Driven through the production derivation
    /// (<see cref="ModalDecomposition.FromVoltageModalMatrix"/> and
    /// <see cref="RlgcToSparams.ModalBlock"/>) rather than a restatement of it, with a deliberately
    /// pathological spread — 10³ against 10⁻³ against 1.</para>
    /// </summary>
    [Fact]
    public void G2_TheBlockTransform_IsInvariantToEigenvectorScaling()
    {
        var p = EmProblemBuilders.Fr4Multiconductor([1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3, tanD: 0.02);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        double[] vicious = [1e3, 1e-3, 1.0];
        var scaled = new Mat<double>(3, 3);
        for (int k = 0; k < 3; k++)
        for (int m = 0; m < 3; m++)
            scaled[k, m] = modes.Tv[k, m] * vicious[m];

        var rebuilt = ModalDecomposition.FromVoltageModalMatrix(rlgc, scaled, modes.Lambda);

        const double w = 2 * Math.PI * 5e9;
        var a = ModalDecomposition.EvaluateAt(rlgc, modes,   w);
        var b = ModalDecomposition.EvaluateAt(rlgc, rebuilt, w);

        // γ_m is invariant outright; Zc_m is invariant under THIS Ti rule (that is the point of it).
        var xa = new Complex[3];
        var xb = new Complex[3];
        for (int m = 0; m < 3; m++)
        {
            var ga = Root(a.Z[m] * a.Y[m]);
            var gb = Root(b.Z[m] * b.Y[m]);
            Assert.Equal(ga.Real, gb.Real, Math.Abs(ga.Real) * 1e-9 + 1e-30);
            Assert.Equal(ga.Imaginary, gb.Imaginary, Math.Abs(ga.Imaginary) * 1e-9);

            xa[m] = Root(a.Z[m] / a.Y[m]);
            xb[m] = Root(b.Z[m] / b.Y[m]);
        }

        var blockA = RlgcToSparams.ModalBlock(modes,   xa);
        var blockB = RlgcToSparams.ModalBlock(rebuilt, xb);
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            double tol = blockA[i, j].Magnitude * 1e-9 + 1e-18;
            Assert.Equal(blockA[i, j].Real,      blockB[i, j].Real,      tol);
            Assert.Equal(blockA[i, j].Imaginary, blockB[i, j].Imaginary, tol);
        }
    }

    /// <summary>
    /// <b>Tier G2 / R-gen-8 — the tline group gets a MODE AXIS, not N named scalars.</b> Rank-2
    /// <c>[freq, mode]</c> cubes for every per-mode quantity, plus <c>ModeCouplingResidual</c> over
    /// <c>[freq]</c>.
    /// </summary>
    [Fact]
    public void G2_TheTlineGroup_CarriesAModeAxis_AndTheCouplingResidual()
    {
        double[] freqs = [1e9, 5e9, 1e10];
        var p = EmProblemBuilders.Fr4Multiconductor([1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3, tanD: 0.02);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), freqs,
                                               CancellationToken.None);

        foreach (string name in new[] { "Zc", "Gamma", "Eeff", "AttenDbPerM", "Rpul", "Lpul", "Gpul", "Cpul" })
        {
            var cube = ds[$"tline.{name}"];
            Assert.Equal(2, cube.Rank);
            Assert.Equal("freq", cube.Axes[0].Name);
            Assert.Equal("mode", cube.Axes[1].Name);
            Assert.Equal(freqs.Length, cube.Axes[0].Length);
            Assert.Equal(3, cube.Axes[1].Length);
        }

        var res = ds["tline.ModeCouplingResidual"];
        Assert.Equal(1, res.Rank);
        Assert.Equal("freq", res.Axes[0].Name);
        Assert.Equal(freqs.Length, res.Axes[0].Length);

        // R-gen-8's decision: the even/odd names belong to a PAIR and are not published for three.
        Assert.False(ds.Contains("tline.ZcEven"));
    }

    /// <summary>
    /// <b>Tier G2 / R-gen-8 — the <c>…Even</c>/<c>…Odd</c> aliases survive for N = 2.</b> A
    /// coupled-line designer thinks in even and odd, and every existing Data Display trace pointing
    /// at <c>tline.ZcEven</c> keeps working. They are slices of the same mode-axis arrays, so a
    /// second name for one number cannot drift from it.
    /// </summary>
    [Fact]
    public void G2_ForAPair_TheEvenOddAliasesAreSlicesOfTheModeAxis()
    {
        double[] freqs = [1e9, 5e9];
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, Gap, tanD: 0.02);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), freqs,
                                               CancellationToken.None);

        var zc = ds["tline.Zc"].ComplexValues;              // [freq, mode], mode fastest
        var even = ds["tline.ZcEven"].ComplexValues;
        var odd  = ds["tline.ZcOdd"].ComplexValues;

        for (int f = 0; f < freqs.Length; f++)
        {
            var pair = new[] { zc[f * 2], zc[f * 2 + 1] };
            Assert.Contains(even[f], pair);
            Assert.Contains(odd[f],  pair);
            Assert.NotEqual(even[f], odd[f]);
            // Z_o < Z_e for every real edge-coupled line — R-cpl-9's sign gate, unchanged.
            Assert.True(odd[f].Real < even[f].Real);
        }
    }

    // ══ Tier G3 — the degenerate and near-degenerate cases (R-gen-6) ═══════════════════════════

    /// <summary>
    /// <b>Tier G3 / R-gen-6 — degenerate eigenvalues are GUARANTEED here, not a corner case.</b> Two
    /// identical conductors far apart have two modes with the same velocity, so λ is repeated and the
    /// eigenvectors are not unique: any linear combination spans the same subspace. That
    /// configuration is L7b's own far-apart gate, so it is not hypothetical.
    ///
    /// <para>The terminal answer stays correct — R-gen-3's invariance covers a repeated-eigenvalue
    /// subspace too — which is exactly what this asserts: the eigenvalue gap collapses monotonically
    /// as the conductors separate (so the eigenvectors become genuinely arbitrary within the
    /// subspace), and the 4-port is still two uncoupled lines throughout.</para>
    ///
    /// <para><b>Exact degeneracy is a limit, not a fixture.</b> Two conductors at a finite spacing
    /// always couple a little, so λ₁ = λ₂ is approached rather than reached; the measured relative
    /// gap falls from ~4e-3 at 30 mm to well under that as they part. Asserting the approach is the
    /// honest form of R-gen-6's gate, and it exercises the ill-conditioned eigenvector regime just
    /// as effectively.</para>
    /// </summary>
    [Fact]
    public void G3_TwoIdenticalConductorsFarApart_ApproachDegeneracy_AndStillSolveCorrectly()
    {
        double prev = double.MaxValue;
        foreach (double gap in new[] { 5e-3, 15e-3, 40e-3 })
        {
            var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, gap);
            var modes = ModalDecomposition.DecomposeGeneral(Extract(p));

            double rel = Math.Abs(modes.Lambda[0] - modes.Lambda[1]) /
                         Math.Max(modes.Lambda[0], modes.Lambda[1]);
            output.WriteLine($"S = {gap * 1e3,4:F0} mm: λ = {modes.Lambda[0]:E6}, {modes.Lambda[1]:E6} " +
                             $"(relative gap {rel:E2})");
            Assert.True(rel < prev,
                $"S = {gap * 1e3:F0} mm: the modes must approach degeneracy as the conductors part " +
                $"— gap {rel:E2} did not improve on {prev:E2}");
            prev = rel;

            var s = S(new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), [5e9],
                                                    CancellationToken.None), 0, 4);
            Assert.True(s[0, 1].Magnitude > 0.5, $"|S21| = {s[0, 1].Magnitude:F4} lost the through path");
            if (gap >= 15e-3)
            {
                Assert.True(s[0, 2].Magnitude < 0.03, $"|S31| = {s[0, 2].Magnitude:F4} is not uncoupled");
                Assert.True(s[0, 3].Magnitude < 0.03, $"|S41| = {s[0, 3].Magnitude:F4} is not uncoupled");
            }
        }
        Assert.True(prev < 5e-3, $"the widest spacing is still {prev:E2} from degenerate");
    }

    /// <summary>
    /// <b>Tier G3 — the numerically nastiest case: NEARLY degenerate.</b> A gap wide enough that the
    /// modes are almost but not quite the same velocity leaves the eigenvectors ill-conditioned even
    /// though the answer is well-conditioned. The terminal 4-port must still be reciprocal, passive
    /// and correct.
    /// </summary>
    [Fact]
    public void G3_NearlyDegenerateModes_StillGiveAWellConditionedTerminalAnswer()
    {
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, 8e-3, tanD: 0.02);
        var rlgc  = Extract(p);
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        double rel = Math.Abs(modes.Lambda[0] - modes.Lambda[1]) /
                     Math.Max(modes.Lambda[0], modes.Lambda[1]);
        output.WriteLine($"relative λ gap = {rel:E3}");
        Assert.InRange(rel, 1e-6, 5e-2);

        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2),
                                               [1e8, 1e9, 5e9, 2e10], CancellationToken.None);
        for (int f = 0; f < 4; f++)
        {
            var s = S(ds, f, 4);
            for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.Equal(s[i, j].Real, s[j, i].Real, 1e-12);
            Assert.True(RfCore.RFNetwork.Passivity(s) <= 1.0 + 1e-9);
        }
    }

    /// <summary>
    /// <b>Tier G3 / R-gen-7 — mode order is stable across a frequency sweep.</b> A <c>Zc[mode]</c>
    /// trace that swaps modes mid-sweep is a plot nobody can read.
    ///
    /// <para>Under Route A this is true <b>by construction and not by a sorting heuristic</b>: Tv
    /// comes from the lossless problem, which has no ω in it, so there is exactly one ordering
    /// decision for the whole sweep. That is a real advantage over a Route B, which would produce a
    /// per-frequency Tv and make mode TRACKING a genuine problem rather than a free one. Asserted on
    /// a near-degenerate pair, where a per-frequency sort would be most likely to flip.</para>
    /// </summary>
    [Fact]
    public void G3_ModeOrderIsStableAcrossTheSweep_ByConstruction()
    {
        double[] freqs = [1e6, 1e7, 1e8, 1e9, 5e9, 1e10, 2e10];
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, 8e-3, tanD: 0.02);
        var ds = new QuasiStaticKernel().Solve(p, EmMeshSettings.Default.Refined(2), freqs,
                                               CancellationToken.None);

        var zc = ds["tline.Zc"].ComplexValues;              // [freq, mode]
        for (int f = 0; f < freqs.Length; f++)
        {
            double m0 = zc[f * 2].Real, m1 = zc[f * 2 + 1].Real;
            Assert.True(m0 < m1,
                $"f = {freqs[f]:G3} Hz: mode 0 (Zc = {m0:F3}) and mode 1 (Zc = {m1:F3}) have swapped " +
                "— the mode axis must keep its identity for the whole sweep");
        }
    }

    // ══ Tier G4 — refusals stay specific ══════════════════════════════════════════════════════

    /// <summary>
    /// <b>Tier G4 / R-gen-9 — over the conductor ceiling, refused BY NAME with the number and what
    /// bounds it.</b> An unbounded N with no message is how a user discovers a limit by waiting.
    /// </summary>
    [Fact]
    public void G4_OverTheConductorCeiling_IsRefusedByNameWithTheNumberAndWhatBoundsIt()
    {
        var widths = new double[QuasiStaticKernel.MaxSignalConductors + 1];
        Array.Fill(widths, 0.5e-3);

        var verdict = new QuasiStaticKernel().CanSolve(
            EmProblemBuilders.Fr4Multiconductor(widths, 0.3e-3));

        Assert.False(verdict.Ok);
        Assert.Contains(widths.Length.ToString(), verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(QuasiStaticKernel.MaxSignalConductors.ToString(), verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("dense boundary-element solve", verdict.Reason, StringComparison.Ordinal);
        // L8e/D6: this one was WRONG, not merely stale. It promised "the sparse or iterative solver
        // that arrives with the full-wave kernel at L8" — kernel B is DENSE too, with its own
        // unknown ceiling, and brings no sparse or iterative solve at all.
        //
        // L9e/M4 — UPDATED, NOT LOOSENED. It then said compression "is scheduled with the general
        // layered stack at L9". L9 arrived; compression was MEASURED and deliberately not built
        // (§L9e's ACA measurement), so the refusal now says that rather than naming a phase.
        Assert.Contains("not built", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("at L8", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("at L9", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary><b>Tier G4.</b> At the ceiling exactly, it is accepted — the boundary is not off by one.</summary>
    [Fact]
    public void G4_AtTheConductorCeiling_IsAccepted()
    {
        var widths = new double[QuasiStaticKernel.MaxSignalConductors];
        Array.Fill(widths, 0.5e-3);

        var verdict = new QuasiStaticKernel().CanSolve(
            EmProblemBuilders.Fr4Multiconductor(widths, 0.3e-3));
        Assert.True(verdict.Ok, verdict.Reason);
    }

    /// <summary>
    /// <b>Tier G4 / R-gen-9 — the geometric-symmetry check SURVIVES; it stopped being a refusal.</b>
    /// It still answers "is this pair mirror-symmetric?", which is what makes L7b's exact even/odd
    /// construction applicable as a test oracle; only its callers changed.
    /// </summary>
    [Fact]
    public void G4_CheckGeometricSymmetry_StillAnswersTheQuestion_ButNoLongerRefuses()
    {
        var symmetric  = EmProblemBuilders.Fr4CoupledMicrostrip(W, Gap);
        var asymmetric = EmProblemBuilders.Fr4CoupledMicrostrip(W, Gap, w2: 2 * W);

        Assert.Null(ModalDecomposition.CheckGeometricSymmetry(symmetric));
        Assert.Contains("not mirror-symmetric",
                        ModalDecomposition.CheckGeometricSymmetry(asymmetric)!, StringComparison.Ordinal);

        var kernel = new QuasiStaticKernel();
        Assert.True(kernel.CanSolve(symmetric).Ok);
        Assert.True(kernel.CanSolve(asymmetric).Ok,
            "an asymmetric pair is what the general modal decomposition exists for — it must no " +
            "longer be refused");
    }

    /// <summary>
    /// <b>Tier G4 — every L7b refusal that is NOT superseded still fires with its own wording.</b>
    /// Narrowing two refusals must not have quietly widened the rest.
    /// </summary>
    [Fact]
    public void G4_TheRefusalsThatAreNotSuperseded_StillFireWithTheirOwnWording()
    {
        var kernel = new QuasiStaticKernel();
        var p = EmProblemBuilders.Fr4CoupledMicrostrip(W, Gap);

        // A conductor owning the wrong number of ports — the D3 statement, unchanged.
        var wrongPorts = kernel.CanSolve(p with { Ports = [p.Ports[0], p.Ports[1], p.Ports[2]] });
        Assert.False(wrongPorts.Ok);
        Assert.Contains("near end", wrongPorts.Reason, StringComparison.Ordinal);

        // A zero-thickness sheet — Wheeler has no interior to recede into.
        var flat = kernel.CanSolve(p with
        {
            Conductors =
            [
                p.Conductors[0] with { Outline = [new EmPoint(-1e-3, 1.6e-3), new EmPoint(1e-3, 1.6e-3)] },
                p.Conductors[1],
            ],
        });
        Assert.False(flat.Ok);

        // A vertical dielectric boundary — the 2.5D premise, which arrives at L8.
        var gapped = kernel.CanSolve(p with
        {
            Regions =
            [
                new EmDielectricRegion(double.NegativeInfinity, 1.0e-3, new EmMaterial(4.4, 0)),
                new EmDielectricRegion(1.2e-3, double.PositiveInfinity, EmMaterial.Air),
            ],
        });
        Assert.False(gapped.Ok);
        // L9e/M4 — UPDATED, NOT LOOSENED: the sloped-boundary refusal no longer names a phase,
        // because after L9 both halves of the old wording were false. It names the premise instead.
        Assert.Contains("3-D formulation", gapped.Reason, StringComparison.Ordinal);
    }

    // ══ R-gen-10 — the extractor and EmProblem need no change ═════════════════════════════════

    /// <summary>
    /// <b>R-gen-10.</b> N conductors require no new input: the cross-section already carries them,
    /// and <see cref="RlgcExtractor"/>'s fill count follows its own stated rule
    /// (<c>2 + N + ground</c>) at N = 3 exactly as it did at N = 2 — R-mom-11's counter gate is
    /// unchanged, not merely still passing.
    /// </summary>
    [Fact]
    public void RGen10_TheExtractorNeedsNoChange_AndTheFillCountFollowsItsOwnRule()
    {
        var rlgc = Extract(EmProblemBuilders.Fr4Multiconductor([1.4e-3, 0.9e-3, 1.8e-3], 0.3e-3));
        Assert.Equal(3, rlgc.ConductorCount);
        Assert.Equal(2 + 3 + 1, rlgc.MatrixFillCount);
        Assert.Equal(4, rlgc.LossSurfaces.Count);      // three conductors + the ground plane
        for (int k = 0; k < 3; k++)
            Assert.True(rlgc.LossSurfaces[k].DLdn[k, k] > 0,
                $"receding conductor {k} must raise its OWN inductance");
    }
}
