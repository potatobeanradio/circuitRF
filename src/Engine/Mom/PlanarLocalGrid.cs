// PMS-4 — a locally refined shared grid: the partition, the cells as blocks, and the rooftop across a refinement step.
//
// D-pms4-1 — A CELL IS A BLOCK OF TODAY'S GRID. The shared gridlines gx, gy are built exactly as the tensor grid builds
// them, every candidate line included, and a local-grid cell spans [gx[IX], gx[IX1]) × [gy[IY], gy[IY1]). Every feature
// edge is still a cell edge (R-msh-1), neighbours are still found by comparing integer spans (R-msh-2), and the
// conformal cut still runs on a rectangle. The tensor grid is the case where every block is 1 × 1.
//
// D-pms4-2 — ONE PARTITION FOR EVERY LEVEL. Refinement is decided once, for the union of every level's requests, so a
// via footprint's blocks pair one-to-one in z. A quadtree over base blocks at the λ cap, then a k:1 balance (2:1 by
// default): no face meets more than k neighbouring faces on a level the two share.
//
// D-pms4-3 — WHEN A BLOCK MAY MERGE. A block is split, per axis, while a line inside it is NEEDED there: a hard line
// within LocalReachCells of its own feature's stretch of the line, or a pitch finer than the block that the size field
// asks for at it (the λ cap or the pitch field, MinCellsAcrossConductor on the metal run through it floored at the
// detail floor, and the edge fan of every attractor whose edge reaches it). Port feeds are never coarsened.
//
// ONE RULE THE ESTIMATOR (PMS-2's LocalGridEstimate) DID NOT HAVE, AND IT IS WHAT KEEPS THE MESH EXACT: a block is
// coarser than 1 × 1 only where it is HOMOGENEOUS — on every level either no position carries a cell or every position
// carries a whole (uncut, unmerged) cell of the same film and stack, and every via footprint covers all of it or none.
// The cells are those the tensor grid built, so the coarse mesh tiles exactly what today's tiles, and a staircased
// oblique rim stays at today's elementary pitch rather than being re-staircased at a coarser one.

namespace CircuitRF.Engine.Mom;

/// <summary>PMS-4 — one half-support of a rooftop on the local grid: a rectangle of one cell and the divergence pulse on
/// it (<see cref="Sign"/> / area, integrating to <see cref="Sign"/>).</summary>
public readonly record struct PlanarStepSupport(int CellIndex, double XMin, double YMin, double XMax, double YMax,
                                                double Sign)
{
    public double Area => (XMax - XMin) * (YMax - YMin);
    /// <summary>The divergence pulse's value on the rectangle; zero for a grounded half.</summary>
    public double Pulse => Sign == 0 ? 0 : Sign / Area;
}

/// <summary>PMS-4 — what the local grid made of a mesh: the partition, and the counters the report quotes.</summary>
internal sealed class PlanarLocalGridResult
{
    public required int[] BlockAt { get; init; }
    public required int[] BlockX0 { get; init; }
    public required int[] BlockY0 { get; init; }
    public int Blocks { get; init; }
    public int CoarseBlocks { get; init; }
    /// <summary>Mean share of interior lines active (a block boundary) where they cross metal, per axis.</summary>
    public double ActiveFractionX { get; init; }
    public double ActiveFractionY { get; init; }

    public bool IsCorner(int pos, int nx) => BlockX0[BlockAt[pos]] == pos % nx && BlockY0[BlockAt[pos]] == pos / nx;
}

/// <summary>
/// <b>PMS-4 — the locally refined shared grid</b> (brief-pms-4-local-grid-mesher.md). <see cref="SurfaceMesher.Mesh"/>
/// calls <see cref="Coarsen"/> when <see cref="PlanarMeshSettings.LocalGrid"/> is on; <see cref="Supports"/> is the
/// D-pms4-4 definition of a rooftop across a refinement step, which the fill will read.
/// </summary>
public static class PlanarLocalGrid
{
    private const int Unique = int.MinValue;

    /// <summary>
    /// <b>D-pms4-4 — a rooftop's two half-supports.</b> Where a coarse cell's face meets finer faces there is one rooftop
    /// per fine face segment; its half-support on the coarse side is the strip of the coarse cell the segment spans —
    /// full depth along the current, segment width across — with a divergence pulse of ±1/(strip area). A full-face
    /// rooftop is the special case where the segment is the face, and a vertical basis's two halves are its two foot
    /// cells (a ground attachment's grounded half has sign 0). Whole-rectangle cells only: a cut cell's support is
    /// <see cref="PlanarBasisFunctions.Supports"/>'.
    /// </summary>
    public static (PlanarStepSupport A, PlanarStepSupport B) Supports(PlanarMesh mesh, PlanarBasis basis)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(basis);
        var a = mesh.Cells[basis.CellA];
        var b = mesh.Cells[basis.CellB];
        if (a.Region is not null || b.Region is not null)
            throw new ArgumentException("A cut cell's support is not a rectangle; use PlanarBasisFunctions.Supports.",
                                        nameof(basis));

        if (basis.AttachesToGround)
            return (Whole(basis.CellB, b, -1.0), Whole(basis.CellB, b, 0.0));
        if (basis.Direction == PlanarBasisDirection.Z)
            return (Whole(basis.CellA, a, +1.0), Whole(basis.CellB, b, -1.0));

        if (!basis.IsSegment) return (Whole(basis.CellA, a, +1.0), Whole(basis.CellB, b, -1.0));

        if (basis.Direction == PlanarBasisDirection.X)
        {
            double y0 = mesh.GridY[basis.SegmentLo], y1 = mesh.GridY[basis.SegmentHi];
            return (new(basis.CellA, a.XMin, y0, a.XMax, y1, +1.0), new(basis.CellB, b.XMin, y0, b.XMax, y1, -1.0));
        }
        double x0 = mesh.GridX[basis.SegmentLo], x1 = mesh.GridX[basis.SegmentHi];
        return (new(basis.CellA, x0, a.YMin, x1, a.YMax, +1.0), new(basis.CellB, x0, b.YMin, x1, b.YMax, -1.0));

        static PlanarStepSupport Whole(int i, PlanarCell c, double sign) => new(i, c.XMin, c.YMin, c.XMax, c.YMax, sign);
    }

    /// <summary>The settings, inputs and size field the partition is decided from.</summary>
    internal sealed record Inputs(
        PlanarProblem Problem,
        double[] GridX,
        double[] GridY,
        PlanarGridDiagnostics Lines,
        Func<double, double> CapX,
        Func<double, double> CapY,
        double BaseStepX,
        double BaseStepY,
        double DetailFloorM,
        int MinCellsAcross,
        int Reach,
        int Balance,
        bool ForceElementary,
        IReadOnlyList<PlanarPort>? Ports,
        ViaFootprintPlan Footprints);

    /// <summary>
    /// Decides the partition and rewrites <paramref name="cells"/> and <paramref name="cellAtPerLayer"/> in place: a
    /// homogeneous block's elementary cells become one block cell at its low corner, every 1 × 1 block keeps the cell
    /// the tensor grid made (the very object), and the order stays (LayerIndex, IY, IX).
    /// </summary>
    internal static PlanarLocalGridResult Coarsen(Inputs inp, List<PlanarCell> cells, int[][] cellAtPerLayer,
                                                  RunControl? control)
    {
        var part = new Partition(inp, cells, cellAtPerLayer);
        part.Build(control);

        // ── Cells: walk the tensor grid's cells in their own order ──────────────────────────────────────────────
        int nx = inp.GridX.Length - 1;
        var gx = inp.GridX; var gy = inp.GridY;
        var newIndex = new int[cells.Count];
        var kept = new List<PlanarCell>(cells.Count);
        var blockCell = new Dictionary<long, int>();   // (level, block) → new index; looked up, never iterated (R-msh-2)
        for (int ci = 0; ci < cells.Count; ci++)
        {
            var c = cells[ci];
            int pos = c.IY * nx + c.IX;
            int bk = part.BlockAt[pos];
            var (x0, x1, y0, y1) = part.Leaves[bk]!.Value;
            if (x1 - x0 == 1 && y1 - y0 == 1) { newIndex[ci] = kept.Count; kept.Add(c); continue; }
            long key = (long)c.LayerIndex * part.Leaves.Count + bk;
            if (c.IX == x0 && c.IY == y0)
            {
                blockCell[key] = kept.Count;
                newIndex[ci] = kept.Count;
                kept.Add(new PlanarCell(c.LayerIndex, x0, y0, gx[x0], gy[y0], gx[x1], gy[y1])
                {
                    Film = c.Film, Stack = c.Stack, IX1 = x1, IY1 = y1,
                });
            }
            else newIndex[ci] = blockCell[key];
        }
        foreach (var at in cellAtPerLayer)
            for (int k = 0; k < at.Length; k++)
                if (at[k] >= 0) at[k] = newIndex[at[k]];
        cells.Clear();
        cells.AddRange(kept);

        return part.Result();
    }

    // ── The partition ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Partition
    {
        private readonly Inputs _in;
        private readonly double[] _gx, _gy;
        private readonly int _nx, _ny, _levels;
        private readonly int[][] _code;          // [level][pos]: −1 no cell, Unique for a cut/merged cell, else a class
        private readonly int[][] _hasSum;        // [level] prefix sums of "a cell here"
        private readonly bool[][] _cover;        // [via][pos]
        private readonly int[] _forcedSum;
        private readonly double[] _runX, _runY;  // narrowest metal run through a position, over every level (∞ off metal)
        private readonly List<(int Lo, int Hi)>[] _needX, _needY;
        private readonly List<(double Coord, double C0, double G, int Lo, int Hi)> _fanX = [], _fanY = [];

        public readonly List<(int X0, int X1, int Y0, int Y1)?> Leaves = [];
        public int[] BlockAt = [];

        public Partition(Inputs inp, List<PlanarCell> cells, int[][] cellAtPerLayer)
        {
            _in = inp;
            _gx = inp.GridX; _gy = inp.GridY;
            _nx = _gx.Length - 1; _ny = _gy.Length - 1;
            _levels = cellAtPerLayer.Length;
            int n = _nx * _ny;

            // Which cells span more than one position (a conformal merge): they may never be part of a coarse block.
            var positions = new int[cells.Count];
            foreach (var at in cellAtPerLayer)
                foreach (int c in at) if (c >= 0) positions[c]++;

            _code = new int[_levels][];
            _hasSum = new int[_levels][];
            for (int li = 0; li < _levels; li++)
            {
                var at = cellAtPerLayer[li];
                var code = new int[n];
                var has = new bool[n];
                for (int p = 0; p < n; p++)
                {
                    int c = at[p];
                    if (c < 0) { code[p] = -1; continue; }
                    has[p] = true;
                    var cell = cells[c];
                    code[p] = cell.Region is not null || positions[c] > 1
                        ? Unique
                        : 1 + (cell.Film + 1) + 65_536 * (cell.Stack + 1);
                }
                _code[li] = code;
                _hasSum[li] = Prefix(has);
            }

            // Via footprints, by the same centre-containment test the vertical scan makes.
            var vias = inp.Problem.ViaList;
            _cover = new bool[vias.Count][];
            for (int v = 0; v < vias.Count; v++)
            {
                var cov = new bool[n];
                var plan = inp.Footprints.Vias[v];
                foreach (var box in plan.Boxes)
                {
                    var poly = vias[v].Polygons[box.Polygon];
                    for (int iy = box.Y0; iy <= box.Y1; iy++)
                    {
                        double yc = 0.5 * (_gy[iy] + _gy[iy + 1]);
                        for (int ix = box.X0; ix <= box.X1; ix++)
                            if (!cov[iy * _nx + ix] && poly.Contains(0.5 * (_gx[ix] + _gx[ix + 1]), yc))
                                cov[iy * _nx + ix] = true;
                    }
                }
                _cover[v] = cov;
            }

            // Port feeds stay elementary (D-pms4-3): a window around each port — across it, the chord of its own metal
            // through the port; along it, max(3 substrate heights, that chord) either side, which covers the end run, the
            // cells a calibration standard is built from and any feed lead the solver grows outward. Not the whole
            // polygon the port sits on: an extracted layout's polygon is a whole NET, and forcing a net elementary
            // forced the whole of a small block (A2 through the extractor: every metal cell).
            var forced = new bool[n];
            if (inp.ForceElementary) Array.Fill(forced, true);
            else if (inp.Ports is not null)
                foreach (var port in inp.Ports)
                    foreach (var loc in port.NegativeLocation is { } neg ? new[] { port.Location, neg } : new[] { port.Location })
                        ForcePortWindow(inp.Problem, port, loc, forced);
            _forcedSum = Prefix(forced);

            // The narrowest metal run through each position, per axis, over every level — measured on the cells.
            _runX = new double[n]; _runY = new double[n];
            Array.Fill(_runX, double.PositiveInfinity); Array.Fill(_runY, double.PositiveInfinity);
            for (int li = 0; li < _levels; li++)
            {
                var at = cellAtPerLayer[li];
                for (int iy = 0; iy < _ny; iy++)
                    for (int ix = 0; ix < _nx;)
                    {
                        if (at[iy * _nx + ix] < 0) { ix++; continue; }
                        int e = ix;
                        while (e < _nx && at[iy * _nx + e] >= 0) e++;
                        double w = _gx[e] - _gx[ix];
                        for (int k = ix; k < e; k++) _runX[iy * _nx + k] = Math.Min(_runX[iy * _nx + k], w);
                        ix = e;
                    }
                for (int ix = 0; ix < _nx; ix++)
                    for (int iy = 0; iy < _ny;)
                    {
                        if (at[iy * _nx + ix] < 0) { iy++; continue; }
                        int e = iy;
                        while (e < _ny && at[e * _nx + ix] >= 0) e++;
                        double w = _gy[e] - _gy[iy];
                        for (int k = iy; k < e; k++) _runY[k * _nx + ix] = Math.Min(_runY[k * _nx + ix], w);
                        iy = e;
                    }
            }

            _needX = Needs(inp.Lines.X, _gx, _gy, _ny, inp.Reach);
            _needY = Needs(inp.Lines.Y, _gy, _gx, _nx, inp.Reach);
            foreach (var a in inp.Lines.X.Attractors)
            {
                var (lo, hi) = Range(_gy, a.AlongLoM, a.AlongHiM);
                _fanX.Add((a.Coord, a.C0M, a.Growth, lo, hi));
            }
            foreach (var a in inp.Lines.Y.Attractors)
            {
                var (lo, hi) = Range(_gx, a.AlongLoM, a.AlongHiM);
                _fanY.Add((a.Coord, a.C0M, a.Growth, lo, hi));
            }
        }

        private void ForcePortWindow(PlanarProblem problem, PlanarPort port, EmPoint loc, bool[] forced)
        {
            bool alongX = port.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double h = problem.Slab.HeightM > 0 ? problem.Slab.HeightM : 0;
            double tol = 1e-9 * Math.Max(_gx[^1] - _gx[0], _gy[^1] - _gy[0]);
            var levels = port.LayerIndex is { } l ? new[] { l } : Enumerable.Range(0, problem.Layers.Count).ToArray();
            foreach (int li in levels)
            {
                if (li < 0 || li >= problem.Layers.Count) continue;
                foreach (var poly in problem.Layers[li].Polygons)
                {
                    var (bx0, by0, bx1, by1) = poly.Bounds();
                    if (loc.X < bx0 - tol || loc.X > bx1 + tol || loc.Y < by0 - tol || loc.Y > by1 + tol) continue;

                    // The chord across the feed, taken just inside the metal from the port's edge.
                    double inward = port.Side switch
                    {
                        PlanarPortSide.MinX or PlanarPortSide.MinY => +1, _ => -1,
                    };
                    double step = Math.Max(tol, 1e-3 * (alongX ? bx1 - bx0 : by1 - by0));
                    double at = (alongX ? loc.X : loc.Y) + inward * step;
                    var (c0, c1) = Chord(poly, at, transverseIsY: alongX, alongX ? loc.Y : loc.X);
                    if (!(c1 > c0)) (c0, c1) = alongX ? (by0, by1) : (bx0, bx1);
                    double reach = Math.Max(3 * h, c1 - c0);
                    double a = alongX ? loc.X : loc.Y;
                    var (w0, w1, v0, v1) = alongX
                        ? (a - reach, a + reach, c0, c1)
                        : (c0, c1, a - reach, a + reach);
                    var (i0, i1) = Range(_gx, Math.Max(w0, bx0), Math.Min(w1, bx1));
                    var (j0, j1) = Range(_gy, Math.Max(v0, by0), Math.Min(v1, by1));
                    for (int iy = j0; iy <= j1; iy++)
                        for (int ix = i0; ix <= i1; ix++) forced[iy * _nx + ix] = true;
                }
            }
        }

        /// <summary>The interval of the polygon's metal on the line (x = at, transverse y) or (y = at, transverse x) that
        /// contains <paramref name="near"/>, or the nearest one; (0, 0) when the line misses the polygon.</summary>
        private static (double Lo, double Hi) Chord(PlanarPolygon poly, double at, bool transverseIsY, double near)
        {
            var xs = new List<double>();
            foreach (var ring in new[] { poly.Outer }.Concat(poly.HoleRings))
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    var (pa, pb) = (ring[j], ring[i]);
                    double ua = transverseIsY ? pa.X : pa.Y, ub = transverseIsY ? pb.X : pb.Y;
                    if ((ua > at) == (ub > at)) continue;
                    double va = transverseIsY ? pa.Y : pa.X, vb = transverseIsY ? pb.Y : pb.X;
                    xs.Add(va + (at - ua) * (vb - va) / (ub - ua));
                }
            xs.Sort();
            (double, double) best = (0, 0);
            double bestD = double.PositiveInfinity;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                double d = near < xs[k] ? xs[k] - near : near > xs[k + 1] ? near - xs[k + 1] : 0;
                if (d < bestD) { bestD = d; best = (xs[k], xs[k + 1]); }
            }
            return best;
        }

        public void Build(RunControl? control)
        {
            var bx = BaseCuts(_gx, _in.BaseStepX);
            var by = BaseCuts(_gy, _in.BaseStepY);
            for (int j = 0; j + 1 < by.Count; j++)
            {
                control?.Token.ThrowIfCancellationRequested();
                for (int i = 0; i + 1 < bx.Count; i++) Refine(bx[i], bx[i + 1], by[j], by[j + 1]);
            }

            BlockAt = new int[_nx * _ny];
            for (int id = 0; id < Leaves.Count; id++) Stamp(Leaves[id]!.Value, id);
            if (!_in.ForceElementary) Balance(control);
        }

        public PlanarLocalGridResult Result()
        {
            var x0 = new int[Leaves.Count];
            var y0 = new int[Leaves.Count];
            int blocks = 0, coarse = 0;
            for (int id = 0; id < Leaves.Count; id++)
            {
                if (Leaves[id] is not { } b) continue;
                x0[id] = b.X0; y0[id] = b.Y0;
                blocks++;
                if (b.X1 - b.X0 > 1 || b.Y1 - b.Y0 > 1) coarse++;
            }
            return new PlanarLocalGridResult
            {
                BlockAt = BlockAt, BlockX0 = x0, BlockY0 = y0, Blocks = blocks, CoarseBlocks = coarse,
                ActiveFractionX = Active(alongX: true), ActiveFractionY = Active(alongX: false),
            };
        }

        /// <summary>Of every crossing of an interior line with metal (a cell on either side, any level), the share where
        /// the line is a block boundary.</summary>
        private double Active(bool alongX)
        {
            long crossings = 0, active = 0;
            int lines = alongX ? _nx : _ny, rows = alongX ? _ny : _nx;
            for (int r = 0; r < rows; r++)
                for (int i = 1; i < lines; i++)
                {
                    int pa = alongX ? r * _nx + i - 1 : (i - 1) * _nx + r;
                    int pb = alongX ? r * _nx + i : i * _nx + r;
                    bool metal = false;
                    for (int li = 0; li < _levels && !metal; li++) metal = _code[li][pa] != -1 || _code[li][pb] != -1;
                    if (!metal) continue;
                    crossings++;
                    if (BlockAt[pa] != BlockAt[pb]) active++;
                }
            return crossings == 0 ? 0 : (double)active / crossings;
        }

        // ── refinement ─────────────────────────────────────────────────────────────────────────────────────────

        private void Refine(int x0, int x1, int y0, int y1)
        {
            bool anyCell = false;
            for (int li = 0; li < _levels && !anyCell; li++) anyCell = Sum(_hasSum[li], x0, x1, y0, y1) > 0;
            if (!anyCell) { Leaves.Add((x0, x1, y0, y1)); return; }

            bool forced = Sum(_forcedSum, x0, x1, y0, y1) > 0;
            bool sx = x1 - x0 > 1 && (forced || NeedSplit(x0, x1, y0, y1, alongX: true));
            bool sy = y1 - y0 > 1 && (forced || NeedSplit(x0, x1, y0, y1, alongX: false));
            if (!sx && !sy && (x1 - x0 > 1 || y1 - y0 > 1) && !Homogeneous(x0, x1, y0, y1))
            {
                sx = x1 - x0 > 1;
                sy = y1 - y0 > 1;
            }
            if (!sx && !sy) { Leaves.Add((x0, x1, y0, y1)); return; }

            int mx = sx ? Mid(_gx, x0, x1) : -1, my = sy ? Mid(_gy, y0, y1) : -1;
            foreach (var (a0, a1) in sy ? new[] { (y0, my), (my, y1) } : new[] { (y0, y1) })
                foreach (var (b0, b1) in sx ? new[] { (x0, mx), (mx, x1) } : new[] { (x0, x1) })
                    Refine(b0, b1, a0, a1);
        }

        /// <summary>Whether the size field, or a needed hard line, asks for the block to be split across
        /// <paramref name="alongX"/> (true: the x axis).</summary>
        private bool NeedSplit(int x0, int x1, int y0, int y1, bool alongX)
        {
            var (a0, a1, c0, c1) = alongX ? (x0, x1, y0, y1) : (y0, y1, x0, x1);
            var need = alongX ? _needX : _needY;
            for (int i = a0 + 1; i < a1; i++)
                foreach (var (lo, hi) in need[i])
                    if (hi >= c0 && lo < c1) return true;

            var g = alongX ? _gx : _gy;
            double lo0 = g[a0], hi0 = g[a1], width = hi0 - lo0;
            var cap = alongX ? _in.CapX : _in.CapY;
            double req = Math.Min(cap(lo0), Math.Min(cap(hi0), cap(0.5 * (lo0 + hi0))));
            if (!(req > 0)) req = double.PositiveInfinity;

            var run = alongX ? _runX : _runY;
            double narrow = double.PositiveInfinity;
            for (int iy = y0; iy < y1; iy++)
                for (int ix = x0; ix < x1; ix++) narrow = Math.Min(narrow, run[iy * _nx + ix]);
            if (double.IsFinite(narrow))
                req = Math.Min(req, Math.Max(narrow, _in.DetailFloorM) / _in.MinCellsAcross);

            foreach (var (coord, f0, gr, lo, hi) in alongX ? _fanX : _fanY)
            {
                if (!(f0 > 0)) continue;
                if (hi + _in.Reach < c0 || lo - _in.Reach >= c1) continue;
                double d = coord < lo0 ? lo0 - coord : coord > hi0 ? coord - hi0 : 0.0;
                req = Math.Min(req, f0 + gr * d);
            }
            return width > req * (1 + 1e-6);
        }

        private bool Homogeneous(int x0, int x1, int y0, int y1)
        {
            int corner = y0 * _nx + x0;
            for (int li = 0; li < _levels; li++)
            {
                var code = _code[li];
                int c0 = code[corner];
                if (c0 == Unique) return false;
                for (int iy = y0; iy < y1; iy++)
                    for (int ix = x0; ix < x1; ix++)
                        if (code[iy * _nx + ix] != c0) return false;
            }
            foreach (var cov in _cover)
            {
                bool v0 = cov[corner];
                for (int iy = y0; iy < y1; iy++)
                    for (int ix = x0; ix < x1; ix++)
                        if (cov[iy * _nx + ix] != v0) return false;
            }
            return true;
        }

        // ── balance ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>k:1 balance between blocks sharing a level: split any block one of whose faces meets more than
        /// <see cref="Inputs.Balance"/> such neighbours, across that face, until none does. Splitting a homogeneous
        /// block leaves homogeneous blocks, so exactness survives it.</summary>
        private void Balance(RunControl? control)
        {
            var queue = new Queue<int>();
            for (int id = 0; id < Leaves.Count; id++) if (Leaves[id] is not null) queue.Enqueue(id);
            int k = _in.Balance;
            int popped = 0;
            while (queue.Count > 0)
            {
                if ((++popped & 0xFFF) == 0) control?.Token.ThrowIfCancellationRequested();
                int id = queue.Dequeue();
                if (Leaves[id] is not { } b) continue;
                bool splitY = b.Y1 - b.Y0 > 1 && (FaceCount(b, xFace: true, low: true) > k ||
                                                  FaceCount(b, xFace: true, low: false) > k);
                bool splitX = b.X1 - b.X0 > 1 && (FaceCount(b, xFace: false, low: true) > k ||
                                                  FaceCount(b, xFace: false, low: false) > k);
                if (!splitX && !splitY) continue;

                var nb = Neighbours(b);
                Leaves[id] = null;
                int mx = splitX ? Mid(_gx, b.X0, b.X1) : -1, my = splitY ? Mid(_gy, b.Y0, b.Y1) : -1;
                foreach (var (y0, y1) in splitY ? new[] { (b.Y0, my), (my, b.Y1) } : new[] { (b.Y0, b.Y1) })
                    foreach (var (x0, x1) in splitX ? new[] { (b.X0, mx), (mx, b.X1) } : new[] { (b.X0, b.X1) })
                    {
                        Leaves.Add((x0, x1, y0, y1));
                        Stamp((x0, x1, y0, y1), Leaves.Count - 1);
                        queue.Enqueue(Leaves.Count - 1);
                    }
                foreach (int n in nb) queue.Enqueue(n);
            }
        }

        /// <summary>Distinct neighbouring blocks along one face that share a level with this block.</summary>
        private int FaceCount((int X0, int X1, int Y0, int Y1) b, bool xFace, bool low)
        {
            int count = 0, prev = -1;
            if (xFace)
            {
                int ix = low ? b.X0 - 1 : b.X1;
                if (ix < 0 || ix >= _nx) return 0;
                for (int iy = b.Y0; iy < b.Y1; iy++)
                {
                    int n = BlockAt[iy * _nx + ix];
                    if (n == prev) continue;
                    prev = n;
                    if (SharesLevel(b, Leaves[n]!.Value)) count++;
                }
            }
            else
            {
                int iy = low ? b.Y0 - 1 : b.Y1;
                if (iy < 0 || iy >= _ny) return 0;
                for (int ix = b.X0; ix < b.X1; ix++)
                {
                    int n = BlockAt[iy * _nx + ix];
                    if (n == prev) continue;
                    prev = n;
                    if (SharesLevel(b, Leaves[n]!.Value)) count++;
                }
            }
            return count;
        }

        private bool SharesLevel((int X0, int X1, int Y0, int Y1) a, (int X0, int X1, int Y0, int Y1) b)
        {
            for (int li = 0; li < _levels; li++)
                if (Sum(_hasSum[li], a.X0, a.X1, a.Y0, a.Y1) > 0 && Sum(_hasSum[li], b.X0, b.X1, b.Y0, b.Y1) > 0)
                    return true;
            return false;
        }

        private List<int> Neighbours((int X0, int X1, int Y0, int Y1) b)
        {
            var list = new List<int>();
            int prev = -1;
            for (int iy = b.Y0; iy < b.Y1; iy++)
                foreach (int ix in new[] { b.X0 - 1, b.X1 })
                {
                    if (ix < 0 || ix >= _nx) continue;
                    int n = BlockAt[iy * _nx + ix];
                    if (n != prev) { prev = n; list.Add(n); }
                }
            for (int ix = b.X0; ix < b.X1; ix++)
                foreach (int iy in new[] { b.Y0 - 1, b.Y1 })
                {
                    if (iy < 0 || iy >= _ny) continue;
                    int n = BlockAt[iy * _nx + ix];
                    if (n != prev) { prev = n; list.Add(n); }
                }
            return list;
        }

        private void Stamp((int X0, int X1, int Y0, int Y1) b, int id)
        {
            for (int iy = b.Y0; iy < b.Y1; iy++)
                for (int ix = b.X0; ix < b.X1; ix++) BlockAt[iy * _nx + ix] = id;
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────

        private static List<int> BaseCuts(double[] g, double step)
        {
            var cuts = new List<int> { 0 };
            if (!(step > 0) || !double.IsFinite(step)) step = g[^1] - g[0];
            double next = g[0] + step;
            for (int i = 1; i < g.Length - 1; i++)
                if (g[i] >= next - 1e-15) { cuts.Add(i); next = g[i] + step; }
            cuts.Add(g.Length - 1);
            return cuts;
        }

        private static int Mid(double[] g, int a, int b)
        {
            double m = 0.5 * (g[a] + g[b]);
            int best = a + 1;
            for (int i = a + 1; i < b; i++)
                if (Math.Abs(g[i] - m) < Math.Abs(g[best] - m)) best = i;
            return best;
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

        private int Sum(int[] s, int x0, int x1, int y0, int y1)
            => s[y1 * (_nx + 1) + x1] - s[y0 * (_nx + 1) + x1] - s[y1 * (_nx + 1) + x0] + s[y0 * (_nx + 1) + x0];

        /// <summary>Per interior line: the elementary ranges along it where it is needed — each hard request's own
        /// stretch of the line widened by <paramref name="reach"/> cells.</summary>
        private static List<(int, int)>[] Needs(PlanarGridAxis axis, double[] g, double[] other, int nOther, int reach)
        {
            var need = new List<(int, int)>[g.Length];
            for (int i = 0; i < need.Length; i++) need[i] = [];
            double tol = Math.Max((g[^1] - g[0]) * 1e-9, 1e-15);
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
                need[i].Add((Math.Max(0, lo - reach), Math.Min(nOther - 1, hi + reach)));
            }
            return need;
        }
    }

    /// <summary>The elementary index range [lo, hi] of cells overlapping [a, b], clamped to the grid.</summary>
    private static (int Lo, int Hi) Range(double[] g, double a, double b)
    {
        int n = g.Length - 1;
        if (!(b >= a)) return (1, 0);
        int lo = Array.BinarySearch(g, a); lo = lo < 0 ? ~lo - 1 : lo;
        int hi = Array.BinarySearch(g, b); hi = hi < 0 ? ~hi - 1 : hi - 1;
        return (Math.Clamp(lo, 0, n - 1), Math.Clamp(hi, 0, n - 1));
    }
}
