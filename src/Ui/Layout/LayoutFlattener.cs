// Framework-free flattener — the single shared helper §6.1 of the design doc requires: booleans,
// offsets, DRC, the mesher, hit-test, and export all call the SAME ToClipperPaths(shape, tolerance)
// (L1e wraps this for Clipper2). Hit-testing a Curve/Circle/RoundedRect is the first consumer, built
// here in L1c rather than deferred to L1e's booleans (docs/sonnet-briefs/brief-L1c-selection-and-properties.md).
//
// R-L1c-1 — DETERMINISM IS MANDATORY. The same (shape, tolerance) must produce a byte-identical
// vertex list every time, on every machine, including after a serialize/deserialize round-trip.
// L1e's booleans, L5b's DRC, and L4's export all have to agree about where a curve's vertices are.
// Every computation here is a fixed sequence of double-precision arithmetic over the shape's own
// integer fields, in a fixed loop order — no dictionaries, no parallelism, no machine-dependent
// state. Do not introduce any of those without re-reading this comment.

namespace CircuitRF.Ui.Layout;

public static class LayoutFlattener
{
    /// <summary>Fallback tolerance when neither the shape nor a technology supplies one — 1 µm at
    /// the default 1000 DBU/µm resolution. Only <see cref="ResolveTolDbu"/> should ever need this;
    /// everything else calls that one resolver.</summary>
    public const long DefaultTolDbu = 1000;

    /// <summary>One resolver, called by everything: the shape's own <c>FlattenTolDbu</c> if set,
    /// otherwise the technology's <c>DefaultFlattenTolDbu</c>, otherwise <see cref="DefaultTolDbu"/>.</summary>
    public static long ResolveTolDbu(LayoutShape shape, Technology? tech)
    {
        long? shapeTol = shape switch
        {
            CurveShape c => c.FlattenTolDbu,
            PathShape p  => p.FlattenTolDbu,
            _            => null,
        };
        if (shapeTol is > 0) return shapeTol.Value;
        if (tech is { DefaultFlattenTolDbu: > 0 }) return tech.DefaultFlattenTolDbu;
        return DefaultTolDbu;
    }

    /// <summary>Caps the number of segments a single arc/circle may expand to, regardless of how
    /// small the sagitta-derived per-segment sweep would otherwise force it (docs: "clamped to
    /// something sane for very large r") — 4096 segments per full 360° revolution.</summary>
    private const double MinSweepPerSegment = 2.0 * Math.PI / 4096.0;

    /// <summary>Recursion depth cap for cubic subdivision — 2^20 segments is far beyond anything a
    /// real layout tolerance would ever demand; it exists purely so a degenerate (e.g. self-crossing
    /// control polygon) curve cannot recurse forever.</summary>
    private const int MaxCubicDepth = 20;

    /// <summary>
    /// Flattens a shape into one or more closed rings, flat <c>[x0,y0,x1,y1,…]</c>, implicitly
    /// closed (never repeats the first vertex at the end). <c>Rect</c>/<c>PolygonShape</c> (and any
    /// edge list whose edges are all <c>Line</c>) are returned as-is — no allocation churn beyond the
    /// one array a <c>Rect</c> needs to synthesize its four corners.
    /// </summary>
    public static IReadOnlyList<long[]> Flatten(LayoutShape shape, long tolDbu)
    {
        tolDbu = Math.Max(tolDbu, 1);

        return shape switch
        {
            RectShape r        => [[r.X1, r.Y1, r.X2, r.Y1, r.X2, r.Y2, r.X1, r.Y2]],
            PolygonShape p     => [p.Xy],
            CircleShape c      => [FlattenCircle(c.Cx, c.Cy, c.R, tolDbu)],
            RoundedRectShape rr => [FlattenRoundedRect(rr, tolDbu)],
            CurveShape curve   => [FlattenClosedEdgeList(curve.Xy, curve.Edges, tolDbu)],
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape,
                "LayoutFlattener.Flatten supports Rect/Polygon/RoundedRect/Circle/Curve only — " +
                "Path is a centerline (offset to an outline is an L1e boolean concern), and " +
                "Via/Label are not filled-region primitives."),
        };
    }

    /// <summary>Flattens an OPEN edge list (a <c>Path</c>'s centerline) — not part of the public
    /// closed-ring contract above, but shares the same arc/cubic subdivision so the tolerance
    /// guarantee is identical. Used by <see cref="LayoutHitTest"/> for distance-to-centerline.</summary>
    internal static long[] FlattenOpenEdgeList(long[] xy, List<LayoutEdge>? edges, long tolDbu)
    {
        tolDbu = Math.Max(tolDbu, 1);
        int n = xy.Length / 2;
        if (n < 2 || edges is null) return xy;

        bool anyCurved = false;
        for (int i = 0; i < n - 1 && i < edges.Count; i++)
            if (edges[i].Kind != EdgeKind.Line) { anyCurved = true; break; }
        if (!anyCurved) return xy;

        var result = new List<long>(xy.Length) { xy[0], xy[1] };
        for (int i = 0; i < n - 1; i++)
        {
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * (i + 1)], y1 = xy[2 * (i + 1) + 1];
            var edge = i < edges.Count ? edges[i] : null;
            AppendFlattenedEdge(result, x0, y0, x1, y1, edge, tolDbu);
        }
        return result.ToArray();
    }

    // ── Closed edge list (Curve) ──────────────────────────────────────────────

    private static long[] FlattenClosedEdgeList(long[] xy, List<LayoutEdge>? edges, long tolDbu)
    {
        int n = xy.Length / 2;
        if (n < 2 || edges is null) return xy;

        bool anyCurved = false;
        for (int i = 0; i < n && i < edges.Count; i++)
            if (edges[i].Kind != EdgeKind.Line) { anyCurved = true; break; }
        if (!anyCurved) return xy;

        var result = new List<long>(xy.Length) { xy[0], xy[1] };
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * j], y1 = xy[2 * j + 1];
            var edge = i < edges.Count ? edges[i] : null;
            AppendFlattenedEdge(result, x0, y0, x1, y1, edge, tolDbu);
        }

        // Implicit-closure convention: the last edge's endpoint is vertex 0 again — drop the
        // duplicate so the ring matches PolygonShape.Xy's "never repeats the first vertex" shape.
        if (result.Count >= 4 && result[0] == result[^2] && result[1] == result[^1])
            result.RemoveRange(result.Count - 2, 2);

        return result.ToArray();
    }

    private static void AppendFlattenedEdge(List<long> xy, long x0, long y0, long x1, long y1, LayoutEdge? edge, long tolDbu)
    {
        switch (edge?.Kind ?? EdgeKind.Line)
        {
            case EdgeKind.Line:
                xy.Add(x1); xy.Add(y1);
                break;

            case EdgeKind.Arc:
                AppendArc(xy, x0, y0, x1, y1, edge!.Bulge, tolDbu);
                break;

            case EdgeKind.Cubic:
                AppendCubic(xy, x0, y0, edge!.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1, tolDbu, 0);
                break;
        }
    }

    // ── Arc (sagitta-bounded) ─────────────────────────────────────────────────

    /// <summary>Maximum sweep, in radians, a single segment may cover while keeping its sagitta
    /// within <paramref name="tolDbu"/> at radius <paramref name="r"/>: <c>2·acos(1 − s/r)</c>,
    /// clamped from below by <see cref="MinSweepPerSegment"/> for very large r (where the raw
    /// formula would otherwise demand an unbounded segment count).</summary>
    private static double MaxSweepPerSegment(double r, long tolDbu)
    {
        double s = Math.Max(1.0, (double)tolDbu);
        double ratio = Math.Clamp(1.0 - s / Math.Max(r, 1e-9), -1.0, 1.0);
        double sweep = 2.0 * Math.Acos(ratio);
        if (!double.IsFinite(sweep) || sweep < MinSweepPerSegment) sweep = MinSweepPerSegment;
        return sweep;
    }

    private static void AppendArc(List<long> xy, long x0, long y0, long x1, long y1, double bulge, long tolDbu)
    {
        if (bulge == 0) { xy.Add(x1); xy.Add(y1); return; }

        var arc = LayoutArc.FromBulge(x0, y0, x1, y1, bulge);
        if (arc.R <= 0) { xy.Add(x1); xy.Add(y1); return; }

        double maxSweepPerSeg = MaxSweepPerSegment(arc.R, tolDbu);
        int segCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) / maxSweepPerSeg));
        double step = arc.Sweep / segCount;

        for (int k = 1; k < segCount; k++)
        {
            double angle = arc.StartAngle + step * k;
            xy.Add((long)Math.Round(arc.Cx + arc.R * Math.Cos(angle)));
            xy.Add((long)Math.Round(arc.Cy + arc.R * Math.Sin(angle)));
        }
        // Exact given endpoint, never re-derived via trig — keeps adjacent edges' shared vertex
        // bit-identical to the original model coordinate.
        xy.Add(x1); xy.Add(y1);
    }

    private static int ComputeSegCountForFullCircle(double r, long tolDbu)
        => Math.Max(3, (int)Math.Ceiling(2.0 * Math.PI / MaxSweepPerSegment(r, tolDbu)));

    private static long[] FlattenCircle(long cx, long cy, long r, long tolDbu)
    {
        if (r <= 0) return [cx, cy];

        int segCount = ComputeSegCountForFullCircle(r, tolDbu);
        var xy = new long[segCount * 2];
        double step = 2.0 * Math.PI / segCount;
        for (int i = 0; i < segCount; i++)
        {
            double angle = step * i;
            xy[2 * i]     = (long)Math.Round(cx + r * Math.Cos(angle));
            xy[2 * i + 1] = (long)Math.Round(cy + r * Math.Sin(angle));
        }
        return xy;
    }

    // ── RoundedRect: four lines + four quarter arcs ───────────────────────────

    private static long[] FlattenRoundedRect(RoundedRectShape rr, long tolDbu)
    {
        long x1 = Math.Min(rr.X1, rr.X2), x2 = Math.Max(rr.X1, rr.X2);
        long y1 = Math.Min(rr.Y1, rr.Y2), y2 = Math.Max(rr.Y1, rr.Y2);
        long cr = Math.Max(0, Math.Min(rr.CornerRadius, Math.Min(x2 - x1, y2 - y1) / 2));

        if (cr <= 0) return [x1, y1, x2, y1, x2, y2, x1, y2];

        double maxSweep = MaxSweepPerSegment(cr, tolDbu);
        var xy = new List<long> { x1 + cr, y1 };

        xy.Add(x2 - cr); xy.Add(y1);
        AppendQuarterArc(xy, x2 - cr, y1 + cr, cr, -Math.PI / 2.0, maxSweep);   // corner (x2,y1)

        xy.Add(x2); xy.Add(y2 - cr);
        AppendQuarterArc(xy, x2 - cr, y2 - cr, cr, 0.0, maxSweep);              // corner (x2,y2)

        xy.Add(x1 + cr); xy.Add(y2);
        AppendQuarterArc(xy, x1 + cr, y2 - cr, cr, Math.PI / 2.0, maxSweep);    // corner (x1,y2)

        xy.Add(x1); xy.Add(y1 + cr);
        AppendQuarterArc(xy, x1 + cr, y1 + cr, cr, Math.PI, maxSweep);         // corner (x1,y1)

        // Last arc's final point is (x1+cr, y1) again — the implicit-closure vertex.
        if (xy.Count >= 4 && xy[0] == xy[^2] && xy[1] == xy[^1])
            xy.RemoveRange(xy.Count - 2, 2);

        return xy.ToArray();
    }

    private static void AppendQuarterArc(List<long> xy, double cx, double cy, double r, double startAngle, double maxSweepPerSeg)
    {
        const double sweep = Math.PI / 2.0;
        int segCount = Math.Max(1, (int)Math.Ceiling(sweep / maxSweepPerSeg));
        double step = sweep / segCount;
        for (int k = 1; k <= segCount; k++)
        {
            double angle = startAngle + step * k;
            xy.Add((long)Math.Round(cx + r * Math.Cos(angle)));
            xy.Add((long)Math.Round(cy + r * Math.Sin(angle)));
        }
    }

    // ── Cubic (recursive subdivision vs. chord tolerance) ─────────────────────

    private static void AppendCubic(List<long> xy, double x0, double y0, double c1x, double c1y,
        double c2x, double c2y, double x1, double y1, long tolDbu, int depth)
    {
        if (depth >= MaxCubicDepth || IsFlatEnough(x0, y0, c1x, c1y, c2x, c2y, x1, y1, tolDbu))
        {
            xy.Add((long)Math.Round(x1)); xy.Add((long)Math.Round(y1));
            return;
        }

        // de Casteljau split at t = 0.5.
        double x01 = (x0 + c1x) / 2.0, y01 = (y0 + c1y) / 2.0;
        double x12 = (c1x + c2x) / 2.0, y12 = (c1y + c2y) / 2.0;
        double x23 = (c2x + x1) / 2.0, y23 = (c2y + y1) / 2.0;
        double x012 = (x01 + x12) / 2.0, y012 = (y01 + y12) / 2.0;
        double x123 = (x12 + x23) / 2.0, y123 = (y12 + y23) / 2.0;
        double xm = (x012 + x123) / 2.0, ym = (y012 + y123) / 2.0;

        AppendCubic(xy, x0, y0, x01, y01, x012, y012, xm, ym, tolDbu, depth + 1);
        AppendCubic(xy, xm, ym, x123, y123, x23, y23, x1, y1, tolDbu, depth + 1);
    }

    private static bool IsFlatEnough(double x0, double y0, double c1x, double c1y,
        double c2x, double c2y, double x1, double y1, long tolDbu)
    {
        double tol = Math.Max(1.0, tolDbu);
        return PointToLineDistance(c1x, c1y, x0, y0, x1, y1) <= tol
            && PointToLineDistance(c2x, c2y, x0, y0, x1, y1) <= tol;
    }

    /// <summary>Perpendicular distance from a point to the INFINITE line through (ax,ay)-(bx,by) —
    /// the standard control-polygon flatness test (not clamped to the segment).</summary>
    private static double PointToLineDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double cross = dx * (py - ay) - dy * (px - ax);
        return Math.Abs(cross) / Math.Sqrt(lenSq);
    }
}
