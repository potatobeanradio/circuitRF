// ================================================================
//  HarmonicaDragCostTests.cs  —  §8.1's measurement, brief-harmonicarf-h6
//
//  "The M1 gesture numbers — measured frame time during a real drag, and which ladder rung it settled
//  on. Tier 9 says only freeze-and-snap holds 30 fps on the shipping model; confirm or contradict
//  that from an actual gesture."
//
//  Cost discipline (§6): Benchmark-tagged, non-parallel, best-of-N minimum, measured ALONE.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Trait("Category", "Benchmark")]
[Collection("HarmonicaUiBenchmarks")]
public sealed class HarmonicaDragCostTests(ITestOutputHelper output)
{
    private const double W = 1400, H = 900;

    private static (double X, double Y) OnPowerPanel(HarmonicaViewModel vm, Complex gamma)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.GammaToCanvas(gamma, (p.W * W, p.H * H));
        return (p.X * W + local.X, p.Y * H + local.Y);
    }

    [Fact]
    public async Task M1Cost_ARealFortyMoveDrag_ItsFrameTimesAndTheRungItSettlesOn()
    {
        var vm = new HarmonicaViewModel();

        var costs = new List<(FrameQuality Quality, FrameTiming Timing)>();
        vm.Pool.Completed += (f, seq) =>
        {
            lock (costs) costs.Add((f.Quality, f.Timing));
            vm.PublishFrame(f);
            // brief-harmonicarf-r5 §3 — settles conflate-and-pace so a real 40-move drag resubmits
            // as each mid-drag solve finishes, exactly as the live view does.
            vm.OnPoolSettled(seq);
        };

        // The document's own opening frame, exactly as HarmonicaView.EnsureFirstSolve produces it.
        var first = Stopwatch.StartNew();
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        first.Stop();

        var opening = costs[^1];
        output.WriteLine($"opening frame ({opening.Quality}): tierA {opening.Timing.TierAMs:F1} ms, " +
                         $"grid {opening.Timing.GridSolveMs:F1} ms, fit {opening.Timing.FitMs:F2} ms, " +
                         $"raster {opening.Timing.RasterMs:F1} ms — total {opening.Timing.TotalMs:F0} ms " +
                         $"(wall {first.Elapsed.TotalMilliseconds:F0} ms)");
        output.WriteLine($"after it, the ladder is at {vm.Scheduler.Quality}");
        output.WriteLine("");

        // Now a real drag: pointer down on L1, 40 moves along an arc, release.
        lock (costs) costs.Clear();
        var marker = vm.Markers[1];
        var g = new HarmonicaGesture(vm);
        var (sx, sy) = OnPowerPanel(vm, marker.Gamma);
        Assert.True(g.PointerDown(sx, sy, W, H));

        var release = new Complex(0.55, -0.30);
        var wall = Stopwatch.StartNew();
        for (int i = 1; i <= 40; i++)
        {
            var t = marker.Gamma + (release - marker.Gamma) * (i / 40.0);
            var (mx, my) = OnPowerPanel(vm, t);
            g.PointerMoved(mx, my, W, H);
        }
        await vm.Pool.DrainAsync();
        var (ux, uy) = OnPowerPanel(vm, release);
        g.PointerUp(ux, uy, W, H);
        await vm.Pool.DrainAsync();
        wall.Stop();

        List<(FrameQuality Quality, FrameTiming Timing)> frames;
        lock (costs) frames = [.. costs];

        output.WriteLine("rung             tierA    gridSolve      fit    raster    render     total");
        output.WriteLine("(render reads 0.00 here — it is measured by HarmonicaCanvas's draw operation,");
        output.WriteLine(" which needs a window. H4-H5's HarmonicaRenderBudgetTests measured it at");
        output.WriteLine(" 6.13 ms / 19.61 ms for the whole four-panel layout at 1x / 2x.)");
        foreach (var (q, t) in frames)
            output.WriteLine($"{q,-14} {t.TierAMs,7:F1} {t.GridSolveMs,11:F1} {t.FitMs,8:F2} " +
                             $"{t.RasterMs,9:F1} {t.RenderMs,9:F2} {t.TotalMs,9:F1}");

        output.WriteLine("");
        output.WriteLine($"the drag settled on {vm.Scheduler.Quality}; " +
                         $"{vm.Pool.SupersededCount} of {vm.Pool.StartedCount + vm.Pool.SupersededCount} " +
                         $"requests were superseded (latest-wins), whole gesture " +
                         $"{wall.Elapsed.TotalMilliseconds:F0} ms of wall clock");
        output.WriteLine($"status strip says: {vm.StatusMessage ?? "(nothing)"}");

        // Tier 9's claim, re-checked from an actual gesture rather than from a synthetic frame.
        var frozen = frames.Where(f => f.Quality == FrameQuality.FrozenContours).ToList();
        var bearing = frames.Where(f => f.Quality != FrameQuality.FrozenContours).ToList();
        if (frozen.Count > 0)
            output.WriteLine($"freeze-and-snap frames: min {frozen.Min(f => f.Timing.TotalMs):F1} ms " +
                             $"({frozen.Count} of them) — 30 fps needs ≤ 33.3 ms");
        if (bearing.Count > 0)
            output.WriteLine($"contour-bearing frames: min {bearing.Min(f => f.Timing.TotalMs):F1} ms " +
                             $"({bearing.Count} of them)");

        Assert.NotEmpty(frames);
        Assert.Equal(FrameQuality.Full, frames[^1].Quality);          // the snap
    }
}

/// <summary>Timing tests in Ui.Tests run one at a time, for the same reason the Harmonica project's
/// do: a benchmark sharing a run with others reads more than twice as slow.</summary>
[Xunit.CollectionDefinition("HarmonicaUiBenchmarks", DisableParallelization = true)]
public sealed class HarmonicaUiBenchmarkCollection;
