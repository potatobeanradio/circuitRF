// Snap tracking in the layout editor — the 3D view's SnapTracker3D, in a plane and in integer DBU. A snap point held under
// the cursor (SnapReferences: the same dwell and the same forgetting as the 3D view) becomes a reference, and while a
// drawing tool is armed the cursor then locks onto the horizontal and vertical lines through it.
//
// TRACKING. When the geometry snap has no POINT to offer (nothing, or only the nearest point on an edge), a reference's
// vertical line is in play when the cursor is within the snap distance of its x, its horizontal line when within the
// snap distance of its y. On the line nearest the cursor the tracked point is:
//   - where the line crosses an outline edge near the cursor — exactly, so a line down from a trace's corner stops on a
//     ground plane's edge with nothing there to snap to;
//   - otherwise the line's point at the cursor, on the grid.
// A vertical line from one reference and a horizontal from another give their crossing: x of one corner, y of another.
// A point snap always wins — the cursor on a corner means that corner.
//
// Pure: the caller hands in the time, the cursor, the snap distance, the grid and a way to ask for edges near a box.

using CircuitRF.Design.Layout;

namespace CircuitRF.Render;

/// <summary>One reference: a DBU point and the layer it came from (the tracked marker is drawn in its colour).</summary>
public readonly record struct LayoutSnapReference(long X, long Y, LayerKey Layer);

/// <summary>A tracking line in force: reference <see cref="Reference"/>'s vertical (x held) or horizontal (y held) line.</summary>
public readonly record struct LayoutTrackLine(int Reference, bool Vertical);

public sealed class LayoutSnapTracker
{
    private readonly SnapReferences<LayoutSnapReference> _set = new((a, b) => a.X == b.X && a.Y == b.Y);

    /// <summary>The references, oldest first.</summary>
    public IReadOnlyList<SnapReferences<LayoutSnapReference>.Entry> References => _set.Items;

    /// <summary>The line(s) the last <see cref="Apply"/> tracked along — none, one, or two when it took their crossing.</summary>
    public LayoutTrackLine? LineA { get; private set; }
    public LayoutTrackLine? LineB { get; private set; }

    /// <summary>Whether the caller should keep ticking: a dwell is pending, or a reference may yet be forgotten.</summary>
    public bool NeedsTick => _set.NeedsTick;

    /// <summary>A candidate a reference can be acquired from, and that beats tracking: every kind but the nearest point on
    /// an edge (which slides with the cursor) and a tracked point itself.</summary>
    public static bool IsPoint(SnapFeatureKind k) => k is not (SnapFeatureKind.Nearest or SnapFeatureKind.Tracking);

    /// <summary>Every reference dropped. True when there were any.</summary>
    public bool Clear()
    {
        LineA = LineB = null;
        return _set.Clear();
    }

    /// <summary>The geometry snap's answer at the cursor now: starts, keeps or ends a dwell.</summary>
    public void Observe(SnapCandidate? raw, long nowMs)
        => _set.Observe(raw is { } c && IsPoint(c.Kind) ? new LayoutSnapReference(c.X, c.Y, c.Layer) : null, nowMs);

    /// <summary>The clock advanced: a dwell completed, or a reference was forgotten. True when the references changed.</summary>
    public bool Tick(long nowMs) => _set.Tick(nowMs);

    /// <summary>
    /// The snap in force: <paramref name="raw"/> itself when it is a point or no line is near the cursor, else the tracked
    /// point (<see cref="SnapFeatureKind.Tracking"/>, on the reference's layer). <paramref name="edgesNear"/> answers the
    /// outline edges meeting a box. Sets <see cref="LineA"/>/<see cref="LineB"/>.
    /// </summary>
    public SnapCandidate? Apply(SnapCandidate? raw, long px, long py, long tolDbu, long gridDbu,
                                Func<Bbox, IReadOnlyList<SnapEdge>> edgesNear, long nowMs)
    {
        LineA = LineB = null;
        var refs = _set.Items;
        if (refs.Count == 0 || tolDbu <= 0 || raw is { } c && IsPoint(c.Kind)) return raw;

        // The lines in play, each with the cursor's distance from it.
        Span<(int Ref, bool Vertical, long D)> near = stackalloc (int, bool, long)[SnapReferences<LayoutSnapReference>.Max * 2];
        int n = 0;
        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i].Point;
            long dx = Math.Abs(px - r.X), dy = Math.Abs(py - r.Y);
            if (dx <= tolDbu && dy <= tolDbu) continue;                // on the reference itself: its own snap answers
            if (dx <= tolDbu) near[n++] = (i, true, dx);
            if (dy <= tolDbu) near[n++] = (i, false, dy);
        }
        if (n == 0) return raw;

        // One reference's vertical and another's horizontal: their crossing.
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (near[i].Vertical && !near[j].Vertical && near[i].Ref != near[j].Ref)
                {
                    LineA = new LayoutTrackLine(near[i].Ref, true);
                    LineB = new LayoutTrackLine(near[j].Ref, false);
                    _set.Use(near[i].Ref, nowMs);
                    _set.Use(near[j].Ref, nowMs);
                    return Tracked(refs[near[i].Ref].Point.X, refs[near[j].Ref].Point.Y, refs[near[i].Ref].Point.Layer);
                }

        int best = 0;
        for (int i = 1; i < n; i++) if (near[i].D < near[best].D) best = i;
        var (ri, vertical, _) = near[best];
        var at = refs[ri].Point;
        long held = vertical ? at.X : at.Y, cursor = vertical ? py : px;
        var box = vertical ? new Bbox(at.X - tolDbu, py - tolDbu, at.X + tolDbu, py + tolDbu)
                           : new Bbox(px - tolDbu, at.Y - tolDbu, px + tolDbu, at.Y + tolDbu);
        long along = Crossing(edgesNear(box), vertical, held, cursor, tolDbu) ?? LayoutSnapping.SnapValue(cursor, gridDbu, false);
        LineA = new LayoutTrackLine(ri, vertical);
        _set.Use(ri, nowMs);
        return vertical ? Tracked(at.X, along, at.Layer) : Tracked(along, at.Y, at.Layer);
    }

    private static SnapCandidate Tracked(long x, long y, LayerKey layer) => new(SnapFeatureKind.Tracking, x, y, layer, false, -1);

    /// <summary>Where the line (x = <paramref name="held"/> when <paramref name="vertical"/>, else y = it) crosses one of
    /// <paramref name="edges"/>, as the coordinate along the line nearest <paramref name="cursor"/> and within
    /// <paramref name="tol"/> of it; null for none. An edge lying along the line crosses it nowhere in particular and is
    /// passed over.</summary>
    internal static long? Crossing(IReadOnlyList<SnapEdge> edges, bool vertical, long held, long cursor, long tol)
    {
        long? best = null;
        double bestD = double.MaxValue;
        foreach (var e in edges)
        {
            // In the line's own frame: a is across the line, b along it.
            double a0 = vertical ? e.X0 : e.Y0, a1 = vertical ? e.X1 : e.Y1;
            double b0 = vertical ? e.Y0 : e.X0, b1 = vertical ? e.Y1 : e.X1;
            if (a0 == a1 || held < Math.Min(a0, a1) || held > Math.Max(a0, a1)) continue;
            double b = b0 + (held - a0) / (a1 - a0) * (b1 - b0);
            double d = Math.Abs(b - cursor);
            if (d <= tol && d < bestD) { bestD = d; best = (long)Math.Round(b, MidpointRounding.AwayFromZero); }
        }
        return best;
    }
}
