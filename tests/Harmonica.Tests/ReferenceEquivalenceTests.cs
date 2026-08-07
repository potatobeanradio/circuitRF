using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// <b>Tier 3, and M6's gate.</b> harmonicaRF must not be a second, divergent answer to a question
/// circuitRF already answers: on the SAME configuration it and the shipped path have to agree on
/// Pout, Gain, DE, PAE and the intrinsic spectra.
///
/// <para><b>The reference is a genuinely different route to the same circuit.</b> It is a
/// <c>Tuner</c>-based netlist — the source and load terminations stamped as real components with
/// their own internal bias tees, the RF drive owned by the source tuner — extracted by
/// <c>HbLinearExtractor</c> exactly as every shipping HB and loadpull run does. harmonicaRF's netlist
/// has no terminations in it at all and closes them algebraically. So the two share the Newton loop
/// and the device, and differ in the topology of the netlist, in how <c>Y_NN</c> is obtained, and in
/// how the drive reaches the gate.</para>
///
/// <para><b>The FOMs are not re-derived on either side.</b> Both go through
/// <c>LoadpullEngine.ComputeFoms</c>, which is the point: if harmonicaRF had its own definition of
/// Pout, this test would be comparing two definitions rather than two solves.</para>
/// </summary>
[Collection("HarmonicaBenchmarks")]
public sealed class ReferenceEquivalenceTests(ITestOutputHelper output)
{
    private const double F0 = 2e9;
    private const int    K  = 5;

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>Hero 2's GaN HEMT equations, folded flat so both fixtures state the identical device.</summary>
    private const string DrainEq =
        "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))" +
        "*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2";
    private const string GateEq = "_v1/50";

    private const double Vgg = -3.05, Vdd = 48;
    private static readonly Complex Zs = new(25, 0);
    private static readonly Complex Zl = new(80, 10);
    private static readonly Complex Zl2 = new(1, 0);

    // ── the two routes ────────────────────────────────────────────────────────

    /// <summary>harmonicaRF, at ideal-bias values so the two circuits are literally the same one.</summary>
    private static (HarmonicaContext Ctx, TerminationSet Terms) Harmonica()
    {
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = GateEq,
                    ["I[2,0]"] = DrainEq,
                },
            },
            Bias     = new BiasSpec { Vgs = Vgg, Vds = Vdd },
            Settings = new HarmonicaSettings
            {
                HarmonicCount = K, FrequencyHz = F0, Tol = 1e-8,
                // The Tuner's own ideal bias tee, matched exactly: C = 1 F, L = 1 H.
                BiasChokeHenries = HarmonicaNetlist.IdealChokeH,
                DcBlockFarads    = HarmonicaNetlist.IdealBlockF,
            },
        };

        var terms = new TerminationSet(K);
        terms.Set(TerminationSide.Source, 1, Zs);
        terms.Set(TerminationSide.Load,   1, Zl);
        terms.Set(TerminationSide.Load,   2, Zl2);

        return (HarmonicaContext.Create(model), terms);
    }

    /// <summary>
    /// The shipped path: a Tuner-based netlist through <c>HbEngine.RunSinglePoint</c>, with the
    /// source tuner driving and the true delivered current recovered from its branch unknowns —
    /// the same machinery <c>LoadpullEngine.RunOneTermination</c> uses inside its ladder.
    /// </summary>
    private static (HbEngine Engine, HbAnalysisParams P, ElaboratedNetlist Netlist,
                    TunerModel Src, TunerModel Load) Reference()
    {
        string cnl = $"""
            RFfreq = {N(F0)}

            Tuner:Src   n_gate 0   Z[1]={N(Zs.Real)}   Zdefault=1e-6   BiasTee=on   Vbias={N(Vgg)}
            Tuner:Load  n_drain 0  Z[1]=complex({N(Zl.Real)},{N(Zl.Imaginary)})  Z[2]={N(Zl2.Real)}  Zdefault=1e-6  BiasTee=on  Vbias={N(Vdd)}

            SDD:M1  n_gate 0  n_drain 0  I[1,0]={GateEq}  I[2,0]={DrainEq}

            analysis HB1 type=hb Tone=RFfreq MaxHarm={K} Tol=1e-8
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p  = HbEngine.Resolve((HarmonicBalanceAnalysis)tb.Analyses[0],
                                  nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);

        TunerModel Tuner(string name)
            => (TunerModel)nl.Components.Single(c => c.InstancePath == name).Model;

        var src  = Tuner("Src");
        var load = Tuner("Load");
        src.SetRole(TunerRole.Source);
        load.SetRole(TunerRole.Load);
        load.SetTone(F0);

        return (new HbEngine(nl, tb), p, nl, src, load);
    }

    private static double RelDiff(double a, double b)
    {
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale == 0 ? 0 : Math.Abs(a - b) / scale;
    }

    private static double RelDiff(Complex a, Complex b)
    {
        double scale = Math.Max(a.Magnitude, b.Magnitude);
        return scale == 0 ? 0 : (a - b).Magnitude / scale;
    }

    // ── TIER 3 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-6.0)]
    [InlineData(4.0)]
    [InlineData(14.0)]
    public void Tier3_TheTwoRoutesAgreeOnEveryFigureOfMerit(double pavlDbm)
    {
        // ── harmonicaRF ───────────────────────────────────────────────────────
        var (ctx, terms) = Harmonica();
        var hPoint = ctx.Solve(terms, pavlDbm);
        Assert.True(hPoint.Converged, $"harmonicaRF did not converge: ‖F‖ = {hPoint.Residual:E3}");
        var h = PinSearch.Measure(ctx, hPoint, terms);

        // ── the shipped path ──────────────────────────────────────────────────
        var (engine, p, nl, src, _) = Reference();
        double pavlW = Math.Pow(10.0, (pavlDbm - 30.0) / 10.0);
        src.SetSourceDrive(F0, pavlW);

        var settings = new AnalysisSettings { InductanceRegularization = RegularizationMode.Always };
        var sr = engine.RunSinglePoint(p, null, settings);
        Assert.True(sr.Converged, $"the reference did not converge: {sr.FailReason}");

        var extractor = new HbLinearExtractor(nl, settings);
        int gateIdx  = Array.IndexOf(extractor.InterfaceNodes, nl.Nodes.GetOrAssign("n_gate"));
        int drainIdx = Array.IndexOf(extractor.InterfaceNodes, nl.Nodes.GetOrAssign("n_drain"));

        // The reference's own true delivered current: the source tuner's Z_Port branch minus its
        // choke branch, by KCL at the DUT node — LoadpullEngine's ComputeSourceInputCurrent.
        var iin = new Complex[K + 1];
        for (int k = 0; k <= K; k++)
        {
            var x = sr.BackSolver!.GetSolution(k, 0);
            iin[k] = x[src.SourceZPortBranchIndex] - x[src.ChokeBranchIndex];
        }

        var rFoms = LoadpullEngine.ComputeFoms(sr.V, iin, sr.INl, drainIdx, gateIdx, pavlW, K);
        double rPdc = sr.V[drainIdx, 0].Real * sr.INl[drainIdx, 0].Real
                    + sr.V[gateIdx,  0].Real * sr.INl[gateIdx,  0].Real;
        double rDe  = rPdc > 1e-9 ? rFoms.PoutW / rPdc : 0;
        double rPae = rPdc > 1e-9 ? (rFoms.PoutW - rFoms.PinDeliveredW) / rPdc : 0;

        // ── compare ───────────────────────────────────────────────────────────
        double hPoutDbm = 10 * Math.Log10(h.PoutW) + 30;
        double rPoutDbm = 10 * Math.Log10(rFoms.PoutW) + 30;

        output.WriteLine($"Pavl = {pavlDbm:F1} dBm");
        output.WriteLine($"  Pout    harmonicaRF {hPoutDbm,8:F5} dBm   reference {rPoutDbm,8:F5} dBm   " +
                         $"Δ {Math.Abs(hPoutDbm - rPoutDbm):E2} dB");
        output.WriteLine($"  Gt      harmonicaRF {h.Foms.GtDb,8:F5} dB    reference {rFoms.GtDb,8:F5} dB    " +
                         $"Δ {Math.Abs(h.Foms.GtDb - rFoms.GtDb):E2} dB");
        output.WriteLine($"  Gp      harmonicaRF {h.Foms.GpDb,8:F5} dB    reference {rFoms.GpDb,8:F5} dB    " +
                         $"Δ {Math.Abs(h.Foms.GpDb - rFoms.GpDb):E2} dB");
        output.WriteLine($"  DE      harmonicaRF {h.De * 100,8:F5} %     reference {rDe * 100,8:F5} %     " +
                         $"rel {RelDiff(h.De, rDe):E2}");
        output.WriteLine($"  PAE     harmonicaRF {h.Pae * 100,8:F5} %     reference {rPae * 100,8:F5} %     " +
                         $"rel {RelDiff(h.Pae, rPae):E2}");
        output.WriteLine($"  Pdc     harmonicaRF {h.PdcW,8:F5} W     reference {rPdc,8:F5} W     " +
                         $"rel {RelDiff(h.PdcW, rPdc):E2}");

        Assert.True(hPoutDbm > 0, "the fixture must actually produce power, or agreement is vacuous");

        Assert.True(Math.Abs(hPoutDbm - rPoutDbm) < 1e-3, $"Pout differs by {Math.Abs(hPoutDbm - rPoutDbm):E3} dB");
        Assert.True(Math.Abs(h.Foms.GtDb - rFoms.GtDb) < 1e-3, "Gt differs");
        Assert.True(Math.Abs(h.Foms.GpDb - rFoms.GpDb) < 1e-3, "Gp differs");
        Assert.True(RelDiff(h.De,  rDe)  < 1e-4, $"DE differs by {RelDiff(h.De, rDe):E3} relative");
        Assert.True(RelDiff(h.Pae, rPae) < 1e-4, $"PAE differs by {RelDiff(h.Pae, rPae):E3} relative");
    }

    [Fact]
    public void Tier3_TheIntrinsicSpectraAgreeToo()
    {
        // The FOMs are scalars at the fundamental; the intrinsic spectra are what harmonicaRF exists
        // to show, and they carry every harmonic. Compared through the SAME conduction-only
        // definition on both sides, so this tests the two solves rather than two definitions.
        const double pavl = 8.0;

        var (ctx, terms) = Harmonica();
        var hPoint = ctx.Solve(terms, pavl);
        Assert.True(hPoint.Converged);

        int gridN = HbFft.GridSize(K, 1);
        var hSpec = IntrinsicPlane.Evaluate(ctx.DutComponent, hPoint.V, ctx.Interface.DeviceNodes,
                                            K, gridN, F0);

        var (engine, p, nl, src, _) = Reference();
        src.SetSourceDrive(F0, Math.Pow(10.0, (pavl - 30.0) / 10.0));
        var settings = new AnalysisSettings { InductanceRegularization = RegularizationMode.Always };
        var sr = engine.RunSinglePoint(p, null, settings);
        Assert.True(sr.Converged);

        var extractor = new HbLinearExtractor(nl, settings);
        var refDut = nl.Components.Single(c => c.InstancePath == "M1");
        var rSpec = IntrinsicPlane.Evaluate(refDut, sr.V, extractor.InterfaceNodes, K, gridN, F0);

        // Compared against each PORT'S OWN SCALE rather than entry by entry, and this is a
        // measurement decision rather than a loosened tolerance. The gate current here is `_v1/50` —
        // exactly linear — so its 2nd…5th harmonic bins are identically zero and come back as FFT
        // round-off, around 1e-18. An entry-wise relative difference between 1.1e-18 and 0 is 1.0,
        // which says nothing about whether the two solves agree. Normalising by the largest bin of
        // the same spectrum asks the question that means something; the guard below then asserts
        // those bins really ARE noise, so the normalisation cannot hide a real signal.
        double Scale(Complex[,] a, int port)
        {
            double m = 0;
            for (int k = 0; k <= K; k++) m = Math.Max(m, a[port, k].Magnitude);
            return m;
        }

        double worstI = 0, worstV = 0;
        bool sawSomethingLarge = false;

        for (int port = 0; port < 2; port++)
        {
            double iScale = Math.Max(Scale(hSpec.portCurrents, port), Scale(rSpec.portCurrents, port));
            double vScale = Math.Max(Scale(hSpec.portVoltages, port), Scale(rSpec.portVoltages, port));

            for (int k = 0; k <= K; k++)
            {
                double di = (hSpec.portCurrents[port, k] - rSpec.portCurrents[port, k]).Magnitude;
                double dv = (hSpec.portVoltages[port, k] - rSpec.portVoltages[port, k]).Magnitude;

                worstI = Math.Max(worstI, iScale > 0 ? di / iScale : 0);
                worstV = Math.Max(worstV, dv / Math.Max(vScale, 1e-30));

                if (hSpec.portVoltages[port, k].Magnitude > 1.0) sawSomethingLarge = true;

                output.WriteLine($"  port {port} k={k}:  I {hSpec.portCurrents[port, k]:G6} vs " +
                                 $"{rSpec.portCurrents[port, k]:G6}   " +
                                 $"|Δ|/scale {(iScale > 0 ? di / iScale : 0):E2}");
            }

            // The guard: a bin that the normalisation forgives must be genuinely negligible on BOTH
            // sides, not merely small on one.
            for (int k = 2; k <= K; k++)
                if (port == 0)
                {
                    Assert.True(hSpec.portCurrents[0, k].Magnitude < 1e-12 * iScale,
                        $"the gate current is linear, so bin {k} should be round-off; it is " +
                        $"{hSpec.portCurrents[0, k].Magnitude:E3} against a scale of {iScale:E3}");
                    Assert.True(rSpec.portCurrents[0, k].Magnitude < 1e-12 * iScale);
                }
        }

        output.WriteLine($"worst |ΔI_intr| / port scale = {worstI:E3};  worst |ΔV_intr| / scale = {worstV:E3}");
        Assert.True(sawSomethingLarge, "the spectra must be non-trivial");
        Assert.True(worstI < 1e-4, $"the intrinsic currents differ by {worstI:E3} of their own scale");
        Assert.True(worstV < 1e-4, $"the intrinsic voltages differ by {worstV:E3} of their own scale");
    }

    // ── TIER 8 — cost, reported ───────────────────────────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Tier8_TheCostOfAContourGrid()
    {
        // §9 item 4: the cost of a 61-point grid, with FIT and EXTRACT reported SEPARATELY from the
        // solves — §6.4.1's own obligation, because a scheduler that lumps them together degrades
        // the wrong one.
        var (ctx, terms) = Harmonica();
        var model = ctx.Model with
        {
            Settings = ctx.Model.Settings with
            {
                CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
            },
        };
        ctx.Apply(model);

        var gammas = ContourGrid.RingGrid(rings: 5, spokes: 12, maxGamma: 0.8);   // 61 points
        Assert.Equal(61, gammas.Length);

        var grid = new ContourGrid();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        grid.Build(ctx, terms, gammas);
        double solveS = sw.Elapsed.TotalSeconds;

        sw.Restart();
        grid.Fit(GridMetric.PoutDbm);
        grid.Fit(GridMetric.DrainEfficiency);
        double fitS = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var polylines = grid.Contours(GridMetric.PoutDbm, levels: 10, resolution: 256);
        double extractS = sw.Elapsed.TotalSeconds;

        output.WriteLine($"61-point grid, K = {K}, taken alone:");
        output.WriteLine($"  SOLVE   {solveS:F3} s  —  {grid.SolveCount} HB solves, " +
                         $"{(double)grid.SolveCount / gammas.Length:F1} per Γ point " +
                         $"(the uniform ladder's is ~30)");
        output.WriteLine($"  FIT     {fitS * 1e3:F2} ms  for TWO metrics, " +
                         $"{grid.FactorizationCount} kernel factorization(s)");
        output.WriteLine($"  EXTRACT {extractS * 1e3:F2} ms  at 256×256 → {polylines.Count} polylines");
        output.WriteLine($"  holes   {grid.HoleCount} of {gammas.Length}");
        output.WriteLine($"§0.2's target for the whole thing: 0.45 s");

        var mxp = grid.Mxp;
        var mxe = grid.Mxe;
        if (mxp is not null) output.WriteLine($"  MXP  Γ = {mxp.Point.Gamma:G4}  {mxp.Value:F2} dBm");
        if (mxe is not null) output.WriteLine($"  MXE  Γ = {mxe.Point.Gamma:G4}  {mxe.Value:F2} %");

        Assert.True(grid.ConvergedCount > 0, "the grid must produce some converged points");
        Assert.True(polylines.Count > 0, "the grid must produce some contours");
    }

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(37)]
    [InlineData(61)]
    [InlineData(200)]
    public void Tier8_FitAndExtractScaling(int n)
    {
        // §6.4.1's measurement obligation: fit and extract time reported SEPARATELY, at
        // n = 37 / 61 / 200. Synthetic nodes, because this measures the CONTOUR PIPELINE and driving
        // real HB solves would bury it under the solves — which is exactly the confusion the
        // obligation exists to prevent.
        var rng = new Random(61 + n);
        var grid = new ContourGrid();
        var gammas = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double mag = 0.8 * Math.Sqrt(rng.NextDouble());
            double ang = 2 * Math.PI * rng.NextDouble();
            gammas[i] = Complex.FromPolarCoordinates(mag, ang);
        }
        ContourGridTests.SeedSyntheticFor(grid, gammas, holeIndex: n / 3);

        // Warm the JIT on a throwaway grid of the same shape, or the first timed fit reads the
        // compiler rather than the arithmetic — n = 200 came back FASTER than n = 61 without this.
        var warm = new ContourGrid();
        ContourGridTests.SeedSyntheticFor(warm, gammas, holeIndex: n / 3);
        warm.Fit(GridMetric.PoutDbm);
        warm.Contours(GridMetric.PoutDbm, levels: 10, resolution: 96);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        grid.Fit(GridMetric.PoutDbm);
        double firstFitMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        grid.Fit(GridMetric.DrainEfficiency);
        double secondFitMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var coarse = grid.Contours(GridMetric.PoutDbm, levels: 10, resolution: 96);
        double coarseMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var full = grid.Contours(GridMetric.PoutDbm, levels: 10, resolution: 256);
        double fullMs = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"n = {n}  ({grid.ConvergedCount} converged, {grid.HoleCount} hole)");
        output.WriteLine($"  FIT      first metric {firstFitMs:F3} ms (factorize + solve), " +
                         $"second metric {secondFitMs:F3} ms (solve only, cached factor)");
        output.WriteLine($"  EXTRACT   96×96 {coarseMs:F2} ms → {coarse.Count} polylines");
        output.WriteLine($"  EXTRACT 256×256 {fullMs:F2} ms → {full.Count} polylines");
        output.WriteLine($"  factorizations: {grid.FactorizationCount}");

        Assert.Equal(1, grid.FactorizationCount);
        Assert.True(secondFitMs <= Math.Max(firstFitMs, 0.05),
            $"the cached re-solve ({secondFitMs:F3} ms) should not cost more than the first fit " +
            $"({firstFitMs:F3} ms) — that is the whole point of the factorization cache");
    }
}
