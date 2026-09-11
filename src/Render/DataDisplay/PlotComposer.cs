// ================================================================
//  PlotComposer.cs  —  laying several plots, their axis label strips and
//  their marker info boxes onto one page, and drawing them
//
//  RND-4 (brief-render-4-data-display.md R-rnd4-2 / R-rnd4-6). Every
//  number here was `PlotExporter.RenderContainersToCanvas`'s, and the
//  arithmetic is unchanged on purpose: it is what makes a marker info
//  box the user DRAGGED land in the exported page exactly where it sits
//  on screen, at any zoom, any aspect and even when the box was dragged
//  outside the plot area. R-rnd4-6 asks for the page to become a
//  parameter and for nothing else about the composition to move.
//
//  WHAT CHANGED IS THE INPUT, NOT THE MATH. The exporter read a
//  `PlotContainerViewModel` and a `MarkerInfoBoxViewModel`; this reads
//  `PlacedPlot` and `PlacedMarkerBox`, which are the same six numbers
//  with no view model behind them. src/Ui fills them from its containers,
//  src/Cli fills them from a `.cdd`, and there is one composition rather
//  than two that agree by inspection.
//
//  THE COORDINATES ARE SCREEN (VIEW) PIXELS, i.e. logical × zoom + pan,
//  which is what the exporter always used. Zoom cancels: the bounding box
//  scales with it, `S` scales inversely, and every product is
//  zoom-invariant — which is why an export looks the same whatever the
//  canvas is zoomed to. A caller with no canvas (the CLI) passes logical
//  coordinates, which is zoom = 1 and pan = 0.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

/// <summary>The page a composition is laid out on, in points at 72 dpi.</summary>
/// <remarks>
/// <see cref="Letter"/> — 792 × 612 with 36 pt margins — is the size the Data Display's Export and
/// Copy have always written, and is the default everywhere so an unadorned export is byte-identical
/// to what it was before the page became a parameter.
/// </remarks>
public readonly record struct PagePlacement(float Width, float Height, float Margin)
{
    public static PagePlacement Letter { get; } = new(792f, 612f, 36f);

    public float UsableWidth  => Width  - 2f * Margin;
    public float UsableHeight => Height - 2f * Margin;
}

/// <summary>One trace's Y-axis label strip, as the composition needs it.</summary>
/// <param name="AutoLabel">
/// <b>The plot-level MINIMAL label</b> (<see cref="TraceLabeler.ComputeMinimalLabels"/>) — what the
/// window's own strip shows. Null falls back to the trace's own description, which is what every
/// caller written before this passed.
///
/// <para><b>It is here because the headless strip did not have it, and the two diverged.</b> RND-4's
/// rule is that a headless render produces what the window produces; the strip text did not, because
/// the window read its AutoLabel and the composer read <see cref="Trace.Description"/> — which for a
/// cube trace is <c>Expression ?? CubeName</c> and nothing else. Two cuts of one pattern, differing
/// only in their pinned φ, therefore printed the SAME strip ("farfield.U" twice) in every exported
/// and CLI-drawn picture while the window told them apart correctly.</para>
/// </param>
public sealed record PlacedLabelStrip(Trace Trace, string? CustomLabel, bool ShowFilePrefix,
                                      string? AutoLabel = null);

/// <summary>One marker info box, positioned in the same screen space as the plots.</summary>
public sealed record PlacedMarkerBox(
    Marker                Marker,
    Trace                 Trace,
    FreqUnit              FreqUnit,
    double                ViewLeft,
    double                ViewTop,
    double                BoxWidth,
    double                BoxHeight,
    IReadOnlyList<Trace>? PlotTraces);

/// <summary>
/// One plot's placement on the canvas — everything <c>PlotContainerViewModel</c> exposed to the
/// exporter and nothing else.
/// </summary>
public sealed class PlacedPlot
{
    public required Plot   Plot         { get; init; }
    public required double ViewLeft     { get; init; }
    public required double ViewTop      { get; init; }
    public required double ViewWidth    { get; init; }
    public required double ViewHeight   { get; init; }

    /// <summary>
    /// The container's LOGICAL width — the denominator of a Table plot's own zoom factor, which is
    /// the ratio of drawn width to authored width. Not <see cref="ViewWidth"/>: that carries the
    /// canvas zoom, and the ratio must not.
    /// </summary>
    public required double LogicalWidth { get; init; }

    /// <summary>Screen width of ONE label strip (zero when there are none).</summary>
    public double LabelStripViewWidth { get; init; }

    public IReadOnlyList<PlacedLabelStrip> LeftLabelStrips  { get; init; } = Array.Empty<PlacedLabelStrip>();
    public IReadOnlyList<PlacedLabelStrip> RightLabelStrips { get; init; } = Array.Empty<PlacedLabelStrip>();
    public IReadOnlyList<PlacedMarkerBox>  MarkerBoxes      { get; init; } = Array.Empty<PlacedMarkerBox>();

    /// <summary>Whether a trace label carries its source's name — the library-count heuristic, already resolved.</summary>
    public bool ShowFilePrefix   { get; init; }
    public bool AlwaysShowSource { get; init; }

    /// <summary>A trace's source alias, when the display has one. Null means "no aliases".</summary>
    public Func<Trace, string?>? AliasFor { get; init; }
}

public static class PlotComposer
{
    /// <summary>
    /// Draws every plot in <paramref name="plots"/> onto <paramref name="canvas"/>, scaled
    /// uniformly so their common bounding box — plots, label strips and marker info boxes together
    /// — fills the page's usable area and is centred on it.
    /// </summary>
    /// <remarks>
    /// The background is cleared here, from <paramref name="settings"/>, and the theme is put
    /// through the export override — both were the exporter's first two statements and both belong
    /// with the composition rather than with the caller, so the CLI cannot forget one.
    /// </remarks>
    public static void Render(
        SKCanvas                  canvas,
        IReadOnlyList<PlacedPlot> plots,
        RenderTheme               theme,
        AppSettings               settings,
        PagePlacement             page)
    {
        float usableW = page.UsableWidth;
        float usableH = page.UsableHeight;
        float margin  = page.Margin;

        theme = settings.GetExportRenderTheme(theme);

        canvas.Clear(settings.ExportTransparentBackground
            ? SKColors.Transparent
            : theme.BackgroundColor);
        if (plots.Count == 0) return;

        // ---- Bounding box in DataDisplay screen-pixel space --------

        double bndL = double.PositiveInfinity;
        double bndT = double.PositiveInfinity;
        double bndR = double.NegativeInfinity;
        double bndB = double.NegativeInfinity;

        foreach (var c in plots)
        {
            double sw     = c.LabelStripViewWidth;
            int    nLeft  = c.LeftLabelStrips.Count;
            int    nRight = c.RightLabelStrips.Count;

            bndL = Math.Min(bndL, c.ViewLeft - nLeft  * sw);
            bndT = Math.Min(bndT, c.ViewTop);
            bndR = Math.Max(bndR, c.ViewLeft + c.ViewWidth + nRight * sw);
            bndB = Math.Max(bndB, c.ViewTop  + c.ViewHeight);

            foreach (var box in c.MarkerBoxes)
            {
                bndL = Math.Min(bndL, box.ViewLeft);
                bndT = Math.Min(bndT, box.ViewTop);
                bndR = Math.Max(bndR, box.ViewLeft + box.BoxWidth);
                bndB = Math.Max(bndB, box.ViewTop  + box.BoxHeight);
            }
        }

        if (double.IsInfinity(bndL)) return;

        double bndW = bndR - bndL;
        double bndH = bndB - bndT;

        float S    = (bndW > 0 && bndH > 0)
            ? (float)Math.Min(usableW / bndW, usableH / bndH)
            : 1f;
        float padX = (usableW - (float)bndW * S) / 2f;
        float padY = (usableH - (float)bndH * S) / 2f;

        // ---- Draw each plot ----------------------------------------

        foreach (var c in plots)
        {
            var plot = c.Plot;

            double sw     = c.LabelStripViewWidth;
            int    nLeft  = c.LeftLabelStrips.Count;
            int    nRight = c.RightLabelStrips.Count;

            float plotX  = margin + padX + (float)(c.ViewLeft - bndL) * S;
            float plotY  = margin + padY + (float)(c.ViewTop  - bndT) * S;
            float plotW  = (float)c.ViewWidth  * S;
            float plotH  = (float)c.ViewHeight * S;
            float stripW = (float)sw * S;

            // Label strips (Smith / Polar only)
            if (nLeft + nRight > 0)
            {
                var   tf       = PlotRenderer.BuildTransforms(plot, (plotW, plotH));
                float vpTop    = (float)(tf.Viewport.Y * plotH);
                float vpBottom = (float)((tf.Viewport.Y + tf.Viewport.Height) * plotH);
                float chartH   = vpBottom - vpTop;
                float chartY   = plotY + vpTop;

                for (int i = 0; i < nLeft; i++)
                {
                    var s = c.LeftLabelStrips[i];
                    DrawAxisLabelStrip(canvas, plotX - (i + 1) * stripW, chartY,
                        stripW, chartH, s.Trace, false, theme, s.CustomLabel, s.ShowFilePrefix,
                        s.AutoLabel);
                }
                for (int i = 0; i < nRight; i++)
                {
                    var s = c.RightLabelStrips[i];
                    DrawAxisLabelStrip(canvas, plotX + plotW + i * stripW, chartY,
                        stripW, chartH, s.Trace, true, theme, s.CustomLabel, s.ShowFilePrefix,
                        s.AutoLabel);
                }
            }

            // Main plot content
            float cTableZoom = (plot.PlotType == PlotType.Table)
                ? plotW / (float)c.LogicalWidth
                : 1f;

            canvas.Save();
            canvas.Translate(plotX, plotY);
            PlotRenderer.Draw(canvas, (plotW, plotH), plot, PlotDetail.Full, theme, c.ShowFilePrefix,
                zoomLevel: cTableZoom,
                aliasFor: c.AliasFor,
                alwaysShowSource: c.AlwaysShowSource);
            canvas.Restore();

            // Marker info boxes
            foreach (var box in c.MarkerBoxes)
            {
                float bx = margin + padX + (float)(box.ViewLeft - bndL) * S;
                float by = margin + padY + (float)(box.ViewTop  - bndT) * S;
                float bw = (float)box.BoxWidth  * S;
                float bh = (float)box.BoxHeight * S;

                canvas.Save();
                canvas.Translate(bx, by);
                MarkerRenderer.DrawInfoBox(
                    canvas, (bw, bh),
                    box.Marker, box.Trace, box.FreqUnit,
                    theme, c.ShowFilePrefix,
                    transparentBackground: settings.MarkerBoxTransparentBackground,
                    plotTraces: box.PlotTraces);
                canvas.Restore();
            }
        }
    }

    // ---- Axis label strip (mirrors AxisLabelControl.LabelDrawOperation.Render) ----

    private static void DrawAxisLabelStrip(
        SKCanvas    canvas,
        float       x,
        float       y,
        float       w,
        float       h,
        Trace       trace,
        bool        isRight,
        RenderTheme theme,
        string?     customLabel,
        bool        showFilePrefix,
        string?     autoLabel)
    {
        float cap        = w * 0.85f;
        float fontSizePx = MathF.Min(MathF.Max(h * 0.04f, MathF.Min(6f, cap)), cap);

        bool    useCustom   = !string.IsNullOrEmpty(customLabel);
        string  displayText = useCustom ? customLabel!
                            : !string.IsNullOrEmpty(autoLabel) ? autoLabel!
                            : (showFilePrefix ? trace.Description : trace.ShortDescription);
        SKColor textColor   = useCustom ? theme.TextColor
                            : RenderTheme.ToSKColor(trace.Properties.LineColor);

        // Per-glyph DejaVu fallback — this is a TRACE label, so it carries whatever the group and
        // cube names carry, and IBM Plex does not cover all of it (U+25B8 "▸", the group separator,
        // is the one that reached a plot). Skia draws a missing glyph as a NOTDEF box.
        using var font     = new SKFont(SkiaFonts.PlexRegular, fontSizePx);
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, fontSizePx);
        using var paint = new SKPaint { Color = textColor, IsAntialias = true };

        string text   = displayText;
        float  maxLen = h - 12f;
        while (text.Length > 1 && RendererText.MeasureTextWithFallback(text, font, fallback) > maxLen)
            text = text[..^1];
        if (text.Length < displayText.Length)
            text = text.TrimEnd() + "…";

        float tw = RendererText.MeasureTextWithFallback(text, font, fallback);
        float cx = x + w / 2f;
        float cy = y + h / 2f;

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.RotateDegrees(isRight ? 90f : -90f);
        RendererText.DrawLeftTextWithFallback(
            canvas, text, -tw / 2f, font.Size * 0.35f, font, fallback, paint);
        canvas.Restore();
    }
}
