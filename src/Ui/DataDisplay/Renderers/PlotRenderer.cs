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
using CircuitRF.Ui.Renderers;
using RfCore.Loadpull;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
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
        public Avalonia.Rect Viewport;

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

        // ---- WorldToCanvasParams ----------------------------------------

        public static (double XScale, double YScale, double XOffset, double YOffset)
            WorldToCanvasParams(Avalonia.Rect window, Avalonia.Rect viewport,
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

        private static Avalonia.Rect ComputeViewport(Plot plot, (double W, double H) canvasSize)
        {
            if (plot.PlotType.IsComplex() && canvasSize.W > 0 && canvasSize.H > 0)
            {
                double effectiveH = Math.Min(canvasSize.W, canvasSize.H);

                double availW = canvasSize.W * (1 - 2 * ComplexSideMargin);
                double availH = effectiveH   * (1 - ComplexTopMarginBase - ComplexBottomMargin);
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

                double viewportY = (ComplexTopMarginBase * effectiveH + topExtra) / canvasSize.H;

                return new Avalonia.Rect(
                    0.5 - fracW / 2,
                    viewportY,
                    fracW,
                    fracH);
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
            VswrReadout?         vswrReadout      = null)
        {
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
                    AxesRenderer.DrawPolarGrid(canvas, canvasSize, plot.Axes, tf, theme);
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
                    stemMode: plotIsRect && trace.IsHarmonicStem);
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
                    AxesRenderer.DrawTitleAndAxisLabels(canvas, canvasSize, plot, tf, theme, showFilePrefix);
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
                            // Full complex trace Z0 is the reference — never drop the imaginary part.
                            var z0Ref = trace.Z0 == System.Numerics.Complex.Zero
                                ? new System.Numerics.Complex(50.0, 0.0)
                                : trace.Z0;
                            canvas.Save();
                            canvas.ClipRect(viewportClip);
                            MarkerRenderer.DrawVswrLocus(canvas, canvasSize, marker, trace, tf, vplane, z0Ref);
                            canvas.Restore();
                        }
                        MarkerRenderer.DrawSymbol(canvas, canvasSize, marker, trace, tf, theme,
                            isSelected:     selectedMarkers?.Contains(marker) ?? false,
                            selectionColor: selectionColor);
                    }

                // ---- Live VSWR drag readout (unclipped — must not be cut off near the edge) ----
                if (vswrReadout is { } ro)
                {
                    using var font  = new SKFont(CircuitRF.Ui.Renderers.SkiaFonts.PlexBold,
                                                 (float)(Math.Min(canvasSize.W, canvasSize.H) * 0.0224));
                    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
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

        public static SKRect ViewportClipRect(Avalonia.Rect viewport, (double W, double H) canvas)
        {
            float l = (float)(viewport.X                    * canvas.W);
            float t = (float)(viewport.Y                    * canvas.H);
            float r = (float)((viewport.X + viewport.Width) * canvas.W);
            float b = (float)((viewport.Y + viewport.Height)* canvas.H);
            return new SKRect(l, t, r, b);
        }

        // ---- Watermark --------------------------------------------------

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
