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
    /// the SAME layer is reachable), <b>then — between POINT-LIKE shapes only — ascending distance
    /// from the query point to the anchor</b>, then ascending list index as a deterministic tie-break.
    ///
    /// <para><b>Why the distance term exists.</b> Owner report, 2026-08-25: "the port 2 hitbox is
    /// interfering with port 3, so I can't drag-select P3, even though port 2 is far from port 3."
    /// Measured on that file: the two anchors are 0.381 mm apart while each port's pick square — a
    /// deliberately generous one, see <see cref="LabelHitBbox"/> — is 2.52 mm across, so each anchor
    /// sits deep inside the other's box. <b>The area term cannot separate them, because
    /// <c>LayoutGeometry.BboxOf</c> of a label is a zero-area POINT</b>; both scored 0, and the sort
    /// fell straight through to ascending list index, so the port written earlier in the <c>.clay</c>
    /// won every overlapping pick and the later one could not be grabbed at all.</para>
    ///
    /// <para>The pick square is generous on purpose and must stay so — a port is a MARKER, and a
    /// user aims at its bar and arrow, not at a glyph. What was missing is that between two markers,
    /// <b>the one you are nearest is the one you meant</b>. The term is scoped to zero-area shapes so
    /// that ordering between real geometry is untouched: two overlapping rectangles of equal area
    /// still tie-break by index exactly as before.</para>
    /// Skips shapes on layers whose resolved <see cref="LayerDef"/> is <c>Visible == false</c> or
    /// <c>Selectable == false</c>; unknown layers resolve through <see cref="FallbackPalette"/> and
    /// are always selectable.
    /// </summary>
    /// <param name="portMarkerRegion">
    /// A port label's pick region, when the caller can supply one. <b>Ports are picked by their MARK
    /// rather than by their anchor</b> (2026-08-25), and the mark's position depends on the port's
    /// TYPE — which lives in the <c>.cem</c>, not in the layout — and on the conductor beneath it. A
    /// caller that knows both passes this; one that does not gets the anchor square, which is what
    /// every caller predating the change gets and is a strictly usable pick region.
    /// </param>
    public static IReadOnlyList<int> HitStack(LayoutView view, Technology? tech, long x, long y, long tolDbu,
                                              Func<LabelShape, Bbox>? portMarkerRegion = null)
    {
        tolDbu = Math.Max(tolDbu, 0);
        var layerMap = tech?.Layers.ToDictionary(l => l.Key);
        var candidates = new List<(int ZOrder, double Area, double Distance, int Index)>();

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

            if (shape is LabelShape { IsPort: true } portLabel && portMarkerRegion is not null)
            {
                var region = portMarkerRegion(portLabel);
                var grownPort = new Bbox(region.MinX - tolDbu, region.MinY - tolDbu,
                                         region.MaxX + tolDbu, region.MaxY + tolDbu);
                if (!grownPort.Contains(x, y)) continue;
            }
            else if (!HitTestShape(shape, x, y, tolDbu, tech)) continue;

            var bb = shape is LabelShape { IsPort: true } pl && portMarkerRegion is not null
                ? portMarkerRegion(pl)                       // rank a port by its MARK, as it is picked
                : LayoutGeometry.BboxOf(shape);
            double area = bb.IsEmpty ? 0.0 : (double)(bb.MaxX - bb.MinX) * (bb.MaxY - bb.MinY);
            if (shape is LabelShape { IsPort: true }) area = 0.0;   // still point-like for ordering

            // Recorded only for a point-like shape, and left at 0 for everything else, so this term
            // can only ever reorder shapes the area term already declared equal AND zero — see the
            // method note. Squared distance: the ordering is the same and there is no square root.
            double distance = 0.0;
            if (area == 0.0 && !bb.IsEmpty)
            {
                double cx = (bb.MinX + bb.MaxX) / 2.0 - x;
                double cy = (bb.MinY + bb.MaxY) / 2.0 - y;
                distance = cx * cx + cy * cy;
            }

            candidates.Add((def.ZOrder, area, distance, i));
        }

        candidates.Sort(static (a, b) =>
        {
            int c = b.ZOrder.CompareTo(a.ZOrder);   // ZOrder descending
            if (c != 0) return c;
            c = a.Area.CompareTo(b.Area);            // ascending area
            if (c != 0) return c;
            c = a.Distance.CompareTo(b.Distance);    // ascending distance — point-like shapes only
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

    /// <summary>Fallback label footprint, used only when real glyph metrics are unavailable — a
    /// monospace-ish estimate from character count and text height. Anchor convention matches
    /// <c>LayoutRenderer.DrawLabelText</c>'s historical default: (X,Y) is the baseline-left origin and
    /// the box extends right and up before rotation.</summary>
    private const double LabelApproxCharWidthRatio = 0.62;

    private static Bbox LabelHitBbox(LabelShape label)
    {
        if (string.IsNullOrEmpty(label.Text))
            return new Bbox(label.X, label.Y, label.X, label.Y);

        // ── ONE measurement, so what you can click and what lights up cannot disagree ────────────
        //
        // Owner report, 2026-08-25: "on one version of the code the hitbox did not match the highlight
        // select box." They were two independently-derived regions — the highlight from real Skia glyph
        // metrics, this from the character-count estimate below — and they could only ever agree by
        // coincidence: 'W' and 'i' are the same width to an estimate that counts characters. Widening a
        // label's anchor (HAlign/VAlign) and its angle past the cardinals made the gap structural rather
        // than merely approximate, since only the renderer's version knew about either.
        //
        // The estimate stays as the fallback for the one case that has no glyphs to measure, and
        // SkiaFonts already degrades to the platform typeface rather than throwing when there is no
        // Avalonia host, so this is safe off the UI thread and in a headless test alike.
        // Ports are NOT measured here: a port's pick region is its MARK (see PortPickBbox), which the
        // caller resolves with the conductor lookup, and the no-conductor fallback below is a square on
        // the anchor deliberately sized from the text rather than fitted to it. Two owner reports have
        // already tuned that square; nothing here changes it.
        if (label.IsPort) return AnchorSquare(label);

        if (Renderers.LayoutRenderer.MeasureLabelWorldBbox(label) is { IsEmpty: false } measured)
            return measured;

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
        // port gets a square centred on the anchor at the LARGER of the two reaches.
        //
        // ── HALF THE LARGER REACH, NOT THE WHOLE OF IT ──────────────────────────────────────
        //
        // Owner report, 2026-08-25: "the hitbox for the ports now seems too big — I am always
        // selecting ports almost everywhere I click in the layout." This read `half = Max(w, h)`,
        // and w/h are FULL extents: an ordinary label of the same text occupies w by h, so making
        // the region symmetric had also DOUBLED it in each direction — four times the area, for a
        // change that was only ever meant to centre it. On the reporter's file (three
        // two-character labels at a 1.016 mm height) that is a 2.52 mm square per port on a
        // 3.5 x 2.2 mm structure, so the three of them covered nearly the whole drawing and a click
        // anywhere landed on a port. Ports also carry a ZERO-AREA bbox, which HitStack's
        // smaller-area-wins rule ranks ahead of any real geometry on the same layer, so the metal
        // underneath was unreachable rather than merely second.
        //
        // Halving makes the square circumscribe the glyph instead of using the glyph as its radius,
        // which is what "symmetric about the anchor" should have meant all along. The click
        // tolerance is added on top by the caller (see HitTestShape's Label case), so a port stays
        // grabbable from just outside its own text — which is the part of the 2026-08-09 report that
        // was really about reach, and it is unchanged.
        // The fallback path's own rotation. It used to be a four-entry table, and an owner report had
        // already caught R90/R270's X range being backwards in it; rotating all four corners reproduces
        // every entry of the corrected table exactly and answers a non-cardinal angle too. The anchor
        // convention here is the historical baseline-left one — HAlign/VAlign are honoured only by the
        // measured path above, which is the path that runs whenever there are glyphs to measure.
        double rad = label.RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var box = Bbox.Empty;
        foreach (var (lx, ly) in new (double X, double Y)[] { (0, 0), (w, 0), (w, h), (0, h) })
        {
            long px = label.X + (long)Math.Round(lx * cos - ly * sin);
            long py = label.Y + (long)Math.Round(lx * sin + ly * cos);
            box = box.Union(new Bbox(px, py, px, py));
        }
        return box;
    }

    /// <summary>
    /// <b>A port's pick region — and what the selection highlight draws.</b> The MARK the port
    /// actually draws, plus a little padding.
    ///
    /// <para>Owner report, 2026-08-25: "the hitbox of the port does not match with the select
    /// highlight rendering", then "make the hitbox/highlight the anchor arrow area + padding". They
    /// were two independently-derived regions and they disagreed almost completely — measured on a
    /// two-character port at a 1.016 mm height, the highlight ran x 63,500..1,217,414 (the tight
    /// GLYPH box, up and right of the anchor) while the pick region was the square
    /// -629,920..+629,920. They shared one corner.</para>
    ///
    /// <para><b>WHERE it sits follows the KIND, because the marks do</b> — see
    /// <see cref="LayoutPortDirection.MarkerBbox"/>. A gap or internal port draws at the anchor; an
    /// EDGE port draws its bar and arrow at the conductor END, so its box goes there, which can be
    /// some distance from the label. That is the deliberate consequence of picking the mark rather
    /// than the text: you grab a port by its arrow.</para>
    ///
    /// <para><b>Falls back to a square about the anchor</b> when the conductor cannot be resolved —
    /// a port on bare dielectric, or a caller with no technology. There is no marker to measure
    /// there (the renderer draws none either), and a port that could not be picked at all would be a
    /// port that could not be moved back onto the metal.</para>
    /// </summary>
    internal static Bbox PortPickBbox(LabelShape label, LayoutPortDirection.PortHint? hint,
                                      bool atAnchor = false)
    {
        long padding = Math.Max(1, label.Height / 4);

        if (hint is not { } h) return AnchorSquare(label);

        var bb = LayoutPortDirection.MarkerBbox(label, h, atAnchor, padding);
        return bb.IsEmpty ? AnchorSquare(label) : bb;
    }

    /// <summary>The no-conductor fallback: a small square on the anchor, sized from the label's own
    /// text so it scales with what is drawn there.</summary>
    private static Bbox AnchorSquare(LabelShape label)
    {
        if (string.IsNullOrEmpty(label.Text))
            return new Bbox(label.X, label.Y, label.X, label.Y);

        long w = Math.Max(1, (long)Math.Round(label.Text.Length * label.Height * LabelApproxCharWidthRatio));
        long h = Math.Max(1, label.Height);
        long half = Math.Max(1, Math.Max(w, h) / 2);
        return new Bbox(label.X - half, label.Y - half, label.X + half, label.Y + half);
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
