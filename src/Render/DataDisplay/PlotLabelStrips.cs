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
    /// <para><b>The back branch of a pattern cut is dropped only when it REPEATS a label already on
    /// that axis</b> (<see cref="Trace.MirrorPatternAngle"/>). It is the φ + 180° half of a cut whose
    /// front half is on the same axis, so when the two carry one label they are one curve in two
    /// traces and not a second quantity — left in, the strip printed the axis name twice.</para>
    ///
    /// <para><b>The "repeats a label" half is the fix to a reported bug</b> — 2026-09-11: ticking
    /// the back-half checkbox made the Y-axis label appear and disappear with it, and the report
    /// asked whether that was intended. It was not. The flag alone was read, so ticking the box
    /// DELETED that trace's
    /// strip whatever the trace was, and a label appearing and disappearing as a checkbox moves
    /// reads as a fault whatever the reason behind it. The ordinary way to build a cut is two traces
    /// pinned at φ and φ + 180°, whose labels DIFFER — two genuinely different slices, both of which
    /// the reader needs named — and those are now stable under the checkbox in both directions. Only
    /// a back branch drawing the very same slice as one already on the axis still collapses, which
    /// is the case the rule was written for.</para>
    ///
    /// <para>Deduping on the label ALONE was tried and is wrong: two ordinary traces of one quantity
    /// (the Add Trace button clones the selected one) are two traces a reader styles separately, and
    /// silently giving them one strip between them is a different bug in a much commoner place.</para>
    /// </summary>
    /// <param name="aliasFor">The source alias resolver the minimal labeller takes; null falls back
    /// to the file-name stem, exactly as <see cref="TraceLabeler.ComputeMinimalLabels"/> does.</param>
    public static (IReadOnlyList<PlacedLabelStrip> Left, IReadOnlyList<PlacedLabelStrip> Right)
        For(Plot plot, bool showFilePrefix, Func<Trace, string?>? aliasFor = null)
    {
        if (!plot.PlotType.IsComplex())
            return (Array.Empty<PlacedLabelStrip>(), Array.Empty<PlacedLabelStrip>());

        // The MINIMAL label, over every trace on the plot — the same call the window makes, from
        // the same place the strip set is decided, so the two can no longer answer differently.
        var labels = TraceLabeler.ComputeMinimalLabels(plot.Traces, showFilePrefix, aliasFor);
        var map    = new Dictionary<Trace, string>();
        for (int i = 0; i < plot.Traces.Count && i < labels.Count; i++) map[plot.Traces[i]] = labels[i];

        var leftTraces  = Labelled(plot.LeftAxisTraces,  map);
        var rightTraces = Labelled(plot.RightAxisTraces, map);

        return (Side(leftTraces,  plot.CustomYLabelOn,  plot.CustomYLabel,  showFilePrefix, map),
                Side(rightTraces, plot.CustomY2LabelOn, plot.CustomY2Label, showFilePrefix, map));
    }

    /// <summary>The traces on one side that name the axis — see <see cref="For"/>. Contour traces
    /// have no Y axis of their own; a back branch whose label is already on this axis is a repeat of
    /// the curve that put it there.</summary>
    private static List<Trace> Labelled(IEnumerable<Trace> traces,
                                        IReadOnlyDictionary<Trace, string> labels)
    {
        var all  = traces.Where(t => !t.IsContourTrace).ToList();
        var kept = new List<Trace>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in all)
        {
            string label = labels.TryGetValue(t, out var l) ? l : "";
            // An unlabelled trace is never a repeat of another unlabelled one: "" is the absence of
            // a name rather than a name two traces share.
            if (t.MirrorPatternAngle && label.Length > 0 && seen.Contains(label)) continue;
            seen.Add(label);
            kept.Add(t);
        }

        // A back branch with no front branch is an odd document, but it is one the reader must still
        // be able to read an axis name off.
        return kept.Count > 0 ? kept : all;
    }

    private static IReadOnlyList<PlacedLabelStrip> Side(
        IReadOnlyList<Trace> traces, bool hasCustom, string? customLabel, bool showFilePrefix,
        IReadOnlyDictionary<Trace, string> labels)
    {
        if (hasCustom)
            return traces.Count > 0
                ? [new PlacedLabelStrip(traces[0], customLabel, showFilePrefix, Auto(traces[0]))]
                : Array.Empty<PlacedLabelStrip>();

        return traces.Select(t => new PlacedLabelStrip(t, null, showFilePrefix, Auto(t))).ToList();

        string? Auto(Trace t) => labels.TryGetValue(t, out var l) ? l : null;
    }
}
