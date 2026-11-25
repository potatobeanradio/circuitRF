// ================================================================
//  ControlRenderer.Switch.cs  —  the button drawn as a switch
//
//  A capsule track with a white thumb. Off, the thumb sits at the start
//  (left, or the bottom of a vertical switch) on a grey track; on, it has
//  slid to the end and the track has filled with the button's colour. The
//  window animates between the two (ControlMotion): the thumb slides and
//  the colour fills as it goes, and while a press is held the thumb
//  stretches toward the side it is about to go to. An export, a copy and
//  `circuitrf render` draw the switch as it rests.
//
//  As the lamp's glow: the thumb's shadow is graded discs, never a blur.
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

/// <summary>
/// Where an animated control is between its two states, for the window's own drawing: <see cref="On"/> runs from 0 (off)
/// to 1 (on) while it changes, and <see cref="Press"/> from 0 to 1 while a press is held. Null draws the control at rest.
/// </summary>
public readonly record struct ControlMotion(double On, double Press);

public static partial class ControlRenderer
{
    /// <summary>Where a switch's parts sit: the track, and the caption under it.</summary>
    /// <param name="Vertical">The thumb travels bottom (off) to top (on).</param>
    public sealed record SwitchLayoutInfo(float U, bool Vertical, SKRect Track, SKRect Label, bool ShowsLabel);

    /// <summary>A switch track's length over its breadth.</summary>
    private const float SwitchAspect = 1.64f;

    public static SwitchLayoutInfo SwitchLayout(ButtonControl c, float w, float h)
    {
        bool vertical = c.Orientation == ControlOrientation.Vertical;
        string caption = c.ShownCaption;
        float baseW = vertical ? 62f : 92f, baseH = vertical ? 92f : 50f;
        float labelBase = caption.Length > 0 ? 20f : 0f;
        float u = Math.Max(0.1f, Math.Min(w / baseW, h / (baseH + labelBase)));
        bool shows = caption.Length > 0 && 12 * u >= MinButtonCaption;
        if (!shows) u = Math.Max(0.1f, Math.Min(w / baseW, h / baseH));
        float pad = Math.Min(7 * u, Math.Min(w, h) * 0.12f);
        float labelH = shows ? 17 * u : 0;
        var label = new SKRect(pad * 0.5f, h - pad * 0.6f - labelH, w - pad * 0.5f, h - pad * 0.6f);
        var region = new SKRect(pad, pad, w - pad, h - pad - labelH - (shows ? 2 * u : 0));
        float len, br;
        if (!vertical)
        {
            len = Math.Min(region.Width, region.Height * SwitchAspect);
            br = len / SwitchAspect;
            var track = SKRect.Create(region.MidX - len / 2, region.MidY - br / 2, len, br);
            return new(u, false, track, label, shows);
        }
        len = Math.Min(region.Height, region.Width * SwitchAspect);
        br = len / SwitchAspect;
        return new(u, true, SKRect.Create(region.MidX - br / 2, region.MidY - len / 2, br, len), label, shows);
    }

    private static bool SwitchFaceContains(ButtonControl c, float w, float h, SKPoint p)
    {
        var t = SwitchLayout(c, w, h).Track;
        t.Inflate(Math.Min(t.Width, t.Height) * 0.15f, Math.Min(t.Width, t.Height) * 0.15f);
        return t.Contains(p);
    }

    private static void DrawSwitch(SKCanvas canvas, ButtonControl c, RenderTheme theme, float w, float h, ControlMotion? motion)
    {
        var l = SwitchLayout(c, w, h);
        float u = l.U;
        DrawFrame(canvas, c, theme, w, h, u);

        float on = (float)Math.Clamp(motion?.On ?? (c.On ? 1 : 0), 0, 1);
        float press = (float)Math.Clamp(motion?.Press ?? 0, 0, 1);
        var track = l.Track;
        float br = l.Vertical ? track.Width : track.Height;   // the track's breadth: its corners are half of it
        var offColor = theme.DarkMode ? new SKColor(57, 57, 61) : new SKColor(223, 223, 228);
        var accent = AccentOf(c);

        using (var fill = new SKPaint { Color = Lerp(offColor, accent, on), IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(track, br / 2), fill);
        // A hairline at rest, off, so a pale track still reads on a pale card; gone as the colour fills it.
        if (on < 1)
            using (var edge = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(18 * (1 - on))), StrokeWidth = Math.Max(0.6f, 0.6f * u),
                                           IsAntialias = true, Style = SKPaintStyle.Stroke })
                canvas.DrawRoundRect(new SKRoundRect(track, br / 2), edge);

        // The thumb: a white disc inset from the track's edge, stretched along the track while a press is held — toward
        // the side it will go to, as a finger pushing it would.
        float inset = br * 0.075f;
        float d = br - 2 * inset;
        float stretch = d * 0.24f * press;
        float travel = (l.Vertical ? track.Height : track.Width) - 2 * inset - d;
        // Along the track from its start (the left, or the bottom): where the disc's leading edge is.
        float start = inset + travel * on;
        float a = start - stretch * on, b = start + d + stretch * (1 - on);
        SKRect thumb = l.Vertical
            ? new SKRect(track.Left + inset, track.Bottom - b, track.Right - inset, track.Bottom - a)
            : new SKRect(track.Left + a, track.Top + inset, track.Left + b, track.Bottom - inset);

        // Its shadow: a soft falloff a little below it, then a tight contact line.
        var sc = new SKPoint(thumb.MidX, thumb.MidY + d * 0.09f);
        float sr = d / 2;
        if (thumb.Width > thumb.Height + 0.5f || thumb.Height > thumb.Width + 0.5f)
        {
            // Stretched: the shadow is the capsule's own, drawn as two discs at its ends and the band between.
            using var sp = new SKPaint { Color = SKColors.Black.WithAlpha(22), IsAntialias = true };
            var sh = thumb; sh.Offset(0, d * 0.09f); sh.Inflate(d * 0.06f, d * 0.06f);
            canvas.DrawRoundRect(new SKRoundRect(sh, Math.Min(sh.Width, sh.Height) / 2), sp);
        }
        else
            GradedDisc(canvas, sc, sr * 1.3f, 0f, 1f,
                       t => SKColors.Black.WithAlpha((byte)(t < 0.72f ? 34 : 34 * (1 - (t - 0.72f) / 0.28f))));
        using (var tp = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(thumb, Math.Min(thumb.Width, thumb.Height) / 2), tp);
        using (var te = new SKPaint { Color = SKColors.Black.WithAlpha(16), StrokeWidth = Math.Max(0.5f, 0.5f * u), IsAntialias = true,
                                      Style = SKPaintStyle.Stroke })
            canvas.DrawRoundRect(new SKRoundRect(thumb, Math.Min(thumb.Width, thumb.Height) / 2), te);

        if (!l.ShowsLabel) return;
        using var font = new SKFont(SkiaFonts.PlexRegular, 12 * u);
        using var fb   = new SKFont(SkiaFonts.DejaVuRegular, 12 * u);
        using var dim  = new SKPaint { Color = theme.TextColor.WithAlpha(200), IsAntialias = true };
        string text = Fit(c.ShownCaption, l.Label.Width, font, fb);
        float tw = RendererText.MeasureTextWithFallback(text, font, fb);
        RendererText.DrawLeftTextWithFallback(canvas, text, l.Label.MidX - tw / 2, l.Label.Top + 17 * u * 0.72f, font, fb, dim);
    }
}
