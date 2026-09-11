// ================================================================
//  PlotCanvasGeometry.cs  —  how much canvas a plot occupies beyond its
//  own width and height
//
//  RND-4 (R-rnd4-2). These three were private properties on
//  `PlotContainerViewModel`, and they are what the exporter's bounding-box
//  fit is measured over — a Smith plot's canvas is TALLER than its Height
//  by however much its title and its overflow X-label rows need, and one
//  label strip is 5% of the plot height. Compose a page without them and
//  every Smith plot in it is framed differently from the one on screen.
//
//  They mirror formulas inside AxesRenderer.DrawComplexXLabels and
//  PlotRenderer.ComputeViewport, term for term, and each says which — the
//  comments are the originals'. That mirroring is exactly why there must
//  be ONE copy: a margin constant changed in the renderer and not here
//  silently clips a title.
// ================================================================

using System;

namespace CircuitRF.Render.DataDisplay;

public static class PlotCanvasGeometry
{
    /// <summary>
    /// Where a marker's info box sits when nobody has dragged it: 15 px right and 10 px down from
    /// the marker glyph, clamped inside the plot. Returned in the Data Display's LOGICAL
    /// coordinates, which is what <c>Marker.InfoBoxPos</c> stores and what a `.cdd` persists.
    /// </summary>
    /// <param name="canvasWidth">The plot canvas in SCREEN pixels — logical × zoom.</param>
    /// <param name="containerLeft">The plot's own logical position, which the result is relative to.</param>
    /// <param name="zoom">The canvas zoom; 1 where there is no canvas.</param>
    /// <remarks>
    /// RND-4 moved this out of <c>DataDisplayViewModel</c>. A never-dragged info box is drawn in an
    /// export exactly like a dragged one (R-rnd4-7 makes both content), so a headless render that
    /// placed it differently would move a box that is on screen — which is the class of difference
    /// this brief's byte-identity gate exists to make impossible.
    /// </remarks>
    public static PlotPoint DefaultInfoBoxPosition(
        Marker marker, Trace trace, Plot plot,
        double canvasWidth, double canvasHeight,
        double containerLeft, double containerTop,
        double zoom)
    {
        var tf = PlotRenderer.BuildTransforms(plot, (canvasWidth, canvasHeight));
        var dl = trace.GetMarkerDataLocation(marker);
        var px = tf.ToCanvas(dl.X, dl.Y, trace.UseSecondaryAxis);

        // Offset 15/10 px from the symbol, clamped inside the container bounds.
        double sxPx = Math.Clamp((double)px.X + 15, 0, Math.Max(0, canvasWidth  - 80));
        double syPx = Math.Clamp((double)px.Y + 10, 0, Math.Max(0, canvasHeight - 50));

        // PlotControl-local screen pixels -> Data Display logical coordinates:
        //   screen X  = containerLeft * zoom + offsetX + sxPx
        //   logical X = (screen X - offsetX) / zoom = containerLeft + sxPx / zoom
        double z = zoom > 0 ? zoom : 1.0;
        return new PlotPoint(containerLeft + sxPx / z, containerTop + syPx / z);
    }

    /// <summary>
    /// Logical (pre-zoom) width of one label strip.
    /// Proportional to plot height so the strip and its font scale with both
    /// user resize and zoom — identical behaviour to Rect Y-axis margin labels.
    /// </summary>
    public static double StripLogicalWidth(double height) => Math.Max(height * 0.05, 10.0);

    /// <summary>
    /// Extra logical height added below the chart to accommodate overflow
    /// X-axis label rows on Smith / Polar plots with multiple traces.
    ///
    /// Mirrors the font-size and row-height formulas in
    /// AxesRenderer.DrawComplexXLabels so the canvas is always
    /// exactly tall enough for every label row without clipping.
    ///
    /// Uses PlotRenderer public margin constants so that
    /// changing a margin in one place automatically keeps this in sync.
    ///
    /// Returns 0 for Rect plots (their X labels live inside the Skia margin).
    /// </summary>
    public static double BottomLabelExtraLogical(Plot plot, double width)
    {
        {
            if (!plot.PlotType.IsComplex()) return 0;

            bool hasCustomX = plot.CustomXLabelOn && !string.IsNullOrEmpty(plot.CustomXLabel);
            // ANT-7 §4: a pattern plot's rows are its CAPTION lines, not one per trace — the branch
            // DrawComplexXLabels takes first. Mirrored here for the same reason every other formula
            // in this file is: a row the canvas is not made tall enough for is a clipped sentence.
            int  n          = plot.IsPolarPattern ? Math.Max(1, PatternCaption.Lines(plot).Count)
                            : hasCustomX ? 1 : Math.Max(1, plot.Traces.Count);

            // Mirror DrawComplexXLabels: lw = min(W,H)/200.  Once extra height
            // is added H > W, so effectiveH = W → lw = W/200.
            double lw         = width / 200.0;
            // Must match DrawComplexXLabels: FontSizeLabel * lw = 8 * lw = h * 0.04
            // (mirrors AxisLabelControl's formula for the Y-axis strip labels).
            double fontSizePx = plot.Axes.FontSizeLabel * lw;
            if (fontSizePx < 4.0) return 0;   // matches DrawComplexXLabels guard

            double lineH = fontSizePx * 1.2;
            // Bottom edge of the last label row — must mirror DrawComplexXLabels exactly.
            // The 2.0 * lw term matches the downward nudge applied in the renderer;
            // change it here whenever you change the "+ 2f * lw" constant there.
            //
            // The BEARINGS push the rows further down when a polar plot prints them
            // (AxesRenderer.PolarBearingDropPx, which the renderer places the rows from). Without
            // this the canvas is sized for rows that start at the ring and the last line falls off
            // the bottom of a plot with "Angles" on.
            double bearingDrop = AxesRenderer.PolarBearingDropPx(plot, (float)lw);
            double rowsH = bearingDrop + lineH * (n - 0.2) + fontSizePx * 0.5 + 2.0 * lw;

            // Compute the natural bottom space below the chart circle for a square canvas.
            // Mirrors ComputeViewport exactly using the public margin constants — including the
            // margin the bearings take out of EVERY side, which shrinks the disc and therefore
            // leaves more room underneath it than the no-bearings case does.
            double bearings = plot.PlotType == PlotType.Polar && plot.ShowPolarAngleLabels
                ? PlotRenderer.ComplexAngleLabelMargin : 0.0;
            double availW  = width * (1.0 - 2.0 * PlotRenderer.ComplexSideMargin - 2.0 * bearings);
            double availH  = width * (1.0 - PlotRenderer.ComplexTopMarginBase - PlotRenderer.ComplexBottomMargin
                                          - 2.0 * bearings);
            double side    = Math.Min(availW, availH);
            double natural = width * (1.0 - PlotRenderer.ComplexTopMarginBase - bearings) - side;

            return Math.Max(0, rowsH - natural);
        }
    }

    /// <summary>
    /// Extra logical height added above the chart circle so the plot title
    /// is never clipped at the canvas top.
    ///
    /// When ViewHeight is grown by this amount,
    /// PlotRenderer.ComputeViewport shifts the chart circle DOWN
    /// by the same number of pixels (via the topExtra calculation), leaving the
    /// extra canvas pixels above the title text.  The chart circle is never
    /// resized — only the container height changes.
    ///
    /// Returns 0 when there is no title or the title fits in the natural
    /// PlotRenderer.ComplexTopMarginBase space.
    /// </summary>
    public static double TopLabelExtraLogical(Plot plot, double width)
    {
        {
            if (!plot.PlotType.IsComplex()) return 0;
            if (string.IsNullOrEmpty(plot.Title)) return 0;

            // Title is drawn at vpTop/2 + titleSz*0.35 (baseline).
            // Top of glyph: vpTop/2 − titleSz*0.65.
            // For no canvas clipping: vpTop ≥ titleSz * 1.3.
            // Natural top space = ComplexTopMarginBase * Width (same constant used
            // for circle sizing — no shrink, pure upward growth).
            double lw       = width / 200.0;
            double titleSz  = plot.Axes.FontSizeLabel * 1.4 * lw;
            double vpTopNat = PlotRenderer.ComplexTopMarginBase * width;
            return Math.Max(0, titleSz * 1.3 - vpTopNat);
        }
    }
}
