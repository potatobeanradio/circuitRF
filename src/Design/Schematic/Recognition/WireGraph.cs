// The wires of a schematic picture as a graph — brief-img-7-schematic-wires-and-regions.md R-im7-4, R-im7-5 (D11).
//
//   skeleton (IM-1) ─► straight runs ─► wire candidates ─► symbol strokes set aside ─► nodes, crossings, dots, nets
//
// A run is a WIRE CANDIDATE when it is straight (RMS residual under MaxResidual w) and either orthogonal within the
// snap angle and longer than MinWireLength w, or diagonal and longer than MinDiagonalLength w.
//
// Two shapes of candidate are strokes of symbols, not wires, and are set aside before the graph is made — without
// them a capacitor's plates, a ground's bars and a resistor's box would all come back as wire:
//  * a straight line no longer than MaxSymbolStroke w whose BOTH ends stop in nothing (the skeleton ends there): a
//    plate, a ground or supply bar. A wire ends on a symbol, a junction or — at one end — a net label; one with two
//    bare ends that short is a stroke;
//  * every candidate on a CYCLE of candidates (a box, a triangle): a loop made only of wire is two wires shorting the
//    same two nodes, which nobody draws, while a symbol's outline is exactly that. At a four-way crossing the cycle is
//    looked for with the crossing's two straight pairs kept apart, so two wires crossing twice are not a loop.
//
// Nodes are the candidates' ends clustered within 1.5 w (or within a junction dot), with an end that lands on another
// wire's interior splitting it (a T). A node with two collinear wires is dissolved — one wire. A degree-3 node always
// connects; a degree-4 node connects per the CrossingRule; a hop (found among the symbol regions, SymbolRegions.cs)
// joins its two halves across the other wire and never connects to it.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

public enum WireOrientation { Horizontal, Vertical, Diagonal }

/// <summary>A node of the wire graph, in picture pixels.</summary>
/// <param name="Degree">How many wire segments end on it.</param>
/// <param name="Dot">A junction dot sits on it (degree 3 or more only).</param>
/// <param name="Free">Every wire end on it stops in bare paper — the skeleton ends there.</param>
public sealed record WireNode(int Id, double X, double Y, int Degree, bool Dot, bool Free)
{
    public bool IsEnd => Degree == 1;
}

/// <summary>One straight wire segment, from node <paramref name="From"/> at <paramref name="A"/> to node
/// <paramref name="To"/> at <paramref name="B"/>.</summary>
/// <param name="Width">Its mean stroke width, pixels.</param>
/// <param name="Residual">The RMS distance of its skeleton pixels from it, pixels.</param>
public sealed record WireSegment(int Id, int From, int To, PointD A, PointD B, WireOrientation Orientation,
    double Width, double Residual)
{
    public double Length => Math.Sqrt((B.x - A.x) * (B.x - A.x) + (B.y - A.y) * (B.y - A.y));
}

public enum CrossingReading
{
    /// <summary>The four wires are one net.</summary>
    Connected,

    /// <summary>Each straight pair is its own net.</summary>
    NotConnected,

    /// <summary>One wire hops over the other: never connected, whatever the crossing rule.</summary>
    Hop,
}

/// <summary>A four-way crossing or a hop, and how it was read — what IM-10's overlay draws and lets the user flip.</summary>
/// <param name="Node">The degree-4 node; null for a hop, which has none.</param>
/// <param name="Segments">The wires crossing there.</param>
public sealed record WireCrossing(int Id, double X, double Y, int? Node, bool Dot, CrossingReading Reading,
    IReadOnlyList<int> Segments)
{
    public bool Connected => Reading == CrossingReading.Connected;
}

/// <summary>The wires read off a schematic picture (R-im7-4, R-im7-5).</summary>
public sealed class WireGraph
{
    private readonly int[] _net;

    private WireGraph(List<WireNode> nodes, List<WireSegment> segments, List<WireCrossing> crossings, int[] net, int nets)
    {
        Nodes = nodes;
        Segments = segments;
        Crossings = crossings;
        _net = net;
        NetCount = nets;
    }

    public static WireGraph Empty { get; } = new([], [], [], [], 0);

    /// <summary>Top to bottom, then left to right.</summary>
    public IReadOnlyList<WireNode> Nodes { get; }

    public IReadOnlyList<WireSegment> Segments { get; }

    /// <summary>Every degree-4 node and every hop, with its reading.</summary>
    public IReadOnlyList<WireCrossing> Crossings { get; }

    /// <summary>How many separate nets the wires make.</summary>
    public int NetCount { get; }

    /// <summary>The net of segment <paramref name="segment"/>, 0 … <see cref="NetCount"/> − 1.</summary>
    public int Net(int segment) => _net[segment];

    public bool Connected(int a, int b) => _net[a] == _net[b];

    /// <summary>The nodes one wire ends on.</summary>
    public IEnumerable<WireNode> Ends => Nodes.Where(n => n.IsEnd);

    /// <summary>The segment ending on end node <paramref name="node"/>.</summary>
    public WireSegment SegmentAt(int node) => Segments.First(s => s.From == node || s.To == node);

    /// <summary>The unit direction a wire arrives at end node <paramref name="node"/> in — from its other end towards it.</summary>
    public (double X, double Y) Incoming(int node)
    {
        var s = SegmentAt(node);
        var (p, q) = s.To == node ? (s.A, s.B) : (s.B, s.A);
        double dx = q.x - p.x, dy = q.y - p.y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (0, 0) : (dx / l, dy / l);
    }

    // ── Runs and candidates ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every straight run of the skeleton, classified (R-im7-4).</summary>
    internal static List<WirePiece> Pieces(SkeletonGraph g, double w, SchematicImageOptions o)
    {
        double turn = Math.Max(o.SnapAngleDeg, 8.0) * Math.PI / 180;
        double snap = o.SnapAngleDeg * Math.PI / 180;
        // Never below 0.6 px: one pixel step in a run is an RMS of 0.5 px, and a wire a fraction of a degree off the
        // axis has one.
        double maxResidual = Math.Max(o.MaxResidual * w, 0.6);
        var r = new List<WirePiece>();
        foreach (var e in g.Edges)
        {
            // Cut where the chain leaves a straight line by more than a pixel — not at the skeleton's own 0.5 px
            // polyline, which keeps every step of a staircase: a wire leaning a fraction of a degree would come back in
            // level pieces joined by diagonal steps.
            var chain = e.Pixels.Select(q => new PointD(q.X + 0.5, q.Y + 0.5)).ToList();
            if (chain.Count < 2) continue;
            var idx = Polylines.SimplifyOpen(chain, 1.0);
            var poly = idx.Select(k => chain[k]).ToList();
            int start = 0;
            for (int v = 1; v < poly.Count; v++)
            {
                bool last = v == poly.Count - 1;
                if (!last && Turn(poly[v - 1], poly[v], poly[v + 1]) <= turn) continue;
                var pts = new List<PointD>();
                for (int k = idx[start]; k <= idx[v]; k++) pts.Add(new PointD(e.Pixels[k].X + 0.5, e.Pixels[k].Y + 0.5));
                int nodeA = start == 0 ? e.From : -1, nodeB = last ? e.To : -1;
                start = v;
                if (pts.Count < 2) continue;

                var fit = Fit.Segment(pts);
                PointD a = fit.A, b = fit.B;
                if (Dist(a, pts[0]) > Dist(b, pts[0])) (a, b) = (b, a);
                if (nodeA >= 0) a = Project(fit, new PointD(g.Nodes[nodeA].X, g.Nodes[nodeA].Y));
                if (nodeB >= 0) b = Project(fit, new PointD(g.Nodes[nodeB].X, g.Nodes[nodeB].Y));
                bool loop = e.From == e.To;
                var p = new WirePiece
                {
                    A = a, B = b, Width = e.MeanWidth, Residual = fit.Residual, Angle = fit.Angle,
                    FreeA = !loop && nodeA >= 0 && g.Nodes[nodeA].Degree == 1,
                    FreeB = !loop && nodeB >= 0 && g.Nodes[nodeB].Degree == 1,
                };
                double ang = fit.Angle;
                double off0 = Math.Min(ang, Math.PI - ang), off90 = Math.Abs(ang - Math.PI / 2);
                p.Orientation = off0 <= snap ? WireOrientation.Horizontal
                              : off90 <= snap ? WireOrientation.Vertical : WireOrientation.Diagonal;
                if (p.Orientation == WireOrientation.Horizontal) { p.A = new PointD(p.A.x, fit.Cy); p.B = new PointD(p.B.x, fit.Cy); }
                if (p.Orientation == WireOrientation.Vertical) { p.A = new PointD(fit.Cx, p.A.y); p.B = new PointD(fit.Cx, p.B.y); }
                p.IsWire = fit.Residual <= maxResidual &&
                           (p.Orientation == WireOrientation.Diagonal ? p.Length > o.MinDiagonalLength * w : p.Length >= o.MinWireLength * w);
                p.Sources.Add(r.Count);
                r.Add(p);
            }
        }
        return r;
    }

    /// <summary>The indices of the wire candidates that are strokes of symbols — short lines with two bare ends, and
    /// every candidate on a cycle of candidates.</summary>
    internal static HashSet<int> SymbolStrokes(IReadOnlyList<WirePiece> candidates, IReadOnlyList<JunctionBlob> blobs,
                                               double w, SchematicImageOptions o)
    {
        var a = Assemble(candidates, [], blobs, w, o, dissolve: false);
        var reject = new HashSet<int>();
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);

        // Lines: segments paired straight through a node.
        int ne = a.Edges.Count;
        var parent = Enumerable.Range(0, ne).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var paired = new HashSet<(int Edge, int Node)>();
        var pairsAt = new List<(int A, int B)>[a.Nodes.Count];
        for (int n = 0; n < a.Nodes.Count; n++)
        {
            pairsAt[n] = [];
            var at = a.EdgesAt(n);
            var cand = new List<(double Cos, int A, int B)>();
            for (int i = 0; i < at.Count; i++)
                for (int j = i + 1; j < at.Count; j++)
                {
                    if (at[i] == at[j] || !a.Straight(at[i], at[j], n, cosSnap)) continue;
                    var (ax, ay) = a.Away(at[i], n);
                    var (bx, by) = a.Away(at[j], n);
                    cand.Add((ax * bx + ay * by, at[i], at[j]));
                }
            var used = new HashSet<int>();
            foreach (var (_, ea, eb) in cand.OrderBy(t => t.Cos).ThenBy(t => t.A).ThenBy(t => t.B))
            {
                if (used.Contains(ea) || used.Contains(eb)) continue;
                used.Add(ea); used.Add(eb);
                pairsAt[n].Add((ea, eb));
                paired.Add((ea, n)); paired.Add((eb, n));
                parent[Find(ea)] = Find(eb);
            }
        }
        foreach (var line in Enumerable.Range(0, ne).GroupBy(Find))
        {
            var ends = new List<int>();
            foreach (int e in line)
            {
                var (f, t) = (a.Edges[e].From, a.Edges[e].To);
                if (!paired.Contains((e, f))) ends.Add(f);
                if (!paired.Contains((e, t))) ends.Add(t);
            }
            if (ends.Count != 2) continue;
            var (n0, n1) = (a.Nodes[ends[0]], a.Nodes[ends[1]]);
            if (a.Degree(ends[0]) != 1 || a.Degree(ends[1]) != 1 || !n0.Free || !n1.Free) continue;
            if (Dist(n0.P, n1.P) > o.MaxSymbolStroke * w) continue;
            foreach (int e in line) foreach (int s in a.Edges[e].Piece.Sources) reject.Add(s);
        }

        // Cycles: every edge that is not a bridge, with a four-way crossing's two straight pairs kept apart.
        var vertexOf = new Dictionary<(int Node, int Edge), int>();
        int nv = a.Nodes.Count;
        for (int n = 0; n < a.Nodes.Count; n++)
            if (a.Degree(n) == 4 && pairsAt[n].Count == 2)
            {
                vertexOf[(n, pairsAt[n][1].A)] = nv;
                vertexOf[(n, pairsAt[n][1].B)] = nv;
                nv++;
            }
        int V(int node, int edge) => vertexOf.TryGetValue((node, edge), out int v) ? v : node;
        var adj = new List<(int To, int Edge)>[nv];
        for (int v = 0; v < nv; v++) adj[v] = [];
        for (int e = 0; e < ne; e++)
        {
            int u = V(a.Edges[e].From, e), v = V(a.Edges[e].To, e);
            if (u == v) { foreach (int s in a.Edges[e].Piece.Sources) reject.Add(s); continue; }
            adj[u].Add((v, e));
            adj[v].Add((u, e));
        }
        var disc = new int[nv];
        var low = new int[nv];
        Array.Fill(disc, -1);
        int time = 0;
        var bridge = new bool[ne];
        void Dfs(int u, int viaEdge)
        {
            disc[u] = low[u] = time++;
            foreach (var (v, e) in adj[u])
            {
                if (e == viaEdge) continue;
                if (disc[v] < 0)
                {
                    Dfs(v, e);
                    low[u] = Math.Min(low[u], low[v]);
                    if (low[v] > disc[u]) bridge[e] = true;
                }
                else low[u] = Math.Min(low[u], disc[v]);
            }
        }
        for (int v = 0; v < nv; v++) if (disc[v] < 0) Dfs(v, -1);
        for (int e = 0; e < ne; e++)
            if (!bridge[e] && a.Edges[e].From != a.Edges[e].To)
                foreach (int s in a.Edges[e].Piece.Sources) reject.Add(s);
        return reject;
    }

    /// <summary>The graph of <paramref name="wires"/>, with each hop's two halves joined across the wire it hops.</summary>
    internal static WireGraph Build(IReadOnlyList<WirePiece> wires, IReadOnlyList<HopJoin> hops,
                                    IReadOnlyList<JunctionBlob> blobs, double w, SchematicImageOptions o)
    {
        var a = Assemble(wires, hops, blobs, w, o, dissolve: true);
        if (a.Edges.Count == 0) return Empty;

        // Reading order for nodes; segments by their first node, then their second.
        var nodeOrder = Enumerable.Range(0, a.Nodes.Count)
            .OrderBy(n => Math.Round(a.Nodes[n].P.y, 6)).ThenBy(n => Math.Round(a.Nodes[n].P.x, 6)).ToList();
        var nodeId = new int[a.Nodes.Count];
        for (int k = 0; k < nodeOrder.Count; k++) nodeId[nodeOrder[k]] = k;
        var nodes = nodeOrder.Select((n, k) =>
        {
            var an = a.Nodes[n];
            int deg = a.Degree(n);
            return new WireNode(k, an.P.x, an.P.y, deg, deg >= 3 && an.Dot, an.Free);
        }).ToList();

        var segOrder = Enumerable.Range(0, a.Edges.Count)
            .OrderBy(e => Math.Min(nodeId[a.Edges[e].From], nodeId[a.Edges[e].To]))
            .ThenBy(e => Math.Max(nodeId[a.Edges[e].From], nodeId[a.Edges[e].To]))
            .ThenBy(e => e).ToList();
        var segId = new int[a.Edges.Count];
        var segments = new List<WireSegment>(segOrder.Count);
        foreach (int e in segOrder)
        {
            var ed = a.Edges[e];
            int f = nodeId[ed.From], t = nodeId[ed.To];
            var (pa, pb) = (a.Nodes[ed.From].P, a.Nodes[ed.To].P);
            if (f > t) { (f, t) = (t, f); (pa, pb) = (pb, pa); }
            segId[e] = segments.Count;
            segments.Add(new WireSegment(segments.Count, f, t, pa, pb, ed.Piece.Orientation, ed.Piece.Width, ed.Piece.Residual));
        }

        // Crossings and nets.
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);
        var crossings = new List<WireCrossing>();
        foreach (int n in nodeOrder)
        {
            var at = a.EdgesAt(n);
            if (at.Count == 4)
            {
                var pairs = new List<(int A, int B)>();
                var left = at.ToList();
                while (left.Count > 0)
                {
                    int x = left[0];
                    var (ax, ay) = a.Away(x, n);
                    int best = -1;
                    double bc = -cosSnap;
                    foreach (int y in left.Skip(1))
                    {
                        if (!a.Straight(x, y, n, cosSnap)) continue;
                        var (bx, by) = a.Away(y, n);
                        double c = ax * bx + ay * by;
                        if (best < 0 || c < bc) { bc = c; best = y; }
                    }
                    if (best < 0) break;
                    pairs.Add((x, best));
                    left.Remove(x);
                    left.Remove(best);
                }
                bool straight = pairs.Count == 2;
                bool dot = a.Nodes[n].Dot;
                var reading = !straight ? CrossingReading.Connected : o.Crossings switch
                {
                    CrossingRule.Never => CrossingReading.NotConnected,
                    CrossingRule.Always => CrossingReading.Connected,
                    _ => dot ? CrossingReading.Connected : CrossingReading.NotConnected,
                };
                crossings.Add(new WireCrossing(0, a.Nodes[n].P.x, a.Nodes[n].P.y, nodeId[n], dot, reading,
                    [.. at.Select(e => segId[e]).OrderBy(s => s)]));
                if (reading == CrossingReading.NotConnected)
                {
                    foreach (var (x, y) in pairs) Union(segId[x], segId[y]);
                    continue;
                }
            }
            for (int k = 1; k < at.Count; k++) Union(segId[at[0]], segId[at[k]]);
        }
        foreach (var hop in a.Hops)
        {
            var through = segments.Where(s => DistToSegment(hop.At, s.A, s.B) <= Math.Max(w, 1.5)).Select(s => s.Id).ToList();
            crossings.Add(new WireCrossing(0, hop.At.x, hop.At.y, null, false, CrossingReading.Hop, through));
        }
        crossings = crossings.Select((c, i) => c with { Id = i }).ToList();

        var net = new int[segments.Count];
        var netOf = new Dictionary<int, int>();
        for (int s = 0; s < segments.Count; s++)
        {
            int r = Find(s);
            if (!netOf.TryGetValue(r, out int id)) netOf[r] = id = netOf.Count;
            net[s] = id;
        }
        return new WireGraph(nodes, segments, crossings, net, netOf.Count);
    }

    /// <summary>The filled blobs on the wires no bigger than a junction dot: where the stroke is at least
    /// 0.9·MinDot w across and at most 1.15·MaxDot w — the slack is the distance transform's own quantisation, which
    /// reads a small disc a little narrow.</summary>
    internal static List<JunctionBlob> Blobs(BinaryImage mask, float[] dt, double w, SchematicImageOptions o)
    {
        double thr = (0.9 * o.MinDot * w + 1) / 2;
        var core = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < dt.Length; i++) core.Pixels[i] = dt[i] >= thr ? (byte)1 : (byte)0;
        var lab = Components.Label(core);
        int n = lab.Components.Count;
        var max = new double[n + 1];
        var sx = new double[n + 1];
        var sy = new double[n + 1];
        var sw = new double[n + 1];
        for (int i = 0; i < dt.Length; i++)
        {
            int c = lab.Labels[i];
            if (c == 0) continue;
            double d = dt[i];
            max[c] = Math.Max(max[c], d);
            sx[c] += d * (i % mask.Width + 0.5);
            sy[c] += d * (i / mask.Width + 0.5);
            sw[c] += d;
        }
        var r = new List<JunctionBlob>();
        for (int c = 1; c <= n; c++)
        {
            if (2 * max[c] - 1 > 1.15 * o.MaxDot * w) continue;     // a filled area, not a dot
            r.Add(new JunctionBlob(new PointD(sx[c] / sw[c], sy[c] / sw[c]), max[c] - 0.5));
        }
        return r;
    }

    // ── Assembly: ends clustered into nodes ─────────────────────────────────────────────────────────────────────

    private sealed class ANode
    {
        public PointD P;
        public bool Free = true;
        public bool Dot;
    }

    private sealed record AEdge(int From, int To, WirePiece Piece);

    private sealed class Assembled
    {
        public readonly List<ANode> Nodes = [];
        public readonly List<AEdge> Edges = [];
        public readonly List<HopJoin> Hops = [];

        public List<int> EdgesAt(int n)
        {
            var r = new List<int>();
            for (int e = 0; e < Edges.Count; e++)
            {
                if (Edges[e].From == n) r.Add(e);
                if (Edges[e].To == n) r.Add(e);
            }
            return r;
        }

        public int Degree(int n) => EdgesAt(n).Count;

        /// <summary>Edges <paramref name="e1"/> and <paramref name="e2"/> leave node <paramref name="n"/> in opposite
        /// directions on one line. Two wires both read horizontal (or both vertical) are on one line whatever their
        /// ends' rounding says — a 7-pixel stub a pixel off is 8° off by angle alone.</summary>
        public bool Straight(int e1, int e2, int n, double cosSnap)
        {
            var (ax, ay) = Away(e1, n);
            var (bx, by) = Away(e2, n);
            double c = ax * bx + ay * by;
            var (o1, o2) = (Edges[e1].Piece.Orientation, Edges[e2].Piece.Orientation);
            if (o1 == o2 && o1 != WireOrientation.Diagonal) return c < 0;
            return c <= -cosSnap;
        }

        /// <summary>The unit direction of edge <paramref name="e"/> leaving node <paramref name="n"/>.</summary>
        public (double X, double Y) Away(int e, int n)
        {
            var ed = Edges[e];
            var p = Nodes[n].P;
            var q = Nodes[ed.From == n ? ed.To : ed.From].P;
            double dx = q.x - p.x, dy = q.y - p.y, l = Math.Sqrt(dx * dx + dy * dy);
            return l < 1e-9 ? (0, 0) : (dx / l, dy / l);
        }
    }

    private static Assembled Assemble(IReadOnlyList<WirePiece> input, IReadOnlyList<HopJoin> hops,
                                      IReadOnlyList<JunctionBlob> blobs, double w, SchematicImageOptions o, bool dissolve)
    {
        var segs = input.Select(p => p.Copy()).ToList();
        var result = new Assembled();

        // Hops: the two halves are one wire across the wire hopped.
        foreach (var hop in hops)
        {
            var e1 = NearestEnd(segs, hop.End1, 2 * w + 1);
            var e2 = NearestEnd(segs, hop.End2, 2 * w + 1);
            if (e1 is not { } x || e2 is not { } y || x.Seg == y.Seg) continue;
            var (s1, s2) = (segs[x.Seg], segs[y.Seg]);
            var m = new WirePiece
            {
                A = x.End == 0 ? s1.B : s1.A, FreeA = x.End == 0 ? s1.FreeB : s1.FreeA,
                B = y.End == 0 ? s2.B : s2.A, FreeB = y.End == 0 ? s2.FreeB : s2.FreeA,
                Width = (s1.Width + s2.Width) / 2, Residual = Math.Max(s1.Residual, s2.Residual),
                Orientation = s1.Orientation, IsWire = true,
            };
            m.Sources.AddRange(s1.Sources.Concat(s2.Sources).Distinct().OrderBy(s => s));
            segs.RemoveAt(Math.Max(x.Seg, y.Seg));
            segs.RemoveAt(Math.Min(x.Seg, y.Seg));
            segs.Add(m);
            result.Hops.Add(hop);
        }

        // A T: an end on another wire's interior splits that wire.
        double tol = 1.5 * w;
        var split = new List<WirePiece>();
        for (int j = 0; j < segs.Count; j++)
        {
            var s = segs[j];
            double len = s.Length;
            var at = new List<double>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (i == j) continue;
                foreach (var p in new[] { segs[i].A, segs[i].B })
                {
                    var (along, perp) = Local(s.A, s.B, p);
                    if (Math.Abs(perp) <= Math.Max(w, 1.5) && along >= tol && along <= len - tol) at.Add(along);
                }
            }
            if (at.Count == 0) { split.Add(s); continue; }
            at.Sort();
            double ux = (s.B.x - s.A.x) / len, uy = (s.B.y - s.A.y) / len;
            var prev = s.A;
            bool prevFree = s.FreeA;
            foreach (double t in at.Distinct())
            {
                var q = new PointD(s.A.x + t * ux, s.A.y + t * uy);
                var piece = s.Copy();
                piece.A = prev; piece.FreeA = prevFree; piece.B = q; piece.FreeB = false;
                split.Add(piece);
                prev = q;
                prevFree = false;
            }
            var lastPiece = s.Copy();
            lastPiece.A = prev; lastPiece.FreeA = prevFree;
            split.Add(lastPiece);
        }
        segs = split;

        // Ends clustered: within 1.5 w, or within one junction dot.
        var ends = new List<(int Seg, int End, PointD P)>();
        for (int i = 0; i < segs.Count; i++)
        {
            ends.Add((i, 0, segs[i].A));
            ends.Add((i, 1, segs[i].B));
        }
        var parent = Enumerable.Range(0, ends.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        var byX = Enumerable.Range(0, ends.Count).OrderBy(k => ends[k].P.x).ThenBy(k => k).ToArray();
        for (int i = 0; i < byX.Length; i++)
            for (int j = i + 1; j < byX.Length && ends[byX[j]].P.x - ends[byX[i]].P.x <= tol; j++)
                if (Dist(ends[byX[i]].P, ends[byX[j]].P) <= tol) Union(byX[i], byX[j]);
        foreach (var b in blobs)
        {
            int first = -1;
            for (int k = 0; k < ends.Count; k++)
            {
                if (Dist(ends[k].P, b.Centre) > b.Radius + w) continue;
                if (first < 0) first = k; else Union(first, k);
            }
        }

        var nodeOfRoot = new Dictionary<int, int>();
        var members = new List<List<int>>();
        for (int k = 0; k < ends.Count; k++)
        {
            int r = Find(k);
            if (!nodeOfRoot.TryGetValue(r, out int n))
            {
                nodeOfRoot[r] = n = members.Count;
                members.Add([]);
            }
            members[n].Add(k);
        }
        var from = new int[segs.Count];
        var to = new int[segs.Count];
        foreach (var m in members)
        {
            var node = new ANode { P = Meet(m.Select(k => (segs[ends[k].Seg], ends[k].P)).ToList()) };
            foreach (int k in m)
            {
                var s = segs[ends[k].Seg];
                node.Free &= ends[k].End == 0 ? s.FreeA : s.FreeB;
                if (ends[k].End == 0) from[ends[k].Seg] = result.Nodes.Count; else to[ends[k].Seg] = result.Nodes.Count;
            }
            double reach = Math.Max(o.DotCentre * w, 1.5);
            node.Dot = blobs.Any(b => Dist(b.Centre, node.P) <= reach + b.Radius * 0.5);
            result.Nodes.Add(node);
        }
        for (int i = 0; i < segs.Count; i++)
            if (from[i] != to[i] || segs[i].Length > tol) result.Edges.Add(new AEdge(from[i], to[i], segs[i]));

        if (dissolve) Dissolve(result, o);
        return result;
    }

    /// <summary>Joins the two wires of every node that has exactly two, collinear.</summary>
    private static void Dissolve(Assembled a, SchematicImageOptions o)
    {
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int n = 0; n < a.Nodes.Count; n++)
            {
                var at = a.EdgesAt(n);
                if (at.Count != 2 || at[0] == at[1]) continue;
                if (!a.Straight(at[0], at[1], n, cosSnap)) continue;
                var (e1, e2) = (a.Edges[at[0]], a.Edges[at[1]]);
                int f = e1.From == n ? e1.To : e1.From, t = e2.From == n ? e2.To : e2.From;
                if (f == t) continue;
                var p = e1.Piece.Copy();
                p.Width = (e1.Piece.Width + e2.Piece.Width) / 2;
                p.Residual = Math.Max(e1.Piece.Residual, e2.Piece.Residual);
                if (e1.Piece.Orientation != e2.Piece.Orientation) p.Orientation = WireOrientation.Diagonal;
                p.Sources = [.. e1.Piece.Sources.Concat(e2.Piece.Sources).Distinct().OrderBy(s => s)];
                a.Edges.RemoveAt(Math.Max(at[0], at[1]));
                a.Edges.RemoveAt(Math.Min(at[0], at[1]));
                a.Edges.Add(new AEdge(f, t, p));
                changed = true;
            }
        }
        // Drop the nodes nothing ends on any more, keeping the order of the rest.
        var keep = new int[a.Nodes.Count];
        Array.Fill(keep, -1);
        var nodes = new List<ANode>();
        for (int n = 0; n < a.Nodes.Count; n++)
            if (a.Edges.Any(e => e.From == n || e.To == n)) { keep[n] = nodes.Count; nodes.Add(a.Nodes[n]); }
        var edges = a.Edges.Select(e => e with { From = keep[e.From], To = keep[e.To] }).ToList();
        a.Nodes.Clear(); a.Nodes.AddRange(nodes);
        a.Edges.Clear(); a.Edges.AddRange(edges);
    }

    /// <summary>Where the wires ending at one node meet: the least-squares point of their lines, or — when they are all
    /// parallel — the mean of the ends projected onto them.</summary>
    private static PointD Meet(List<(WirePiece S, PointD End)> ends)
    {
        double mx = ends.Average(e => e.End.x), my = ends.Average(e => e.End.y);
        double sxx = 0, sxy = 0, syy = 0, bx = 0, by = 0;
        foreach (var (s, _) in ends)
        {
            double dx = s.B.x - s.A.x, dy = s.B.y - s.A.y, l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) continue;
            double nx = -dy / l, ny = dx / l, c = nx * (s.A.x - mx) + ny * (s.A.y - my);
            sxx += nx * nx; sxy += nx * ny; syy += ny * ny;
            bx += nx * c; by += ny * c;
        }
        double det = sxx * syy - sxy * sxy, tr = sxx + syy;
        if (tr > 1e-12 && det > 1e-3 * tr * tr)
            return new PointD(mx + (syy * bx - sxy * by) / det, my + (sxx * by - sxy * bx) / det);
        // Parallel: move the mean along the one normal they share.
        if (tr > 1e-12)
        {
            double nx = Math.Sqrt(sxx / tr), ny = Math.Sqrt(syy / tr) * (sxy < 0 ? -1 : 1);
            double c = (nx * bx + ny * by) / tr;
            return new PointD(mx + c * nx, my + c * ny);
        }
        return new PointD(mx, my);
    }

    private static (int Seg, int End)? NearestEnd(List<WirePiece> segs, PointD p, double within)
    {
        (int, int)? best = null;
        double bd = within;
        for (int i = 0; i < segs.Count; i++)
        {
            double da = Dist(segs[i].A, p), db = Dist(segs[i].B, p);
            if (da <= bd) { bd = da; best = (i, 0); }
            if (db < bd) { bd = db; best = (i, 1); }
        }
        return best;
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    internal static double Dist(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    /// <summary><paramref name="p"/> in the frame of segment a→b: distance along from a, and signed distance across.</summary>
    internal static (double Along, double Perp) Local(PointD a, PointD b, PointD p)
    {
        double ux = b.x - a.x, uy = b.y - a.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return (0, Dist(a, p));
        ux /= l; uy /= l;
        double dx = p.x - a.x, dy = p.y - a.y;
        return (dx * ux + dy * uy, -dx * uy + dy * ux);
    }

    internal static double DistToSegment(PointD p, PointD a, PointD b)
    {
        double l = Dist(a, b);
        if (l < 1e-12) return Dist(a, p);
        var (along, perp) = Local(a, b, p);
        if (along < 0) return Dist(a, p);
        if (along > l) return Dist(b, p);
        return Math.Abs(perp);
    }

    private static PointD Project(SegmentFit f, PointD p)
    {
        double ux = f.B.x - f.A.x, uy = f.B.y - f.A.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return f.A;
        ux /= l; uy /= l;
        double t = (p.x - f.Cx) * ux + (p.y - f.Cy) * uy;
        return new PointD(f.Cx + t * ux, f.Cy + t * uy);
    }

    private static double Turn(PointD a, PointD b, PointD c)
    {
        double a1 = Math.Atan2(b.y - a.y, b.x - a.x), a2 = Math.Atan2(c.y - b.y, c.x - b.x);
        double d = Math.Abs(a2 - a1);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }
}

/// <summary>A straight run of the skeleton while the wires are being read.</summary>
internal sealed class WirePiece
{
    public PointD A, B;

    /// <summary>The skeleton ends at that end — the stroke stops in bare paper.</summary>
    public bool FreeA, FreeB;

    public double Width, Residual;

    /// <summary>The fitted direction before any snapping, 0 … π, y down.</summary>
    public double Angle;

    public WireOrientation Orientation;
    public bool IsWire;

    /// <summary>The runs it was made from (indices into the first list of pieces).</summary>
    public List<int> Sources = [];

    public double Length => WireGraph.Dist(A, B);

    public WirePiece Copy()
    {
        var c = (WirePiece)MemberwiseClone();
        c.Sources = [.. Sources];
        return c;
    }
}

/// <summary>A filled blob on the wires — a junction dot when it sits on a node of three wires or more.</summary>
internal sealed record JunctionBlob(PointD Centre, double Radius);

/// <summary>A hop: the wire ends at <paramref name="End1"/> and <paramref name="End2"/> are one wire, crossing the
/// other at <paramref name="At"/> without connecting to it.</summary>
internal sealed record HopJoin(PointD End1, PointD End2, PointD At);
