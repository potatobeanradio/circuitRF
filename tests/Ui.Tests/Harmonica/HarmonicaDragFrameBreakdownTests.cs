// ================================================================
//  HarmonicaDragFrameBreakdownTests.cs — §1.4 of brief-harmonicarf-r3b-frame-rate-and-loadpull
//
//  §1.1 measured the SOLVE side of an L1-marker-drag frame at ~33 ms (sweep + dataset + loadline) and
//  the owner observed ~11 fps (~90 ms/frame) — "roughly two thirds of the frame is unaccounted for by
//  the solve." This file measures every stage that CAN be measured without a live Avalonia
//  Application (Ui.Tests may not call Avalonia runtime APIs — SkiaSharp canvas drawing is not one of
//  those, it needs no live app/window, which is why HarmonicaRenderBudgetTests/HarmonicaFrameTierCostTests
//  already draw through it): the real tier-A solve (post §1's evaluator work), the real canvas render,
//  and the SolvePool/dispatcher overhead a real drag pays per pointer move. The one stage that
//  genuinely cannot be measured is the §7.5 readout-strip rebuild (real Avalonia
//  StackPanel/TextBlock construction) — ReadoutStripView.SetItems is now self-timed
//  (LastSetItemsMs) so an interactive session reports the real number; this file reports the READOUT
//  ITEM COUNT instead, as the size the strip rebuild scales with.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class HarmonicaDragFrameBreakdownTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public HarmonicaDragFrameBreakdownTests(ITestOutputHelper output)
    {
        _out = output;
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    private static (double Ms, T Value) BestOf<T>(int reps, Func<T> body)
    {
        var value = body();
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

    /// <summary>
    /// The whole mid-drag frame, everything this file CAN measure. Real solve (tier A + dataset +
    /// loadline, exactly what an L1-marker drag runs — §1.5 keeps it fully live), real canvas render
    /// through the same SkiaSharp panel renderers the view uses, and SolvePool's own per-submission
    /// overhead measured against a representative synthetic job.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void MidDragFrame_FullBreakdown_SolveRenderPoolOverhead()
    {
        var theme = HarmonicaRenderTheme.Dark;

        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Source, 1, new Complex(25, 0));
        vm.Terminations.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var solver = new HarmonicaSolver();
        var ctx    = HarmonicaContext.Create(vm.Model);

        // A REAL drag starts from an already-fully-solved document — the user has been looking at a
        // populated 61-point grid with its contours before they ever touch a marker. §1's own
        // carry-forward rule (R-h9r2-1) means those contour polylines stay ON SCREEN, frozen, for
        // every frame of the drag — so a render-cost measurement that starts from an EMPTY grid (no
        // prior full solve) understates what the drag frame actually draws. Solve the full grid
        // once, first, exactly as §6.8's coarse ring set does.
        var fullOpt = new HarmonicaSolver.Options { Rings = 3, Spokes = 12, SkipContours = false };
        var full = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], fullOpt);
        Assert.True(full.SmithPower.GridPoints.Count > 0, "fixture must produce a real contour layer to carry forward");

        // Warm: first drag frame pays JIT/first-touch.
        var dragOpt = new HarmonicaSolver.Options { SkipContours = true, Quality = FrameQuality.Full };
        _ = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], dragOpt,
                         previousPower: full.SmithPower, previousEfficiency: full.SmithEfficiency);

        // ── solve: tier-A sweep + dataset + loadline, exactly what a drag frame runs ────────────
        var (solveMs, frame) = BestOf(9, () => solver.Solve(ctx, vm.Terminations, [.. vm.Markers], dragOpt,
                                                             previousPower: full.SmithPower, previousEfficiency: full.SmithEfficiency));
        Assert.True(frame.SmithPower.GridPoints.Count > 0, "the carried-forward contour layer must survive onto the drag frame");

        // ── render: the same SkiaSharp panel renderers HarmonicaView draws with, at BOTH an ordinary
        // 1x window and a Retina/HiDPI 2x device scale — R1/R4 (HarmonicaRenderBudgetTests) already
        // measured the 2x cost is NOT a small multiple of 1x for this content (contour rasters scale
        // with pixel count), so both are reported rather than assuming one predicts the other.
        (double Ms, string Label)[] renderCases =
        [
            (RenderAt(1600, 1000, 1f, frame, theme), "1x  (1600x1000, an ordinary window)"),
            (RenderAt(1600, 1000, 2f, frame, theme), "2x  (3200x2000 px, a Retina/HiDPI display)"),
        ];

        // Per-panel breakdown — which of the four panels actually dominates the render total. Sized to
        // the SAME sub-rects RenderAt below actually gives each panel in the real 4-panel layout
        // (Smith: w/2 x h*6/10; loadline/power: w*4/10 x h/2) — drawing each at the FULL canvas size
        // instead would inflate every panel's apparent cost and not sum back to the combined total.
        const int SmithW = 800, SmithH = 600, SideW = 640, SideH = 500;
        (string Panel, double Ms1x, double Ms2x)[] perPanel =
        [
            ("SmithPower",      PanelRenderAt(SmithW, SmithH, 1f, theme, c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithPower, theme, darkMode: true)),
                                 PanelRenderAt(SmithW, SmithH, 2f, theme, c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithPower, theme, darkMode: true))),
            ("SmithEfficiency", PanelRenderAt(SmithW, SmithH, 1f, theme, c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithEfficiency, theme, darkMode: true)),
                                 PanelRenderAt(SmithW, SmithH, 2f, theme, c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithEfficiency, theme, darkMode: true))),
            ("Loadline",        PanelRenderAt(SideW, SideH, 1f, theme, c => HarmonicaPanelRenderer.DrawLoadlinePanel(c, (SideW, SideH), frame.Loadline, theme, darkMode: true)),
                                 PanelRenderAt(SideW, SideH, 2f, theme, c => HarmonicaPanelRenderer.DrawLoadlinePanel(c, (SideW, SideH), frame.Loadline, theme, darkMode: true))),
            ("PowerSweep",      PanelRenderAt(SideW, SideH, 1f, theme, c => HarmonicaPanelRenderer.DrawPowerSweepPanel(c, (SideW, SideH), frame.PowerSweep, theme, darkMode: true)),
                                 PanelRenderAt(SideW, SideH, 2f, theme, c => HarmonicaPanelRenderer.DrawPowerSweepPanel(c, (SideW, SideH), frame.PowerSweep, theme, darkMode: true))),
        ];

        // How much of the render is the FROZEN contour polylines specifically — DrawContours has "no
        // geometry cache" by its own doc comment, so a drag frame re-issues every DrawPath call for
        // data that has not changed, every frame. Isolated by re-rendering with Contours cleared.
        var noContours = frame with
        {
            SmithPower      = frame.SmithPower      with { Contours = [] },
            SmithEfficiency = frame.SmithEfficiency with { Contours = [] },
        };
        double render1xNoContours = RenderAt(1600, 1000, 1f, noContours, theme);
        double render2xNoContours = RenderAt(1600, 1000, 2f, noContours, theme);

        // ── pool overhead: CancellationTokenSource + Task.Run per submission, against a
        // representative synthetic job that costs roughly what tier A does. Framework-free —
        // SolvePool knows nothing about HarmonicaFrame.
        using var pool = new SolvePool<int>(workerCount: 1);
        var gate = new ManualResetEventSlim(false);
        pool.Completed += (_, _) => gate.Set();

        double poolMs = BestOf(20, () =>
        {
            gate.Reset();
            pool.Submit((_, ct) => { Thread.Sleep(0); return 0; });
            gate.Wait(1000);
            return 0;
        }).Ms;

        int readoutCount = frame.Readouts.Count;

        _out.WriteLine("§1.4 — mid-drag L1-marker frame, full breakdown (best of 9/20, measured alone)");
        _out.WriteLine($"contour layer carried forward: {frame.SmithPower.GridPoints.Count} Γ points, " +
                       $"{frame.SmithPower.Contours.Count} polylines (frozen, from the pre-drag full solve)");
        _out.WriteLine($"solve  (tier A + dataset + loadline) : {solveMs,8:F2} ms  ({solver.LastSolveCount} HB solves)");
        foreach (var (ms, label) in renderCases)
            _out.WriteLine($"render (canvas, 2 Smith + loadline + power sweep) @{label} : {ms,8:F2} ms");
        _out.WriteLine("  per-panel breakdown (each panel drawn alone, own surface, best of 9):");
        foreach (var (panel, ms1x, ms2x) in perPanel)
            _out.WriteLine($"    {panel,-16} @1x {ms1x,7:F2} ms   @2x {ms2x,7:F2} ms");
        _out.WriteLine($"  of which, the 30 frozen contour polylines alone (no geometry cache — redrawn " +
                       $"from scratch every frame regardless of R-h9r2-1's freeze): " +
                       $"@1x {renderCases[0].Ms - render1xNoContours,6:F2} ms   " +
                       $"@2x {renderCases[1].Ms - render2xNoContours,6:F2} ms");
        _out.WriteLine($"pool   (SolvePool.Submit → Completed round trip, 1 worker) : {poolMs,8:F2} ms");
        _out.WriteLine($"readout strip: {readoutCount} items (rebuild cost NOT measurable here — Ui.Tests " +
                       "may not call Avalonia runtime APIs; ReadoutStripView.SetItems now self-times via " +
                       "LastSetItemsMs for an interactive read)");
        foreach (var (ms, label) in renderCases)
        {
            double total = solveMs + ms + poolMs;
            _out.WriteLine($"measured total (solve+render+pool) @{label} : {total,8:F2} ms " +
                           $"=> {1000.0 / total:F1} fps upper bound (excludes the strip and the Avalonia " +
                           "compositor/dispatcher round trip, neither measurable outside a live app)");
        }
    }

    /// <summary>
    /// brief-harmonicarf-r4 §4.5 — the SAME per-panel table
    /// <see cref="MidDragFrame_FullBreakdown_SolveRenderPoolOverhead"/> reports, "before", but with the
    /// <see cref="HarmonicaBackdropCache"/> WARM: the steady state of an actual marker drag, where the
    /// grid/contour/theme key is unchanged frame to frame and only the live marker glyph moves — exactly
    /// what <c>HarmonicaCanvas</c>'s own persistent per-panel cache instance sees after its first frame.
    /// Directly comparable to the "before" table above: same panel sizes, same fixture, same best-of-9
    /// discipline, only the cache is now on.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void MidDragFrame_PerPanelRenderCost_WithBackdropCache_WarmSteadyState()
    {
        var theme = HarmonicaRenderTheme.Dark;

        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Source, 1, new Complex(25, 0));
        vm.Terminations.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var solver = new HarmonicaSolver();
        var ctx    = HarmonicaContext.Create(vm.Model);

        var fullOpt = new HarmonicaSolver.Options { Rings = 3, Spokes = 12, SkipContours = false };
        var full = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], fullOpt);

        var dragOpt = new HarmonicaSolver.Options { SkipContours = true, Quality = FrameQuality.Full };
        var frame = solver.Solve(ctx, vm.Terminations, [.. vm.Markers], dragOpt,
                                 previousPower: full.SmithPower, previousEfficiency: full.SmithEfficiency);
        Assert.True(frame.SmithPower.GridPoints.Count > 0, "fixture must carry the frozen contour layer, same as the uncached table above");

        const int SmithW = 800, SmithH = 600;
        (string Panel, double Ms1x, double Ms2x)[] perPanel =
        [
            ("SmithPower",
                PanelRenderAtCached(SmithW, SmithH, 1f, theme, cache =>
                    c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithPower, theme,
                                                               darkMode: true, cache: cache, deviceScale: 1.0)),
                PanelRenderAtCached(SmithW, SmithH, 2f, theme, cache =>
                    c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithPower, theme,
                                                               darkMode: true, cache: cache, deviceScale: 2.0))),
            ("SmithEfficiency",
                PanelRenderAtCached(SmithW, SmithH, 1f, theme, cache =>
                    c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithEfficiency, theme,
                                                               darkMode: true, cache: cache, deviceScale: 1.0)),
                PanelRenderAtCached(SmithW, SmithH, 2f, theme, cache =>
                    c => HarmonicaPanelRenderer.DrawSmithPanel(c, (SmithW, SmithH), frame.SmithEfficiency, theme,
                                                               darkMode: true, cache: cache, deviceScale: 2.0))),
        ];

        _out.WriteLine("§4 — per-panel render cost, backdrop cache WARM (steady state of a marker drag: " +
                       "grid/contour/theme key unchanged, only the live marker glyph redrawn), best of 9, measured alone");
        foreach (var (panel, ms1x, ms2x) in perPanel)
            _out.WriteLine($"    {panel,-16} @1x {ms1x,7:F2} ms   @2x {ms2x,7:F2} ms");
    }

    /// <summary>Like <see cref="PanelRenderAt"/>, but backed by a <see cref="HarmonicaBackdropCache"/>
    /// that is warmed by one untimed frame BEFORE the timed loop — reproducing the steady state of a
    /// drag, where the cache was already built on the frame before the one being measured, not the
    /// cold first-build cost (which the counters in <c>HarmonicaBackdropCacheTests</c> already cover).
    /// <paramref name="drawFor"/> receives the (per-call, disposed at the end) cache instance and
    /// returns the actual per-frame draw action closed over it.</summary>
    private static double PanelRenderAtCached(int w, int h, float scale, HarmonicaRenderTheme theme,
                                              Func<HarmonicaBackdropCache, Action<SKCanvas>> drawFor)
    {
        using var cache = new HarmonicaBackdropCache();
        var draw = drawFor(cache);
        using var surface = SKSurface.Create(new SKImageInfo((int)(w * scale), (int)(h * scale)));
        var canvas = surface.Canvas;

        void Frame()
        {
            canvas.Clear(theme.Background);
            canvas.Save();
            canvas.Scale(scale);
            draw(canvas);
            canvas.Restore();
        }

        Frame(); // cold: builds the cache once, exactly like the first frame of a drag
        return BestOf(9, () => { Frame(); return 0; }).Ms;
    }

    /// <summary>Times ONE panel's own draw call in isolation, on its own full-size surface — the same
    /// (W,H) every panel gets in the real 4-panel layout (each panel is drawn into its own placement
    /// rect, but the renderer itself only sees the rect's own size, so this reproduces its real cost).</summary>
    private static double PanelRenderAt(int w, int h, float scale, HarmonicaRenderTheme theme, Action<SKCanvas> draw)
        => BestOf(9, () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)(w * scale), (int)(h * scale)));
            var canvas = surface.Canvas;
            canvas.Clear(theme.Background);
            canvas.Save();
            canvas.Scale(scale);
            draw(canvas);
            canvas.Restore();
            return 0;
        }).Ms;

    private static double RenderAt(int w, int h, float scale, HarmonicaFrame frame, HarmonicaRenderTheme theme)
        => BestOf(9, () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)(w * scale), (int)(h * scale)));
            var canvas = surface.Canvas;
            canvas.Clear(theme.Background);
            canvas.Save();
            canvas.Scale(scale);
            HarmonicaPanelRenderer.DrawSmithPanel(canvas, (w / 2, h * 6 / 10), frame.SmithPower,      theme, darkMode: true);
            HarmonicaPanelRenderer.DrawSmithPanel(canvas, (w / 2, h * 6 / 10), frame.SmithEfficiency, theme, darkMode: true);
            HarmonicaPanelRenderer.DrawLoadlinePanel(canvas, (w * 4 / 10, h / 2), frame.Loadline, theme, darkMode: true);
            HarmonicaPanelRenderer.DrawPowerSweepPanel(canvas, (w * 4 / 10, h / 2), frame.PowerSweep, theme, darkMode: true);
            canvas.Restore();
            return 0;
        }).Ms;
}
