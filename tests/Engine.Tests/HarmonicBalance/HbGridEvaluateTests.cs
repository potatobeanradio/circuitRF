using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// brief-hb-p4-sdd-grid-evaluate.md §5 (engine side) — the HB answer is unchanged by the grid door.
///
/// <para>Every claim here is asserted at ZERO difference, not at a tolerance. The grid path runs the
/// same compiled instruction sequence with the same IEEE arithmetic in a different loop order, so a
/// converged HB solve has nothing legitimate to differ in — and a solve is a fixed point, which
/// amplifies any per-sample discrepancy through the Newton iteration rather than averaging it away.
/// A difference of one ulp in a device current is therefore a real defect, and this is where it
/// shows.</para>
///
/// <para>Two of these tests would pass vacuously if the fixture never actually took the grid path,
/// so the counters in <see cref="NonlinearEvalDiagnostics"/> are checked alongside: the grid door is
/// entered once per device per iteration, and the SDD's per-sample entry point is not entered at
/// all.</para>
/// </summary>
public sealed class HbGridEvaluateTests(ITestOutputHelper output)
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

    private static (Library Lib, TestBench Tb) Read(string hero, string file)
        => CnlReader.ReadFile(Path.Combine(TestDataDir(hero), file));

    // ── 1. Single-tone heroes: the whole solve, bit for bit ───────────────────

    [Theory]
    [InlineData("Hero2", "hero2.cnl")]
    [InlineData("Hero2", "hero2_convergence.cnl")]
    [InlineData("Hero4", "hero4.cnl")]
    public void SingleToneHero_GridPathAnswer_IsBitIdenticalToScalarPath(string hero, string file)
    {
        var (lib, tb) = Read(hero, file);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();

        var (gridV, gridI, gridCalls, gridScalarCalls) = SolveOnce(lib, tb, hba, useGrid: true);
        var (scalV, scalI, scalGridCalls, scalScalarCalls) = SolveOnce(lib, tb, hba, useGrid: false);

        var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        int gridN = HbFft.GridSize(p.MaxHarmonic, 1);

        output.WriteLine($"{hero}/{file} (gridN={gridN}): grid path took {gridCalls} EvaluateGrid " +
                         $"calls and {gridScalarCalls} per-sample SDD evaluations; scalar path took " +
                         $"{scalGridCalls} EvaluateGrid calls and {scalScalarCalls} per-sample ones.");

        // §5.7 — the fixture really did go through the door under test, and really did not on the
        // reference run. Without this the bit-identity assertion below is a tautology.
        Assert.True(gridCalls > 0, "grid path never called EvaluateGrid");
        Assert.Equal(0, scalGridCalls);

        // The per-sample counter does not reach zero and should not: the nonlinear-DC pre-solve that
        // seeds the initial guess evaluates ONE operating point per Newton step (there is nothing to
        // batch there — brief §8 excludes it), and those calls land on the same counter. What is
        // being asserted is that no HB DEVICE PASS went per-sample: a single one is gridN evaluations
        // per device, so anything below gridN cannot contain one.
        Assert.True(gridScalarCalls < gridN,
            $"grid path made {gridScalarCalls} per-sample evaluations — at least one device pass " +
            $"went sample by sample (a pass is {gridN} of them).");
        Assert.True(scalScalarCalls >= gridN,
            $"the reference run made only {scalScalarCalls} per-sample evaluations — it did not " +
            "take the scalar device path, so this comparison would prove nothing.");

        AssertBitIdentical(scalV, gridV, "V");
        AssertBitIdentical(scalI, gridI, "INl");
    }

    private static (Complex[,] V, Complex[,] INl, long GridCalls, long ScalarCalls)
        SolveOnce(Library lib, TestBench tb, HarmonicBalanceAnalysis hba, bool useGrid)
    {
        var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        try
        {
            NonlinearEvalDiagnostics.DisableGridEvaluate = !useGrid;
            NonlinearEvalDiagnostics.Counting = true;
            NonlinearEvalDiagnostics.Reset();
            var sp = new HbEngine(netlist, tb).RunSinglePoint(p);
            Assert.True(sp.Converged, "HB did not converge");
            return (sp.V, sp.INl, NonlinearEvalDiagnostics.GridCalls, NonlinearEvalDiagnostics.ScalarCalls);
        }
        finally
        {
            NonlinearEvalDiagnostics.Counting = false;
            NonlinearEvalDiagnostics.DisableGridEvaluate = false;
        }
    }

    // ── 2. Multi-tone: the 2-D lattice and the APFT list ──────────────────────

    [Theory]
    [InlineData("hero5.cnl")]        // two tones — 2-D lattice, the 1,024-sample grid
    [InlineData("hero5_3tone.cnl")]  // three tones — APFT sample list
    public void MultiToneHero5_GridPathAnswer_IsBitIdenticalToScalarPath(string file)
    {
        var (lib, tb) = Read("Hero5", file);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();

        var withGrid = SweepOnce(lib, tb, hba, useGrid: true);
        var withScalar = SweepOnce(lib, tb, hba, useGrid: false);

        output.WriteLine($"Hero5/{file}: grid path {withGrid.GridCalls} EvaluateGrid calls and " +
                         $"{withGrid.ScalarCalls} per-sample SDD evaluations; scalar path " +
                         $"{withScalar.GridCalls} / {withScalar.ScalarCalls}.");
        Assert.True(withGrid.GridCalls > 0, "grid path never called EvaluateGrid");
        Assert.Equal(0, withScalar.GridCalls);
        // As above: the residue is the nonlinear-DC pre-solve, one point per Newton step. A
        // multi-tone device pass is 1,024 (two-tone) or 756 (APFT) evaluations, so the scalar run
        // making far more of them is what confirms the two paths really differed.
        Assert.True(withScalar.ScalarCalls > 10 * withGrid.ScalarCalls,
            $"scalar path made {withScalar.ScalarCalls} per-sample evaluations against the grid " +
            $"path's {withGrid.ScalarCalls} — the two runs did not take different paths.");

        AssertBitIdentical(withScalar.V, withGrid.V, "V");
        AssertBitIdentical(withScalar.I, withGrid.I, "INl");
    }

    /// <summary>
    /// <c>Run</c>, not <c>RunSinglePoint</c>: only <c>Run</c> dispatches a multi-tone parameter set to
    /// the 2-D lattice / APFT engines. <c>RunSinglePoint</c> takes the single-tone Newton loop
    /// whatever it is handed, so a "two-tone" test written against it silently measures the
    /// single-tone path — which is how the first version of this test came to report a 10-entry
    /// V cube for a fixture whose lattice has 31 mixing products.
    /// </summary>
    private static (Complex[] V, Complex[] I, long GridCalls, long ScalarCalls)
        SweepOnce(Library lib, TestBench tb, HarmonicBalanceAnalysis hba, bool useGrid)
    {
        var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        if (p.SweepVarName is not null) p = p with { SweepStop = p.SweepStart };
        try
        {
            NonlinearEvalDiagnostics.DisableGridEvaluate = !useGrid;
            NonlinearEvalDiagnostics.Counting = true;
            NonlinearEvalDiagnostics.Reset();
            var ds = (RfCore.Data.DataSet)new HbEngine(netlist, tb).Run(p);
            Assert.True(ds["Converged"].RealValues.All(c => c > 0.5), "multi-tone HB did not converge");
            return (ds["V"].ComplexValues, ds["I"].ComplexValues,
                    NonlinearEvalDiagnostics.GridCalls, NonlinearEvalDiagnostics.ScalarCalls);
        }
        finally
        {
            NonlinearEvalDiagnostics.Counting = false;
            NonlinearEvalDiagnostics.DisableGridEvaluate = false;
        }
    }

    // ── 3. The control-current SDD is NOT on the grid path ────────────────────

    /// <summary>
    /// A device with <c>C[n]</c> control references keeps the scalar path: its <c>_c_ref(t)</c> seeds
    /// are produced by a two-pass self-consistent loop the grid door has no shape for. The point of
    /// asserting it is that a wrong answer there would be silent — the counter is the only visible
    /// difference between "correctly on the scalar path" and "wrongly on the grid one".
    /// </summary>
    [Fact]
    public void ControlCurrentSdd_StaysOnTheScalarPath()
    {
        const string cnl = @"
V_1Tone:VS   n_in 0   Vdc=0  Freq=1e9  V=1.0
R:R_src      n_in n_d   R=50
L:L1         n_d  n_s    L=1e-9  R=1
R:R_s        n_s  0      R=10
SDD:X1       n_d 0   Ports=1  I[1,0]=0.02*_v1+0.5*_c1  C[1]=L1

analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-7
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();

        var sdd = netlist.Components.Select(c => c.Model).OfType<SddModel>().Single();
        Assert.NotEmpty(sdd.ControlRefs);
        // The equation has no conditional, so nothing about the PROGRAM stops it — the device closes
        // the door because it has control references and no engine feeds them through it. Asserting
        // this at the model is what makes the run below meaningful: the first version of this test
        // found the engine handing a control device an empty control span, which the grid evaluator
        // could only answer with an out-of-range span.
        Assert.False(sdd.PrefersGridEvaluate);

        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        try
        {
            NonlinearEvalDiagnostics.Counting = true;
            NonlinearEvalDiagnostics.Reset();
            // Run(), not RunSinglePoint(): the control-current context is built only there
            // (HbEngine's own comment says loadpull and two-tone pass null), and without it a
            // control SDD is asked for an operating point with no _c seeds at all.
            var ds = (RfCore.Data.DataSet)new HbEngine(netlist, tb).Run(p);
            Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
            output.WriteLine($"control SDD: {NonlinearEvalDiagnostics.GridCalls} grid calls, " +
                             $"{NonlinearEvalDiagnostics.ScalarCalls} per-sample evaluations.");
            Assert.Equal(0, NonlinearEvalDiagnostics.GridCalls);
            Assert.True(NonlinearEvalDiagnostics.ScalarCalls > 0);
        }
        finally { NonlinearEvalDiagnostics.Counting = false; }
    }

    // ── 4. Parallel samples equal serial samples ──────────────────────────────

    /// <summary>
    /// M3 — splitting a 1,024-sample two-tone grid across cores is a performance decision, and a
    /// performance decision may not move the answer. Forced to both sides of its threshold on the
    /// same fixture, the whole sweep must agree bit for bit.
    /// </summary>
    [Fact]
    public void TwoTone_ParallelSamples_EqualSerialSamples_BitForBit()
    {
        var (lib, tb) = Read("Hero5", "hero5.cnl");
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();

        int saved = SddModel.GridParallelThreshold;
        try
        {
            SddModel.GridParallelThreshold = int.MaxValue;
            var serial = SweepOnce(lib, tb, hba, useGrid: true);
            SddModel.GridParallelThreshold = 1;
            var parallel = SweepOnce(lib, tb, hba, useGrid: true);

            AssertBitIdentical(serial.V, parallel.V, "V");
            AssertBitIdentical(serial.I, parallel.I, "I");
            output.WriteLine($"two-tone: serial and parallel agree on all " +
                             $"{serial.V.Length} V entries and {serial.I.Length} INl entries.");
        }
        finally { SddModel.GridParallelThreshold = saved; }
    }

    // ── 5. Allocation ─────────────────────────────────────────────────────────

    /// <summary>
    /// §5.9 — the per-sample allocation is gone, asserted as a byte COUNT rather than a time.
    /// The scalar path allocates six arrays for every sample of the grid (a NonlinearResult's I, Q,
    /// Dg, Dc plus the gradient arrays inside the evaluator); the grid path writes into buffers the
    /// engine already owns, so what remains is the pass's own spectra — which do not scale with the
    /// device count and are the same on both paths.
    /// </summary>
    [Fact]
    public void OneDevicePass_AllocatesFarLessOnTheGridPath()
    {
        var (lib, tb) = Read("Hero2", "hero2.cnl");
        var netlist = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

        int[] ifNodes = [.. netlist.NonlinearNodes.Where(n => n > 0).Distinct().OrderBy(n => n)];
        int N = ifNodes.Length;
        int K = p.MaxHarmonic;
        int gridN = HbFft.GridSize(K, 1);

        var V = new Complex[N, K + 1];
        for (int n = 0; n < N; n++) { V[n, 0] = new Complex(1.0 + n, 0); V[n, 1] = new Complex(0.7, -0.3); }

        long Measure(bool useGrid, int samples)
        {
            try
            {
                NonlinearEvalDiagnostics.DisableGridEvaluate = !useGrid;
                HbNewton.EvaluateNonlinear(V, N, K, samples, netlist, ifNodes);   // warm the buffers
                long before = GC.GetAllocatedBytesForCurrentThread();
                HbNewton.EvaluateNonlinear(V, N, K, samples, netlist, ifNodes);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            finally { NonlinearEvalDiagnostics.DisableGridEvaluate = false; }
        }

        // The claim is not "the pass allocates nothing" — it still allocates its own per-sample time
        // buffers (iTime, qTime, dgTime, dcTime, vTime) and the FFT temporaries, all of which scale
        // with the grid, are identical on both paths, and are not what this brief touches. The claim
        // is that the DEVICE EVALUATION's share of that growth is gone. Measuring at two grid sizes
        // and differencing is what separates the two, and it does not depend on the fixture's own
        // sample count the way an absolute ceiling would.
        long scalarSmall = Measure(useGrid: false, gridN);
        long scalarBig = Measure(useGrid: false, gridN * 16);
        long gridSmall = Measure(useGrid: true, gridN);
        long gridBig = Measure(useGrid: true, gridN * 16);

        long scalarGrowth = scalarBig - scalarSmall;
        long gridGrowth = gridBig - gridSmall;

        output.WriteLine($"Hero 2, N={N} interface nodes, K={K}: one EvaluateNonlinear allocates");
        output.WriteLine($"  scalar path: {scalarSmall,10:N0} B at {gridN} samples, " +
                         $"{scalarBig,10:N0} B at {gridN * 16} — growth {scalarGrowth:N0} B");
        output.WriteLine($"  grid   path: {gridSmall,10:N0} B at {gridN} samples, " +
                         $"{gridBig,10:N0} B at {gridN * 16} — growth {gridGrowth:N0} B");
        output.WriteLine($"  the device evaluation's own share of the growth — " +
                         $"{scalarGrowth - gridGrowth:N0} B over {gridN * 15} extra samples, " +
                         $"{(scalarGrowth - gridGrowth) / (double)(gridN * 15):F0} B/sample — is gone; " +
                         $"what remains is the pass's own buffers, the same on both paths.");

        Assert.True(gridSmall < scalarSmall,
            $"grid path allocated {gridSmall} B, scalar path {scalarSmall} B — no reduction");
        Assert.True(scalarGrowth - gridGrowth > 100L * gridN * 15,
            $"the per-sample device allocation is still there: growth fell only from " +
            $"{scalarGrowth} B to {gridGrowth} B over {gridN * 15} extra samples.");
        Assert.True(gridGrowth < scalarGrowth * 0.8,
            $"grid-path allocation growth {gridGrowth} B is not meaningfully below the scalar " +
            $"path's {scalarGrowth} B.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void AssertBitIdentical(Complex[,] expected, Complex[,] actual, string what)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (int i = 0; i < expected.GetLength(0); i++)
            for (int j = 0; j < expected.GetLength(1); j++)
                AssertBits(expected[i, j], actual[i, j], $"{what}[{i},{j}]");
    }

    private static void AssertBitIdentical(Complex[] expected, Complex[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            AssertBits(expected[i], actual[i], $"{what}[{i}]");
    }

    private static void AssertBits(Complex e, Complex a, string what)
    {
        if (Same(e.Real, a.Real) && Same(e.Imaginary, a.Imaginary)) return;
        Assert.Fail($"{what}: scalar {e} vs grid {a}");

        static bool Same(double x, double y)
            => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)
            || (double.IsNaN(x) && double.IsNaN(y));
    }
}
