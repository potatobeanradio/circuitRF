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
using CircuitRF.Ui.Renderers;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    internal static class ContourRenderer
    {
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
            TransformSet    tf)
        {
            if (levels.Levels.Length == 0) return;
            int res = grid.XSpace.Length;
            if (res < 2 || grid.YSpace.Length != res) return;

            int nBands  = levels.Levels.Length + 1;
            var palette = BuildTopoPalette(nBands);

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

            canvas.Restore();
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
        /// Stroke each polyline world→canvas and optionally label every line.
        /// Must be called inside PlotRenderer's viewport clip.
        /// </summary>
        public static void DrawIsoLines(
            SKCanvas                   canvas,
            (double W, double H)       canvasSize,
            IReadOnlyList<IsoPolyline> polylines,
            TransformSet               tf,
            SKColor                    lineColor,
            float                      strokeWidth,
            bool                       drawLabels)
        {
            if (polylines == null || polylines.Count == 0) return;

            using var linePaint = new SKPaint
            {
                Color       = lineColor,
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeCap   = SKStrokeCap.Round,
                StrokeJoin  = SKStrokeJoin.Round,
            };

            using var labelFont  = new SKFont(SkiaFonts.PlexRegular, 8f);
            using var labelPaint = new SKPaint
            {
                Color       = lineColor,
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
            };
            using var bgPaint = new SKPaint
            {
                Color       = new SKColor(0, 0, 0, 140),
                IsAntialias = false,
                Style       = SKPaintStyle.Fill,
            };

            float minLabelLen = 30f;  // skip label on polylines shorter than this (canvas px)

            foreach (var pl in polylines)
            {
                var pts = pl.Points;
                if (pts.Count < 2) continue;

                using var path = new SKPath();
                var p0 = tf.ToCanvas(pts[0].X, pts[0].Y, useSecondary: false);
                path.MoveTo(p0);
                for (int i = 1; i < pts.Count; i++)
                    path.LineTo(tf.ToCanvas(pts[i].X, pts[i].Y, useSecondary: false));
                if (pl.Closed) path.Close();

                canvas.DrawPath(path, linePaint);

                if (drawLabels)
                {
                    int mid = pts.Count / 2;
                    var pm  = tf.ToCanvas(pts[mid].X, pts[mid].Y, useSecondary: false);

                    float length = PathCanvasLength(pts, tf);
                    if (length < minLabelLen) continue;

                    string label = FormatLevel(pl.Level);
                    float tw = labelFont.MeasureText(label);
                    float th = 8f;
                    var  bg  = new SKRect(pm.X - tw / 2 - 2, pm.Y - th - 1, pm.X + tw / 2 + 2, pm.Y + 2);

                    canvas.DrawRect(bg, bgPaint);
                    canvas.DrawText(label, pm.X, pm.Y, SKTextAlign.Center, labelFont, labelPaint);
                }
            }
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
            float                pointRadius = 28f)
        {
            if (scatter.Coords.Length == 0) return;

            // Per-point bloom: a translucent warm radial gradient. Additive blend
            // means dense clusters sum to brighter/hotter; sparse points stay dim.
            // Colours chosen so a single point reads cool-ish and overlaps saturate
            // toward red (the classic density ramp), achieved purely by additive RGB.
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                BlendMode   = SKBlendMode.Plus,
            };

            // Gradient stops: warm core fading to transparent. Alpha kept modest so
            // ~3-4 overlaps approach full saturation.
            var colors = new SKColor[]
            {
                new SKColor(180,  40,  10, 120),  // core (adds red strongly)
                new SKColor( 60,  90,  20,  70),  // mid  (adds some green → yellows on overlap)
                new SKColor(  0,   0,   0,   0),  // transparent edge
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
        //  Helpers
        // ================================================================

        // Build a palette of nBands OPAQUE colours: blue (low) → red (high).
        // Opaque because DrawTopoMapFill composites bands through one SaveLayer
        // alpha (TopoLayerAlpha); per-band translucency would double-dip.
        private static SKColor[] BuildTopoPalette(int nBands)
        {
            var c = new SKColor[nBands];
            for (int i = 0; i < nBands; i++)
            {
                float t = nBands > 1 ? (float)i / (nBands - 1) : 0f;
                // Hue 240° (blue) → 0° (red) as value increases.
                float hue = (1f - t) * 240f;
                var (r, g, b) = HsvToRgb(hue, 0.85f, 0.90f);
                c[i] = new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
            }
            return c;
        }

        private static (float R, float G, float B) HsvToRgb(float h, float s, float v)
        {
            h = ((h % 360f) + 360f) % 360f;
            float c = v * s;
            float x = c * (1f - Math.Abs(h / 60f % 2f - 1f));
            float m = v - c;
            float r, g, b;
            if      (h <  60f) { r = c; g = x; b = 0; }
            else if (h < 120f) { r = x; g = c; b = 0; }
            else if (h < 180f) { r = 0; g = c; b = x; }
            else if (h < 240f) { r = 0; g = x; b = c; }
            else if (h < 300f) { r = x; g = 0; b = c; }
            else               { r = c; g = 0; b = x; }
            return (r + m, g + m, b + m);
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
