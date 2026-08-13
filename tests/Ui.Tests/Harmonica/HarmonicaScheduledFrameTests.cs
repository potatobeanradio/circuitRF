// ================================================================
//  HarmonicaScheduledFrameTests.cs  —  M6 of brief-harmonicarf-h4-h5, the document loop
//
//  TIER 5 (through the document)  the ladder degrades on the REAL request path, and tier A is still
//                                 in every plan the document actually solves.
//  TIER 6                         tier C is computed ONCE across a termination drag — the DCIV
//                                 family depends on no termination.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaScheduledFrameTests(ITestOutputHelper output)
{
    private sealed class Clock
    {
        private double _now;
        public double Read() => _now;
        public void Advance(double ms) => _now += ms;
    }

    [Fact]
    public async Task Tier5_ThroughTheDocument_TheLadderDegradesAndTierAIsAlwaysSolved()
    {
        // The scheduler is proven in isolation by FrameSchedulerTests; this proves the document
        // actually ASKS it — a policy nothing consults is a policy that does not exist.
        //
        // R-h9r2-2 (this brief) changed what there is to measure here: `HarmonicaViewModel.OptionsFor`
        // now forces `SkipContours = true` for EVERY `dragging: true` request, regardless of which
        // ladder rung the scheduler picked — so `GridSolveMs`/`FitMs`/`RasterMs` are all zero on every
        // recorded frame in this loop (only `HarmonicaSolver`'s single tier-A Pin drive-up is ever
        // costed while dragging). The ~400–600 ms contour-bearing-frame premise this test used to lean
        // on for "obviously over budget" no longer exists during a drag — there is no contour cost left
        // to record. `FrameScheduler.RecordFrame` still degrades on `TierAMs` alone, which is real but
        // small (measured ~15–30 ms on this fixture) — so the target is set well under that instead of
        // at the production 33.3 ms, to reliably force the SAME over-budget-every-frame walk the ladder
        // has always been expected to make. This is deliberately about tier A now, not contours: the
        // brief's own words are "[the ladder] still measures, still degrades, still recovers — it
        // simply no longer has a contour rung to announce" while a drag is in progress.
        var clock = new Clock();
        var vm = new HarmonicaViewModel { Scheduler = new FrameScheduler(clock.Read, 3.0) };

        // Stand in for the view: publishing is the ONE thing the view owns (it must happen on the UI
        // thread), so a headless test has to play that part or Frame never moves.
        //
        // NOTE (H6): PublishFrame now ALSO records the frame's measured cost — R-h6-4 made the frame
        // loop self-feeding, because a ladder that is never told what a frame cost can never degrade
        // and D4's status message can never fire. So this loop no longer records a synthetic timing
        // of its own: it would be the SECOND record per frame and the ladder would fall two rungs an
        // iteration.
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);

        var plans = new List<FramePlan>();

        for (int i = 0; i < 4; i++)
        {
            vm.SetMarkerImpedance(vm.Markers[1], new Complex(70 + i * 3, 10));
            vm.RequestScheduledFrame(dragging: true);
            await vm.Pool.DrainAsync();

            var plan = vm.LastPlan!.Value;
            plans.Add(plan);
            output.WriteLine($"frame {i}: quality={plan.Quality} skip={plan.SkipContours} timing={vm.Frame.Timing}");

            // D4 through the document: tier A is in every plan the document actually solves.
            Assert.True(plan.IncludesTierA);

            // R-h9r2-2's own invariant — OptionsFor forces SkipContours regardless of which rung the
            // scheduler is actually on, so the solved frame never carries a fresh grid while dragging.
            Assert.Empty(vm.Frame.SmithPower.GridPoints);

            clock.Advance(50);
        }

        Assert.Equal(FrameQuality.Full,           plans[0].Quality);
        Assert.Equal(FrameQuality.CoarseRaster,   plans[1].Quality);
        Assert.Equal(FrameQuality.CoarseGrid,     plans[2].Quality);
        Assert.Equal(FrameQuality.FrozenContours, plans[3].Quality);

        // Freeze-and-snap really did stop solving contours — asserted on the published frame, not on
        // the plan, so a plan that said SkipContours while the solver ignored it would fail here.
        Assert.Empty(vm.Frame.SmithPower.GridPoints);

        // ...and releasing snaps back to the real answer.
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        Assert.Equal(FrameQuality.Full, vm.LastPlan!.Value.Quality);
        Assert.NotEmpty(vm.Frame.SmithPower.GridPoints);

        output.WriteLine($"4 hopeless frames → {string.Join(" → ", plans.ConvertAll(p => p.Quality))}, " +
                         $"release → {vm.LastPlan!.Value.Quality}");
    }

    [Fact]
    public async Task Tier6_TheDcivFamilyIsComputedOnce_AcrossAScheduledTerminationDrag()
    {
        // Tier C depends only on the model, its parameters and the bias sweep range — never on
        // terminations. A drag that recomputed it per frame would pay for the same answer 20 times.
        var clock = new Clock();
        var vm = new HarmonicaViewModel { Scheduler = new FrameScheduler(clock.Read, 33.3) };

        for (int i = 0; i < 20; i++)
        {
            vm.SetMarkerImpedance(vm.Markers[1], new Complex(60 + i, 5 + 0.5 * i));
            vm.RequestScheduledFrame(dragging: true);
            await vm.Pool.DrainAsync();
            // This test does NOT stand in for the view, so nothing publishes and nothing records —
            // the synthetic timing here is the only one, and it is what walks the ladder down to
            // freeze-and-snap so 20 frames cost 20 drive-ups rather than 20 full grids.
            vm.RecordFrameCost(new FrameTiming(4, 900, 6, 90, 10));
            clock.Advance(20);
        }

        Assert.Equal(1, vm.DcivComputeCount);

        // Each worker that ran created its context exactly once and never rebuilt the netlist — the
        // pooled equivalent of §6.1's "one rebuild". More than one CREATION is correct and expected:
        // D2 gives every worker its own, because neither type is thread-safe.
        foreach (var w in vm.Pool.Workers)
        {
            Assert.True(w.ContextCreateCount <= 1);
            Assert.True(w.ContextRebuildCount <= 1,
                $"worker {w.Index} rebuilt its netlist {w.ContextRebuildCount} times — a termination " +
                "change is a VALUE change and must never rebuild");
        }

        int used = vm.Pool.Workers.Count(w => w.Context is not null);
        output.WriteLine($"after 20 scheduled frames: DcivComputeCount = {vm.DcivComputeCount}, " +
                         $"{used} of {vm.Pool.WorkerCount} worker(s) used, ladder at {vm.Scheduler.Quality}");
    }

    [Fact]
    public async Task ATerminationDrag_NeverRebuildsTheContext_BecauseNothingStructuralMoved()
    {
        // §6.1's own rule, re-checked on the scheduled path: a termination change is a VALUE change.
        var vm = new HarmonicaViewModel();

        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        int after = vm.Pool.Workers[0].ContextRebuildCount;

        for (int i = 0; i < 10; i++)
        {
            vm.SetMarkerImpedance(vm.Markers[0], new Complex(20 + i, -3 * i));
            vm.RequestScheduledFrame(dragging: true);
            await vm.Pool.DrainAsync();
        }

        Assert.Equal(after, vm.Pool.Workers[0].ContextRebuildCount);
    }
}
