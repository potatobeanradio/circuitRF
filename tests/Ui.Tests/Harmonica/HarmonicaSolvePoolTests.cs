// ================================================================
//  HarmonicaSolvePoolTests.cs  —  M5 of brief-harmonicarf-h4-h5, the Ui half
//
//  D5       the coarse/full raster switch is the two named resolutions and nothing else.
//  R-h45-8  a frame requested through the pool uses the WORKER's pooled context and grid, and the
//           DCIV cache (tier C) is still computed once even though N workers are solving.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSolvePoolTests(ITestOutputHelper output)
{
    // ── D5 — the raster switch ───────────────────────────────────────────────

    [Fact]
    public void D5_TheRasterSwitchIsTheTwoNamedResolutions_AndNothingElse()
    {
        Assert.Equal(96,  HarmonicaSolver.Options.CoarseRasterResolution);
        Assert.Equal(256, HarmonicaSolver.Options.FullRasterResolution);

        var full = new HarmonicaSolver.Options();
        Assert.Equal(HarmonicaSolver.Options.FullRasterResolution, full.RasterResolution);

        Assert.Equal(96,  full.WithRasterFor(dragging: true).RasterResolution);
        Assert.Equal(256, full.WithRasterFor(dragging: true).WithRasterFor(dragging: false).RasterResolution);
    }

    [Fact]
    public void D5_TheSwitchChangesTheRasterAndNothingElse()
    {
        // Degrading the raster must not quietly degrade the GRID too — that would lose information,
        // which is exactly the trade D5 exists to avoid.
        var full = new HarmonicaSolver.Options
        {
            Rings = 5, Spokes = 12, MaxGamma = 0.75, Levels = 8,
            EfficiencyMetric = GridMetric.Pae, IntrinsicPlane = false,
        };
        var drag = full.WithRasterFor(dragging: true);

        Assert.Equal(full with { RasterResolution = drag.RasterResolution }, drag);
    }

    // ── R-h45-8 — the pooled path, end to end ────────────────────────────────

    [Fact]
    public async Task PooledFrame_SolvesThroughAWorkersOwnContextAndGrid_AndPublishes()
    {
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(80, 10));

        HarmonicaFrame? published = null;
        vm.Pool.Completed += (f, _) => published = f;

        long seq = vm.RequestFrame(new HarmonicaSolver.Options
        {
            Rings = 2, Spokes = 6, MaxGamma = 0.6, RasterResolution = 96,
        });
        await vm.Pool.DrainAsync();

        Assert.Equal(seq, vm.Pool.LastCompletedSequence);
        Assert.NotNull(published);
        Assert.NotEmpty(published!.SmithPower.GridPoints);

        // The worker's own state was used — not a throwaway context and grid built inside Solve.
        var used = vm.Pool.Workers.Single(w => w.Context is not null);
        Assert.Equal(1, used.ContextCreateCount);
        Assert.NotEmpty(used.Grid.Points);

        output.WriteLine($"pooled frame: {vm.Pool.Workers.Count} worker(s), " +
                         $"{used.Grid.Points.Count} Γ points on worker {used.Index}");
    }

    // ── R9C §4 — the first frame is solved at full quality, like every other frame ──────────

    [Fact]
    public void TheFirstScheduledFrame_PlansFullQuality_WithTheFullRingSet()
    {
        // HarmonicaView.EnsureFirstSolve now calls RequestScheduledFrame(dragging: false) — exactly
        // this call — instead of a bare RequestFrame(), which used to take Options' own (coarse)
        // defaults. NextPlan(dragging: false) always resolves to FrameQuality.Full regardless of the
        // ladder's current rung (§6.8's "freeze-and-snap": a released drag always gets the full grid).
        var vm = new HarmonicaViewModel();
        vm.RequestScheduledFrame(dragging: false);

        Assert.NotNull(vm.LastPlan);
        var plan = vm.LastPlan!.Value;
        Assert.Equal(FrameQuality.Full, plan.Quality);
        Assert.Equal(FrameScheduler.FullRings,  plan.Rings);
        Assert.Equal(FrameScheduler.FullSpokes, plan.Spokes);
        Assert.False(plan.SkipContours);
        Assert.Equal(HarmonicaSolver.Options.FullRasterResolution, plan.RasterResolution);
    }

    [Fact]
    public void ABareOptionsRecord_AlsoDefaultsToTheFullRingSet()
    {
        // R9C §4 — a bare `new Options()` (used by tests, and reachable by any future caller) must not
        // silently stay on the coarse rung: that is precisely the trap that let a document's LAUNCH
        // frame (the old bare RequestFrame()) diverge from every later frame.
        var opt = new HarmonicaSolver.Options();
        Assert.Equal(FrameScheduler.FullRings,  opt.Rings);
        Assert.Equal(FrameScheduler.FullSpokes, opt.Spokes);
    }

    [Fact]
    public async Task TierC_IsComputedOnce_EvenThoughEveryWorkerHasItsOwnContext()
    {
        // The DCIV cache lives on the SHARED solver, so N workers do not compute it N times. Without
        // that, moving to a pool would have silently multiplied tier C's cost by the core count.
        var vm = new HarmonicaViewModel();

        var opt = new HarmonicaSolver.Options { Rings = 1, Spokes = 4, MaxGamma = 0.4, SkipContours = true };
        for (int i = 0; i < 6; i++)
        {
            vm.SetMarkerImpedance(vm.Markers[0], new Complex(40 + i, 5 * i));
            vm.RequestFrame(opt);
            await vm.Pool.DrainAsync();      // force each frame through rather than superseding it
        }

        Assert.Equal(1, vm.DcivComputeCount);
        output.WriteLine($"6 pooled frames → DcivComputeCount = {vm.DcivComputeCount}");
    }

    // ── §3 (R1C) — the grid-solve progress signal ────────────────────────────

    [Fact]
    public async Task AGridFrame_SetsIsSolvingGridImmediately_AndTicksProgressToCompletion()
    {
        var vm = new HarmonicaViewModel();
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);

        var ticks = new System.Collections.Generic.List<(int Done, int Total)>();
        vm.GridSolveProgress += (done, total) => ticks.Add((done, total));

        long seq = vm.RequestFrame(new HarmonicaSolver.Options
        {
            Rings = 2, Spokes = 6, MaxGamma = 0.6, RasterResolution = 96,
        });

        // §1's own replacement for the toolbar's Solve button sets this SYNCHRONOUSLY, on the calling
        // thread, before the pooled work has even started — the bar must appear at the moment the
        // request is made, not only once the grid starts producing points.
        Assert.True(vm.IsSolvingGrid);

        await vm.Pool.DrainAsync();

        Assert.Equal(seq, vm.Pool.LastCompletedSequence);
        Assert.NotEmpty(ticks);
        // The FINAL tick always reaches the total — the bar can never land short.
        Assert.Equal(ticks[^1].Total, ticks[^1].Done);
        Assert.All(ticks, t => Assert.Equal(ticks[0].Total, t.Total));

        // Publishing resets the flag — the bar disappears once the frame lands.
        Assert.False(vm.IsSolvingGrid);
    }

    [Fact]
    public async Task ASkipContoursFrame_NeverSetsIsSolvingGrid_AndReportsNoProgress()
    {
        // §3's own rule: "a frame with SkipContours solves no grid — it must not show a bar that
        // never moves."
        var vm = new HarmonicaViewModel();
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);

        int ticks = 0;
        vm.GridSolveProgress += (_, _) => ticks++;

        vm.RequestFrame(new HarmonicaSolver.Options
        {
            Rings = 1, Spokes = 4, MaxGamma = 0.4, SkipContours = true,
        });
        Assert.False(vm.IsSolvingGrid);

        await vm.Pool.DrainAsync();

        Assert.Equal(0, ticks);
        Assert.False(vm.IsSolvingGrid);
    }

    [Fact]
    public async Task ABadModel_LandsInSolveError_RatherThanKillingTheDocument()
    {
        // Same contract the synchronous path already has — a pool failure must not be different.
        var broken = HarmonicaViewModel.DefaultModel();
        broken = broken with
        {
            Dut = broken.Dut with
            {
                Parameters = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "this is not an expression",
                },
            },
        };
        var vm = new HarmonicaViewModel(broken);

        Exception? failure = null;
        vm.Pool.Failed += (ex, _) => failure = ex;

        vm.RequestFrame(new HarmonicaSolver.Options { SkipContours = true });
        await vm.Pool.DrainAsync();

        Assert.NotNull(failure);
        vm.PublishFailure(failure!);
        Assert.False(string.IsNullOrEmpty(vm.SolveError));
    }
}
