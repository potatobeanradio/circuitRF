// ================================================================
//  SolvePoolTests.cs  —  M5 of brief-harmonicarf-h4-h5
//
//  TIER 4   latest-wins: a synthetic 200-event drag completes a BOUNDED number of solves and the
//           last result corresponds to the last event.
//  R-h45-8  one context per worker, pooled, rebuilt only on structural change.
//  R-h45-9  a superseded job stops at its next cancellation point rather than finishing.
//  D5       the coarse/full raster switch is the two named resolutions and nothing else.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class SolvePoolTests(ITestOutputHelper output)
{
    // ── TIER 4 — latest-wins on a 200-event drag ─────────────────────────────

    [Fact]
    public async Task Tier4_A200EventDrag_CompletesABoundedNumberOfSolves_AndTheLastEventWins()
    {
        // The job is synthetic and fast on purpose. Tier 4 tests the pool's POLICY — that a backlog
        // never forms and that a stale frame never overwrites a newer one — not the solver's cost.
        // Driving it with real HB solves would measure the solver and take minutes to say the same
        // thing about the pool.
        using var pool = new SolvePool<long>(workerCount: 4);

        int workDone = 0;
        long last = 0;

        for (int i = 0; i < 200; i++)
        {
            last = pool.Submit((_, ct) =>
            {
                // A real frame's cancellation point is between Γ points; this stands in for one.
                for (int k = 0; k < 20; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.SpinWait(200);
                }
                Interlocked.Increment(ref workDone);
                return 0L;
            });
        }

        await pool.DrainAsync();

        output.WriteLine($"200 events → started {pool.StartedCount}, completed {pool.CompletedCount}, " +
                         $"superseded {pool.SupersededCount}, last published seq {pool.LastCompletedSequence}");

        // THE BOUND, stated rather than left implied: at most one in-flight job per worker can have
        // been started and still been the latest when it finished, plus the final frame itself. If a
        // queue formed instead, this would be in the hundreds.
        Assert.True(pool.CompletedCount <= pool.WorkerCount + 1,
            $"latest-wins should complete at most {pool.WorkerCount + 1} of 200 events; " +
            $"got {pool.CompletedCount} — a backlog is forming");

        // The last event MUST win — a drag that ends on a stale frame is the failure users see.
        Assert.Equal(200, last);
        Assert.Equal(200, pool.LastCompletedSequence);

        // Every event is accounted for: published, or explicitly superseded.
        Assert.Equal(200, pool.CompletedCount + pool.SupersededCount);
    }

    [Fact]
    public async Task Tier4_AStaleJobThatFinishesAnyway_IsNeverPublished()
    {
        // The subtle half of latest-wins: a job that ignores its token and runs to completion must
        // still not overwrite a newer result. Cancellation is cooperative; the discard is not.
        using var pool = new SolvePool<string>(workerCount: 2);

        var firstMayFinish = new ManualResetEventSlim(false);
        var published = new List<string>();
        pool.Completed += (r, _) => { lock (published) published.Add(r); };

        pool.Submit((_, _) => { firstMayFinish.Wait(TimeSpan.FromSeconds(5)); return "stale"; });
        await Task.Delay(30);                     // let it actually start
        pool.Submit((_, _) => "fresh");
        await Task.Delay(30);                     // let the fresh one publish
        firstMayFinish.Set();                     // NOW let the stale one finish

        await pool.DrainAsync();

        Assert.Equal("fresh", pool.LastResult);
        Assert.DoesNotContain("stale", published);
    }

    [Fact]
    public async Task Tier4_ASupersededJob_StopsAtItsNextCancellationPoint_RatherThanFinishing()
    {
        // R-h45-9 directly: the token must actually reach the job, and observing it must abandon the
        // frame. Counted in STEPS so "stopped early" is a measurement, not an inference from timing.
        using var pool = new SolvePool<int>(workerCount: 1);

        int stepsRun = 0;
        var started = new ManualResetEventSlim(false);

        pool.Submit((_, ct) =>
        {
            started.Set();
            for (int k = 0; k < 10_000; k++)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref stepsRun);
                Thread.SpinWait(500);
            }
            return 1;
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the first job never started");
        pool.Submit((_, _) => 2);                 // supersedes it
        await pool.DrainAsync();

        output.WriteLine($"superseded job ran {stepsRun} of 10,000 steps");
        Assert.True(stepsRun < 10_000, "the superseded job ran to completion — the token never reached it");
        Assert.Equal(2, pool.LastResult);
    }

    // ── R-h45-8 — one context per worker, pooled ─────────────────────────────

    [Fact]
    public void R8_EachWorkerOwnsItsOwnContextAndGrid_NeverAShared()
    {
        using var pool = new SolvePool<int>(workerCount: 3);
        Assert.Equal(3, pool.WorkerCount);

        var model = Model();
        foreach (var w in pool.Workers) w.EnsureContext(model, Settings);

        var ctxs  = pool.Workers.Select(w => w.Context!).ToArray();
        var grids = pool.Workers.Select(w => w.Grid).ToArray();

        Assert.Equal(3, ctxs.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(3, grids.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void R8_AWorkersContextIsCreatedOnce_AndReusedAcrossBiasAndTerminationChanges()
    {
        // The pooling claim, measured where it happens: a context is CREATED once, and a change that
        // is not structural costs no netlist rebuild.
        using var pool = new SolvePool<int>(workerCount: 1);
        var w = pool.Workers[0];

        var model = Model();
        w.EnsureContext(model, Settings);

        int rebuildsAfterFirst = w.ContextRebuildCount;

        for (int i = 0; i < 20; i++)
            w.EnsureContext(model with { Bias = new BiasSpec { Vgs = -3.0 - i * 0.01, Vds = 48 } },
                            Settings);

        Assert.Equal(1, w.ContextCreateCount);
        Assert.Equal(rebuildsAfterFirst, w.ContextRebuildCount);
        output.WriteLine($"20 bias changes → {w.ContextCreateCount} context creation(s), " +
                         $"{w.ContextRebuildCount} netlist rebuild(s)");
    }

    [Fact]
    public void R8_AStructuralChange_DoesRebuildTheContext()
    {
        // The negative control. Without it the test above would pass against a context that never
        // rebuilds at all, which would be a real bug wearing the right counter.
        using var pool = new SolvePool<int>(workerCount: 1);
        var w = pool.Workers[0];

        var model = Model();
        w.EnsureContext(model, Settings);
        int before = w.ContextRebuildCount;

        var changed = model with
        {
            Settings = model.Settings with { HarmonicCount = model.Settings.HarmonicCount + 1 },
        };
        w.EnsureContext(changed, Settings);

        Assert.True(w.ContextRebuildCount > before,
            "a structural change must rebuild the netlist — otherwise the reuse test proves nothing");
        Assert.Equal(1, w.ContextCreateCount);
    }

    [Fact]
    public void R8_TheDefaultWorkerCount_IsCoresMinusTwo_AndNeverBelowOne()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 2), SolvePool<int>.DefaultWorkerCount);
        Assert.True(SolvePool<int>.DefaultWorkerCount >= 1);
    }

    // ── R-h45-9 — the grid's own cancellation point ──────────────────────────

    [Fact]
    public void R9_ContourGridBuild_AbandonsBetweenGammaPoints_WhenCancelled()
    {
        // The real path, not a stand-in: a cancelled Build must throw rather than solve the grid out.
        var ctx = HarmonicaContext.Create(Model(), Settings);
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var grid = new ContourGrid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => grid.Build(ctx, terms, ContourGrid.RingGrid(3, 12, 0.8), ct: cts.Token));

        // Nothing was published: an abandoned frame leaves no partial grid behind for a renderer to
        // pick up. Build clears its point list before the first cancellation check.
        Assert.Empty(grid.Points);
    }

    [Fact]
    public void R9_AnUncancelledBuild_StillRunsTheWholeGrid()
    {
        // The control for the test above — the token must not have changed the ordinary path.
        var ctx = HarmonicaContext.Create(Model(), Settings);
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var grid = new ContourGrid();
        var g = ContourGrid.RingGrid(2, 6, 0.6);
        grid.Build(ctx, terms, g, ct: CancellationToken.None);

        Assert.Equal(g.Length, grid.Points.Count);
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT, coefficients folded in so the fixture needs no globals.</summary>
    private static CircuitModel Model() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
        },
    };
}
