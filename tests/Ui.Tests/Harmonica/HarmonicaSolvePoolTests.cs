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
