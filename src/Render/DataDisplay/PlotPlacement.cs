// ================================================================
//  PlotPlacement.cs  —  one plot's placement on a composed page
//
//  `circuitrf render`'s own placement of a saved plot, moved below the
//  firewall (brief-modelcomposer-15 R-mc15-7a) so the Model Composer's
//  report PDF — written by the window and by `model report --pdf` — is
//  composed by the same function rather than a copy of it. The label
//  strips, the marker boxes and the logical geometry are decided here
//  once, for every caller that arrives at a set of PlacedPlots.
// ================================================================

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static class PlotPlacement
{
    /// <summary>
    /// One plot as the composition needs it, at its LOGICAL coordinates — zoom 1, pan 0, which the composition
    /// normalizes away (<see cref="PlotComposer"/>'s header says where).
    /// </summary>
    /// <param name="hasMultipleSources">Whether a trace label carries its source's name.</param>
    /// <param name="aliasFor">A trace's source alias, or null for "no aliases".</param>
    /// <param name="overlay">Transient chrome drawn above this plot's traces (<see cref="PlacedPlot.Overlay"/>).</param>
    public static PlacedPlot Place(
        Plot plot, double left, double top, double width, double height, FreqUnit freqUnit,
        bool hasMultipleSources, Func<Trace, string?>? aliasFor,
        Action<SKCanvas, TransformSet, RenderTheme>? overlay = null)
    {
        double stripW = PlotCanvasGeometry.StripLogicalWidth(height);
        double topX   = PlotCanvasGeometry.TopLabelExtraLogical(plot, width);
        double botX   = PlotCanvasGeometry.BottomLabelExtraLogical(plot, width);
        // A rectangular plot's Y-label columns beyond the first sit outside its own width
        // (Plot.LabelColumnsOutside): the canvas starts left of `left` and is wider than `width`.
        double leftX  = PlotCanvasGeometry.LeftLabelExtraLogical(plot, width);
        double canvW  = width + leftX + PlotCanvasGeometry.RightLabelExtraLogical(plot, width);

        // Which traces get a strip is PlotLabelStrips' rule, the same one the window's own
        // container follows (RND-4 R-rnd4-7: the strips are content and come out in an export).
        var (leftStrips, rightStrips) = PlotLabelStrips.For(plot, hasMultipleSources);

        var boxes = new List<PlacedMarkerBox>();
        foreach (var t in plot.Traces)
        foreach (var m in t.Markers)
        {
            if (!m.ShowInfoBox) continue;

            // A marker whose box was never dragged has no stored position — the window places one
            // 15/10 px from the glyph the first time it builds the box, and PERSISTS it. A `.cdd`
            // written by the application therefore always carries a finite position; this is the
            // same rule for one that does not (a hand-written file, or a marker added by a client),
            // through the same function, so the two agree.
            var pos = double.IsNaN(m.InfoBoxPos.X) || double.IsNaN(m.InfoBoxPos.Y)
                ? PlotCanvasGeometry.DefaultInfoBoxPosition(
                      m, t, plot, canvW, height + topX + botX, left - leftX, top, zoom: 1.0)
                : m.InfoBoxPos;
            double bx = pos.X;
            double by = pos.Y;
            var (w, h) = MarkerRenderer.MeasureInfoBox(m, t, freqUnit,
                                                       hasMultipleSources, plot.Traces);
            boxes.Add(new PlacedMarkerBox(m, t, freqUnit, bx, by, w, h, plot.Traces));
        }

        // A floating legend sits where it was put, in the same logical space as the info boxes; one nobody placed goes
        // where the Inside box would be (LegendRenderer.DefaultFloatingPosition, the window's rule too).
        PlacedLegend? legend = null;
        if (LegendRenderer.Floats(plot))
        {
            double canvasH = height + topX + botX;
            double lw      = LegendRenderer.LineWidthFor(canvW, canvasH);
            var    pos     = double.IsNaN(plot.LegendPos.X) || double.IsNaN(plot.LegendPos.Y)
                ? LegendRenderer.DefaultFloatingPosition(plot, canvW, canvasH, left - leftX, top - topX, aliasFor, hasMultipleSources)
                : plot.LegendPos;
            var (w, h) = LegendRenderer.MeasureFloating(plot, lw, aliasFor, hasMultipleSources);
            legend = new PlacedLegend(pos.X, pos.Y, w, h, lw);
        }

        return new PlacedPlot
        {
            Plot                = plot,
            ViewLeft            = left - leftX,
            ViewTop             = top - topX,
            ViewWidth           = canvW,
            ViewHeight          = height + topX + botX,
            LogicalWidth        = width,
            LabelStripViewWidth = stripW,
            LeftLabelStrips     = leftStrips,
            RightLabelStrips    = rightStrips,
            MarkerBoxes         = boxes,
            Legend              = legend,
            ShowFilePrefix      = hasMultipleSources,
            AlwaysShowSource    = hasMultipleSources,
            AliasFor            = aliasFor,
            Overlay             = overlay,
        };
    }
}
