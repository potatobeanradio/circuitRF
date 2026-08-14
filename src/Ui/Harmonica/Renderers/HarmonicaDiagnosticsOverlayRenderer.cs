// ================================================================
//  HarmonicaDiagnosticsOverlayRenderer.cs  —  §1 of
//  brief-harmonicarf-r5-the-unmeasured-stage-and-drag-starvation
//
//  §1.2 — "the overlay must not distort what it measures. It draws on the same canvas whose cost is
//  under investigation. Keep it to plain text, no chart, no antialiased chrome, and time its own draw
//  so the overlay's cost is visible in the overlay." That is the whole design brief for this file:
//  one flat, non-antialiased backing rect, one non-antialiased monospace-ish text paint, no per-line
//  decoration, and a Stopwatch around the lot.
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica.Renderers;

/// <summary>
/// Draws one <see cref="HarmonicaDiagnosticsOverlay"/>'s current rolling-window stats plus the other
/// live counters §1.1 asks for, as a flat text block in the canvas's top-left corner.
///
/// <para><b>Theme-independent on purpose</b> — a HUD meant to stay legible while the owner is
/// dragging, watching FOR a stutter, should not depend on whichever Harmonica.* role happens to be
/// active; a fixed dark backing plus light text is the same convention a game engine's own debug
/// overlay uses, for the same reason.</para>
/// </summary>
public static class HarmonicaDiagnosticsOverlayRenderer
{
    private const float LineHeightPx = 15f;
    private const float PaddingPx    = 6f;
    private const float TextSizePx   = 12f;

    /// <summary>
    /// Draws the HUD and writes this call's own cost back onto <paramref name="diagnostics"/>'s
    /// <see cref="HarmonicaDiagnosticsOverlay.LastDrawMs"/> — read by the NEXT call, per that
    /// property's own one-frame-behind convention.
    /// </summary>
    public static void Draw(SKCanvas canvas, HarmonicaDiagnosticsOverlay diagnostics,
                            HarmonicaViewModel vm, double readoutSetItemsMs, double readoutSetInputsMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var stats = diagnostics.Compute();
        var pool  = vm.Pool;
        var t     = vm.Frame.Timing;
        double ratio = pool.StartedCount > 0 ? (double)pool.CompletedCount / pool.StartedCount : 0.0;

        var lines = new List<string>
        {
            "harmonicaRF diagnostics (§1) — Display ▸ Diagnostics Overlay to reset",
            $"frame interval  last {stats.LastMs,6:F1}  mean {stats.MeanMs,6:F1}  " +
                $"p95 {stats.P95Ms,6:F1}  p99 {stats.P99Ms,6:F1}  max {stats.MaxMs,6:F1} ms   " +
                $">33ms: {stats.OverBudgetCount}/{stats.SampleCount}",
            $"solve   tierA {t.TierAMs,6:F1}  grid {t.GridSolveMs,6:F1}  fit {t.FitMs,6:F2}  " +
                $"raster {t.RasterMs,6:F1}  render {vm.LastRenderMs,6:F2} ms",
            $"strip   SetItems {readoutSetItemsMs,6:F2}  SetInputs {readoutSetInputsMs,6:F2} ms",
            $"pool    started {pool.StartedCount,5}  completed {pool.CompletedCount,5}  " +
                $"superseded {pool.SupersededCount,5}  completed/started {ratio,5:F2}",
            $"counters  no-op-skip {vm.NoOpDragFrameSkipCount,5}  lever1-disabled {vm.Lever1DisabledCount,5}",
            $"GC      gen0 +{stats.Gen0Delta,3}  gen1 +{stats.Gen1Delta,3}  " +
                $"(over the last {stats.SampleCount} recorded frames)",
            $"overlay draw {diagnostics.LastDrawMs,6:F3} ms (this class's own cost — simplify if this " +
                "climbs past ~0.2 ms)",
        };

        float width  = 600f;
        float height = PaddingPx * 2 + LineHeightPx * lines.Count;

        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = false };
        canvas.DrawRect(new SKRect(0, 0, width, height), bg);

        using var font = new SKFont(SkiaFonts.PlexRegular, TextSizePx) { Subpixel = false, Edging = SKFontEdging.Alias };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = false };

        float y = PaddingPx + TextSizePx;
        foreach (string line in lines)
        {
            canvas.DrawText(line, PaddingPx, y, SKTextAlign.Left, font, text);
            y += LineHeightPx;
        }

        sw.Stop();
        diagnostics.LastDrawMs = sw.Elapsed.TotalMilliseconds;
    }
}
