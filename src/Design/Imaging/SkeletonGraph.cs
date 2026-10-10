// The skeleton as a graph — brief-img-1-raster-core.md R-im1-6.
//
// Nodes at end points (degree 1) and junctions (degree ≥ 3, adjacent junction pixels merged into one node), edges as
// the pixel chains between them, each simplified to a polyline (Douglas–Peucker at 0.5 px) carrying the mean stroke
// width along it. Spurs — a branch from a junction to an end shorter than one stroke width — are pruned, and the
// junction they leave behind with two edges is dissolved into one edge.
//
// Before the walk, staircase corners are removed: Zhang–Suen leaves a pixel at the inside corner of a diagonal step
// whose two 4-neighbours are already 8-adjacent, and such a pixel has three skeleton neighbours without being a
// junction. Removing it never disconnects anything (its two neighbours touch diagonally).

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

/// <summary>A node: an end (degree 1), a junction (degree ≥ 3), an isolated dot (0) or the anchor of a closed loop (2).</summary>
/// <param name="X">Pixel coordinates of its centre (pixel centres at ½; a merged junction is the mean of its pixels).</param>
public sealed record SkeletonNode(int Id, double X, double Y, int Degree)
{
    public bool IsJunction => Degree >= 3;
    public bool IsEnd => Degree == 1;
}

/// <summary>An edge between two nodes (the same node for a closed loop).</summary>
/// <param name="Pixels">The chain, node pixel to node pixel.</param>
/// <param name="Polyline">The chain simplified, from <see cref="From"/>'s centre to <see cref="To"/>'s.</param>
/// <param name="PolylineIndices">For each polyline vertex, the index in <see cref="Pixels"/> it stands on.</param>
/// <param name="MeanWidth">The mean stroke width along it, in pixels.</param>
public sealed record SkeletonEdge(int Id, int From, int To, IReadOnlyList<(int X, int Y)> Pixels, PathD Polyline,
    IReadOnlyList<int> PolylineIndices, double MeanWidth, double Length);

public sealed record SkeletonGraphOptions
{
    /// <summary>The stroke width spurs are measured against; null estimates it (<see cref="StrokeWidth"/>).</summary>
    public double? StrokeWidth { get; init; }

    /// <summary>The Douglas–Peucker tolerance for each edge's polyline, in pixels.</summary>
    public double Simplify { get; init; } = 0.5;

    public int MaxThreads { get; init; }
}

public sealed class SkeletonGraph
{
    private SkeletonGraph(IReadOnlyList<SkeletonNode> nodes, IReadOnlyList<SkeletonEdge> edges, double strokeWidth)
    {
        Nodes = nodes;
        Edges = edges;
        StrokeWidth = strokeWidth;
    }

    public IReadOnlyList<SkeletonNode> Nodes { get; }
    public IReadOnlyList<SkeletonEdge> Edges { get; }

    /// <summary>The stroke width the spurs were pruned against.</summary>
    public double StrokeWidth { get; }

    /// <summary>The 8-neighbours of (x, y) set in <paramref name="img"/>.</summary>
    public static int Neighbours(BinaryImage img, int x, int y)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if ((dx | dy) != 0 && img.At(x + dx, y + dy)) n++;
        return n;
    }

    /// <summary>Thins <paramref name="mask"/> and reads its skeleton as a graph.</summary>
    public static SkeletonGraph Build(BinaryImage mask, SkeletonGraphOptions? options = null)
    {
        options ??= new SkeletonGraphOptions();
        var dt = DistanceTransform.Compute(mask, options.MaxThreads);
        var skel = Skeleton.Thin(mask);
        RemoveStaircaseCorners(skel);
        double sw = options.StrokeWidth ?? Imaging.StrokeWidth.Estimate(skel, dt);
        var g = Walk(skel);
        Prune(g, sw);
        return Freeze(g, dt, mask.Width, sw, options.Simplify);
    }

    internal static void RemoveStaircaseCorners(BinaryImage s)
    {
        int w = s.Width, h = s.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!s[x, y]) continue;
                bool n = s.At(x, y - 1), e = s.At(x + 1, y), so = s.At(x, y + 1), wv = s.At(x - 1, y);
                bool ne = s.At(x + 1, y - 1), se = s.At(x + 1, y + 1), sw = s.At(x - 1, y + 1), nw = s.At(x - 1, y - 1);
                if ((n && e && !so && !sw && !wv) || (e && so && !wv && !nw && !n) ||
                    (so && wv && !n && !ne && !e) || (wv && n && !e && !se && !so))
                    s.Pixels[y * w + x] = 0;
            }
    }

    // ── The mutable graph the walk builds and the pruning edits ───────────────────────────────────────────────────

    private sealed class MNode
    {
        public required List<(int X, int Y)> Pixels;
        public bool Alive = true;
    }

    private sealed class MEdge
    {
        public int From, To;
        public required List<(int X, int Y)> Chain;
        public bool Alive = true;
    }

    private sealed class MGraph
    {
        public readonly List<MNode> Nodes = [];
        public readonly List<MEdge> Edges = [];

        public int Degree(int n)
        {
            int d = 0;
            foreach (var e in Edges)
            {
                if (!e.Alive) continue;
                if (e.From == n) d++;
                if (e.To == n) d++;
            }
            return d;
        }
    }

    // 4-neighbours first, so a walk along a diagonal step takes the pixel beside it before the one past it.
    private static readonly (int Dx, int Dy)[] Order = [(0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1)];

    private static MGraph Walk(BinaryImage s)
    {
        int w = s.Width, h = s.Height;
        var g = new MGraph();
        var nodeOf = new int[w * h];
        Array.Fill(nodeOf, -1);

        // Node pixels: ends and junction pixels; adjacent junction pixels are one node.
        var isJunction = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!s[x, y]) continue;
                int nb = Neighbours(s, x, y);
                if (nb >= 3) isJunction[y * w + x] = true;
                else if (nb <= 1)
                {
                    nodeOf[y * w + x] = g.Nodes.Count;
                    g.Nodes.Add(new MNode { Pixels = [(x, y)] });
                }
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!isJunction[y * w + x] || nodeOf[y * w + x] >= 0) continue;
                int id = g.Nodes.Count;
                var px = new List<(int, int)>();
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));
                nodeOf[y * w + x] = id;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    px.Add((cx, cy));
                    foreach (var (dx, dy) in Order)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int j = ny * w + nx;
                        if (isJunction[j] && nodeOf[j] < 0) { nodeOf[j] = id; stack.Push((nx, ny)); }
                    }
                }
                px.Sort((a, b) => a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item1.CompareTo(b.Item1));
                g.Nodes.Add(new MNode { Pixels = px });
            }

        var visited = new bool[w * h];
        var direct = new HashSet<(int, int)>();
        void WalkFrom(int startNode, (int X, int Y) q, (int X, int Y) r)
        {
            var chain = new List<(int X, int Y)> { q, r };
            visited[r.Y * w + r.X] = true;
            var prev = q;
            var cur = r;
            while (true)
            {
                (int X, int Y)? next = null;
                int endNode = -1;
                foreach (var (dx, dy) in Order)
                {
                    int nx = cur.X + dx, ny = cur.Y + dy;
                    if (!s.At(nx, ny) || (nx, ny) == prev) continue;
                    int nn = nodeOf[ny * w + nx];
                    if (nn < 0) continue;
                    if (nn == startNode && chain.Count < 3) continue;   // not straight back into the node just left
                    next = (nx, ny);
                    endNode = nn;
                    break;
                }
                if (next is null)
                    foreach (var (dx, dy) in Order)
                    {
                        int nx = cur.X + dx, ny = cur.Y + dy;
                        if (!s.At(nx, ny) || nodeOf[ny * w + nx] >= 0 || visited[ny * w + nx]) continue;
                        next = (nx, ny);
                        break;
                    }
                if (next is null)
                {
                    // A dead end that is not an end point (a corner the staircase rule left with one way on): it
                    // becomes an end.
                    endNode = g.Nodes.Count;
                    nodeOf[cur.Y * w + cur.X] = endNode;
                    g.Nodes.Add(new MNode { Pixels = [cur] });
                    g.Edges.Add(new MEdge { From = startNode, To = endNode, Chain = chain });
                    return;
                }
                chain.Add(next.Value);
                if (endNode >= 0)
                {
                    g.Edges.Add(new MEdge { From = startNode, To = endNode, Chain = chain });
                    return;
                }
                visited[next.Value.Y * w + next.Value.X] = true;
                prev = cur;
                cur = next.Value;
            }
        }

        for (int n = 0; n < g.Nodes.Count; n++)
            foreach (var q in g.Nodes[n].Pixels.ToList())
                foreach (var (dx, dy) in Order)
                {
                    int nx = q.X + dx, ny = q.Y + dy;
                    if (!s.At(nx, ny)) continue;
                    int j = ny * w + nx;
                    int other = nodeOf[j];
                    if (other == n) continue;
                    if (other >= 0)
                    {
                        // Two nodes touching: an edge of no length, recorded once.
                        int pa = q.Y * w + q.X;
                        if (direct.Add((Math.Min(pa, j), Math.Max(pa, j))))
                            g.Edges.Add(new MEdge { From = n, To = other, Chain = [q, (nx, ny)] });
                        continue;
                    }
                    if (visited[j]) continue;
                    WalkFrom(n, q, (nx, ny));
                }

        // Closed loops with no node on them: the first pixel in raster order anchors each.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!s[x, y] || visited[i] || nodeOf[i] >= 0) continue;
                int id = g.Nodes.Count;
                nodeOf[i] = id;
                g.Nodes.Add(new MNode { Pixels = [(x, y)] });
                foreach (var (dx, dy) in Order)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!s.At(nx, ny) || visited[ny * w + nx] || nodeOf[ny * w + nx] >= 0) continue;
                    WalkFrom(id, (x, y), (nx, ny));
                    break;
                }
            }
        return g;
    }

    private static double ChainLength(List<(int X, int Y)> c)
    {
        double s = 0;
        for (int i = 1; i < c.Count; i++)
            s += (c[i].X != c[i - 1].X && c[i].Y != c[i - 1].Y) ? Math.Sqrt(2) : 1;
        return s;
    }

    private static void Prune(MGraph g, double strokeWidth)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < g.Edges.Count; i++)
            {
                var e = g.Edges[i];
                if (!e.Alive || e.From == e.To) continue;
                int df = g.Degree(e.From), dt = g.Degree(e.To);
                bool spur = (df == 1 && dt >= 3) || (dt == 1 && df >= 3);
                if (!spur || ChainLength(e.Chain) >= strokeWidth) continue;
                e.Alive = false;
                if (df == 1) g.Nodes[e.From].Alive = false; else g.Nodes[e.To].Alive = false;
                changed = true;
            }
            changed |= Dissolve(g);
        }
        Dissolve(g);
    }

    /// <summary>Joins the two edges of every node left with exactly two (that are not one loop).</summary>
    private static bool Dissolve(MGraph g)
    {
        bool any = false;
        for (int n = 0; n < g.Nodes.Count; n++)
        {
            if (!g.Nodes[n].Alive) continue;
            var ends = new List<(MEdge E, bool AtFrom)>();
            foreach (var e in g.Edges)
            {
                if (!e.Alive) continue;
                if (e.From == n) ends.Add((e, true));
                if (e.To == n) ends.Add((e, false));
            }
            if (ends.Count != 2 || ends[0].E == ends[1].E) continue;
            var (a, aAtFrom) = ends[0];
            var (b, bAtFrom) = ends[1];
            // a runs … → n, b runs n → …
            var ca = aAtFrom ? Enumerable.Reverse(a.Chain).ToList() : a.Chain;
            int aStart = aAtFrom ? a.To : a.From;
            var cb = bAtFrom ? b.Chain : Enumerable.Reverse(b.Chain).ToList();
            int bEnd = bAtFrom ? b.To : b.From;
            var joined = new List<(int X, int Y)>(ca);
            // The node's own pixels sit between the two chains; a merged junction may have several, and they join.
            for (int i = cb[0] == joined[^1] ? 1 : 0; i < cb.Count; i++) joined.Add(cb[i]);
            a.Alive = b.Alive = false;
            g.Edges.Add(new MEdge { From = aStart, To = bEnd, Chain = joined });
            g.Nodes[n].Alive = false;
            any = true;
        }
        return any;
    }

    private static SkeletonGraph Freeze(MGraph g, float[] dt, int w, double strokeWidth, double simplify)
    {
        var map = new int[g.Nodes.Count];
        Array.Fill(map, -1);
        var nodes = new List<SkeletonNode>();
        var centres = new List<PointD>();
        for (int n = 0; n < g.Nodes.Count; n++)
        {
            if (!g.Nodes[n].Alive) continue;
            map[n] = nodes.Count;
            var px = g.Nodes[n].Pixels;
            var c = new PointD(px.Average(p => p.X + 0.5), px.Average(p => p.Y + 0.5));
            centres.Add(c);
            nodes.Add(new SkeletonNode(nodes.Count, c.x, c.y, g.Degree(n)));
        }
        var edges = new List<SkeletonEdge>();
        foreach (var e in g.Edges)
        {
            if (!e.Alive) continue;
            int f = map[e.From], t = map[e.To];
            var pts = new List<PointD>(e.Chain.Count) { centres[f] };
            for (int i = 1; i < e.Chain.Count - 1; i++) pts.Add(new PointD(e.Chain[i].X + 0.5, e.Chain[i].Y + 0.5));
            pts.Add(centres[t]);
            var keep = Polylines.SimplifyOpen(pts, simplify);
            var poly = new PathD(keep.Count);
            foreach (int k in keep) poly.Add(pts[k]);
            double sum = 0;
            int cnt = 0;
            for (int i = 1; i < e.Chain.Count - 1; i++)
            {
                sum += Math.Max(1.0, 2.0 * dt[e.Chain[i].Y * w + e.Chain[i].X] - 1.0);
                cnt++;
            }
            if (cnt == 0)
                foreach (var p in e.Chain) { sum += Math.Max(1.0, 2.0 * dt[p.Y * w + p.X] - 1.0); cnt++; }
            edges.Add(new SkeletonEdge(edges.Count, f, t, e.Chain, poly, keep, sum / cnt, Polylines.Length(poly)));
        }
        // Stable order: by the first pixel of each chain, then its second.
        var ordered = edges
            .OrderBy(e => Math.Min(Key(e.Pixels[0], w), Key(e.Pixels[^1], w)))
            .ThenBy(e => Math.Max(Key(e.Pixels[0], w), Key(e.Pixels[^1], w)))
            .ThenBy(e => e.Pixels.Count)
            .Select((e, i) => e with { Id = i })
            .ToList();
        return new SkeletonGraph(nodes, ordered, strokeWidth);
    }

    private static long Key((int X, int Y) p, int w) => (long)p.Y * w + p.X;
}
