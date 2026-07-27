// Framework-free geometry builders for reshaping an existing LayoutShape (docs/design/layout-view.md
// §6.3 R14, §3.2 R9a, L1d brief). Every function here is IMMUTABLE-STYLE per R-L1d-1: given a shape,
// build and return a brand-new shape (via LayoutGeometry.Clone), never mutate the one the renderer or
// the model may currently be reading. The caller (LayoutEditorViewModel) is the only place that ever
// swaps the result into LayoutView.Shapes, via ReplaceShapeCommand, at the shape's own fixed index.
//
// Snapping is deliberately NOT done here — every function takes already-decided coordinates/deltas.
// The three different snap rules (move = delta, vertex = resulting position, edge = perpendicular
// offset) all live together in LayoutEditorViewModel so they read as one considered system rather
// than being scattered across files (see that class for the rationale).

namespace CircuitRF.Ui.Layout;

public static class LayoutShapeEditing
{
    // ── Shape-kind-agnostic vertex-list access (Polygon / Curve / Path only) ─────────────────

    internal static long[] XyOf(LayoutShape shape) => shape switch
    {
        PolygonShape p => p.Xy,
        CurveShape c   => c.Xy,
        PathShape p    => p.Xy,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a vertex-list shape"),
    };

    private static void SetXy(LayoutShape shape, long[] xy)
    {
        switch (shape)
        {
            case PolygonShape p: p.Xy = xy; break;
            case CurveShape c:   c.Xy = xy; break;
            case PathShape p:    p.Xy = xy; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a vertex-list shape");
        }
    }

    /// <summary>Null for <see cref="PolygonShape"/> (no edge list — every edge is implicitly Line).</summary>
    internal static List<LayoutEdge>? EdgesOf(LayoutShape shape) => shape switch
    {
        CurveShape c => c.Edges,
        PathShape p  => p.Edges,
        _ => null,
    };

    private static void SetEdges(LayoutShape shape, List<LayoutEdge> edges)
    {
        switch (shape)
        {
            case CurveShape c: c.Edges = edges; break;
            case PathShape p:  p.Edges = edges; break;
            // PolygonShape: promotion (ConvertEdge) is the only path that gives it an edge list.
        }
    }

    internal static bool IsClosed(LayoutShape shape) => shape is PolygonShape or CurveShape;

    private static LayoutEdge CloneEdge(LayoutEdge e) =>
        new() { Kind = e.Kind, Bulge = e.Bulge, C1X = e.C1X, C1Y = e.C1Y, C2X = e.C2X, C2Y = e.C2Y };

    public static bool IsVertexListShape(LayoutShape shape) => shape is PolygonShape or CurveShape or PathShape;

    /// <summary>True when edge <paramref name="edgeIndex"/> is Line-kind (or the shape has no edge
    /// list at all, e.g. <see cref="PolygonShape"/>, where every edge is implicitly Line).</summary>
    public static bool IsStraightEdge(LayoutShape shape, int edgeIndex)
    {
        var edges = EdgesOf(shape);
        if (edges is null) return true;
        return edgeIndex >= edges.Count || edges[edgeIndex].Kind == EdgeKind.Line;
    }

    // ── Vertex move (R-L1d gesture: "Drag vertex") ─────────────────────────────────────────

    public static LayoutShape SetVertex(LayoutShape shape, int vertexIndex, long x, long y)
    {
        var clone = LayoutGeometry.Clone(shape);
        var xy = XyOf(clone);
        xy[2 * vertexIndex] = x;
        xy[2 * vertexIndex + 1] = y;
        return clone;
    }

    // ── Edge translate (R-L1d gesture: "Drag edge midpoint / edge line") ───────────────────
    // Moves ONLY the edge's two endpoints by the caller-supplied (already-perpendicular,
    // already-snapped) delta; every other vertex is untouched. Known simplification: an ADJACENT
    // Arc/Cubic edge's own curvature (bulge / absolute control points) is not re-anchored — widening
    // a straight run next to a curved one may visually detach the curve at that shared vertex. Not
    // exercised by any L1d gate; a candidate for a follow-up if it proves to matter in practice.

    public static LayoutShape TranslateEdgeEndpoints(LayoutShape shape, int edgeIndex, long dx, long dy)
    {
        var xy = XyOf(shape);
        int n = xy.Length / 2;
        bool closed = IsClosed(shape);
        int j = closed ? (edgeIndex + 1) % n : edgeIndex + 1;

        var clone = LayoutGeometry.Clone(shape);
        var cxy = XyOf(clone);
        cxy[2 * edgeIndex] += dx; cxy[2 * edgeIndex + 1] += dy;
        cxy[2 * j] += dx; cxy[2 * j + 1] += dy;
        return clone;
    }

    /// <summary>Same gesture as <see cref="TranslateEdgeEndpoints"/> for a <c>Rect</c>/<c>RoundedRect</c>,
    /// which has no vertex list to move two endpoints of — a rectangle's 4 edges are each defined by a
    /// SINGLE field (Y1=bottom, X2=right, Y2=top, X1=left, matching
    /// <see cref="LayoutHandles"/>'s edge-index convention), so "translate this edge perpendicular to
    /// itself" is just adding the already-snapped scalar <paramref name="delta"/> to that one field.
    /// Not normalized here — like <see cref="ResizeRectCorner"/>, the caller normalizes once at commit
    /// so a mid-drag "inside-out" rect (edge dragged past its opposite edge) stays well-defined
    /// throughout the whole gesture.</summary>
    public static RectShape TranslateRectEdge(RectShape shape, int edgeIndex, long delta)
    {
        var clone = (RectShape)LayoutGeometry.Clone(shape);
        switch (edgeIndex)
        {
            case 0: clone.Y1 += delta; break; // bottom
            case 1: clone.X2 += delta; break; // right
            case 2: clone.Y2 += delta; break; // top
            case 3: clone.X1 += delta; break; // left
        }
        return clone;
    }

    public static RoundedRectShape TranslateRoundedRectEdge(RoundedRectShape shape, int edgeIndex, long delta)
    {
        var clone = (RoundedRectShape)LayoutGeometry.Clone(shape);
        switch (edgeIndex)
        {
            case 0: clone.Y1 += delta; break;
            case 1: clone.X2 += delta; break;
            case 2: clone.Y2 += delta; break;
            case 3: clone.X1 += delta; break;
        }
        return clone;
    }

    // ── Bulge / cubic control point (R-L1d gestures) ────────────────────────────────────────

    public static LayoutShape SetBulge(LayoutShape shape, int edgeIndex, double bulge)
    {
        var clone = LayoutGeometry.Clone(shape);
        var edges = EdgesOf(clone);
        if (edges is null || edgeIndex >= edges.Count) return clone;
        edges[edgeIndex] = new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge };
        return clone;
    }

    public static LayoutShape SetCubicControl(LayoutShape shape, int edgeIndex, int subIndex, long x, long y)
    {
        var clone = LayoutGeometry.Clone(shape);
        var edges = EdgesOf(clone);
        if (edges is null || edgeIndex >= edges.Count) return clone;
        var e = edges[edgeIndex];
        if (subIndex == 0) { e.C1X = x; e.C1Y = y; } else { e.C2X = x; e.C2Y = y; }
        return clone;
    }

    // ── Circle radius / RoundedRect corner radius ───────────────────────────────────────────

    public static CircleShape SetRadius(CircleShape shape, long radius)
    {
        var clone = (CircleShape)LayoutGeometry.Clone(shape);
        clone.R = Math.Max(0, radius);
        return clone;
    }

    public static RoundedRectShape SetCornerRadius(RoundedRectShape shape, long radius)
    {
        var clone = (RoundedRectShape)LayoutGeometry.Clone(shape);
        long x1 = Math.Min(shape.X1, shape.X2), x2 = Math.Max(shape.X1, shape.X2);
        long y1 = Math.Min(shape.Y1, shape.Y2), y2 = Math.Max(shape.Y1, shape.Y2);
        long maxRadius = Math.Min(x2 - x1, y2 - y1) / 2;
        clone.CornerRadius = Math.Clamp(radius, 0, maxRadius);
        return clone;
    }

    // ── Rect / RoundedRect corner resize (normalize only at commit — see ResizeRectCorner doc) ──

    /// <summary>Moves one corner (0=(X1,Y1), 1=(X2,Y1), 2=(X2,Y2), 3=(X1,Y2)) — during a live drag
    /// the rect may go "inside-out" (X1&gt;X2), which is fine; the renderer already normalizes for
    /// display. Call <see cref="NormalizeRect"/> once, at commit, so the corner-index mapping stays
    /// simple and stable throughout the whole drag.</summary>
    public static RectShape ResizeRectCorner(RectShape shape, int cornerIndex, long x, long y)
    {
        var clone = (RectShape)LayoutGeometry.Clone(shape);
        switch (cornerIndex)
        {
            case 0: clone.X1 = x; clone.Y1 = y; break;
            case 1: clone.X2 = x; clone.Y1 = y; break;
            case 2: clone.X2 = x; clone.Y2 = y; break;
            case 3: clone.X1 = x; clone.Y2 = y; break;
        }
        return clone;
    }

    public static RoundedRectShape ResizeRoundedRectCorner(RoundedRectShape shape, int cornerIndex, long x, long y)
    {
        var clone = (RoundedRectShape)LayoutGeometry.Clone(shape);
        switch (cornerIndex)
        {
            case 0: clone.X1 = x; clone.Y1 = y; break;
            case 1: clone.X2 = x; clone.Y1 = y; break;
            case 2: clone.X2 = x; clone.Y2 = y; break;
            case 3: clone.X1 = x; clone.Y2 = y; break;
        }
        return clone;
    }

    public static RectShape NormalizeRect(RectShape r) => new()
    {
        Layer = r.Layer, Net = r.Net,
        X1 = Math.Min(r.X1, r.X2), Y1 = Math.Min(r.Y1, r.Y2),
        X2 = Math.Max(r.X1, r.X2), Y2 = Math.Max(r.Y1, r.Y2),
    };

    public static RoundedRectShape NormalizeRoundedRect(RoundedRectShape rr) => new()
    {
        Layer = rr.Layer, Net = rr.Net,
        X1 = Math.Min(rr.X1, rr.X2), Y1 = Math.Min(rr.Y1, rr.Y2),
        X2 = Math.Max(rr.X1, rr.X2), Y2 = Math.Max(rr.Y1, rr.Y2),
        CornerRadius = rr.CornerRadius,
    };

    // ── Remove vertex (blocked below 3 for a closed shape, below 2 for a Path) ──────────────

    public static LayoutShape? RemoveVertex(LayoutShape shape, int vertexIndex)
    {
        if (!IsVertexListShape(shape)) return null;
        var xy = XyOf(shape);
        int n = xy.Length / 2;
        bool closed = IsClosed(shape);
        int minCount = closed ? 3 : 2;
        if (n <= minCount || vertexIndex < 0 || vertexIndex >= n) return null;

        var clone = LayoutGeometry.Clone(shape);
        var cxy = XyOf(clone);
        var newXy = new long[(n - 1) * 2];
        for (int i = 0, k = 0; i < n; i++)
        {
            if (i == vertexIndex) continue;
            newXy[2 * k] = cxy[2 * i]; newXy[2 * k + 1] = cxy[2 * i + 1]; k++;
        }
        SetXy(clone, newXy);

        var edges = EdgesOf(clone);
        if (edges is not null)
        {
            // An open Path's true endpoint (vertex 0 or n-1) has only ONE adjacent edge — that edge
            // is simply dropped (shortens the path by one segment), never replaced. Every other
            // vertex (any vertex of a closed shape; a MIDDLE vertex of a Path) has both an entering
            // and a leaving edge, which collapse into one new straight Line edge connecting the two
            // former neighbors directly (curvature is not preserved through a removed vertex — a
            // deliberate simplification; there is no principled way to merge two arbitrary curved
            // edges into one equivalent curve).
            bool isOpenEndpoint = !closed && (vertexIndex == 0 || vertexIndex == n - 1);
            int edgeCount = closed ? n : n - 1;
            var newEdges = new List<LayoutEdge>();
            for (int i = 0; i < edgeCount; i++)
            {
                int viFrom = i, viTo = closed ? (i + 1) % n : i + 1;
                if (viFrom == vertexIndex) continue; // the "leaving" edge — dropped
                if (viTo == vertexIndex)
                {
                    if (!isOpenEndpoint) newEdges.Add(new LayoutEdge { Kind = EdgeKind.Line });
                    continue;
                }
                newEdges.Add(i < edges.Count ? CloneEdge(edges[i]) : new LayoutEdge());
            }
            SetEdges(clone, newEdges);
        }

        return clone;
    }

    // ── Insert vertex (Ctrl/Cmd+click an edge) ──────────────────────────────────────────────
    // A straight (Line) edge inserts at the SNAPPED click point — an ordinary new vertex. A curved
    // (Arc/Cubic) edge instead splits AT THE EXACT PARAMETER nearest the click, deliberately NOT
    // snapped: the new vertex must lie exactly on the original curve (both resulting sub-edges share
    // the source arc's center/radius, or are an exact de Casteljau split of the source cubic) so the
    // shape is visually unchanged (gate 7) — forcing that point onto the snap grid would pull it off
    // the curve and distort it. This mirrors §1.5 R5's "an off-grid vertex is merely unusual, not a
    // bug" — a curve-preserving split point is exactly such a legitimate off-grid vertex.

    public static LayoutShape InsertVertexOnEdge(LayoutShape shape, int edgeIndex, long clickX, long clickY, long snapDbu, bool suspendSnap)
    {
        var xy = XyOf(shape);
        int n = xy.Length / 2;
        bool closed = IsClosed(shape);
        int j = closed ? (edgeIndex + 1) % n : edgeIndex + 1;
        long x0 = xy[2 * edgeIndex], y0 = xy[2 * edgeIndex + 1];
        long x1 = xy[2 * j], y1 = xy[2 * j + 1];
        var edges = EdgesOf(shape);
        var edge = edges is not null && edgeIndex < edges.Count ? edges[edgeIndex] : null;

        switch (edge?.Kind ?? EdgeKind.Line)
        {
            case EdgeKind.Arc when edge is not null:
            {
                var arc = LayoutArc.FromBulge(x0, y0, x1, y1, edge.Bulge);
                if (arc.R <= 0) goto default;

                double clickAngle = Math.Atan2(clickY - arc.Cy, clickX - arc.Cx);
                double splitAngle = ClampAngleToSweep(arc.StartAngle, arc.Sweep, clickAngle);
                long sx = (long)Math.Round(arc.Cx + arc.R * Math.Cos(splitAngle));
                long sy = (long)Math.Round(arc.Cy + arc.R * Math.Sin(splitAngle));

                double sweep1 = splitAngle - arc.StartAngle;
                double sweep2 = (arc.StartAngle + arc.Sweep) - splitAngle;
                var e1 = new LayoutEdge { Kind = EdgeKind.Arc, Bulge = LayoutArc.ToBulge(sweep1) };
                var e2 = new LayoutEdge { Kind = EdgeKind.Arc, Bulge = LayoutArc.ToBulge(sweep2) };
                return InsertVertexAt(shape, edgeIndex, sx, sy, e1, e2);
            }

            case EdgeKind.Cubic when edge is not null:
            {
                double t = NearestTOnCubic(x0, y0, edge.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1, clickX, clickY);
                var (q0, r0, s, r1, q2) = SplitCubic(x0, y0, edge.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1, t);
                long sx = (long)Math.Round(s.X), sy = (long)Math.Round(s.Y);
                var e1 = new LayoutEdge
                {
                    Kind = EdgeKind.Cubic,
                    C1X = (long)Math.Round(q0.X), C1Y = (long)Math.Round(q0.Y),
                    C2X = (long)Math.Round(r0.X), C2Y = (long)Math.Round(r0.Y),
                };
                var e2 = new LayoutEdge
                {
                    Kind = EdgeKind.Cubic,
                    C1X = (long)Math.Round(r1.X), C1Y = (long)Math.Round(r1.Y),
                    C2X = (long)Math.Round(q2.X), C2Y = (long)Math.Round(q2.Y),
                };
                return InsertVertexAt(shape, edgeIndex, sx, sy, e1, e2);
            }

            default:
            {
                var (sx, sy) = LayoutSnapping.SnapPoint(clickX, clickY, snapDbu, suspendSnap);
                return InsertVertexAt(shape, edgeIndex, sx, sy,
                    new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line });
            }
        }
    }

    private static LayoutShape InsertVertexAt(LayoutShape shape, int edgeIndex, long x, long y, LayoutEdge first, LayoutEdge second)
    {
        var xy = XyOf(shape);
        int n = xy.Length / 2;
        bool closed = IsClosed(shape);
        int insertAt = edgeIndex + 1;

        var clone = LayoutGeometry.Clone(shape);
        var cxy = XyOf(clone);
        var newXy = new long[(n + 1) * 2];
        Array.Copy(cxy, 0, newXy, 0, insertAt * 2);
        newXy[insertAt * 2] = x; newXy[insertAt * 2 + 1] = y;
        Array.Copy(cxy, insertAt * 2, newXy, (insertAt + 1) * 2, (n - insertAt) * 2);
        SetXy(clone, newXy);

        var edges = EdgesOf(clone);
        if (edges is not null)
        {
            int edgeCount = closed ? n : n - 1;
            var newEdges = new List<LayoutEdge>();
            for (int i = 0; i < edgeCount; i++)
            {
                if (i == edgeIndex) { newEdges.Add(first); newEdges.Add(second); }
                else newEdges.Add(i < edges.Count ? CloneEdge(edges[i]) : new LayoutEdge());
            }
            SetEdges(clone, newEdges);
        }

        return clone;
    }

    // ── Edge kind conversion + the promotion rule (R-L1d-3, §4) ─────────────────────────────
    // PolygonShape carries no edge list, so converting one of ITS edges away from Line REPLACES it
    // with an equivalent CurveShape — same layer, net, vertices; the caller swaps it in at the
    // SAME index via ReplaceShapeCommand, which is what makes this "the same shape, now curved"
    // rather than a new object elsewhere in the list. A PathShape already carries an edge list and
    // simply gains the converted edge in place — no type change. Reverse demotion (all-Line again)
    // is deliberately NOT automatic — leaving a CurveShape with every edge Line is acceptable and
    // simpler, per the brief.

    public static LayoutShape ConvertEdge(LayoutShape shape, int edgeIndex, EdgeKind newKind)
    {
        LayoutShape working = shape is PolygonShape poly
            ? new CurveShape { Layer = poly.Layer, Net = poly.Net, Xy = (long[])poly.Xy.Clone() }
            : LayoutGeometry.Clone(shape);

        var xy = XyOf(working);
        int n = xy.Length / 2;
        bool closed = IsClosed(working);
        int j = closed ? (edgeIndex + 1) % n : edgeIndex + 1;
        long x0 = xy[2 * edgeIndex], y0 = xy[2 * edgeIndex + 1];
        long x1 = xy[2 * j], y1 = xy[2 * j + 1];

        var edges = EdgesOf(working);
        if (edges is null)
        {
            edges = [];
            int edgeCount = closed ? n : n - 1;
            for (int i = 0; i < edgeCount; i++) edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
            SetEdges(working, edges);
        }
        while (edges.Count <= edgeIndex) edges.Add(new LayoutEdge { Kind = EdgeKind.Line });

        edges[edgeIndex] = newKind switch
        {
            EdgeKind.Line  => new LayoutEdge { Kind = EdgeKind.Line },
            EdgeKind.Arc   => new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0 }, // a straight arc, immediately draggable via its new bulge handle
            EdgeKind.Cubic => ThirdPointsCubic(x0, y0, x1, y1),                  // control points at 1/3 and 2/3 — initial shape unchanged
            _ => throw new ArgumentOutOfRangeException(nameof(newKind), newKind, null),
        };

        return working;
    }

    private static LayoutEdge ThirdPointsCubic(long x0, long y0, long x1, long y1) => new()
    {
        Kind = EdgeKind.Cubic,
        C1X = x0 + (x1 - x0) / 3, C1Y = y0 + (y1 - y0) / 3,
        C2X = x0 + 2 * (x1 - x0) / 3, C2Y = y0 + 2 * (y1 - y0) / 3,
    };

    // ── Edge-line hit-testing (Ctrl+click insert, plain-click edge drag) ────────────────────

    /// <summary>Nearest edge (by index) within <paramref name="tolDbu"/> of (x,y), or null. Tests
    /// every edge kind against its true geometry (segment / arc / sampled cubic). A <c>Rect</c>/
    /// <c>RoundedRect</c> has no vertex list, so its 4 axis-aligned edges are tested directly against
    /// its corners in the same edge-index order <see cref="LayoutHandles"/>/<see cref="TranslateRectEdge"/>
    /// use — this is what lets "drag edge midpoint / edge line" work on those shapes too.</summary>
    public static int? FindEdgeLineHit(LayoutShape shape, long px, long py, long tolDbu)
    {
        if (shape is RectShape or RoundedRectShape) return FindAxisAlignedEdgeLineHit(shape, px, py, tolDbu);
        if (!IsVertexListShape(shape)) return null;
        var xy = XyOf(shape);
        int n = xy.Length / 2;
        if (n < 2) return null;
        bool closed = IsClosed(shape);
        var edges = EdgesOf(shape);
        int edgeCount = closed ? n : n - 1;

        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1], x1 = xy[2 * j], y1 = xy[2 * j + 1];
            var edge = edges is not null && i < edges.Count ? edges[i] : null;

            double dist = edge?.Kind switch
            {
                EdgeKind.Arc   => DistanceToArc(x0, y0, x1, y1, edge.Bulge, px, py),
                EdgeKind.Cubic => DistanceToCubicSampled(x0, y0, edge.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1, px, py),
                _ => DistanceToSegment(px, py, x0, y0, x1, y1),
            };
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return bestDist <= tolDbu ? best : null;
    }

    private static int? FindAxisAlignedEdgeLineHit(LayoutShape shape, long px, long py, long tolDbu)
    {
        var (x1, y1, x2, y2) = shape switch
        {
            RectShape r => (r.X1, r.Y1, r.X2, r.Y2),
            RoundedRectShape rr => (rr.X1, rr.Y1, rr.X2, rr.Y2),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not an axis-aligned rect shape"),
        };
        // Same corner order as LayoutHandles.BuildRectCorners: 0=(x1,y1) 1=(x2,y1) 2=(x2,y2) 3=(x1,y2).
        Span<long> cx = [x1, x2, x2, x1];
        Span<long> cy = [y1, y1, y2, y2];

        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            double dist = DistanceToSegment(px, py, cx[i], cy[i], cx[j], cy[j]);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return bestDist <= tolDbu ? best : null;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9) return Distance(px, py, ax, ay);
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
        return Distance(px, py, ax + t * dx, ay + t * dy);
    }

    private static double DistanceToArc(long x0, long y0, long x1, long y1, double bulge, long px, long py)
    {
        if (bulge == 0) return DistanceToSegment(px, py, x0, y0, x1, y1);
        var arc = LayoutArc.FromBulge(x0, y0, x1, y1, bulge);
        if (arc.R <= 0) return DistanceToSegment(px, py, x0, y0, x1, y1);

        double angle = Math.Atan2(py - arc.Cy, px - arc.Cx);
        double clamped = ClampAngleToSweep(arc.StartAngle, arc.Sweep, angle);
        double nx = arc.Cx + arc.R * Math.Cos(clamped), ny = arc.Cy + arc.R * Math.Sin(clamped);
        return Distance(px, py, nx, ny);
    }

    private static double DistanceToCubicSampled(long x0, long y0, long c1x, long c1y, long c2x, long c2y, long x1, long y1, long px, long py)
    {
        const int samples = 24;
        double best = double.MaxValue;
        double prevX = x0, prevY = y0;
        for (int i = 1; i <= samples; i++)
        {
            double t = (double)i / samples;
            var (x, y) = CubicPoint(x0, y0, c1x, c1y, c2x, c2y, x1, y1, t);
            double d = DistanceToSegment(px, py, prevX, prevY, x, y);
            if (d < best) best = d;
            prevX = x; prevY = y;
        }
        return best;
    }

    private static double Distance(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Clamps a candidate angle to the arc's own sweep range, expressed as a fraction of
    /// the sweep in [0.02, 0.98] — avoids a degenerate (zero-length) split when the click is very
    /// close to either endpoint, and handles direction (sweep may be negative) and wraparound.</summary>
    private static double ClampAngleToSweep(double start, double sweep, double angle)
    {
        const double twoPi = 2.0 * Math.PI;
        double dir = Math.Sign(sweep);
        if (dir == 0) return start;
        double diff = dir * (angle - start);
        diff = ((diff % twoPi) + twoPi) % twoPi;
        double sweepAbs = Math.Abs(sweep);
        double frac = sweepAbs > 1e-9 ? diff / sweepAbs : 0.5;
        frac = Math.Clamp(frac, 0.02, 0.98);
        return start + dir * frac * sweepAbs;
    }

    // ── Cubic Bézier helpers (exact de Casteljau split) ─────────────────────────────────────

    private static (double X, double Y) Lerp(double ax, double ay, double bx, double by, double t) =>
        (ax + (bx - ax) * t, ay + (by - ay) * t);

    private static (double X, double Y) CubicPoint(double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1, double t)
    {
        var (q0x, q0y) = Lerp(x0, y0, c1x, c1y, t);
        var (q1x, q1y) = Lerp(c1x, c1y, c2x, c2y, t);
        var (q2x, q2y) = Lerp(c2x, c2y, x1, y1, t);
        var (r0x, r0y) = Lerp(q0x, q0y, q1x, q1y, t);
        var (r1x, r1y) = Lerp(q1x, q1y, q2x, q2y, t);
        return Lerp(r0x, r0y, r1x, r1y, t);
    }

    /// <summary>de Casteljau split at parameter t — mathematically EXACT regardless of which t is
    /// chosen (the two resulting cubics always trace the identical original curve); only WHERE the
    /// split lands depends on t. Returns (Q0, R0, S, R1, Q2): Q0/R0 are the first sub-curve's control
    /// points, S is the shared split point (also the new vertex), R1/Q2 are the second sub-curve's.</summary>
    private static ((double X, double Y) Q0, (double X, double Y) R0, (double X, double Y) S, (double X, double Y) R1, (double X, double Y) Q2)
        SplitCubic(double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1, double t)
    {
        var q0 = Lerp(x0, y0, c1x, c1y, t);
        var q1 = Lerp(c1x, c1y, c2x, c2y, t);
        var q2 = Lerp(c2x, c2y, x1, y1, t);
        var r0 = Lerp(q0.Item1, q0.Item2, q1.Item1, q1.Item2, t);
        var r1 = Lerp(q1.Item1, q1.Item2, q2.Item1, q2.Item2, t);
        var s = Lerp(r0.Item1, r0.Item2, r1.Item1, r1.Item2, t);
        return (q0, r0, s, r1, q2);
    }

    /// <summary>Approximate nearest parameter t (sampled, then clamped away from the endpoints) —
    /// only affects WHERE the split lands, never whether the resulting curve is exact (see
    /// <see cref="SplitCubic"/>).</summary>
    private static double NearestTOnCubic(double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1, long clickX, long clickY)
    {
        const int samples = 32;
        double bestT = 0.5, bestDist = double.MaxValue;
        for (int i = 1; i < samples; i++)
        {
            double t = (double)i / samples;
            var (x, y) = CubicPoint(x0, y0, c1x, c1y, c2x, c2y, x1, y1, t);
            double dx = x - clickX, dy = y - clickY;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; bestT = t; }
        }
        return Math.Clamp(bestT, 0.02, 0.98);
    }
}
