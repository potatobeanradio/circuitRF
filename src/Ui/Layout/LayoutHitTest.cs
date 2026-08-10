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

    // ── Instances (L3a, docs/sonnet-briefs/brief-L3a-instances-and-arrays.md R-L3a-5) ──────────────
    // "Clicking an instance selects the instance, not its contents" — hit-testing descends into the
    // sub-cell only far enough to decide whether the point is on ACTUAL GEOMETRY, never treating the
    // whole bbox as one giant click target (which would make every array select on any click inside
    // its overall extent). A broken/unresolved instance is the one exception: with no real geometry to
    // test, its WHOLE placeholder box (array-expanded) is the target, matching R-L3a-1's "stays fully
    // selectable and movable."

    /// <summary>Instance indices under (x,y) within <paramref name="tolDbu"/>, topmost first (list
    /// order — instances have no <c>ZOrder</c>/layer of their own, so "topmost" is simply "drawn
    /// last," i.e. descending index).</summary>
    public static IReadOnlyList<int> HitInstanceStack(LayoutView view, Technology? tech, string baseDir, long x, long y, long tolDbu)
    {
        tolDbu = Math.Max(tolDbu, 0);
        var queryRect = new Bbox(x - tolDbu, y - tolDbu, x + tolDbu, y + tolDbu);
        Bbox InstanceBboxFor(LayoutInstance i) => CellHierarchy.InstanceBbox(i, baseDir);

        var hits = new List<int>();
        foreach (var entry in view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, InstanceBboxFor, CellLayoutResolver.Generation, queryRect))
        {
            if (entry.Kind != SpatialEntryKind.Instance) continue;
            int idx = entry.Index;
            if (idx < 0 || idx >= view.Instances.Count) continue;
            if (InstanceHitTest(view.Instances[idx], tech, baseDir, x, y, tolDbu)) hits.Add(idx);
        }
        hits.Sort(static (a, b) => b.CompareTo(a)); // descending index = topmost (last drawn) first
        return hits;
    }

    private static bool InstanceHitTest(LayoutInstance inst, Technology? tech, string baseDir, long px, long py, long tolDbu)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = CellHierarchy.ResolveForWalk(inst, baseDir, visiting, 0);
        if (step.State != InstanceResolutionState.Resolved)
            return ArrayCellsContain(inst, CellHierarchy.PlaceholderBbox(inst), px, py, tolDbu);

        visiting.Add(step.ResolvedCellDir!);
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        bool found = false;
        for (int r = 0; r < rows && !found; r++)
        for (int c = 0; c < cols && !found; c++)
        {
            var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(px, py, inst, r, c);
            long localTol = (long)Math.Round(tolDbu / Math.Max(Math.Abs(inst.Mag), 1e-9));
            found = CellGeometryHitTest(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!),
                (long)Math.Round(lx), (long)Math.Round(ly), localTol, visiting, 1);
        }
        visiting.Remove(step.ResolvedCellDir!);
        return found;
    }

    /// <summary>Whether (px,py) (grown by <paramref name="tolDbu"/>) falls inside ANY array cell of
    /// <paramref name="baseBbox"/> — used only for the broken/unresolved placeholder case, where the
    /// whole box (not real geometry within it) is the click target.</summary>
    private static bool ArrayCellsContain(LayoutInstance inst, Bbox baseBbox, long px, long py, long tolDbu)
    {
        var grown = new Bbox(baseBbox.MinX - tolDbu, baseBbox.MinY - tolDbu, baseBbox.MaxX + tolDbu, baseBbox.MaxY + tolDbu);
        long w = grown.MaxX - grown.MinX, h = grown.MaxY - grown.MinY;
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var (ox, oy) = LayoutInstanceTransform.ArrayCellOrigin(inst, r, c);
            long dx = ox - inst.X, dy = oy - inst.Y;
            var cell = new Bbox(grown.MinX + dx, grown.MinY + dy, grown.MinX + dx + w, grown.MinY + dy + h);
            if (cell.Contains(px, py)) return true;
        }
        return false;
    }

    /// <summary>Recursive, depth-capped test of whether (x,y) (already in THIS view's own local frame,
    /// tolerance already scaled for the accumulated magnification) lands on real geometry — this view's
    /// own shapes, or (recursively) any of its own nested instances. Deliberately reuses
    /// <see cref="HitTestShape"/> unchanged — the SAME per-shape test a top-level click uses, so a
    /// shape's hit footprint can never silently differ between "directly on the canvas" and "reached
    /// through an instance."</summary>
    private static bool CellGeometryHitTest(LayoutView view, Technology? tech, string baseDir, long x, long y, long tolDbu, HashSet<string> visiting, int depth)
    {
        foreach (var shape in view.Shapes)
            if (HitTestShape(shape, x, y, tolDbu, tech)) return true;

        if (depth >= CellHierarchy.MaxDepth) return false;

        foreach (var nested in view.Instances)
        {
            var step = CellHierarchy.ResolveForWalk(nested, baseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved) continue; // a nested broken ref contributes no real geometry to hit

            visiting.Add(step.ResolvedCellDir!);
            int rows = Math.Max(1, nested.Rows), cols = Math.Max(1, nested.Cols);
            bool found = false;
            for (int r = 0; r < rows && !found; r++)
            for (int c = 0; c < cols && !found; c++)
            {
                var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(x, y, nested, r, c);
                long localTol = (long)Math.Round(tolDbu / Math.Max(Math.Abs(nested.Mag), 1e-9));
                found = CellGeometryHitTest(step.SubView!, tech, CellHierarchy.LayoutBaseDirOf(step.ResolvedCellDir!),
                    (long)Math.Round(lx), (long)Math.Round(ly), localTol, visiting, depth + 1);
            }
            visiting.Remove(step.ResolvedCellDir!);
            if (found) return true;
        }
        return false;
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
    /// baseline-left origin, the box extends right and up before rotation — except for a PORT, whose
    /// box is symmetric about the anchor (see the comment in the body).</summary>
    private const double LabelApproxCharWidthRatio = 0.62;

    private static Bbox LabelHitBbox(LabelShape label)
    {
        if (string.IsNullOrEmpty(label.Text))
            return new Bbox(label.X, label.Y, label.X, label.Y);

        long w = Math.Max(1, (long)Math.Round(label.Text.Length * label.Height * LabelApproxCharWidthRatio));
        long h = Math.Max(1, label.Height);

        // A PORT's pick region is SYMMETRIC about its anchor; an ordinary label's is not.
        //
        // Owner report, 2026-08-09: "when dragging the port, the snap distance appears asymmetric —
        // in the direction of the arrow it will snap farther than the opposite direction." Measured
        // on a 2-character port label of height H, the box below reaches 1.24·H in the text direction
        // (+x at R0, which is also where the arrow points) and ZERO the other way — the anchor sits on
        // the box's own corner. So the port could be grabbed from a long way ahead of it and only
        // within the click tolerance behind it.
        //
        // The asymmetry is CORRECT for an annotation, whose text genuinely runs one way from its
        // baseline-left origin. It is wrong for a port, because a port is a MARKER: what the user sees
        // and aims at is the plane bar and arrow, drawn about the conductor end, not the text. So a
        // port gets a square centred on the anchor at the LARGER of the two reaches — the owner's own
        // "the farther distance is working good right now for UX", made uniform rather than reduced.
        if (label.IsPort)
        {
            long half = Math.Max(w, h);
            return new Bbox(label.X - half, label.Y - half, label.X + half, label.Y + half);
        }

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
