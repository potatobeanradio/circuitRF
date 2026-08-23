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
    private readonly List<IntrinsicSnapFeature> _features;
    private readonly Dictionary<(long Kx, long Ky), List<int>> _grid = new();
    private readonly long _cellSize;

    private LayoutSnapFeatureIndex(List<IntrinsicSnapFeature> features)
    {
        _features = features;
        _cellSize = ComputeCellSize(features);
        for (int i = 0; i < features.Count; i++)
            BucketOf(features[i].X, features[i].Y).Add(i);
    }

    private (long, long) KeyOf(long x, long y) => (FloorDiv(x, _cellSize), FloorDiv(y, _cellSize));

    private List<int> BucketOf(long x, long y)
    {
        var key = KeyOf(x, y);
        if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = [];
        return list;
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor((double)a / b);

    private static long ComputeCellSize(List<IntrinsicSnapFeature> features)
    {
        if (features.Count == 0) return 1;
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (var f in features)
        {
            if (f.X < minX) minX = f.X; if (f.X > maxX) maxX = f.X;
            if (f.Y < minY) minY = f.Y; if (f.Y > maxY) maxY = f.Y;
        }
        long span = Math.Max(maxX - minX, maxY - minY);
        // ~64 buckets across the cell's own extent — bounded below so a degenerate (single-point)
        // cell never produces a zero bucket size.
        return Math.Max(1, span / 64);
    }

    /// <summary>Every feature within <paramref name="tolDbu"/> of (x,y) — a conservative near-cursor
    /// set (grid buckets, not an exact circle test until the caller applies its own tolerance), so
    /// this scales with what's near the cursor, never with how many features the whole cell holds.</summary>
    /// <param name="accept">Applied BEFORE a feature is admitted, so <paramref name="cap"/> can never
    /// discard something a later filter would have kept. Null accepts everything.</param>
    /// <param name="cap">At most this many features come back, the best by priority
    /// <see cref="SnapFeatureKind"/> then distance — the same order the query above this one returns
    /// in, applied here so a dense cell never MATERIALISES the rest. 0 means unbounded.
    ///
    /// <para>Unbounded is not a sane default for a caller that draws a marker: snap tolerance is a
    /// fixed SCREEN distance in world units, so over a six-figure via field a cursor at full extent has
    /// tens of thousands of features inside it — one list, grown and copied a dozen times, on every
    /// pointer move, for a caller that reads the first entry.</para></param>
    public IReadOnlyList<IntrinsicSnapFeature> QueryNear(long x, long y, long tolDbu, ref SnapQueryCounters counters,
                                                        int cap = 0, Func<IntrinsicSnapFeature, bool>? accept = null)
    {
        if (_features.Count == 0) return [];

        long r = Math.Max(1, tolDbu);
        var (kx0, ky0) = KeyOf(x - r, y - r);
        var (kx1, ky1) = KeyOf(x + r, y + r);

        long spanX = kx1 - kx0 + 1, spanY = ky1 - ky0 + 1;

        // THE BUCKET SWEEP MUST BE BOUNDED BY THE FEATURE COUNT, NOT BY THE QUERY RADIUS. This was a
        // hard hang, and the shape of it is worth keeping: the bucket size is derived from the cell's
        // OWN extent (ComputeCellSize, span/64), so a cell whose features are all at ONE POINT gets
        // the floor of 1 DBU per bucket. A single placed via is exactly that — its only intrinsic
        // feature is its centre. The snap tolerance meanwhile is a few SCREEN pixels converted at the
        // current zoom, so zoomed out on a board it is hundreds of thousands of DBU. The two together
        // ask this loop for ~10^12 iterations over a dictionary holding one entry: 100% CPU, no
        // progress, and only on a layout holding a single point-like shape — which is why it read as
        // intermittent (any other shape gives a non-degenerate span, and the zoom decides the radius).
        //
        // The grid is an optimisation, and it stops being one the moment the query rect covers more
        // buckets than the cell has features. Scanning the features directly is then both correct and
        // strictly cheaper, and is bounded by something that cannot blow up.
        long budget = _features.Count;
        bool sweepIsWorthIt = spanX <= budget && spanY <= budget && spanX * spanY <= budget;

        List<IntrinsicSnapFeature>? result = null;
        int examined = 0;

        long probed = 0;

        bool saturated = false;
        var worstKind = SnapFeatureKind.Pin;
        double worstDistSq = 0;

        if (sweepIsWorthIt)
        {
            for (long kx = kx0; kx <= kx1; kx++)
            for (long ky = ky0; ky <= ky1; ky++)
            {
                probed++;
                if (!_grid.TryGetValue((kx, ky), out var bucket)) continue;
                foreach (var idx in bucket) Consider(idx);
            }
        }
        else
        {
            for (int i = 0; i < _features.Count; i++) Consider(i);
        }

        counters.FeaturesExamined += examined;
        counters.BucketsProbed += probed;
        if (result is null) return [];
        if (cap > 0)
        {
            SortKept();
            if (result.Count > cap) result.RemoveRange(cap, result.Count - cap);
        }
        return result;

        void Consider(int idx)
        {
            examined++;
            var f = _features[idx];
            double dx = f.X - x, dy = f.Y - y;
            double distSq = dx * dx + dy * dy;
            if (distSq > (double)r * r) return;
            if (accept is not null && !accept(f)) return;

            if (cap > 0)
            {
                // Rejected on arrival once the kept set is full and this is worse than its tail —
                // nothing past the cap can reach the answer, so it is never stored.
                if (saturated)
                {
                    int k = f.Kind.CompareTo(worstKind);
                    if (k > 0 || (k == 0 && distSq >= worstDistSq)) return;
                }
                (result ??= []).Add(f);
                // Trimming at twice the cap amortises the sort over the entries it admits.
                if (result.Count >= cap * 2) Trim();
                return;
            }
            (result ??= []).Add(f);
        }

        void Trim()
        {
            SortKept();
            result!.RemoveRange(cap, result.Count - cap);
            var worst = result[^1];
            worstKind = worst.Kind;
            double wdx = worst.X - x, wdy = worst.Y - y;
            worstDistSq = wdx * wdx + wdy * wdy;
            saturated = true;
        }

        void SortKept()
        {
            long qx = x, qy = y;
            result!.Sort((a, b) =>
            {
                int k = a.Kind.CompareTo(b.Kind);
                if (k != 0) return k;
                double ax = a.X - qx, ay = a.Y - qy, bx = b.X - qx, by = b.Y - qy;
                return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
            });
        }
    }

    // ── Build + cache ──────────────────────────────────────────────────────────

    private static readonly ConditionalWeakTable<LayoutView, LayoutSnapFeatureIndex> Cache = new();

    /// <summary>Evicts <paramref name="view"/>'s cached index. Mirrors
    /// <c>LayoutRenderer.InvalidateCompiledGeometry</c>'s exact contract — safe to call on a view that
    /// was never indexed (no-op).</summary>
    public static void Invalidate(LayoutView view) => Cache.Remove(view);

    public static LayoutSnapFeatureIndex Get(LayoutView view, Technology? tech)
    {
        if (Cache.TryGetValue(view, out var cached)) return cached;
        var built = Build(view, tech);
        Cache.AddOrUpdate(view, built);
        return built;
    }

    private static LayoutSnapFeatureIndex Build(LayoutView view, Technology? tech)
    {
        var features = new List<IntrinsicSnapFeature>();
        for (int i = 0; i < view.Shapes.Count; i++)
            AddShapeFeatures(features, view.Shapes[i], i);

        // The cell's OWN pins first — the same order the pin overlay uses, and for the same reason:
        // an IMPORTED cell has no generator to re-invoke, so gating this on PCellOrigin made its pins
        // unsnappable. That is a worse gap than the overlay's was: a pin the user can SEE but cannot
        // snap to is half a connection, which reads as the snap being broken rather than the pin
        // being absent.
        foreach (var pin in CellPins.Resolve(view, tech))
            features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Pin, pin.X, pin.Y, pin.Layer, -1));

        return new LayoutSnapFeatureIndex(features);
    }

    private static void AddShapeFeatures(List<IntrinsicSnapFeature> features, LayoutShape shape, int ownerIndex)
    {
        switch (shape)
        {
            case RectShape r:
                AddBoxFeatures(features, r.Layer, r.X1, r.Y1, r.X2, r.Y2, ownerIndex);
                break;
            case RoundedRectShape rr:
                AddBoxFeatures(features, rr.Layer, rr.X1, rr.Y1, rr.X2, rr.Y2, ownerIndex);
                break;
            case CircleShape c:
                // A circle has no vertex/edge list — only its centre is an intrinsic feature; nearest-
                // point-on-edge (computed live, see LayoutSnapQuery) covers its boundary.
                features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Centroid, c.Cx, c.Cy, c.Layer, ownerIndex));
                break;
            case PolygonShape p:
                AddRingFeatures(features, p.Layer, p.Xy, null, closed: true, ownerIndex);
                if (p.Holes is { } holes)
                    foreach (var h in holes) AddRingFeatures(features, p.Layer, h, null, closed: true, ownerIndex);
                AddBboxCentroid(features, p.Layer, p.Xy, ownerIndex);
                break;
            case CurveShape curve:
                AddRingFeatures(features, curve.Layer, curve.Xy, curve.Edges, closed: true, ownerIndex);
                if (curve.Holes is { } choles)
                    foreach (var h in choles) AddRingFeatures(features, curve.Layer, h, null, closed: true, ownerIndex);
                AddBboxCentroid(features, curve.Layer, curve.Xy, ownerIndex);
                break;
            case PathShape path:
                AddRingFeatures(features, path.Layer, path.Xy, path.Edges, closed: false, ownerIndex);
                break;
            case ViaShape via:
                // A via has no corners: X/Y is its CENTRE, so it is a Centroid and draws the circle
                // glyph. It was declared CornerEndpoint, which drew the square — "the rendered glyph
                // is a square, which should mean corner" (owner report). The kind is not decoration:
                // R-snp-5's priority order is what decides which feature wins when several are within
                // tolerance, and a via's centre is exactly as intentional as a rect's centre, not as
                // intentional as a drawn vertex.
                features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Centroid, via.X, via.Y, via.Layer, ownerIndex));
                break;
            // LabelShape, BitmapShape: not real geometry — no snap features, mirrors LayoutHandles.Build's
            // own "not geometry-reshape targets" exclusion for the same two kinds.
        }
    }

    private static void AddBoxFeatures(List<IntrinsicSnapFeature> features, LayerKey layer, long x1, long y1, long x2, long y2, int ownerIndex)
    {
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.CornerEndpoint, x1, y1, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.CornerEndpoint, x2, y1, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.CornerEndpoint, x2, y2, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.CornerEndpoint, x1, y2, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Midpoint, (x1 + x2) / 2, y1, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Midpoint, x2, (y1 + y2) / 2, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Midpoint, (x1 + x2) / 2, y2, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Midpoint, x1, (y1 + y2) / 2, layer, ownerIndex));
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Centroid, (x1 + x2) / 2, (y1 + y2) / 2, layer, ownerIndex));
    }

    private static void AddRingFeatures(List<IntrinsicSnapFeature> features, LayerKey layer, long[] xy, List<LayoutEdge>? edges, bool closed, int ownerIndex)
    {
        int n = xy.Length / 2;
        if (n == 0) return;
        for (int i = 0; i < n; i++)
            features.Add(new IntrinsicSnapFeature(SnapFeatureKind.CornerEndpoint, xy[2 * i], xy[2 * i + 1], layer, ownerIndex));

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
            features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Midpoint, mid.Mx, mid.My, layer, ownerIndex));
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
    private static void AddBboxCentroid(List<IntrinsicSnapFeature> features, LayerKey layer, long[] xy, int ownerIndex)
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
        features.Add(new IntrinsicSnapFeature(SnapFeatureKind.Centroid, (minX + maxX) / 2, (minY + maxY) / 2, layer, ownerIndex));
    }
}
