// The single Clipper2 conversion point (docs/design/layout-view.md §6.1): booleans, offsets, DRC
// (L5b), the mesher (L6), and export (L4) all call ToClipperPaths/FromClipperTree, so the flattening
// tolerance is never chosen twice with two different answers. Built on LayoutFlattener — this file
// must NOT re-implement curve flattening.
//
// Our DBU integers go straight in: Clipper2's Path64/Point64 are long-based, exactly §1.1's storage
// type. No scaling to a working integer grid, no float conversion, no precision loss anywhere in this
// pipeline — the tempting "scale to a working integer grid" step other clipping libraries need is
// simply absent here. Coordinate magnitudes (<= ~10^9) sit far inside Clipper2's safe range.
//
// Fill rule: FillRule.NonZero everywhere, stated once here and never varied per call site — it is
// what makes self-intersection repair (LayoutBooleans.Repair) produce the outer region rather than a
// checkerboard.

using Clipper2Lib;

namespace CircuitRF.Design.Layout;

public static class LayoutClipper
{
    public const FillRule Rule = FillRule.NonZero;

    /// <summary>Converts one shape's geometry to Clipper2's integer path form, at
    /// <paramref name="tolDbu"/> flattening tolerance. <c>PathShape</c> gets its geometry OUTLINE
    /// here, via <c>InflatePaths</c> on the flattened centerline at <c>Width/2</c> with the join/cap
    /// matching its <c>End</c> style — this is NOT the display outline (R-L1e-1): the renderer's
    /// <c>LayoutRenderer.BuildPathOutline</c> keeps using the Skia stroker + Simplify so a curved
    /// trace still renders with adaptive, zoom-correct curves. Two outlines, two purposes; do not
    /// unify them.</summary>
    public static Paths64 ToClipperPaths(LayoutShape shape, long tolDbu)
    {
        if (shape is PathShape path)
            return PathOutlinePaths(path, tolDbu);

        var rings = LayoutFlattener.Flatten(shape, tolDbu);
        var paths = new Paths64(rings.Count);
        foreach (var ring in rings)
            paths.Add(RingToPath64(ring));
        return paths;
    }

    /// <summary>Wraps rings a caller already flattened itself (e.g. <c>LayoutTextFlatten</c>'s glyph
    /// contours, each flattened individually via <see cref="LayoutFlattener.Flatten"/> since they are
    /// not one shape's own rings) into Clipper2 <see cref="Paths64"/> — for callers that need the
    /// DBU-to-Clipper2 conversion without also re-flattening through <see cref="ToClipperPaths"/>'s
    /// single-shape path.</summary>
    public static Paths64 RingsToClipperPaths(IEnumerable<long[]> rings)
    {
        var paths = new Paths64();
        foreach (var ring in rings) paths.Add(RingToPath64(ring));
        return paths;
    }

    /// <summary>Rebuilds shapes from a Clipper2 boolean/offset result, preserving the hole structure
    /// (§3.1a) the tree already encodes: every non-hole node becomes one <see cref="PolygonShape"/>
    /// whose immediate hole children become its <c>Holes</c>; islands nested inside a hole recurse as
    /// further top-level shapes. Deterministic — a fixed walk order over Clipper2's own (deterministic)
    /// tree, no dictionaries, no parallelism (R-L1c-1's determinism discipline, extended here).</summary>
    public static IReadOnlyList<LayoutShape> FromClipperTree(PolyTree64 tree, LayerKey layer, string? net)
    {
        var results = new List<LayoutShape>();
        CollectSolids(tree, results, layer, net);
        return results;
    }

    private static void CollectSolids(PolyPath64 node, List<LayoutShape> results, LayerKey layer, string? net)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var solid = node[i];   // IsHole == false at this recursion level
            List<long[]>? holes = null;
            for (int j = 0; j < solid.Count; j++)
            {
                var hole = solid[j];   // IsHole == true
                (holes ??= []).Add(Path64ToRing(hole.Polygon));
                CollectSolids(hole, results, layer, net);   // islands nested inside this hole
            }
            results.Add(new PolygonShape { Layer = layer, Net = net, Xy = Path64ToRing(solid.Polygon), Holes = holes });
        }
    }

    // ── DBU <-> Clipper2 ─────────────────────────────────────────────────────

    private static Path64 RingToPath64(long[] ring)
    {
        int n = ring.Length / 2;
        var path = new Path64(n);
        for (int i = 0; i < n; i++)
            path.Add(new Point64(ring[2 * i], ring[2 * i + 1]));
        return path;
    }

    private static long[] Path64ToRing(Path64? path)
    {
        if (path is null) return [];
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++)
        {
            xy[2 * i] = path[i].X;
            xy[2 * i + 1] = path[i].Y;
        }
        return xy;
    }

    // ── PathShape -> geometry outline via InflatePaths ────────────────────────

    private static Paths64 PathOutlinePaths(PathShape path, long tolDbu)
    {
        var centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tolDbu);
        if (centerline.Length < 4) return [];   // fewer than 2 points — no outline to build

        var subject = new Paths64 { RingToPath64(centerline) };
        double delta = path.Width / 2.0;
        var endType = path.End switch
        {
            PathEndStyle.Round  => EndType.Round,
            PathEndStyle.Square => EndType.Square,
            PathEndStyle.Extended => EndType.Square,   // same offset amount as Square — see LayoutRenderer.ExtendedCenterline
            _                    => EndType.Butt,       // Flush
        };
        return Clipper.InflatePaths(subject, delta, JoinType.Round, endType);
    }

    // ── R-L1e-0 / §3.1a R10b: enforce hole validity on any non-Clipper2 construction path ─────────

    /// <summary>A hole must lie inside its outer ring and intersect neither that ring nor another
    /// hole. Clipper2's own <see cref="PolyTree64"/> output (every boolean/offset/repair in
    /// <c>LayoutBooleans</c>) already satisfies this by construction. This is the enforcement point
    /// for any OTHER way holes can enter the model — today that is a hand-edited <c>.clay</c> file
    /// (<c>LayoutPersistence</c> calls this for every loaded shape); a future paste (L1f) or import
    /// (L4) should call it too. Cheap no-op when the shape is already valid — only a genuinely invalid
    /// hole triggers the Clipper2 <c>Union</c> re-derivation, which may reorder vertices/holes (a
    /// normally-constructed shape never observes that, since it is already valid).</summary>
    public static IReadOnlyList<LayoutShape> EnsureValidHoles(LayoutShape shape)
    {
        var holes = shape switch
        {
            PolygonShape p => p.Holes,
            CurveShape c    => c.Holes,
            _               => null,
        };
        if (holes is not { Count: > 0 }) return [shape];

        long tol = LayoutFlattener.ResolveTolDbu(shape, null);
        var rings = LayoutFlattener.Flatten(shape, tol);
        if (HolesAreValid(rings)) return [shape];

        var paths = ToClipperPaths(shape, tol);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, Rule);
        return FromClipperTree(tree, shape.Layer, shape.Net);
    }

    /// <summary>
    /// The three conditions: every hole vertex lies inside-or-on the outer ring, no hole crosses that
    /// ring, and no two holes cross each other.
    ///
    /// <para><b>Bounding boxes are a PREFILTER, never a decision.</b> Every reject below is a case in
    /// which no segment pair can possibly meet, so this returns exactly what the unfiltered triple
    /// loop returns — <c>LayoutClipperHoleValidityTests</c> holds that against a brute-force copy of
    /// the original over a randomized corpus. What they buy, on the shape that motivated this (a
    /// Gerber-imported copper pour: 228 holes, 21,772 hole vertices, a 1,751-vertex outer ring):</para>
    /// <list type="bullet">
    /// <item>the hole-vs-hole pairs are ~26k ring-box tests instead of ~233M segment-pair tests,
    /// because the holes of one pour are disjoint by construction and essentially every pair dies on
    /// its box;</item>
    /// <item>the hole-vs-outer crossing test is ~1,751 segment-box tests plus a handful of full
    /// scans, instead of 38M segment-pair tests — see <see cref="RingsIntersect"/> for why the LONGER
    /// ring has to be the one on the outside of that loop.</item>
    /// </list>
    /// <para>The remaining term is the point-in-ring test, which no box helps: a ray cast has to see
    /// every segment the ray can cross, so cutting it needs an index over the outer ring rather than
    /// a rejection test.</para>
    /// </summary>
    internal static bool HolesAreValid(IReadOnlyList<long[]> rings)
    {
        var outer = rings[0];

        // One pass per ring, computed once. Every prefilter below reads these rather than re-deriving
        // them per pair, which is what makes the hole-vs-hole reject O(1) per pair.
        var info = new RingInfo[rings.Count];
        for (int i = 0; i < rings.Count; i++) info[i] = RingInfo.Of(rings[i]);

        for (int i = 1; i < rings.Count; i++)
        {
            var hole = rings[i];
            foreach (var v in EnumeratePoints(hole))
                if (!PointInOrOnRing(outer, v.X, v.Y)) return false;
            if (RingsIntersect(hole, outer, info[i], info[0])) return false;

            for (int j = i + 1; j < rings.Count; j++)
                if (RingsIntersect(hole, rings[j], info[i], info[j])) return false;
        }
        return true;
    }

    /// <summary>
    /// What one pass over a ring tells the prefilters: its axis-aligned extent, its segment count,
    /// and whether it carries a ZERO-LENGTH segment.
    ///
    /// <para><c>MaxX &lt; MinX</c> marks a ring with no vertices at all, which <see cref="Overlap"/>
    /// then reports as overlapping nothing — correct, since a ring with no vertices has no segments
    /// to intersect.</para>
    /// </summary>
    private readonly record struct RingInfo(
        long MinX, long MinY, long MaxX, long MaxY, int Segments, bool HasZeroLengthSegment)
    {
        public static RingInfo Of(long[] xy)
        {
            int n = xy.Length / 2;
            if (n == 0) return new RingInfo(0, 0, -1, -1, 0, false);

            long minX = xy[0], maxX = xy[0], minY = xy[1], maxY = xy[1];
            for (int i = 2; i < xy.Length; i += 2)
            {
                long x = xy[i], y = xy[i + 1];
                if (x < minX) minX = x; else if (x > maxX) maxX = x;
                if (y < minY) minY = y; else if (y > maxY) maxY = y;
            }

            // A one-vertex ring's single segment runs from the vertex to itself, so it counts too —
            // the (i + 1) % n wrap in RingSegment is what makes that so.
            bool zeroLength = n == 1;
            for (int i = 0; i < n && !zeroLength; i++)
            {
                int j = (i + 1) % n;
                zeroLength = xy[2 * i] == xy[2 * j] && xy[2 * i + 1] == xy[2 * j + 1];
            }

            return new RingInfo(minX, minY, maxX, maxY, n, zeroLength);
        }
    }

    /// <summary>Touching boxes count as overlapping — a rejection has to be certain, and two rings
    /// whose boxes share an edge can share a point.</summary>
    private static bool Overlap(in RingInfo a, in RingInfo b)
        => a.MinX <= b.MaxX && b.MinX <= a.MaxX && a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    private static IEnumerable<(long X, long Y)> EnumeratePoints(long[] xy)
    {
        for (int i = 0; i < xy.Length; i += 2)
            yield return (xy[i], xy[i + 1]);
    }

    /// <summary>
    /// Ray-cast containment. <b>The one term no box helps</b> — a ray has to see every segment it can
    /// cross, so there is nothing to reject — and after the two prefilters in
    /// <see cref="HolesAreValid"/> it is what is left: ~155 ms of the 0.30 s that reading a
    /// Gerber-imported board's 1,573 holed shapes now costs, nearly all of it on the one 228-hole
    /// pour.
    ///
    /// <para><b>Gating <see cref="OnSegment"/> behind the segment's own box was tried and MEASURED NO
    /// BETTER</b> — 0.34 s against a 0.30-0.34 s spread for this, i.e. inside the noise. The
    /// point-lies-on-this-segment test is three multiplies on values already in registers, so four
    /// integer compares and a branch per segment buy back about what they cost; the simpler code
    /// wins on a tie. Cutting this further needs an INDEX over the outer ring — segments bucketed by
    /// y, so a cast at height <c>py</c> visits one band instead of all N — which is a different piece
    /// of work with a build cost of its own, and is not done here.</para>
    /// </summary>
    private static bool PointInOrOnRing(long[] ring, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            if (OnSegment(px, py, xi, yi, xj, yj)) return true;
            bool crosses = (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        if (Math.Abs(cross) > 1e-6) return false;
        double dot = (px - ax) * (bx - ax) + (py - ay) * (by - ay);
        double lenSq = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        return dot >= 0 && dot <= lenSq;
    }

    /// <summary>
    /// Whether any segment of <paramref name="a"/> meets any segment of <paramref name="b"/>, with
    /// the callers' precomputed boxes used to skip pairs that provably cannot meet.
    ///
    /// <para><b>The LONGER ring goes on the outside of the loop, and that is the whole point.</b> The
    /// per-segment reject can only throw work away when it is tested against the OTHER ring's box, so
    /// the ring being rejected has to be the long one: a hole lies inside the outer ring's box, so
    /// rejecting the hole's few segments against the outer's box discards nothing, while rejecting
    /// the outer's thousands against the hole's small box discards nearly all of them. Both orders
    /// give the same answer — <see cref="SegmentsIntersect"/> is symmetric in its two segments — so
    /// this picks the one that is fast.</para>
    /// </summary>
    private static bool RingsIntersect(long[] a, long[] b, in RingInfo ia, in RingInfo ib)
    {
        // A ZERO-LENGTH segment reports as meeting ANYTHING, wherever the two rings are — so this
        // case has to be answered before the boxes get a say, and it is the one place a box reject
        // would otherwise change the result. OnSegment's window is `0 <= dot <= lenSq`, and a
        // segment from a point to itself has lenSq = 0 and dot = 0 for every point, so it passes for
        // all of them; SegmentsIntersect's collinear branch then returns true. That makes the
        // unfiltered algorithm call a ring with a repeated consecutive vertex invalid against
        // everything, and such a shape is re-derived through Clipper on load today. Preserved, not
        // corrected: whether that repair should happen is a question about R10b, not something a
        // performance edit gets to settle silently. Found by the differential gate, not by reading.
        if ((ia.HasZeroLengthSegment && ib.Segments > 0) ||
            (ib.HasZeroLengthSegment && ia.Segments > 0)) return true;

        if (!Overlap(ia, ib)) return false;
        return a.Length >= b.Length ? ScanAgainst(a, b, ib) : ScanAgainst(b, a, ia);
    }

    private static bool ScanAgainst(long[] scanned, long[] against, in RingInfo againstBox)
    {
        int na = scanned.Length / 2, nb = against.Length / 2;
        for (int i = 0; i < na; i++)
        {
            var (ax0, ay0, ax1, ay1) = RingSegment(scanned, i, na);

            // A segment whose own extent misses the other ring's box cannot meet any segment of it —
            // every one of them is inside that box. Kept inclusive (< / >, never <= / >=) so a
            // segment merely touching the box's edge still goes through the real test.
            if (Math.Max(ax0, ax1) < againstBox.MinX || Math.Min(ax0, ax1) > againstBox.MaxX ||
                Math.Max(ay0, ay1) < againstBox.MinY || Math.Min(ay0, ay1) > againstBox.MaxY)
                continue;

            for (int j = 0; j < nb; j++)
            {
                var (bx0, by0, bx1, by1) = RingSegment(against, j, nb);
                if (SegmentsIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1)) return true;
            }
        }
        return false;
    }

    private static (double, double, double, double) RingSegment(long[] xy, int i, int n)
    {
        int j = (i + 1) % n;
        return (xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
    }

    private static bool SegmentsIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        // Collinear/touching cases — conservative (treat touching as intersecting, per R10b).
        if (d1 == 0 && OnSegment(ax0, ay0, bx0, by0, bx1, by1)) return true;
        if (d2 == 0 && OnSegment(ax1, ay1, bx0, by0, bx1, by1)) return true;
        if (d3 == 0 && OnSegment(bx0, by0, ax0, ay0, ax1, ay1)) return true;
        if (d4 == 0 && OnSegment(bx1, by1, ax0, ay0, ax1, ay1)) return true;
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
}
