// Straight runs of a skeleton — brief-img-1-raster-core.md R-im1-8.
//
// Each edge's polyline is cut where it turns by more than the angle tolerance; each straight run is refitted (total least
// squares) to the skeleton pixels it stands for, and a run ending on a node reaches the node's centre. Runs meeting at a
// node from opposite sides and on one line are merged — a wire with a T-junction part-way along is one wire, not two.

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

/// <summary>One straight run.</summary>
/// <param name="Width">The mean stroke width of the edges it came from, in pixels.</param>
/// <param name="Residual">The RMS distance of its skeleton pixels from it.</param>
/// <param name="Edges">The skeleton edges it was read from.</param>
public sealed record ImageSegment(PointD A, PointD B, double Width, double Residual, IReadOnlyList<int> Edges)
{
    public double Length => Math.Sqrt((B.x - A.x) * (B.x - A.x) + (B.y - A.y) * (B.y - A.y));
}

public sealed record SegmentOptions
{
    /// <summary>Two pieces turning by less than this many degrees are one run.</summary>
    public double AngleDeg { get; init; } = 3.0;

    /// <summary>Runs shorter than this many stroke widths are not reported (null: 2).</summary>
    public double MinLengthStrokes { get; init; } = 2.0;
}

public static class Segments
{
    private sealed class Run
    {
        public required List<PointD> Pts;
        public int NodeA = -1, NodeB = -1;     // the node at each end, when it ends on one
        public double Width;
        public required List<int> Edges;
    }

    public static List<ImageSegment> FromSkeleton(SkeletonGraph graph, SegmentOptions? options = null)
    {
        options ??= new SegmentOptions();
        double tol = options.AngleDeg * Math.PI / 180;
        double minLen = Math.Max(2.0, options.MinLengthStrokes * Math.Max(1.0, graph.StrokeWidth));
        var runs = new List<Run>();
        foreach (var e in graph.Edges)
        {
            var poly = e.Polyline;
            var idx = e.PolylineIndices;
            int start = 0;
            for (int v = 1; v < poly.Count; v++)
            {
                bool last = v == poly.Count - 1;
                if (!last && Turn(poly[v - 1], poly[v], poly[v + 1]) <= tol) continue;
                // The run is polyline vertices start … v.
                var pts = new List<PointD>();
                for (int k = idx[start]; k <= idx[v]; k++) pts.Add(new PointD(e.Pixels[k].X + 0.5, e.Pixels[k].Y + 0.5));
                runs.Add(new Run
                {
                    Pts = pts,
                    NodeA = start == 0 ? e.From : -1,
                    NodeB = last ? e.To : -1,
                    Width = e.MeanWidth,
                    Edges = [e.Id],
                });
                start = v;
            }
        }

        // Merge across nodes: pairs of runs ending on one node, on one line, greedily by angle.
        var parent = Enumerable.Range(0, runs.Count).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        var fits = runs.Select(r => Fit.Segment(r.Pts)).ToList();
        foreach (var node in graph.Nodes)
        {
            var ends = new List<int>();
            for (int i = 0; i < runs.Count; i++)
                if (runs[i].NodeA == node.Id || runs[i].NodeB == node.Id) ends.Add(i);
            if (ends.Count < 2) continue;
            var pairs = new List<(double D, int A, int B)>();
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                {
                    var fa = fits[ends[i]];
                    var fb = fits[ends[j]];
                    double d = Math.Abs(fa.Angle - fb.Angle);
                    d = Math.Min(d, Math.PI - d);
                    if (d > tol) continue;
                    double lim = Math.Max(1.0, graph.StrokeWidth / 2);
                    if (Offset(fa, fb.A) > lim || Offset(fa, fb.B) > lim) continue;
                    pairs.Add((d, ends[i], ends[j]));
                }
            var used = new HashSet<int>();
            foreach (var (_, a, b) in pairs.OrderBy(p => p.D).ThenBy(p => p.A).ThenBy(p => p.B))
            {
                if (used.Contains(a) || used.Contains(b)) continue;
                used.Add(a);
                used.Add(b);
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        var groups = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < runs.Count; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = [];
            g.Add(i);
        }
        var result = new List<ImageSegment>();
        foreach (var members in groups.Values)
        {
            var pts = new List<PointD>();
            var edges = new SortedSet<int>();
            double wsum = 0, wlen = 0;
            var endNodes = new List<int>();
            foreach (int i in members)
            {
                pts.AddRange(runs[i].Pts);
                foreach (int ed in runs[i].Edges) edges.Add(ed);
                wsum += runs[i].Width * runs[i].Pts.Count;
                wlen += runs[i].Pts.Count;
            }
            var fit = Fit.Segment(pts);
            // An end on a node reaches the node's centre, projected onto the line.
            var a = fit.A;
            var b = fit.B;
            foreach (int i in members)
                foreach (int nd in new[] { runs[i].NodeA, runs[i].NodeB })
                {
                    if (nd < 0) continue;
                    var c = Project(fit, new PointD(graph.Nodes[nd].X, graph.Nodes[nd].Y));
                    if (Dist(c, a) < Dist(c, b)) { if (Along(fit, c) < Along(fit, a)) a = c; }
                    else if (Along(fit, c) > Along(fit, b)) b = c;
                }
            var seg = new ImageSegment(a, b, wlen > 0 ? wsum / wlen : graph.StrokeWidth, fit.Residual, [.. edges]);
            if (seg.Length >= minLen) result.Add(seg);
        }
        return result;
    }

    private static double Turn(PointD a, PointD b, PointD c)
    {
        double a1 = Math.Atan2(b.y - a.y, b.x - a.x), a2 = Math.Atan2(c.y - b.y, c.x - b.x);
        double d = Math.Abs(a2 - a1);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    private static double Offset(SegmentFit f, PointD p)
    {
        double ux = f.B.x - f.A.x, uy = f.B.y - f.A.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return Dist(p, f.A);
        return Math.Abs((p.x - f.Cx) * -uy / l + (p.y - f.Cy) * ux / l);
    }

    private static double Along(SegmentFit f, PointD p)
    {
        double ux = f.B.x - f.A.x, uy = f.B.y - f.A.y, l = Math.Sqrt(ux * ux + uy * uy);
        return l < 1e-12 ? 0 : ((p.x - f.Cx) * ux + (p.y - f.Cy) * uy) / l;
    }

    private static PointD Project(SegmentFit f, PointD p)
    {
        double ux = f.B.x - f.A.x, uy = f.B.y - f.A.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return f.A;
        ux /= l; uy /= l;
        double t = (p.x - f.Cx) * ux + (p.y - f.Cy) * uy;
        return new PointD(f.Cx + t * ux, f.Cy + t * uy);
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
}
