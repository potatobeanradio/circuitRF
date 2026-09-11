using SkiaSharp;

namespace CircuitRF.Render;

public enum LayoutRulerOrientation { Horizontal, Vertical }

/// <summary>
/// Draws one ruler strip (top or left) in the layout's <see cref="LayoutUnit"/> — tick spacing from
/// the 1/2/5×10ⁿ sequence (<see cref="LayoutGridMath.ComputeRulerTickStepDbu"/>) so labels never
/// collide, plus a cursor position indicator. Framework-free apart from SkiaSharp, mirroring every
/// other renderer in this folder.
/// </summary>
public static class LayoutRulerRenderer
{
    /// <summary>The horizontal strip's HEIGHT.</summary>
    public const double Thickness = 22.0;

    /// <summary>
    /// The vertical strip's WIDTH — wider than <see cref="Thickness"/>, and the two XAML hosts
    /// (<c>LayoutEditorView</c>, <c>WBondProfileView</c>) must match it.
    ///
    /// <para><b>It is sized to hold four characters at <see cref="BaseLabelFontSize"/> and no
    /// more.</b> The vertical ruler writes its label HORIZONTALLY across a strip that used to be
    /// the same 22 px as the horizontal one is tall, which fits about three and a half digits, so
    /// "-1000" — an entirely ordinary coordinate, since the canvas pans through zero in one gesture
    /// — was cut off mid-number with nothing to say it had been. Four characters is the deliberate
    /// floor rather than the widest label imaginable: the strip is chrome beside the drawing, and a
    /// label that needs more room gets a smaller font (see <see cref="ComputeLabelFontSize"/>)
    /// instead of taking that room from the canvas.</para>
    /// </summary>
    public const double VerticalThickness = 26.0;

    private const double MinLabelPixelSpacing = 60.0;

    /// <summary>The label size when everything fits — every ruler drew at this before the fit rule.</summary>
    private const float BaseLabelFontSize = 10f;

    /// <summary>
    /// The floor <see cref="ComputeLabelFontSize"/> will not shrink past. Below this the label stops
    /// being readable, at which point clipping and illegibility cost the same and the larger of the
    /// two is the better answer.
    /// </summary>
    private const float MinLabelFontSize = 7f;

    private const float LabelPadLeft  = 1f;
    private const float LabelPadRight = 1f;

    // See LayoutRenderer's identical [ThreadStatic] cache — same reasoning: Avalonia hands a custom
    // draw operation the whole render-surface canvas, so a bare canvas.Clear(...) with no clip in
    // force wipes every sibling control already painted this frame, not just this ruler's strip.
    [System.ThreadStatic]
    private static SKPaint? _backgroundPaint;

    private static SKPaint BackgroundPaint(SKColor color)
    {
        var paint = _backgroundPaint ??= new SKPaint { Style = SKPaintStyle.Fill };
        paint.Color = color;
        return paint;
    }

    public static void Draw(
        SKCanvas canvas, (double W, double H) size, LayoutRulerOrientation orientation,
        LayoutViewport vp, int dbuPerMicron, LayoutUnit displayUnit, double? cursorWorld,
        LayoutRenderTheme theme)
    {
        canvas.Save();
        try
        {
            var clipRect = SKRect.Create(0, 0, (float)size.W, (float)size.H);
            canvas.ClipRect(clipRect);
            canvas.DrawRect(clipRect, BackgroundPaint(theme.RulerBackground));

            if (vp.Zoom <= 0 || dbuPerMicron <= 0) return;

            long step = LayoutGridMath.ComputeRulerTickStepDbu(vp.Zoom, displayUnit, dbuPerMicron, MinLabelPixelSpacing);
            if (step <= 0) return;

            using var tickPaint = new SKPaint { IsAntialias = false, Color = theme.RulerTick, StrokeWidth = 1f };
            using var textPaint = new SKPaint { IsAntialias = true, Color = theme.RulerText };
            using var font = new SKFont(
                SkiaFonts.PlexRegular, ComputeLabelFontSize(vp, step, dbuPerMicron, displayUnit));

            if (orientation == LayoutRulerOrientation.Horizontal)
                DrawHorizontal(canvas, size, vp, step, dbuPerMicron, displayUnit, tickPaint, textPaint, font);
            else
                DrawVertical(canvas, size, vp, step, dbuPerMicron, displayUnit, tickPaint, textPaint, font);

            if (cursorWorld is { } world)
                DrawCursorIndicator(canvas, size, orientation, vp, world, theme);
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static void DrawHorizontal(
        SKCanvas canvas, (double W, double H) size, LayoutViewport vp, long step,
        int dbuPerMicron, LayoutUnit displayUnit, SKPaint tickPaint, SKPaint textPaint, SKFont font)
    {
        long iStart = (long)Math.Floor(vp.VisibleMinX / step);
        long iEnd   = (long)Math.Ceiling(vp.VisibleMaxX / step);
        if (iEnd - iStart > 4096) return;

        for (long i = iStart; i <= iEnd; i++)
        {
            long wx = i * step;
            float sx = (float)vp.WorldToScreenX(wx);
            if (sx < -20 || sx > size.W + 20) continue;
            canvas.DrawLine(sx, (float)(size.H - 8), sx, (float)size.H, tickPaint);
            string label = LayoutUnits.Format(wx, displayUnit, dbuPerMicron);
            canvas.DrawText(label, sx + 2, (float)(size.H - 10), SKTextAlign.Left, font, textPaint);
        }
    }

    private static void DrawVertical(
        SKCanvas canvas, (double W, double H) size, LayoutViewport vp, long step,
        int dbuPerMicron, LayoutUnit displayUnit, SKPaint tickPaint, SKPaint textPaint, SKFont font)
    {
        long jStart = (long)Math.Floor(vp.VisibleMinY / step);
        long jEnd   = (long)Math.Ceiling(vp.VisibleMaxY / step);
        if (jEnd - jStart > 4096) return;

        for (long j = jStart; j <= jEnd; j++)
        {
            long wy = j * step;
            float sy = (float)vp.WorldToScreenY(wy);
            if (sy < -20 || sy > size.H + 20) continue;
            canvas.DrawLine((float)(size.W - 8), sy, (float)size.W, sy, tickPaint);
            string label = LayoutUnits.Format(wy, displayUnit, dbuPerMicron);
            canvas.DrawText(label, LabelPadLeft, sy - 2, SKTextAlign.Left, font, textPaint);
        }
    }

    /// <summary>
    /// One label size for BOTH strips, chosen so the VERTICAL ruler's widest currently-visible label
    /// fits inside <see cref="VerticalThickness"/>.
    ///
    /// <para><b>Only the vertical labels are measured, and the horizontal ruler takes the answer.</b>
    /// The vertical strip is the only one with a hard width — a horizontal label has the whole tick
    /// spacing (<see cref="MinLabelPixelSpacing"/> at least) to sit in and never runs out of room.
    /// Sizing the two independently would leave a window whose two rulers are lettered differently,
    /// which reads as a rendering fault rather than as a fit. Both controls are handed the same
    /// <paramref name="vp"/> (the canvas's own), so each can compute this identically without the
    /// two needing to talk.</para>
    ///
    /// <para>The result is quantised to a quarter point so that panning — which changes the label
    /// set continuously — steps the size occasionally rather than jittering it every frame.</para>
    /// </summary>
    internal static float ComputeLabelFontSize(
        LayoutViewport vp, long step, int dbuPerMicron, LayoutUnit displayUnit)
    {
        double available = VerticalThickness - LabelPadLeft - LabelPadRight;
        if (available <= 0) return MinLabelFontSize;

        long jStart = (long)Math.Floor(vp.VisibleMinY / step);
        long jEnd   = (long)Math.Ceiling(vp.VisibleMaxY / step);
        if (jEnd - jStart > 4096) return BaseLabelFontSize;   // DrawVertical draws nothing either

        using var probe = new SKFont(SkiaFonts.PlexRegular, BaseLabelFontSize);
        float widest = 0f;
        for (long j = jStart; j <= jEnd; j++)
        {
            double sy = vp.WorldToScreenY(j * step);
            if (sy < -20 || sy > vp.Height + 20) continue;     // same visibility test DrawVertical applies
            float w = probe.MeasureText(LayoutUnits.Format(j * step, displayUnit, dbuPerMicron));
            if (w > widest) widest = w;
        }

        if (widest <= available) return BaseLabelFontSize;

        // Skia's advance widths scale linearly with text size, so one measurement at the base size
        // gives the exact factor; no search is needed.
        float fitted = (float)(BaseLabelFontSize * available / widest);
        fitted = (float)(Math.Floor(fitted * 4.0) / 4.0);
        return Math.Clamp(fitted, MinLabelFontSize, BaseLabelFontSize);
    }

    private static void DrawCursorIndicator(
        SKCanvas canvas, (double W, double H) size, LayoutRulerOrientation orientation,
        LayoutViewport vp, double worldValue, LayoutRenderTheme theme)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = theme.CursorIndicator, StrokeWidth = 2f };
        if (orientation == LayoutRulerOrientation.Horizontal)
        {
            float sx = (float)vp.WorldToScreenX(worldValue);
            if (sx < 0 || sx > size.W) return;
            canvas.DrawLine(sx, 0, sx, (float)size.H, paint);
        }
        else
        {
            float sy = (float)vp.WorldToScreenY(worldValue);
            if (sy < 0 || sy > size.H) return;
            canvas.DrawLine(0, sy, (float)size.W, sy, paint);
        }
    }
}
