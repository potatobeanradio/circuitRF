// Bounding boxes and arc math. Framework-free — no SKPath / Avalonia types.
// Flattening and ToClipperPaths are L1 concerns (docs/design/layout-view.md §3.2) — this file
// only computes conservative-or-exact bounding boxes and the bulge<->arc conversion the L1
// flattener will reuse.

namespace CircuitRF.Ui.Layout;

public readonly record struct Bbox(long MinX, long MinY, long MaxX, long MaxY)
{
    public static readonly Bbox Empty = new(long.MaxValue, long.MaxValue, long.MinValue, long.MinValue);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public Bbox Union(Bbox other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new Bbox(
            Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
    }

    public bool Contains(long x, long y) => !IsEmpty && x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    public bool Intersects(Bbox other) =>
        !IsEmpty && !other.IsEmpty &&
        MinX <= other.MaxX && MaxX >= other.MinX &&
        MinY <= other.MaxY && MaxY >= other.MinY;
}

/// <summary>Bulge &lt;-&gt; (center, radius, start angle, sweep) conversion, shared by bbox math and
/// (later) the L1 flattener. Bulge convention: <c>bulge = tan(sweep/4)</c>; positive bulge sweeps
/// the arc in the direction of increasing angle (standard atan2 sense) from P0 to P1.</summary>
public static class LayoutArc
{
    public readonly record struct ArcParams(double Cx, double Cy, double R, double StartAngle, double Sweep);

    /// <summary>Converts a chord + bulge into center/radius/start-angle/sweep. Bulge must be non-zero
    /// (a zero bulge is a straight edge, not an arc).</summary>
    public static ArcParams FromBulge(long x0, long y0, long x1, long y1, double bulge)
        => FromBulge((double)x0, (double)y0, (double)x1, (double)y1, bulge);

    /// <summary>Double-precision overload — used by the L1a renderer to derive arc parameters
    /// directly from already-transformed path-space coordinates rather than DBU integers. The
    /// derivation is purely local (chord direction + a perpendicular offset), so it is correct in
    /// any consistent coordinate space, including one with a flipped Y axis (path space is built
    /// Y-down/screen-sense — see LayoutRenderer) — do not re-derive this math from DBU-space output.</summary>
    public static ArcParams FromBulge(double x0, double y0, double x1, double y1, double bulge)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d == 0 || bulge == 0)
            return new ArcParams(x0, y0, 0, 0, 0);

        double ux = dx / d, uy = dy / d;
        // Right-perpendicular of the chord direction — the side positive bulge bulges toward,
        // consistent with sweep = +4*atan(bulge) being an increasing-angle (atan2) sweep.
        double nx = uy, ny = -ux;

        double h = bulge * d / 2.0;
        double mx = (x0 + x1) / 2.0, my = (y0 + y1) / 2.0;
        double apexX = mx + nx * h, apexY = my + ny * h;

        double rSigned = d * (1 + bulge * bulge) / (4.0 * bulge);
        double cx = apexX - nx * rSigned, cy = apexY - ny * rSigned;
        double r = Math.Abs(rSigned);

        double startAngle = Math.Atan2(y0 - cy, x0 - cx);
        double sweep = 4.0 * Math.Atan(bulge);

        return new ArcParams(cx, cy, r, startAngle, sweep);
    }

    /// <summary>Inverse of the sweep half of <see cref="FromBulge"/>: bulge from a sweep angle (radians).</summary>
    public static double ToBulge(double sweep) => Math.Tan(sweep / 4.0);

    /// <summary>Exact bounding box of the arc from (x0,y0) to (x1,y1) with the given bulge — includes
    /// both endpoints and any of the circle's four axis extremes (0/90/180/270°) that the sweep passes
    /// through, so a bulging arc's true extent (which can exceed its chord's bbox) is captured exactly.</summary>
    public static Bbox ArcExtremes(long x0, long y0, long x1, long y1, double bulge)
    {
        if (bulge == 0)
            return new Bbox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

        var (cx, cy, r, startAngle, sweep) = FromBulge(x0, y0, x1, y1, bulge);
        if (r == 0)
            return new Bbox(x0, y0, x0, y0);

        double minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        double minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);

        for (int k = 0; k < 4; k++)
        {
            double axisAngle = k * Math.PI / 2.0;

            // Strictly interior to the sweep only: an axisAngle that coincides (within
            // floating tolerance) with the start or end angle duplicates a point we already
            // know exactly (x0,y0)/(x1,y1) — recomputing it via center+radius+trig would
            // reintroduce the very floating error (e.g. sin(Math.PI) != 0) those exact
            // integers avoid, corrupting an otherwise-exact bbox with a spurious 1-DBU pad.
            if (!SweepStrictlyContains(startAngle, sweep, axisAngle)) continue;

            double px = cx + r * Math.Cos(axisAngle);
            double py = cy + r * Math.Sin(axisAngle);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }

        return new Bbox(
            (long)Math.Floor(minX), (long)Math.Floor(minY),
            (long)Math.Ceiling(maxX), (long)Math.Ceiling(maxY));
    }

    /// <summary>True if <paramref name="angle"/> lies strictly between <paramref name="start"/> and
    /// <paramref name="start"/>+<paramref name="sweep"/> (direction given by sweep's sign) — excludes
    /// the two boundary angles themselves (see the call site for why that matters).</summary>
    private static bool SweepStrictlyContains(double start, double sweep, double angle)
    {
        const double twoPi = 2.0 * Math.PI;
        const double eps = 1e-6;
        double dir = Math.Sign(sweep);
        double sweepAbs = Math.Abs(sweep);
        double diff = dir * (angle - start);
        diff = ((diff % twoPi) + twoPi) % twoPi;
        return diff > eps && diff < sweepAbs - eps;
    }
}

/// <summary>Bounding boxes (L0a) plus whole-shape translation (L1c — <see cref="TranslateBy"/>).
/// The flattener and ToClipperPaths belong to L1e.</summary>
public static class LayoutGeometry
{
    /// <summary>
    /// Translates every coordinate of <paramref name="shape"/> by <c>(dx, dy)</c> in place — used
    /// by <c>MoveShapesCommand</c> (docs/design/layout-view.md L1c). <b>R-L1c-3: callers must snap
    /// the DELTA before calling this, never the resulting vertices</b> — this method adds the same
    /// (already-snapped) integer delta to every vertex, which is what keeps off-grid geometry
    /// (imported GDSII, 45° diagonals, flattened arcs) exactly self-consistent after a move. Cubic
    /// control points (absolute DBU coordinates, not relative) translate along with their edge list.
    /// </summary>
    public static void TranslateBy(LayoutShape shape, long dx, long dy)
    {
        switch (shape)
        {
            case RectShape r:
                r.X1 += dx; r.Y1 += dy; r.X2 += dx; r.Y2 += dy;
                break;

            case PolygonShape p:
                TranslatePoints(p.Xy, dx, dy);
                break;

            case RoundedRectShape rr:
                rr.X1 += dx; rr.Y1 += dy; rr.X2 += dx; rr.Y2 += dy;
                break;

            case CircleShape c:
                c.Cx += dx; c.Cy += dy;
                break;

            case CurveShape curve:
                TranslatePoints(curve.Xy, dx, dy);
                TranslateEdges(curve.Edges, dx, dy);
                break;

            case PathShape path:
                TranslatePoints(path.Xy, dx, dy);
                TranslateEdges(path.Edges, dx, dy);
                break;

            case ViaShape via:
                via.X += dx; via.Y += dy;
                break;

            case LabelShape label:
                label.X += dx; label.Y += dy;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static void TranslatePoints(long[] xy, long dx, long dy)
    {
        for (int i = 0; i < xy.Length; i += 2)
        {
            xy[i]     += dx;
            xy[i + 1] += dy;
        }
    }

    private static void TranslateEdges(List<LayoutEdge>? edges, long dx, long dy)
    {
        if (edges is null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            e.C1X += dx; e.C1Y += dy;
            e.C2X += dx; e.C2Y += dy;
        }
    }

    /// <summary>Deep-clones a shape (including its edge list, where present) — used for a live
    /// move-drag preview (<c>LayoutOverlay.DragOverrides</c>), which must never mutate the model's
    /// own shape instance mid-drag.</summary>
    public static LayoutShape Clone(LayoutShape shape) => shape switch
    {
        RectShape r        => new RectShape { Layer = r.Layer, Net = r.Net, X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2 },
        PolygonShape p     => new PolygonShape { Layer = p.Layer, Net = p.Net, Xy = (long[])p.Xy.Clone() },
        RoundedRectShape rr => new RoundedRectShape { Layer = rr.Layer, Net = rr.Net, X1 = rr.X1, Y1 = rr.Y1, X2 = rr.X2, Y2 = rr.Y2, CornerRadius = rr.CornerRadius },
        CircleShape c      => new CircleShape { Layer = c.Layer, Net = c.Net, Cx = c.Cx, Cy = c.Cy, R = c.R },
        CurveShape curve   => new CurveShape { Layer = curve.Layer, Net = curve.Net, Xy = (long[])curve.Xy.Clone(), Edges = CloneEdges(curve.Edges), FlattenTolDbu = curve.FlattenTolDbu },
        PathShape path     => new PathShape { Layer = path.Layer, Net = path.Net, Xy = (long[])path.Xy.Clone(), Edges = CloneEdges(path.Edges), Width = path.Width, End = path.End, FlattenTolDbu = path.FlattenTolDbu },
        ViaShape via       => new ViaShape { Layer = via.Layer, Net = via.Net, X = via.X, Y = via.Y, PadSize = via.PadSize, DrillSize = via.DrillSize, LandingLayer = via.LandingLayer },
        LabelShape label   => new LabelShape { Layer = label.Layer, Net = label.Net, X = label.X, Y = label.Y, Text = label.Text, Height = label.Height, Rotation = label.Rotation, IsPort = label.IsPort },
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static List<LayoutEdge>? CloneEdges(List<LayoutEdge>? edges) =>
        edges?.Select(e => new LayoutEdge { Kind = e.Kind, Bulge = e.Bulge, C1X = e.C1X, C1Y = e.C1Y, C2X = e.C2X, C2Y = e.C2Y }).ToList();

    public static Bbox BboxOf(LayoutShape shape) => shape switch
    {
        RectShape r        => new Bbox(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2)),
        PolygonShape p      => BboxOfPoints(p.Xy),
        RoundedRectShape rr => new Bbox(Math.Min(rr.X1, rr.X2), Math.Min(rr.Y1, rr.Y2), Math.Max(rr.X1, rr.X2), Math.Max(rr.Y1, rr.Y2)),
        CircleShape c       => new Bbox(c.Cx - c.R, c.Cy - c.R, c.Cx + c.R, c.Cy + c.R),
        CurveShape curve    => BboxOfEdgeList(curve.Xy, curve.Edges, closed: true),
        PathShape path      => BboxOfPath(path),
        ViaShape via        => new Bbox(via.X - via.PadSize / 2, via.Y - via.PadSize / 2, via.X + via.PadSize / 2, via.Y + via.PadSize / 2),
        LabelShape label    => new Bbox(label.X, label.Y, label.X, label.Y),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    // ── Vertex-list / edge-list helpers ──────────────────────────────────────

    private static Bbox BboxOfPoints(long[] xy)
    {
        if (xy.Length < 2) return Bbox.Empty;
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        for (int i = 0; i < xy.Length; i += 2)
        {
            long x = xy[i], y = xy[i + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return new Bbox(minX, minY, maxX, maxY);
    }

    /// <summary>Bbox of a closed edge list: exact for Line edges (covered by the vertex bbox), exact
    /// arc extremes for Arc edges, conservative convex-hull-of-control-points for Cubic edges.</summary>
    private static Bbox BboxOfEdgeList(long[] xy, List<LayoutEdge>? edges, bool closed)
    {
        var bb = BboxOfPoints(xy);
        if (edges == null || xy.Length < 4) return bb;

        int n = xy.Length / 2;
        int edgeCount = closed ? n : n - 1;
        int count = Math.Min(edgeCount, edges.Count);

        for (int i = 0; i < count; i++)
        {
            var e = edges[i];
            if (e.Kind == EdgeKind.Line) continue;

            int j = closed ? (i + 1) % n : i + 1;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * j], y1 = xy[2 * j + 1];

            bb = e.Kind == EdgeKind.Arc
                ? bb.Union(LayoutArc.ArcExtremes(x0, y0, x1, y1, e.Bulge))
                : bb.Union(BboxOfPoints([x0, y0, e.C1X, e.C1Y, e.C2X, e.C2Y, x1, y1]));
        }

        return bb;
    }

    private static Bbox GrowBbox(Bbox bb, long amount) =>
        bb.IsEmpty ? bb : new Bbox(bb.MinX - amount, bb.MinY - amount, bb.MaxX + amount, bb.MaxY + amount);

    // ── Path (open, width, end style) ────────────────────────────────────────

    private static Bbox BboxOfPath(PathShape path)
    {
        var xy = path.Xy;
        int n = xy.Length / 2;
        if (n == 0) return Bbox.Empty;
        if (n == 1) return new Bbox(xy[0], xy[1], xy[0], xy[1]);

        long halfW = (path.Width + 1) / 2; // ceiling — never underestimate the stroke half-width
        var bb = Bbox.Empty;

        for (int i = 0; i < n - 1; i++)
        {
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * (i + 1)], y1 = xy[2 * (i + 1) + 1];
            var edge = path.Edges != null && i < path.Edges.Count ? path.Edges[i] : null;
            var kind = edge?.Kind ?? EdgeKind.Line;

            Bbox segBb = kind switch
            {
                EdgeKind.Line  => LineStrokeBbox(x0, y0, x1, y1, halfW),
                EdgeKind.Arc   => GrowBbox(LayoutArc.ArcExtremes(x0, y0, x1, y1, edge!.Bulge), halfW),
                EdgeKind.Cubic => GrowBbox(BboxOfPoints([x0, y0, edge!.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1]), halfW),
                _ => throw new ArgumentOutOfRangeException(),
            };
            bb = bb.Union(segBb);
        }

        // End caps: approximate the tangent at each end by its adjacent chord direction — exact for
        // Line edges, a reasonable (and conservative-enough for L0a) approximation for Arc/Cubic ends;
        // exact curved-end tangents belong to the L1 flattener. In both calls (vx,vy) is the path
        // endpoint and (nx,ny) is its neighbor vertex; vx-nx/vy-ny already points outward — away
        // from P1 at the start, continuing past Plast at the end.
        bb = bb.Union(CapBbox(xy[0], xy[1], xy[2], xy[3], halfW, path.End));
        bb = bb.Union(CapBbox(xy[2 * (n - 1)], xy[2 * (n - 1) + 1], xy[2 * (n - 2)], xy[2 * (n - 2) + 1], halfW, path.End));

        return bb;
    }

    private static Bbox LineStrokeBbox(long x0, long y0, long x1, long y1, long halfW)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0) return new Bbox(x0, y0, x0, y0);

        double px = -dy / len * halfW, py = dx / len * halfW;
        double minX = Math.Min(x0 + px, Math.Min(x0 - px, Math.Min(x1 + px, x1 - px)));
        double maxX = Math.Max(x0 + px, Math.Max(x0 - px, Math.Max(x1 + px, x1 - px)));
        double minY = Math.Min(y0 + py, Math.Min(y0 - py, Math.Min(y1 + py, y1 - py)));
        double maxY = Math.Max(y0 + py, Math.Max(y0 - py, Math.Max(y1 + py, y1 - py)));

        return new Bbox(
            (long)Math.Floor(minX), (long)Math.Floor(minY),
            (long)Math.Ceiling(maxX), (long)Math.Ceiling(maxY));
    }

    /// <param name="vx">Endpoint vertex X.</param>
    /// <param name="vy">Endpoint vertex Y.</param>
    /// <param name="nx">The endpoint's neighbor vertex (used to derive the outward tangent), X.</param>
    /// <param name="ny">Same, Y.</param>
    private static Bbox CapBbox(long vx, long vy, long nx, long ny, long halfW, PathEndStyle style)
    {
        if (style == PathEndStyle.Flush) return Bbox.Empty;

        double dx = vx - nx, dy = vy - ny;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0) return Bbox.Empty;
        double tx = dx / len, ty = dy / len;   // outward tangent (away from the path body)
        double px = -ty, py = tx;              // perpendicular

        if (style == PathEndStyle.Round)
            return new Bbox(vx - halfW, vy - halfW, vx + halfW, vy + halfW);

        // Square / Extended: rectangular cap extending halfW beyond the endpoint along the tangent.
        double ex = vx + tx * halfW, ey = vy + ty * halfW;
        double c1x = ex + px * halfW, c1y = ey + py * halfW;
        double c2x = ex - px * halfW, c2y = ey - py * halfW;
        double c3x = vx + px * halfW, c3y = vy + py * halfW;
        double c4x = vx - px * halfW, c4y = vy - py * halfW;

        double minX = Math.Min(Math.Min(c1x, c2x), Math.Min(c3x, c4x));
        double maxX = Math.Max(Math.Max(c1x, c2x), Math.Max(c3x, c4x));
        double minY = Math.Min(Math.Min(c1y, c2y), Math.Min(c3y, c4y));
        double maxY = Math.Max(Math.Max(c1y, c2y), Math.Max(c3y, c4y));

        return new Bbox(
            (long)Math.Floor(minX), (long)Math.Floor(minY),
            (long)Math.Ceiling(maxX), (long)Math.Ceiling(maxY));
    }
}
