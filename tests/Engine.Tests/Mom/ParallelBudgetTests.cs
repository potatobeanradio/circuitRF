// M1/M2 (brief-em-sweep-performance) — the core cap must never change an answer, and that is
// asserted as BIT-IDENTITY rather than as a tolerance.
//
// R-fil-11 already says the fill's row parallelism cannot change a number: the parallelism is over
// ROWS of the packed upper triangle, every entry is written exactly once, and nothing accumulates
// into shared state. Nothing tested it. R-emp-8 turns that claim into a gate, and R-emp-13 does the
// same one level up for a whole de-embedded sweep — which is where M2's fan-out lives and where a
// scheduling-dependent answer would actually be introduced.
//
// A TOLERANCE WOULD BE THE WRONG GATE HERE. Two runs that agree to 1e-12 are exactly what a
// parallel accumulation whose order varies produces; the property being defended is that no such
// accumulation exists, and only bit-identity says that.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class ParallelBudgetTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double Freq = 10e9;

    /// <summary>The coarse fixture is ~90 unknowns — comfortably past <c>ForRows</c>' own count &gt; 8
    /// gate, so every cap under test genuinely takes the parallel branch rather than falling through
    /// to the sequential one and agreeing for the wrong reason.</summary>
    private static (PlanarFillCores Cores, PlanarKernelPair Kernel, int N) Fixture(int? cap,
                                                                                  PlanarParallelBudget? budget = null)
    {
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.0, Freq);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var st      = PlanarFillSettings.Default with { MaxDegreeOfParallelism = cap, Budget = budget };
        var cores   = PlanarFill.BuildCores(mesh, st);
        return (cores, PlanarLineFixtures.Kernel(slab, Freq), mesh.Bases.Count);
    }

    private static Mat<Complex> FillAt(int? cap, PlanarParallelBudget? budget = null)
    {
        var (cores, k, _) = Fixture(cap, budget);
        return PlanarFill.Fill(cores, k.VectorPotential, k.Scalar, 2.0 * Math.PI * Freq);
    }

    private static void AssertBitIdentical(Mat<Complex> a, Mat<Complex> b, string what)
    {
        Assert.Equal(a.RowCount, b.RowCount);
        Assert.Equal(a.ColCount, b.ColCount);
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
            {
                Assert.True(BitConverter.DoubleToInt64Bits(a[i, j].Real)
                         == BitConverter.DoubleToInt64Bits(b[i, j].Real),
                    $"{what}: Re Z[{i},{j}] differs — {a[i, j].Real:G17} vs {b[i, j].Real:G17}");
                Assert.True(BitConverter.DoubleToInt64Bits(a[i, j].Imaginary)
                         == BitConverter.DoubleToInt64Bits(b[i, j].Imaginary),
                    $"{what}: Im Z[{i},{j}] differs — {a[i, j].Imaginary:G17} vs {b[i, j].Imaginary:G17}");
            }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-8 — the fill, entry by entry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void REmp8_TheSameMatrixFilledAtCap1_2_AndUnbounded_IsBitIdentical()
    {
        var (_, _, n) = Fixture(null);
        var unbounded = FillAt(null);
        var one       = FillAt(1);
        var two       = FillAt(2);

        AssertBitIdentical(unbounded, one, "cap 1 vs unbounded");
        AssertBitIdentical(unbounded, two, "cap 2 vs unbounded");
        _out.WriteLine($"N = {n}: caps 1 / 2 / unbounded are bit-identical over all {n * n} entries");
    }

    [Fact]
    public void REmp8_TheBudgetPath_IsBitIdenticalToTheOrdinaryCappedPath()
    {
        // The budget is the shape a FANNED-OUT run takes: a permit per fill-row worker, shared
        // across every solve in flight. It is a third code path through ForRows and it must land on
        // the same bits as the other two.
        var plain    = FillAt(2);
        var budgeted = FillAt(2, new PlanarParallelBudget(2));
        AssertBitIdentical(plain, budgeted, "budget vs plain cap");
        _out.WriteLine("the shared-budget path fills bit-identically to the plain cap");
    }

    [Fact]
    public void ACapOfZeroOrFewer_IsRefusedByName_NotAcceptedAsUnbounded()
    {
        // Null is how "no cap" is spelled. A zero would reach Parallel.For as a framework exception
        // with no mention of a core count in it, which is the one value that would run NOTHING
        // rather than run slowly.
        // Validate() is the one place every fill passes through, so that is where the refusal is
        // asserted — not on the record's own construction, which deliberately validates nothing.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => (PlanarFillSettings.Default with { MaxDegreeOfParallelism = 0 }).Validate());
        Assert.Contains("core cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => (PlanarFillSettings.Default with { MaxDegreeOfParallelism = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarParallelBudget(0));
        _out.WriteLine($"refused: {ex.Message}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P10 — the budget's own instrument, and that its permits come back
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P10_TheBudgetsCounters_SeeTheFillTheyExistToMeasure()
    {
        // brief-em-p10-fanout-starvation.md's milestone 1 needed to know how many threads sit parked
        // in Enter() while a fanned-out point runs; the counters that answered it are kept, so the
        // question can be re-asked without re-instrumenting the solver. What is gated here is only
        // that they are WIRED TO THE BUDGETED PATH — a counter measuring nothing is the failure mode
        // an instrument actually has.
        //
        // They are process-wide by design (a run creates exactly one budget, so "any budget" and
        // "the budget" are the same set), which means another test's fill can move them at any
        // moment. So this asserts that they MOVE, never that they hold a particular value: an
        // equality against zero would be a race against the runner, not a statement about this code.
        long before = PlanarParallelBudget.EnterCount;

        FillAt(2, new PlanarParallelBudget(2));

        Assert.True(PlanarParallelBudget.EnterCount > before,
            "no worker took a permit — the fill did not go down the budgeted branch of ForRows");
        Assert.True(PlanarParallelBudget.TotalWaitSeconds >= 0);
        Assert.True(PlanarParallelBudget.WaitingThreads  >= 0);
        _out.WriteLine($"{PlanarParallelBudget.EnterCount - before} worker(s) joined a budgeted loop; "
                     + $"{PlanarParallelBudget.TotalWaitSeconds:F3} thread-seconds parked process-wide so far");
    }

    [Fact]
    public void P10_APermitAlwaysComesBack_SoOneBudgetSurvivesASecondFill()
    {
        // PlanarParallel.cs's header claims a permit always comes back, which is what makes the
        // scheme deadlock-free. A LEAKED permit is invisible in a single fill and fatal in the next
        // one: at cap 1 the second fill's first worker would park forever. So the gate is a second
        // fill through the SAME budget, with a wall-clock guard so a regression fails the test
        // rather than hanging the run.
        var         budget = new PlanarParallelBudget(1);
        Exception?  failure = null;
        var runner = new Thread(() =>
        {
            try { FillAt(1, budget); FillAt(1, budget); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };   // background, so a parked worker cannot outlive the run

        runner.Start();
        Assert.True(runner.Join(TimeSpan.FromSeconds(60)),
            "the second fill never finished — a worker is parked on a permit the first fill kept");
        Assert.Null(failure);
        _out.WriteLine("one cap-1 budget drove two fills back to back: every permit was returned");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-13 — a whole DE-EMBEDDED sweep, which is where M2's fan-out actually lives
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void REmp13_TheSameSweepAtCap1_AndAtCap8_IsBitIdentical()
    {
        // Cap 1 takes PlanarFanOut's strictly-in-order path and never builds a budget at all; cap 8
        // fans the DUT and both calibration standards out concurrently under one shared permit pool.
        // Two genuinely different schedules, one required answer.
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, Freq);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        double[] freqs = [9e9, 10e9];

        var serial   = PlanarSolve.Run(mesh, ports, slab, freqs,
                                       PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 1 });
        var parallel = PlanarSolve.Run(mesh, ports, slab, freqs,
                                       PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 8 });

        Assert.Equal(serial.Points.Count, parallel.Points.Count);
        for (int p = 0; p < serial.Points.Count; p++)
        {
            Assert.Equal(serial.Points[p].FrequencyHz, parallel.Points[p].FrequencyHz);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Assert.True(BitConverter.DoubleToInt64Bits(serial.Points[p].S[i, j].Real)
                             == BitConverter.DoubleToInt64Bits(parallel.Points[p].S[i, j].Real),
                        $"Re S[{i},{j}] at {serial.Points[p].FrequencyHz:G4} Hz differs");
                    Assert.True(BitConverter.DoubleToInt64Bits(serial.Points[p].S[i, j].Imaginary)
                             == BitConverter.DoubleToInt64Bits(parallel.Points[p].S[i, j].Imaginary),
                        $"Im S[{i},{j}] at {serial.Points[p].FrequencyHz:G4} Hz differs");
                }
        }
        _out.WriteLine($"{serial.Points.Count} de-embedded point(s), {serial.StandardCount} standard mesh(es): "
                     + "cap 1 and cap 8 are bit-identical");

        // The run's own note is asserted HERE rather than in a test of its own, because the only way
        // to produce it is to pay for a de-embedded solve — and this test has already paid.
        Assert.Contains(parallel.Notes, n => n.Contains("solved concurrently") && n.Contains("8 core"));
        // Cap 1 means exactly what it says: nothing to spend, so no fan-out to describe.
        Assert.DoesNotContain(serial.Notes, n => n.Contains("solved concurrently"));
        foreach (var n in parallel.Notes.Where(n => n.Contains("concurrently"))) _out.WriteLine($"  · {n}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void REmp13_TheSameSweepUnderAdaptiveSampling_IsBitIdenticalAtEitherCap()
    {
        // Adaptive sampling replays the branch continuation over the solved set rather than solving
        // straight through, so it is the path where an out-of-order solve could most plausibly leak
        // into the published answer. R-adf-2/3 say it cannot; the cap must not weaken that.
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, Freq);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        double[] freqs = [8e9, 9e9, 10e9, 11e9, 12e9];
        var adaptive = PlanarAdaptiveSettings.Default;

        var serial   = PlanarSolve.Run(mesh, ports, slab, freqs,
                          PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 1, Adaptive = adaptive });
        var parallel = PlanarSolve.Run(mesh, ports, slab, freqs,
                          PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 8, Adaptive = adaptive });

        Assert.Equal(freqs.Length, serial.Points.Count);
        Assert.Equal(serial.Points.Count, parallel.Points.Count);
        for (int p = 0; p < serial.Points.Count; p++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Assert.True(BitConverter.DoubleToInt64Bits(serial.Points[p].S[i, j].Real)
                             == BitConverter.DoubleToInt64Bits(parallel.Points[p].S[i, j].Real),
                        $"adaptive: Re S[{i},{j}] at point {p} differs");
                    Assert.True(BitConverter.DoubleToInt64Bits(serial.Points[p].S[i, j].Imaginary)
                             == BitConverter.DoubleToInt64Bits(parallel.Points[p].S[i, j].Imaginary),
                        $"adaptive: Im S[{i},{j}] at point {p} differs");
                }
        _out.WriteLine("adaptive on: cap 1 and cap 8 publish the same grid, bit-identically");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M2's own cost — gate 10, and it is a MEASUREMENT rather than a pass/fail
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M2_TheFanOutsOwnGain_IsMeasuredWithRowParallelismHeldOFF()
    {
        // The pre-M2 shape — solves in order, each fill using the whole machine — is not reachable
        // through the shipped settings any more, so it cannot be timed directly. What CAN be
        // isolated exactly is the fan-out's own contribution: hold PlanarFillSettings.Parallel OFF
        // in both runs, so the ONLY difference is whether the DUT and the calibration standards are
        // solved concurrently. That is M2, with the fill's own row parallelism factored out.
        //
        // Then time the shipped configuration (row parallelism on, automatic cap) beside them, so
        // the two knobs can be read against one another rather than one being reported alone.
        var slab    = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, Freq);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Shipping);
        double[] one = [Freq];

        var serialFill = PlanarFillSettings.Default with { Parallel = false };

        // Warm the DCIM fit and the JIT, or the first run pays for both.
        PlanarSolve.Run(mesh, ports, slab, one,
            PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 1, Fill = serialFill });

        double Time(PlanarSolveSettings st)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r  = PlanarSolve.Run(mesh, ports, slab, one, st);
            sw.Stop();
            Assert.Single(r.Points);
            return sw.Elapsed.TotalSeconds;
        }

        double serial   = Time(PlanarSolveSettings.Default with { MaxDegreeOfParallelism = 1,    Fill = serialFill });
        double fannedOut= Time(PlanarSolveSettings.Default with { MaxDegreeOfParallelism = null, Fill = serialFill });
        double shipped  = Time(PlanarSolveSettings.Default);

        int n = mesh.Bases.Count;
        _out.WriteLine($"N = {n} (DUT), {Environment.ProcessorCount} core(s) reported, one de-embedded point:");
        _out.WriteLine($"  fill serial, solves in order        : {serial:F1} s");
        _out.WriteLine($"  fill serial, solves FANNED OUT (M2) : {fannedOut:F1} s   ({serial / fannedOut:F2}x)");
        _out.WriteLine($"  shipped (row parallelism + fan-out) : {shipped:F1} s   ({serial / shipped:F2}x vs serial)");

        // No threshold is asserted on the ratio. The work is badly unbalanced by construction — the
        // standards' meshes are not the DUT's size — so the span is the largest solve, not the mean,
        // and a machine with fewer cores than solves in flight legitimately gains less. What IS
        // asserted is that fanning out never made it SLOWER by a margin no scheduling noise explains.
        Assert.True(fannedOut < serial * 1.5,
            $"fanning out cost more than it saved: {fannedOut:F1} s against {serial:F1} s serial");
    }
}
