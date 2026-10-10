// Contours — brief-img-1-raster-core.md R-im1-7.
//
// Marching squares on a coverage map at 0.5, sampled at pixel centres and padded with 0 beyond the picture, so every
// contour closes and every vertex is SUB-PIXEL (linearly interpolated between two centres). A saddle cell is decided by
// the mean of its four corners. Segments are oriented by one local rule — from the crossing where a clockwise walk
// round the cell leaves the inside to the one where it re-enters — so outer boundaries and holes come out with opposite
// windings without a second pass.
//
// Then each ring is simplified (Douglas–Peucker, default 0.35 px) and SNAPPED: an edge within the angle tolerance of
// 0°/90° (and 45° when asked) is rotated onto it about the centroid of the contour points it stands for, and its
// neighbours re-intersected; a short unsnapped edge left between two snapped ones is the rounding of an anti-aliased
// corner and is dropped; a run of unsnapped edges meeting within 2° of a right angle is squared exactly. A rectilinear
// trace comes back rectilinear rather than as a 0.4° parallelogram.
//
// Holes are nested by containment (each in the smallest outer boundary holding it) and every region is returned as
// Clipper2 paths — the library the layout booleans already use — outer boundary positive, holes negative.

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

public sealed record ContourOptions
{
    /// <summary>The coverage level traced.</summary>
    public float Level { get; init; } = 0.5f;

    /// <summary>Douglas–Peucker tolerance, in pixels.</summary>
    public double Simplify { get; init; } = 0.35;

    /// <summary>Whether edges are snapped at all.</summary>
    public bool Snap { get; init; } = true;

    /// <summary>An edge within this many degrees of 0° or 90° is snapped onto it.</summary>
    public double SnapAngleDeg { get; init; } = 3.0;

    /// <summary>Also snap onto 45° and 135°.</summary>
    public bool Snap45 { get; init; }

    /// <summary>A corner within this many degrees of 90° between unsnapped edges is made exact.</summary>
    public double RightAngleDeg { get; init; } = 2.0;

    /// <summary>An unsnapped edge shorter than this between two snapped, non-parallel edges is a rounded corner.</summary>
    public double ChamferMax { get; init; } = 2.0;

    /// <summary>A ring enclosing less than this many square pixels is dropped (speckle).</summary>
    public double MinArea { get; init; }
}

/// <summary>One traced region: an outer boundary and the holes in it, in pixel coordinates.</summary>
public sealed record ContourRegion(PathD Outer, IReadOnlyList<PathD> Holes)
{
    /// <summary>The outer boundary then its holes, Clipper2's convention: outer positive, holes negative.</summary>
    public PathsD Paths
    {
        get
        {
            var p = new PathsD(1 + Holes.Count) { Outer };
            p.AddRange(Holes);
            return p;
        }
    }

    /// <summary>The enclosed area net of the holes, in square pixels.</summary>
    public double Area => Clipper.Area(Outer) + Holes.Sum(Clipper.Area);
}

public static class Contours
{
    /// <summary>The raw marching-squares rings of <paramref name="map"/> at <paramref name="level"/>, unsimplified,
    /// in a consistent winding (outer boundaries one way, holes the other).</summary>
    public static List<PathD> Iso(CoverageMap map, float level = 0.5f)
    {
        int w = map.Width, h = map.Height;
        int gw = w + 2, gh = h + 2;   // grid points at pixel centres, -1 … w
        float V(int i, int j) => (uint)i < (uint)w && (uint)j < (uint)h ? map.Values[j * w + i] : 0f;
        int Idx(int i, int j) => (j + 1) * gw + (i + 1);
        int HEdge(int i, int j) => 2 * Idx(i, j);        // (i,j)–(i+1,j)
        int VEdge(int i, int j) => 2 * Idx(i, j) + 1;    // (i,j)–(i,j+1)

        PointD Cross(int edge)
        {
            int idx = edge >> 1;
            int i = idx % gw - 1, j = idx / gw - 1;
            bool horizontal = (edge & 1) == 0;
            int di = horizontal ? 1 : 0, dj = horizontal ? 0 : 1;
            double t = CrossingAt(V(i - di, j - dj), V(i, j), V(i + di, j + dj), V(i + 2 * di, j + 2 * dj), level);
            return horizontal ? new PointD(i + 0.5 + t, j + 0.5) : new PointD(i + 0.5, j + 0.5 + t);
        }

        var next = new Dictionary<int, int>();   // segment start edge → end edge
        var starts = new List<int>();
        void Seg(int a, int b)
        {
            next[a] = b;
            starts.Add(a);
        }

        for (int j = -1; j < h; j++)
            for (int i = -1; i < w; i++)
            {
                bool tl = V(i, j) >= level, tr = V(i + 1, j) >= level, br = V(i + 1, j + 1) >= level, bl = V(i, j + 1) >= level;
                int code = (tl ? 1 : 0) | (tr ? 2 : 0) | (br ? 4 : 0) | (bl ? 8 : 0);
                if (code is 0 or 15) continue;
                int top = HEdge(i, j), right = VEdge(i + 1, j), bottom = HEdge(i, j + 1), left = VEdge(i, j);
                // Clockwise: top (tl→tr), right (tr→br), bottom (br→bl), left (bl→tl).
                if (code == 5 || code == 10)
                {
                    bool centre = (V(i, j) + V(i + 1, j) + V(i + 1, j + 1) + V(i, j + 1)) / 4f >= level;
                    if (code == 5)   // tl, br inside: A = top (in→out), B = right (out→in), C = bottom (in→out), D = left
                    {
                        if (centre) { Seg(top, right); Seg(bottom, left); }
                        else { Seg(top, left); Seg(bottom, right); }
                    }
                    else             // tr, bl inside: right (in→out), bottom (out→in), left (in→out), top (out→in)
                    {
                        if (centre) { Seg(right, bottom); Seg(left, top); }
                        else { Seg(right, top); Seg(left, bottom); }
                    }
                    continue;
                }
                int exit = -1, entry = -1;
                if (tl && !tr) exit = top; else if (!tl && tr) entry = top;
                if (tr && !br) exit = right; else if (!tr && br) entry = right;
                if (br && !bl) exit = bottom; else if (!br && bl) entry = bottom;
                if (bl && !tl) exit = left; else if (!bl && tl) entry = left;
                Seg(exit, entry);
            }

        var rings = new List<PathD>();
        var used = new HashSet<int>();
        foreach (int s in starts)
        {
            if (used.Contains(s)) continue;
            var ring = new PathD();
            int e = s;
            while (used.Add(e))
            {
                ring.Add(Cross(e));
                if (!next.TryGetValue(e, out e)) break;
            }
            if (ring.Count >= 3) rings.Add(ring);
        }
        return rings;
    }

    /// <summary>Where between two pixel centres valued <paramref name="a"/> and <paramref name="b"/> the coverage
    /// crosses <paramref name="level"/>, as a fraction of the way from a to b.
    ///
    /// Where the profile is a clean step — the centre beyond the outside one fully out, the centre beyond the inside one
    /// fully in — the edge is placed EXACTLY: a box-filtered step at e gives coverages whose sum over the two pixels is
    /// 1.5 − (e − a's centre), whichever of the two pixels the edge lies in. Linear interpolation between centres is off
    /// by up to 0.09 px there, periodically along a slanted edge, which reads as up to 0.8° of tilt on a 20 px side.
    /// Anywhere else (a feature thinner than two pixels, a corner) it falls back to linear interpolation.</summary>
    internal static double CrossingAt(float before, float a, float b, float after, float level)
    {
        const float Out = 0.02f, In = 0.98f;
        double t;
        if (a < level && b >= level && before <= Out && after >= In) t = 1.5 - (a + b);
        else if (a >= level && b < level && before >= In && after <= Out) t = (a + b) - 0.5;
        else t = (level - a) / (double)(b - a);
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>The regions of <paramref name="map"/>: traced, nested, simplified and snapped.</summary>
    public static List<ContourRegion> Trace(CoverageMap map, ContourOptions? options = null)
    {
        options ??= new ContourOptions();
        var rings = Iso(map, options.Level).Where(r => Math.Abs(Clipper.Area(r)) >= Math.Max(options.MinArea, 1e-9)).ToList();
        if (rings.Count == 0) return [];
        var areas = rings.Select(Clipper.Area).ToList();
        int biggest = 0;
        for (int i = 1; i < rings.Count; i++) if (Math.Abs(areas[i]) > Math.Abs(areas[biggest])) biggest = i;
        double outerSign = Math.Sign(areas[biggest]);

        var outers = new List<int>();
        var holes = new List<int>();
        for (int i = 0; i < rings.Count; i++) (Math.Sign(areas[i]) == outerSign ? outers : holes).Add(i);
        var bounds = rings.Select(Clipper.GetBounds).ToList();
        var holesOf = outers.ToDictionary(o => o, _ => new List<int>());
        foreach (int hIdx in holes)
        {
            var probe = rings[hIdx][0];
            int best = -1;
            foreach (int o in outers)
            {
                var b = bounds[o];
                if (probe.x < b.left || probe.x > b.right || probe.y < b.top || probe.y > b.bottom) continue;
                if (Clipper.PointInPolygon(probe, rings[o]) == PointInPolygonResult.IsOutside) continue;
                if (best < 0 || Math.Abs(areas[o]) < Math.Abs(areas[best])) best = o;
            }
            if (best >= 0) holesOf[best].Add(hIdx);
        }

        var regions = new List<ContourRegion>();
        foreach (int o in outers)
        {
            var outer = Orient(Shape(rings[o], options), positive: true);
            var hs = holesOf[o].Select(hi => Orient(Shape(rings[hi], options), positive: false)).Where(p => p.Count >= 3).ToList();
            if (outer.Count < 3) continue;
            hs.Sort(CompareTopLeft);
            regions.Add(new ContourRegion(outer, hs));
        }
        regions.Sort((a, b) => CompareTopLeft(a.Outer, b.Outer));
        return regions;
    }

    private static int CompareTopLeft(PathD a, PathD b)
    {
        var ba = Clipper.GetBounds(a);
        var bb = Clipper.GetBounds(b);
        int c = ba.top.CompareTo(bb.top);
        if (c != 0) return c;
        c = ba.left.CompareTo(bb.left);
        return c != 0 ? c : Clipper.Area(b).CompareTo(Clipper.Area(a));
    }

    private static PathD Orient(PathD p, bool positive)
    {
        if (p.Count >= 3 && (Clipper.Area(p) > 0) != positive) p.Reverse();
        return p;
    }

    // ── Simplify and snap one ring ────────────────────────────────────────────────────────────────────────────────

    private sealed class Edge
    {
        public double Cx, Cy;     // a point on the line
        public double Ux, Uy;     // unit direction (exact for a snapped edge)
        public double Length;
        public bool Snapped;
        public int First, Last;   // raw indices it spans
    }

    internal static PathD Shape(PathD raw, ContourOptions o)
    {
        var keep = Polylines.SimplifyClosed(raw, o.Simplify);
        var simple = new PathD(keep.Count);
        foreach (int k in keep) simple.Add(raw[k]);
        if (!o.Snap || keep.Count < 3) return simple;

        int n = raw.Count, m = keep.Count;
        var edges = new List<Edge>(m);
        for (int i = 0; i < m; i++) edges.Add(FitEdge(raw, keep[i], keep[(i + 1) % m]));

        // A short edge between two longer ones meeting near a right angle is an anti-aliased corner's rounding. It goes
        // BEFORE snapping: its direction is noise, and a noisy 1 px edge that happened to lie within the tolerance of an
        // axis would otherwise be snapped and carry nothing true.
        bool cut = true;
        while (cut && edges.Count > 3)
        {
            cut = false;
            for (int i = 0; i < edges.Count && edges.Count > 3; i++)
            {
                var e = edges[i];
                var a = edges[(i + edges.Count - 1) % edges.Count];
                var b = edges[(i + 1) % edges.Count];
                if (e.Length >= o.ChamferMax || a.Length < 2 * e.Length || b.Length < 2 * e.Length) continue;
                if (a.Length < o.ChamferMax || b.Length < o.ChamferMax) continue;
                if (Math.Abs(a.Ux * b.Uy - a.Uy * b.Ux) < Math.Sin(60 * Math.PI / 180)) continue;
                edges.RemoveAt(i);
                cut = true;
                break;
            }
        }

        double tol = o.SnapAngleDeg * Math.PI / 180;
        var targets = new List<(double Ang, double Ux, double Uy)> { (0, 1, 0), (Math.PI / 2, 0, 1), (Math.PI, 1, 0) };
        if (o.Snap45)
        {
            double r = Math.Sqrt(0.5);
            targets.Add((Math.PI / 4, r, r));
            targets.Add((3 * Math.PI / 4, -r, r));
        }
        foreach (var e in edges)
        {
            double ang = Math.Atan2(e.Uy, e.Ux);
            if (ang < 0) ang += Math.PI;   // a line's direction, 0 … π
            foreach (var t in targets)
                if (Math.Abs(ang - t.Ang) <= tol)
                {
                    e.Ux = t.Ux; e.Uy = t.Uy; e.Snapped = true;
                    break;
                }
        }

        // A short unsnapped edge between two snapped, non-parallel ones is an anti-aliased corner's rounding.
        bool changed = true;
        while (changed && edges.Count > 3)
        {
            changed = false;
            for (int i = 0; i < edges.Count && edges.Count > 3; i++)
            {
                var e = edges[i];
                var a = edges[(i + edges.Count - 1) % edges.Count];
                var b = edges[(i + 1) % edges.Count];
                if (e.Snapped || e.Length >= o.ChamferMax || !a.Snapped || !b.Snapped) continue;
                if (Math.Abs(a.Ux * b.Uy - a.Uy * b.Ux) < 1e-9) continue;
                edges.RemoveAt(i);
                changed = true;
                break;
            }
        }

        // Neighbouring edges on one line are one edge.
        changed = true;
        while (changed && edges.Count > 3)
        {
            changed = false;
            for (int i = 0; i < edges.Count && edges.Count > 3; i++)
            {
                var a = edges[i];
                var b = edges[(i + 1) % edges.Count];
                if (Math.Abs(a.Ux * b.Uy - a.Uy * b.Ux) > Math.Sin(0.5 * Math.PI / 180)) continue;
                double off = Math.Abs((b.Cx - a.Cx) * -a.Uy + (b.Cy - a.Cy) * a.Ux);
                if (off > 0.5) continue;
                double wa = a.Length, wb = b.Length, wt = Math.Max(wa + wb, 1e-12);
                a.Cx = (a.Cx * wa + b.Cx * wb) / wt;
                a.Cy = (a.Cy * wa + b.Cy * wb) / wt;
                if (!a.Snapped && b.Snapped) { a.Ux = b.Ux; a.Uy = b.Uy; a.Snapped = true; }
                a.Length += b.Length;
                a.Last = b.Last;
                edges.RemoveAt((i + 1) % edges.Count);
                changed = true;
                break;
            }
        }

        SquareRightAngles(edges, o.RightAngleDeg * Math.PI / 180);

        // Re-intersect.
        var outp = new PathD(edges.Count);
        for (int i = 0; i < edges.Count; i++)
        {
            var a = edges[i];
            var b = edges[(i + 1) % edges.Count];
            var shared = raw[a.Last % n];
            if (!Intersect(a, b, out var p) || Dist(p, shared) > 3.0)
            {
                // Parallel, or meeting far from where the contour turned: keep the turn, on each line.
                outp.Add(Project(a, shared));
                outp.Add(Project(b, shared));
                continue;
            }
            outp.Add(p);
        }
        // The polygon starts at the corner ending the last edge; rotate so it starts where the first edge starts.
        if (outp.Count > 0) { var last = outp[^1]; outp.RemoveAt(outp.Count - 1); outp.Insert(0, last); }
        return Dedupe(outp);
    }

    private static Edge FitEdge(PathD raw, int first, int last)
    {
        int n = raw.Count;
        var pts = new List<PointD>();
        for (int k = first; ; k = (k + 1) % n)
        {
            pts.Add(raw[k]);
            if (k == last) break;
        }
        double len = Polylines.Length(pts);
        // The points near either end belong to the corners as much as to this edge.
        double trim = Math.Min(1.0, len / 4);
        var inner = new List<PointD>();
        double acc = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            if (i > 0) acc += Dist(pts[i], pts[i - 1]);
            if (acc >= trim && len - acc >= trim) inner.Add(pts[i]);
        }
        if (inner.Count < 2) inner = pts;
        var fit = Fit.Segment(inner);
        double ux = fit.B.x - fit.A.x, uy = fit.B.y - fit.A.y, ul = Math.Sqrt(ux * ux + uy * uy);
        // Keep the edge's own direction of travel.
        double dx = raw[last].x - raw[first].x, dy = raw[last].y - raw[first].y;
        if (ul < 1e-12) { ux = dx; uy = dy; ul = Math.Sqrt(ux * ux + uy * uy); }
        if (ul < 1e-12) { ux = 1; uy = 0; ul = 1; }
        ux /= ul; uy /= ul;
        if (ux * dx + uy * dy < 0) { ux = -ux; uy = -uy; }
        return new Edge { Cx = fit.Cx, Cy = fit.Cy, Ux = ux, Uy = uy, Length = Math.Sqrt(dx * dx + dy * dy), First = first, Last = last };
    }

    /// <summary>Runs of unsnapped edges meeting within <paramref name="tol"/> of a right angle share one orientation, so
    /// their corners are exact.</summary>
    private static void SquareRightAngles(List<Edge> edges, double tol)
    {
        int m = edges.Count;
        if (m < 3) return;
        bool NearRight(Edge a, Edge b) => Math.Abs(Math.Abs(a.Ux * b.Ux + a.Uy * b.Uy)) <= Math.Sin(tol);
        var group = new int[m];
        Array.Fill(group, -1);
        int groups = 0;
        for (int i = 0; i < m; i++)
        {
            if (edges[i].Snapped || group[i] >= 0) continue;
            var stack = new Stack<int>();
            stack.Push(i);
            group[i] = groups;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                foreach (int nb in new[] { (k + 1) % m, (k + m - 1) % m })
                    if (!edges[nb].Snapped && group[nb] < 0 && NearRight(edges[k], edges[nb]))
                    {
                        group[nb] = groups;
                        stack.Push(nb);
                    }
            }
            groups++;
        }
        for (int gi = 0; gi < groups; gi++)
        {
            var members = Enumerable.Range(0, m).Where(k => group[k] == gi).ToList();
            if (members.Count < 2) continue;
            // The length-weighted mean orientation mod 90°. A snapped neighbour does NOT fix it: a run squared onto an
            // axis because one neighbour was snapped would be snapping by another name, past the stated tolerance.
            double sx = 0, sy = 0;
            foreach (int k in members)
            {
                double a4 = 4 * Math.Atan2(edges[k].Uy, edges[k].Ux);
                sx += edges[k].Length * Math.Cos(a4);
                sy += edges[k].Length * Math.Sin(a4);
            }
            double phi = Math.Atan2(sy, sx) / 4;
            foreach (int k in members)
            {
                var e = edges[k];
                double best = double.MaxValue, bx = e.Ux, by = e.Uy;
                for (int q = 0; q < 4; q++)
                {
                    double a = phi + q * Math.PI / 2, cx = Math.Cos(a), cy = Math.Sin(a);
                    double d = 1 - (cx * e.Ux + cy * e.Uy);
                    if (d < best) { best = d; bx = cx; by = cy; }
                }
                e.Ux = bx; e.Uy = by;
            }
        }
    }

    private static bool Intersect(Edge a, Edge b, out PointD p)
    {
        double den = a.Ux * b.Uy - a.Uy * b.Ux;
        if (Math.Abs(den) < 1e-9) { p = default; return false; }
        // Axis-aligned lines give their coordinate exactly, so a snapped corner is exact rather than nearly so.
        bool aH = a.Snapped && a.Uy == 0, aV = a.Snapped && a.Ux == 0, bH = b.Snapped && b.Uy == 0, bV = b.Snapped && b.Ux == 0;
        if (aH && bV) { p = new PointD(b.Cx, a.Cy); return true; }
        if (aV && bH) { p = new PointD(a.Cx, b.Cy); return true; }
        double t = ((b.Cx - a.Cx) * b.Uy - (b.Cy - a.Cy) * b.Ux) / den;
        double x = a.Cx + t * a.Ux, y = a.Cy + t * a.Uy;
        if (aH || bH) y = aH ? a.Cy : b.Cy;
        if (aV || bV) x = aV ? a.Cx : b.Cx;
        p = new PointD(x, y);
        return true;
    }

    private static PointD Project(Edge e, PointD p)
    {
        double t = (p.x - e.Cx) * e.Ux + (p.y - e.Cy) * e.Uy;
        double x = e.Cx + t * e.Ux, y = e.Cy + t * e.Uy;
        if (e.Snapped && e.Uy == 0) y = e.Cy;
        if (e.Snapped && e.Ux == 0) x = e.Cx;
        return new PointD(x, y);
    }

    private static PathD Dedupe(PathD p)
    {
        var r = new PathD(p.Count);
        foreach (var q in p)
            if (r.Count == 0 || Dist(r[^1], q) > 1e-9) r.Add(q);
        while (r.Count > 1 && Dist(r[0], r[^1]) <= 1e-9) r.RemoveAt(r.Count - 1);
        return r;
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
}
