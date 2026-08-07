// ================================================================
//  HarmonicaFrameTierCostTests.cs  —  M6 / Tier 9 of brief-harmonicarf-h4-h5
//
//  TIER 9  cost: §3's five render measurements (M1, HarmonicaRenderBudgetTests) PLUS frame time at
//          each degradation tier — which is this file. Measured on the REAL path: the real solver,
//          the real grid, the real renderers, at the four rungs §6.8 names.
//
//  Category=Benchmark, taken ALONE, best-of-N. This repo has been bitten three times by a timing
//  test that shared a parallel run; the discipline is restated here rather than assumed.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class HarmonicaFrameTierCostTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public HarmonicaFrameTierCostTests(ITestOutputHelper output)
    {
        _out = output;
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Tier9_FrameTimeAtEachDegradationTier()
    {
        const int W = 700, H = 560;
        var theme = HarmonicaRenderTheme.Dark;

        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Source, 1, new Complex(25, 0));
        vm.Terminations.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var solver = new HarmonicaSolver();
        var ctx    = HarmonicaContext.Create(vm.Model);
        var grid   = new ContourGrid();

        // Warm: the first frame pays the DCIV family (tier C, computed once and held) and every
        // first-touch JIT. Timing it would measure neither the solve nor the render.
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers],
                         new HarmonicaSolver.Options { Rings = 1, Spokes = 4, SkipContours = true },
                         grid);

        var clock = new SyntheticClock();
        var sched = new FrameScheduler(clock.Read);

        var rows = new List<(FrameQuality Q, int Pts, double SolveMs, double RenderMs, int Solves)>();

        foreach (var q in new[] { FrameQuality.Full, FrameQuality.CoarseRaster,
                                  FrameQuality.CoarseGrid, FrameQuality.FrozenContours })
        {
            // Drive the scheduler down to this rung so the plan comes from the scheduler itself,
            // not from a hand-written copy of its numbers.
            sched.Reset();
            while (sched.Quality != q)
            {
                sched.RecordFrame(sched.NextPlan(true), new FrameTiming(4, 900, 6, 90, 10));
                clock.Advance(50);
            }
            var plan = sched.NextPlan(dragging: true);
            Assert.Equal(q, plan.Quality);

            var opt = new HarmonicaSolver.Options
            {
                Rings            = plan.Rings,
                Spokes           = plan.Spokes,
                RasterResolution = plan.RasterResolution,
                SkipContours     = plan.SkipContours,
            };

            var (solveMs, frame) = BestOf(5, () => solver.Solve(ctx, vm.Terminations,
                                                               [.. vm.Markers], opt, grid));

            double renderMs = BestOf(5, () =>
            {
                using var surface = SKSurface.Create(new SKImageInfo(W, H));
                surface.Canvas.Clear(theme.Background);
                HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), frame.SmithPower,
                                                      theme, darkMode: true);
                HarmonicaPanelRenderer.DrawSmithPanel(surface.Canvas, (W, H), frame.SmithEfficiency,
                                                      theme, darkMode: true);
                HarmonicaPanelRenderer.DrawLoadlinePanel(surface.Canvas, (W, H), frame.Loadline,
                                                         theme, darkMode: true);
                HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), frame.PowerSweep,
                                                           theme, darkMode: true);
                return 0;
            }).Ms;

            rows.Add((q, frame.SmithPower.GridPoints.Count, solveMs, renderMs, solver.LastSolveCount));
        }

        _out.WriteLine("");
        _out.WriteLine("Tier 9 — frame time at each degradation tier (best of 5, measured alone)");
        _out.WriteLine($"{"rung",-16} {"Γ pts",6} {"HB solves",10} {"solve ms",10} {"render ms",10} {"total ms",10}");
        foreach (var r in rows)
            _out.WriteLine($"{r.Q,-16} {r.Pts,6} {r.Solves,10} {r.SolveMs,10:F1} " +
                           $"{r.RenderMs,10:F2} {r.SolveMs + r.RenderMs,10:F1}");

        // The ladder must actually BUY something — a degradation that costs the same as the rung
        // above it is a policy that only looks like it is working. Loose factors on purpose: this is
        // a monotonicity claim, not a wall-clock budget.
        var byRung = rows.ToDictionary(r => r.Q);
        Assert.True(byRung[FrameQuality.FrozenContours].SolveMs < byRung[FrameQuality.Full].SolveMs,
            "freeze-and-snap must cost materially less than the full grid — it solves no contours at all");
        Assert.True(byRung[FrameQuality.CoarseGrid].Solves <= byRung[FrameQuality.CoarseRaster].Solves,
            "the coarse ring set must cost no more HB solves than the full grid");
        Assert.Equal(0, byRung[FrameQuality.FrozenContours].Pts);
    }

    private sealed class SyntheticClock
    {
        private double _now;
        public double Read() => _now;
        public void Advance(double ms) => _now += ms;
    }

    /// <summary>Best-of-N minimum. A genuine cost is low in EVERY sample; the minimum is the one
    /// statistic a descheduled sample cannot inflate.</summary>
    private static (double Ms, T Value) BestOf<T>(int reps, Func<T> body)
    {
        var value = body();                                     // warm
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            value = body();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return (best, value);
    }
}
