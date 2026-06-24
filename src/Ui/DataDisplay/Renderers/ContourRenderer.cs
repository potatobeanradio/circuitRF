// ================================================================
//  ContourRenderer.cs  —  Skia rendering for loadpull contours
//
//  All output is VECTOR (SKPath fills + gradient SKShaders), so it
//  exports crisply to PDF/SVG via SKDocument.CreatePdf / SKSvgCanvas
//  (canvas.DrawBitmap would embed a raster — deliberately avoided).
//
//  Three draw calls, all from PlotRenderer.Draw inside its viewport clip:
//
//    DrawTopoMapFill   — discrete colour bands as filled SKPaths, one
//                        per level threshold, built by "marching-squares
//                        fill" (per-cell ≥-threshold polygons). Band edges
//                        coincide with the iso-lines by construction;
//                        NaN cells drop out (clips to the Γ-disk).
//    DrawIsoLines      — polylines world→canvas via tf.ToCanvas;
//                        optional per-line level labels.
//    DrawHeatMapFill   — additive radial gradients (one per measured
//                        point) blended with SKBlendMode.Plus; density
//                        reads as heat. Pure vector (PDF radial shadings).
//
//  Draw order managed by PlotRenderer: fills first, lines over.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.Renderers;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    internal static class ContourRenderer
    {
        // lw at a nominal 400×400 (zoom=1) canvas: min(400,400)/200 = 2.0.
        // All contour element sizes are expressed as multiples of lw so they
        // scale identically to grid lines when the canvas (already zoom-scaled)
        // grows or shrinks.
        private const float BaseLw = 2.0f;
        // ================================================================
        //  TopoMap fill  (§3 in brief) — VECTOR band paths
        // ================================================================

        /// <summary>
        /// Fill discrete elevation bands as vector SKPaths.  For N level
        /// thresholds there are N+1 bands; we paint back-to-front: the whole
        /// (non-NaN) footprint in band 0's colour, then the region value≥Lk in
        /// band (k+1)'s OPAQUE colour for each ascending level.  Because higher
        /// thresholds nest inside lower ones, paint order yields correct bands
        /// whose edges land exactly on the iso-lines, and every pixel ends up a
        /// single band colour (no translucent stacking).  The whole stack is
        /// composited through one SaveLayer alpha so the Smith grid shows through
        /// uniformly underneath.  NaN cells are skipped, so the fill clips
        /// naturally to the Γ-disk.  All vector → exports to PDF/SVG.
        /// </summary>
        public static void DrawTopoMapFill(
            SKCanvas        canvas,
            SurfaceGrid     grid,
            ContourLevelSet levels,
            TransformSet    tf,
            ContourColorMap colorMap = ContourColorMap.Hot,
            SurfacePlane    plane    = SurfacePlane.Gamma)
        {
            if (levels.Levels.Length == 0) return;
            int res = grid.XSpace.Length;
            if (res < 2 || grid.YSpace.Length != res) return;

            int nBands  = levels.Levels.Length + 1;
            var palette = BuildPalette(nBands, colorMap);

            // §1: on Smith/Polar (Γ-plane) clip to the unit disk so the fill has a
            // clean circular edge rather than ragged NaN-cell gaps at the boundary.
            bool needsClip = plane == SurfacePlane.Gamma;
            if (needsClip)
            {
                canvas.Save();
                var center = tf.ToCanvas(0.0, 0.0, useSecondary: false);
                var edge   = tf.ToCanvas(1.0, 0.0, useSecondary: false);
                float radius = Math.Abs(edge.X - center.X) * 1.02f;
                using var circlePath = new SKPath();
                circlePath.AddCircle(center.X, center.Y, radius);
                canvas.ClipPath(circlePath, antialias: true);
            }

            // Composite all bands opaquely among themselves, then apply one global
            // alpha so the grid underneath shows through without inter-band blending.
            using var layerPaint = new SKPaint { Color = new SKColor(0, 0, 0, TopoLayerAlpha) };
            canvas.SaveLayer(layerPaint);

            // Band 0: the entire non-NaN footprint (region value ≥ −∞).
            using (var basePath = BuildGeqRegionPath(grid, double.NegativeInfinity, tf))
            using (var basePaint = new SKPaint { Color = palette[0], Style = SKPaintStyle.Fill, IsAntialias = true })
                canvas.DrawPath(basePath, basePaint);

            // Bands 1..N: region value ≥ levels[k], painted over, in ascending order.
            for (int k = 0; k < levels.Levels.Length; k++)
            {
                using var path  = BuildGeqRegionPath(grid, levels.Levels[k], tf);
                using var paint = new SKPaint { Color = palette[k + 1], Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawPath(path, paint);
            }

            canvas.Restore();  // pairs with SaveLayer

            if (needsClip)
                canvas.Restore();  // pairs with Save/ClipPath
        }

        /// <summary>Global translucency of the TopoMap fill over the grid (0–255).</summary>
        private const byte TopoLayerAlpha = 210;

        /// <summary>
        /// Build a vector SKPath covering every grid cell sub-region where the
        /// interpolated value is ≥ <paramref name="level"/>.  Per cell: classify
        /// the 4 corners (NaN corner ⇒ skip cell), then emit the polygon of the
        /// "inside" (≥level) sub-region using marching-squares geometry with
        /// linear edge interpolation.  Adjacent cells' polygons abut, so the
        /// union reads as one filled region; SKPath even-odd/winding handles the
        /// overlaps.  Coordinates mapped world→canvas via tf.ToCanvas.
        /// </summary>
        private static SKPath BuildGeqRegionPath(SurfaceGrid grid, double level, TransformSet tf)
        {
            int res  = grid.XSpace.Length;
            var path = new SKPath { FillType = SKPathFillType.Winding };

            // Scratch buffer for the per-cell polygon (max 8 vertices for a square cell).
            Span<SKPoint> poly = stackalloc SKPoint[8];

            for (int yi = 0; yi < res - 1; yi++)
            {
                for (int xi = 0; xi < res - 1; xi++)
                {
                    // Corner values (bl, br, tr, tl) — CCW from bottom-left.
                    double vBL = grid.Values[yi       * res + xi];
                    double vBR = grid.Values[yi       * res + xi + 1];
                    double vTR = grid.Values[(yi + 1) * res + xi + 1];
                    double vTL = grid.Values[(yi + 1) * res + xi];

                    // Any NaN corner ⇒ outside the Γ-disk; skip the whole cell.
                    if (double.IsNaN(vBL) || double.IsNaN(vBR) ||
                        double.IsNaN(vTR) || double.IsNaN(vTL))
                        continue;

                    // Corner world coords.
                    double xL = grid.XSpace[xi],     xR = grid.XSpace[xi + 1];
                    double yB = grid.YSpace[yi],      yT = grid.YSpace[yi + 1];

                    int n = BuildCellGeqPolygon(
                        level, vBL, vBR, vTR, vTL, xL, xR, yB, yT, tf, poly);

                    if (n >= 3)
                    {
                        path.MoveTo(poly[0]);
                        for (int i = 1; i < n; i++) path.LineTo(poly[i]);
                        path.Close();
                    }
                }
            }
            return path;
        }

        /// <summary>
        /// Compute the polygon of the ≥level sub-region of one grid cell and write
        /// its canvas-space vertices into <paramref name="outPoly"/>; returns the
        /// vertex count (0 if the cell is entirely below level).  Walks the cell
        /// boundary CCW (BL→BR→TR→TL), keeping every corner that is ≥level and
        /// inserting the linearly-interpolated crossing point on each edge where
        /// the inside/outside state changes.  This is the filled-region companion
        /// to marching-squares line extraction; shared edges between neighbouring
        /// cells use identical crossing points, so bands tile seamlessly and their
        /// outer edges coincide with the iso-lines.
        /// </summary>
        private static int BuildCellGeqPolygon(
            double level,
            double vBL, double vBR, double vTR, double vTL,
            double xL, double xR, double yB, double yT,
            TransformSet tf, Span<SKPoint> outPoly)
        {
            // Fast paths: all-in / all-out.
            bool inBL = vBL >= level, inBR = vBR >= level,
                 inTR = vTR >= level, inTL = vTL >= level;

            if (!inBL && !inBR && !inTR && !inTL) return 0;

            int n = 0;
            // Corner coords in world space.
            // Edge order CCW: BL→BR (bottom), BR→TR (right), TR→TL (top), TL→BL (left).
            // For each edge: if the start corner is inside, emit it; then if the
            // edge crosses the level, emit the crossing point.
            AddEdge(level, inBL, vBL, vBR, xL, yB, xR, yB, tf, outPoly, ref n); // bottom
            AddEdge(level, inBR, vBR, vTR, xR, yB, xR, yT, tf, outPoly, ref n); // right
            AddEdge(level, inTR, vTR, vTL, xR, yT, xL, yT, tf, outPoly, ref n); // top
            AddEdge(level, inTL, vTL, vBL, xL, yT, xL, yB, tf, outPoly, ref n); // left
            return n;
        }

        /// <summary>
        /// Append, for one cell edge (start→end), the start corner (if inside) and
        /// the level crossing point (if the edge changes inside/outside state).
        /// Linear interpolation t = (level − vStart)/(vEnd − vStart).
        /// </summary>
        private static void AddEdge(
            double level, bool startInside,
            double vStart, double vEnd,
            double xStart, double yStart, double xEnd, double yEnd,
            TransformSet tf, Span<SKPoint> outPoly, ref int n)
        {
            if (startInside)
                outPoly[n++] = tf.ToCanvas(xStart, yStart, useSecondary: false);

            bool endInside = vEnd >= level;
            if (startInside != endInside)
            {
                double denom = vEnd - vStart;
                double t     = denom != 0 ? (level - vStart) / denom : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double cx = xStart + t * (xEnd - xStart);
                double cy = yStart + t * (yEnd - yStart);
                outPoly[n++] = tf.ToCanvas(cx, cy, useSecondary: false);
            }
        }

        // ================================================================
        //  Iso-lines + labels  (§2 in brief)
        // ================================================================

        /// <summary>
        /// Stroke each polyline world-to-canvas and optionally label every line.
        /// Must be called inside PlotRenderer's viewport clip.
        /// </summary>
        public static void DrawIsoLines(
            SKCanvas                   canvas,
            (double W, double H)       canvasSize,
            IReadOnlyList<IsoPolyline> polylines,
            TransformSet               tf,
            SKColor                    lineColor,
            bool                       lineColorOverridden,
            float                      strokeWidth,
            bool                       drawLabels,
            SKColor                    labelBg,
            SKColor                    labelFg,
            double                     labelSpacing,
            ContourColorMap            colorMap        = ContourColorMap.Hot,
            float                      levelFontSize   = 9f,
            bool                       fadeLineOpacity = false)
        {
            if (polylines == null || polylines.Count == 0) return;

            // Scale stroke and font by canvas-proportional lw so they track zoom
            // identically to the Smith/Rect grid lines.
            float lw                = AxesRenderer.LineWidth(canvasSize);
            float effectiveStroke   = strokeWidth   * lw / BaseLw;
            float effectiveFontSize = levelFontSize * lw / BaseLw;

            // Pre-compute level range — needed for both §E colormap contrast and fade.
            double minLevel = double.MaxValue, maxLevel = double.MinValue;
            foreach (var pl in polylines)
            {
                if (pl.Level < minLevel) minLevel = pl.Level;
                if (pl.Level > maxLevel) maxLevel = pl.Level;
            }
            double levelRange = Math.Max(maxLevel - minLevel, 1e-9);

            using var linePaint = new SKPaint
            {
                StrokeWidth = effectiveStroke,
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeCap   = SKStrokeCap.Round,
                StrokeJoin  = SKStrokeJoin.Round,
            };
            using var labelFont  = new SKFont(SkiaFonts.PlexRegular, effectiveFontSize);
            using var labelPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var bgPaint    = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            using var bgStroke   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f };

            // Label-box padding (canvas px), scaled by lw so it tracks zoom like the font. Slightly
            // larger than the old fixed 2 px so the text never crowds the border (design feedback).
            // capHeight = the text's visual height (ascent above baseline + descent below); the box is
            // centered on the label anchor in BOTH axes and the baseline is placed so the glyphs sit in
            // the exact center of the box.
            var   labelMetrics = labelFont.Metrics;
            float labelPadX    = 4f * lw / BaseLw;
            float labelPadY    = 3f * lw / BaseLw;
            float capHeight    = labelMetrics.Descent - labelMetrics.Ascent;  // ascent is negative

            // §3 — label positions are world-unit based so they don't shift on zoom.
            // spacingW is in world coordinates; a canvas-px guard keeps labels off tiny paths.
            const float  minLabelLenPx = 30f;
            double spacingW = Math.Max(labelSpacing, 1e-6);

            // §5 — one line color for all iso-lines (per-level variation removed).
            // Derive a single high-contrast color from the colormap midpoint before the loop.
            SKColor baseLineColor = ResolveBaseLineColor(lineColor, lineColorOverridden, colorMap);

            int ringIndex = 0;  // stagger counter across all labelled polylines

            foreach (var pl in polylines)
            {
                var pts = pl.Points;
                if (pts.Count < 2) continue;

                // Apply fade alpha on top of base colour.
                float fadeF = 1.0f;
                if (fadeLineOpacity)
                {
                    double tFade = (maxLevel - pl.Level) / levelRange;  // 0=peak, 1=edge
                    fadeF = (float)Math.Max(0.0, 1.0 - tFade);
                }
                linePaint.Color  = baseLineColor.WithAlpha((byte)Math.Round(baseLineColor.Alpha * fadeF));
                labelPaint.Color = labelFg.WithAlpha((byte)Math.Round(labelFg.Alpha * fadeF));

                using var path = new SKPath();
                var p0 = tf.ToCanvas(pts[0].X, pts[0].Y, useSecondary: false);
                path.MoveTo(p0);
                for (int i = 1; i < pts.Count; i++)
                    path.LineTo(tf.ToCanvas(pts[i].X, pts[i].Y, useSecondary: false));
                if (pl.Closed) path.Close();
                canvas.DrawPath(path, linePaint);

                if (drawLabels)
                {
                    // Canvas-px guard: skip paths too short to read (zoom-invariant intent).
                    float length = PathCanvasLength(pts, tf);
                    if (length < minLabelLenPx) { ringIndex++; continue; }

                    string labelText = FormatLevel(pl.Level);
                    float  tw        = labelFont.MeasureText(labelText);

                    // Stagger start per ring in world-unit fractions.
                    double startFrac = 0.5 + 0.18 * ((ringIndex % 3) - 1);
                    startFrac = Math.Max(0.15, Math.Min(0.85, startFrac));
                    double targetArcW = startFrac * spacingW;

                    // §3 — walk in world coordinates; convert to canvas only when drawing.
                    double arcSoFarW  = 0.0;
                    double prevWx     = pts[0].X, prevWy = pts[0].Y;

                    for (int i = 1; i < pts.Count; i++)
                    {
                        double curWx  = pts[i].X, curWy = pts[i].Y;
                        double dx     = curWx - prevWx, dy = curWy - prevWy;
                        double segLen = Math.Sqrt(dx * dx + dy * dy);
                        double segEnd = arcSoFarW + segLen;

                        while (targetArcW <= segEnd)
                        {
                            double t_seg = segLen > 0.0 ? (targetArcW - arcSoFarW) / segLen : 0.0;
                            double pmWx  = prevWx + t_seg * dx;
                            double pmWy  = prevWy + t_seg * dy;
                            var    pm    = tf.ToCanvas(pmWx, pmWy, useSecondary: false);

                            bgPaint.Color  = labelBg.WithAlpha((byte)Math.Round(labelBg.Alpha * fadeF));
                            bgStroke.Color = new SKColor(0, 0, 0, (byte)Math.Round(120 * fadeF));

                            // Box centered on the label anchor (pm) in BOTH axes, with padded
                            // half-extents. Text is horizontally centered (SKTextAlign.Center at pm.X)
                            // and vertically centered: baseline offset places the glyph block's midpoint
                            // at pm.Y, so the text sits in the exact center of the box (design feedback).
                            float halfW = tw / 2f + labelPadX;
                            float halfH = capHeight / 2f + labelPadY;
                            var   bg    = new SKRect(pm.X - halfW, pm.Y - halfH,
                                                     pm.X + halfW, pm.Y + halfH);
                            canvas.DrawRect(bg, bgPaint);
                            canvas.DrawRect(bg, bgStroke);

                            float baselineY = pm.Y - (labelMetrics.Ascent + labelMetrics.Descent) / 2f;
                            canvas.DrawText(labelText, pm.X, baselineY,
                                            SKTextAlign.Center, labelFont, labelPaint);

                            targetArcW += spacingW;
                        }

                        arcSoFarW = segEnd;
                        prevWx    = curWx;
                        prevWy    = curWy;
                    }

                    ringIndex++;
                }
            }
        }

        private static byte LerpByte(byte a, byte b, double t)
            => (byte)Math.Round(a + (b - a) * t);

        /// <summary>
        /// The single iso-line color actually drawn: the user's <paramref name="lineColor"/> when
        /// <paramref name="lineColorOverridden"/>, otherwise a high-contrast color auto-derived from the
        /// colormap midpoint (luminance-inverted then capped at 0.45 so it stays readable). Shared with the
        /// trace card's color-swatch so the indicator matches the rendered lines.
        /// </summary>
        internal static SKColor ResolveBaseLineColor(SKColor lineColor, bool lineColorOverridden,
            ContourColorMap colorMap)
        {
            if (lineColorOverridden) return lineColor;

            var mapColor = ContourColormaps.Sample(colorMap, 0.5);
            float lum = (0.299f * mapColor.Red + 0.587f * mapColor.Green + 0.114f * mapColor.Blue) / 255f;
            byte hi = lum > 0.5f ? (byte)0 : (byte)255;
            byte r = LerpByte(mapColor.Red,   hi, 0.5);
            byte g = LerpByte(mapColor.Green, hi, 0.5);
            byte b = LerpByte(mapColor.Blue,  hi, 0.5);
            // §3: luminance ceiling — if the candidate is too light (Gray/Bone/Winter/GistHeat/Copper
            // midpoints land near 0.5 and can lerp toward white), scale down until luminance ≤ 0.45.
            float lineL = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            const float LumCeiling = 0.45f;
            if (lineL > LumCeiling)
            {
                float scale = LumCeiling / lineL;
                r = (byte)Math.Round(r * scale);
                g = (byte)Math.Round(g * scale);
                b = (byte)Math.Round(b * scale);
            }
            return new SKColor(r, g, b, lineColor.Alpha);
        }

        // ================================================================
        //  HeatMap fill  (§4 in brief — experimental) — VECTOR
        // ================================================================

        /// <summary>
        /// Density heat map of the measured scatter points, rendered as VECTOR:
        /// each point is a radial gradient (hot, semi-opaque core → transparent
        /// edge) drawn with SKBlendMode.Plus so overlapping points accumulate
        /// toward the hot end.  No bitmap — exports to PDF/SVG as radial shadings.
        /// Experimental; behind the fill-type selector.
        /// </summary>
        public static void DrawHeatMapFill(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            ScatterReduction     scatter,
            TransformSet         tf,
            ContourColorMap      colorMap    = ContourColorMap.Hot,
            float                pointRadius = 28f)
        {
            if (scatter.Coords.Length == 0) return;

            // Per-point bloom: additive radial gradients in the chosen colormap hue.
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                BlendMode   = SKBlendMode.Plus,
            };

            var coreRgb = ContourColormaps.Sample(colorMap, 1.0);
            var midRgb  = ContourColormaps.Sample(colorMap, 0.6);
            var colors = new SKColor[]
            {
                new SKColor(coreRgb.Red, coreRgb.Green, coreRgb.Blue, 120),
                new SKColor(midRgb.Red,  midRgb.Green,  midRgb.Blue,  70),
                new SKColor(0, 0, 0, 0),
            };
            var stops = new float[] { 0f, 0.5f, 1f };

            foreach (var coord in scatter.Coords)
            {
                var pt = tf.ToCanvas(coord.Real, coord.Imaginary, useSecondary: false);
                using var shader = SKShader.CreateRadialGradient(
                    pt, pointRadius, colors, stops, SKShaderTileMode.Clamp);
                paint.Shader = shader;
                canvas.DrawCircle(pt, pointRadius, paint);
            }
            paint.Shader = null;
        }

        // ================================================================
        //  Grid-point dots and optima markers
        // ================================================================

        /// <summary>Draw a small dot at each original measured loadpull point.</summary>
        public static void DrawGridPoints(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            ScatterReduction     scatter,
            TransformSet         tf,
            SKColor              color,
            float                pointRadius = 2.5f)
        {
            if (scatter.Coords.Length == 0) return;
            float lw              = AxesRenderer.LineWidth(canvasSize);
            float effectiveRadius = pointRadius * lw / BaseLw;
            using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (var coord in scatter.Coords)
            {
                var pt = tf.ToCanvas(coord.Real, coord.Imaginary, useSecondary: false);
                canvas.DrawCircle(pt, effectiveRadius, paint);
            }
        }

        /// <summary>Draw MXP / MXE circle markers if enabled and coords are available.</summary>
        public static void DrawOptimaMarkers(
            SKCanvas canvas, ContourData cd, TransformSet tf, (double W, double H) canvasSize)
        {
            if (cd.DisplayMxp && cd.MxpCoord is { } mxp)
                DrawOptimumMarker(canvas, mxp, 'P', MxpAccent(cd.ColorMap), tf, canvasSize);
            if (cd.DisplayMxe && cd.MxeCoord is { } mxe)
                DrawOptimumMarker(canvas, mxe, 'E', MxeAccent(cd.ColorMap), tf, canvasSize);
        }

        private static void DrawOptimumMarker(
            SKCanvas canvas, Complex coord, char letter, SKColor accent, TransformSet tf,
            (double W, double H) canvasSize)
        {
            var pt = tf.ToCanvas(coord.Real, coord.Imaginary, useSecondary: false);
            // lw-proportional sizes: at BaseLw=2 (nominal 400px canvas) these match
            // the original constants 7/1.5/9, and scale with zoom like grid lines.
            float lw = AxesRenderer.LineWidth(canvasSize);
            float r  = 3.5f * lw;
            float sw = 0.75f * lw;
            float fs = 4.5f * lw;

            // Filled circle
            using var fill = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle(pt, r, fill);

            // Black ring
            using var ring = new SKPaint
                { Color = SKColors.Black, StrokeWidth = sw, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawCircle(pt, r, ring);

            // Centered letter — luminance-based contrast colour
            float lum = (0.299f * accent.Red + 0.587f * accent.Green + 0.114f * accent.Blue) / 255f;
            var   textColor = lum > 0.5f ? SKColors.Black : SKColors.White;

            using var font    = new SKFont(SkiaFonts.PlexBold, fs);
            var       metrics = font.Metrics;
            float     baselineY = pt.Y - (metrics.Ascent + metrics.Descent) / 2f;

            using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
            canvas.DrawText(letter.ToString(), pt.X, baselineY, SKTextAlign.Center, font, textPaint);
        }

        private static SKColor MxpAccent(ContourColorMap colorMap)
            => EnsureBright(ContourColormaps.Sample(colorMap, 0.15));

        private static SKColor MxeAccent(ContourColorMap colorMap)
            => EnsureBright(ContourColormaps.Sample(colorMap, 0.85));

        private static SKColor EnsureBright(SKColor c)
        {
            // Mix 60% toward white so the accent is always legible on the fill.
            byte r = (byte)(c.Red   + (255 - c.Red)   * 0.6f);
            byte g = (byte)(c.Green + (255 - c.Green) * 0.6f);
            byte b = (byte)(c.Blue  + (255 - c.Blue)  * 0.6f);
            return new SKColor(r, g, b, 255);
        }

        // ================================================================
        //  Helpers
        // ================================================================

        // Build a palette of nBands OPAQUE colours from the selected colormap.
        // Opaque because DrawTopoMapFill composites bands through one SaveLayer
        // alpha (TopoLayerAlpha); per-band translucency would double-dip.
        private static SKColor[] BuildPalette(int nBands, ContourColorMap colorMap)
        {
            var c = new SKColor[nBands];
            for (int i = 0; i < nBands; i++)
            {
                double t = nBands > 1 ? (double)i / (nBands - 1) : 0.0;
                c[i] = ContourColormaps.Sample(colorMap, t);
            }
            return c;
        }

        private static float PathCanvasLength(IReadOnlyList<(double X, double Y)> pts, TransformSet tf)
        {
            float len  = 0f;
            var   prev = tf.ToCanvas(pts[0].X, pts[0].Y, useSecondary: false);
            for (int i = 1; i < pts.Count; i++)
            {
                var cur = tf.ToCanvas(pts[i].X, pts[i].Y, useSecondary: false);
                float dx = cur.X - prev.X, dy = cur.Y - prev.Y;
                len += MathF.Sqrt(dx * dx + dy * dy);
                prev = cur;
            }
            return len;
        }

        private static string FormatLevel(double level)
        {
            double abs = Math.Abs(level);
            if (abs >= 100 || abs == 0) return level.ToString("F0");
            if (abs >= 10)              return level.ToString("F1");
            return                             level.ToString("F2");
        }
    }
}
