// ================================================================
//  HarmonicaPowerSweepDragCostTests.cs — brief-harmonicarf-r2b R-h9r2-19
//
//  "EVERY point, EVERY frame, drag included. No decimation, ever." Tier A now drives the full
//  Start..Stop ladder (PinSearch.Sweep) on every frame rather than PinSearch.Run's cheap bracket, so
//  this measures what that actually costs during a real drag, before and after R-h9r2-19's lever 1
//  (frame-to-frame warm start at each Pin LEVEL) — the number this brief owes a report on.
//
//  Cost discipline: Benchmark-tagged, non-parallel, measured ALONE.
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
public sealed class HarmonicaPowerSweepDragCostTests(ITestOutputHelper output)
{
    private const double W = 1400, H = 900;

    /// <summary>The shipped default document's own DUT, but with the Pin range forced to the record's
    /// own default (−10…50 dBm, 1 dB step ⇒ 61 points) — <c>DefaultModel()</c> itself tunes PinMaxDbm
    /// to 34 dBm for its own demo reasons, which is not the range this brief's own gates are stated
    /// against ("the default 61-point range").</summary>
    private static HarmonicaViewModel NewVm()
    {
        var model = HarmonicaViewModel.DefaultModel();
        model = model with { Settings = model.Settings with { PinStartDbm = -10, PinMaxDbm = 50, PinStepDbm = 1 } };
        return new HarmonicaViewModel(model);
    }

    private static (double X, double Y) OnPowerPanel(HarmonicaViewModel vm, Complex gamma)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.GammaToCanvas(gamma, (p.W * W, p.H * H));
        return (p.X * W + local.X, p.Y * H + local.Y);
    }

    [Fact]
    public async Task M1_TheOpeningFrame_Cold_AndAFortyMoveDrag_Warm_TierACostPerFrame()
    {
        var vm = NewVm();

        // Note: HarmonicaSolver.LastSolveCount is per-WORKER instance state, not per-frame — with
        // multiple SolvePool workers in flight it cannot be reliably attributed to any one completed
        // frame here, so this measures wall-clock (FrameTiming.TierAMs, stamped ON the frame itself
        // by the worker that solved it) rather than a solve count.
        var frames = new List<(FrameTiming Timing, int Points)>();
        vm.Pool.Completed += (f, seq) =>
        {
            lock (frames) frames.Add((f.Timing, f.PowerSweep.PinAvailDbm.Length));
            vm.PublishFrame(f);
            // brief-harmonicarf-r5 §3 — settles conflate-and-pace, same as the live view.
            vm.OnPoolSettled(seq);
        };

        // ── the opening frame: COLD, no prior-frame spectra to warm-start from ──────────────
        var openWall = Stopwatch.StartNew();
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        openWall.Stop();
        var opening = frames[^1];
        output.WriteLine($"opening frame (cold): {opening.Points} pts, " +
                         $"tierA {opening.Timing.TierAMs:F1} ms (wall {openWall.Elapsed.TotalMilliseconds:F1} ms)");

        // ── a real 40-move drag: every frame after the first can warm-start per-level from the
        //    PREVIOUS frame's converged spectra (R-h9r2-19 lever 1, HarmonicaSolver._lastSweepLevelSpectra) ──
        lock (frames) frames.Clear();
        var marker = vm.Markers[1];
        var g = new HarmonicaGesture(vm);
        var (sx, sy) = OnPowerPanel(vm, marker.Gamma);
        Assert.True(g.PointerDown(sx, sy, W, H));

        var release = marker.Gamma + new Complex(0.15, -0.10);
        var dragWall = Stopwatch.StartNew();
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
        dragWall.Stop();

        List<(FrameTiming Timing, int Points)> dragFrames;
        lock (frames) dragFrames = [.. frames];

        Assert.NotEmpty(dragFrames);
        // R-h9r2-19's own gate: no decimation, ever — every dragging frame carries the FULL ladder,
        // exactly like the released/opening one.
        Assert.All(dragFrames, f => Assert.Equal(opening.Points, f.Points));

        double avgTierA = dragFrames.Average(f => f.Timing.TierAMs);
        double minTierA = dragFrames.Min(f => f.Timing.TierAMs);
        double maxTierA = dragFrames.Max(f => f.Timing.TierAMs);
        double fps = dragFrames.Count / (dragWall.Elapsed.TotalMilliseconds / 1000.0);

        output.WriteLine("");
        output.WriteLine($"drag: {dragFrames.Count} of 40 requested moves actually PUBLISHED " +
                         $"(SolvePool's own latest-wins superseding — {40 - dragFrames.Count} superseded), " +
                         $"{opening.Points} pts/frame each");
        output.WriteLine($"tierA per frame (warm, lever 1 on): avg {avgTierA:F2} ms, " +
                         $"min {minTierA:F2} ms, max {maxTierA:F2} ms — vs. cold opening {opening.Timing.TierAMs:F1} ms");
        output.WriteLine($"achieved publish rate over the gesture: {fps:F1} fps ({dragWall.Elapsed.TotalMilliseconds:F0} ms / {dragFrames.Count} frames)");
        output.WriteLine($"§6.8's 33.3 ms/frame (30 fps) target: tierA alone {(avgTierA <= 33.3 ? "meets it on average" : "does NOT reliably meet it")} on this 61-point fixture.");
    }
}
