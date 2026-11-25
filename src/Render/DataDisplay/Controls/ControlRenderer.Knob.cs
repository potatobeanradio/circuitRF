// ================================================================
//  ControlRenderer.Knob.cs  —  the knob (brief-ddl-6 R-ddl6-1)
//
//  A dial over a 270° sweep, open at the bottom: the first stop at lower
//  left, the last at lower right, the middle straight up. Tick marks sit
//  outside it, the value run is an accent arc, and an indicator line on
//  the body points at the value; the value is written under the dial.
//  As the slider's, the layout is a function of the frame alone and
//  scales with its short side, and the same layout answers the window's
//  hit tests (the dial operates, the rest of the frame arranges).
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static partial class ControlRenderer
{
    /// <summary>Where a knob's parts sit in a frame.</summary>
    /// <param name="U">The scale unit: the frame against a Medium knob's.</param>
    /// <param name="Label">The label strip — part of the frame.</param>
    /// <param name="Radius">The dial's radius, to the value arc; the ticks sit outside it.</param>
    /// <param name="TickRoom">How far past <paramref name="Radius"/> the ticks reach — the face's outer edge.</param>
    /// <param name="ValueBaseline">The value's baseline, under the dial.</param>
    public sealed record KnobLayoutInfo(float U, SKRect Label, SKPoint Center, float Radius, float TickRoom, float ValueBaseline);

    /// <summary>The angle (degrees, clockwise from +x as Skia measures) of fraction <paramref name="f"/> of the sweep.</summary>
    public static float KnobAngle(double f) => (float)(135 + KnobControl.SweepDegrees * Math.Clamp(f, 0, 1));

    public static KnobLayoutInfo KnobLayout(KnobControl c, float w, float h)
    {
        float u = Math.Max(0.2f, Math.Min(w / 120f, h / 140f));
        float pad = 7 * u;
        float labelH = c.ShowLabel ? 16 * u : 0;
        var label = new SKRect(pad, pad * 0.8f, w - pad, pad * 0.8f + labelH);
        float valueH = c.ShowValue ? 17 * u : 0;
        float endsH = c.ShowEnds ? 6 * u : 0;
        float tickRoom = c.ShowTicks ? 8 * u : 3 * u;
        float top = labelH > 0 ? label.Bottom + 3 * u : pad;
        float bottom = h - pad - valueH - endsH;
        // The sweep is open at the bottom, so the dial's lowest drawn point is ~0.71 of its outer radius below the
        // centre: the dial is sized to the height that leaves, and to the width.
        float outerFromH = (bottom - top) / 1.71f;
        float outerFromW = (w - 2 * pad) / 2f;
        float outer = Math.Max(6 * u, Math.Min(outerFromH, outerFromW));
        float r = Math.Max(4 * u, outer - tickRoom);
        var center = new SKPoint(w / 2, top + outer + Math.Max(0, (bottom - top - 1.71f * outer) / 2));
        float valueBaseline = h - pad - 3 * u;
        return new KnobLayoutInfo(u, label, center, r, tickRoom, valueBaseline);
    }

    /// <summary>The point at fraction <paramref name="f"/> of the sweep on a circle of radius <paramref name="r"/>.</summary>
    private static SKPoint KnobPoint(SKPoint center, float r, double f)
    {
        double a = KnobAngle(f) * Math.PI / 180;
        return new SKPoint(center.X + r * (float)Math.Cos(a), center.Y + r * (float)Math.Sin(a));
    }

    private static bool KnobFaceContains(KnobControl c, float w, float h, SKPoint p)
    {
        var l = KnobLayout(c, w, h);
        float dx = p.X - l.Center.X, dy = p.Y - l.Center.Y;
        float reach = l.Radius + l.TickRoom + 2 * l.U;
        return dx * dx + dy * dy <= reach * reach;
    }

    /// <summary>The stops a knob draws a tick at: every stop when there are few, else eleven.</summary>
    private static int[] KnobTicks(int n)
    {
        if (n < 2) return [];
        int count = n <= 41 ? n : 11;
        var ticks = new int[count];
        for (int k = 0; k < count; k++)
            ticks[k] = count == n ? k : (int)Math.Round((double)k * (n - 1) / (count - 1));
        return ticks;
    }

    /// <summary>
    /// The stop whose tick is under <paramref name="p"/> on a BOUND knob that shows its ticks — a click there sets that
    /// stop (R-ddl6-1) — or null. A range knob's ticks are only marks: it is turned.
    /// </summary>
    public static int? KnobTickAt(KnobControl c, float w, float h, SKPoint p)
    {
        if (c.Domain != SliderDomain.Bound || !c.ShowTicks) return null;
        var l = KnobLayout(c, w, h);
        float dx = p.X - l.Center.X, dy = p.Y - l.Center.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < l.Radius - 1 * l.U || d > l.Radius + l.TickRoom + 3 * l.U) return null;
        int n = c.Stops.Count;
        int? best = null; float bestD = float.MaxValue;
        foreach (int i in KnobTicks(n))
        {
            var tp = KnobPoint(l.Center, l.Radius + l.TickRoom * 0.5f, (double)i / (n - 1));
            float td = (tp.X - p.X) * (tp.X - p.X) + (tp.Y - p.Y) * (tp.Y - p.Y);
            if (td < bestD) { bestD = td; best = i; }
        }
        // Within a few pixels of the tick, so a press between two ticks turns the knob instead.
        float slack = Math.Max(5 * l.U, 4);
        return best is not null && bestD <= slack * slack ? best : null;
    }

    private static void DrawKnob(SKCanvas canvas, KnobControl c, RenderTheme theme, float w, float h)
    {
        var l = KnobLayout(c, w, h);
        float u = l.U;
        var accent = AccentOf(c);
        DrawFrame(canvas, theme, w, h, u);

        using var font     = new SKFont(SkiaFonts.PlexRegular, 12 * u);
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, 12 * u);
        using var small    = new SKFont(SkiaFonts.PlexRegular, 9.5f * u);
        using var smallFb  = new SKFont(SkiaFonts.DejaVuRegular, 9.5f * u);
        using var text     = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        using var dim      = new SKPaint { Color = theme.TextColor.WithAlpha(170), IsAntialias = true };

        var stops = c.Stops;
        int n = stops.Count, at = c.StopIndex;
        double f = n > 1 ? (double)at / (n - 1) : 0;
        string unit = c.DisplayUnit;

        if (c.ShowLabel)
        {
            string cap = Fit(c.Caption, l.Label.Width, font, fallback);
            float cw = RendererText.MeasureTextWithFallback(cap, font, fallback);
            RendererText.DrawLeftTextWithFallback(canvas, cap, (w - cw) / 2, l.Label.Top + 16 * u * 0.78f, font, fallback, dim);
        }

        // The value arc: the whole sweep, then the run from the first stop to the value in the accent.
        var arcRect = new SKRect(l.Center.X - l.Radius, l.Center.Y - l.Radius, l.Center.X + l.Radius, l.Center.Y + l.Radius);
        using (var arc = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f * u, StrokeCap = SKStrokeCap.Round,
                                       IsAntialias = true,
                                       Color = theme.DarkMode ? theme.TextColor.WithAlpha(85) : theme.GridColor.WithAlpha(150) })
        {
            using (var track = new SKPath()) { track.AddArc(arcRect, 135, (float)KnobControl.SweepDegrees); canvas.DrawPath(track, arc); }
            if (f > 0)
            {
                arc.Color = accent;
                using var run = new SKPath();
                run.AddArc(arcRect, 135, (float)(KnobControl.SweepDegrees * f));
                canvas.DrawPath(run, arc);
            }
        }

        // Ticks outside the arc: every stop when there are few, else eleven, the ends longer.
        if (c.ShowTicks && n > 1)
        {
            using var tp = new SKPaint { Color = theme.TickColor.WithAlpha(140), StrokeWidth = 1f * u, IsAntialias = true };
            var ticks = KnobTicks(n);
            for (int k = 0; k < ticks.Length; k++)
            {
                double tf = (double)ticks[k] / (n - 1);
                float len = (k == 0 || k == ticks.Length - 1 ? 5.5f : 3.5f) * u;
                canvas.DrawLine(KnobPoint(l.Center, l.Radius + 3 * u, tf), KnobPoint(l.Center, l.Radius + 3 * u + len, tf), tp);
            }
        }

        // The body: a disc a shade off the card, with a hairline edge, and the indicator line pointing at the value.
        float body = l.Radius * 0.72f;
        var bodyTop = theme.DarkMode ? Lerp(theme.BackgroundColor, SKColors.White, 0.16f) : Lerp(theme.BackgroundColor, SKColors.Black, 0.02f);
        var bodyBot = theme.DarkMode ? Lerp(theme.BackgroundColor, SKColors.White, 0.06f) : Lerp(theme.BackgroundColor, SKColors.Black, 0.10f);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(l.Center.X, l.Center.Y - body),
                                                          new SKPoint(l.Center.X, l.Center.Y + body),
                                                          [bodyTop, bodyBot], SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader, IsAntialias = true })
            canvas.DrawCircle(l.Center, body, fill);
        using (var edge = new SKPaint { Color = theme.BorderColor.WithAlpha(120), StrokeWidth = 0.9f * u, IsAntialias = true,
                                        Style = SKPaintStyle.Stroke })
            canvas.DrawCircle(l.Center, body, edge);
        using (var needle = new SKPaint { Color = accent, StrokeWidth = 3f * u, StrokeCap = SKStrokeCap.Round, IsAntialias = true })
            canvas.DrawLine(KnobPoint(l.Center, body * 0.28f, f), KnobPoint(l.Center, body * 0.86f, f), needle);

        // The end labels at the two ends of the sweep, and the value under the dial between them.
        if (c.ShowEnds && n > 1)
        {
            string lo = Format(c, stops[0], unit), hi = Format(c, stops[n - 1], unit);
            var pl = KnobPoint(l.Center, l.Radius + l.TickRoom, 0);
            var ph = KnobPoint(l.Center, l.Radius + l.TickRoom, 1);
            float y = pl.Y + 9.5f * u;
            float hiW = RendererText.MeasureTextWithFallback(hi, small, smallFb);
            float loW = RendererText.MeasureTextWithFallback(lo, small, smallFb);
            RendererText.DrawLeftTextWithFallback(canvas, lo, Math.Max(4 * u, pl.X - loW / 2), y, small, smallFb, dim);
            RendererText.DrawLeftTextWithFallback(canvas, hi, Math.Min(w - 4 * u - hiW, ph.X - hiW / 2), y, small, smallFb, dim);
        }
        if (c.ShowValue)
        {
            string v = Fit(Format(c, stops[at], unit), w - 8 * u, font, fallback);
            float vw = RendererText.MeasureTextWithFallback(v, font, fallback);
            RendererText.DrawLeftTextWithFallback(canvas, v, (w - vw) / 2, l.ValueBaseline, font, fallback, text);
        }
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t)
        => new((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
               (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
}
