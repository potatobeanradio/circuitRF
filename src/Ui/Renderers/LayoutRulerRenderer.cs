using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public enum LayoutRulerOrientation { Horizontal, Vertical }

/// <summary>
/// Draws one ruler strip (top or left) in the layout's <see cref="LayoutUnit"/> — tick spacing from
/// the 1/2/5×10ⁿ sequence (<see cref="LayoutGridMath.ComputeRulerTickStepDbu"/>) so labels never
/// collide, plus a cursor position indicator. Framework-free apart from SkiaSharp, mirroring every
/// other renderer in this folder.
/// </summary>
public static class LayoutRulerRenderer
{
    public const double Thickness = 22.0;
    private const double MinLabelPixelSpacing = 60.0;

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
            using var font = new SKFont(SkiaFonts.PlexRegular, 10f);

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
            canvas.DrawText(label, 2, sy - 2, SKTextAlign.Left, font, textPaint);
        }
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
