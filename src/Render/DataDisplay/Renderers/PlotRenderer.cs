// ================================================================
//  PlotRenderer.cs  —  Coordinate transforms and top-level draw
//
//  Ported from splotRF/src/Renderers/PlotRenderer.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay; watermark text changed to
//  "circuitRF"; font seam retargeted to IBM Plex (PlexBold).
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay
{
    // ============================================================
    //  VswrReadout  —  transient drag-readout payload
    // ============================================================

    /// <summary>
    /// Transient readout shown next to the pointer while the user drags a VSWR locus.
    /// Not part of the persistent Plot model — built from drag state and discarded on release.
    /// </summary>
    public readonly record struct VswrReadout(string Text, SkiaSharp.SKPoint PointerPx);

    // ============================================================
    //  TransformSet
    // ============================================================

    public struct TransformSet
    {
        /// <summary>Primary axis world→canvas linear map.</summary>
        public (double XScale, double YScale, double XOffset, double YOffset) Primary;

        /// <summary>Secondary axis world→canvas linear map.</summary>
        public (double XScale, double YScale, double XOffset, double YOffset) Secondary;

        public (double W, double H) CanvasSize;

        /// <summary>
        /// Fractional viewport used to build this transform set.
        /// Always use this, not plot.Axes.Viewport, inside renderers.
        /// </summary>
        public PlotRect Viewport;

        // ---- Mapping helpers --------------------------------------------

        public SKPoint PrimaryToCanvas(float wx, float wy) =>
            new SKPoint(
                (float)(wx * Primary.XScale   + Primary.XOffset),
                (float)(wy * Primary.YScale   + Primary.YOffset));

        public SKPoint PrimaryToCanvas(double wx, double wy) =>
            PrimaryToCanvas((float)wx, (float)wy);

        public SKPoint SecondaryToCanvas(float wx, float wy) =>
            new SKPoint(
                (float)(wx * Secondary.XScale + Secondary.XOffset),
                (float)(wy * Secondary.YScale + Secondary.YOffset));

        public SKPoint SecondaryToCanvas(double wx, double wy) =>
            SecondaryToCanvas((float)wx, (float)wy);

        public SKPoint ToCanvas(double wx, double wy, bool useSecondary) =>
            useSecondary ? SecondaryToCanvas(wx, wy) : PrimaryToCanvas(wx, wy);

        public (double Wx, double Wy) PrimaryFromCanvas(float cx, float cy) =>
            ((cx - Primary.XOffset)   / Primary.XScale,
             (cy - Primary.YOffset)   / Primary.YScale);

        public (double Wx, double Wy) SecondaryFromCanvas(float cx, float cy) =>
            ((cx - Secondary.XOffset) / Secondary.XScale,
             (cy - Secondary.YOffset) / Secondary.YScale);
    }

    // ============================================================
    //  PlotRenderer
    // ============================================================

    public static class PlotRenderer
    {
        // ---- Complex-plot viewport margin constants ---------------------

        /// <summary>Fractional side (left + right) margin around the chart circle.</summary>
        public const double ComplexSideMargin = 0.01;

        /// <summary>Fractional bottom margin below the chart circle.</summary>
        public const double ComplexBottomMargin = 0.01;

        /// <summary>Fractional top margin above the chart circle.</summary>
        public const double ComplexTopMarginBase = 0.01;

        /// <summary>
        /// <b>Extra fractional margin on every side when a polar plot prints its bearings</b>
        /// (<see cref="Plot.ShowPolarAngleLabels"/>). The disc SHRINKS to make room rather than the
        /// numbers being drawn over the outermost ring: on a pattern plot the outer ring is the
        /// reference and a trace sits ON it at the peak, so an overprinted "90" would land on the
        /// one sample the reader is looking for. 0.055 is what a three-digit bearing needs at the
        /// tick font's own size, measured at the 420-point default box.
        /// </summary>
        public const double ComplexAngleLabelMargin = 0.055;

        // ---- WorldToCanvasParams ----------------------------------------

        public static (double XScale, double YScale, double XOffset, double YOffset)
            WorldToCanvasParams(PlotRect window, PlotRect viewport,
                                (double W, double H) canvas)
        {
            double vpLeft   = viewport.X      * canvas.W;
            double vpTop    = viewport.Y      * canvas.H;
            double vpWidth  = viewport.Width  * canvas.W;
            double vpHeight = viewport.Height * canvas.H;

            double xScale  =  vpWidth  / window.Width;
            double xOffset =  vpLeft   - window.Left * xScale;
            double yScale  = -vpHeight / window.Height;
            double yOffset =  vpTop + vpHeight + window.Top * (vpHeight / window.Height);

            return (xScale, yScale, xOffset, yOffset);
        }

        /// <summary>Builds the complete TransformSet for a plot at the given canvas size.</summary>
        public static TransformSet BuildTransforms(Plot plot, (double W, double H) canvasSize)
        {
            var vp = ComputeViewport(plot, canvasSize);
            return new TransformSet
            {
                Primary    = WorldToCanvasParams(plot.Axes.Window,          vp, canvasSize),
                Secondary  = WorldToCanvasParams(plot.Axes.WindowSecondary, vp, canvasSize),
                CanvasSize = canvasSize,
                Viewport   = vp
            };
        }

        private static PlotRect ComputeViewport(Plot plot, (double W, double H) canvasSize)
        {
            if (plot.PlotType.IsComplex() && canvasSize.W > 0 && canvasSize.H > 0)
            {
                double effectiveH = Math.Min(canvasSize.W, canvasSize.H);

                // The bearings live OUTSIDE the boundary ring, so the room for them comes out of
                // the viewport — which is the one place that makes the clip, the transform, the
                // ring lattice and the autoscale all agree about where the disc's edge is.
                double bearings = plot.PlotType == PlotType.Polar && plot.ShowPolarAngleLabels
                    ? ComplexAngleLabelMargin : 0.0;

                double availW = canvasSize.W * (1 - 2 * ComplexSideMargin - 2 * bearings);
                double availH = effectiveH   * (1 - ComplexTopMarginBase - ComplexBottomMargin
                                                  - 2 * bearings);
                double side   = Math.Min(availW, availH);
                double fracW  = side / canvasSize.W;
                double fracH  = side / canvasSize.H;

                double topExtra = 0;
                if (!string.IsNullOrEmpty(plot.Title))
                {
                    double titleSz   = plot.Axes.FontSizeLabel * 1.4 * effectiveH / 200.0;
                    double vpTopBase = ComplexTopMarginBase * effectiveH;
                    topExtra = Math.Max(0, titleSz * 1.3 - vpTopBase);
                }

                double viewportY = ((ComplexTopMarginBase + bearings) * effectiveH + topExtra)
                                   / canvasSize.H;

                return new PlotRect(
                    0.5 - fracW / 2,
                    viewportY,
                    fracW,
                    fracH);
            }
            // Rect: the X label lives in the bottom margin, and a "plot versus" plot can need MORE
            // than one row there (one per trace, when their X quantities differ). Give the extra
            // rows their room by shrinking the plot box rather than letting them fall off the
            // canvas — capped, so a 20-trace plot cannot squeeze the box to nothing.
            if (plot.PlotType == PlotType.Rect && plot.XLabelsDiffer
                && canvasSize.W > 0 && canvasSize.H > 0)
            {
                int rows = plot.XLabelTraces.Count;
                if (rows > 1)
                {
                    double lw   = Math.Min(canvasSize.W, canvasSize.H) / 200.0;
                    double rowH = plot.Axes.FontSizeTicks * 0.9 * lw * 1.25 / canvasSize.H;
                    var v = plot.Axes.Viewport;
                    double shrink = Math.Min((rows - 1) * rowH, v.Height * 0.4);
                    return new PlotRect(v.X, v.Y, v.Width, v.Height - shrink);
                }
            }
            return plot.Axes.Viewport;
        }

        // ---- Draw — top-level entry point -------------------------------

        public static void Draw(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            PlotDetail           detail,
            RenderTheme          theme,
            bool                 showFilePrefix   = true,
            float                watermarkOpacity = 0.06f,
            HashSet<Marker>?     selectedMarkers  = null,
            SKColor              selectionColor   = default,
            float                zoomLevel        = 1f,
            VswrReadout?         vswrReadout      = null,
            Func<Trace, string?>? aliasFor        = null,
            bool?                 alwaysShowSource = null)
        {
            // ANT-10: the 3D pattern surface has no world window, so it leaves BEFORE
            // BuildTransforms — the same seam, and for the same reason, as the Table above it
            // (brief-antenna-10-pattern-3d.md §2: every plot-kind seam here is an early return or an
            // additive switch case, never a two-armed if).
            if (plot.PlotType == PlotType.Surface3D)
            {
                SurfaceRenderer.Draw(canvas, canvasSize, plot, detail, theme,
                    aliasFor:         aliasFor,
                    alwaysShowSource: alwaysShowSource);
                return;
            }

            if (plot.PlotType == PlotType.Table)
            {
                TableRenderer.Draw(canvas, canvasSize, plot, theme,
                    zoomLevel:       zoomLevel,
                    showFilePrefix:  showFilePrefix,
                    selectedMarkers: selectedMarkers,
                    selectionColor:  selectionColor);
                return;
            }

            var tf = BuildTransforms(plot, canvasSize);

            // ---- Grid ----
            switch (plot.PlotType)
            {
                case PlotType.Rect:
                    AxesRenderer.DrawRectGrid(canvas, canvasSize, plot.Axes, tf, detail, theme);
                    break;
                case PlotType.Polar:
                    AxesRenderer.DrawPolarGrid(canvas, canvasSize, plot.Axes, tf, theme, plot.PatternScale,
                                               plot.ShowPolarAngleLabels);
                    break;
                case PlotType.Smith:
                    AxesRenderer.DrawSmithGrid(canvas, canvasSize, plot.Axes, tf, theme);
                    break;
            }

            // ---- Watermark ----
            if (plot.ShowWatermark && watermarkOpacity > 0)
                DrawWatermark(canvas, canvasSize, plot, watermarkOpacity);

            // ---- Clip to plot area (traces + multi-marker lines) ----
            canvas.Save();
            canvas.ClipRect(ViewportClipRect(tf.Viewport, canvasSize));

            // ---- Contour fill pre-pass (under all traces) ---------------
            foreach (var trace in plot.Traces)
            {
                var cd = trace.ContourData;
                if (cd == null) continue;
                if (cd.Grid == null) continue;

                switch (cd.FillType)
                {
                    case ContourFillType.TopoMap:
                        if (cd.Levels.Levels.Length > 0)
                        {
                            var contourPlane = plot.PlotType is PlotType.Smith or PlotType.Polar
                                ? SurfacePlane.Gamma : SurfacePlane.Z;
                            // §1: for Smith/Polar use the disk-covering fill grid so the fill
                            // reaches the circular clip edge; iso-lines use cd.Grid (recommended box).
                            var fillGrid = cd.FillGrid ?? cd.Grid;
                            ContourRenderer.DrawTopoMapFill(canvas, fillGrid, cd.Levels, tf, cd.ColorMap, contourPlane);
                        }
                        break;

                    case ContourFillType.HeatMap:
                        if (cd.Scatter is { } sc)
                            ContourRenderer.DrawHeatMapFill(canvas, canvasSize, sc, tf, cd.ColorMap);
                        break;
                }
            }

            // ---- WSProbe margin reference lines (WSP-4 R-wsp4-7) ---------
            //
            //  UNDER the curves, because they are a scale the curve is read against and not
            //  something drawn on top of it. Two, and they say different kinds of thing:
            //
            //   • the run's own MarginThreshold (dashed) — a SETTING, the level below which the run
            //     itself reported the probe as worth looking at;
            //   • the −12 dB floor (lighter) — a FACT: a node with positive resistance on both
            //     sides never reads below it, so anything under that line certifies negative
            //     resistance on one side (WSP-9 §2.2, overview D-16).
            //
            //  Drawn once per distinct level, not once per trace: two margin traces on one plot
            //  share the same floor, and stacking two identical strokes darkens it.
            if (plot.PlotType == PlotType.Rect)
                DrawWspMarginReferenceLines(canvas, canvasSize, plot, tf, theme);

            // ---- WSProbe critical points, on a polar plot (R-wsp4-7) -----
            //
            //  The two families read against DIFFERENT points: a driving-point locus (1/H0, 1/Y0)
            //  is read for a clockwise crossing of the negative real axis about the ORIGIN, and a
            //  loop gain is read about +1. Both are drawn, once, when the plot carries a WSProbe
            //  trace at all — a reader who has to remember which one applies is a reader who will
            //  occasionally use the wrong one.
            if (plot.PlotType == PlotType.Polar && plot.Traces.Any(t => t.IsWspTrace))
                DrawWspCriticalPoints(canvas, tf, theme);

            // ---- Traces --------------------------------------------------
            bool plotIsRect = plot.PlotType == PlotType.Rect;
            foreach (var trace in plot.Traces)
            {
                if (trace.IsContourTrace)
                {
                    var cd = trace.ContourData!;
                    var polylines = cd.GetPolylines();
                    if (polylines != null && cd.ShowIsoLines)
                        ContourRenderer.DrawIsoLines(
                            canvas, canvasSize, polylines, tf,
                            cd.LineColor, cd.LineColorOverridden, cd.StrokeWidth, cd.DrawLabels,
                            cd.LabelBackground, cd.LabelForeground, cd.LabelSpacing,
                            cd.ColorMap, (float)cd.LevelFontSize, cd.FadeLineOpacity);
                    if (cd.DisplayGridPoints && cd.Scatter is { } scPts)
                        ContourRenderer.DrawGridPoints(canvas, canvasSize, scPts, tf, cd.GridPointColor, (float)cd.GridPointSize);
                    ContourRenderer.DrawOptimaMarkers(canvas, cd, tf, canvasSize);
                    continue;
                }
                TraceRenderer.Draw(canvas, canvasSize, trace, tf, theme,
                    stemMode: plotIsRect && (trace.IsHarmonicStem || trace.IsMixIndexStem));
            }

            if (plot.PlotType == PlotType.Rect)
                foreach (var trace in plot.Traces)
                    foreach (var marker in trace.Markers)
                        if (marker.IsMulti)
                            MarkerRenderer.DrawMultiMarkerLine(canvas, canvasSize, marker, trace, tf, theme);

            canvas.Restore();

            // ---- Title, x-axis label, and global Y labels ----
            if (detail == PlotDetail.Full)
            {
                if (plot.PlotType == PlotType.Rect)
                    AxesRenderer.DrawTitleAndAxisLabels(canvas, canvasSize, plot, tf, theme, aliasFor, alwaysShowSource);
                else if (plot.PlotType.IsComplex())
                    AxesRenderer.DrawComplexXLabels(canvas, canvasSize, plot, tf, theme);
            }

            if (detail == PlotDetail.Full)
            {
                var viewportClip = ViewportClipRect(tf.Viewport, canvasSize);
                foreach (var trace in plot.Traces)
                    foreach (var marker in trace.Markers)
                    {
                        if (marker.VswrEnabled && VswrAvailableFor(plot, trace, marker))
                        {
                            var vplane = plot.PlotType is PlotType.Smith or PlotType.Polar
                                ? SurfacePlane.Gamma : SurfacePlane.Z;
                            // Full complex marker reference — never drop the imaginary part, and use
                            // the reference the DISPLAYED data actually carries (MarkerZ0: the
                            // port's own Z0 when the trace's Z0 Override is off).
                            var z0Ref = trace.MarkerZ0 == System.Numerics.Complex.Zero
                                ? new System.Numerics.Complex(50.0, 0.0)
                                : trace.MarkerZ0;
                            canvas.Save();
                            canvas.ClipRect(viewportClip);
                            MarkerRenderer.DrawVswrLocus(canvas, canvasSize, marker, trace, tf, vplane, z0Ref);
                            canvas.Restore();
                        }
                        // The marker glyph is clipped to the plot box, like the trace it is
                        // attached to. Panning moves a marker's DATA POINT out of view; drawn
                        // unclipped its triangle and name kept going, over the tick labels and
                        // outside the axes entirely. On Smith/Polar the viewport is the SQUARE
                        // that bounds the chart circle, so this clip still lets a marker sit in
                        // the corners outside the circle — which is what those plots want.
                        canvas.Save();
                        canvas.ClipRect(viewportClip);
                        MarkerRenderer.DrawSymbol(canvas, canvasSize, marker, trace, tf, theme,
                            isSelected:     selectedMarkers?.Contains(marker) ?? false,
                            selectionColor: selectionColor);
                        canvas.Restore();
                    }

                // ---- Live VSWR drag readout (unclipped — must not be cut off near the edge) ----
                if (vswrReadout is { } ro)
                {
                    using var font  = new SKFont(SkiaFonts.PlexRegular,
                                                 (float)(Math.Min(canvasSize.W, canvasSize.H) * 0.0224));
                    // Theme text colour — the SAME one MarkerInfoBox draws its lines in. It was a
                    // hardcoded black, which is invisible against a dark-theme plot background.
                    using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };
                    canvas.DrawText(ro.Text, ro.PointerPx.X + 10f, ro.PointerPx.Y - 10f,
                                    SKTextAlign.Left, font, paint);
                }
            }
        }

        // §6.1: VSWR locus is available only when the marker has a well-defined Z/Γ value.
        //  - Smith/Polar plot: any marker on a complex plane qualifies.
        //  - Rect plot: only a contour marker (Z-plane); ordinary Rect traces are Cartesian (no Z/Γ).
        //  - Table excluded (tables don't reach this renderer path).
        internal static bool VswrAvailableFor(Plot plot, Trace trace, Marker marker)
        {
            if (plot.PlotType is PlotType.Smith or PlotType.Polar) return true;
            if (plot.PlotType == PlotType.Rect) return trace.IsContourTrace;
            return false;
        }

        // ---- Viewport clip rect -----------------------------------------

        public static SKRect ViewportClipRect(PlotRect viewport, (double W, double H) canvas)
        {
            float l = (float)(viewport.X                    * canvas.W);
            float t = (float)(viewport.Y                    * canvas.H);
            float r = (float)((viewport.X + viewport.Width) * canvas.W);
            float b = (float)((viewport.Y + viewport.Height)* canvas.H);
            return new SKRect(l, t, r, b);
        }

        // ---- Watermark --------------------------------------------------

        /// <summary>The origin and +1 as small reference marks on a polar plot carrying a WSProbe
        /// trace. See the call site for why both.</summary>
        private static void DrawWspCriticalPoints(SKCanvas canvas, TransformSet tf, RenderTheme theme)
        {
            using var paint = new SKPaint
            {
                Color = theme.TickColor.WithAlpha(150), StrokeWidth = 1f,
                IsAntialias = true, Style = SKPaintStyle.Stroke,
            };
            foreach (double re in new[] { 0.0, 1.0 })
            {
                var p = tf.PrimaryToCanvas(re, 0.0);
                canvas.DrawLine(p.X - 4f, p.Y, p.X + 4f, p.Y, paint);
                canvas.DrawLine(p.X, p.Y - 4f, p.X, p.Y + 4f, paint);
            }
        }

        /// <summary>
        /// The threshold and the −12 dB floor beneath every WSProbe margin trace on a rectangular
        /// plot. No-op when the plot carries none. See the call site for what each line means.
        /// </summary>
        private static void DrawWspMarginReferenceLines(
            SKCanvas canvas, (double W, double H) canvasSize, Plot plot, TransformSet tf,
            RenderTheme theme)
        {
            var drawn = new HashSet<(double Level, bool Secondary, bool Dashed)>();
            var clip  = ViewportClipRect(tf.Viewport, canvasSize);

            foreach (var trace in plot.Traces)
            {
                if (!trace.ShowsWspMarginReferenceLines) continue;

                // The floor first, so the dashed threshold draws over it when the run set −12.
                Line(trace, WspMarginFloorDb, dashed: false, alpha: 70);
                if (double.IsFinite(trace.WspMarginThresholdDb))
                    Line(trace, trace.WspMarginThresholdDb, dashed: true, alpha: 140);
            }

            void Line(Trace trace, double db, bool dashed, byte alpha)
            {
                double level = trace.WspMarginLevelInDisplayUnits(db);
                if (!double.IsFinite(level)) return;
                if (!drawn.Add((level, trace.UseSecondaryAxis, dashed))) return;

                var p0 = tf.ToCanvas(0, level, trace.UseSecondaryAxis);
                if (p0.Y < clip.Top || p0.Y > clip.Bottom) return;   // outside the framed window

                using var paint = new SKPaint
                {
                    Color       = theme.TickColor.WithAlpha(alpha),
                    StrokeWidth = 1f,
                    IsAntialias = true,
                    Style       = SKPaintStyle.Stroke,
                };

                if (!dashed)
                {
                    canvas.DrawLine(clip.Left, p0.Y, clip.Right, p0.Y, paint);
                    return;
                }

                // The dash is drawn as SEGMENTS rather than through SKPathEffect.CreateDash,
                // because Skia's SVG device does not honour a path effect on a stroke: the line
                // simply does not appear in the file, while the identical call renders on the PNG
                // and PDF backends. Emitting the geometry keeps the three exports agreeing, which
                // is the property `render`'s own byte-identity gate rests on.
                const float on = 5f, off = 4f;
                for (float x = clip.Left; x < clip.Right; x += on + off)
                    canvas.DrawLine(x, p0.Y, Math.Min(x + on, clip.Right), p0.Y, paint);
            }
        }

        /// <summary>
        /// The floor a passive node cannot go below (WSP-9 §2.2): with positive resistance on both
        /// sides of the node the margin is bounded below by 0.25, which is −12 dB at overview
        /// D-16's convention. Below it, one side presents negative resistance.
        /// </summary>
        public const double WspMarginFloorDb = -12.0;

        private static void DrawWatermark(
            SKCanvas             canvas,
            (double W, double H) canvasSize,
            Plot                 plot,
            float                opacity)
        {
            const string text = "circuitRF";
            float size = (float)(canvasSize.W * 0.45 * (text.Length <= 3 ? 1.0 : 0.5));

            using var font  = new SKFont(SkiaFonts.PlexBold, size);
            using var paint = new SKPaint
            {
                Color       = RenderTheme.WithOpacity(SKColors.Gray, opacity),
                IsAntialias = true
            };

            double vpCx = (plot.Axes.Viewport.X + plot.Axes.Viewport.Width  / 2) * canvasSize.W;
            double vpCy = (plot.Axes.Viewport.Y + plot.Axes.Viewport.Height / 2) * canvasSize.H;

            float tw = font.MeasureText(text);
            canvas.DrawText(text,
                (float)(vpCx - tw / 2),
                (float)(vpCy + size / 3),
                SKTextAlign.Left, font, paint);
        }
    }
}
