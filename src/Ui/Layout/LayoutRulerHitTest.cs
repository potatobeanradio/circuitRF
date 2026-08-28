using System;
using System.Collections.Generic;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Hit-testing the in-design ruler annotations (docs/design/layout-view.md §9B.6 / R-rul-11).
///
/// <para><b>A linear scan, deliberately</b> (§9B.11): there are tens of rulers, not 500,000, and
/// <see cref="LayoutView.Rulers"/> is not in the spatial index. A scan is the right-sized tool and
/// keeps the index a pure function of <see cref="LayoutView.Shapes"/>.</para>
///
/// <para><b>Unlike <see cref="LayoutHitTest"/>, this file is NOT framework-free</b>, and that is the
/// point rather than an oversight: a ruler's readout text is part of its pick region, and the only
/// thing in this codebase that knows the font metrics is <c>LayoutRenderer</c>. Measuring the box
/// again here — by a character-count estimate, say — is exactly the drift
/// <c>LayoutRenderer.MeasureLabelWorldBbox</c>'s own doc comment records: a hit region and a highlight
/// that disagree about where the text is.</para>
///
/// <para><b>Rulers hit-test ABOVE all geometry</b>, since they paint above it — a ruler lying over a
/// trace is grabbed before the trace. Callers therefore ask here FIRST and only fall through to
/// <see cref="LayoutHitTest"/> when nothing is found.</para>
/// </summary>
public static class LayoutRulerHitTest
{
    /// <summary>What part of a ruler a press landed on.</summary>
    public enum RulerPart
    {
        /// <summary>The measurement line's body, or its readout text.</summary>
        Body,
        /// <summary>The first endpoint — dragging it moves that endpoint alone and re-measures.</summary>
        Endpoint1,
        /// <summary>The second endpoint.</summary>
        Endpoint2,
    }

    /// <summary>
    /// The topmost ruler under <paramref name="x"/>/<paramref name="y"/> within
    /// <paramref name="tolDbu"/>, and which part of it was hit — or null when the point is over no
    /// ruler. Later rulers win, matching the paint order (the last one drawn is the one on top).
    ///
    /// <para><b>Endpoints outrank the body</b>, so grabbing the very end of a ruler re-measures it
    /// rather than sliding the whole thing. That mirrors §6.3's vertex-over-body rule exactly.</para>
    /// </summary>
    /// <param name="devicePxPerDbu">The viewport's own zoom — needed because a
    /// <see cref="RulerSizeMode.Fixed"/> ruler's painted text extent is a function of it (that IS the
    /// mode). 0 falls back to the ruler's stored world height, which still yields a usable region.</param>
    public static (int Index, RulerPart Part)? Hit(
        LayoutView view, long x, long y, long tolDbu, double devicePxPerDbu)
    {
        tolDbu = Math.Max(tolDbu, 0);

        for (int i = view.Rulers.Count - 1; i >= 0; i--)
            if (PartHitOn(view, i, x, y, tolDbu, devicePxPerDbu) is { } part)
                return (i, part);

        return null;
    }

    /// <summary>
    /// EVERY ruler under the point, topmost first — the ruler half of a click's pick stack.
    ///
    /// <para><see cref="Hit"/> answers "which one would I grab", which is what an endpoint drag and
    /// the context menu need. This answers "what is stacked here", which is what overlap cycling
    /// needs (§6.2 R-L1c-2): a click that lands on a ruler AND on the geometry beneath it has to be
    /// able to walk down to that geometry, and a stack of one cannot express that.</para>
    /// </summary>
    public static List<int> HitStack(LayoutView view, long x, long y, long tolDbu, double devicePxPerDbu)
    {
        tolDbu = Math.Max(tolDbu, 0);

        var hits = new List<int>();
        for (int i = view.Rulers.Count - 1; i >= 0; i--)
            if (PartHitOn(view, i, x, y, tolDbu, devicePxPerDbu) is not null)
                hits.Add(i);
        return hits;
    }

    /// <summary>The one hit predicate both entry points share — the line, either endpoint, or the
    /// readout's box (R-rul-11: "clicking the number selects the ruler, which is the affordance a
    /// user reaches for first"). Endpoints outrank the body, mirroring §6.3's vertex-over-body
    /// rule.</summary>
    private static RulerPart? PartHitOn(
        LayoutView view, int index, long x, long y, long tolDbu, double devicePxPerDbu)
    {
        var r = view.Rulers[index];

        if (WithinPoint(r.X1, r.Y1, x, y, tolDbu)) return RulerPart.Endpoint1;
        if (WithinPoint(r.X2, r.Y2, x, y, tolDbu)) return RulerPart.Endpoint2;
        if (DistanceToSegment(r.X1, r.Y1, r.X2, r.Y2, x, y) <= tolDbu) return RulerPart.Body;

        var textBb = LayoutRenderer.MeasureRulerTextWorldBbox(
            r, view.DisplayUnit, view.DbuPerMicron, devicePxPerDbu);
        if (!textBb.IsEmpty && new Bbox(textBb.MinX - tolDbu, textBb.MinY - tolDbu,
                                        textBb.MaxX + tolDbu, textBb.MaxY + tolDbu).Contains(x, y))
            return RulerPart.Body;

        return null;
    }

    /// <summary>
    /// Rulers whose WHOLE line is enclosed by (left-to-right) or which intersect (right-to-left) the
    /// marquee — §6.2's existing enclose/crossing rule, applied to the segment's own bbox so the
    /// answer never depends on the zoom the marquee happened to be drawn at.
    /// </summary>
    public static List<int> Marquee(LayoutView view, Bbox marquee, bool leftToRight)
    {
        var hits = new List<int>();
        for (int i = 0; i < view.Rulers.Count; i++)
        {
            var r = view.Rulers[i];
            var bb = new Bbox(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2),
                              Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));
            bool matches = leftToRight
                ? bb.MinX >= marquee.MinX && bb.MaxX <= marquee.MaxX &&
                  bb.MinY >= marquee.MinY && bb.MaxY <= marquee.MaxY
                : bb.Intersects(marquee);
            if (matches) hits.Add(i);
        }
        return hits;
    }

    private static bool WithinPoint(long px, long py, long x, long y, long tolDbu)
    {
        long dx = x - px, dy = y - py;
        return (double)dx * dx + (double)dy * dy <= (double)tolDbu * tolDbu;
    }

    /// <summary>Perpendicular distance from a point to a segment, clamped to the segment's ends.</summary>
    internal static double DistanceToSegment(long ax, long ay, long bx, long by, long px, long py)
    {
        double vx = (double)bx - ax, vy = (double)by - ay;
        double wx = (double)px - ax, wy = (double)py - ay;
        double lenSq = vx * vx + vy * vy;
        double t = lenSq <= 0 ? 0 : Math.Clamp((wx * vx + wy * vy) / lenSq, 0, 1);
        double cx = ax + t * vx, cy = ay + t * vy;
        double dx = px - cx, dy = py - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
