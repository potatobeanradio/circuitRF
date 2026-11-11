// PMS-2 R-pms2-3 / R-pms2-4 — what a locally refined shared grid, and a stacked-metal region, would make of a mesh,
// estimated OFFLINE: arithmetic over today's gridlines and the requests behind them (PlanarGridDiagnostics), not a
// mesher.
//
// A cell of the local grid is a BLOCK of today's elementary cells, [IX0, IX1) × [IY0, IY1) — brief-pms-4's D-pms4-1 —
// so every feature edge is still a cell edge and adjacency is an integer question. One partition serves every level
// (a via's blocks still pair in z). It starts from blocks at the λ cap and splits a block, per axis, while either:
//   • a hard line inside it is needed there — its feature's own stretch of the line, widened by `Reach` cells; or
//   • the size field asks for a finer pitch than the block has: the λ cap, MinCellsAcrossConductor on the metal in
//     the block (its own run, floored at the detail floor), and the edge fan of any attractor whose edge is within
//     `Reach` cells along it.
// Blocks touching a port's feed polygon are kept elementary (brief-pms-4 D-pms4-3). A 2:1 balance is then enforced
// between blocks that share a metal level: no face meets more than two neighbouring faces. Rooftops are one per
// adjacent block pair on a level — one per fine face segment across a refinement step.
//
// Its calibration: with every block forced elementary, the counts are today's, computed independently of the mesher
// (its own even-odd tests against the polygons). Only then is any other number from it worth reporting.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public sealed record LocalGridCounts(
    int Leaves,
    IReadOnlyList<int> CellsPerLevel,
    IReadOnlyList<int> RooftopsPerLevel,
    int Vertical,
    int RefinementSteps,
    int InhomogeneousBlocks,
    int StackedRegions,
    int DroppedLowerCells,
    int DroppedVertical)
{
    public int Cells => CellsPerLevel.Sum();
    public int Unknowns => RooftopsPerLevel.Sum() + Vertical;
    /// <summary>One dense frequency point at this N, cores estimated (<see cref="PlanarSystem.ResidentBytes(int,int,bool)"/>).</summary>
    public long ResidentBytes => PlanarSystem.ResidentBytes(Unknowns, Cells);
}

public sealed class LocalGridEstimate
{
    /// <summary>How many elementary cells beyond its feature's own stretch a hard line or an edge fan reaches.</summary>
    public int Reach { get; init; } = 2;
    /// <summary>The smallest side, in metres, of a via-joined region the stacked-region estimate merges. A 6 µm post never
    /// qualifies; a region or a MIM Via window does.</summary>
    public double StackedMinSideM { get; init; } = 30e-6;

    private readonly PlanarProblem _p;
    private readonly double[] _gx, _gy;
    private readonly int _nx, _ny, _levels;
    private readonly bool[][] _metal;      // [level][iy*nx+ix]
    private readonly int[][] _metalSum;    // 2-D prefix sums, (nx+1)*(ny+1)
    private readonly bool[][] _cover;      // [via][iy*nx+ix]
    private readonly int[][] _coverSum;
    private readonly bool[] _forced;
    private readonly int[] _forcedSum;
    private readonly double[] _runX, _runY; // narrowest metal run through a cell over all levels (∞ off metal)
    private readonly double _waveCap, _minCells, _detailFloor;
    private readonly List<(int Lo, int Hi)>[] _needX, _needY;   // per interior line, needed elementary index ranges
    private readonly List<(double Coord, double C0, double G, int Lo, int Hi)> _fanX = [], _fanY = [];

    public LocalGridEstimate(PlanarProblem problem, PlanarGridDiagnostics diag, IReadOnlyList<PlanarPort> ports,
                             int minCellsAcross, int reach = 2)
    {
        Reach = reach;
        _p = problem;
        _gx = diag.X.Lines; _gy = diag.Y.Lines;
        _nx = _gx.Length - 1; _ny = _gy.Length - 1;
        _levels = problem.Layers.Count;
        _waveCap = diag.X.WaveCapM;
        _minCells = minCellsAcross;
        _detailFloor = diag.DetailFloorM;

        _metal = new bool[_levels][];
        _metalSum = new int[_levels][];
        for (int li = 0; li < _levels; li++)
        {
            _metal[li] = Mask(problem.Layers[li].Polygons, union: true);
            _metalSum[li] = Prefix(_metal[li]);
        }
        _cover = new bool[problem.ViaList.Count][];
        _coverSum = new int[problem.ViaList.Count][];
        for (int v = 0; v < problem.ViaList.Count; v++)
        {
            _cover[v] = Mask(problem.ViaList[v].Polygons, union: true);
            _coverSum[v] = Prefix(_cover[v]);
        }

        // Port feeds stay elementary: the conductor polygon each port sits on, on its level.
        _forced = new bool[_nx * _ny];
        foreach (var port in ports)
        {
            var levels = port.LayerIndex is { } l ? new[] { l } : Enumerable.Range(0, _levels).ToArray();
            foreach (int li in levels)
                foreach (var poly in problem.Layers[li].Polygons)
                {
                    var (bx0, by0, bx1, by1) = poly.Bounds();
                    double t = 1e-12;
                    if (port.Location.X < bx0 - t || port.Location.X > bx1 + t ||
                        port.Location.Y < by0 - t || port.Location.Y > by1 + t) continue;
                    var (i0, i1) = Range(_gx, bx0, bx1);
                    var (j0, j1) = Range(_gy, by0, by1);
                    for (int iy = j0; iy <= j1; iy++)
                        for (int ix = i0; ix <= i1; ix++) _forced[iy * _nx + ix] = true;
                }
        }
        _forcedSum = Prefix(_forced);

        // The narrowest metal run through each cell, per axis, over every level.
        _runX = new double[_nx * _ny]; _runY = new double[_nx * _ny];
        Array.Fill(_runX, double.PositiveInfinity); Array.Fill(_runY, double.PositiveInfinity);
        for (int li = 0; li < _levels; li++)
        {
            var m = _metal[li];
            for (int iy = 0; iy < _ny; iy++)
                for (int ix = 0; ix < _nx;)
                {
                    if (!m[iy * _nx + ix]) { ix++; continue; }
                    int e = ix;
                    while (e < _nx && m[iy * _nx + e]) e++;
                    double w = _gx[e] - _gx[ix];
                    for (int k = ix; k < e; k++) _runX[iy * _nx + k] = Math.Min(_runX[iy * _nx + k], w);
                    ix = e;
                }
            for (int ix = 0; ix < _nx; ix++)
                for (int iy = 0; iy < _ny;)
                {
                    if (!m[iy * _nx + ix]) { iy++; continue; }
                    int e = iy;
                    while (e < _ny && m[e * _nx + ix]) e++;
                    double w = _gy[e] - _gy[iy];
                    for (int k = iy; k < e; k++) _runY[k * _nx + ix] = Math.Min(_runY[k * _nx + ix], w);
                    iy = e;
                }
        }

        _needX = Needs(diag.X, _gx, _gy, _ny);
        _needY = Needs(diag.Y, _gy, _gx, _nx);
        foreach (var a in diag.X.Attractors)
        {
            var (lo, hi) = Range(_gy, a.AlongLoM, a.AlongHiM);
            _fanX.Add((a.Coord, a.C0M, a.Growth, lo, hi));
        }
        foreach (var a in diag.Y.Attractors)
        {
            var (lo, hi) = Range(_gx, a.AlongLoM, a.AlongHiM);
            _fanY.Add((a.Coord, a.C0M, a.Growth, lo, hi));
        }
    }

    // ── the partition ───────────────────────────────────────────────────────────────────────────

    private sealed record Block(int X0, int X1, int Y0, int Y1);

    /// <summary>Counts for the local grid, or — <paramref name="forceUniform"/> — for every block elementary, which is
    /// today's tensor grid and the estimator's calibration.</summary>
    public LocalGridCounts Count(bool forceUniform = false, bool stacked = false)
    {
        var leaves = new List<Block?>();
        if (forceUniform)
        {
            for (int iy = 0; iy < _ny; iy++)
                for (int ix = 0; ix < _nx; ix++) leaves.Add(new Block(ix, ix + 1, iy, iy + 1));
        }
        else
        {
            var bx = BaseCuts(_gx); var by = BaseCuts(_gy);
            for (int j = 0; j + 1 < by.Count; j++)
                for (int i = 0; i + 1 < bx.Count; i++) Refine(new Block(bx[i], bx[i + 1], by[j], by[j + 1]), leaves);
        }

        var map = new int[_nx * _ny];
        for (int id = 0; id < leaves.Count; id++) Stamp(leaves[id]!, id, map);
        if (!forceUniform) Balance(leaves, map);

        var live = new List<int>();
        for (int id = 0; id < leaves.Count; id++) if (leaves[id] is not null) live.Add(id);

        // Per-leaf level metal (majority), and which leaves are inhomogeneous.
        var metal = new bool[_levels][];
        int inhomogeneous = 0;
        for (int li = 0; li < _levels; li++) metal[li] = new bool[leaves.Count];
        foreach (int id in live)
        {
            var b = leaves[id]!;
            int area = (b.X1 - b.X0) * (b.Y1 - b.Y0);
            bool mixed = false;
            for (int li = 0; li < _levels; li++)
            {
                int c = Sum(_metalSum[li], b);
                metal[li][id] = 2 * c > area;
                if (c > 0 && c < area) mixed = true;
            }
            if (mixed) inhomogeneous++;
        }

        var cover = new bool[_p.ViaList.Count][];
        for (int v = 0; v < _p.ViaList.Count; v++)
        {
            cover[v] = new bool[leaves.Count];
            foreach (int id in live)
            {
                var b = leaves[id]!;
                cover[v][id] = 2 * Sum(_coverSum[v], b) > (b.X1 - b.X0) * (b.Y1 - b.Y0);
            }
        }

        var metal0 = metal.Select(m => (bool[])m.Clone()).ToArray();

        // R-pms2-4 — a fully overlapped, via-joined region keeps its top level and a one-cell rim of vertical unknowns.
        int regions = 0, droppedCells = 0, droppedVertical = 0;
        var skipVertical = new bool[_p.ViaList.Count][];
        for (int v = 0; v < _p.ViaList.Count; v++) skipVertical[v] = new bool[leaves.Count];
        if (stacked)
        {
            var dropLevel = new bool[_levels][];
            for (int li = 0; li < _levels; li++) dropLevel[li] = new bool[leaves.Count];
            for (int v = 0; v < _p.ViaList.Count; v++)
            {
                var via = _p.ViaList[v];
                if (via.ToGround) continue;
                int lo = via.LowerLayerIndex, hi = via.UpperLayerIndex;
                bool In(int id) => cover[v][id] && metal[lo][id] && metal[hi][id];
                var comp = new int[leaves.Count];
                Array.Fill(comp, -1);
                int nComp = 0;
                foreach (int seed in live)
                {
                    if (!In(seed) || comp[seed] >= 0) continue;
                    var members = new List<int>();
                    var stack = new Stack<int>();
                    stack.Push(seed); comp[seed] = nComp;
                    double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                    while (stack.Count > 0)
                    {
                        int id = stack.Pop();
                        members.Add(id);
                        var b = leaves[id]!;
                        x0 = Math.Min(x0, _gx[b.X0]); x1 = Math.Max(x1, _gx[b.X1]);
                        y0 = Math.Min(y0, _gy[b.Y0]); y1 = Math.Max(y1, _gy[b.Y1]);
                        foreach (int n in Neighbours(b, map))
                            if (comp[n] < 0 && In(n)) { comp[n] = nComp; stack.Push(n); }
                    }
                    if (x1 - x0 >= StackedMinSideM && y1 - y0 >= StackedMinSideM)
                    {
                        regions++;
                        foreach (int id in members)
                        {
                            var b = leaves[id]!;
                            bool interior = b.X0 > 0 && b.Y0 > 0 && b.X1 < _nx && b.Y1 < _ny;
                            if (interior)
                                foreach (int n in Neighbours(b, map, includeOutside: true))
                                    if (n < 0 || comp[n] != comp[id]) { interior = false; break; }
                            if (!interior) continue;
                            dropLevel[lo][id] = true;
                            skipVertical[v][id] = true;
                        }
                    }
                    nComp++;
                }
            }
            for (int li = 0; li < _levels; li++)
                foreach (int id in live)
                    if (dropLevel[li][id] && metal[li][id]) { metal[li][id] = false; droppedCells++; }
        }

        // Cells and rooftops per level: one rooftop per adjacent leaf pair carrying the level's metal.
        var cellsPer = new int[_levels];
        var roofPer = new int[_levels];
        int steps = 0;
        foreach (int id in live)
        {
            var b = leaves[id]!;
            for (int li = 0; li < _levels; li++)
            {
                if (!metal[li][id]) continue;
                cellsPer[li]++;
                int faces = 0;
                if (b.X1 < _nx)
                {
                    int prev = -1;
                    for (int iy = b.Y0; iy < b.Y1; iy++)
                    {
                        int n = map[iy * _nx + b.X1];
                        if (n == prev) continue;
                        prev = n;
                        if (metal[li][n]) { roofPer[li]++; faces++; }
                    }
                    if (faces > 1) steps++;
                }
                faces = 0;
                if (b.Y1 < _ny)
                {
                    int prev = -1;
                    for (int ix = b.X0; ix < b.X1; ix++)
                    {
                        int n = map[b.Y1 * _nx + ix];
                        if (n == prev) continue;
                        prev = n;
                        if (metal[li][n]) { roofPer[li]++; faces++; }
                    }
                    if (faces > 1) steps++;
                }
            }
        }

        // Vertical bases: a covered leaf with metal on both levels (on the meshed one for a ground attachment).
        int vertical = 0;
        for (int v = 0; v < _p.ViaList.Count; v++)
        {
            var via = _p.ViaList[v];
            foreach (int id in live)
            {
                if (!cover[v][id] || !metal0[via.UpperLayerIndex][id]) continue;
                if (!via.ToGround && !metal0[via.LowerLayerIndex][id]) continue;
                if (skipVertical[v][id]) { droppedVertical++; continue; }
                vertical++;
            }
        }

        return new LocalGridCounts(live.Count, cellsPer, roofPer, vertical, steps, inhomogeneous,
                                   regions, droppedCells, droppedVertical);
    }

    // ── refinement ─────────────────────────────────────────────────────────────────────────────

    private List<int> BaseCuts(double[] g)
    {
        var cuts = new List<int> { 0 };
        double step = double.IsFinite(_waveCap) && _waveCap > 0 ? _waveCap : g[^1] - g[0];
        double next = g[0] + step;
        for (int i = 1; i < g.Length - 1; i++)
            if (g[i] >= next - 1e-15) { cuts.Add(i); next = g[i] + step; }
        cuts.Add(g.Length - 1);
        return cuts;
    }

    private void Refine(Block b, List<Block?> into)
    {
        bool anyMetal = false;
        for (int li = 0; li < _levels && !anyMetal; li++) anyMetal = Sum(_metalSum[li], b) > 0;
        if (!anyMetal) { into.Add(b); return; }

        // A block partly on metal is a boundary no hard line resolves — an oblique edge — and is left staircased at the
        // pitch the size field asks for there, as the same artwork alone would be today. Its metal is the majority.
        bool forced = Sum(_forcedSum, b) > 0;
        bool sx = b.X1 - b.X0 > 1 && (forced || NeedSplit(b, x: true));
        bool sy = b.Y1 - b.Y0 > 1 && (forced || NeedSplit(b, x: false));
        if (!sx && !sy) { into.Add(b); return; }
        Split(b, into, sx, sy);
    }

    private void Split(Block b, List<Block?> into, bool sx, bool sy)
    {
        if (!sx && !sy) { into.Add(b); return; }
        int mx = sx ? Mid(_gx, b.X0, b.X1) : -1;
        int my = sy ? Mid(_gy, b.Y0, b.Y1) : -1;
        var xs = sx ? new[] { (b.X0, mx), (mx, b.X1) } : new[] { (b.X0, b.X1) };
        var ys = sy ? new[] { (b.Y0, my), (my, b.Y1) } : new[] { (b.Y0, b.Y1) };
        foreach (var (y0, y1) in ys)
            foreach (var (x0, x1) in xs) Refine(new Block(x0, x1, y0, y1), into);
    }

    /// <summary>Whether the block must be split across <paramref name="x"/> (true: the x-axis).</summary>
    private bool NeedSplit(Block b, bool x)
    {
        var (a0, a1, c0, c1) = x ? (b.X0, b.X1, b.Y0, b.Y1) : (b.Y0, b.Y1, b.X0, b.X1);
        var need = x ? _needX : _needY;
        for (int i = a0 + 1; i < a1; i++)
            foreach (var (lo, hi) in need[i])
                if (hi >= c0 && lo < c1) return true;

        var g = x ? _gx : _gy;
        double width = g[a1] - g[a0];
        double req = double.IsFinite(_waveCap) && _waveCap > 0 ? _waveCap : double.PositiveInfinity;

        // MinCellsAcrossConductor on the metal in the block.
        var run = x ? _runX : _runY;
        double narrow = double.PositiveInfinity;
        for (int iy = b.Y0; iy < b.Y1; iy++)
            for (int ix = b.X0; ix < b.X1; ix++) narrow = Math.Min(narrow, run[iy * _nx + ix]);
        if (double.IsFinite(narrow)) req = Math.Min(req, Math.Max(narrow, _detailFloor) / _minCells);

        // The edge fan of every attractor whose edge reaches this block along it.
        double lo0 = g[a0], hi0 = g[a1];
        foreach (var (coord, f0, gr, lo, hi) in x ? _fanX : _fanY)
        {
            if (!(f0 > 0)) continue;
            if (hi + Reach < c0 || lo - Reach >= c1) continue;
            double d = coord < lo0 ? lo0 - coord : coord > hi0 ? coord - hi0 : 0.0;
            req = Math.Min(req, f0 + gr * d);
        }
        return width > req * (1 + 1e-6);
    }

    private static int Mid(double[] g, int a, int b)
    {
        double m = 0.5 * (g[a] + g[b]);
        int best = a + 1;
        for (int i = a + 1; i < b; i++)
            if (Math.Abs(g[i] - m) < Math.Abs(g[best] - m)) best = i;
        return best;
    }

    /// <summary>2:1 balance between blocks sharing a metal level: split any block one of whose faces meets more than two
    /// such neighbours, across that face, until none does.</summary>
    private void Balance(List<Block?> leaves, int[] map)
    {
        var queue = new Queue<int>();
        for (int id = 0; id < leaves.Count; id++) if (leaves[id] is not null) queue.Enqueue(id);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            var b = leaves[id];
            if (b is null) continue;
            bool splitY = b.Y1 - b.Y0 > 1 && (FaceCount(b, map, leaves, vertical: true, low: true) > 2 ||
                                              FaceCount(b, map, leaves, vertical: true, low: false) > 2);
            bool splitX = b.X1 - b.X0 > 1 && (FaceCount(b, map, leaves, vertical: false, low: true) > 2 ||
                                              FaceCount(b, map, leaves, vertical: false, low: false) > 2);
            if (!splitX && !splitY) continue;

            var nb = Neighbours(b, map).ToList();
            leaves[id] = null;
            int mx = splitX ? Mid(_gx, b.X0, b.X1) : -1, my = splitY ? Mid(_gy, b.Y0, b.Y1) : -1;
            var xs = splitX ? new[] { (b.X0, mx), (mx, b.X1) } : new[] { (b.X0, b.X1) };
            var ys = splitY ? new[] { (b.Y0, my), (my, b.Y1) } : new[] { (b.Y0, b.Y1) };
            foreach (var (y0, y1) in ys)
                foreach (var (x0, x1) in xs)
                {
                    var c = new Block(x0, x1, y0, y1);
                    leaves.Add(c);
                    Stamp(c, leaves.Count - 1, map);
                    queue.Enqueue(leaves.Count - 1);
                }
            foreach (int n in nb) queue.Enqueue(n);
        }
    }

    private int FaceCount(Block b, int[] map, List<Block?> leaves, bool vertical, bool low)
    {
        int count = 0, prev = -1;
        if (vertical)
        {
            int ix = low ? b.X0 - 1 : b.X1;
            if (ix < 0 || ix >= _nx) return 0;
            for (int iy = b.Y0; iy < b.Y1; iy++)
            {
                int n = map[iy * _nx + ix];
                if (n == prev) continue;
                prev = n;
                if (SharesMetal(b, leaves[n]!)) count++;
            }
        }
        else
        {
            int iy = low ? b.Y0 - 1 : b.Y1;
            if (iy < 0 || iy >= _ny) return 0;
            for (int ix = b.X0; ix < b.X1; ix++)
            {
                int n = map[iy * _nx + ix];
                if (n == prev) continue;
                prev = n;
                if (SharesMetal(b, leaves[n]!)) count++;
            }
        }
        return count;
    }

    private bool SharesMetal(Block a, Block b)
    {
        for (int li = 0; li < _levels; li++)
            if (Sum(_metalSum[li], a) > 0 && Sum(_metalSum[li], b) > 0) return true;
        return false;
    }

    private IEnumerable<int> Neighbours(Block b, int[] map, bool includeOutside = false)
    {
        int prev = int.MinValue;
        for (int iy = b.Y0; iy < b.Y1; iy++)
        {
            foreach (int ix in new[] { b.X0 - 1, b.X1 })
            {
                if (ix < 0 || ix >= _nx) { if (includeOutside) yield return -1; continue; }
                int n = map[iy * _nx + ix];
                if (n != prev) { prev = n; yield return n; }
            }
        }
        for (int ix = b.X0; ix < b.X1; ix++)
            foreach (int iy in new[] { b.Y0 - 1, b.Y1 })
            {
                if (iy < 0 || iy >= _ny) { if (includeOutside) yield return -1; continue; }
                int n = map[iy * _nx + ix];
                if (n != prev) { prev = n; yield return n; }
            }
    }

    private void Stamp(Block b, int id, int[] map)
    {
        for (int iy = b.Y0; iy < b.Y1; iy++)
            for (int ix = b.X0; ix < b.X1; ix++) map[iy * _nx + ix] = id;
    }

    // ── geometry, independent of the mesher ───────────────────────────────────────────────────

    /// <summary>Per elementary cell: is its centre inside any of <paramref name="polys"/> (even-odd per polygon,
    /// holes included).</summary>
    private bool[] Mask(IReadOnlyList<PlanarPolygon> polys, bool union)
    {
        var m = new bool[_nx * _ny];
        var xs = new List<double>();
        foreach (var poly in polys)
        {
            var (bx0, by0, bx1, by1) = poly.Bounds();
            var (j0, j1) = Range(_gy, by0, by1);
            var (i0, i1) = Range(_gx, bx0, bx1);
            for (int iy = j0; iy <= j1; iy++)
            {
                double yc = 0.5 * (_gy[iy] + _gy[iy + 1]);
                xs.Clear();
                Crossings(poly.Outer, yc, xs);
                foreach (var h in poly.HoleRings) Crossings(h, yc, xs);
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int ix = i0; ix <= i1; ix++)
                {
                    double xc = 0.5 * (_gx[ix] + _gx[ix + 1]);
                    int left = 0;
                    foreach (double c in xs) if (c < xc) left++;
                    if ((left & 1) == 1) m[iy * _nx + ix] = true;
                }
            }
        }
        _ = union;
        return m;
    }

    private static void Crossings(IReadOnlyList<EmPoint> ring, double y, List<double> into)
    {
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[j]; var b = ring[i];
            if ((a.Y > y) == (b.Y > y)) continue;
            into.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
        }
    }

    /// <summary>The elementary index range [lo, hi] of cells overlapping [a, b] (clamped to the grid).</summary>
    private static (int Lo, int Hi) Range(double[] g, double a, double b)
    {
        int n = g.Length - 1;
        if (!(b >= a)) return (1, 0);
        int lo = Array.BinarySearch(g, a); lo = lo < 0 ? ~lo - 1 : lo;
        int hi = Array.BinarySearch(g, b); hi = hi < 0 ? ~hi - 1 : hi - 1;
        return (Math.Clamp(lo, 0, n - 1), Math.Clamp(hi, 0, n - 1));
    }

    private List<(int, int)>[] Needs(PlanarGridAxis axis, double[] g, double[] other, int nOther)
    {
        var need = new List<(int, int)>[g.Length];
        for (int i = 0; i < need.Length; i++) need[i] = [];
        double tol = (g[^1] - g[0]) * 1e-9;
        foreach (var o in axis.HardOrigins)
        {
            if (!(o.AlongHiM >= o.AlongLoM)) continue;
            int i = Array.BinarySearch(g, o.Coord);
            if (i < 0)
            {
                int k = ~i;
                if (k < g.Length && Math.Abs(g[k] - o.Coord) <= tol) i = k;
                else if (k > 0 && Math.Abs(g[k - 1] - o.Coord) <= tol) i = k - 1;
                else continue;
            }
            if (i <= 0 || i >= g.Length - 1) continue;
            var (lo, hi) = Range(other, o.AlongLoM, o.AlongHiM);
            need[i].Add((Math.Max(0, lo - Reach), Math.Min(nOther - 1, hi + Reach)));
        }
        return need;
    }

    private int[] Prefix(bool[] m)
    {
        var s = new int[(_nx + 1) * (_ny + 1)];
        for (int iy = 0; iy < _ny; iy++)
        {
            int row = 0;
            for (int ix = 0; ix < _nx; ix++)
            {
                row += m[iy * _nx + ix] ? 1 : 0;
                s[(iy + 1) * (_nx + 1) + ix + 1] = s[iy * (_nx + 1) + ix + 1] + row;
            }
        }
        return s;
    }

    private int Sum(int[] s, Block b)
        => s[b.Y1 * (_nx + 1) + b.X1] - s[b.Y0 * (_nx + 1) + b.X1] - s[b.Y1 * (_nx + 1) + b.X0] + s[b.Y0 * (_nx + 1) + b.X0];

    // ── R-pms2-2's shared-grid measure ────────────────────────────────────────────────────────

    /// <summary>
    /// Per axis: of every crossing of an interior gridline with a metal cell (either side of the line, any level), how
    /// many lie more than <paramref name="k"/> cells along the line from what asked for that line — a hard line's
    /// feature, or the edge whose fan placed it. Bulk lines are needed everywhere under the tensor rule and are never
    /// counted remote. Also returns how many lines have at least one remote metal crossing.
    /// </summary>
    public static (long Crossings, long Remote, int LinesWithRemote) SharedGridMeasure(
        PlanarGridDiagnostics diag, PlanarProblem problem, bool xAxis, int k)
    {
        var axis = xAxis ? diag.X : diag.Y;
        var g = axis.Lines;
        var other = xAxis ? diag.Y.Lines : diag.X.Lines;
        int nx = diag.X.Lines.Length - 1, ny = diag.Y.Lines.Length - 1;
        var est = new LocalGridEstimate(problem, diag, [], 1, reach: 0);
        var any = new bool[nx * ny];
        foreach (var m in est._metal) for (int c = 0; c < any.Length; c++) any[c] |= m[c];

        var origins = est.Needs(axis, g, other, other.Length - 1);   // reach 0: the feature's own stretch
        double tol = (g[^1] - g[0]) * 1e-9;
        var fans = xAxis ? est._fanX : est._fanY;
        long crossings = 0, remote = 0;
        int linesWithRemote = 0;
        for (int i = 1; i < g.Length - 1; i++)
        {
            var src = axis.Source[i];
            var ranges = new List<(int Lo, int Hi)>(origins[i]);
            if (src == PlanarGridLineSource.EdgeFan)
                foreach (var f in fans)
                    if (f.C0 > 0 && f.C0 + f.G * Math.Abs(g[i] - f.Coord) < axis.BulkCapM * (1 - 1e-9))
                        ranges.Add((f.Lo, f.Hi));
            bool bulk = src is PlanarGridLineSource.BulkWavelength or PlanarGridLineSource.BulkMinCellsAcross
                                or PlanarGridLineSource.BulkPitchField;
            bool lineRemote = false;
            for (int j = 0; j < other.Length - 1; j++)
            {
                int a = xAxis ? j * nx + (i - 1) : (i - 1) * nx + j;
                int b = xAxis ? j * nx + i : i * nx + j;
                if (!any[a] && !any[b]) continue;
                crossings++;
                if (bulk) continue;
                bool near = false;
                foreach (var (lo, hi) in ranges)
                    if (j >= lo - k && j <= hi + k) { near = true; break; }
                if (!near) { remote++; lineRemote = true; }
            }
            if (lineRemote) linesWithRemote++;
        }
        _ = tol;
        return (crossings, remote, linesWithRemote);
    }
}
