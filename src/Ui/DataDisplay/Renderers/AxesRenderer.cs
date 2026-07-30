// ================================================================
//  AxesRenderer.cs  —  Skia drawing for rectangular, polar, and Smith grids
//
//  Ported from splotRF/src/Renderers/AxesRenderer.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay; font seam retargeted from
//  SkiaFonts.Regular/Bold (DejaVu) to SkiaFonts.PlexRegular/PlexBold
//  (IBM Plex).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using RfCore;

namespace CircuitRF.Ui.DataDisplay
{
    public static class AxesRenderer
    {
        // ---- Tunable constants ------------------------------------------

        public const float DescriptionStripPad = 3f;

        // ================================================================
        //  Rectangular grid
        // ================================================================

        public static void DrawRectGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            PlotDetail           detail,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);
            var (minorTicks, majorTicks, labels) = detail.Properties();

            var (textFont, textPaint) = MakeTextObjects(axes.FontSizeTicks, lw, theme);
            using var _tf = textFont;
            using var _tp = textPaint;

            DrawBorder(canvas, axes, tf, lw, theme);

            if (!majorTicks) return;

            var ticks = axes.Ticks(minorTicks);

            using var majorGridPath  = new SKPath();
            using var majorTickPath  = new SKPath();

            foreach (var tx in ticks.MajorX)
            {
                var top = tf.PrimaryToCanvas(tx, axes.Window.Top);
                var bot = tf.PrimaryToCanvas(tx, axes.Window.Bottom);
                majorGridPath.MoveTo(top); majorGridPath.LineTo(bot);

                var tickBase = tf.PrimaryToCanvas(tx, axes.Window.Top);
                var tickTip  = tf.PrimaryToCanvas(tx, axes.Window.Top + axes.TickLengthY);
                majorTickPath.MoveTo(tickBase); majorTickPath.LineTo(tickTip);

                if (labels)
                {
                    double v     = Math.Abs(tx) < 1e-12 ? 0 : tx;
                    string label = v.ToString($"G{axes.NumDigitsXAxis}");
                    float  tw    = textFont.MeasureText(label);
                    canvas.DrawText(label,
                        tickBase.X - tw / 2f,
                        tickBase.Y + textFont.Size * 1.4f,
                        SKTextAlign.Left, textFont, textPaint);
                }
            }

            using var majorGridPathY  = new SKPath();
            using var majorTickPathY  = new SKPath();
            using var majorGridPathY2 = new SKPath();
            using var majorTickPathY2 = new SKPath();

            foreach (var (yPrimary, ySecondary) in ticks.MajorY)
            {
                if (!double.IsFinite(yPrimary)) continue;

                var left  = tf.PrimaryToCanvas(axes.Window.Left,  yPrimary);
                var right = tf.PrimaryToCanvas(axes.Window.Right, yPrimary);
                majorGridPathY.MoveTo(left); majorGridPathY.LineTo(right);

                var tl0 = tf.PrimaryToCanvas(axes.Window.Left,                    yPrimary);
                var tl1 = tf.PrimaryToCanvas(axes.Window.Left + axes.TickLengthX, yPrimary);
                majorTickPathY.MoveTo(tl0); majorTickPathY.LineTo(tl1);

                if (labels)
                {
                    double v     = Math.Abs(yPrimary) < 1e-12 ? 0 : yPrimary;
                    string label = v.ToString($"G{axes.NumDigitsLeftY}");
                    float  tw    = textFont.MeasureText(label);
                    canvas.DrawText(label,
                        tl0.X - tw - lw * 4f,
                        tl0.Y + textFont.Size * 0.35f,
                        SKTextAlign.Left, textFont, textPaint);
                }

                if (axes.ShowSecondary)
                {
                    if (axes.SecondaryShareGrid)
                    {
                        var tr0 = tf.PrimaryToCanvas(axes.Window.Right - axes.TickLengthX, yPrimary);
                        var tr1 = tf.PrimaryToCanvas(axes.Window.Right,                    yPrimary);
                        majorTickPathY.MoveTo(tr0); majorTickPathY.LineTo(tr1);
                    }
                    else if (double.IsFinite(ySecondary))
                    {
                        var sr0 = tf.SecondaryToCanvas(axes.WindowSecondary.Right - axes.TickLengthX, ySecondary);
                        var sr1 = tf.SecondaryToCanvas(axes.WindowSecondary.Right,                    ySecondary);
                        majorTickPathY2.MoveTo(sr0); majorTickPathY2.LineTo(sr1);

                        var sl = tf.SecondaryToCanvas(axes.WindowSecondary.Left,  ySecondary);
                        var sr = tf.SecondaryToCanvas(axes.WindowSecondary.Right, ySecondary);
                        majorGridPathY2.MoveTo(sl); majorGridPathY2.LineTo(sr);
                    }

                    if (labels && double.IsFinite(ySecondary))
                    {
                        double v2     = Math.Abs(ySecondary) < 1e-12 ? 0 : ySecondary;
                        string label2 = v2.ToString($"G{axes.NumDigitsRightY}");
                        var    rPt    = tf.SecondaryToCanvas(axes.WindowSecondary.Right, ySecondary);
                        canvas.DrawText(label2,
                            rPt.X + lw * 4f,
                            rPt.Y + textFont.Size * 0.35f,
                            SKTextAlign.Left, textFont, textPaint);
                    }
                }
            }

            using var gridPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.GridColor, axes.MinorTransparencyScale),
                StrokeWidth = (float)axes.GridThicknessFactor * lw,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = false
            };
            canvas.DrawPath(majorGridPath,  gridPaint);
            canvas.DrawPath(majorGridPathY, gridPaint);
            if (axes.ShowSecondary && !axes.SecondaryShareGrid)
                canvas.DrawPath(majorGridPathY2, gridPaint);

            using var tickPaint = StrokePaint(theme.TickColor, (float)axes.TickThicknessFactor * lw);
            canvas.DrawPath(majorTickPath,  tickPaint);
            canvas.DrawPath(majorTickPathY, tickPaint);
            if (axes.ShowSecondary) canvas.DrawPath(majorTickPathY2, tickPaint);

            if (!minorTicks) return;

            using var minorGridPath  = new SKPath();
            using var minorTickPath  = new SKPath();
            using var minorGridPathY = new SKPath();
            using var minorTickPathY = new SKPath();

            foreach (var tx in ticks.MinorX)
            {
                minorGridPath.MoveTo(tf.PrimaryToCanvas(tx, axes.Window.Top));
                minorGridPath.LineTo(tf.PrimaryToCanvas(tx, axes.Window.Bottom));
                minorTickPath.MoveTo(tf.PrimaryToCanvas(tx, axes.Window.Top));
                minorTickPath.LineTo(tf.PrimaryToCanvas(tx, axes.Window.Top + axes.TickLengthY / 2));
            }

            foreach (var ty in ticks.MinorY)
            {
                minorGridPathY.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  ty));
                minorGridPathY.LineTo(tf.PrimaryToCanvas(axes.Window.Right, ty));
                minorTickPathY.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,                    ty));
                minorTickPathY.LineTo(tf.PrimaryToCanvas(axes.Window.Left + axes.TickLengthX / 2, ty));
            }

            using var mgPaint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(theme.MinorGridColor, axes.MinorTransparencyScale * 0.4),
                StrokeWidth = (float)axes.GridThicknessFactor * lw / 2f,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = false
            };
            canvas.DrawPath(minorGridPath,  mgPaint);
            canvas.DrawPath(minorGridPathY, mgPaint);

            using var mtPaint = StrokePaint(theme.TickColor, (float)axes.TickThicknessFactor * lw / 2f);
            canvas.DrawPath(minorTickPath,  mtPaint);
            canvas.DrawPath(minorTickPathY, mtPaint);
        }

        // ================================================================
        //  Polar grid
        // ================================================================

        public static void DrawPolarGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);

            canvas.Save();
            canvas.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize));

            using var axisPaint = StrokePaint(theme.GridColor, lw * (float)axes.GridThicknessFactor);

            using var cross = new SKPath();
            cross.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  0.0));
            cross.LineTo(tf.PrimaryToCanvas(axes.Window.Right, 0.0));
            cross.MoveTo(tf.PrimaryToCanvas(0.0, axes.Window.Top));
            cross.LineTo(tf.PrimaryToCanvas(0.0, axes.Window.Bottom));
            canvas.DrawPath(cross, axisPaint);

            var ticks = axes.Ticks(true);
            var radii  = ticks.MinorX.Select(Math.Abs).Where(r => r > 1e-12).Distinct().ToList();
            var ctr    = tf.PrimaryToCanvas(0.0, 0.0);

            foreach (double r in radii)
            {
                var   edge = tf.PrimaryToCanvas(r, 0.0);
                float pxR  = Math.Abs(edge.X - ctr.X);
                if (pxR > 0)
                    canvas.DrawCircle(ctr.X, ctr.Y, pxR, axisPaint);
            }

            if (canvasSize.W > 250 && axes.Window.Width < 8)
            {
                var (lblFont, lblPaint) = MakeTextObjects(axes.FontSizeTicks * 0.85, lw, theme);
                using var _lf1 = lblFont;
                using var _lp1 = lblPaint;
                lblPaint.Color = RenderTheme.WithOpacity(lblPaint.Color, axes.MinorTransparencyScale);

                foreach (double r in radii)
                {
                    var    pt   = tf.PrimaryToCanvas(r, 0.0);
                    string text = r.ToString("G4") + "  ";
                    float  tw   = lblFont.MeasureText(text);
                    canvas.DrawText(text,
                        pt.X - tw,
                        pt.Y,
                        SKTextAlign.Left, lblFont, lblPaint);
                }
            }

            canvas.Restore();
        }

        // ================================================================
        //  Smith chart grid
        // ================================================================

        public static void DrawSmithGrid(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Axes                 axes,
            TransformSet         tf,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);

            canvas.Save();
            canvas.ClipRect(PlotRenderer.ViewportClipRect(tf.Viewport, canvasSize));

            bool clipToUnit = axes.Window.Width == 2
                           && axes.Window.X     == -1
                           && axes.Window.Y     == -1;
            if (clipToUnit)
            {
                var   ctr  = tf.PrimaryToCanvas(0.0, 0.0);
                var   edge = tf.PrimaryToCanvas(1.0, 0.0);
                float pxR  = Math.Abs(edge.X - ctr.X);
                using var unitPath = new SKPath();
                unitPath.AddCircle(ctr.X, ctr.Y, pxR);
                canvas.ClipPath(unitPath);
            }

            using var smithPaint = StrokePaint(theme.GridColor, lw * (float)axes.GridThicknessFactor);
            using var minorPaint = StrokePaint(
                RenderTheme.WithOpacity(theme.GridColor, axes.MinorTransparencyScale),
                lw * (float)axes.GridThicknessFactor);

            using var realAxis = new SKPath();
            realAxis.MoveTo(tf.PrimaryToCanvas(axes.Window.Left,  0.0));
            realAxis.LineTo(tf.PrimaryToCanvas(axes.Window.Right, 0.0));
            if (!clipToUnit)
            {
                realAxis.MoveTo(tf.PrimaryToCanvas(1.0, axes.Window.Top));
                realAxis.LineTo(tf.PrimaryToCanvas(1.0, axes.Window.Bottom));
            }

            double[] constantRValues = { 0, 0.5, 1, 2, 5, 10, -0.5, -0.8, -1.2, -1.5, -2, -2.5, -3, -4, -7, -12 };
            double[] constantXValues = { 0.2, 0.5, 1, 2, 5, 10 };

            var rCircles = new (float cx, float cy, float r, double rVal)[constantRValues.Length];
            for (int i = 0; i < constantRValues.Length; i++)
            {
                double rv     = constantRValues[i];
                double radius = 1.0 / (1.0 + rv);
                double centreX = 1.0 - radius;
                var    cPx    = tf.PrimaryToCanvas(centreX, 0.0);
                var    ePx    = tf.PrimaryToCanvas(centreX + radius, 0.0);
                float  pxR    = Math.Abs(ePx.X - cPx.X);
                rCircles[i]   = (cPx.X, cPx.Y, pxR, rv);
            }

            var xCircles = new (float cx, float cy, float r, double xVal)[constantXValues.Length];
            for (int i = 0; i < constantXValues.Length; i++)
            {
                double xv     = constantXValues[i];
                double radius = 1.0 / xv;
                var    cPx    = tf.PrimaryToCanvas(1.0, -radius);
                var    ePx    = tf.PrimaryToCanvas(1.0 + radius, -radius);
                float  pxR    = Math.Abs(ePx.X - cPx.X);
                xCircles[i]   = (cPx.X, cPx.Y, pxR, xv);
            }

            var rMaskTable = new System.Collections.Generic.Dictionary<int, int[]>
            {
                [1]  = new[] { 2 },
                [2]  = new[] { 4 },
                [3]  = new[] { 4 },
                [4]  = new[] { 5 },
                [11] = new[] { 2 },
                [12] = new[] { 4 },
                [13] = new[] { 4 },
                [14] = new[] { 5 },
            };

            var xMaskTable = new System.Collections.Generic.Dictionary<int, int[]>
            {
                [0] = new[] {  3, 13 },
                [1] = new[] {  2, 13 },
                [2] = new[] {  4, 14 },
                [3] = new[] {  4, 14 },
                [4] = new[] {  4, 14 },
            };

            SKPath EvenOddExclusionPath(IEnumerable<(float cx, float cy, float r)> circles)
            {
                var p = new SKPath { FillType = SKPathFillType.EvenOdd };
                p.AddRect(SKRect.Create(0, 0, (float)canvasSize.W, (float)canvasSize.H));
                foreach (var (cx, cy, r) in circles)
                    p.AddCircle(cx, cy, r);
                return p;
            }

            for (int i = 0; i < rCircles.Length; i++)
            {
                var (cx, cy, pxR, rVal) = rCircles[i];
                if (pxR <= 0 || !float.IsFinite(pxR)) continue;

                var paint = rVal == 0 ? smithPaint : minorPaint;

                if (rMaskTable.TryGetValue(i, out int[]? xMaskIndices))
                {
                    float realY = tf.PrimaryToCanvas(0.0, 0.0).Y;
                    var maskedCircles = xMaskIndices
                        .SelectMany(xi =>
                        {
                            var (xcx, xcy, xr, _) = xCircles[xi];
                            return new[] { (xcx, xcy, xr), (xcx, 2f * realY - xcy, xr) };
                        });

                    canvas.Save();
                    using var clipPath = EvenOddExclusionPath(maskedCircles);
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                    canvas.DrawCircle(cx, cy, pxR, paint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawCircle(cx, cy, pxR, paint);
                }
            }

            float realAxisY = tf.PrimaryToCanvas(0.0, 0.0).Y;

            for (int i = 0; i < xCircles.Length; i++)
            {
                var (cx, cy, pxR, _) = xCircles[i];
                if (pxR <= 0 || !float.IsFinite(pxR)) continue;

                float conjCy = 2f * realAxisY - cy;

                if (xMaskTable.TryGetValue(i, out int[]? rMaskIndices))
                {
                    var maskCircles = rMaskIndices
                        .Select(ri => { var (rcx, rcy, rr, _) = rCircles[ri]; return (rcx, rcy, rr); });

                    canvas.Save();
                    using var clipPath = EvenOddExclusionPath(maskCircles);
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                    canvas.DrawCircle(cx,     cy,     pxR, minorPaint);
                    canvas.DrawCircle(cx,     conjCy, pxR, minorPaint);
                    canvas.Restore();
                }
                else
                {
                    canvas.DrawCircle(cx,     cy,     pxR, minorPaint);
                    canvas.DrawCircle(cx,     conjCy, pxR, minorPaint);
                }
            }

            canvas.DrawPath(realAxis, smithPaint);

            if (canvasSize.W > 250 && axes.Window.Width < 8)
            {
                var (lblFont, lblPaint) = MakeTextObjects(axes.FontSizeTicks * 0.85, lw, theme);
                using var _lf2 = lblFont;
                using var _lp2 = lblPaint;
                lblPaint.Color = RenderTheme.WithOpacity(lblPaint.Color, axes.MinorTransparencyScale);

                lblFont.GetFontMetrics(out var metrics);

                double[] rLabels = axes.Window.Width > 3
                    ? new[] { 0.5, 1.0, 2.0, 5.0 }
                    : new[] { 0.5, 1.0, 2.0, 5.0, 10.0 };

                foreach (double rVal in rLabels)
                {
                    string text    = " " + rVal.ToString("G4");
                    var    g       = RfHelpers.Z2G(new Complex(rVal, 0));
                    var    pt      = tf.PrimaryToCanvas(g.Real, g.Imaginary);
                    float  baselineY = pt.Y - metrics.Ascent;
                    canvas.DrawText(text, pt.X, baselineY,
                        SKTextAlign.Left, lblFont, lblPaint);
                }

                double[] xLabels = axes.Window.Width > 3
                    ? constantXValues.Take(constantXValues.Length - 1).ToArray()
                    : constantXValues;

                foreach (double xVal in xLabels)
                {
                    var g   = RfHelpers.Z2G(new Complex(0, xVal));
                    var ptU = tf.PrimaryToCanvas( g.Real,  g.Imaginary);
                    var ptD = tf.PrimaryToCanvas( g.Real, -g.Imaginary);

                    string textU  = " "  + xVal.ToString("G4");
                    string textD  = " -" + xVal.ToString("G4");
                    float  twU    = lblFont.MeasureText(textU);
                    float  twD    = lblFont.MeasureText(textD);

                    float baselineYU = ptU.Y - metrics.Ascent;
                    float baselineYD = ptD.Y - metrics.Descent;

                    if (xVal <= 1.0)
                    {
                        canvas.DrawText(textU, ptU.X, baselineYU,
                            SKTextAlign.Left, lblFont, lblPaint);
                        canvas.DrawText(textD, ptD.X, baselineYD,
                            SKTextAlign.Left, lblFont, lblPaint);
                    }
                    else
                    {
                        canvas.DrawText(textU, ptU.X - twU, baselineYU,
                            SKTextAlign.Left, lblFont, lblPaint);
                        canvas.DrawText(textD, ptD.X - twD, baselineYD,
                            SKTextAlign.Left, lblFont, lblPaint);
                    }
                }
            }

            canvas.Restore();
        }

        // ================================================================
        //  Title, X-label, and global Y-label rendering
        // ================================================================

        public struct LabelHitRects
        {
            public SKRect Title;
            public SKRect XLabel;
            public SKRect YLabel;
            public SKRect Y2Label;
        }

        public static LabelHitRects DrawTitleAndAxisLabels(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            TransformSet         tf,
            RenderTheme          theme,
            Func<Trace, string?>? aliasFor = null)
        {
            float lw = LineWidth(canvasSize);
            var (font, paint) = MakeTextObjects(plot.Axes.FontSizeLabel * 1.4, lw, theme);
            using var _f = font;
            using var _p = paint;

            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpLeft    = (float)(tf.Viewport.X                        * w);
            float vpTop     = (float)(tf.Viewport.Y                        * h);
            float vpRight   = (float)((tf.Viewport.X + tf.Viewport.Width)  * w);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (vpLeft + vpRight) / 2f;
            float vpCenterY = (vpTop  + vpBottom) / 2f;

            var rects = new LabelHitRects();

            string title = plot.Title;
            if (!string.IsNullOrEmpty(title))
            {
                // Shrink-to-fit: a long title (e.g. "Pout (dBm) at Constant Eff=…") can be wider
                // than the control. Since the PlotControl now clips to its bounds, scale the title
                // font down so the full text fits within the canvas width instead of being clipped.
                float avail   = w - 4f * lw;                 // small side margin
                float titleW  = font.MeasureText(title);
                float titleSz = font.Size;
                if (titleW > avail && titleW > 0f)
                {
                    titleSz = Math.Max(font.Size * (avail / titleW), font.Size * 0.5f);
                    font.Size = titleSz;
                }
                float tw = font.MeasureText(title);
                float tx = vpCenterX - tw / 2f;
                float ty = vpTop / 2f + font.Size * 0.35f;
                canvas.DrawText(title, tx, ty, SKTextAlign.Left, font, paint);
                rects.Title = new SKRect(tx, ty - font.Size, tx + tw, ty + font.Size * 0.5f);
            }

            string xLabel = plot.XLabel;
            if (!string.IsNullOrEmpty(xLabel))
            {
                using var xFont = new SKFont(SkiaFonts.PlexRegular,
                    (float)(plot.Axes.FontSizeTicks * 0.9f * lw));
                float tw = xFont.MeasureText(xLabel);
                float tx = vpCenterX - tw / 2f;
                float ty = vpBottom + (h - vpBottom) / 2f + xFont.Size * 0.35f;
                canvas.DrawText(xLabel, tx, ty, SKTextAlign.Left, xFont, paint);
                rects.XLabel = new SKRect(tx, ty - xFont.Size, tx + tw, ty + xFont.Size * 0.5f);
            }

            {
                using var yFont  = new SKFont(SkiaFonts.PlexRegular, (float)(plot.Axes.FontSizeTicks * 0.9f * lw));
                using var yPaint = new SKPaint { IsAntialias = true };
                float sw     = yFont.Size * 1.5f;
                float maxLen = h - 12f;

                void DrawAt(string text, SKColor color, float cx, bool rotRight)
                {
                    if (cx < 0f || cx > w) return;
                    string s = text;
                    while (s.Length > 1 && yFont.MeasureText(s) > maxLen)
                        s = s[..^1];
                    if (s.Length < text.Length) s = "…" + s.TrimStart();
                    yPaint.Color = color;
                    float tw = yFont.MeasureText(s);
                    canvas.Save();
                    canvas.Translate(cx, vpCenterY);
                    canvas.RotateDegrees(rotRight ? 90f : -90f);
                    canvas.DrawText(s, -tw / 2f, yFont.Size * 0.35f,
                        SKTextAlign.Left, yFont, yPaint);
                    canvas.Restore();
                }

                var (leftAnchor, rightAnchor) =
                    ComputeYLabelAnchors(plot.Axes, vpLeft, vpRight, lw);

                // Compute per-plot minimal labels (same policy as the label-strip controls, and now
                // the same alias resolver too — this Rect Y-axis margin label used to fall back to
                // the raw file-stem heuristic regardless of any alias the user set, since no
                // resolver was ever threaded down to this renderer).
                bool alwaysSource = AppSettingsViewModel.Instance.AlwaysDisplayDataSourcePrefix;
                var  allLabels    = TraceLabeler.ComputeMinimalLabels(plot.Traces, alwaysSource, aliasFor);
                var  labelLookup  = new Dictionary<Trace, string>();
                for (int i = 0; i < plot.Traces.Count; i++)
                    labelLookup[plot.Traces[i]] = allLabels[i];

                // Reference X-axis = first trace's cube X-axis (the plot's X axis). Cube traces whose X-axis
                // name differs are softly flagged "dimension mismatch" but still attempt to render.
                string? refCubeXAxis = plot.Traces.Count > 0 && plot.Traces[0].IsCubeBound
                    ? plot.Traces[0].CubeXAxisName
                    : null;

                string LabelFor(Trace t)
                {
                    string networkFallback = labelLookup.GetValueOrDefault(t, t.ShortDescription);
                    bool mismatch = t.IsCubeBound
                                    && refCubeXAxis != null
                                    && !string.Equals(t.CubeXAxisName, refCubeXAxis, StringComparison.Ordinal);
                    return t.RectYLabel(networkFallback, mismatch);
                }

                string yLabel = plot.YLabel;
                if (!string.IsNullOrEmpty(yLabel))
                {
                    float cx = leftAnchor - sw * 0.5f;
                    DrawAt(yLabel, theme.TextColor, cx, false);
                    rects.YLabel = new SKRect(cx - sw * 0.5f, vpTop, leftAnchor, vpBottom);
                }
                else if (!plot.CustomYLabelOn)
                {
                    var leftTraces = plot.LeftAxisTraces;
                    int col = 0;
                    for (int i = 0; i < leftTraces.Count; i++)
                    {
                        if (leftTraces[i].IsContourTrace) continue;
                        float cx = leftAnchor - sw * (col + 0.5f);
                        DrawAt(LabelFor(leftTraces[i]),
                               RenderTheme.ToSKColor(leftTraces[i].Properties.LineColor),
                               cx, false);
                        col++;
                    }
                }

                string y2Label = plot.Y2Label;
                if (!string.IsNullOrEmpty(y2Label) && plot.Axes.ShowSecondary)
                {
                    float cx = rightAnchor + sw * 0.5f;
                    DrawAt(y2Label, theme.TextColor, cx, true);
                    rects.Y2Label = new SKRect(rightAnchor, vpTop, cx + sw * 0.5f, vpBottom);
                }
                else if (plot.Axes.ShowSecondary && !plot.CustomY2LabelOn)
                {
                    var rightTraces = plot.RightAxisTraces;
                    int col2 = 0;
                    for (int i = 0; i < rightTraces.Count; i++)
                    {
                        if (rightTraces[i].IsContourTrace) continue;
                        float cx = rightAnchor + sw * (col2 + 0.5f);
                        DrawAt(LabelFor(rightTraces[i]),
                               RenderTheme.ToSKColor(rightTraces[i].Properties.LineColor),
                               cx, true);
                        col2++;
                    }
                }
            }

            return rects;
        }

        public static void DrawComplexXLabels(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            TransformSet         tf,
            RenderTheme          theme)
        {
            float lw = LineWidth(canvasSize);
            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpTop     = (float)(tf.Viewport.Y * h);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (float)((tf.Viewport.X + tf.Viewport.Width / 2.0) * w);

            string title = plot.Title;
            if (!string.IsNullOrEmpty(title) && vpTop > 4f)
            {
                float titleSz = (float)(plot.Axes.FontSizeLabel * 1.4f * lw);
                using var tf2 = new SKFont(SkiaFonts.PlexRegular, titleSz);
                using var tp2 = new SKPaint { Color = theme.TextColor, IsAntialias = true };
                // Shrink-to-fit so a long contour title ("X at Constant Y=…") fits the control
                // width rather than being clipped at the now-bounded edges.
                float availT = w - 4f * lw;
                float measT  = tf2.MeasureText(title);
                if (measT > availT && measT > 0f)
                    tf2.Size = Math.Max(titleSz * (availT / measT), titleSz * 0.5f);
                float tw2 = tf2.MeasureText(title);
                canvas.DrawText(title, vpCenterX - tw2 / 2f,
                    vpTop / 2f + tf2.Size * 0.35f, SKTextAlign.Left, tf2, tp2);
            }

            var traces = plot.Traces;
            if (traces.Count == 0) return;

            bool hasCustomX = plot.CustomXLabelOn && !string.IsNullOrEmpty(plot.CustomXLabel);

            // Contour traces never emit freq X-labels; filter them out.
            var nonContourTraces = traces.Where(t => !t.IsContourTrace).ToList();
            if (!hasCustomX && nonContourTraces.Count == 0) return;

            bool multiTrace = nonContourTraces.Count > 1;
            int  drawCount  = hasCustomX ? 1 : nonContourTraces.Count;

            float fontSizePx = (float)(plot.Axes.FontSizeLabel * lw);
            if (fontSizePx < 4f) return;

            float lineH = fontSizePx * 1.2f;

            using var font  = new SKFont(SkiaFonts.PlexRegular, fontSizePx);
            using var paint = new SKPaint { IsAntialias = true };

            for (int i = 0; i < drawCount; i++)
            {
                string label;
                if (hasCustomX)
                {
                    label       = plot.CustomXLabel;
                    paint.Color = theme.TextColor;
                }
                else
                {
                    var    t   = nonContourTraces[i];
                    double min = t.MinFreq;
                    double max = t.MaxFreq;
                    if (!double.IsFinite(min) || !double.IsFinite(max)) continue;

                    double scale = plot.FreqUnits.Scale();
                    string sMin  = (scale * min).ToString("G4");
                    string sMax  = (scale * max).ToString("G4");
                    label       = $"freq ({sMin} to {sMax} {plot.FreqUnits.Description()})";
                    paint.Color = multiTrace
                        ? RenderTheme.ToSKColor(nonContourTraces[i].Properties.LineColor)
                        : theme.TextColor;
                }

                float ty = vpBottom + lineH * (i + 0.8f) + 2f * lw;
                if (ty > h) break;

                float tw = font.MeasureText(label);
                canvas.DrawText(label, vpCenterX - tw / 2f, ty,
                    SKTextAlign.Left, font, paint);
            }
        }

        public static LabelHitRects ComputeLabelHitRects(
            Plot plot, (double W, double H) canvasSize)
        {
            if (!plot.PlotType.IsRect()) return default;

            var tf = PlotRenderer.BuildTransforms(plot, canvasSize);
            float lw = LineWidth(canvasSize);

            float titleSzPx  = (float)(plot.Axes.FontSizeLabel  * 1.4 * lw);
            float xLabelSzPx = (float)(plot.Axes.FontSizeTicks  * 0.9 * lw);

            float w  = (float)canvasSize.W;
            float h  = (float)canvasSize.H;

            float vpLeft    = (float)(tf.Viewport.X                     * w);
            float vpTop     = (float)(tf.Viewport.Y                     * h);
            float vpRight   = (float)((tf.Viewport.X + tf.Viewport.Width)  * w);
            float vpBottom  = (float)((tf.Viewport.Y + tf.Viewport.Height) * h);
            float vpCenterX = (vpLeft + vpRight) / 2f;
            float vpCenterY = (vpTop  + vpBottom) / 2f;

            var rects = new LabelHitRects();

            float ApproxWidth(string s, float sz) => s.Length * sz * 0.55f;

            if (!string.IsNullOrEmpty(plot.Title))
            {
                float tw = ApproxWidth(plot.Title, titleSzPx);
                float tx = vpCenterX - tw / 2f;
                float ty = vpTop / 2f + titleSzPx * 0.35f;
                rects.Title = new SKRect(tx, ty - titleSzPx * 1.2f, tx + tw, ty + titleSzPx * 0.5f);
            }

            if (!string.IsNullOrEmpty(plot.XLabel))
            {
                float tw = ApproxWidth(plot.XLabel, xLabelSzPx);
                float tx = vpCenterX - tw / 2f;
                float ty = vpBottom + (h - vpBottom) / 2f + xLabelSzPx * 0.35f;
                rects.XLabel = new SKRect(tx, ty - xLabelSzPx * 1.2f, tx + tw, ty + xLabelSzPx * 0.5f);
            }

            float yFontSz = (float)(plot.Axes.FontSizeTicks * 0.9f * lw);
            float ySw     = yFontSz * 1.5f;

            var (leftAnchorH, rightAnchorH) =
                ComputeYLabelAnchors(plot.Axes, vpLeft, vpRight, lw);

            if (!string.IsNullOrEmpty(plot.YLabel))
                rects.YLabel = new SKRect(leftAnchorH - ySw, vpTop, leftAnchorH, vpBottom);

            if (!string.IsNullOrEmpty(plot.Y2Label) && plot.Axes.ShowSecondary)
                rects.Y2Label = new SKRect(rightAnchorH, vpTop, rightAnchorH + ySw, vpBottom);

            return rects;
        }

        // ================================================================
        //  Private helpers
        // ================================================================

        private static void DrawBorder(
            SKCanvas canvas, Axes axes, TransformSet tf, float lw, RenderTheme theme)
        {
            using var path = new SKPath();
            var bl = tf.PrimaryToCanvas(axes.Window.Left,  axes.Window.Top);
            var tl = tf.PrimaryToCanvas(axes.Window.Left,  axes.Window.Bottom);
            var tr = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Bottom);
            var br = tf.PrimaryToCanvas(axes.Window.Right, axes.Window.Top);

            path.MoveTo(bl); path.LineTo(tl);
            path.MoveTo(bl); path.LineTo(br);

            if (axes.ShowSecondary)
            {
                path.MoveTo(tl); path.LineTo(tr);
                path.MoveTo(tr); path.LineTo(br);
            }

            using var p = StrokePaint(theme.BorderColor, 2f * (float)axes.GridThicknessFactor * lw);
            canvas.DrawPath(path, p);
        }

        public static float LineWidth((double W, double H) canvas) =>
            (float)(Math.Min(canvas.W, canvas.H) / 200.0);

        private static SKPaint StrokePaint(SKColor color, float width) =>
            new SKPaint
            {
                Color       = color,
                StrokeWidth = width,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap   = SKStrokeCap.Square
            };

        private static (SKFont Font, SKPaint Paint) MakeTextObjects(
            double fontSizeFactor, float lw, RenderTheme theme)
        {
            var font  = new SKFont(SkiaFonts.PlexRegular, (float)(fontSizeFactor * lw));
            var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
            return (font, paint);
        }

        /// <summary>
        /// Width of the widest tick number ACTUALLY DRAWN on one Y axis — measured by walking the
        /// same tick set, applying the same format string, and applying the same near-zero
        /// normalisation that <see cref="DrawRectGrid"/>'s own tick-label loop uses, so the two can
        /// never disagree about how wide the number column is.
        ///
        /// **Measuring only the window endpoints (Top/Bottom) is NOT equivalent and was the bug**:
        /// tick labels are drawn at every major gridline, and an intermediate tick can format wider
        /// than both endpoints — e.g. an axis spanning -10…10 draws "-10" and "10" at its ends but
        /// also "-7.5" in between, which is wider than either. The anchor derived from it then sat
        /// too close to the axis and the rotated per-trace Y label overlapped the tick numbers.
        /// This affects BOTH axes symmetrically (left labels are right-aligned ending at the left
        /// anchor; right labels start at the right anchor), so both are computed the same way here.
        ///
        /// Falls back to the endpoint measurement only when the tick set yields nothing finite
        /// (degenerate axis), so the label can never end up flush against the axis.
        /// </summary>
        internal static float MaxYTickLabelWidth(SKFont font, Axes axes, bool secondary)
        {
            // MajorY is identical for minorTicks true/false (minor ticks populate separate lists),
            // so the cheaper call is safe and matches what the draw path lays out.
            var ticks  = axes.Ticks(minorTicks: false);
            int digits = secondary ? axes.NumDigitsRightY : axes.NumDigitsLeftY;

            float max = 0f;
            foreach (var (primary, secondaryValue) in ticks.MajorY)
            {
                // The draw loop skips the whole iteration on a non-finite PRIMARY (the padding NaNs
                // Axes.Ticks appends to equalise list lengths), then guards the secondary label on
                // its own finiteness — mirror both, or a padded NaN row would be measured as a
                // label that is never actually drawn.
                if (!double.IsFinite(primary)) continue;
                double raw = secondary ? secondaryValue : primary;
                if (!double.IsFinite(raw)) continue;

                double v = Math.Abs(raw) < 1e-12 ? 0 : raw;
                max = Math.Max(max, font.MeasureText(v.ToString($"G{digits}")));
            }
            if (max > 0f) return max;

            var win = secondary ? axes.WindowSecondary : axes.Window;
            return Math.Max(font.MeasureText(win.Top   .ToString($"G{digits}")),
                            font.MeasureText(win.Bottom.ToString($"G{digits}")));
        }

        /// <summary>
        /// X anchors for the rotated per-trace Y-label columns: just outside the widest actual tick
        /// number on each side, plus <see cref="DescriptionStripPad"/>. Shared by the draw path and
        /// the hit-rect path — these were two hand-maintained copies of the same expression, which
        /// is exactly how a label and its own clickable region drift apart.
        /// </summary>
        /// <remarks>
        /// The <paramref name="tickFont"/> overload is the real implementation; the convenience
        /// overload below just supplies the tick font the renderer draws with. Taking the font as a
        /// parameter is also what makes this headlessly testable — <c>SkiaFonts.PlexRegular</c>
        /// cannot load without a live Avalonia platform (see src/Ui/CLAUDE.md), so a test passes
        /// <c>SKTypeface.Default</c> instead and still exercises the real geometry.
        /// </remarks>
        internal static (float Left, float Right) ComputeYLabelAnchors(
            SKFont tickFont, Axes axes, float vpLeft, float vpRight, float lw)
        {
            float leftTickW  = MaxYTickLabelWidth(tickFont, axes, secondary: false);
            float rightTickW = MaxYTickLabelWidth(tickFont, axes, secondary: true);
            return (vpLeft  - lw * 4f - leftTickW  - DescriptionStripPad * lw,
                    vpRight + lw * 4f + rightTickW + DescriptionStripPad * lw);
        }

        private static (float Left, float Right) ComputeYLabelAnchors(
            Axes axes, float vpLeft, float vpRight, float lw)
        {
            using var tickFont = new SKFont(SkiaFonts.PlexRegular, (float)(axes.FontSizeTicks * lw));
            return ComputeYLabelAnchors(tickFont, axes, vpLeft, vpRight, lw);
        }
    }
}
