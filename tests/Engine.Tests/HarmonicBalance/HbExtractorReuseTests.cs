using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// HB-P2 — the linear extractor outlives one solve, and the post-solve re-evaluation goes.
///
/// <para>Two structural claims, each asserted as a COUNT rather than a time:</para>
/// <list type="number">
/// <item>One LU factorization per harmonic per TOPOLOGY, not per solve — however many warm solves
/// run against one netlist, and whatever the drive does.</item>
/// <item>A value change in the linear partition (a loadpull tuner's per-grid-point impedance
/// override) is picked up with NO cooperation from the caller — no invalidation call anywhere —
/// because the cache validates itself by comparing the matrix it just stamped against the one each
/// stored LU was built from. The gate for that is an answer bit-identical to a fresh engine's.</item>
/// </list>
///
/// <para>Plus M3: the post-convergence per-port currents come from the last Newton device pass
/// instead of a second full evaluation of every device at every sample.</para>
/// </summary>
public sealed class HbExtractorReuseTests(ITestOutputHelper output)
{
    private static string TestDataDir(string hero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", hero);
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"testdata/{hero} not found");
    }

    private static (Library Lib, TestBench Tb, ElaboratedNetlist Netlist, HbAnalysisParams P)
        Load(string hero, string file)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir(hero), file));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        return (lib, tb, netlist, p);
    }

    /// <summary>
    /// Re-drive the netlist's tone sources at a new available power, the way a Pin ladder does:
    /// the drive amplitude is a global expression, so re-evaluating the globals and handing them to
    /// every tone source moves I_src and NOTHING in the matrix.
    /// </summary>
    private static void SetDrive(ElaboratedNetlist netlist, double vsMag)
    {
        var globals = new Dictionary<string, Value>(netlist.ResolvedGlobals, StringComparer.Ordinal)
        {
            ["Vs_mag"] = new Value(vsMag),
        };
        foreach (var ec in netlist.Components)
            if (ec.Model is ToneSourceModelBase tsm) tsm.ReevaluateFromGlobals(globals);
    }

    private static double MaxAbsDiff(Complex[,] a, Complex[,] b)
    {
        double m = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                m = Math.Max(m, (a[i, j] - b[i, j]).Magnitude);
        return m;
    }

    // ── 1. Same answer, one factorization per harmonic ────────────────────────

    [Fact]
    public void WarmSolvesOnOneEngine_MatchFreshEnginesExactly_AndFactorizeEachHarmonicOnce()
    {
        var (lib, tb, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        int K = p.MaxHarmonic;

        // A rising drive ladder, the shape a Pin sweep or a loadpull's inner loop takes.
        double[] drives = Enumerable.Range(0, 20).Select(i => 0.5 + 0.15 * i).ToArray();

        // Reused engine: one extractor for the whole ladder, warm-started throughout.
        var shared = new HbEngine(netlist, tb);
        var sharedV = new Complex[drives.Length][,];
        Complex[,]? warm = null;
        for (int i = 0; i < drives.Length; i++)
        {
            SetDrive(netlist, drives[i]);
            var sp = shared.RunSinglePoint(p, warm);
            Assert.True(sp.Converged, $"shared engine did not converge at Vs={drives[i]}");
            sharedV[i] = sp.V;
            warm = sp.V;
        }

        // Reference: a FRESH engine (and therefore a fresh extractor and fresh factorizations) for
        // every rung, on its own freshly elaborated netlist, warm-started identically.
        var refNetlist = new Elaborator(lib).Elaborate(tb);
        Complex[,]? refWarm = null;
        for (int i = 0; i < drives.Length; i++)
        {
            SetDrive(refNetlist, drives[i]);
            var sp = new HbEngine(refNetlist, tb).RunSinglePoint(p, refWarm);
            Assert.True(sp.Converged, $"fresh engine did not converge at Vs={drives[i]}");

            // BIT-identical, not within a tolerance: the reused extractor hands the Newton loop the
            // same Y_NN and the same I_src, so there is nothing for the two paths to differ in.
            Assert.Equal(0.0, MaxAbsDiff(sharedV[i], sp.V));
            refWarm = sp.V;
        }

        // What ONE solve costs, for comparison — the count must not grow with the ladder.
        var singleNetlist = new Elaborator(lib).Elaborate(tb);
        SetDrive(singleNetlist, drives[0]);
        var oneEngine = new HbEngine(singleNetlist, tb);
        Assert.True(oneEngine.RunSinglePoint(p).Converged);
        int costOfOne = oneEngine.LinearFactorizations;

        output.WriteLine($"{drives.Length} warm solves, K={K}: {shared.LinearFactorizations} " +
                         $"factorizations, the same {costOfOne} a SINGLE solve costs; " +
                         "interface V bit-identical to fresh engines.");

        // The whole point: 20 solves cost exactly what one costs. (It is K+2 rather than K+1 on
        // this fixture, not K+1: Hero 2's ideal chokes pin the DC interface, so IfNecessary
        // inductance regularization engages — one speculative unregularized factorization at ω = 0
        // plus the regularized one. That happens ONCE; the mode is sticky thereafter.)
        Assert.Equal(costOfOne, shared.LinearFactorizations);
        Assert.Equal(K + 2, shared.LinearFactorizations);
    }

    // ── 2. A termination change is picked up with no invalidation call ────────

    [Fact]
    public void TunerImpedanceOverride_IsPickedUpWithNoInvalidationCall_AndRefactorsOneHarmonic()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir("Hero3"), "hero3.cnl"));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().First();
        var lpp = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
        var ctx = new LoadpullEngine(netlist, tb).PrepareContext(lpp);

        double pavlW = Math.Pow(10.0, (lpp.PinStartDbm - 30.0) / 10.0);
        int K = ctx.HbParams.MaxHarmonic;

        var shared = new HbEngine(netlist, tb);

        // First grid point — cold, so every harmonic factorizes once.
        ctx.SweptModel.SetHarmonicOverride(lpp.TuneHarm, lpp.Grid.Points[0].Z);
        ctx.SrcModel.SetSourceDrive(lpp.ToneHz, pavlW);
        var first = shared.RunSinglePoint(ctx.HbParams, null, ctx.SolveSettings);
        Assert.True(first.Converged);
        int afterFirst = shared.LinearFactorizations;
        Assert.Equal(K + 1, afterFirst);

        // Same grid point, a HIGHER drive: I_src moves, the matrix does not. Nothing may refactor.
        ctx.SrcModel.SetSourceDrive(lpp.ToneHz, pavlW * 4.0);
        var driveUp = shared.RunSinglePoint(ctx.HbParams, first.V, ctx.SolveSettings);
        Assert.True(driveUp.Converged);
        Assert.Equal(afterFirst, shared.LinearFactorizations);

        // A DIFFERENT termination at the tuned harmonic — and DELIBERATELY no InvalidateLinear call,
        // which is the property under test. Exactly one harmonic's matrix moved, so exactly one
        // refactorization may happen.
        var pointN = lpp.Grid.Points.First(g => (g.Z - lpp.Grid.Points[0].Z).Magnitude > 1.0);
        ctx.SweptModel.SetHarmonicOverride(lpp.TuneHarm, pointN.Z);
        var moved = shared.RunSinglePoint(ctx.HbParams, driveUp.V, ctx.SolveSettings);
        Assert.True(moved.Converged);
        int refactors = shared.LinearFactorizations - afterFirst;

        output.WriteLine($"Γ move {lpp.Grid.Points[0].Z:F2} → {pointN.Z:F2} at harmonic " +
                         $"{lpp.TuneHarm}: {refactors} refactorization(s) out of {K + 1} harmonics.");
        Assert.Equal(1, refactors);

        // And the answer is a fresh engine's, exactly — this is what a missed invalidation would
        // silently break, and it is asserted at zero rather than at a tolerance.
        var freshNetlist = new Elaborator(lib).Elaborate(tb);
        var freshCtx = new LoadpullEngine(freshNetlist, tb).PrepareContext(lpp);
        freshCtx.SweptModel.SetHarmonicOverride(lpp.TuneHarm, pointN.Z);
        freshCtx.SrcModel.SetSourceDrive(lpp.ToneHz, pavlW * 4.0);
        var fresh = new HbEngine(freshNetlist, tb)
            .RunSinglePoint(freshCtx.HbParams, driveUp.V, freshCtx.SolveSettings);
        Assert.True(fresh.Converged);
        Assert.Equal(0.0, MaxAbsDiff(moved.V, fresh.V));
        Assert.Equal(0.0, MaxAbsDiff(moved.INl, fresh.INl));
    }

    // ── 3. The source RHS is unchanged by the persistent stamp ───────────────

    [Theory]
    [InlineData("Hero2", "hero2_convergence.cnl")]
    [InlineData("Hero4", "hero4.cnl")]
    [InlineData("Hero5", "hero5.cnl")]
    public void BuildSourceRhs_OnAReusedExtractor_EqualsAFreshStampsRhs(string hero, string file)
    {
        var (_, _, netlist, p) = Load(hero, file);
        var settings = new AnalysisSettings();
        double omega0 = 2.0 * Math.PI * p.ToneFreqsHz[0];
        int K = p.MaxHarmonic;

        // Exercise the persistent MnaSystem hard: extract every harmonic first (so the pattern
        // cache, the AMD ordering and the LU cache are all warm), then ask for the RHS.
        var reused = new HbLinearExtractor(netlist, settings);
        reused.ExtractDC();
        for (int k = 1; k <= K; k++) reused.Extract(k * omega0);

        for (int k = 0; k <= K; k++)
        {
            double w = k == 0 ? 0.0 : k * omega0;
            var fromReused = reused.BuildSourceRhs(w);
            var fromFresh  = new HbLinearExtractor(netlist, settings).BuildSourceRhs(w);

            Assert.Equal(fromFresh.Length, fromReused.Length);
            for (int i = 0; i < fromFresh.Length; i++)
                Assert.Equal(fromFresh[i], fromReused[i]);   // bit equality
        }
    }

    // ── 4. The back-solver keeps working after an invalidation ───────────────

    [Fact]
    public void BackSolver_SurvivesAnInvalidationAndALaterSolve()
    {
        var (_, tb, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        var eng = new HbEngine(netlist, tb);

        var first = eng.RunSinglePoint(p);
        Assert.True(first.Converged);
        var bs = first.BackSolver;
        Assert.NotNull(bs);

        // Read every non-ground node at every harmonic through the back-solver, then invalidate and
        // run another solve at a different drive on the same engine.
        int K = p.MaxHarmonic;
        var before = new Complex[netlist.Nodes.Count - 1, K + 1];
        for (int c = 1; c < netlist.Nodes.Count; c++)
            for (int k = 0; k <= K; k++)
                before[c - 1, k] = bs!.GetNodeVoltage(c, k, 0);

        // Drop every cached factorization, then solve again on the same engine — the later solve
        // refactorizes from scratch and must not disturb what the old back-solver already holds.
        eng.InvalidateLinear();
        Assert.True(eng.RunSinglePoint(p, first.V).Converged);

        // The old back-solver holds solution VECTORS, not factorizations, so its answers are frozen
        // at the point it was handed out — including the harmonics it had not been asked for yet.
        for (int c = 1; c < netlist.Nodes.Count; c++)
            for (int k = 0; k <= K; k++)
                Assert.Equal(before[c - 1, k], bs!.GetNodeVoltage(c, k, 0));
    }

    // ── 5. M3 — port currents from the last pass equal the re-evaluated ones ──

    [Theory]
    [InlineData("Hero2", "hero2_convergence.cnl")]
    [InlineData("Hero4", "hero4.cnl")]
    public void SingleTone_PortCurrentsFromLastPass_EqualTheReEvaluatedOnes(string hero, string file)
    {
        var (_, _, netlist, p) = Load(hero, file);
        var settings = new AnalysisSettings();
        int K = p.MaxHarmonic, gridN = HbFft.GridSize(K, p.FFTOverSample);
        double f0 = p.ToneHz, omega0 = 2.0 * Math.PI * f0;

        var ex = new HbLinearExtractor(netlist, settings);
        int N = ex.InterfaceCount;
        var ifNodes = ex.InterfaceNodes;

        var yNN = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = ex.ExtractDC();
        for (int k = 1; k <= K; k++) (yNN[k], iSrc[k]) = ex.Extract(k * omega0);

        var dc = NonlinearDcEngine.Run(netlist, settings);
        var V = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        {
            int c = ifNodes[n];
            V[n, 0] = new Complex(c > 0 && c - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[c - 1] : 0, 0);
            for (int k = 1; k <= K; k++) V[n, k] = new Complex(1e-3, 1e-3);
        }

        var sr = HbNewton.Solve(V, yNN, iSrc, f0, K, N, netlist, ifNodes, gridN, settings, p.Tol);
        Assert.True(sr.Converged);
        Assert.NotNull(sr.PortTerms);

        var fromLastPass = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, netlist, ifNodes, null, sr.INl, sr.PortTerms);
        var reEvaluated = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, netlist, ifNodes, null, sr.INl);

        AssertSpectraMatch(fromLastPass, reEvaluated, 1e-13);
        output.WriteLine($"{hero}: {fromLastPass.Count} port-current keys agree to 1e-13 " +
                         "between the last-pass buffer and a full re-evaluation.");
    }

    [Fact]
    public void TwoTone_PortCurrentsFromLastPass_EqualTheReEvaluatedOnes()
    {
        var (_, _, netlist, p) = Load("Hero5", "hero5.cnl");
        var settings = new AnalysisSettings();

        var grid     = new MixingGrid(p.MaxMixOrder);
        var (N1, N2) = HbFft2D.GridSizes(p.MaxMixOrder, p.MaxMixOrder, p.FFTOverSample);
        var ex       = new HbLinearExtractor(netlist, settings);
        int N        = ex.InterfaceCount;
        var ifNodes  = ex.InterfaceNodes;

        // Any physically plausible operating point does: the claim is that the buffered path and
        // the re-evaluation compute the same spectra from the SAME V, which a converged solve is
        // not needed to decide (and the goldens already gate convergence itself).
        var V = SeedInterfaceV(netlist, settings, ifNodes, N, grid.MixCount);

        double f1 = p.ToneFreqsHz[0], f2 = p.ToneFreqsHz[1];

        var buf = AllocPortTerms2D(netlist, N1, N2);
        HbNewton2D.EvaluateNonlinear2D(V, grid, N, N1, N2, netlist, ifNodes, buf);

        var fromLastPass = HbNewton2D.ComputeDevicePortCurrents2D(
            V, grid, N, N1, N2, f1, f2, netlist, ifNodes, buf);
        var reEvaluated  = HbNewton2D.ComputeDevicePortCurrents2D(
            V, grid, N, N1, N2, f1, f2, netlist, ifNodes);

        AssertSpectraMatch(fromLastPass, reEvaluated, 1e-13);
        output.WriteLine($"two-tone: {fromLastPass.Count} port-current keys agree to 1e-13.");
    }

    [Fact]
    public void NTone_PortCurrentsFromLastPass_EqualTheReEvaluatedOnes()
    {
        var (_, _, netlist, p) = Load("Hero5", "hero5_3tone.cnl");
        var settings = new AnalysisSettings();

        var apft    = HbApft.Get(p.ToneFreqsHz.Length, p.MaxMixOrder, settings.HbApftOversample);
        var ex      = new HbLinearExtractor(netlist, settings);
        int N       = ex.InterfaceCount;
        var ifNodes = ex.InterfaceNodes;

        var V = SeedInterfaceV(netlist, settings, ifNodes, N, apft.MixCount);

        var lattice = new MixingLattice(p.ToneFreqsHz.Length, p.MaxMixOrder);

        var buf = AllocPortTermsNd(netlist, apft.SampleCount);
        HbNewtonNd.EvaluateNonlinearNd(V, apft, N, netlist, ifNodes, buf);

        var fromLastPass = HbNewtonNd.ComputeDevicePortCurrentsNd(
            V, apft, N, lattice, p.ToneFreqsHz, netlist, ifNodes, buf);
        var reEvaluated  = HbNewtonNd.ComputeDevicePortCurrentsNd(
            V, apft, N, lattice, p.ToneFreqsHz, netlist, ifNodes);

        AssertSpectraMatch(fromLastPass, reEvaluated, 1e-13);
        output.WriteLine($"{p.ToneFreqsHz.Length}-tone: {fromLastPass.Count} port-current keys " +
                         "agree to 1e-13.");
    }

    // ── 6. The buffer is genuinely consumed, and ignored where it must be ────

    [Fact]
    public void ASuppliedBufferIsActuallyRead_AndAWrongShapedOneIsIgnored()
    {
        var (_, _, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        var settings = new AnalysisSettings();
        int K = p.MaxHarmonic, gridN = HbFft.GridSize(K, p.FFTOverSample);
        double omega0 = 2.0 * Math.PI * p.ToneHz;

        var ex = new HbLinearExtractor(netlist, settings);
        int N = ex.InterfaceCount;
        var ifNodes = ex.InterfaceNodes;

        var yNN = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = ex.ExtractDC();
        for (int k = 1; k <= K; k++) (yNN[k], iSrc[k]) = ex.Extract(k * omega0);

        var dc = NonlinearDcEngine.Run(netlist, settings);
        var V = new Complex[N, K + 1];
        for (int n = 0; n < N; n++)
        {
            int c = ifNodes[n];
            V[n, 0] = new Complex(c > 0 && c - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[c - 1] : 0, 0);
            for (int k = 1; k <= K; k++) V[n, k] = new Complex(1e-3, 1e-3);
        }
        var sr = HbNewton.Solve(V, yNN, iSrc, p.ToneHz, K, N, netlist, ifNodes, gridN, settings, p.Tol);
        Assert.True(sr.Converged);

        var real = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, p.ToneHz, netlist, ifNodes, null, sr.INl);

        // A buffer of the RIGHT shape carrying deliberately wrong numbers must change the answer —
        // otherwise "it agrees with the re-evaluation" would be satisfied by never reading it.
        var garbage = GarbageLike(sr.PortTerms!, (i, t) => 1.0 + i);
        var fromGarbage = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, p.ToneHz, netlist, ifNodes, null, sr.INl, garbage);
        Assert.NotEqual(real.First().Value[0], fromGarbage.First().Value[0]);

        // A buffer of the WRONG shape (a different grid size) is not read at all — the shape check
        // falls back to re-evaluation rather than indexing into it.
        var wrongShape = sr.PortTerms!
            .Select(d => new PortTermTimes(d.PortCount, d.GridN + 2)).ToArray();
        var fromWrong = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, p.ToneHz, netlist, ifNodes, null, sr.INl, wrongShape);
        AssertSpectraMatch(fromWrong, real, 0.0);
    }

    // ── 6b. The control-current path keeps its re-evaluation ─────────────────

    [Fact]
    public void ControlCurrentSdd_IgnoresTheLastPassBuffer_AndReEvaluatesAtTheConvergedCRef()
    {
        // An SDD that mirrors an IProbe current: I[1,0] = _c1, C[1] = IP1.
        const string Netlist = @"
Vdc:VDD       n_in 0            Vdc=1
R:Rbias       n_in n_sdd        R=100
IProbe:IP1    n_sdd n_x
R:Rsense      n_x 0             R=10
C:Cs          n_sdd 0           C=1 pF
V_1Tone:Vd    n_drv 0           Freq=1e9  V=0.05  Phase=0
R:Rdrv        n_drv n_sdd       R=50
SDD:X1        n_sdd 0           Ports=1  I[1,0]=0.5*_c1 + 1e-3*_v1*_v1  C[1]=IP1

analysis HB1 type=hb Tone=1e9 MaxHarm=3 FFTOverSample=1 Tol=1e-9 MaxIter=60
";
        string path = Path.Combine(Path.GetTempPath(), $"hbp2_ctrl_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, Netlist);
        Library lib; TestBench tb;
        try { (lib, tb) = CnlReader.ReadFile(path); }
        finally { File.Delete(path); }

        var netlist  = new Elaborator(lib).Elaborate(tb);
        var settings = new AnalysisSettings();
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

        Assert.Contains(netlist.Components, c => c.Model is SddModel sd && sd.ControlRefs.Length > 0);

        int K = p.MaxHarmonic, gridN = HbFft.GridSize(K, p.FFTOverSample);
        double f0 = p.ToneHz, omega0 = 2.0 * Math.PI * f0;

        var ex = new HbLinearExtractor(netlist, settings);
        int N = ex.InterfaceCount;
        var ifNodes = ex.InterfaceNodes;

        var yNN = new Complex[K + 1][,];
        var iSrc = new Complex[K + 1][];
        (yNN[0], iSrc[0]) = ex.ExtractDC();
        for (int k = 1; k <= K; k++) (yNN[k], iSrc[k]) = ex.Extract(k * omega0);

        var bSrc = new Complex[K + 1][];
        for (int k = 0; k <= K; k++) bSrc[k] = ex.BuildSourceRhs(k == 0 ? 0.0 : k * omega0);
        var cc = new ControlCurrentContext(ex, bSrc, f0, K);

        var V = SeedInterfaceV(netlist, settings, ifNodes, N, K + 1);
        var sr = HbNewton.Solve(V, yNN, iSrc, f0, K, N, netlist, ifNodes, gridN, settings, p.Tol,
                                cc: cc);
        Assert.True(sr.Converged);
        Assert.NotNull(sr.PortTerms);

        var reference = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, netlist, ifNodes, cc, sr.INl);

        // The last Newton pass's own buffer is NOT the post-solve answer here: the post-solve
        // currents are evaluated at the converged _c_ref, which that pass (one iterate behind on
        // its seed) did not use. Handing over a buffer — even a deliberately wrong one — must
        // change nothing, because the control path must not read it.
        var garbage = GarbageLike(sr.PortTerms!, (_, _) => 7.0);

        var withBuffer = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, netlist, ifNodes, cc, sr.INl, garbage);
        AssertSpectraMatch(withBuffer, reference, 0.0);

        // And the exemption is not vacuous — the same garbage DOES reach the answer with cc = null.
        var noCcGarbage = HbNewton.ComputeDevicePortCurrents(
            V, N, K, gridN, f0, netlist, ifNodes, null, sr.INl, garbage);
        Assert.NotEqual(reference.First().Value[0], noCcGarbage[reference.First().Key][0]);
    }

    // ── 7. Allocation ceiling for one warm solve ─────────────────────────────

    [Fact]
    public void OneWarmRunSinglePoint_AllocatesUnderTheCeiling()
    {
        var (_, tb, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        var eng = new HbEngine(netlist, tb);

        var first = eng.RunSinglePoint(p);
        Assert.True(first.Converged);
        for (int i = 0; i < 3; i++) eng.RunSinglePoint(p, first.V);   // warm the caches

        long before = GC.GetAllocatedBytesForCurrentThread();
        eng.RunSinglePoint(p, first.V);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"one warm RunSinglePoint on Hero 2 allocates {allocated / 1024.0:F1} KB " +
                         "(was ~393 KB before HB-P2; the extractor's per-solve rebuild is what left).");

        // A byte COUNT with headroom, not a time: the extractor used to rebuild and refactorize
        // every harmonic per solve, which was ~70% of the allocation.
        Assert.True(allocated < 150 * 1024,
            $"one warm RunSinglePoint allocated {allocated / 1024.0:F1} KB, over the 150 KB ceiling");
    }

    // ── 8. Z(freq) memoization is exact and still frequency-dependent ─────────

    [Fact]
    public void ZPortMemo_IsExactPerFrequency_AndStillBandsByFrequency()
    {
        var (_, _, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        var settings = new AnalysisSettings();
        double omega0 = 2.0 * Math.PI * p.ToneHz;
        int K = p.MaxHarmonic;

        // Hero 2's terminations are piecewise in `freq`, so a memo keyed on anything but the
        // frequency — or one that froze the first answer — would make every harmonic's Y_NN equal.
        var ex = new HbLinearExtractor(netlist, settings);
        var y = new Complex[K + 1][,];
        for (int k = 1; k <= K; k++) (y[k], _) = ex.Extract(k * omega0);

        Assert.True((y[1][0, 0] - y[2][0, 0]).Magnitude > 1e-9,
            "Y_NN at the fundamental and the second harmonic are equal — the Z(freq) bands collapsed");

        // Repeating the extraction reproduces each harmonic bit for bit.
        for (int k = 1; k <= K; k++)
        {
            var (again, _) = ex.Extract(k * omega0);
            for (int i = 0; i < again.GetLength(0); i++)
                for (int j = 0; j < again.GetLength(1); j++)
                    Assert.Equal(y[k][i, j], again[i, j]);
        }
    }

    // ── 9. A reused MnaSystem builds its pattern once and solves the same ─────

    [Fact]
    public void ReusedMnaSystem_BuildsItsPatternOnce_AcrossManyRestamps()
    {
        var (_, _, netlist, p) = Load("Hero2", "hero2_convergence.cnl");
        var settings = new AnalysisSettings();
        double omega0 = 2.0 * Math.PI * p.ToneHz;
        int K = p.MaxHarmonic;

        // Drive the extractor the way a Pin ladder does and check it never refactorizes past the
        // first pass — which is the observable consequence of the pattern and AMD caches surviving.
        var ex = new HbLinearExtractor(netlist, settings);
        for (int rep = 0; rep < 10; rep++)
        {
            SetDrive(netlist, 0.5 + 0.1 * rep);
            ex.ExtractDC();
            for (int k = 1; k <= K; k++) ex.Extract(k * omega0);
        }

        // K+2, not K+1: see the note in WarmSolvesOnOneEngine_… — Hero 2's DC interface is
        // voltage-pinned, so the very first ExtractDC pays one speculative unregularized
        // factorization before the regularized one. Ten drive levels cost the same as one.
        int afterTen = ex.Factorizations;
        output.WriteLine($"10 drive levels x {K + 1} harmonics: {afterTen} factorizations.");
        Assert.Equal(K + 2, afterTen);

        // And an explicit invalidation really does force the cold path again — but only once more:
        // the regularization mode is sticky, so the speculative pass is not repeated.
        ex.InvalidateLinear();
        for (int rep = 0; rep < 3; rep++)
        {
            ex.ExtractDC();
            for (int k = 1; k <= K; k++) ex.Extract(k * omega0);
        }
        Assert.Equal(afterTen + (K + 1), ex.Factorizations);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The netlist's DC operating point at the mix-index 0 bin, with a small non-zero
    /// seed everywhere else — a plausible spectrum for the device to be evaluated at.</summary>
    private static Complex[,] SeedInterfaceV(
        ElaboratedNetlist netlist, AnalysisSettings settings, int[] ifNodes, int n, int m)
    {
        var dc = NonlinearDcEngine.Run(netlist, settings);
        var V = new Complex[n, m];
        for (int i = 0; i < n; i++)
        {
            int c = ifNodes[i];
            V[i, 0] = new Complex(
                c > 0 && c - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[c - 1] : 0.0, 0);
            for (int j = 1; j < m; j++) V[i, j] = new Complex(0.02 / j, 0.01 / j);
        }
        return V;
    }

    private static HbNewton2D.PortTermTimes2D[] AllocPortTerms2D(
        ElaboratedNetlist netlist, int n1, int n2)
    {
        var buf = new HbNewton2D.PortTermTimes2D[netlist.NonlinearComponents.Count];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = new HbNewton2D.PortTermTimes2D(
                netlist.Components[netlist.NonlinearComponents[i]].Model.PortCount, n1, n2);
        return buf;
    }

    private static HbNewtonNd.PortTermTimesNd[] AllocPortTermsNd(
        ElaboratedNetlist netlist, int samples)
    {
        var buf = new HbNewtonNd.PortTermTimesNd[netlist.NonlinearComponents.Count];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = new HbNewtonNd.PortTermTimesNd(
                netlist.Components[netlist.NonlinearComponents[i]].Model.PortCount, samples);
        return buf;
    }

    /// <summary>A same-shaped buffer whose CONDUCTION and CHARGE rows both carry deliberately wrong
    /// numbers — both, because a terminal current is the weighted sum of the two and poisoning only
    /// one would leave a device that carries no charge unaffected.</summary>
    private static PortTermTimes[] GarbageLike(
        PortTermTimes[] like, Func<int, int, double> value)
        => like.Select(d =>
        {
            var g = new PortTermTimes(d.PortCount, d.GridN);
            for (int i = 0; i < d.PortCount; i++)
                for (int t = 0; t < d.GridN; t++)
                {
                    g.W0[i, t] = value(i, t);
                    g.W1[i, t] = value(i, t);
                }
            return g;
        }).ToArray();

    private static void AssertSpectraMatch(
        Dictionary<string, Complex[]> a, Dictionary<string, Complex[]> b, double tol)
    {
        Assert.Equal(b.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     a.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (key, spec) in a)
        {
            var other = b[key];
            Assert.Equal(other.Length, spec.Length);
            for (int i = 0; i < spec.Length; i++)
                Assert.True((spec[i] - other[i]).Magnitude <= tol,
                    $"{key}[{i}]: {spec[i]} vs {other[i]} (|Δ|={(spec[i] - other[i]).Magnitude:E3})");
        }
    }
}
