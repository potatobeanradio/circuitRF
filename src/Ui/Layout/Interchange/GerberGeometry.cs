// Shape-to-boundary geometry shared by GerberWriter (docs/sonnet-briefs/brief-L4c-gerber-export.md §3).
// Gerber can express Line/Arc edges natively (G01/G02/G03 inside a region) — unlike GDSII, which has no
// arc primitive at all and must flatten every curve, Gerber's own writer needs a Ring/RingEdge
// representation that PRESERVES arcs, mirroring the role DxfWriter's own (private) RingEdge plays for
// DXF's bulge-carrying LWPOLYLINE. Each interchange writer owns its own Ring shim (RingEdge is a
// format-local view over the SAME LayoutEdge/Xy data), per L4a/L4b's own precedent — this is not a
// second flattener (LayoutFlattener is untouched; cubic edges, which Gerber cannot express at all, fall
// back to the same local de Casteljau subdivision DxfWriter already uses for its own cubic-in-ring case).

namespace CircuitRF.Ui.Layout.Interchange;

internal static class GerberGeometry
{
    internal readonly record struct RingEdge(long X0, long Y0, long X1, long Y1, EdgeKind Kind, double Bulge,
        long C1X, long C1Y, long C2X, long C2Y);

    /// <summary>Walks a closed vertex list + parallel edge list into per-edge (start, end, kind) form —
    /// the same shape <c>DxfWriter.Ring</c> builds, kept local to this writer.</summary>
    internal static List<RingEdge> Ring(long[] xy, List<LayoutEdge>? edges)
    {
        int n = xy.Length / 2;
        var result = new List<RingEdge>(n);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var e = edges is not null && i < edges.Count ? edges[i] : null;
            result.Add(new RingEdge(
                xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1],
                e?.Kind ?? EdgeKind.Line, e?.Bulge ?? 0.0,
                e?.C1X ?? 0, e?.C1Y ?? 0, e?.C2X ?? 0, e?.C2Y ?? 0));
        }
        return result;
    }

    /// <summary>tan(22.5°) — the exact constant §4's <c>.clay</c> example, L1h's arc promotion, and
    /// DxfWriter's own <c>RoundedRectRing</c> all already use for a RoundedRect's four quarter-circle
    /// corners (docs: "RoundedRect's four corners use the same tan(22.5°) constant").</summary>
    internal const double RoundedRectKappa = 0.41421356237309515;

    /// <summary>Synthesizes a RoundedRect's boundary as 4 lines + 4 quarter-circle arcs (all positive
    /// bulge — the walk is consistently CCW-oriented, matching <c>LayoutFlattener.FlattenRoundedRect</c>'s
    /// own vertex placement) — so a rounded corner exports as a real G02/G03 arc, never a flattened chord.</summary>
    internal static List<RingEdge> RoundedRectRing(RoundedRectShape rr)
    {
        long x1 = Math.Min(rr.X1, rr.X2), x2 = Math.Max(rr.X1, rr.X2);
        long y1 = Math.Min(rr.Y1, rr.Y2), y2 = Math.Max(rr.Y1, rr.Y2);
        long cr = Math.Max(0, Math.Min(rr.CornerRadius, Math.Min(x2 - x1, y2 - y1) / 2));

        if (cr <= 0) return Ring([x1, y1, x2, y1, x2, y2, x1, y2], null);

        var edges = new List<LayoutEdge>
        {
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = RoundedRectKappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = RoundedRectKappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = RoundedRectKappa },
            new() { Kind = EdgeKind.Line },
            new() { Kind = EdgeKind.Arc, Bulge = RoundedRectKappa },
        };
        long[] xy =
        [
            x1 + cr, y1,  x2 - cr, y1,
            x2, y1 + cr,  x2, y2 - cr,
            x2 - cr, y2,  x1 + cr, y2,
            x1, y2 - cr,  x1, y1 + cr,
        ];
        return Ring(xy, edges);
    }

    internal static bool HasCubic(List<RingEdge> ring) => ring.Exists(e => e.Kind == EdgeKind.Cubic);
    internal static bool HasArc(List<RingEdge> ring) => ring.Exists(e => e.Kind == EdgeKind.Arc);

    /// <summary>Flattens only the Cubic edges of a ring to straight-line chords (Arc edges pass through
    /// untouched) — Gerber has no Bezier primitive at all, so a Cubic edge (rare — a hand-drawn curve
    /// with a dragged bulge handle promoted to cubic, or an imported spline) must always be approximated,
    /// counted via the caller's own diagnostics, never silently. Local de Casteljau subdivision, mirrors
    /// <c>DxfWriter</c>'s own equivalent private helper (kept per-writer, not shared — see file header).</summary>
    internal static List<RingEdge> FlattenCubicsInRing(List<RingEdge> ring, long tolDbu)
    {
        var result = new List<RingEdge>();
        foreach (var e in ring)
        {
            if (e.Kind != EdgeKind.Cubic) { result.Add(e); continue; }
            var pts = new List<long> { e.X0, e.Y0 };
            AppendFlattenedCubic(pts, e.X0, e.Y0, e.C1X, e.C1Y, e.C2X, e.C2Y, e.X1, e.Y1, Math.Max(1, tolDbu), 0);
            for (int i = 0; i + 3 < pts.Count; i += 2)
                result.Add(new RingEdge(pts[i], pts[i + 1], pts[i + 2], pts[i + 3], EdgeKind.Line, 0, 0, 0, 0, 0));
        }
        return result;
    }

    private static void AppendFlattenedCubic(
        List<long> xy, double x0, double y0, double c1x, double c1y, double c2x, double c2y,
        double x1, double y1, long tolDbu, int depth)
    {
        if (depth >= 20 || IsFlatEnough(x0, y0, c1x, c1y, c2x, c2y, x1, y1, tolDbu))
        {
            xy.Add((long)Math.Round(x1)); xy.Add((long)Math.Round(y1));
            return;
        }

        double x01 = (x0 + c1x) / 2.0, y01 = (y0 + c1y) / 2.0;
        double x12 = (c1x + c2x) / 2.0, y12 = (c1y + c2y) / 2.0;
        double x23 = (c2x + x1) / 2.0, y23 = (c2y + y1) / 2.0;
        double x012 = (x01 + x12) / 2.0, y012 = (y01 + y12) / 2.0;
        double x123 = (x12 + x23) / 2.0, y123 = (y12 + y23) / 2.0;
        double xm = (x012 + x123) / 2.0, ym = (y012 + y123) / 2.0;

        AppendFlattenedCubic(xy, x0, y0, x01, y01, x012, y012, xm, ym, tolDbu, depth + 1);
        AppendFlattenedCubic(xy, xm, ym, x123, y123, x23, y23, x1, y1, tolDbu, depth + 1);
    }

    private static bool IsFlatEnough(double x0, double y0, double c1x, double c1y,
        double c2x, double c2y, double x1, double y1, long tolDbu)
    {
        double tol = Math.Max(1.0, tolDbu);
        return PointToLineDistance(c1x, c1y, x0, y0, x1, y1) <= tol
            && PointToLineDistance(c2x, c2y, x0, y0, x1, y1) <= tol;
    }

    private static double PointToLineDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double cross = dx * (py - ay) - dy * (px - ax);
        return Math.Abs(cross) / Math.Sqrt(lenSq);
    }
}
