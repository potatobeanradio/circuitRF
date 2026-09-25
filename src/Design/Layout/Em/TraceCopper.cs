// One conductor layer's unioned copper and the line queries the trace-impedance tools make on it —
// the probe's (TraceImpedanceProbe) and the whole-layout analysis's (TraceImpedanceAnalysis).
//
// Two modes, one class. A probe unions ±15 mm of copper around the click, a few thousand edges, and
// asks about UNBOUNDED lines through it: every query scans every edge, and a chord's ends are found by
// crossing parity. The analysis unions a whole layer (tens of thousands of edges on a real board, most
// of them the arc segments of a pour's antipads) and asks many thousands of questions, so it builds a
// uniform grid over the edges and every query is BOUNDED to a reach along the line: the candidates are
// the edges in the cells the segment passes through, and inside/outside comes from a point-in-copper
// test rather than parity over a line that no longer runs from infinity.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Em;

internal sealed class TraceCopper
{
    public Paths64 Paths { get; }

    /// <summary>Edge i runs from (Ax, Ay) to (Bx, By). Ring order, so edge i and i+1 of one ring
    /// share a vertex.</summary>
    public readonly double[] Ax, Ay, Bx, By;

    /// <summary>The ring each edge belongs to.</summary>
    public readonly int[] RingOf;

    // ── the grid (indexed mode only) ────────────────────────────────────────────────────────────
    private readonly bool _indexed;
    private readonly double _x0, _y0, _cell;
    private readonly int _nx, _ny;
    private readonly int[] _cellStart = [], _cellEdges = [];
    private readonly ThreadLocal<(int[] Stamp, int Tick)> _stamps;

    public bool Indexed => _indexed;

    public TraceCopper(Paths64 paths, bool indexed = false)
    {
        Paths = paths;
        int n = paths.Sum(p => p.Count);
        Ax = new double[n]; Ay = new double[n]; Bx = new double[n]; By = new double[n];
        RingOf = new int[n];
        int k = 0;
        for (int r = 0; r < paths.Count; r++)
        {
            var p = paths[r];
            for (int i = 0; i < p.Count; i++, k++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                Ax[k] = a.X; Ay[k] = a.Y; Bx[k] = b.X; By[k] = b.Y;
                RingOf[k] = r;
            }
        }
        _stamps = new ThreadLocal<(int[], int)>(() => (new int[Ax.Length], 0));

        _indexed = indexed && n > 0;
        if (!_indexed) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            minX = Math.Min(minX, Math.Min(Ax[i], Bx[i])); maxX = Math.Max(maxX, Math.Max(Ax[i], Bx[i]));
            minY = Math.Min(minY, Math.Min(Ay[i], By[i])); maxY = Math.Max(maxY, Math.Max(Ay[i], By[i]));
        }
        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        // About four edges per cell on average, and never more than ~1M cells.
        _cell = Math.Max(Math.Sqrt(w * h / Math.Max(1, n) * 4), Math.Sqrt(w * h / 1e6));
        _nx = Math.Max(1, (int)Math.Ceiling(w / _cell) + 1);
        _ny = Math.Max(1, (int)Math.Ceiling(h / _cell) + 1);
        _x0 = minX; _y0 = minY;

        // Counting sort of (cell, edge) pairs into CSR arrays.
        var counts = new int[_nx * _ny + 1];
        void Cells(int e, Action<int> f)
        {
            int ix0 = CellX(Math.Min(Ax[e], Bx[e])), ix1 = CellX(Math.Max(Ax[e], Bx[e]));
            int iy0 = CellY(Math.Min(Ay[e], By[e])), iy1 = CellY(Math.Max(Ay[e], By[e]));
            for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++)
                    f(iy * _nx + ix);
        }
        for (int e = 0; e < n; e++) Cells(e, c => counts[c + 1]++);
        for (int c = 0; c < _nx * _ny; c++) counts[c + 1] += counts[c];
        _cellStart = (int[])counts.Clone();
        _cellEdges = new int[counts[^1]];
        var fill = (int[])counts.Clone();
        for (int e = 0; e < n; e++) { int edge = e; Cells(e, c => _cellEdges[fill[c]++] = edge); }
    }

    private int CellX(double x) => Math.Clamp((int)Math.Floor((x - _x0) / _cell), 0, _nx - 1);
    private int CellY(double y) => Math.Clamp((int)Math.Floor((y - _y0) / _cell), 0, _ny - 1);

    /// <summary>A fresh de-duplication tick for this thread's stamp array.</summary>
    private int[] NextStamp(out int tick)
    {
        var (stamp, t) = _stamps.Value;
        t++;
        if (t == int.MaxValue) { Array.Clear(stamp); t = 1; }
        _stamps.Value = (stamp, t);
        tick = t;
        return stamp;
    }

    /// <summary>The edges in the cells the segment P + t·u, t ∈ [t0, t1], passes through (and their
    /// neighbours across a boundary the segment grazes), each once.</summary>
    private void SegmentEdges(double px, double py, double ux, double uy, double t0, double t1, List<int> into)
    {
        var stamp = NextStamp(out int tick);
        double ax = px + t0 * ux, ay = py + t0 * uy, bx = px + t1 * ux, by = py + t1 * uy;
        // A walk in half-cell steps visiting each cell and its 4-neighbours is a superset of the
        // exact traversal and costs nothing worth optimising at these segment lengths.
        double len = t1 - t0;
        int steps = Math.Max(1, (int)Math.Ceiling(len / (0.5 * _cell)));
        int lastCell = -1;
        for (int s = 0; s <= steps; s++)
        {
            double f = (double)s / steps;
            int ix = CellX(ax + f * (bx - ax)), iy = CellY(ay + f * (by - ay));
            int c = iy * _nx + ix;
            if (c == lastCell) continue;
            lastCell = c;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx != 0 && dy != 0) continue;
                    int jx = ix + dx, jy = iy + dy;
                    if (jx < 0 || jy < 0 || jx >= _nx || jy >= _ny) continue;
                    int cc = jy * _nx + jx;
                    for (int k = _cellStart[cc]; k < _cellStart[cc + 1]; k++)
                    {
                        int e = _cellEdges[k];
                        if (stamp[e] == tick) continue;
                        stamp[e] = tick;
                        into.Add(e);
                    }
                }
        }
    }

    /// <summary>Whether (px, py) is in copper — crossing parity of the ray to +x.</summary>
    public bool Contains(double px, double py)
    {
        int crossings = 0;
        if (!_indexed)
        {
            for (int i = 0; i < Ax.Length; i++) Count(i);
            return (crossings & 1) == 1;
        }

        var stamp = NextStamp(out int tick);
        int iy = CellY(py);
        if (py < _y0 || py > _y0 + _ny * _cell) return false;
        for (int ix = CellX(px); ix < _nx; ix++)
        {
            int c = iy * _nx + ix;
            for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
            {
                int e = _cellEdges[k];
                if (stamp[e] == tick) continue;
                stamp[e] = tick;
                Count(e);
            }
        }
        return (crossings & 1) == 1;

        void Count(int i)
        {
            if (Ay[i] > py == By[i] > py) return;
            double x = Ax[i] + (py - Ay[i]) / (By[i] - Ay[i]) * (Bx[i] - Ax[i]);
            if (x > px) crossings++;
        }
    }

    /// <summary>Every crossing of the line P + t·u with an edge: (t, edge). Half-open on the side
    /// test, so a line through a vertex is counted once. Indexed: only |t| ≤ <paramref name="reach"/>.</summary>
    private List<(double T, int Edge)> Crossings(double px, double py, double ux, double uy, double reach)
    {
        var hits = new List<(double, int)>();
        void Test(int i)
        {
            double sa = ux * (Ay[i] - py) - uy * (Ax[i] - px);
            double sb = ux * (By[i] - py) - uy * (Bx[i] - px);
            if (sa > 0 == sb > 0) return;
            double l = sa / (sa - sb);
            double hx = Ax[i] + l * (Bx[i] - Ax[i]), hy = Ay[i] + l * (By[i] - Ay[i]);
            hits.Add(((hx - px) * ux + (hy - py) * uy, i));
        }

        if (!_indexed)
            for (int i = 0; i < Ax.Length; i++) Test(i);
        else
        {
            var edges = new List<int>();
            SegmentEdges(px, py, ux, uy, -reach, reach, edges);
            foreach (int e in edges) Test(e);
            hits.RemoveAll(h => Math.Abs(h.Item1) > reach);
        }
        hits.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        return hits;
    }

    /// <summary>The reach of an unbounded query in indexed mode — generous against any trace.</summary>
    public const double DefaultReachDbu = 30_000_000;

    /// <summary>The copper interval of the line through P that contains P, or null where P is not
    /// in copper. Indexed: an end beyond <paramref name="reach"/> is reported AT the reach with edge −1.</summary>
    public (double T0, double T1, int E0, int E1)? ChordAt(double px, double py, double ux, double uy,
                                                          double reach = DefaultReachDbu)
    {
        var hits = Crossings(px, py, ux, uy, reach);
        if (!_indexed)
        {
            int below = hits.FindLastIndex(h => h.T < 0);
            if (below < 0 || below % 2 == 1 || below + 1 >= hits.Count) return null;   // even count before P → outside
            return (hits[below].T, hits[below + 1].T, hits[below].Edge, hits[below + 1].Edge);
        }

        if (!Contains(px, py)) return null;
        int b = hits.FindLastIndex(h => h.T < 0);
        int a = hits.FindIndex(h => h.T >= 0);
        return (b >= 0 ? hits[b].T : -reach, a >= 0 ? hits[a].T : reach,
                b >= 0 ? hits[b].Edge : -1, a >= 0 ? hits[a].Edge : -1);
    }

    /// <summary>Every copper interval of the line, in t. Indexed: clipped to ±<paramref name="reach"/>.</summary>
    public IEnumerable<(double T0, double T1)> Intervals(double px, double py, double ux, double uy,
                                                          double reach = DefaultReachDbu)
    {
        var hits = Crossings(px, py, ux, uy, reach);
        if (!_indexed)
        {
            for (int i = 0; i + 1 < hits.Count; i += 2)
                yield return (hits[i].T, hits[i + 1].T);
            yield break;
        }

        bool inside = Contains(px - reach * ux, py - reach * uy);
        double start = -reach;
        foreach (var (t, _) in hits)
        {
            if (inside) yield return (start, t);
            inside = !inside;
            start = t;
        }
        if (inside) yield return (start, reach);
    }

    /// <summary>The length of [a, b] on the line that is copper.</summary>
    public double Covered(double px, double py, double ux, double uy, double a, double b)
    {
        double reach = _indexed ? Math.Max(Math.Abs(a), Math.Abs(b)) + 1 : DefaultReachDbu;
        double sum = 0;
        foreach (var (t0, t1) in Intervals(px, py, ux, uy, reach))
            sum += Math.Max(0, Math.Min(t1, b) - Math.Max(t0, a));
        return sum;
    }

    /// <summary>The edge nearest P (within <paramref name="reach"/> when indexed), or −1.</summary>
    public int NearestEdge(double px, double py, double reach = DefaultReachDbu)
    {
        int best = -1; double bestD = double.PositiveInfinity;
        void Test(int i)
        {
            double dx = Bx[i] - Ax[i], dy = By[i] - Ay[i];
            double len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(((px - Ax[i]) * dx + (py - Ay[i]) * dy) / len2, 0, 1);
            double ex = px - Ax[i] - t * dx, ey = py - Ay[i] - t * dy;
            double d = ex * ex + ey * ey;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (!_indexed) { for (int i = 0; i < Ax.Length; i++) Test(i); return best; }
        foreach (int e in EdgesNear(px - reach, py - reach, px + reach, py + reach)) Test(e);
        return best;
    }

    /// <summary>Every edge whose cell range meets the box (indexed mode), each once.</summary>
    public List<int> EdgesNear(double x0, double y0, double x1, double y1)
    {
        var list = new List<int>();
        if (!_indexed)
        {
            for (int i = 0; i < Ax.Length; i++)
                if (Math.Max(Ax[i], Bx[i]) >= x0 && Math.Min(Ax[i], Bx[i]) <= x1 &&
                    Math.Max(Ay[i], By[i]) >= y0 && Math.Min(Ay[i], By[i]) <= y1) list.Add(i);
            return list;
        }
        var stamp = NextStamp(out int tick);
        for (int iy = CellY(y0); iy <= CellY(y1); iy++)
            for (int ix = CellX(x0); ix <= CellX(x1); ix++)
            {
                int c = iy * _nx + ix;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int e = _cellEdges[k];
                    if (stamp[e] == tick) continue;
                    stamp[e] = tick;
                    list.Add(e);
                }
            }
        return list;
    }

    public double Length(int e)
    {
        double dx = Bx[e] - Ax[e], dy = By[e] - Ay[e];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>sin of the angle between edge <paramref name="e"/> and the unit direction v.</summary>
    public double SinTo(int e, double vx, double vy)
    {
        if (e < 0) return 0;
        double dx = Bx[e] - Ax[e], dy = By[e] - Ay[e];
        double len = Math.Sqrt(dx * dx + dy * dy);
        return len == 0 ? 0 : (dx * vy - dy * vx) / len;
    }

    public double AngleDeg(int e) =>
        e < 0 ? 0 : (Math.Atan2(By[e] - Ay[e], Bx[e] - Ax[e]) * 180 / Math.PI + 360) % 180;
}
