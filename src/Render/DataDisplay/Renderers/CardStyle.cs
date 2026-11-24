// ================================================================
//  CardStyle.cs  —  the rounded panel the Data Display's floating
//  things sit on (brief-ddl-5)
//
//  One look, three users: a display control's frame, a marker info box,
//  and a plot drawn as a card (Plot.Card, off by default). Kept in one
//  place so the three never drift apart — a box a shade off the
//  background, a hairline border, rounded corners. Plain fills and
//  strokes only, so it stays vector in an SVG or PDF export.
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public static class CardStyle
    {
        /// <summary>The panel's fill: a shade off the theme's background, darker in light mode and lighter in dark.</summary>
        public static SKColor Fill(RenderTheme theme)
        {
            var bg = theme.BackgroundColor;
            return theme.DarkMode
                ? new SKColor((byte)Math.Min(255, bg.Red + 14), (byte)Math.Min(255, bg.Green + 14), (byte)Math.Min(255, bg.Blue + 14))
                : new SKColor((byte)(bg.Red * 0.965), (byte)(bg.Green * 0.965), (byte)(bg.Blue * 0.965));
        }

        /// <summary>The hairline border.</summary>
        public static SKColor Border(RenderTheme theme) => theme.GridColor.WithAlpha(170);

        /// <summary>
        /// Draws the card filling <paramref name="rect"/> with corners of <paramref name="radius"/>. With
        /// <paramref name="fill"/> false only the border is drawn — a marker info box the user asked to be transparent.
        /// </summary>
        public static void Draw(SKCanvas canvas, RenderTheme theme, SKRect rect, float radius, bool fill = true,
                                byte fillAlpha = 255)
        {
            var r = new SKRoundRect(new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f), radius);
            if (fill)
                using (var paint = new SKPaint { Color = Fill(theme).WithAlpha(fillAlpha), IsAntialias = true })
                    canvas.DrawRoundRect(r, paint);
            using var border = new SKPaint { Color = Border(theme), StrokeWidth = 1f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawRoundRect(r, border);
        }

        /// <summary>A selection outline following the card's corners.</summary>
        public static void DrawSelection(SKCanvas canvas, SKRect rect, float radius, SKColor color, float width)
        {
            var r = new SKRoundRect(new SKRect(rect.Left + width / 2, rect.Top + width / 2, rect.Right - width / 2, rect.Bottom - width / 2),
                                    Math.Max(0, radius - width / 2));
            using var paint = new SKPaint { Color = color, StrokeWidth = width, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawRoundRect(r, paint);
        }
    }
}
