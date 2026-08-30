// ================================================================
//  HbLineSearchTests.cs — HB-P3 M1: the backtracking line search on ‖F‖.
//
//  The failure it was written for, measured on the SHIPPED fixture before the change (Release,
//  cold RunSinglePoint from a DC seed, hero2_convergence.cnl):
//
//      Pavl  0 dBm →   3 iterations, converged
//      Pavl 10 dBm →   4 iterations, converged
//      Pavl 16 dBm → 100 iterations, NOT converged (‖F‖ = 4.64e-1)
//      Pavl 20 dBm → 100 iterations, NOT converged (‖F‖ = 9.57e-1)
//
//  and after it: 6 iterations each, with 4 and 6 backtracks respectively.
// ================================================================

using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public sealed class HbLineSearchTests(ITestOutputHelper output)
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

    private readonly record struct Point(
        bool Converged, int Iterations, int Backtracks, double MinLambda, bool Stalled,
        double Residual, Complex[,] V);

    /// <summary>One cold-or-warm single-tone solve of hero2_convergence.cnl at a given drive.</summary>
    private static Point Hero2At(double pavlDbm, Complex[,]? warmStart,
        AnalysisSettings? settings = null, int? maxIter = null)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir("Hero2"), "hero2_convergence.cnl"));
        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int pinIdx = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");
        tb.GlobalVariables[pinIdx] =
            new Variable("Pavl_dbm", pavlDbm.ToString("G17", CultureInfo.InvariantCulture), null);

        using var netlist = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        if (maxIter is { } mi) p = p with { MaxIter = mi };

        var sp = new HbEngine(netlist, tb, settings ?? new AnalysisSettings()).RunSinglePoint(p, warmStart);
        return new Point(
            sp.Converged, sp.Iterations,
            sp.IterTrace.Sum(r => r.Backtracks),
            sp.IterTrace.Count > 0 ? sp.IterTrace.Min(r => r.Lambda) : 1.0,
            sp.IterTrace.Any(r => r.Stalled),
            sp.IterTrace.Count > 0 ? sp.IterTrace[^1].ResidualNorm : double.NaN,
            sp.V);
    }

    private static double MaxAbsDiff(Complex[,] a, Complex[,] b)
    {
        double m = 0;
        for (int n = 0; n < a.GetLength(0); n++)
            for (int k = 0; k < a.GetLength(1); k++)
                m = Math.Max(m, (a[n, k] - b[n, k]).Magnitude);
        return m;
    }

    // ── 1. The points the undamped loop could not solve ──────────────────────

    /// <summary>
    /// The whole reason HB-P3 exists. Both drives ran to <c>MaxIter = 100</c> and returned
    /// non-converged before the line search; both now converge in a handful of iterations, onto the
    /// SAME root the warm-chained sweep walks to — which is the part that says the line search found
    /// the answer rather than merely some fixed point.
    /// </summary>
    [Theory]
    [InlineData(16.0)]
    [InlineData(20.0)]
    public void AColdPointAtCompression_Converges_OntoTheWarmChainsOwnRoot(double pavlDbm)
    {
        var cold = Hero2At(pavlDbm, warmStart: null);
        output.WriteLine($"cold {pavlDbm:F0} dBm: converged={cold.Converged} iters={cold.Iterations} " +
                         $"backtracks={cold.Backtracks} minLambda={cold.MinLambda:G4} ‖F‖={cold.Residual:E3}");

        Assert.True(cold.Converged, $"cold {pavlDbm} dBm did not converge (‖F‖={cold.Residual:E3})");
        Assert.True(cold.Iterations <= 15, $"took {cold.Iterations} iterations; expected ≤ 15");
        Assert.False(cold.Stalled);

        // Walk up to the same drive in 1 dB steps, each point warm-started from the last — the route
        // that always worked — and compare where the two arrive.
        Complex[,]? seed = null;
        for (double dbm = 0; dbm <= pavlDbm + 1e-9; dbm += 1.0)
        {
            var w = Hero2At(dbm, seed);
            Assert.True(w.Converged, $"warm chain broke at {dbm} dBm");
            seed = w.V;
        }

        // RELATIVE, because both solves stop at ‖F‖ ≈ 3e-8 against the fixture's 1e-7 tolerance and
        // therefore sit at slightly different within-tolerance iterates: measured 1.32e-6 V of
        // absolute disagreement on a circuit whose interface swings tens of volts, i.e. ~2.6e-8
        // relative. A different root would differ by volts, not by a part in 10^8.
        double diff  = MaxAbsDiff(cold.V, seed!);
        double scale = Math.Max(1.0, Enumerable.Range(0, cold.V.GetLength(0))
            .SelectMany(n => Enumerable.Range(0, cold.V.GetLength(1)).Select(k => cold.V[n, k].Magnitude))
            .Max());
        output.WriteLine($"  cold-vs-warm-chain max |ΔV| = {diff:E3} V ({diff / scale:E2} relative)");
        Assert.True(diff / scale < 1e-6,
            $"cold and warm-chained solutions differ by {diff:E3} V ({diff / scale:E2} relative) — different root?");
    }

    // ── 2. What a converging solve costs ─────────────────────────────────────

    /// <summary>
    /// The no-regression half. λ = 1 is tried first and an ordinary Newton step passes Armijo on the
    /// first trial, so almost every point of a warm sweep takes exactly the steps it took before.
    ///
    /// <para><b>"Almost", not "every" — and the brief said every.</b> Measured over the 21-point warm
    /// chain from 0 to 30 dBm in 1.5 dB steps: 19 points take zero backtracks, and TWO (13.5 and
    /// 15.0 dBm, on the compression knee) take three and one. So a warm, converging solve is NOT
    /// unconditionally byte-identical to the undamped loop; on those two points the full step
    /// genuinely increases ‖F‖ and the line search refuses it. The total is 85 Newton iterations
    /// either way. The byte-level guard for the frozen answers is the golden suite itself
    /// (<c>Hero2RegressionTests</c>, <c>Hero4Tests</c>, <c>Hero5GateTests</c>), which compares
    /// committed CSVs and is unchanged by HB-P3.</para>
    /// </summary>
    [Fact]
    public void AWarmSweep_BacktracksOnlyWhereTheFullStepGrowsTheResidual()
    {
        double[] pins = Enumerable.Range(0, 21).Select(i => i * 1.5).ToArray();

        Complex[,]? seed = null;
        int totalIters = 0;
        var backtracked = new List<(double Pin, int Iters, int Backtracks)>();

        foreach (double pin in pins)
        {
            var pt = Hero2At(pin, seed);
            Assert.True(pt.Converged, $"warm point {pin} dBm did not converge");
            Assert.False(pt.Stalled,  $"warm point {pin} dBm stalled the line search");

            // A step with no backtracks is the undamped step, exactly: λ is the directive's own
            // Lambda (1 here), not some reduced value.
            if (pt.Backtracks == 0) Assert.Equal(1.0, pt.MinLambda);
            else backtracked.Add((pin, pt.Iterations, pt.Backtracks));

            totalIters += pt.Iterations;
            seed = pt.V;
        }

        foreach (var b in backtracked)
            output.WriteLine($"  backtracked at {b.Pin:F1} dBm: {b.Iters} iters, {b.Backtracks} backtracks");
        output.WriteLine($"warm sweep: {pins.Length} points, {totalIters} Newton iterations, " +
                         $"{backtracked.Count} point(s) backtracked");

        Assert.True(backtracked.Count <= 2,
            $"{backtracked.Count} of {pins.Length} warm points backtracked; the line search should be " +
            "inert on a warm chain apart from the known compression-knee pair");
    }

    /// <summary>
    /// <c>Lambda</c> (B2) keeps its meaning: it is the FIRST λ the search tries, so a directive that
    /// asks for a damped step still gets exactly that step whenever it is accepted.
    /// </summary>
    [Fact]
    public void AFixedLambdaBelowOne_IsTheStartingStep()
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir("Hero2"), "hero2_convergence.cnl"));
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        using var netlist = new Elaborator(lib).Elaborate(tb);

        var p  = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit)
                 with { Lambda = 0.5 };
        var sp = new HbEngine(netlist, tb).RunSinglePoint(p);

        output.WriteLine($"Lambda=0.5: converged={sp.Converged} iters={sp.Iterations} " +
                         $"backtracks={sp.IterTrace.Sum(r => r.Backtracks)}");

        Assert.True(sp.Converged);
        // Every accepted STEP that needed no backtracking sits at exactly the requested λ. SkipLast
        // drops the trailing record, which reports the converged residual and takes no step at all —
        // it carries the field's default 1.0 by construction, not a step the search chose.
        foreach (var r in sp.IterTrace.SkipLast(1).Where(r => r.Backtracks == 0))
            Assert.Equal(0.5, r.Lambda);
        Assert.True(sp.IterTrace.Count > 1, "expected at least one step record");
    }

    // ── 3. Multi-tone ────────────────────────────────────────────────────────

    /// <summary>
    /// The three-tone loop had the same defect and the same cure. Measured against the pre-HB-P3
    /// engine on <c>hero5_3tone.cnl</c>: <b>17 dBm is the first level at which the undamped loop
    /// fails</b> — it returns non-converged at ‖F‖ = 2.53e-1 there and at every level above (0.26 at
    /// 18 dBm, 0.34 at 20). With the line search the same cold point converges in 8 iterations with
    /// two backtracks.
    ///
    /// <para><b>The two-tone loops did NOT have a failure level to find, and the brief assumed they
    /// would.</b> Both routes — the mixing lattice (<c>HbNewtonNd</c>, the default since 2026-08-30)
    /// and the rectangular <c>HbNewton2D</c> — solve <c>hero5.cnl</c> cold at every drive from 18 to
    /// 32 dBm with and without the line search. The lattice route refuses one step at 28 dBm; the
    /// rectangular route refuses none anywhere in that span. So this test pins the level that exists
    /// and records the absence of the other rather than inventing a fixture to manufacture it.</para>
    /// </summary>
    [Fact]
    public void AThreeToneColdSolve_AtTheLevelTheUndampedLoopFailed_Converges()
    {
        const double FirstFailingDbm = 17.0;

        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir("Hero5"), "hero5_3tone.cnl"));
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int idx = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");
        Assert.True(idx >= 0);
        var orig = tb.GlobalVariables[idx];

        foreach (double dbm in new[] { FirstFailingDbm, 18.0, 20.0 })
        {
            tb.GlobalVariables[idx] =
                new Variable("Pavl_dbm", dbm.ToString("G17", CultureInfo.InvariantCulture), orig.Unit);
            using var netlist = new Elaborator(lib).Elaborate(tb);
            var p  = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
            var rr = new HbEngine(netlist, tb).Run(p);

            var steps = rr.Trace!.Steps;
            int bt    = steps.Sum(s => s.IterTrace.Sum(r => r.Backtracks));
            int iters = steps.Sum(s => s.Iterations);
            output.WriteLine($"3-tone cold {dbm:F0} dBm: converged={rr.Converged} iters={iters} backtracks={bt}");

            Assert.True(rr.Converged, $"3-tone cold at {dbm} dBm did not converge");
            Assert.True(bt > 0,
                $"at {dbm} dBm the line search refused no step — the level the pre-brief loop failed " +
                "at has moved, and this test no longer pins what it says it does");
            Assert.DoesNotContain(steps.SelectMany(s => s.IterTrace), r => r.Stalled);
        }
        tb.GlobalVariables[idx] = orig;
    }

    // ── 4. The evaluation count ──────────────────────────────────────────────

    /// <summary>
    /// The accepted trial's evaluation IS the next iteration's entry evaluation, so a converging
    /// solve evaluates the devices once per iteration — never twice at the same V, which is what the
    /// pre-brief loop shape would have cost had the line search simply been bolted on top of it.
    /// A backtrack adds one evaluation each, and nothing else does.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void EvaluationsPerSolve_EqualIterationsPlusBacktracks(double pavlDbm)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(TestDataDir("Hero2"), "hero2_convergence.cnl"));
        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int pinIdx = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");
        tb.GlobalVariables[pinIdx] =
            new Variable("Pavl_dbm", pavlDbm.ToString("G17", CultureInfo.InvariantCulture), null);

        using var netlist = new Elaborator(lib).Elaborate(tb);
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
        var eng = new HbEngine(netlist, tb);

        // RunSinglePoint's only device evaluations are the Newton's own — it does no post-convergence
        // port-current pass, which is why the count is readable here and not through Run.
        HbNewton.ResetEvaluations();
        var sp = eng.RunSinglePoint(p);
        int evals = HbNewton.Evaluations;

        int backtracks = sp.IterTrace.Sum(r => r.Backtracks);
        output.WriteLine($"{pavlDbm:F0} dBm: iterations={sp.Iterations} backtracks={backtracks} " +
                         $"evaluations={evals}");

        Assert.True(sp.Converged);
        Assert.Equal(sp.Iterations + backtracks, evals);
    }
}
