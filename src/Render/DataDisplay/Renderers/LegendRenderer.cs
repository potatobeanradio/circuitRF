// ================================================================
//  LegendRenderer.cs  —  a plot's legend (brief-dfr26-6 R-dfr26-6-2)
//
//  One entry per trace: a short sample of its line, in its colour and
//  line style, beside the name its Y axis gives it. Two placements in
//  the plot's own canvas: INSIDE, a box in the plot box's top-right
//  corner, and BELOW, a band under the plotting region laid out as a
//  grid of equal cells. A third, FLOATING, is the Inside box dragged
//  out onto the Data Display canvas; its host places and draws it.
//
//  The band's HEIGHT depends only on the trace count and the canvas,
//  never on the label text. It decides where the plot box ends, and
//  the box is also computed by every hit test through
//  PlotRenderer.BuildTransforms — which has no alias resolver and no
//  source-prefix setting to measure the text with. Text that does not
//  fit its cell is cut short with an ellipsis instead.
//
//  A dashed or dotted sample is emitted as SEGMENTS, not through
//  SKPathEffect, for StatisticsRenderer's reason: Skia's SVG device
//  drops a path effect on a stroke, and the three exports must agree.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    public static class LegendRenderer
    {
        /// <summary>The traces a legend lists — every one that names an axis: not a contour, and not a
        /// curve its plot's host derived (<see cref="Trace.ExcludeFromAxisLabels"/>).</summary>
        public static IEnumerable<Trace> Listed(Plot plot)
        {
            foreach (var t in plot.Traces)
                if (!t.IsContourTrace && !t.ExcludeFromAxisLabels) yield return t;
        }

        /// <summary>Whether <paramref name="plot"/> draws a legend INSIDE ITS OWN CANVAS: it asks for one docked there
        /// (Inside or Below), it is a kind that has a plot box, and something is listed. A
        /// <see cref="LegendPlacement.Floating"/> legend is drawn by its host on the canvas around the plots instead —
        /// see <see cref="Floats"/>.</summary>
        public static bool Draws(Plot plot)
            => plot.Legend is LegendPlacement.Inside or LegendPlacement.Below && Lists(plot);

        /// <summary>Whether <paramref name="plot"/>'s legend is a FLOATING box — dragged out of the plot onto the Data
        /// Display canvas, where it is placed and drawn like a marker info box.</summary>
        public static bool Floats(Plot plot) => plot.Legend == LegendPlacement.Floating && Lists(plot);

        private static bool Lists(Plot plot)
        {
            if (plot.PlotType is not (PlotType.Rect or PlotType.Smith or PlotType.Polar)) return false;
            foreach (var _ in Listed(plot)) return true;
            return false;
        }

        /// <summary>The height, in canvas pixels, of the band a BELOW legend takes under the plot; zero
        /// for any other placement.</summary>
        public static double BelowBandPx(Plot plot, (double W, double H) canvas)
            => BelowBandPx(plot, canvas.W, Math.Min(canvas.W, canvas.H) / 200.0);

        /// <summary><see cref="BelowBandPx(Plot, ValueTuple{double, double})"/> at a stated line-width
        /// unit — what <see cref="PlotCanvasGeometry"/> sizes a Smith or Polar canvas with, before that
        /// canvas exists. The disc's width does not depend on how much taller than wide the canvas is, so a
        /// square canvas of <paramref name="width"/> stands in for it.</summary>
        public static double BelowBandPx(Plot plot, double width, double lw)
        {
            if (plot.Legend != LegendPlacement.Below || !Draws(plot)) return 0;
            var m = Metrics.For(plot, lw);
            var (x0, x1) = PlotRenderer.PlotRegionX(plot, (width, width));
            var (_, rows) = Grid(m, x1 - x0, Listings(plot).Count);
            return rows * m.RowH + m.Pad;
        }

        /// <remarks>Drawn at ANY size. It used to stop below a 4 px font, which is where a plot lands when the Data
        /// Display is zoomed out, so the legend vanished — and a Below band with it, moving the plot box — while the
        /// axes and the curves it names were still drawn.</remarks>
        public static void Draw(
            SKCanvas canvas, (double W, double H) canvasSize, Plot plot, TransformSet tf, RenderTheme theme,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            if (!Draws(plot)) return;
            var m = Metrics.For(plot, Math.Min(canvasSize.W, canvasSize.H) / 200.0);

            using var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx);
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);

            var (rows, box) = Layout(canvasSize, plot, tf, m, font, fallback, aliasFor, alwaysShowSource);
            if (box is { } b) DrawFrame(canvas, b, theme, AppSettings.Current.MarkerBoxTransparentBackground);
            DrawRows(canvas, rows, m, font, fallback, theme);
        }

        /// <summary>
        /// The trace whose legend entry is under canvas point (<paramref name="x"/>, <paramref name="y"/>), or null —
        /// for the entry's context menu (DFR27-3 Rename…). Laid out by <see cref="Layout"/>, the arithmetic
        /// <see cref="Draw"/> draws with, so a click lands on the entry it sees.
        /// </summary>
        public static Trace? HitEntry(
            (double W, double H) canvasSize, Plot plot, TransformSet tf, double x, double y,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            foreach (var r in DockedRows(canvasSize, plot, tf, aliasFor, alwaysShowSource))
                if (r.Hit.Contains((float)x, (float)y)) return r.Trace;
            return null;
        }

        /// <summary>
        /// The rectangle a docked legend occupies on its plot's canvas — the box when Inside, the cells of the band when
        /// Below — or null when it draws none. What a press on the legend hits, what its selection outline is drawn
        /// around, and where a legend dragged out of the plot starts from.
        /// </summary>
        public static SKRect? DockedBounds(
            (double W, double H) canvasSize, Plot plot, TransformSet tf,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            if (!Draws(plot)) return null;
            var m = Metrics.For(plot, Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            using var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx);
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
            var (rows, box) = Layout(canvasSize, plot, tf, m, font, fallback, aliasFor, alwaysShowSource);
            if (box is { } b) return b;
            if (rows.Count == 0) return null;
            var u = rows[0].Hit;
            foreach (var r in rows) u = SKRect.Union(u, r.Hit);
            return u;
        }

        private static List<Row> DockedRows(
            (double W, double H) canvasSize, Plot plot, TransformSet tf,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            if (!Draws(plot)) return [];
            var m = Metrics.For(plot, Math.Min(canvasSize.W, canvasSize.H) / 200.0);
            using var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx);
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
            return Layout(canvasSize, plot, tf, m, font, fallback, aliasFor, alwaysShowSource).Rows;
        }

        // ---- Floating -------------------------------------------------------
        //
        //  A floating legend is the INSIDE box lifted off the plot: the same rows, sample and frame, measured in
        //  Data Display LOGICAL units at the line-width unit the plot's own canvas has, so it reads at the size it
        //  had when docked and scales with the canvas zoom exactly as a marker info box does. Its text is not cut
        //  to 60 % of the plot width as the docked box's is — off the plot there is nothing for it to cover.

        /// <summary>The line-width unit of a plot canvas <paramref name="canvasW"/> x <paramref name="canvasH"/> logical
        /// units — what the docked legend's metrics are derived from.</summary>
        public static double LineWidthFor(double canvasW, double canvasH) => Math.Min(canvasW, canvasH) / 200.0;

        /// <summary>A floating legend's size in logical units, for a plot canvas of line-width unit <paramref name="lw"/>.
        /// (0, 0) when the plot floats no legend.</summary>
        public static (double W, double H) MeasureFloating(
            Plot plot, double lw, Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            if (!Floats(plot)) return (0, 0);
            var m = Metrics.For(plot, lw);
            using var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx);
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
            var entries = Entries(plot, alwaysShowSource ?? AppSettings.Current.AlwaysDisplayDataSourcePrefix, aliasFor);
            float widest = 0f;
            foreach (var e in entries)
                widest = Math.Max(widest, RendererText.MeasureTextWithFallback(e.Label, font, fallback));
            return (m.Pad * 2 + m.SampleW + m.Gap + widest, entries.Count * m.RowH + m.Pad);
        }

        /// <summary>
        /// Draws a floating legend at (0, 0) filling <paramref name="size"/> — the box <see cref="MeasureFloating"/>
        /// measured, scaled to the pixels it is shown at. With <paramref name="isSelected"/> it is outlined as a selected
        /// marker info box is.
        /// </summary>
        public static void DrawFloating(
            SKCanvas canvas, (double W, double H) size, Plot plot, double lw, RenderTheme theme,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource, bool transparentBackground,
            bool isSelected = false, SKColor selectionColor = default)
        {
            var (lw0, lh0) = MeasureFloating(plot, lw, aliasFor, alwaysShowSource);
            if (lw0 <= 0 || lh0 <= 0 || size.W <= 0 || size.H <= 0) return;
            var m = Metrics.For(plot, lw);

            canvas.Save();
            canvas.Scale((float)(size.W / lw0), (float)(size.H / lh0));
            using (var font     = new SKFont(SkiaFonts.PlexRegular, (float)m.FontPx))
            using (var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size))
            {
                var box     = SKRect.Create(0, 0, (float)lw0, (float)lh0);
                var entries = Entries(plot, alwaysShowSource ?? AppSettings.Current.AlwaysDisplayDataSourcePrefix, aliasFor);
                float rowH = (float)m.RowH, pad = (float)m.Pad;
                float room = (float)(lw0 - m.Pad * 2 - m.SampleW - m.Gap);
                var rows = new List<Row>(entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    float cy = pad * 0.5f + rowH * (i + 0.5f);
                    rows.Add(new Row(entries[i].Trace, entries[i].Label, pad, cy, room, SKRect.Create(0, cy - rowH * 0.5f, box.Width, rowH), entries[i].Curve));
                }
                DrawFrame(canvas, box, theme, transparentBackground);
                DrawRows(canvas, rows, m, font, fallback, theme);
            }
            canvas.Restore();

            if (isSelected)
                DrawSelection(canvas, SKRect.Create(0, 0, (float)size.W, (float)size.H), selectionColor);
        }

        /// <summary>The selection outline a selected legend — floating or docked — is drawn with: the marker info box's.</summary>
        public static void DrawSelection(SKCanvas canvas, SKRect rect, SKColor color)
        {
            using var hl = new SKPaint { Color = color, StrokeWidth = 3.1f, Style = SKPaintStyle.Stroke, IsAntialias = false };
            canvas.DrawRect(rect, hl);
        }

        /// <summary>
        /// Where a floating legend nobody has placed goes, in logical coordinates: where the INSIDE box would be, against
        /// the plot box's top-right corner. A <c>.cdd</c> written by the application always carries a position; this is
        /// the rule for one that does not, shared by the window and a headless render so the two agree.
        /// </summary>
        public static PlotPoint DefaultFloatingPosition(
            Plot plot, double canvasW, double canvasH, double canvasLeft, double canvasTop,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            double lw = LineWidthFor(canvasW, canvasH);
            var (w, _) = MeasureFloating(plot, lw, aliasFor, alwaysShowSource);
            var tf  = PlotRenderer.BuildTransforms(plot, (canvasW, canvasH));
            var m   = Metrics.For(plot, lw);
            double right = (tf.Viewport.X + tf.Viewport.Width) * canvasW;
            return new PlotPoint(canvasLeft + right - m.Pad - w, canvasTop + tf.Viewport.Y * canvasH + m.Pad);
        }

        // ---- Shared layout and drawing --------------------------------------

        /// <summary>One legend entry as laid out: where its sample starts, its centre line, the room its text has,
        /// and the rectangle a click on it hits.</summary>
        private readonly record struct Row(Trace Trace, string Label, float X, float Cy, float TextRoom, SKRect Hit, int Curve = -1);

        private static (List<Row> Rows, SKRect? Box) Layout(
            (double W, double H) canvasSize, Plot plot, TransformSet tf, Metrics m, SKFont font, SKFont fallback,
            Func<Trace, string?>? aliasFor, bool? alwaysShowSource)
        {
            var entries = Entries(plot, alwaysShowSource ?? AppSettings.Current.AlwaysDisplayDataSourcePrefix, aliasFor);
            var rows    = new List<Row>(entries.Count);

            float w = (float)canvasSize.W, h = (float)canvasSize.H;
            float rowH = (float)m.RowH, pad = (float)m.Pad, sample = (float)m.SampleW, gap = (float)m.Gap;

            if (plot.Legend == LegendPlacement.Below)
            {
                // ACROSS THE PLOTTING REGION, not the canvas: the band names the curves in the plot box above it, and
                // reaching out under the Y-axis labels made it read as belonging to the whole frame.
                var (x0, x1) = PlotRenderer.PlotRegionX(plot, canvasSize);
                float band = (float)BelowBandPx(plot, canvasSize);
                var (cols, _) = Grid(m, x1 - x0, entries.Count);
                float cellW  = (float)(x1 - x0) / cols;
                for (int i = 0; i < entries.Count; i++)
                {
                    int   r  = i / cols, c = i % cols;
                    float x  = (float)x0 + c * cellW;
                    float cy = h - band + pad * 0.5f + rowH * (r + 0.5f);
                    rows.Add(new Row(entries[i].Trace, entries[i].Label, x, cy, cellW - gap - sample - gap,
                                     SKRect.Create(x, cy - rowH * 0.5f, cellW, rowH), entries[i].Curve));
                }
                return (rows, null);
            }

            // INSIDE: a box against the plot box's top-right corner, one entry per row.
            float vpLeft  = (float)(tf.Viewport.X * w), vpTop = (float)(tf.Viewport.Y * h);
            float vpRight = (float)((tf.Viewport.X + tf.Viewport.Width) * w);
            float maxText = Math.Max(0f, (vpRight - vpLeft) * 0.6f - sample - gap - 2f * pad);
            float widest  = 0f;
            foreach (var e in entries)
                widest = Math.Max(widest, Math.Min(maxText, RendererText.MeasureTextWithFallback(e.Label, font, fallback)));

            float boxW = pad * 2f + sample + gap + widest;
            float boxH = entries.Count * rowH + pad;
            var   box  = SKRect.Create(vpRight - pad - boxW, vpTop + pad, boxW, boxH);
            // The text's room is the width the box was sized from, not one worked back out of the box: in
            // floats that comes back a hair short of the widest label and cut every one of them short.
            for (int i = 0; i < entries.Count; i++)
            {
                float cy = box.Top + pad * 0.5f + rowH * (i + 0.5f);
                rows.Add(new Row(entries[i].Trace, entries[i].Label, box.Left + pad, cy, maxText,
                                 SKRect.Create(box.Left, cy - rowH * 0.5f, boxW, rowH), entries[i].Curve));
            }
            return (rows, box);
        }

        /// <summary>The box's frame. Dressed as a marker info box is, and by the same setting: no fill unless the user has
        /// turned the boxes' transparent background off. The plot itself paints no background — the host or the export
        /// decides it — so a box with a fill of its own is a white patch on any page that is not white.</summary>
        private static void DrawFrame(SKCanvas canvas, SKRect box, RenderTheme theme, bool transparentBackground)
        {
            if (!transparentBackground)
                using (var fill = new SKPaint { Style = SKPaintStyle.Fill,
                                                Color = RenderTheme.WithOpacity(theme.BackgroundColor, 0.90) })
                    canvas.DrawRect(box, fill);
            using (var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f, Color = theme.BorderColor })
                canvas.DrawRect(box, edge);
        }

        private static void DrawRows(SKCanvas canvas, List<Row> rows, Metrics m, SKFont font, SKFont fallback, RenderTheme theme)
        {
            using var text = new SKPaint { IsAntialias = true, Color = theme.TextColor };
            float sample = (float)m.SampleW, gap = (float)m.Gap;
            float baseline = font.Size * 0.35f;
            foreach (var r in rows)
            {
                DrawSample(canvas, r.Trace, r.X, r.X + sample, r.Cy, (float)m.Lw, theme, r.Curve);
                string s = Fit(r.Label, r.TextRoom, font, fallback);
                RendererText.DrawLeftTextWithFallback(canvas, s, r.X + sample + gap, r.Cy + baseline, font, fallback, text);
            }
        }

        /// <summary>Each listed trace and the name its axis gives it — on a Rect plot the Y-axis label,
        /// on Smith and Polar the label strip's. A pattern cut over a sweep is one entry per value: the value first, since a long name is what gets cut,
        /// and alone when nothing else on the plot needs telling apart (brief-cosim-pattern-5 R-cp5-5).</summary>
        internal static List<(Trace Trace, string Label, int Curve)> Entries(Plot plot, bool showSource, Func<Trace, string?>? aliasFor)
        {
            var minimal  = TraceLabeler.ComputeMinimalLabels(plot.Traces, showSource, aliasFor);
            var listings = Listings(plot);
            bool oneTrace = listings.Select(l => l.Trace).Distinct().Count() == 1;
            var list     = new List<(Trace, string, int)>(listings.Count);
            foreach (var (t, curve) in listings)
            {
                int i = plot.Traces.IndexOf(t);
                string name = i >= 0 && i < minimal.Count ? minimal[i] : t.ShortDescription;
                name = plot.PlotType == PlotType.Rect ? t.RectYLabel(name, false) : name;
                list.Add((t, curve < 0 ? name : oneTrace ? t.FamilyCurveLabel(curve) : $"{t.FamilyCurveLabel(curve)}: {name}", curve));
            }
            return list;
        }

        /// <summary>What a legend has a row for: a trace (curve −1), or each curve of a pattern cut over a sweep. The back branch of such a cut is the same curves at
        /// the same values, so it adds none while a front branch is there to name them.</summary>
        private static List<(Trace Trace, int Curve)> Listings(Plot plot)
        {
            var listed = Listed(plot).ToList();
            bool hasFront = listed.Exists(t => t.LabelsFamilyCurves && !t.MirrorPatternAngle);
            var rows = new List<(Trace, int)>(listed.Count);
            foreach (var t in listed)
            {
                if (!t.LabelsFamilyCurves) { rows.Add((t, -1)); continue; }
                if (t.MirrorPatternAngle && hasFront) continue;
                for (int k = 0; k < t.FamilyCurves.Count; k++) rows.Add((t, k));
            }
            return rows;
        }

        // ---- Layout ------------------------------------------------------

        private readonly record struct Metrics(double Lw, double FontPx, double RowH, double Pad, double SampleW, double Gap)
        {
            // The Y-axis label's own size, so an entry reads at the size of the name it repeats.
            public static Metrics For(Plot plot, double lw)
            {
                double f = plot.Axes.FontSizeTicks * 0.9 * lw;
                return new Metrics(lw, f, f * 1.5, f * 0.5, f * 2.2, f * 0.5);
            }
        }

        /// <summary>Columns and rows of the BELOW grid across a plotting region <paramref name="width"/> wide: as many
        /// equal cells as leave each room for a sample and about fifteen characters.</summary>
        private static (int Cols, int Rows) Grid(Metrics m, double width, int n)
        {
            if (n <= 0) return (1, 0);
            double cell = m.SampleW + m.Gap + m.FontPx * 8.0;
            int    cols = (int)Math.Clamp(Math.Floor(width / cell), 1, n);
            return (cols, (n + cols - 1) / cols);
        }

        private static string Fit(string s, float room, SKFont font, SKFont fallback)
        {
            if (room <= 0f) return "";
            if (RendererText.MeasureTextWithFallback(s, font, fallback) <= room) return s;
            while (s.Length > 1 && RendererText.MeasureTextWithFallback(s + "…", font, fallback) > room) s = s[..^1];
            return s.TrimEnd() + "…";
        }

        /// <summary>A short horizontal run of the trace's line — its colour, width and line type — or,
        /// for a trace drawn as points only, one point.</summary>
        private static void DrawSample(SKCanvas canvas, Trace trace, float x0, float x1, float y, float lw, RenderTheme theme, int curve = -1)
        {
            var   props = trace.Properties;
            var   color = theme.ToSKColor(curve < 0 ? props.LineColor : trace.FamilyCurveColor(curve), props.LineOpacity);
            float sw    = Math.Max(0.5f, lw * (float)props.LineWidth);

            if (!props.LineEnabled)
            {
                using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = color };
                canvas.DrawCircle((x0 + x1) / 2f, y, Math.Max(sw, lw * 1.5f), dot);
                return;
            }

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, IsAntialias = true, Color = color, StrokeWidth = sw,
                StrokeCap = props.LineType == LineType.Dotted ? SKStrokeCap.Round : SKStrokeCap.Butt,
            };
            switch (props.LineType)
            {
                case LineType.Dashed:
                    for (float x = x0; x < x1; x += sw * 5f)
                        canvas.DrawLine(x, y, Math.Min(x + sw * 3f, x1), y, paint);
                    break;
                case LineType.Dotted:
                    for (float x = x0 + sw / 2f; x <= x1; x += sw * 2.5f)
                        canvas.DrawLine(x, y, x + 0.01f, y, paint);
                    break;
                default:
                    canvas.DrawLine(x0, y, x1, y, paint);
                    break;
            }
        }
    }
}
