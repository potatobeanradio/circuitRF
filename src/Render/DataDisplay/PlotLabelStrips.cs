// ================================================================
//  PlotLabelStrips.cs  —  which traces get a Y-axis label strip, and
//  what each strip says
//
//  RND-4 (R-rnd4-2/R-rnd4-7). The strips are CONTENT — R-rnd4-7 lists
//  them among the things that come out in an export, beside the markers
//  and the info boxes — so a headless render has to produce exactly the
//  set the window produces, on the same side, in the same order. That
//  rule was inside `PlotContainerViewModel.UpdateLabelStrips`, mixed in
//  with the strip VIEW MODELS it also builds (their width, their theme,
//  the on-screen AutoLabel that the export deliberately does not use).
//
//  Split so the rule is said once: the view model maps these onto its own
//  LabelStripViewModels, and `circuitrf render` places them directly.
//
//  Smith and Polar only — a Rect plot renders its Y-axis labels INSIDE
//  the Skia canvas margin and has no strips at all.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Render.DataDisplay;

public static class PlotLabelStrips
{
    /// <summary>
    /// The left and right strips for <paramref name="plot"/>.
    ///
    /// <para>A CUSTOM axis label collapses that side to ONE strip carrying the custom text — it
    /// names the axis, not a trace, so per-trace strips beside it would be saying two different
    /// things about the same axis. With no custom label there is one strip per trace on that side;
    /// contour traces are excluded, having no Y axis of their own.</para>
    ///
    /// <para><b>The back branch of a pattern cut is excluded for the same reason</b>
    /// (<see cref="Trace.MirrorPatternAngle"/>): it is the φ + 180° half of a cut whose front half
    /// is already on this axis, so it is one curve in two traces and not a second quantity. Left in,
    /// it printed the axis name twice. <b>Unless it is the only thing on that side</b> — a back
    /// branch with no front branch is an odd document, but it is one the reader must still be able
    /// to read an axis name off.</para>
    /// </summary>
    public static (IReadOnlyList<PlacedLabelStrip> Left, IReadOnlyList<PlacedLabelStrip> Right)
        For(Plot plot, bool showFilePrefix)
    {
        if (!plot.PlotType.IsComplex())
            return (Array.Empty<PlacedLabelStrip>(), Array.Empty<PlacedLabelStrip>());

        var leftTraces  = Labelled(plot.LeftAxisTraces);
        var rightTraces = Labelled(plot.RightAxisTraces);

        return (Side(leftTraces,  plot.CustomYLabelOn,  plot.CustomYLabel,  showFilePrefix),
                Side(rightTraces, plot.CustomY2LabelOn, plot.CustomY2Label, showFilePrefix));
    }

    /// <summary>The traces on one side that name the axis — see <see cref="For"/>.</summary>
    private static List<Trace> Labelled(IEnumerable<Trace> traces)
    {
        var all   = traces.Where(t => !t.IsContourTrace).ToList();
        var front = all.Where(t => !t.MirrorPatternAngle).ToList();
        return front.Count > 0 ? front : all;
    }

    private static IReadOnlyList<PlacedLabelStrip> Side(
        IReadOnlyList<Trace> traces, bool hasCustom, string? customLabel, bool showFilePrefix)
    {
        if (hasCustom)
            return traces.Count > 0
                ? [new PlacedLabelStrip(traces[0], customLabel, showFilePrefix)]
                : Array.Empty<PlacedLabelStrip>();

        return traces.Select(t => new PlacedLabelStrip(t, null, showFilePrefix)).ToList();
    }
}
