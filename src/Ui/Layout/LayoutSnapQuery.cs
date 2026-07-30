// Geometry snap — the query engine (docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.2).
// Framework-free. Bounds candidates via the L2b spatial index (R-snp-14) for instance discovery and
// for the near-shape set intersections/nearest-on-edge draw from; transforms the CURSOR into each
// nested instance's local frame rather than transforming geometry into world space (R-snp-13); and
// computes relational (intersection) candidates live over that bounded set — never indexed (R-snp-12).
//
// Scope note: intersection candidates are computed among TOP-LEVEL shapes only (never reaching inside
// a resolved instance) — a deliberate, stated simplification. The brief allows cross-instance
// projected intersections; restricting to the top level keeps the live pairwise test bounded and
// simple while still covering the common "snap a new trace to where two existing shapes cross" case.
// Nested-instance recursion has no cycle-visiting-set of its own (unlike CellHierarchy.ResolveForWalk)
// — CellHierarchy.MaxDepth alone bounds termination, which is sufficient here since a snap query never
// mutates anything and a pathological cycle merely wastes MaxDepth recursion levels once, not forever.

namespace CircuitRF.Ui.Layout;

/// <summary>Plain mutable counters — asserted directly in tests, no <c>Stopwatch</c> (R-L2a-3),
/// mirroring <c>LayoutFrameCounters</c>'s own shape.</summary>
public struct SnapQueryCounters
{
    public int FeaturesExamined;
    public int CandidatesReturned;
    public int IntersectionPairsTested;
}

public static class LayoutSnapQuery
{
    /// <summary>
    /// Finds every snap candidate within <paramref name="tolDbu"/> of (<paramref name="worldX"/>,
    /// <paramref name="worldY"/>), sorted by priority (R-snp-5: Pin &gt; CornerEndpoint &gt;
    /// Intersection &gt; Midpoint &gt; Centroid &gt; Nearest) then by distance to the cursor.
    /// <paramref name="excludeShapeIndices"/>/<paramref name="excludeInstanceIndices"/> (top-level only)
    /// let a live drag skip whatever geometry it is currently moving — brief-geometry-snap-followups.md
    /// R-snpf-4/5: a SET, not a single index, because a multi-shape (or instance) selection must
    /// exclude every dragged member, not just one. Never mutates anything — safe to call on every
    /// qualifying pointer move (the caller is responsible for R-snp-16's sub-pixel skip).
    /// </summary>
    public static IReadOnlyList<SnapCandidate> FindCandidates(
        LayoutView view, Technology? tech, string baseDir,
        long worldX, long worldY, long tolDbu, bool includeIntersections,
        IReadOnlySet<int>? excludeShapeIndices, IReadOnlySet<int>? excludeInstanceIndices, ref SnapQueryCounters counters)
    {
        var result = new List<SnapCandidate>();
        if (tolDbu <= 0) return result;

        // ── Top-level intrinsic features (corner/midpoint/centroid/pin) — R-snp-12 ────────────────
        var topIndex = LayoutSnapFeatureIndex.Get(view, tech);
        foreach (var f in topIndex.QueryNear(worldX, worldY, tolDbu, ref counters))
        {
            if (excludeShapeIndices is not null && excludeShapeIndices.Contains(f.OwnerShapeIndex)) continue;
            if (!ResolveLayer(tech, f.Layer).Visible) continue; // locked IS snappable — only Visible gates
            result.Add(new SnapCandidate(f.Kind, f.X, f.Y, f.Layer, false, f.OwnerShapeIndex));
        }

        // ── L2b spatial index — bounds instance discovery AND the near-shape set below (R-snp-14) ──
        var queryRect = new Bbox(worldX - tolDbu, worldY - tolDbu, worldX + tolDbu, worldY + tolDbu);
        Bbox InstanceBboxFor(LayoutInstance inst) => CellHierarchy.InstanceBbox(inst, baseDir);
        var entries = view.SpatialIndex.QueryIntersecting(
            view.Shapes, view.Instances, InstanceBboxFor, CellLayoutResolver.Generation, queryRect);

        var nearShapeIndices = new List<int>();
        foreach (var entry in entries)
        {
            if (entry.Kind == SpatialEntryKind.Shape)
            {
                if (excludeShapeIndices is not null && excludeShapeIndices.Contains(entry.Index)) continue;
                if (entry.Index < 0 || entry.Index >= view.Shapes.Count) continue;
                // §2.5: hidden layers contribute nothing — Nearest/Intersection candidates must obey
                // the same visibility gate the intrinsic-feature loop above already applies.
                if (!ResolveLayer(tech, view.Shapes[entry.Index].Layer).Visible) continue;
                nearShapeIndices.Add(entry.Index);
            }
            else
            {
                if (entry.Index < 0 || entry.Index >= view.Instances.Count) continue;
                if (excludeInstanceIndices is not null && excludeInstanceIndices.Contains(entry.Index)) continue;
                // R-snp-13: transform the cursor into the instance's local frame, never the geometry.
                RecurseInstance(view.Instances[entry.Index], baseDir, worldX, worldY, tolDbu, tech,
                    result, entry.Index, depth: 0, ref counters);
            }
        }

        // ── Intersections — relational, computed live over the bounded near-shape set, never indexed ──
        if (includeIntersections)
            AddIntersectionCandidates(view, tech, nearShapeIndices, worldX, worldY, tolDbu, result, ref counters);

        // ── Nearest point on edge — lowest priority, same bounded near-shape set ──────────────────
        AddNearestOnEdgeCandidates(view, tech, nearShapeIndices, worldX, worldY, tolDbu, result, ref counters);

        double DistSq(SnapCandidate c)
        {
            double dx = c.X - worldX, dy = c.Y - worldY;
            return dx * dx + dy * dy;
        }
        result.Sort((a, b) =>
        {
            int k = a.Kind.CompareTo(b.Kind);
            return k != 0 ? k : DistSq(a).CompareTo(DistSq(b));
        });

        counters.CandidatesReturned = result.Count;
        return result;
    }

    /// <summary>
    /// Recurses one instance placement (every array cell) — resolves its sub-cell, inverse-transforms
    /// the cursor (already expressed in THIS call's caller frame) into the sub-cell's own local frame,
    /// queries that sub-cell's own cached feature index, then forward-transforms any hits back into
    /// the caller's frame before adding them to <paramref name="result"/>. Recurses into the sub-cell's
    /// OWN nested instances the same way, one level at a time, so an arbitrarily (but depth-capped)
    /// deep hierarchy composes correctly without ever materializing a shape in world space.
    /// </summary>
    private static void RecurseInstance(
        LayoutInstance inst, string baseDir, double callerCursorX, double callerCursorY, long tolDbu,
        Technology? tech, List<SnapCandidate> result, int topLevelOwnerIndex, int depth, ref SnapQueryCounters counters)
    {
        if (depth >= CellHierarchy.MaxDepth) return;
        var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
        if (res.State != CellLayoutState.Resolved) return;

        string subBaseDir = CellHierarchy.LayoutBaseDirOf(res.ResolvedCellDir!);
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(callerCursorX, callerCursorY, inst, r, c);
            long localTol = (long)Math.Round(tolDbu / Math.Max(Math.Abs(inst.Mag), 1e-9));
            long lxi = (long)Math.Round(lx), lyi = (long)Math.Round(ly);

            var idx = LayoutSnapFeatureIndex.Get(res.View!, tech);
            foreach (var f in idx.QueryNear(lxi, lyi, localTol, ref counters))
            {
                if (!ResolveLayer(tech, f.Layer).Visible) continue;
                var (wx, wy) = LayoutInstanceTransform.TransformPoint(f.X, f.Y, inst, r, c);
                result.Add(new SnapCandidate(f.Kind, wx, wy, f.Layer, true, topLevelOwnerIndex));
            }

            foreach (var nested in res.View!.Instances)
            {
                var nestedLocal = new List<SnapCandidate>();
                RecurseInstance(nested, subBaseDir, lx, ly, localTol, tech, nestedLocal, topLevelOwnerIndex, depth + 1, ref counters);
                foreach (var cand in nestedLocal)
                {
                    var (wx, wy) = LayoutInstanceTransform.TransformPoint(cand.X, cand.Y, inst, r, c);
                    result.Add(cand with { X = wx, Y = wy });
                }
            }
        }
    }

    // ── Intersections (relational — live only, R-snp-12) ────────────────────────────────────────

    private static void AddIntersectionCandidates(
        LayoutView view, Technology? tech, List<int> nearShapeIndices,
        long worldX, long worldY, long tolDbu, List<SnapCandidate> result, ref SnapQueryCounters counters)
    {
        for (int a = 0; a < nearShapeIndices.Count; a++)
        for (int b = a + 1; b < nearShapeIndices.Count; b++)
        {
            int ia = nearShapeIndices[a], ib = nearShapeIndices[b];
            var shapeA = view.Shapes[ia];
            var shapeB = view.Shapes[ib];

            foreach (var segA in EdgeSegmentsOf(shapeA, tech))
            foreach (var segB in EdgeSegmentsOf(shapeB, tech))
            {
                counters.IntersectionPairsTested++;
                if (!TrySegmentIntersect(segA, segB, out long ix, out long iy)) continue;
                double dx = ix - worldX, dy = iy - worldY;
                if (dx * dx + dy * dy > (double)tolDbu * tolDbu) continue;
                result.Add(new SnapCandidate(SnapFeatureKind.Intersection, ix, iy, shapeA.Layer, false, ia));
            }
        }
    }

    // ── Nearest point on edge (lowest priority) ─────────────────────────────────────────────────

    private static void AddNearestOnEdgeCandidates(
        LayoutView view, Technology? tech, List<int> nearShapeIndices,
        long worldX, long worldY, long tolDbu, List<SnapCandidate> result, ref SnapQueryCounters counters)
    {
        foreach (var i in nearShapeIndices)
        {
            var shape = view.Shapes[i];
            foreach (var seg in EdgeSegmentsOf(shape, tech))
            {
                var (nx, ny, distSq) = NearestOnSegment(worldX, worldY, seg.X0, seg.Y0, seg.X1, seg.Y1);
                if (distSq > (double)tolDbu * tolDbu) continue;
                result.Add(new SnapCandidate(SnapFeatureKind.Nearest, nx, ny, shape.Layer, false, i));
            }
        }
    }

    // ── Shared geometry helpers ──────────────────────────────────────────────────────────────────

    private static IEnumerable<(long X0, long Y0, long X1, long Y1)> EdgeSegmentsOf(LayoutShape shape, Technology? tech)
    {
        switch (shape)
        {
            case PathShape path:
            {
                var ring = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, LayoutFlattener.ResolveTolDbu(shape, tech));
                int n = ring.Length / 2;
                for (int i = 0; i + 1 < n; i++)
                    yield return (ring[2 * i], ring[2 * i + 1], ring[2 * (i + 1)], ring[2 * (i + 1) + 1]);
                break;
            }
            case LabelShape:
            case BitmapShape:
            case ViaShape:
                break; // no flatten-able boundary
            default:
            {
                IReadOnlyList<long[]> rings;
                try { rings = LayoutFlattener.Flatten(shape, LayoutFlattener.ResolveTolDbu(shape, tech)); }
                catch (ArgumentOutOfRangeException) { break; }
                foreach (var ring in rings)
                {
                    int n = ring.Length / 2;
                    for (int i = 0; i < n; i++)
                    {
                        int j = (i + 1) % n;
                        yield return (ring[2 * i], ring[2 * i + 1], ring[2 * j], ring[2 * j + 1]);
                    }
                }
                break;
            }
        }
    }

    private static (long X, long Y, double DistSq) NearestOnSegment(long px, long py, long ax, long ay, long bx, long by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9)
        {
            double d0x = px - ax, d0y = py - ay;
            return (ax, ay, d0x * d0x + d0y * d0y);
        }
        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
        double nx = ax + t * dx, ny = ay + t * dy;
        double ddx = px - nx, ddy = py - ny;
        return ((long)Math.Round(nx), (long)Math.Round(ny), ddx * ddx + ddy * ddy);
    }

    private static bool TrySegmentIntersect(
        (long X0, long Y0, long X1, long Y1) s1, (long X0, long Y0, long X1, long Y1) s2, out long ix, out long iy)
    {
        double x1 = s1.X0, y1 = s1.Y0, x2 = s1.X1, y2 = s1.Y1;
        double x3 = s2.X0, y3 = s2.Y0, x4 = s2.X1, y4 = s2.Y1;
        double d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        ix = 0; iy = 0;
        if (Math.Abs(d) < 1e-9) return false; // parallel or degenerate

        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
        double u = ((x1 - x3) * (y1 - y2) - (y1 - y3) * (x1 - x2)) / d;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;

        ix = (long)Math.Round(x1 + t * (x2 - x1));
        iy = (long)Math.Round(y1 + t * (y2 - y1));
        return true;
    }

    private static LayerDef ResolveLayer(Technology? tech, LayerKey key)
    {
        if (tech is { } t)
            foreach (var l in t.Layers)
                if (l.Key == key) return l;
        return FallbackPalette.For(key);
    }
}
