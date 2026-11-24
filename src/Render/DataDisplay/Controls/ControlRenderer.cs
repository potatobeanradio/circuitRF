// ================================================================
//  ControlRenderer.cs  —  every display control, drawn by Skia
//  (brief-ddl-5 R-ddl5-2, R-ddl5-4, R-ddl5-6; overview D7)
//
//  One renderer for the canvas, Copy/Export and `circuitrf render`, so a
//  control looks the same everywhere and exports as vector paths. Nothing
//  here uses a blur mask filter or an image filter: both rasterise or
//  vanish in Skia's SVG and PDF output.
//
//  Layout is a function of the frame alone, and everything scales with
//  the frame's SHORT side: a Large slider is a bigger slider, not a Small
//  one with more empty space. The same layout answers the window's two
//  questions — is this point on the face (operate) or the frame (move),
//  and which stop is under it.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static class ControlRenderer
{
    /// <summary>The accent a control takes when it names none: the display's own selection colour.</summary>
    public static SKColor DefaultAccent => RenderTheme.SelectionColorFallback;

    /// <summary>A control's accent: its own <c>#rrggbb</c>, else <see cref="DefaultAccent"/>.</summary>
    public static SKColor AccentOf(DisplayControl c)
        => c.Accent is { Length: > 0 } a && SKColor.TryParse(a, out var col) ? col : DefaultAccent;

    /// <summary>Where a slider's parts sit in a frame of <paramref name="w"/> × <paramref name="h"/>.</summary>
    /// <param name="U">The scale unit: the frame's short side over a Medium slider's.</param>
    /// <param name="Label">The label strip — part of the frame.</param>
    /// <param name="TrackStart">The first stop's centre.</param>
    /// <param name="TrackEnd">The last stop's centre.</param>
    /// <param name="Face">The track and thumb: what operates.</param>
    public sealed record SliderLayout(float U, bool Vertical, SKRect Label, SKPoint TrackStart, SKPoint TrackEnd,
                                      float ThumbRadius, SKRect Face);

    public static SliderLayout Layout(SliderControl c, float w, float h)
    {
        bool vertical = c.Orientation == ControlOrientation.Vertical;
        // The short side sets the scale; the long side caps it, so a frame made tall for its width (or wide for its
        // height, vertically) does not grow text the track has no room for.
        float shortU = vertical ? w / 85f : h / 68f;
        float longU  = vertical ? h / 170f : w / 170f;
        float u = Math.Max(0.2f, Math.Min(shortU, longU));
        float pad = 7 * u, labelH = 16 * u, r = 7 * u;
        var label = new SKRect(pad, pad * 0.8f, w - pad, pad * 0.8f + labelH);
        if (!vertical)
        {
            // The track sits under the label with room for the ticks and the end labels below it.
            float below = (c.ShowTicks ? 6 * u : 0) + (c.ShowEnds ? 13 * u : 0);
            float top = label.Bottom + r + 2 * u;
            float y = Math.Min(top + Math.Max(0, (h - pad - below - top - r) * 0.5f), h - pad - below - r);
            var a = new SKPoint(pad + r, y);
            var b = new SKPoint(w - pad - r, y);
            // The face reaches down over the tick row: the ticks read as part of the track, and a drag begun on them
            // slides the value rather than moving the panel.
            float faceBelow = Math.Max(r + 3 * u, c.ShowTicks ? r + 1.5f * u + 6 * u : 0);
            return new(u, false, label, a, b, r, new SKRect(a.X - r - 3 * u, y - r - 3 * u, b.X + r + 3 * u, y + faceBelow));
        }
        else
        {
            // Vertical: the label on top, the value under it, the track down the left with ticks and ends beside it.
            float valueH = c.ShowValue ? 14 * u : 0;
            float x = pad + r + 2 * u;
            var b = new SKPoint(x, label.Bottom + valueH + r + 4 * u);   // the LAST stop is at the top
            var a = new SKPoint(x, h - pad - r);
            float faceRight = Math.Max(r + 3 * u, c.ShowTicks ? r + 1.5f * u + 6 * u : 0);
            return new(u, true, label, a, b, r, new SKRect(x - r - 3 * u, b.Y - r - 3 * u, x + faceRight, a.Y + r + 3 * u));
        }
    }

    /// <summary>The centre of stop <paramref name="i"/> of <paramref name="n"/>.</summary>
    public static SKPoint StopPoint(SliderLayout l, int i, int n)
    {
        float f = n > 1 ? (float)i / (n - 1) : 0f;
        return new SKPoint(l.TrackStart.X + f * (l.TrackEnd.X - l.TrackStart.X),
                           l.TrackStart.Y + f * (l.TrackEnd.Y - l.TrackStart.Y));
    }

    /// <summary>True when <paramref name="p"/> (frame coordinates) is on the face — the track or the thumb.</summary>
    public static bool HitFace(DisplayControl c, float w, float h, SKPoint p) => c switch
    {
        SliderControl s => Layout(s, w, h).Face.Contains(p),
        _ => false,
    };

    /// <summary>The stop nearest <paramref name="p"/>'s projection onto the track.</summary>
    public static int StopAt(SliderControl c, float w, float h, SKPoint p)
    {
        var l = Layout(c, w, h);
        int n = c.Stops.Count;
        float dx = l.TrackEnd.X - l.TrackStart.X, dy = l.TrackEnd.Y - l.TrackStart.Y;
        float len2 = dx * dx + dy * dy;
        float f = len2 > 0 ? ((p.X - l.TrackStart.X) * dx + (p.Y - l.TrackStart.Y) * dy) / len2 : 0;
        f = Math.Clamp(f, 0f, 1f);
        return (int)Math.Round(f * (n - 1));
    }

    /// <summary>Draws <paramref name="c"/> into <paramref name="bounds"/> on <paramref name="canvas"/>.</summary>
    public static void Draw(SKCanvas canvas, DisplayControl c, RenderTheme theme, SKRect bounds)
    {
        canvas.Save();
        canvas.Translate(bounds.Left, bounds.Top);
        switch (c)
        {
            case SliderControl s: DrawSlider(canvas, s, theme, bounds.Width, bounds.Height); break;
        }
        canvas.Restore();
    }

    private static void DrawSlider(SKCanvas canvas, SliderControl c, RenderTheme theme, float w, float h)
    {
        var l = Layout(c, w, h);
        float u = l.U;
        var accent = AccentOf(c);

        DrawFrame(canvas, theme, w, h, u);

        using var font     = new SKFont(SkiaFonts.PlexRegular, 12 * u);
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, 12 * u);
        using var small    = new SKFont(SkiaFonts.PlexRegular, 10 * u);
        using var smallFb  = new SKFont(SkiaFonts.DejaVuRegular, 10 * u);
        using var text     = new SKPaint { Color = theme.TextColor, IsAntialias = true };
        using var dim      = new SKPaint { Color = theme.TextColor.WithAlpha(170), IsAntialias = true };

        var stops = c.Stops;
        int n = stops.Count, at = c.StopIndex;
        string unit = c.DisplayUnit;
        string valueText = Format(c, stops[at], unit);

        // The label strip: the caption on the left, the value on the right (or under it, vertically).
        float baseline = l.Label.Top + l.Label.Height * 0.78f;
        float valueW = c.ShowValue ? RendererText.MeasureTextWithFallback(valueText, font, fallback) : 0;
        float captionRoom = l.Vertical || !c.ShowValue ? l.Label.Width : l.Label.Width - valueW - 8 * u;
        RendererText.DrawLeftTextWithFallback(canvas, Fit(c.Caption, captionRoom, font, fallback), l.Label.Left, baseline,
                                              font, fallback, dim);
        if (c.ShowValue)
        {
            if (!l.Vertical)
                RendererText.DrawLeftTextWithFallback(canvas, valueText, l.Label.Right - valueW, baseline, font, fallback, text);
            else
                RendererText.DrawLeftTextWithFallback(canvas, Fit(valueText, l.Label.Width, font, fallback), l.Label.Left,
                                                      baseline + 14 * u, font, fallback, text);
        }

        // The track: the whole of it, then the run from the first stop to the thumb in the accent.
        var thumb = StopPoint(l, at, n);
        using (var track = new SKPaint { Color = theme.DarkMode ? theme.TextColor.WithAlpha(85) : theme.GridColor.WithAlpha(150),
                                         StrokeWidth = 3.5f * u,
                                         StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke })
        {
            canvas.DrawLine(l.TrackStart, l.TrackEnd, track);
            track.Color = accent;
            canvas.DrawLine(l.TrackStart, thumb, track);
        }

        // Ticks: every stop when there are few, else eleven, so a 1001-stop range is not a solid bar.
        var perp = l.Vertical ? new SKPoint(1, 0) : new SKPoint(0, 1);
        float tick0 = l.ThumbRadius + 1.5f * u;
        if (c.ShowTicks && n > 1)
        {
            using var tp = new SKPaint { Color = theme.TickColor.WithAlpha(140), StrokeWidth = 1f * u, IsAntialias = true };
            int count = n <= 41 ? n : 11;
            for (int k = 0; k < count; k++)
            {
                int i = count == n ? k : (int)Math.Round((double)k * (n - 1) / (count - 1));
                var p = StopPoint(l, i, n);
                float len = (k == 0 || k == count - 1 ? 5 : 3.5f) * u;
                canvas.DrawLine(p.X + perp.X * tick0, p.Y + perp.Y * tick0,
                                p.X + perp.X * (tick0 + len), p.Y + perp.Y * (tick0 + len), tp);
            }
        }

        // The end labels: the first and last stops.
        if (EndLabels(c, w, h) is { } ends)
            foreach (var e in new[] { ends.Lo, ends.Hi })
                RendererText.DrawLeftTextWithFallback(canvas, e.Text, e.Rect.Left, e.Baseline, small, smallFb, dim);

        // The thumb: an accent disc with a ring of the background, and a hairline outside it so it reads on any fill.
        using (var fill = new SKPaint { Color = accent, IsAntialias = true })
            canvas.DrawCircle(thumb, l.ThumbRadius, fill);
        using (var ring = new SKPaint { Color = theme.BackgroundColor, StrokeWidth = 2f * u, IsAntialias = true,
                                        Style = SKPaintStyle.Stroke })
            canvas.DrawCircle(thumb, l.ThumbRadius - 1f * u, ring);
        using (var edge = new SKPaint { Color = theme.BorderColor.WithAlpha(90), StrokeWidth = 0.8f * u, IsAntialias = true,
                                        Style = SKPaintStyle.Stroke })
            canvas.DrawCircle(thumb, l.ThumbRadius + 0.4f * u, edge);
    }

    /// <summary>One end label: its text, the box it occupies (frame coordinates) and the baseline it is drawn on.</summary>
    public sealed record EndLabel(string Text, SKRect Rect, float Baseline, float FontSize);

    /// <summary>
    /// Where a slider's first and last stops are written, or null when it shows no end labels — what the end labels
    /// are drawn at, and what a double-click to edit them hits (the editor sits exactly on the text).
    /// </summary>
    public static (EndLabel Lo, EndLabel Hi)? EndLabels(SliderControl c, float w, float h)
    {
        var stops = c.Stops;
        int n = stops.Count;
        if (!c.ShowEnds || n < 2) return null;
        var l = Layout(c, w, h);
        float u = l.U, size = 10 * u;
        using var small   = new SKFont(SkiaFonts.PlexRegular, size);
        using var smallFb = new SKFont(SkiaFonts.DejaVuRegular, size);
        string unit = c.DisplayUnit;
        string lo = Format(c, stops[0], unit), hi = Format(c, stops[n - 1], unit);
        float loW = RendererText.MeasureTextWithFallback(lo, small, smallFb);
        float hiW = RendererText.MeasureTextWithFallback(hi, small, smallFb);
        // Clear of the long end tick (it reaches 5u past the tick start): a label touching it reads as a minus sign.
        float off = l.ThumbRadius + 1.5f * u + (c.ShowTicks ? (l.Vertical ? 9 * u : 6 * u) : 1 * u);
        EndLabel At(string text, float x, float baseline, float width)
            => new(text, new SKRect(x, baseline - size * 0.95f, x + width, baseline + size * 0.3f), baseline, size);
        if (!l.Vertical)
        {
            float y = l.TrackStart.Y + off + size * 0.85f;
            return (At(lo, l.TrackStart.X - l.ThumbRadius, y, loW), At(hi, l.TrackEnd.X + l.ThumbRadius - hiW, y, hiW));
        }
        float x = l.TrackStart.X + off;
        return (At(lo, x, l.TrackStart.Y + size * 0.35f, loW), At(hi, x, l.TrackEnd.Y + size * 0.35f, hiW));
    }

    /// <summary>The frame: the display's card (<see cref="CardStyle"/>), which marker info boxes and carded plots share.</summary>
    private static void DrawFrame(SKCanvas canvas, RenderTheme theme, float w, float h, float u)
        => CardStyle.Draw(canvas, theme, new SKRect(0, 0, w, h), 5 * u);

    /// <summary>The gripper's corner triangle: points whose distance in from the bottom-right corner, along both edges
    /// together, is under this many pixels resize. A triangle rather than a square, so it stays off the end label.</summary>
    public const float GripReach = 13f;

    /// <summary>True when <paramref name="p"/> is in the gripper's corner triangle of a <paramref name="w"/> ×
    /// <paramref name="h"/> frame.</summary>
    public static bool OnGrip(float w, float h, SKPoint p)
        => p.X <= w && p.Y <= h && (w - p.X) + (h - p.Y) <= GripReach;

    /// <summary>
    /// The window's chrome over a control — the selection outline, the gripper, the thumb's hover/pressed halo and, on a
    /// locked selected control, a lock badge (R-ddl5-9). Never exported: <see cref="Draw"/> does not call it.
    /// </summary>
    public static void DrawChrome(SKCanvas canvas, DisplayControl control, float w, float h, bool selected, bool locked,
                                  bool grip, bool thumbActive, SKColor selection)
    {
        if (thumbActive && control is SliderControl s)
        {
            // The thumb answers the pointer: a ring of its accent around it while hovered or held. A plain stroke,
            // not a blur — the canvas and an export stay the same kind of drawing.
            var l = Layout(s, w, h);
            var at = StopPoint(l, s.StopIndex, s.Stops.Count);
            using var halo = new SKPaint { Color = AccentOf(s).WithAlpha(70), IsAntialias = true };
            canvas.DrawCircle(at, l.ThumbRadius + 4 * l.U, halo);
        }
        if (selected)
            CardStyle.DrawSelection(canvas, new SKRect(0, 0, w, h), 5, selection, 2f);
        if (grip && !locked)
        {
            using var gp = new SKPaint { Color = selection, StrokeWidth = 1.2f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            for (int k = 1; k <= 3; k++)
                canvas.DrawLine(w - 2.5f - 3f * k, h - 2.5f, w - 2.5f, h - 2.5f - 3f * k, gp);
        }
        if (selected && locked)
        {
            // A padlock badge centred ON the frame's corner, half outside it: clear of every label, at any size.
            float cx = w - 1, cy = h - 1, r = 7.5f;
            using (var badge = new SKPaint { Color = selection.WithAlpha(255), IsAntialias = true })
                canvas.DrawCircle(cx, cy, r, badge);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(cx - 3.5f, cy - 1f, cx + 3.5f, cy + 4f), 1f), white);
            using var shackle = new SKPaint { Color = SKColors.White, StrokeWidth = 1.3f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            path.MoveTo(cx - 2f, cy - 1f);
            path.LineTo(cx - 2f, cy - 2.5f);
            path.ArcTo(new SKRect(cx - 2f, cy - 4.5f, cx + 2f, cy - 0.5f), 180, 180, false);
            path.LineTo(cx + 2f, cy - 1f);
            canvas.DrawPath(path, shackle);
        }
    }

    /// <summary>A stop as the slider shows it: engineering notation in its unit (<c>2.4 GHz</c>), or the bare number.</summary>
    public static string Format(SliderControl c, double v, string unit)
    {
        // A range reads in the unit it was written in — a MHz slider says "2400 MHz", never "2.4 GHz", so the number
        // the user typed is the number they see, and the end editor opens on the same text. A bound slider shows its
        // axis's samples in engineering notation, as every axis does.
        if (c.Domain == SliderDomain.Range)
        {
            string num = Significant(v);
            return unit.Length > 0 ? $"{num} {unit}" : num;
        }
        var (scale, baseUnit) = SiUnit.Split(unit);
        return LinkMatch.Format(v * scale, baseUnit);
    }

    /// <summary>Four significant digits, never in exponent form: 792.4466 → "792.4", 2400 → "2400", 0.01234 → "0.01234".</summary>
    public static string Significant(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return v == 0 ? "0" : "NaN";
        int decimals = Math.Clamp(3 - (int)Math.Floor(Math.Log10(Math.Abs(v))), 0, 6);
        return Math.Round(v, decimals).ToString("0." + new string('#', Math.Max(1, decimals)),
                                                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Fit(string s, float room, SKFont font, SKFont fallback)
    {
        if (room <= 0) return "";
        if (RendererText.MeasureTextWithFallback(s, font, fallback) <= room) return s;
        while (s.Length > 1 && RendererText.MeasureTextWithFallback(s + "…", font, fallback) > room) s = s[..^1];
        return s.TrimEnd() + "…";
    }
}
