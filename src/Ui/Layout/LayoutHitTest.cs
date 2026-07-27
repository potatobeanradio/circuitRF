// Framework-free hit-testing (docs/design/layout-view.md §6.2/§6.3, brief-L1c-selection-and-properties).
// No spatial index — L1c iterates shapes linearly, which is correct for this phase (§5.2's R-tree is
// L2; this signature deliberately does not presuppose one). Hit-testing is a screen-to-world feature:
// callers convert a ~4 px tolerance into DBU using the CURRENT zoom, per query, never cached — see
// the brief's "Read first" section for why a cached/derived-from-SnapDbu tolerance is the exact class
// of bug the L1b/L1-fix round already made once.

using System.Linq;

namespace CircuitRF.Ui.Layout;

public static class LayoutHitTest
{
    /// <summary>
    /// Returns shape indices under (x,y) within <paramref name="tolDbu"/>, ordered per §6.2:
    /// <c>ZOrder</c> descending, then ascending bbox area (so a small shape sitting on a large one on
    /// the SAME layer is reachable), then ascending list index as a deterministic tie-break.
    /// Skips shapes on layers whose resolved <see cref="LayerDef"/> is <c>Visible == false</c> or
    /// <c>Selectable == false</c>; unknown layers resolve through <see cref="FallbackPalette"/> and
    /// are always selectable.
    /// </summary>
    public static IReadOnlyList<int> HitStack(LayoutView view, Technology? tech, long x, long y, long tolDbu)
    {
        tolDbu = Math.Max(tolDbu, 0);
        var layerMap = tech?.Layers.ToDictionary(l => l.Key);
        var candidates = new List<(int ZOrder, double Area, int Index)>();

        // L2b: the tolerance is still computed per query from the live viewport by the caller and
        // expands the QUERY rect here — it is never cached or index-derived (docs/sonnet-briefs/
        // brief-L2b-spatial-index.md §3). The per-shape exact test (HitTestShape) and the ordering
        // below are byte-for-byte the same as the pre-index linear scan; only which shapes are
        // CONSIDERED changes.
        var queryRect = new Bbox(x - tolDbu, y - tolDbu, x + tolDbu, y + tolDbu);
        foreach (var i in view.SpatialIndex.QueryIntersecting(view.Shapes, queryRect))
        {
            var shape = view.Shapes[i];
            LayerDef def = layerMap is not null && layerMap.TryGetValue(shape.Layer, out var found)
                ? found
                : FallbackPalette.For(shape.Layer);
            if (!def.Visible || !def.Selectable) continue;

            if (!HitTestShape(shape, x, y, tolDbu, tech)) continue;

            var bb = LayoutGeometry.BboxOf(shape);
            double area = bb.IsEmpty ? 0.0 : (double)(bb.MaxX - bb.MinX) * (bb.MaxY - bb.MinY);
            candidates.Add((def.ZOrder, area, i));
        }

        candidates.Sort(static (a, b) =>
        {
            int c = b.ZOrder.CompareTo(a.ZOrder);   // ZOrder descending
            if (c != 0) return c;
            c = a.Area.CompareTo(b.Area);            // ascending area
            if (c != 0) return c;
            return a.Index.CompareTo(b.Index);       // ascending index — deterministic tie-break
        });

        return candidates.Select(c => c.Index).ToArray();
    }

    // ── Per-shape tests ────────────────────────────────────────────────────────

    private static bool HitTestShape(LayoutShape shape, long x, long y, long tolDbu, Technology? tech)
    {
        switch (shape)
        {
            case RectShape:
            case PolygonShape:
            case RoundedRectShape:
            case CircleShape:
            case CurveShape:
            {
                // Flatten at least as finely as the click tolerance itself (never coarser than the
                // shape/tech default) so the polygon approximation can't itself hide a hit near a
                // curved edge.
                long shapeTol = LayoutFlattener.ResolveTolDbu(shape, tech);
                long flattenTol = Math.Max(1, Math.Min(shapeTol, Math.Max(tolDbu, 1)));
                var rings = LayoutFlattener.Flatten(shape, flattenTol);

                // §3.1a holes: element 0 is the outer ring, every following element a hole. A point
                // near ANY ring's edge (outer or hole boundary) is a hit — the boundary is still part
                // of the shape. A point strictly inside a hole is NOT a hit, even though it is inside
                // the outer ring, since the hole is a cut-out of the filled region.
                var outer = rings[0];
                if (DistanceToRingEdges(outer, x, y) <= tolDbu) return true;
                if (!PointInPolygon(outer, x, y)) return false;

                for (int i = 1; i < rings.Count; i++)
                {
                    var hole = rings[i];
                    if (DistanceToRingEdges(hole, x, y) <= tolDbu) return true;
                    if (PointInPolygon(hole, x, y)) return false;
                }
                return true;
            }

            case PathShape path:
            {
                long shapeTol = LayoutFlattener.ResolveTolDbu(shape, tech);
                long flattenTol = Math.Max(1, Math.Min(shapeTol, Math.Max(tolDbu, 1)));
                var centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, flattenTol);
                double dist = DistanceToPolyline(centerline, x, y);
                return dist <= path.Width / 2.0 + tolDbu;
            }

            case ViaShape via:
            {
                double dx = x - via.X, dy = y - via.Y;
                return Math.Sqrt(dx * dx + dy * dy) <= via.PadSize / 2.0 + tolDbu;
            }

            case LabelShape label:
            {
                var bb = LabelHitBbox(label);
                var grown = new Bbox(bb.MinX - tolDbu, bb.MinY - tolDbu, bb.MaxX + tolDbu, bb.MaxY + tolDbu);
                return grown.Contains(x, y);
            }

            case BitmapShape bmp:
            {
                // Full participation in select/move/scale (§3) — a plain rect hit-test against the
                // placement bbox, grown by the click tolerance, same shape as ViaShape/LabelShape above.
                var grown = new Bbox(bmp.X - tolDbu, bmp.Y - tolDbu, bmp.X + bmp.W + tolDbu, bmp.Y + bmp.H + tolDbu);
                return grown.Contains(x, y);
            }

            default:
                return false;
        }
    }

    /// <summary>Approximate label footprint — framework-free, no font metrics available at this
    /// layer, so a label's hit box is a monospace-ish estimate from its character count and text
    /// height rather than an exact glyph measurement (that lives in the renderer, in Skia, at
    /// display time). Anchor convention matches <c>LayoutRenderer.DrawLabelText</c>: (X,Y) is the
    /// baseline-left origin, the box extends right and up before rotation.</summary>
    private const double LabelApproxCharWidthRatio = 0.62;

    private static Bbox LabelHitBbox(LabelShape label)
    {
        if (string.IsNullOrEmpty(label.Text))
            return new Bbox(label.X, label.Y, label.X, label.Y);

        long w = Math.Max(1, (long)Math.Round(label.Text.Length * label.Height * LabelApproxCharWidthRatio));
        long h = Math.Max(1, label.Height);

        // Owner report: the R90/R270 selection box rendered in the completely wrong spot — this table
        // had the local "far corner" (the text's top-right in its own pre-rotation frame) landing on
        // the WRONG SIDE of the anchor for a 90°-rotated label. Verified against the actual rendered
        // transform (LayoutRenderer.DrawLabelText: translate to the anchor, THEN rotate, THEN draw at
        // local (0,0) extending +X/-Y) via each rotation's real corner mapping — R0 and R180 were
        // already correct; only R90/R270's X range was backwards.
        return label.Rotation switch
        {
            LayoutRotation.R0   => new Bbox(label.X, label.Y, label.X + w, label.Y + h),
            LayoutRotation.R90  => new Bbox(label.X - h, label.Y, label.X, label.Y + w),
            LayoutRotation.R180 => new Bbox(label.X - w, label.Y - h, label.X, label.Y),
            LayoutRotation.R270 => new Bbox(label.X, label.Y - w, label.X + h, label.Y),
            _                   => new Bbox(label.X, label.Y, label.X + w, label.Y + h),
        };
    }

    // ── Geometry primitives ───────────────────────────────────────────────────

    private static bool PointInPolygon(long[] ring, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            bool crosses = (yi > py) != (yj > py)
                && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static double DistanceToRingEdges(long[] ring, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 2) return double.MaxValue;

        double min = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double d = DistanceToSegment(px, py, ring[2 * i], ring[2 * i + 1], ring[2 * j], ring[2 * j + 1]);
            if (d < min) min = d;
        }
        return min;
    }

    private static double DistanceToPolyline(long[] pts, long px, long py)
    {
        int n = pts.Length / 2;
        if (n == 0) return double.MaxValue;
        if (n == 1) return Distance(px, py, pts[0], pts[1]);

        double min = double.MaxValue;
        for (int i = 0; i < n - 1; i++)
        {
            double d = DistanceToSegment(px, py, pts[2 * i], pts[2 * i + 1], pts[2 * (i + 1)], pts[2 * (i + 1) + 1]);
            if (d < min) min = d;
        }
        return min;
    }

    private static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9) return Distance(px, py, ax, ay);

        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
        return Distance(px, py, ax + t * dx, ay + t * dy);
    }

    private static double Distance(double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
