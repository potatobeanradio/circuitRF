using System.Numerics;
using CircuitRF.WBond.Mom;
using Xunit.Abstractions;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// WM-3 §5–§7: the cost model a user is shown before pressing Run, the thread count a sweep chooses,
/// the reuse of one setup across two grids, and cancellation.
///
/// <para><b>Routine, at size S, in milliseconds.</b> A prediction that is systematically wrong is worse
/// than no prediction, so it is gated — but the gate belongs where every run sees it, not behind an
/// opt-in flag.</para>
/// </summary>
public sealed class MomCostModelTests(ITestOutputHelper output)
{
    private static WBondDesign Sized(int wires, int arrays) =>
        TestDesigns.ParallelArray(n: wires, pitchMil: 6.0, lengthMil: 100.0, heightMil: 10.0,
                                  diameterMil: 1.0, arrays: arrays);

    private static double[] Grid(int points)
    {
        var f = new double[points];
        for (int i = 0; i < points; i++) f[i] = Math.Pow(10.0, 8.0 + 2.6 * i / Math.Max(1, points - 1));
        return f;
    }

    // ---------------------------------------------------------------- §6, the prediction

    /// <summary>
    /// The prediction is <b>self-consistent</b>: the sweep number is the setup plus the points at the
    /// speedup the thread count implies, and it is what the report prints. No stopwatch.
    ///
    /// <h3>The stopwatch half of §6's gate is in the Benchmark tier, and that is a measured decision</h3>
    /// <para>Written here first, as §10 asks. It then <b>failed 2 runs in 4</b> when the full solution
    /// suite was running alongside it — a Debug build under a ten-way parallel start reads seven times
    /// the Release prediction, which is a fact about the machine's load and not about the model. That is
    /// the exact case the repo's own rule covers (<c>RfCore.Tests</c>' <c>Rbf2DPerfTests</c>: fast, but
    /// wall-clock-sensitive, so tagged <c>Benchmark</c>). <b>The prediction is still gated against a
    /// stopwatch at three sizes</b> — S, M and reduced L — in
    /// <see cref="MomSpeedTests.ThePredictionMatchesTheStopwatchAtSizeS"/> and
    /// <see cref="MomSpeedTests.TheStageTableAndTheTargets"/>.</para>
    /// </summary>
    [Fact]
    public void ThePredictionIsSelfConsistent()
    {
        var design = Sized(8, 2);
        var report = WireMomMesh.Predict(design, WireMomSettings.Balanced);

        Assert.Equal(WireMomCost.SetupSeconds(report.Segments), report.PredictedSetupSeconds, 12);
        Assert.Equal(WireMomCost.PerPointSeconds(report.Segments), report.PredictedPerPointSeconds, 12);

        foreach (int points in new[] { 1, 2, 21, 201 })
        {
            int threads = Math.Min(report.SolveThreads, points);
            double expected = report.PredictedSetupSeconds +
                              points * report.PredictedPerPointSeconds / WireMomCost.ParallelSpeedup(threads);

            Assert.Equal(expected, report.PredictedSweepSeconds(points), 12);
            Assert.Contains(WireMomCost.Duration(report.PredictedSweepSeconds(points)), report.CostSummary(points));
        }

        // More points is never cheaper, and one point is never cheaper than the setup alone.
        Assert.True(report.PredictedSweepSeconds(201) > report.PredictedSweepSeconds(21));
        Assert.True(report.PredictedSweepSeconds(1) > report.PredictedSetupSeconds);

        output.WriteLine(report.CostSummary(201));
    }

    /// <summary>
    /// The model's two halves are separately sane: the setup is quadratic-to-cubic and the per-point
    /// cost is cubic, so doubling N_s must multiply the per-point prediction by between 4 and 8.
    /// <b>A gate on the shape, which no machine speed can move.</b>
    /// </summary>
    [Fact]
    public void TheModelIsCubicWhereTheArithmeticIs()
    {
        double one = WireMomCost.PerPointSeconds(1000);
        double two = WireMomCost.PerPointSeconds(2000);
        Assert.InRange(two / one, 4.0, 8.0);

        double setupOne = WireMomCost.SetupSeconds(1000);
        double setupTwo = WireMomCost.SetupSeconds(2000);
        Assert.InRange(setupTwo / setupOne, 4.0, 8.0);
    }

    // ---------------------------------------------------------------- §4.1, the thread count

    /// <summary>
    /// <b>Memory, not cores, sets the width of a sweep.</b> One thread's workspace is
    /// <c>16·N_s²</c> bytes, so a budget of one of them is a serial sweep however many cores are free —
    /// and the number chosen is reported in the result's notes rather than left for the user to infer
    /// from a stopwatch.
    /// </summary>
    [Fact]
    public void TheThreadCountIsSetByMemoryNotCores()
    {
        const int ns = 4800, t = 16;
        long perThread = WireMomCost.BytesPerSolveThread(ns, t);

        Assert.Equal(1, WireMomCost.SolveThreadCount(ns, t, new WireMomSettings { SolveMemoryBudgetBytes = perThread }));
        Assert.Equal(1, WireMomCost.SolveThreadCount(ns, t, new WireMomSettings { SolveMemoryBudgetBytes = 1 }));
        Assert.Equal(3, WireMomCost.SolveThreadCount(ns, t, new WireMomSettings
        {
            SolveMemoryBudgetBytes = 3 * perThread,
            MaxSolveThreads = 16,
        }));

        // The cap is honoured whichever way round the two limits sit.
        Assert.Equal(2, WireMomCost.SolveThreadCount(ns, t, new WireMomSettings
        {
            SolveMemoryBudgetBytes = 100L * perThread,
            MaxSolveThreads = 2,
        }));

        output.WriteLine($"N_s = {ns}: {perThread / 1048576.0:F0} MB per thread, default budget " +
                         $"{WireMomCost.DefaultMemoryBudgetBytes() / 1048576.0:F0} MB, " +
                         $"{WireMomCost.SolveThreadCount(ns, t)} thread(s) here.");
    }

    /// <summary>The sweep says how wide it ran, and a serial one says that too.</summary>
    [Fact]
    public void TheSweepReportsHowWideItRan()
    {
        var design = Sized(4, 2);

        var serial = WireMomSolver.Create(design, WireMomSettings.Balanced with { MaxSolveThreads = 1 })
                                  .Solve(Grid(6));
        Assert.Contains(serial.Notes, n => n.Contains("one frequency point at a time"));

        var wide = WireMomSolver.Create(design, WireMomSettings.Balanced with { MaxSolveThreads = 4 })
                                .Solve(Grid(6));
        Assert.Contains(wide.Notes, n => n.Contains("frequency points at a time"));

        // A single point is not a sweep and does not carry the note.
        Assert.DoesNotContain(WireMomSolver.Create(design).Solve(Grid(1)).Notes,
                              n => n.Contains("frequency point"));
    }

    /// <summary>
    /// <b>The parallel sweep gives the same numbers as the serial one</b> — the property that a
    /// per-thread workspace exists to protect. Before WM-3 the scratch <c>M̃</c> was a field on the
    /// solver, and running two points at once would have had them overwrite each other's matrix.
    /// </summary>
    [Fact]
    public void TheParallelSweepAgreesWithTheSerialOne()
    {
        var design = Sized(6, 2);
        var grid = Grid(9);

        var serial = WireMomSolver.Create(design, WireMomSettings.Balanced with { MaxSolveThreads = 1 }).Solve(grid);
        var parallel = WireMomSolver.Create(design, WireMomSettings.Balanced with { MaxSolveThreads = 8 }).Solve(grid);

        for (int i = 0; i < grid.Length; i++)
        {
            var a = serial.PortImpedance(i);
            var b = parallel.PortImpedance(i);
            for (int k = 0; k < a.Length; k++)
                Assert.True((a[k] - b[k]).Magnitude <= 1e-12 * (1.0 + a[k].Magnitude),
                    $"point {i}, entry {k}: serial {a[k]}, parallel {b[k]}.");
        }
    }

    // ---------------------------------------------------------------- §6.1/§6.2, budgets and warnings

    /// <summary>
    /// <see cref="WireMomCost.SegmentsForBudget"/> answers with a number that <b>really fits</b>, and the
    /// rung above it really does not. A budget answer that is merely a direction is the failure
    /// <c>em-refusal-must-name-a-binding-remedy</c> is about.
    /// </summary>
    [Fact]
    public void SegmentsForBudgetNamesAValueThatFits_AndTheNextOneUpDoesNot()
    {
        var design = Sized(60, 4);
        const int points = 201;
        const double budget = 30.0;

        int fits = WireMomCost.SegmentsForBudget(design, points, budget);
        Assert.InRange(fits, 1, 48);

        var atFit = WireMomMesh.Predict(design, WireMomSettings.Default with { TargetSegmentsPerWire = fits });
        Assert.True(atFit.PredictedSweepSeconds(points) <= budget,
            $"{fits} segments/wire is {atFit.PredictedSweepSeconds(points):F1} s, over the {budget} s budget.");

        var above = WireMomMesh.Predict(design, WireMomSettings.Default with { TargetSegmentsPerWire = fits + 1 });
        Assert.True(above.PredictedSweepSeconds(points) > budget,
            $"{fits + 1} segments/wire also fits ({above.PredictedSweepSeconds(points):F1} s), so the answer is not the largest one.");

        output.WriteLine($"60 wires, {points} points, {budget} s: {fits} segments/wire " +
                         $"({atFit.Segments:N0} unknowns, {atFit.PredictedSweepSeconds(points):F1} s); " +
                         $"{fits + 1} would be {above.PredictedSweepSeconds(points):F1} s.");
    }

    /// <summary>
    /// The slow-run <b>warning</b> — not a refusal — fires on a design that would take minutes, names a
    /// coarser rung, and that rung is really cheaper. A short sweep of a small design gets no warning at
    /// all, which is the half that keeps the warning meaningful.
    /// </summary>
    [Fact]
    public void TheSlowRunWarningNamesACheaperRungThatIsReallyCheaper()
    {
        Assert.Null(WireMomMesh.SlowRunWarning(Sized(4, 2), points: 11));

        var big = Sized(200, 8);
        string? warning = WireMomMesh.SlowRunWarning(big, points: 201);
        Assert.NotNull(warning);
        output.WriteLine(warning);

        // It is a warning, not a refusal: the design still meshes and still solves.
        Assert.Null(WireMomMesh.RefusalFor(big));

        int fits = WireMomCost.SegmentsForBudget(big, 201, 60.0);
        if (fits > 0)
        {
            var coarser = WireMomMesh.Predict(big, WireMomSettings.Default with { TargetSegmentsPerWire = fits });
            Assert.True(coarser.PredictedSweepSeconds(201) <
                        WireMomMesh.Predict(big).PredictedSweepSeconds(201));
            Assert.Contains($"{fits} segments per wire", warning);
        }
    }

    /// <summary>
    /// The ceiling refusal (WM-1 §8) now carries the <b>cost</b> of the remedy it names, because
    /// "lower it to 13" is only actionable if 13 is known to be affordable as well as small enough.
    /// </summary>
    [Fact]
    public void TheCeilingRefusalNamesWhatItsRemedyCosts()
    {
        string? refusal = WireMomMesh.RefusalFor(Sized(600, 12));
        Assert.NotNull(refusal);
        output.WriteLine(refusal);

        Assert.Contains("per frequency point", refusal);
        Assert.Contains("setup", refusal);
    }

    // ---------------------------------------------------------------- §7, plan reuse and cancellation

    /// <summary>
    /// <b>One setup, two grids.</b> The solver IS the plan — everything frequency-independent is in
    /// <see cref="WireMomSolver.Create"/> — so re-solving on a second grid must produce exactly what a
    /// freshly built solver would, bit for bit, and must not rebuild anything.
    /// </summary>
    [Fact]
    public void OneSetupServesTwoGrids()
    {
        var design = Sized(6, 2);
        var solver = WireMomSolver.Create(design);

        var coarse = solver.Solve(Grid(5));
        var fine = solver.Solve(Grid(9));
        var fresh = WireMomSolver.Create(design).Solve(Grid(9));

        Assert.Equal(5, coarse.Frequencies.Count);
        for (int i = 0; i < fine.Frequencies.Count; i++)
        {
            var a = fine.PortImpedance(i);
            var b = fresh.PortImpedance(i);
            for (int k = 0; k < a.Length; k++) Assert.Equal(b[k], a[k]);
        }
    }

    /// <summary>
    /// <see cref="WireMomSolver.Matches"/> answers the narrow question — same design object, equal
    /// settings — and nothing wider. <b>It is not a staleness check</b>: a design that has been edited
    /// still matches, which is why the caller holds the plan and no cache does.
    /// </summary>
    [Fact]
    public void MatchesIsAboutTheSettings_NotAboutEdits()
    {
        var design = Sized(4, 2);
        var solver = WireMomSolver.Create(design, WireMomSettings.Balanced);

        Assert.True(solver.Matches(design, WireMomSettings.Balanced));
        Assert.False(solver.Matches(design, WireMomSettings.Fast));
        Assert.False(solver.Matches(Sized(4, 2), WireMomSettings.Balanced));

        // Documented, not desirable: an edit does not show up here.
        design.Arrays[0].Wires[0].Points[0] = Point3.Mils(0, 0, 25);
        Assert.True(solver.Matches(design, WireMomSettings.Balanced));
    }

    /// <summary>
    /// Setup is the long half at large N, so cancellation has to reach inside it and not only inside the
    /// frequency loop.
    /// </summary>
    [Fact]
    public void SetupAndSweepBothHonourCancellation()
    {
        var design = Sized(8, 2);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => WireMomSolver.Create(design, WireMomSettings.Balanced, cancelled.Token));

        var solver = WireMomSolver.Create(design);
        Assert.ThrowsAny<OperationCanceledException>(() => solver.Solve(Grid(9), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(
            () => WireMomSolver.Create(design, WireMomSettings.Balanced with { MaxSolveThreads = 1 })
                               .Solve(Grid(9), cancelled.Token));
    }

    // ---------------------------------------------------------------- §3, the fallback path

    /// <summary>
    /// <b>The fallback is real, it is taken, and it is reported.</b> Forced by demanding a pivot ratio
    /// no matrix can meet — the mechanism cannot be exercised by real bond geometry, which is itself the
    /// measured result (<c>RESOLVED.md</c>), so the alternative to forcing it is not testing it.
    /// </summary>
    [Fact]
    public void WhenTheGuardBitesTheSweepFallsBackToTheLuAndSaysSo()
    {
        var design = Sized(6, 2);
        var grid = Grid(5);

        var forced = WireMomSolver.Create(design, WireMomSettings.Balanced with { MinimumPivotRatio = 1.0 })
                                  .Solve(grid);
        var lu = WireMomSolver.Create(design, WireMomSettings.Balanced with { SymmetricFactorisation = false })
                              .Solve(grid);

        Assert.Contains(forced.Notes, n => n.Contains("fell back"));
        Assert.Contains(forced.Notes, n => n.Contains($"{grid.Length} of {grid.Length}") || n.Contains($"{grid.Length} frequency point"));

        for (int i = 0; i < grid.Length; i++)
        {
            var a = forced.PortImpedance(i);
            var b = lu.PortImpedance(i);
            for (int k = 0; k < a.Length; k++) Assert.Equal(b[k], a[k]);
        }

        // And a normal run takes no fallback at all.
        Assert.DoesNotContain(WireMomSolver.Create(design).Solve(grid).Notes, n => n.Contains("fell back"));
    }
}
