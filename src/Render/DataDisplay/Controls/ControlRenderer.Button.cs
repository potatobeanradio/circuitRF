// ================================================================
//  ControlRenderer.Button.cs  —  the illuminated button and the readout
//  (brief-ddl-6 R-ddl6-2, R-ddl6-3; overview D7)
//
//  THE GLOW IS GRADED DISCS, NEVER A BLUR, AND NEVER A RADIAL SHADER. A
//  lit button's halo falls from its colour at partial alpha to nothing,
//  on a disc larger than the face; a second, tighter falloff is the
//  face's own bloom. Each is drawn as concentric filled circles whose
//  alphas compose to the falloff exactly (GradedDisc). A blur mask filter
//  would rasterise in SVG and PDF, or vanish; and Skia's SVG writer has
//  no radial gradient at all — it leaves an empty <defs/> and an
//  unfilled circle, which every SVG viewer paints BLACK. Plain circles
//  are vector in every export and look the same on the canvas.
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static partial class ControlRenderer
{
    /// <summary>Where a button's parts sit: the face disc, the room its glow takes, and the label under it.</summary>
    public sealed record ButtonLayoutInfo(float U, SKPoint Center, float Radius, float GlowRadius, SKRect Label);

    /// <summary>How far the glow reaches past the face, as a multiple of its radius.</summary>
    private const float GlowReach = 1.34f;

    public static ButtonLayoutInfo ButtonLayout(ButtonControl c, float w, float h)
    {
        bool under = c.LabelPlacement == ButtonLabelPlacement.Under;
        float u = Math.Max(0.2f, Math.Min(w / 96f, h / (under ? 112f : 96f)));
        float pad = 5 * u;
        float labelH = under ? 17 * u : 0;
        var label = new SKRect(pad, h - pad - labelH, w - pad, h - pad);
        var region = new SKRect(pad, pad, w - pad, h - pad - labelH);
        float r = Math.Max(4 * u, Math.Min(region.Width, region.Height) / 2 / GlowReach);
        return new ButtonLayoutInfo(u, new SKPoint(region.MidX, region.MidY), r, r * GlowReach, label);
    }

    private static bool ButtonFaceContains(ButtonControl c, float w, float h, SKPoint p)
    {
        var l = ButtonLayout(c, w, h);
        float dx = p.X - l.Center.X, dy = p.Y - l.Center.Y;
        return dx * dx + dy * dy <= l.Radius * l.Radius;
    }

    private static void DrawButton(SKCanvas canvas, ButtonControl c, RenderTheme theme, float w, float h)
    {
        var l = ButtonLayout(c, w, h);
        float u = l.U, r = l.Radius;
        var col = AccentOf(c);
        var ctr = l.Center;
        DrawFrame(canvas, theme, w, h, u);

        if (c.On)
        {
            // The halo: the colour at partial alpha fading to nothing, from the face's edge out to the glow's.
            float inner = r / l.GlowRadius;
            GradedDisc(canvas, ctr, l.GlowRadius, inner, 1f, t =>
            {
                float k = (t - inner) / (1 - inner);                       // 0 at the face's edge, 1 at the glow's
                float a = k < 0.45f ? 150 + (52 - 150) * (k / 0.45f) : 52 * (1 - (k - 0.45f) / 0.55f);
                return col.WithAlpha((byte)Math.Clamp(a, 0, 255));
            });

            // The face, lit: the full colour at mid-radius, deeper at the rim, opaque.
            GradedDisc(canvas, ctr, r, 0f, 1f, t => t < 0.55f ? col : Lerp(col, Lerp(col, SKColors.Black, 0.22f), (t - 0.55f) / 0.45f));

            // The bloom: a second, tighter falloff of near-white at the face's heart, a little above its centre.
            GradedDisc(canvas, new SKPoint(ctr.X, ctr.Y - 0.12f * r), 0.62f * r, 0f, 1f,
                       t => SKColors.White.WithAlpha((byte)(165 * (1 - t) * (1 - t))));

            using var rim = new SKPaint { Color = Lerp(col, SKColors.Black, 0.35f), StrokeWidth = 1f * u, IsAntialias = true,
                                          Style = SKPaintStyle.Stroke };
            canvas.DrawCircle(ctr, r, rim);
        }
        else
        {
            // Dark: the colour at low saturation, dim, with a little light from above so it still reads as a button.
            var grey = theme.DarkMode ? new SKColor(58, 58, 62) : new SKColor(226, 226, 230);
            var off  = Lerp(grey, col, theme.DarkMode ? 0.24f : 0.20f);
            using (var face = SKShader.CreateLinearGradient(new SKPoint(ctr.X, ctr.Y - r), new SKPoint(ctr.X, ctr.Y + r),
                       [Lerp(off, SKColors.White, theme.DarkMode ? 0.10f : 0.45f), Lerp(off, SKColors.Black, theme.DarkMode ? 0.18f : 0.06f)],
                       SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = face, IsAntialias = true })
                canvas.DrawCircle(ctr, r, paint);
            using var rim = new SKPaint { Color = theme.BorderColor.WithAlpha(140), StrokeWidth = 1f * u, IsAntialias = true,
                                          Style = SKPaintStyle.Stroke };
            canvas.DrawCircle(ctr, r, rim);
        }

        string caption = c.Caption;
        if (caption.Length == 0) return;
        if (c.LabelPlacement == ButtonLabelPlacement.Under)
        {
            using var font = new SKFont(SkiaFonts.PlexRegular, 12 * u);
            using var fb   = new SKFont(SkiaFonts.DejaVuRegular, 12 * u);
            using var dim  = new SKPaint { Color = theme.TextColor.WithAlpha(200), IsAntialias = true };
            string t = Fit(caption, l.Label.Width, font, fb);
            float tw = RendererText.MeasureTextWithFallback(t, font, fb);
            RendererText.DrawLeftTextWithFallback(canvas, t, l.Label.MidX - tw / 2, l.Label.Top + 17 * u * 0.72f, font, fb, dim);
        }
        else
        {
            // On the face: dark text on a light colour, light text on a dark one; on the dark face, the theme's.
            float size = Math.Min(13 * u, r * 0.42f);
            using var font = new SKFont(SkiaFonts.PlexBold, size);
            using var fb   = new SKFont(SkiaFonts.DejaVuBold, size);
            var ink = c.On ? (Luma(col) > 0.62 ? new SKColor(20, 20, 24) : SKColors.White) : theme.TextColor.WithAlpha(190);
            using var paint = new SKPaint { Color = ink, IsAntialias = true };
            string t = Fit(caption, 1.6f * r, font, fb);
            float tw = RendererText.MeasureTextWithFallback(t, font, fb);
            RendererText.DrawLeftTextWithFallback(canvas, t, ctr.X - tw / 2, ctr.Y + size * 0.35f, font, fb, paint);
        }
    }

    /// <summary>How many circles a graded disc is drawn with: fine enough that no step shows at any control size.</summary>
    private const int GradeSteps = 28;

    /// <summary>
    /// A radial falloff drawn as concentric circles, from <paramref name="radius"/>·<paramref name="tTo"/> in to
    /// <paramref name="radius"/>·<paramref name="tFrom"/>; <paramref name="at"/> gives the colour wanted at fraction t
    /// of the radius. An opaque colour is drawn as it is. A translucent one is drawn with the alpha that, composed over
    /// the circles already drawn outside it, gives exactly the alpha wanted there — the falloffs here are one colour each,
    /// so the composite is that colour at that alpha, with no seams between rings.
    /// </summary>
    private static void GradedDisc(SKCanvas canvas, SKPoint center, float radius, float tFrom, float tTo, Func<float, SKColor> at)
    {
        using var paint = new SKPaint { IsAntialias = true };
        double under = 0;   // the alpha composed so far, outside this ring
        for (int k = 0; k < GradeSteps; k++)
        {
            float tOuter = tTo - (tTo - tFrom) * k / GradeSteps;
            float tMid = tTo - (tTo - tFrom) * (k + 0.5f) / GradeSteps;
            var c = at(tMid);
            double want = c.Alpha / 255.0;
            if (c.Alpha == 255) { paint.Color = c; under = 1; }
            else
            {
                double a = under >= 1 ? 0 : Math.Clamp(1 - (1 - want) / (1 - under), 0, 1);
                if (want < under) a = 0;   // a falloff never darkens back towards its centre here
                paint.Color = c.WithAlpha((byte)Math.Round(a * 255));
                under = 1 - (1 - under) * (1 - paint.Color.Alpha / 255.0);
            }
            if (paint.Color.Alpha > 0) canvas.DrawCircle(center, radius * tOuter, paint);
        }
    }

    private static double Luma(SKColor c) => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0;

    // ---- the readout --------------------------------------------------------------------------------

    /// <summary>A readout's scale unit: the frame against a Medium readout's.</summary>
    private static float ReadoutU(float w, float h) => Math.Max(0.2f, Math.Min(w / 196f, h / 62f));

    private static void DrawReadout(SKCanvas canvas, ReadoutControl c, RenderTheme theme, float w, float h)
    {
        float u = ReadoutU(w, h);
        float pad = 7 * u;
        DrawFrame(canvas, theme, w, h, u);

        float top = pad;
        string? name = c.ShowName ? (string.IsNullOrWhiteSpace(c.Label) ? c.NameText ?? c.Name : c.Label) : null;
        if (name is { Length: > 0 })
        {
            using var nf  = new SKFont(SkiaFonts.PlexRegular, 10.5f * u);
            using var nfb = new SKFont(SkiaFonts.DejaVuRegular, 10.5f * u);
            using var dim = new SKPaint { Color = theme.TextColor.WithAlpha(160), IsAntialias = true };
            RendererText.DrawLeftTextWithFallback(canvas, Fit(name, w - 2 * pad, nf, nfb), pad, top + 10.5f * u * 0.8f, nf, nfb, dim);
            top += 13 * u;
        }

        // The digits fill what is left, right-aligned as an instrument's are, and shrink to fit the width.
        bool problem = c.Text is null;
        string text = c.Text ?? c.Problem ?? "—";
        float room = h - pad - top;
        float size = problem ? Math.Min(11 * u, room * 0.7f) : room * 0.78f;
        using var font = new SKFont(problem ? SkiaFonts.PlexRegular : SkiaFonts.PlexSemiBold, size);
        using var fb   = new SKFont(SkiaFonts.DejaVuRegular, size);
        float tw = RendererText.MeasureTextWithFallback(text, font, fb);
        if (tw > w - 2 * pad && tw > 0)
        {
            size *= (w - 2 * pad) / tw;
            font.Size = size; fb.Size = size;
            tw = RendererText.MeasureTextWithFallback(text, font, fb);
        }
        var ink = problem ? theme.TextColor.WithAlpha(140) : c.Accent is { Length: > 0 } ? AccentOf(c) : theme.TextColor;
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        float baseline = top + room / 2 + size * 0.36f;
        float x = problem ? pad : w - pad - tw;
        RendererText.DrawLeftTextWithFallback(canvas, text, x, baseline, font, fb, paint);
    }
}
