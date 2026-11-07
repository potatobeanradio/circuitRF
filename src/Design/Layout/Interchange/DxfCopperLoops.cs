// A drawing of bare outlines into closed loops (brief DFR38-1). Pure: points in, loops and what would not
// close out. No technology, no layers, no messages.
//
// The order is the requirement's. Each step changes what the next one sees:
//
//   1. Points within SamePointDbu of each other become one point: a file's shared corners agree to about
//      1e-12 mm, and two rounding passes can still leave them a unit apart.
//   2. Every segment splits at every point lying on it. One rule then covers a T junction and a collinear
//      overlap: both leave two segments between the same two points, or one point on a longer segment.
//   3. Dangling ends join in pairs within the stated tolerance, nearest first, by moving one onto the
//      other. A closing segment would add an edge a micron long, which the mesher then has to resolve.
//   4. An edge drawn more than once stays once if the loop it belongs to closes that way (an overlap at the
//      board edge). It goes if its ends are left with an odd number of edges: two loops sharing an edge,
//      each drawn in full, are one region and the edge is inside it.
//   5. Whatever cannot be part of a loop comes off as an open outline.
//   6. What is left has an even number of edges at every point, so it decomposes into closed walks. Which
//      walks does not matter: even-odd counts boundary crossings mod 2, and that is the same for any
//      decomposition of the same edges. So the reading of nested and touching loops is settled by the
//      edges alone.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>An outline that closed nothing: its points, and the ones of them that met no other edge.</summary>
public sealed record DxfOpenOutline(long[] Xy, IReadOnlyList<(long X, long Y)> OpenEnds);

/// <summary>What <see cref="DxfCopperLoops.Chain"/> found.</summary>
/// <param name="Loops">Closed walks, for an even-odd fill. Not one per drawn loop: see the file's header.</param>
/// <param name="OverlapsResolved">Edges drawn more than once that were kept once.</param>
/// <param name="SharedEdgesRemoved">Edges drawn twice that lay between two loops and were dropped.</param>
/// <param name="EndsJoined">Pairs of dangling ends joined within the tolerance.</param>
/// <param name="LargestJoinDbu">The widest gap so joined, or 0.</param>
public sealed record DxfLoopResult(
    IReadOnlyList<long[]> Loops, IReadOnlyList<DxfOpenOutline> Open,
    int OverlapsResolved, int SharedEdgesRemoved, int EndsJoined, long LargestJoinDbu);

public static class DxfCopperLoops
{
    /// <summary>Two points this close are one point, whatever the join tolerance says.</summary>
    public const long SamePointDbu = 2;

    /// <param name="polylines">Open point lists, flat x,y.</param>
    /// <param name="rings">Closed point lists, flat x,y, implicitly closed.</param>
    /// <param name="joinToleranceDbu">How far apart two dangling ends may be and still be joined.</param>
    public static DxfLoopResult Chain(
        IReadOnlyList<long[]> polylines, IReadOnlyList<long[]> rings, long joinToleranceDbu)
    {
        var points = new List<(long X, long Y)>();
        var ids = new Dictionary<(long, long), int>();
        int IdOf(long x, long y)
        {
            if (ids.TryGetValue((x, y), out int id)) return id;
            ids[(x, y)] = points.Count;
            points.Add((x, y));
            return points.Count - 1;
        }

        var drawn = new List<(int A, int B)>();
        foreach (var xy in polylines)
            for (int i = 0; i + 3 < xy.Length; i += 2)
                drawn.Add((IdOf(xy[i], xy[i + 1]), IdOf(xy[i + 2], xy[i + 3])));
        foreach (var xy in rings)
            for (int i = 0, n = xy.Length / 2; i < n && n >= 2; i++)
            {
                int j = (i + 1) % n;
                drawn.Add((IdOf(xy[2 * i], xy[2 * i + 1]), IdOf(xy[2 * j], xy[2 * j + 1])));
            }

        var same = MergeNearPoints(points);
        var edges = new Graph();
        foreach (var (a, b) in SplitAtPointsOnThem(points, same, drawn))
            edges.Add(a, b);

        var (joined, largest) = JoinDanglingEnds(points, edges, joinToleranceDbu);

        // The ends as they stand before anything is peeled: these are the ones a report can name.
        var dangling = edges.Vertices.Where(v => edges.Degree(v) == 1).ToHashSet();

        int removed = RemoveSharedEdges(edges);
        int overlaps = edges.AllEdges.Count(e => edges.Multiplicity(e.A, e.B) > 1);

        var open = new Graph();
        PeelStubs(edges, open);

        // Past the stubs, an odd point has three or more edges, and no loop reading can use them all: a
        // line across a pour, ending on both of its sides. The fewest edges between two such points come off.
        while (edges.Vertices.Where(v => edges.Degree(v) % 2 == 1).ToList() is { Count: >= 2 } odd)
        {
            var path = ShortestPath(edges, odd[0], odd.Skip(1).ToHashSet());
            if (path is null) break;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                edges.Remove(path[i], path[i + 1]);
                open.Add(path[i], path[i + 1]);
            }
            PeelStubs(edges, open);
        }

        return new DxfLoopResult(
            ClosedWalks(edges, points), OpenChains(open, dangling, points),
            overlaps, removed, joined, largest);
    }

    /// <summary>
    /// Even-odd fill of <paramref name="loops"/>: the outermost region is copper, what it encloses is not,
    /// and so on inward. <paramref name="invert"/> takes the other parity, so what the outermost loops
    /// enclose as clearances is the copper and the outermost region itself is not.
    /// </summary>
    public static IReadOnlyList<PolygonShape> Fill(IReadOnlyList<long[]> loops, bool invert)
    {
        var clipper = new Clipper64 { PreserveCollinear = false };
        clipper.AddSubject(LayoutClipper.RingsToClipperPaths(loops));
        var tree = new PolyTree64();
        clipper.Execute(ClipType.Union, FillRule.EvenOdd, tree);

        var shapes = new List<PolygonShape>();
        Collect(tree, invert ? 1 : 0, 0, shapes);
        return shapes;
    }

    private static void Collect(PolyPath64 node, int fillDepth, int depth, List<PolygonShape> shapes)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var child = node[i];
            if (depth % 2 == fillDepth)
            {
                List<long[]>? holes = null;
                for (int j = 0; j < child.Count; j++) (holes ??= []).Add(RingOf(child[j].Polygon));
                shapes.Add(new PolygonShape { Xy = RingOf(child.Polygon), Holes = holes });
            }
            Collect(child, fillDepth, depth + 1, shapes);
        }
    }

    private static long[] RingOf(Path64? path)
    {
        if (path is null) return [];
        var xy = new long[path.Count * 2];
        for (int i = 0; i < path.Count; i++) { xy[2 * i] = path[i].X; xy[2 * i + 1] = path[i].Y; }
        return xy;
    }

    // ── 1. near points ───────────────────────────────────────────────────────────────────────────

    /// <summary>For each point, the id of the first point within <see cref="SamePointDbu"/> of it.</summary>
    private static int[] MergeNearPoints(List<(long X, long Y)> points)
    {
        var map = new int[points.Count];
        var grid = new Dictionary<(long, long), List<int>>();
        long cell = SamePointDbu + 1;

        for (int i = 0; i < points.Count; i++)
        {
            var (x, y) = points[i];
            long cx = FloorDiv(x, cell), cy = FloorDiv(y, cell);
            int found = -1;
            for (long gx = cx - 1; gx <= cx + 1 && found < 0; gx++)
            for (long gy = cy - 1; gy <= cy + 1 && found < 0; gy++)
            {
                if (!grid.TryGetValue((gx, gy), out var list)) continue;
                foreach (int k in list)
                    if (Math.Abs(points[k].X - x) <= SamePointDbu && Math.Abs(points[k].Y - y) <= SamePointDbu)
                    { found = k; break; }
            }
            map[i] = found >= 0 ? found : i;
            if (found >= 0) continue;
            if (!grid.TryGetValue((cx, cy), out var cellList)) grid[(cx, cy)] = cellList = [];
            cellList.Add(i);
        }
        return map;
    }

    private static long FloorDiv(long v, long d) => (long)Math.Floor((double)v / d);

    // ── 2. split ─────────────────────────────────────────────────────────────────────────────────

    private static List<(int A, int B)> SplitAtPointsOnThem(
        List<(long X, long Y)> points, int[] same, List<(int A, int B)> drawn)
    {
        var kept = Enumerable.Range(0, points.Count).Where(i => same[i] == i).ToList();
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (int i in kept)
        {
            minX = Math.Min(minX, points[i].X); maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y); maxY = Math.Max(maxY, points[i].Y);
        }

        long extent = kept.Count == 0 ? 1 : Math.Max(maxX - minX, maxY - minY);
        long cell = Math.Max(extent / Math.Max(1, (long)Math.Sqrt(kept.Count)), SamePointDbu * 2 + 1);
        var grid = new Dictionary<(long, long), List<int>>();
        foreach (int i in kept)
        {
            var key = (FloorDiv(points[i].X, cell), FloorDiv(points[i].Y, cell));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i);
        }

        var result = new List<(int, int)>(drawn.Count);
        var on = new List<(double T, int P)>();
        foreach (var (rawA, rawB) in drawn)
        {
            int a = same[rawA], b = same[rawB];
            if (a == b) continue;
            var (ax, ay) = points[a];
            var (bx, by) = points[b];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;

            on.Clear();
            void Test(int p)
            {
                if (p == a || p == b) return;
                double px = points[p].X - ax, py = points[p].Y - ay;
                double t = (px * dx + py * dy) / len2;
                if (t <= 0 || t >= 1) return;
                double cross = px * dy - py * dx;
                if (cross * cross <= SamePointDbu * SamePointDbu * len2) on.Add((t, p));
            }

            long x0 = FloorDiv(Math.Min(ax, bx) - SamePointDbu, cell), x1 = FloorDiv(Math.Max(ax, bx) + SamePointDbu, cell);
            long y0 = FloorDiv(Math.Min(ay, by) - SamePointDbu, cell), y1 = FloorDiv(Math.Max(ay, by) + SamePointDbu, cell);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > kept.Count)
                foreach (int p in kept) Test(p);
            else
                for (long gx = x0; gx <= x1; gx++)
                for (long gy = y0; gy <= y1; gy++)
                    if (grid.TryGetValue((gx, gy), out var list))
                        foreach (int p in list) Test(p);

            on.Sort();
            int from = a;
            foreach (var (_, p) in on) { result.Add((from, p)); from = p; }
            result.Add((from, b));
        }
        return result;
    }

    // ── 3. join ──────────────────────────────────────────────────────────────────────────────────

    private static (int Joined, long Largest) JoinDanglingEnds(List<(long X, long Y)> points, Graph edges, long tol)
    {
        if (tol <= 0) return (0, 0);

        var ends = edges.Vertices.Where(v => edges.Degree(v) == 1).OrderBy(v => points[v].X).ThenBy(v => v).ToList();
        var pairs = new List<(double D2, int A, int B)>();
        for (int i = 0; i < ends.Count; i++)
        for (int j = i + 1; j < ends.Count && points[ends[j]].X - points[ends[i]].X <= tol; j++)
        {
            double dx = points[ends[i]].X - points[ends[j]].X, dy = points[ends[i]].Y - points[ends[j]].Y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= (double)tol * tol) pairs.Add((d2, ends[i], ends[j]));
        }
        pairs.Sort();

        int joined = 0;
        double largest = 0;
        var used = new HashSet<int>();
        foreach (var (d2, a, b) in pairs)
        {
            if (used.Contains(a) || used.Contains(b)) continue;
            // The two ends of one short segment are not a gap: joining them would delete it.
            if (edges.Neighbours(a).Contains(b)) continue;
            used.Add(a); used.Add(b);
            edges.MergeInto(b, a);
            joined++;
            largest = Math.Max(largest, d2);
        }
        return (joined, (long)Math.Round(Math.Sqrt(largest)));
    }

    // ── 4. shared edges ──────────────────────────────────────────────────────────────────────────

    /// <summary>Removes each run of multiply-drawn edges that joins two odd points. Kept once, such a run
    /// leaves both of them unable to close; gone, both are even and the loops either side are one.</summary>
    private static int RemoveSharedEdges(Graph edges)
    {
        int removed = 0;
        foreach (int start in edges.Vertices.ToList())
        {
            if (edges.Degree(start) % 2 == 0 || edges.Degree(start) < 3) continue;

            var from = new Dictionary<int, int> { [start] = start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            int goal = -1;
            while (queue.Count > 0 && goal < 0)
            {
                int v = queue.Dequeue();
                foreach (int w in edges.Neighbours(v))
                {
                    if (from.ContainsKey(w) || edges.Multiplicity(v, w) < 2) continue;
                    from[w] = v;
                    if (edges.Degree(w) % 2 == 1 && edges.Degree(w) >= 3) { goal = w; break; }
                    queue.Enqueue(w);
                }
            }
            if (goal < 0) continue;

            for (int v = goal; v != start; v = from[v]) { edges.Remove(v, from[v]); removed++; }
        }
        return removed;
    }

    // ── 5. what cannot close ─────────────────────────────────────────────────────────────────────

    private static void PeelStubs(Graph edges, Graph open)
    {
        var queue = new Queue<int>(edges.Vertices.Where(v => edges.Degree(v) == 1));
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            if (edges.Degree(v) != 1) continue;
            int w = edges.Neighbours(v).First();
            edges.Remove(v, w);
            open.Add(v, w);
            if (edges.Degree(w) == 1) queue.Enqueue(w);
        }
    }

    private static List<int>? ShortestPath(Graph edges, int start, HashSet<int> goals)
    {
        var from = new Dictionary<int, int> { [start] = start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int w in edges.Neighbours(v))
            {
                if (!from.TryAdd(w, v)) continue;
                if (goals.Contains(w))
                {
                    var path = new List<int> { w };
                    while (path[^1] != start) path.Add(from[path[^1]]);
                    return path;
                }
                queue.Enqueue(w);
            }
        }
        return null;
    }

    private static List<DxfOpenOutline> OpenChains(Graph open, HashSet<int> dangling, List<(long X, long Y)> points)
    {
        var result = new List<DxfOpenOutline>();
        void Emit(List<int> chain)
        {
            var xy = new long[chain.Count * 2];
            for (int i = 0; i < chain.Count; i++) { xy[2 * i] = points[chain[i]].X; xy[2 * i + 1] = points[chain[i]].Y; }
            var ends = new[] { chain[0], chain[^1] }.Distinct().Where(dangling.Contains).Select(v => points[v]).ToList();
            result.Add(new DxfOpenOutline(xy, ends));
        }

        // A chain starts wherever the outline branches or ends, and runs through points with two edges.
        foreach (int start in open.Vertices.Where(v => open.Degree(v) != 2).OrderBy(v => v).ToList())
            while (open.Degree(start) > 0)
            {
                var chain = new List<int> { start };
                int at = start;
                do
                {
                    int next = open.Neighbours(at).OrderBy(v => v).First();
                    open.Remove(at, next);
                    chain.Add(next);
                    at = next;
                } while (open.Degree(at) == 1);
                Emit(chain);
            }

        // Only points with two edges left: a closed walk taken off by the odd-point step.
        foreach (int start in open.Vertices.OrderBy(v => v).ToList())
            while (open.Degree(start) > 0)
            {
                var chain = new List<int> { start };
                int at = start;
                do
                {
                    int next = open.Neighbours(at).OrderBy(v => v).First();
                    open.Remove(at, next);
                    chain.Add(next);
                    at = next;
                } while (open.Degree(at) > 0);
                Emit(chain);
            }

        return result;
    }

    // ── 6. closed walks ──────────────────────────────────────────────────────────────────────────

    private static List<long[]> ClosedWalks(Graph edges, List<(long X, long Y)> points)
    {
        var walks = new List<long[]>();
        foreach (int start in edges.Vertices.OrderBy(v => v).ToList())
            while (edges.Degree(start) > 0)
            {
                var xy = new List<long>();
                int at = start;
                do
                {
                    xy.Add(points[at].X); xy.Add(points[at].Y);
                    int next = edges.Neighbours(at).OrderBy(v => v).First();
                    edges.Remove(at, next);
                    at = next;
                } while (at != start && edges.Degree(at) > 0);
                walks.Add([.. xy]);
            }
        return walks;
    }

    // ── the graph ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Undirected, with each edge's drawn count. Degree counts an edge once, however often drawn.</summary>
    private sealed class Graph
    {
        private readonly Dictionary<int, Dictionary<int, int>> _adjacent = [];

        public IEnumerable<int> Vertices => _adjacent.Keys;
        public int Degree(int v) => _adjacent.TryGetValue(v, out var n) ? n.Count : 0;
        public IEnumerable<int> Neighbours(int v) => _adjacent.TryGetValue(v, out var n) ? n.Keys : [];
        public int Multiplicity(int a, int b) => _adjacent.TryGetValue(a, out var n) ? n.GetValueOrDefault(b) : 0;

        public IEnumerable<(int A, int B)> AllEdges =>
            _adjacent.SelectMany(kv => kv.Value.Keys.Where(w => kv.Key < w).Select(w => (kv.Key, w)));

        public void Add(int a, int b)
        {
            if (a == b) return;
            Side(a)[b] = Side(a).GetValueOrDefault(b) + 1;
            Side(b)[a] = Side(b).GetValueOrDefault(a) + 1;
        }

        public void Remove(int a, int b)
        {
            _adjacent[a].Remove(b);
            _adjacent[b].Remove(a);
        }

        /// <summary>Moves every edge of <paramref name="from"/> onto <paramref name="to"/>.</summary>
        public void MergeInto(int from, int to)
        {
            foreach (var (w, count) in _adjacent[from].ToList())
            {
                _adjacent[w].Remove(from);
                for (int i = 0; i < count; i++) Add(to, w);
            }
            _adjacent.Remove(from);
        }

        private Dictionary<int, int> Side(int v)
        {
            if (!_adjacent.TryGetValue(v, out var n)) _adjacent[v] = n = [];
            return n;
        }
    }
}
