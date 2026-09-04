// Geometry snap — intrinsic, per-cell, cell-local feature index (docs/sonnet-briefs/
// brief-snap-distance-and-geometry-snap.md §2.1/R-snp-12). Framework-free — no Skia/Avalonia.
// Corners/endpoints, midpoints, centroids, and PCell pins are properties of ONE shape/cell, so they
// are cached per-cell in CELL-LOCAL coordinates; intersections are relational (a property of a PAIR,
// possibly spanning cells/instances/layers) and are deliberately NEVER indexed here — see
// LayoutSnapQuery, which computes them live over a bounded near-cursor candidate set instead.

using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Layout;

/// <summary>Priority order, highest first (R-snp-5): the more INTENTIONAL a feature is, the higher
/// it ranks. Declared in exactly this order so a plain enum-value comparison sorts candidates
/// correctly — do not reorder without also checking <see cref="LayoutSnapQuery"/>'s sort.</summary>
public enum SnapFeatureKind
{
    Pin,
    CornerEndpoint,
    Intersection,
    Midpoint,
    Centroid,
    Nearest,
}

/// <summary>One snap candidate, already in WORLD-space coordinates (after any instance transform has
/// been applied — R-snp-13 transforms the query, not the geometry, but the RESULT handed back to the
/// caller is always in world space). <see cref="Layer"/> drives the marker's colour (R-snp-4);
/// <see cref="OwnerIsInstance"/>/<see cref="OwnerIndex"/> identify which TOP-LEVEL shape or instance a
/// click-through grab (R-snp-8) selects and drags — always a top-level entry, even when the feature
/// itself was found several levels down a resolved hierarchy (dragging reaches only as deep as the
/// top-level placement; pushing in is how a nested shape is edited directly).</summary>
public readonly record struct SnapCandidate(
    SnapFeatureKind Kind, long X, long Y, LayerKey Layer,
    bool OwnerIsInstance, int OwnerIndex);

/// <summary>One shape's (or PCell's) intrinsic feature — corner/endpoint, edge midpoint, or centroid,
/// plus (once, per resolved PCell) its pins. Coordinates are in the CELL's OWN local DBU frame, never
/// transformed — R-snp-13's whole point is that the QUERY (the cursor) is transformed into this frame
/// instead, so this index never needs to know about any instance placement that might reference it.
/// <see cref="OwnerShapeIndex"/> is the feature's index into the owning <see cref="LayoutView.Shapes"/>
/// list, or -1 for a PCell pin (owned by the cell as a whole, not one shape).</summary>
public readonly record struct IntrinsicSnapFeature(SnapFeatureKind Kind, long X, long Y, LayerKey Layer, int OwnerShapeIndex);

/// <summary>
/// The per-cell intrinsic feature index (R-snp-12): built once per <see cref="LayoutView"/>, cached
/// by REFERENCE (mirrors <c>LayoutRenderer.Instances.cs</c>'s own <c>_cellCompileCache</c> exactly —
/// same invalidation contract, see <see cref="Invalidate"/>), and queried via a small uniform grid so
/// a near-cursor lookup examines only nearby buckets rather than every feature in the cell — the
/// design doc's own §5.2 text explicitly allows "an R-tree (or per-layer uniform grid)"; a grid is the
/// right-sized tool for point features.
/// </summary>
public sealed class LayoutSnapFeatureIndex
{
    /// <summary>
    /// One feature KIND's own features and its own uniform grid. Split per kind because the query's
    /// order is priority-then-distance: a <see cref="SnapFeatureKind.Pin"/> anywhere inside the
    /// tolerance outranks a <see cref="SnapFeatureKind.Centroid"/> one DBU from the cursor, so a
    /// single mixed grid can never stop searching outward — a nearer feature of a better kind might
    /// still be out there. Per kind, the order collapses to distance ALONE, and "the nearest `cap` of
    /// this kind" is settled the moment the search ring passes the worst one kept. Merging the
    /// per-kind answers back together reproduces the mixed order exactly, because the final answer
    /// can hold at most <c>cap</c> of any one kind and within a kind those are its nearest.
    ///
    /// <para><b>The grid is a dense CSR table, not a dictionary of lists.</b> Bucket size is the
    /// kind's own extent over <see cref="GridSideCells"/>, so the populated range can never exceed
    /// <c>GridSideCells + 1</c> buckets per side — a few thousand cells, small enough to address
    /// directly. That turns a probe into two array reads instead of a hash lookup, and it removes the
    /// per-bucket <c>List&lt;int&gt;</c> whose doubling was most of this type's allocation: building
    /// the index for one dense generated cell allocated 235 MB, against 45 MB of features actually
    /// stored.</para>
    /// </summary>
    private sealed class KindIndex
    {
        public required IntrinsicSnapFeature[] Features;
        public long CellSize;
        /// <summary>The POPULATED bucket range. Every sweep is clamped to it, which is what makes the
        /// bucket count a property of the cell rather than of the query radius — see
        /// <see cref="QueryNear"/>.</summary>
        public long Kx0, Ky0, Kx1, Ky1;
        public int NxCells;
        /// <summary>Bucket <c>c</c> owns <c>Entries[BucketStart[c] .. BucketStart[c + 1])</c>, each an
        /// index into <see cref="Features"/>. Null below <see cref="GridMinFeatures"/> — indirection
        /// costs more than the distance test it saves when there is barely anything to skip.</summary>
        public int[]? BucketStart;
        public int[]? Entries;
    }

    /// <summary>Below this a kind is scanned linearly and gets no grid at all.</summary>
    private const int GridMinFeatures = 64;

    /// <summary>Buckets across a kind's own extent, per side. Also the bound that lets the grid be a
    /// dense array — see <see cref="KindIndex"/>.</summary>
    private const int GridSideCells = 64;

    private readonly KindIndex?[] _byKind;
    private readonly int _featureCount;

    private LayoutSnapFeatureIndex(IntrinsicSnapFeature[]?[] byKind)
    {
        _byKind = new KindIndex?[byKind.Length];
        for (int k = 0; k < byKind.Length; k++)
        {
            if (byKind[k] is not { Length: > 0 } arr) continue;
            _featureCount += arr.Length;
            _byKind[k] = BuildKind(arr);
        }
    }

    private static KindIndex BuildKind(IntrinsicSnapFeature[] arr)
    {
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (var f in arr)
        {
            if (f.X < minX) minX = f.X; if (f.X > maxX) maxX = f.X;
            if (f.Y < minY) minY = f.Y; if (f.Y > maxY) maxY = f.Y;
        }

        // Bounded below so a degenerate (single-point) kind never produces a zero bucket size.
        long span = Math.Max(maxX - minX, maxY - minY);
        long cellSize = Math.Max(1, span / GridSideCells);

        var ki = new KindIndex { Features = arr, CellSize = cellSize };
        if (arr.Length < GridMinFeatures) return ki;

        ki.Kx0 = FloorDiv(minX, cellSize); ki.Kx1 = FloorDiv(maxX, cellSize);
        ki.Ky0 = FloorDiv(minY, cellSize); ki.Ky1 = FloorDiv(maxY, cellSize);

        long nx = ki.Kx1 - ki.Kx0 + 1, ny = ki.Ky1 - ki.Ky0 + 1;
        // Cannot trip on the sizing above — but the dense table is only affordable BECAUSE of it, so
        // the fallback is spelled out rather than assumed away: a future change to how cellSize is
        // chosen degrades this to a linear scan instead of allocating a table off the end of memory.
        if (nx * ny > 1 << 20) return ki;

        int cells = (int)(nx * ny);
        ki.NxCells = (int)nx;

        // Counting sort into CSR: tally per bucket, prefix-sum into offsets, then place. No per-bucket
        // list, no growth, and the entries of one bucket end up contiguous.
        var start = new int[cells + 1];
        foreach (var f in arr) start[CellOf(ki, f) + 1]++;
        for (int c = 0; c < cells; c++) start[c + 1] += start[c];

        var cursor = new int[cells];
        Array.Copy(start, cursor, cells);
        var entries = new int[arr.Length];
        for (int i = 0; i < arr.Length; i++) entries[cursor[CellOf(ki, arr[i])]++] = i;

        ki.BucketStart = start;
        ki.Entries = entries;
        return ki;
    }

    private static int CellOf(KindIndex ki, in IntrinsicSnapFeature f) =>
        (int)(FloorDiv(f.Y, ki.CellSize) - ki.Ky0) * ki.NxCells + (int)(FloorDiv(f.X, ki.CellSize) - ki.Kx0);

    private static long FloorDiv(long a, long b) => (long)Math.Floor((double)a / b);

    /// <summary>How many features this index holds. Test instrumentation — the point of the bounded
    /// query below is that its cost is NOT this number.</summary>
    public int FeatureCount => _featureCount;

    /// <summary>Every feature within <paramref name="tolDbu"/> of (x,y), best-first when
    /// <paramref name="cap"/> bounds the answer.
    ///
    /// <para><b>The cost is set by what is NEAR THE CURSOR, never by the tolerance and never by how
    /// many features the cell holds.</b> That distinction is the whole point of this method and it is
    /// not a micro-optimisation: snap tolerance is a fixed SCREEN distance converted to world units,
    /// so it grows without bound as the user zooms out. Zoomed far enough out on a generated
    /// capacitor carrying a six-figure via field, the tolerance covers the entire cell — every one of
    /// its features is "within tolerance", and a search bounded by the tolerance degenerates into a
    /// scan of the whole cell on every pointer move. The features are all inside a few device pixels
    /// of each other at that zoom, so the scan is not buying accuracy either; the nearest handful is
    /// the whole answer.</para>
    ///
    /// <para>So the sweep walks the grid in RINGS outward from the cursor's own bucket and stops as
    /// soon as the next ring cannot beat what is already kept, and it is clamped to the POPULATED
    /// bucket range so a radius far larger than the cell probes the cell, not the radius. Both bounds
    /// matter: the clamp alone still visits every bucket the cell has, and the ring termination alone
    /// still walks empty space out to the radius. This also subsumes the degenerate case that was
    /// once a hard hang — a cell whose features sit at a single point buckets at 1 DBU, and a
    /// board-scale radius asked the old sweep for ~10^12 probes over a dictionary holding one
    /// entry.</para></summary>
    /// <param name="accept">Applied BEFORE a feature is admitted, so <paramref name="cap"/> can never
    /// discard something a later filter would have kept. Null accepts everything.</param>
    /// <param name="cap">At most this many features come back, the best by priority
    /// <see cref="SnapFeatureKind"/> then distance. 0 means unbounded — every feature inside the
    /// tolerance, in no particular order.</param>
    public IReadOnlyList<IntrinsicSnapFeature> QueryNear(long x, long y, long tolDbu, ref SnapQueryCounters counters,
                                                        int cap = 0, Func<IntrinsicSnapFeature, bool>? accept = null)
    {
        if (_featureCount == 0) return [];

        // Clamped so `x - r` / `x + r` cannot overflow for an absurd radius; a radius this size
        // already covers any real layout many times over.
        long r = Math.Clamp(tolDbu, 1, long.MaxValue / 4);

        List<IntrinsicSnapFeature>? result = null;
        int examined = 0;
        long probed = 0;

        foreach (var ki in _byKind)
        {
            if (ki is null) continue;
            var (kindExamined, kindProbed) = CollectFromKind(ki, x, y, r, cap, accept, ref result);
            examined += kindExamined;
            probed += kindProbed;
        }

        counters.FeaturesExamined += examined;
        counters.BucketsProbed += probed;

        if (result is null) return [];
        if (cap > 0)
        {
            SortByKindThenDistance(result, x, y);
            if (result.Count > cap) result.RemoveRange(cap, result.Count - cap);
        }
        return result;
    }

    /// <summary>Appends this kind's answer — its nearest <paramref name="cap"/> features inside the
    /// radius, or all of them when <paramref name="cap"/> is 0 — to <paramref name="result"/>.</summary>
    private static (int Examined, long Probed) CollectFromKind(
        KindIndex ki, long x, long y, long r, int cap,
        Func<IntrinsicSnapFeature, bool>? accept, ref List<IntrinsicSnapFeature>? result)
    {
        int examined = 0;
        long probed = 0;
        var arr = ki.Features;
        double rSq = (double)r * r;

        // This kind's own bounded best-set. One kind, so the order is distance alone — which is what
        // lets the ring loop below know when it is finished.
        List<IntrinsicSnapFeature>? kept = null;
        bool saturated = false;
        double worstDistSq = 0;

        void Consider(int idx)
        {
            examined++;
            var f = arr[idx];
            double dx = f.X - x, dy = f.Y - y;
            double distSq = dx * dx + dy * dy;
            if (distSq > rSq) return;
            if (saturated && distSq >= worstDistSq) return;
            if (accept is not null && !accept(f)) return;

            (kept ??= []).Add(f);
            // Trimming at twice the cap amortises the sort over the entries it admits.
            if (cap > 0 && kept.Count >= cap * 2)
            {
                SortByDistance(kept, x, y);
                kept.RemoveRange(cap, kept.Count - cap);
                var worst = kept[^1];
                double wdx = worst.X - x, wdy = worst.Y - y;
                worstDistSq = wdx * wdx + wdy * wdy;
                saturated = true;
            }
        }

        if (ki.BucketStart is null)
        {
            for (int i = 0; i < arr.Length; i++) Consider(i);
        }
        else
        {
            long cs = ki.CellSize;
            long kx0 = Math.Max(FloorDiv(x - r, cs), ki.Kx0), kx1 = Math.Min(FloorDiv(x + r, cs), ki.Kx1);
            long ky0 = Math.Max(FloorDiv(y - r, cs), ki.Ky0), ky1 = Math.Min(FloorDiv(y + r, cs), ki.Ky1);
            if (kx0 > kx1 || ky0 > ky1) goto done;

            // The ring is centred on the cursor's own bucket, clamped into the populated range so a
            // cursor outside the cell still sweeps from its nearest edge outward. Clamping only ever
            // makes the ring-distance bound below more conservative (the true cursor is further away
            // than the clamped centre), so it can delay termination but never cause an early one.
            long cx = Math.Clamp(FloorDiv(x, cs), kx0, kx1);
            long cy = Math.Clamp(FloorDiv(y, cs), ky0, ky1);
            long maxD = Math.Max(Math.Max(cx - kx0, kx1 - cx), Math.Max(cy - ky0, ky1 - cy));

            void Probe(long kx, long ky)
            {
                probed++;
                // The bucket's OWN nearest corner, against the same worst-kept bound the ring test
                // uses. A ring is a Chebyshev shell, which is a poor stand-in for distance once the
                // cursor is well outside the cell: first contact then happens at a large radius, and
                // that shell is hundreds of buckets long while only the few nearest its closest point
                // can contribute. This is the same test one level down, and it costs four
                // comparisons against a bucket's worth of distance arithmetic.
                if (saturated
                    && RectDistSq(x, y, (double)kx * cs, (double)ky * cs,
                                  (double)(kx + 1) * cs, (double)(ky + 1) * cs) >= worstDistSq)
                    return;
                int cell = (int)(ky - ki.Ky0) * ki.NxCells + (int)(kx - ki.Kx0);
                int end = ki.BucketStart![cell + 1];
                for (int t = ki.BucketStart[cell]; t < end; t++) Consider(ki.Entries![t]);
            }

            for (long d = 0; d <= maxD; d++)
            {
                // How close the cursor can possibly be to anything NOT YET SWEPT. Rings d' < d have
                // covered the column band [cx-d+1, cx+d-1] and the row band likewise (each clipped to
                // the populated range), so everything still unseen lies beyond one of those four
                // edges — and the nearest such edge is a hard floor on what a later ring can offer.
                //
                // Measured from the CURSOR, not from the ring's centre bucket, and that is the whole
                // subtlety: the centre is the cursor's bucket CLAMPED into the populated range, so
                // for a cursor outside the cell entirely (every placed instance the pointer is not
                // currently over) the two are different by an unbounded amount. Bounding by the
                // ring's own radius instead reads "0 cells away" for a cursor a millimetre off the
                // cell, never terminates, and sweeps the whole cell — which is exactly the cost this
                // method exists to avoid, on 23 of the 24 instances in a typical frame.
                if (saturated && d > 0)
                {
                    double floorSq = UnsweptDistanceSq(x, y, cx, cy, d - 1, kx0, kx1, ky0, ky1, cs);
                    if (floorSq > worstDistSq) break;
                }

                if (d == 0) { Probe(cx, cy); continue; }

                long left = cx - d, right = cx + d, top = cy - d, bottom = cy + d;
                for (long kx = Math.Max(left, kx0); kx <= Math.Min(right, kx1); kx++)
                {
                    if (top    >= ky0 && top    <= ky1) Probe(kx, top);
                    if (bottom >= ky0 && bottom <= ky1) Probe(kx, bottom);
                }
                for (long ky = Math.Max(top + 1, ky0); ky <= Math.Min(bottom - 1, ky1); ky++)
                {
                    if (left  >= kx0 && left  <= kx1) Probe(left, ky);
                    if (right >= kx0 && right <= kx1) Probe(right, ky);
                }
            }
        }

    done:
        if (kept is null) return (examined, probed);
        if (cap > 0 && kept.Count > cap)
        {
            SortByDistance(kept, x, y);
            kept.RemoveRange(cap, kept.Count - cap);
        }
        (result ??= []).AddRange(kept);
        return (examined, probed);
    }

    /// <summary>Squared distance from the cursor to the nearest point of the populated range that
    /// rings 0..<paramref name="d"/> around (<paramref name="cx"/>, <paramref name="cy"/>) have NOT
    /// covered, or <see cref="double.PositiveInfinity"/> once they cover all of it.
    ///
    /// <para><b>A true 2-D distance to what is left, not the smaller of two per-axis gaps.</b> Those
    /// are not the same quantity and the difference is the difference between terminating and not:
    /// for a cursor level with the cell but a long way off to one side, the rows just outside the
    /// swept band are one bucket away in Y and the whole standoff distance away in X. Projected onto
    /// Y alone that reads as one bucket — a floor so weak it never passes anything, so the sweep runs
    /// to the end of the range on every instance the cursor is not sitting inside.</para>
    ///
    /// <para>What remains is the populated box minus the swept box, which is up to four rectangular
    /// slabs; the answer is the nearest of them, each measured as an honest point-to-rectangle
    /// distance.</para></summary>
    private static double UnsweptDistanceSq(long qx, long qy, long cx, long cy, long d,
                                            long kx0, long kx1, long ky0, long ky1, long cs)
    {
        double rx0 = (double)kx0 * cs, rx1 = (double)(kx1 + 1) * cs;
        double ry0 = (double)ky0 * cs, ry1 = (double)(ky1 + 1) * cs;

        double bx0 = (double)Math.Max(cx - d, kx0) * cs, bx1 = (double)(Math.Min(cx + d, kx1) + 1) * cs;
        double by0 = (double)Math.Max(cy - d, ky0) * cs, by1 = (double)(Math.Min(cy + d, ky1) + 1) * cs;

        double best = double.PositiveInfinity;
        if (bx0 > rx0) best = Math.Min(best, RectDistSq(qx, qy, rx0, ry0, bx0, ry1));   // left slab
        if (bx1 < rx1) best = Math.Min(best, RectDistSq(qx, qy, bx1, ry0, rx1, ry1));   // right slab
        if (by0 > ry0) best = Math.Min(best, RectDistSq(qx, qy, bx0, ry0, bx1, by0));   // below
        if (by1 < ry1) best = Math.Min(best, RectDistSq(qx, qy, bx0, by1, bx1, ry1));   // above
        return best;
    }

    private static double RectDistSq(double px, double py, double x0, double y0, double x1, double y1)
    {
        double dx = px < x0 ? x0 - px : px > x1 ? px - x1 : 0;
        double dy = py < y0 ? y0 - py : py > y1 ? py - y1 : 0;
        return dx * dx + dy * dy;
    }

    private static void SortByDistance(List<IntrinsicSnapFeature> items, long qx, long qy) =>
        items.Sort((a, b) =>
        {
            double ax = a.X - qx, ay = a.Y - qy, bx = b.X - qx, by = b.Y - qy;
            return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
        });

    private static void SortByKindThenDistance(List<IntrinsicSnapFeature> items, long qx, long qy) =>
        items.Sort((a, b) =>
        {
            int k = a.Kind.CompareTo(b.Kind);
            if (k != 0) return k;
            double ax = a.X - qx, ay = a.Y - qy, bx = b.X - qx, by = b.Y - qy;
            return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
        });

    // ── Build + cache ──────────────────────────────────────────────────────────

    private static readonly ConditionalWeakTable<LayoutView, LayoutSnapFeatureIndex> Cache = new();

    /// <summary>Evicts <paramref name="view"/>'s cached index. Mirrors
    /// <c>LayoutRenderer.InvalidateCompiledGeometry</c>'s exact contract — safe to call on a view that
    /// was never indexed (no-op).</summary>
    public static void Invalidate(LayoutView view) => Cache.Remove(view);

    /// <summary>Whether <paramref name="view"/>'s index is already built. Test instrumentation for
    /// the open-time prewarm (<c>LayoutEditorViewModel.PrewarmPlacedCellSnapIndices</c>) — whose
    /// entire observable effect is that this reads true before any pointer move has happened.</summary>
    internal static bool IsCached(LayoutView view) => Cache.TryGetValue(view, out _);

    public static LayoutSnapFeatureIndex Get(LayoutView view, Technology? tech)
    {
        if (Cache.TryGetValue(view, out var cached)) return cached;
        var built = Build(view, tech);
        Cache.AddOrUpdate(view, built);
        return built;
    }

    /// <summary>Where every feature this index holds is declared. Runs TWICE per build — once
    /// counting, once writing — over the one traversal below, so the pass that sizes the arrays and
    /// the pass that fills them cannot disagree about what a shape contributes.
    ///
    /// <para>The alternative, a <c>List&lt;IntrinsicSnapFeature&gt;</c> grown to its final length and
    /// then split by kind, is what this replaces: on a dense generated cell it doubled its way to a
    /// six-figure length and was then copied out again, which was most of the build's 235 MB. Walking
    /// the shape list twice costs a few million comparisons and allocates nothing.</para></summary>
    private sealed class FeatureSink
    {
        private readonly int[] _counts = new int[Enum.GetValues<SnapFeatureKind>().Length];
        private int[]? _fill;

        /// <summary>Non-null once <see cref="Allocate"/> has run — which is also what switches
        /// <see cref="Add"/> from counting to writing.</summary>
        public IntrinsicSnapFeature[]?[]? Arrays { get; private set; }

        public void Add(SnapFeatureKind kind, long x, long y, LayerKey layer, int ownerIndex)
        {
            int k = (int)kind;
            if (Arrays is null) { _counts[k]++; return; }
            Arrays[k]![_fill![k]++] = new IntrinsicSnapFeature(kind, x, y, layer, ownerIndex);
        }

        public void Allocate()
        {
            var arrays = new IntrinsicSnapFeature[_counts.Length][];
            for (int k = 0; k < _counts.Length; k++)
                if (_counts[k] > 0) arrays[k] = new IntrinsicSnapFeature[_counts[k]];
            _fill = new int[_counts.Length];
            Arrays = arrays;
        }
    }

    private static LayoutSnapFeatureIndex Build(LayoutView view, Technology? tech)
    {
        // Resolved once and reused across both passes — it is a per-cell resolution, not a per-pass
        // one, and running it twice would be the one part of this that a second pass actually costs.
        var pins = CellPins.Resolve(view, tech);

        var sink = new FeatureSink();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < view.Shapes.Count; i++)
                AddShapeFeatures(sink, view.Shapes[i], i);

            // The cell's OWN pins — an IMPORTED cell has no generator to re-invoke, so gating this on
            // PCellOrigin made its pins unsnappable. That is a worse gap than the pin overlay's was: a
            // pin the user can SEE but cannot snap to is half a connection, which reads as the snap
            // being broken rather than the pin being absent.
            foreach (var pin in pins)
                sink.Add(SnapFeatureKind.Pin, pin.X, pin.Y, pin.Layer, -1);

            if (pass == 0) sink.Allocate();
        }

        return new LayoutSnapFeatureIndex(sink.Arrays!);
    }

    private static void AddShapeFeatures(FeatureSink sink, LayoutShape shape, int ownerIndex)
    {
        switch (shape)
        {
            case RectShape r:
                AddBoxFeatures(sink, r.Layer, r.X1, r.Y1, r.X2, r.Y2, ownerIndex);
                break;
            case RoundedRectShape rr:
                AddBoxFeatures(sink, rr.Layer, rr.X1, rr.Y1, rr.X2, rr.Y2, ownerIndex);
                break;
            case CircleShape c:
                // A circle has no vertex/edge list — only its centre is an intrinsic feature; nearest-
                // point-on-edge (computed live, see LayoutSnapQuery) covers its boundary.
                sink.Add(SnapFeatureKind.Centroid, c.Cx, c.Cy, c.Layer, ownerIndex);
                break;
            case PolygonShape p:
                AddRingFeatures(sink, p.Layer, p.Xy, null, closed: true, ownerIndex);
                if (p.Holes is { } holes)
                    foreach (var h in holes) AddHoleFeatures(sink, p.Layer, h, ownerIndex);
                AddBboxCentroid(sink, p.Layer, p.Xy, ownerIndex);
                break;
            case CurveShape curve:
                AddRingFeatures(sink, curve.Layer, curve.Xy, curve.Edges, closed: true, ownerIndex);
                if (curve.Holes is { } choles)
                    foreach (var h in choles) AddHoleFeatures(sink, curve.Layer, h, ownerIndex);
                AddBboxCentroid(sink, curve.Layer, curve.Xy, ownerIndex);
                break;
            case PathShape path:
                AddRingFeatures(sink, path.Layer, path.Xy, path.Edges, closed: false, ownerIndex);
                break;
            case ViaShape via:
                // A via has no corners: X/Y is its CENTRE, so it is a Centroid and draws the circle
                // glyph. It was declared CornerEndpoint, which drew the square — "the rendered glyph
                // is a square, which should mean corner" (owner report). The kind is not decoration:
                // R-snp-5's priority order is what decides which feature wins when several are within
                // tolerance, and a via's centre is exactly as intentional as a rect's centre, not as
                // intentional as a drawn vertex.
                sink.Add(SnapFeatureKind.Centroid, via.X, via.Y, via.Layer, ownerIndex);
                break;
            // LabelShape, BitmapShape: not real geometry — no snap features, mirrors LayoutHandles.Build's
            // own "not geometry-reshape targets" exclusion for the same two kinds.
        }
    }

    /// <summary>
    /// An inner ring's own features — its vertices/edge midpoints, as before, PLUS its CENTRE
    /// (owner request, 2026-09-04: "snap to the centre of a hole").
    ///
    /// <para>A drilled hole in a pour, a mounting hole, an annular ring's clearance — the thing worth
    /// aiming at is the axis, and until now only the flattened arc's vertices were offered, which is
    /// the one point on a round hole nobody wants. The outer ring already gets exactly this treatment
    /// (<see cref="AddBboxCentroid"/>), so a hole getting one is the rule applying evenly rather than
    /// a new kind of feature: same <see cref="SnapFeatureKind.Centroid"/> kind, same circle glyph,
    /// same rank against a corner or a pin.</para>
    ///
    /// <para><b>It costs one feature per RING, not per vertex</b> — a Gerber pour with 228 holes and
    /// ~21,772 hole vertices gains 228 entries, well under a percent of what its rings already
    /// contribute, and the index is built once per cell and cached by reference. The query itself is
    /// unaffected: centroids are their own kind with their own grid, so a hole centre is examined only
    /// when the cursor is near one.</para>
    ///
    /// <para>The BBOX centre, matching <see cref="AddBboxCentroid"/> — for a circular hole (every
    /// drilled one) that IS the axis, and for an irregular cutout it is the same answer the outer ring
    /// would give, so the two can never disagree about what "centre" means.</para>
    /// </summary>
    private static void AddHoleFeatures(FeatureSink sink, LayerKey layer, long[] ring, int ownerIndex)
    {
        AddRingFeatures(sink, layer, ring, null, closed: true, ownerIndex);
        AddBboxCentroid(sink, layer, ring, ownerIndex);
    }

    private static void AddBoxFeatures(FeatureSink sink, LayerKey layer, long x1, long y1, long x2, long y2, int ownerIndex)
    {
        sink.Add(SnapFeatureKind.CornerEndpoint, x1, y1, layer, ownerIndex);
        sink.Add(SnapFeatureKind.CornerEndpoint, x2, y1, layer, ownerIndex);
        sink.Add(SnapFeatureKind.CornerEndpoint, x2, y2, layer, ownerIndex);
        sink.Add(SnapFeatureKind.CornerEndpoint, x1, y2, layer, ownerIndex);
        sink.Add(SnapFeatureKind.Midpoint, (x1 + x2) / 2, y1, layer, ownerIndex);
        sink.Add(SnapFeatureKind.Midpoint, x2, (y1 + y2) / 2, layer, ownerIndex);
        sink.Add(SnapFeatureKind.Midpoint, (x1 + x2) / 2, y2, layer, ownerIndex);
        sink.Add(SnapFeatureKind.Midpoint, x1, (y1 + y2) / 2, layer, ownerIndex);
        sink.Add(SnapFeatureKind.Centroid, (x1 + x2) / 2, (y1 + y2) / 2, layer, ownerIndex);
    }

    private static void AddRingFeatures(FeatureSink sink, LayerKey layer, long[] xy, List<LayoutEdge>? edges, bool closed, int ownerIndex)
    {
        int n = xy.Length / 2;
        if (n == 0) return;
        for (int i = 0; i < n; i++)
            sink.Add(SnapFeatureKind.CornerEndpoint, xy[2 * i], xy[2 * i + 1], layer, ownerIndex);

        int edgeCount = closed ? n : n - 1;
        for (int i = 0; i < edgeCount; i++)
        {
            int j = closed ? (i + 1) % n : i + 1;
            long x0 = xy[2 * i], y0 = xy[2 * i + 1];
            long x1 = xy[2 * j], y1 = xy[2 * j + 1];
            var edge = edges is not null && i < edges.Count ? edges[i] : null;

            (long Mx, long My) mid = edge?.Kind switch
            {
                EdgeKind.Arc   => LayoutHandles.ArcHandlePosition(x0, y0, x1, y1, edge.Bulge),
                EdgeKind.Cubic => CubicMidpoint(x0, y0, edge.C1X, edge.C1Y, edge.C2X, edge.C2Y, x1, y1),
                _              => ((x0 + x1) / 2, (y0 + y1) / 2),
            };
            sink.Add(SnapFeatureKind.Midpoint, mid.Mx, mid.My, layer, ownerIndex);
        }
    }

    /// <summary>Cubic Bézier evaluated at t=0.5 — the standard control-polygon midpoint formula
    /// (B(0.5) = (P0 + 3C1 + 3C2 + P1) / 8).</summary>
    private static (long, long) CubicMidpoint(double x0, double y0, double c1x, double c1y, double c2x, double c2y, double x1, double y1)
    {
        double mx = (x0 + 3 * c1x + 3 * c2x + x1) / 8.0;
        double my = (y0 + 3 * c1y + 3 * c2y + y1) / 8.0;
        return ((long)Math.Round(mx), (long)Math.Round(my));
    }

    /// <summary>Bbox-centre "centroid" — deterministic and well-defined even for a self-intersecting or
    /// zero-area ring (unlike a signed-area polygon centroid), matching L1h's own Scale anchor choice
    /// for the identical reason.</summary>
    private static void AddBboxCentroid(FeatureSink sink, LayerKey layer, long[] xy, int ownerIndex)
    {
        int n = xy.Length / 2;
        if (n == 0) return;
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            long x = xy[2 * i], y = xy[2 * i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        sink.Add(SnapFeatureKind.Centroid, (minX + maxX) / 2, (minY + maxY) / 2, layer, ownerIndex);
    }
}
