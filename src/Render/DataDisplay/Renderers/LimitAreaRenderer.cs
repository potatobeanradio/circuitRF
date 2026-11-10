// ================================================================
//  LimitAreaRenderer.cs  —  a Rect plot's hatched limit areas
//
//  The side of a limit a curve must stay out of, hatched in the theme's
//  limit colour with the limit itself drawn as a solid edge. Under the
//  curves, so a curve crossing into an area reads as crossing it.
// ================================================================

using System;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public static class LimitAreaRenderer
    {
        public static void Draw(SKCanvas canvas, (double W, double H) canvasSize, Plot plot, TransformSet tf,
                                RenderTheme theme)
        {
            if (plot.LimitAreas.Count == 0) return;

            var   clip = PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize);
            float lw   = (float)(Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            var   c    = theme.LimitColor;

            using var fill  = new SKPaint { Color = c.WithAlpha(22), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var hatch = new SKPaint
            {
                Color = c.WithAlpha(115), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.75f, lw * 0.45f),
                IsAntialias = true,
            };
            using var edge = new SKPaint
            {
                Color = c, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, lw * 0.8f), IsAntialias = true,
            };

            foreach (var area in plot.LimitAreas)
            {
                if (!double.IsFinite(area.Limit)) continue;
                bool sec = area.OnSecondaryAxis && plot.Axes.ShowSecondary;
                var  win = sec ? plot.Axes.WindowSecondary : plot.Axes.Window;

                double x0 = double.IsFinite(area.XStart) ? area.XStart : plot.Axes.Window.Left;
                double x1 = double.IsFinite(area.XEnd)   ? area.XEnd   : plot.Axes.Window.Right;
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (!tf.XIsPlottable(x0)) x0 = plot.Axes.Window.Left;
                if (!tf.XIsPlottable(x0) || !tf.XIsPlottable(x1)) continue;

                // The window's Top is its minimum and its Bottom its maximum (world Y).
                double yFar = area.Side == LimitAreaSide.Above
                    ? Math.Max(win.Bottom, area.Limit)
                    : Math.Min(win.Top, area.Limit);

                var a = tf.ToCanvas(x0, area.Limit, sec);
                var b = tf.ToCanvas(x1, yFar, sec);
                var rect = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                // A limit beyond the window on the hatched side leaves the whole span hatched, so the far
                // edge is pushed past the clip rather than to the limit.
                if (area.Side == LimitAreaSide.Above) rect.Top    = Math.Min(rect.Top, clip.Top);
                else                                  rect.Bottom = Math.Max(rect.Bottom, clip.Bottom);
                rect.Intersect(clip);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                canvas.Save();
                canvas.ClipRect(rect);
                canvas.DrawRect(rect, fill);
                float step = Math.Max(4f, lw * 3.5f);
                for (float s = rect.Left - rect.Height; s < rect.Right; s += step)
                    canvas.DrawLine(s, rect.Bottom, s + rect.Height, rect.Top, hatch);
                canvas.Restore();

                float ly = a.Y;
                if (ly >= clip.Top && ly <= clip.Bottom)
                    canvas.DrawLine(rect.Left, ly, rect.Right, ly, edge);
            }
        }
    }
}
