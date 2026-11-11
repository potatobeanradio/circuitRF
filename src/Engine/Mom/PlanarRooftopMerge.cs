// PM-2 Part 2 — rooftop merging: local coarsening on the tensor grid, by a fixed linear map.
//
// THE PROBLEM IT ANSWERS. The tensor grid makes every gridline cross the whole part, so a fan raised
// by one edge is fine everywhere along its length — also where nothing needs it. PM-1 and PM-2 Part 1
// measured how much of a real mesh that is: 57-59 % of the unknowns on the internal test geometry sit
// in cells more than 4× finer than the size field asks for once each fan is localised to the feature
// that raised it (RESOLVED.md §PM-1 §5, §PM-2 Part 1). 4× rather than Part 1's 2× is measured — see
// SpreadFactor.
//
// THE CONSTRUCTION. The grid and its elemental rooftops are untouched. Along each row, consecutive
// cells are grouped into x-RUNS, each no longer than the localised field allows at any cell it covers,
// and one MACRO-rooftop replaces the x-rooftops between two neighbouring runs: the rooftop on the
// coarse support, a current ramping linearly from the far end of one run to the shared edge and down
// again across the other. Columns are grouped into y-runs the same way for the y-rooftops. It is
// written as a fixed-weight sum of the elemental rooftops it covers, so
//
//   * the merged space is a SUBSPACE of the elemental one — the solve is an ordinary Galerkin
//     restriction, Z_m = Tᵀ Z T, and it can only be less accurate, never inconsistent;
//   * normal current is continuous at every join BY CONSTRUCTION — every macro is a sum of
//     div-conforming elemental rooftops, so there is no line charge at a run boundary to get
//     wrong, which is the trap the brief names;
//   * a run of one cell next to a run of one cell is the elemental rooftop itself, weight 1, so an
//     unmerged region is the identity and a mesh where nothing merges is the elemental system to the
//     bit (gated).
//
// ONE DIRECTION PER RUN, AND THAT IS THE PART THAT WAS WRONG FIRST. The obvious construction groups
// cells into 2-D BLOCKS and puts one macro between each pair of blocks. It is a subspace too, its
// normal current is continuous too, and it is wrong: a block taller than the edge it shares with a
// neighbour carries that neighbour's x-macro on the shared ROWS only, and the y-rooftops between its
// own rows are gone, so a string of 2-row blocks along a line is a SLIT no current can cross. Measured
// on the test geometry: the two output ports' reflections turned 6° (|ΔS22| 0.13) while |S| moved by
// 0.004, and restricting the blocks to grow across the line only did not help. A run merges rooftops
// of ONE direction along that direction — the brief's own "a run of consecutive rooftops along one
// gridline direction" — so every edge keeps a rooftop of its own normal current.
//
// THE WEIGHTS. An elemental x-rooftop on row r at gridline x_k carries unit current across an edge
// of height h_r, so its current density at x_k is 1/h_r, and it is linear in x across each of its two
// cells. A piecewise-linear J(x) with nodes at the gridlines is therefore Σ_k h_r·J(x_k)·f_k exactly —
// linear interpolation by hat functions. The macro's J is (x − Xa)/((X1 − Xa)·H) on the near run
// and (Xb − x)/((Xb − X1)·H) on the far one, H the face's height, so its weight on the rooftop at
// (r, x_k) is h_r·J(x_k); at the face that is h_r/H, and the rows sum to the unit current a rooftop
// carries.
//
// WHAT IS KEPT ELEMENTAL, AND WHY.
//   * A cut cell, a cell that absorbed a sliver, and any cell next to one: their rooftops are not
//     rectangles' and the weights above are written for rectangles.
//   * Every cell within EndRunHeights substrate heights of a port's end face (PlanarCalibration's own
//     end-run length; doubling it was measured to change nothing): a calibration standard reproduces exactly that stretch of the DUT's mesh, and
//     the de-embedding divides any difference between the two by a₂₁² (CLAUDE.md §3.5).
//   * Nothing near the feature that raised a line merges in any case — the localised field there is
//     the fan's own c₀ — which is the brief's "only spread unknowns are candidates".
//
// DENSE ONLY, single level only. PM-3 added the two exclusions SurfaceMesher.BuildRooftopMerge
// applies before this map is built: only rooftops transverse to a measured current direction merge,
// and nothing within CornerZoneWidths of a polygon vertex does (RESOLVED.md §PM-3). The accelerator's near correction would have to be exact between
// macro pairs, and Part 1 measured the accelerator already slower than dense on the artwork this is
// for (RESOLVED.md §PM-2 Part 1 §4). A via mesh is excluded until something measures one.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>PM-2 — the sparse map <c>T</c> from the merged basis to the elemental one</b>: column <c>m</c>
/// is macro-rooftop <c>m</c> as weights on elemental rooftops. Held on <see cref="PlanarMesh.Merge"/>;
/// the dense system is <c>Tᵀ Z T</c> and a solve hands back <c>T·x</c>.
/// </summary>
public sealed class PlanarRooftopMerge
{
    /// <summary>
    /// A cell joins a run only where the localised size field at its centre is more than this many
    /// times the cell's extent along the run. <b>4, and the choice IS delicate, measured on the Patch
    /// Antenna example</b> (worst |ΔS11| against the elemental mesh over 5.3–6.3 GHz at 20 cells/λ):
    /// 0.27 at 2, 1.3e-2 at 3, <b>2.4e-4 at 4</b>. The step is Part 1's §6 finding: the graded marcher
    /// sizes a fan cell by the field at its end nearest the attractor, so the field at its centre reads
    /// 1.8-1.9× the cell, and more once the fan is localised in 2-D — below about 4 the fans
    /// themselves merge. RESOLVED.md §PM-2 Part 2.
    /// </summary>
    public const double SpreadFactor = 4.0;

    /// <summary>
    /// PM-3 — how closely the current must follow the axis PERPENDICULAR to a run's direction for
    /// the run to be allowed: |cos| of the angle between the current and that axis, 30° either side.
    /// A run merges rooftops of one direction along that direction, which is harmless where those
    /// rooftops carry the transverse current and not where they carry the current itself.
    /// </summary>
    public static readonly double TransverseCos = Math.Cos(Math.PI / 6);

    /// <summary>
    /// PM-3 — the radius, in narrowest-conductor widths, of the zone about every polygon vertex in
    /// which nothing merges. One width: the current that turns at a junction, a step or along a
    /// flattened arc settles within about a width of the vertex, and the measured failure (the bends
    /// and the S-jog of the internal test geometry, RESOLVED.md §PM-3 §1) lies entirely inside that
    /// zone while the straight output branches, which merge exactly, lie outside it.
    /// </summary>
    public const double CornerZoneWidths = 1.0;

    /// <summary>Elemental rooftops — the mesh's own basis count.</summary>
    public int ElementalCount { get; }

    /// <summary>Unknowns the dense system holds: one per macro-rooftop.</summary>
    public int Count { get; }

    /// <summary>Runs of more than one cell, x-runs and y-runs together.</summary>
    public int MergedRunCount { get; }

    /// <summary>Cells kept elemental because they lie in a port's calibrated end run.</summary>
    public int PortProtectedCells { get; }

    // T by column (macro) and by row (elemental), both CSR, both sorted.
    internal readonly int[]    ColPtr, ColRow;
    internal readonly double[] ColVal;
    internal readonly int[]    RowPtr, RowCol;
    internal readonly double[] RowVal;

    // Per elemental rooftop: is its edge the FACE of some macro — the one gridline a macro's ramp
    // peaks on, weight 1 — or an interior gridline of a run, carrying a fraction of a neighbour's
    // unknown. PM-3: what the mesh overlay draws, so the picture matches the count.
    private readonly bool[] _face;

    private PlanarRooftopMerge(int elemental, List<List<(int E, double W)>> cols, int mergedRuns,
                               int portProtected)
    {
        ElementalCount = elemental;
        Count = cols.Count;
        MergedRunCount = mergedRuns;
        PortProtectedCells = portProtected;

        ColPtr = new int[Count + 1];
        int nnz = 0;
        for (int m = 0; m < Count; m++) { ColPtr[m] = nnz; nnz += cols[m].Count; }
        ColPtr[Count] = nnz;
        ColRow = new int[nnz]; ColVal = new double[nnz];
        var rowCount = new int[elemental + 1];
        _face = new bool[elemental];
        for (int m = 0; m < Count; m++)
        {
            int o = ColPtr[m];
            foreach (var (e, w) in cols[m])
            {
                ColRow[o] = e; ColVal[o] = w; o++; rowCount[e + 1]++;
                if (Math.Abs(w - 1.0) <= FaceWeightTolerance) { _face[e] = true; }
            }
        }
        RowPtr = new int[elemental + 1];
        for (int e = 0; e < elemental; e++) RowPtr[e + 1] = RowPtr[e] + rowCount[e + 1];
        RowCol = new int[nnz]; RowVal = new double[nnz];
        var fill = (int[])RowPtr.Clone();
        for (int m = 0; m < Count; m++)               // m ascending ⇒ each row's columns sorted
            for (int k = ColPtr[m]; k < ColPtr[m + 1]; k++)
            {
                int e = ColRow[k], o = fill[e]++;
                RowCol[o] = m; RowVal[o] = ColVal[k];
            }
    }

    /// <summary>Non-zeros of <c>T</c>.</summary>
    public int NonZeros => ColRow.Length;

    /// <summary>How close to 1 a macro's weight on an elemental rooftop must be for that rooftop to
    /// count as the macro's face. The face weight is <c>h_r·(1/(H))</c> with <c>h_r = H</c> on a
    /// single-row run, i.e. 1 up to the division's round-off.</summary>
    private const double FaceWeightTolerance = 1e-9;

    /// <summary>
    /// <b>PM-3 — whether elemental rooftop <paramref name="elementalBasis"/>'s edge is still the face
    /// of its own unknown.</b> True for an unmerged rooftop (its identity column has weight 1) and for
    /// the one gridline each macro peaks on; false for an interior gridline of a run, where the
    /// current is a fixed fraction of the neighbouring macros' and nothing is solved for across that
    /// edge. The mesh overlay draws an internal edge only where this is true, so what is drawn is
    /// what is solved; it must not re-derive the merge.
    /// </summary>
    public bool IsSolvedFace(int elementalBasis) => _face[elementalBasis];

    /// <summary>How many elemental rooftops are a face (<see cref="IsSolvedFace"/>) — one per macro
    /// on a single-level mesh, so this equals <see cref="Count"/> there.</summary>
    public int SolvedFaceCount
    {
        get { int n = 0; foreach (bool f in _face) if (f) n++; return n; }
    }

    /// <summary>
    /// <b>Whether merging can be applied to this problem at all</b> — a single conductor level with no
    /// vias on the one-slab kernel (see the header: dense and single-level only). <b>The one spelling
    /// of that question</b>, read by the mesher when it decides whether to build a merge and by the EM
    /// Setup panel when it decides whether the control is live, so the two cannot disagree (P12's
    /// lesson about the accelerated ceiling). The accelerated solve is the other exclusion and is a
    /// setting, not a property of the problem, so it is asked beside this rather than inside it.
    /// </summary>
    public static bool CanApplyTo(PlanarProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.Layers.Count == 1 && problem.ViaList.Count == 0 && !problem.RequiresGeneralKernel;
    }

    /// <summary>Macro <paramref name="macro"/> as (elemental rooftop, weight) pairs, ascending.</summary>
    public IEnumerable<(int Basis, double Weight)> Column(int macro)
    {
        for (int k = ColPtr[macro]; k < ColPtr[macro + 1]; k++) yield return (ColRow[k], ColVal[k]);
    }

    /// <summary><c>Tᵀ v</c> — an elemental right-hand side tested against the merged basis.</summary>
    public Vec<Complex> Restrict(Vec<Complex> elemental)
    {
        var r = new Vec<Complex>(Count);
        for (int m = 0; m < Count; m++)
        {
            Complex s = Complex.Zero;
            for (int k = ColPtr[m]; k < ColPtr[m + 1]; k++) s += ColVal[k] * elemental[ColRow[k]];
            r[m] = s;
        }
        return r;
    }

    /// <summary><c>T x</c> — merged coefficients as the elemental current they describe.</summary>
    public Vec<Complex> Prolong(Vec<Complex> merged)
    {
        var r = new Vec<Complex>(ElementalCount);
        for (int e = 0; e < ElementalCount; e++)
        {
            Complex s = Complex.Zero;
            for (int k = RowPtr[e]; k < RowPtr[e + 1]; k++) s += RowVal[k] * merged[RowCol[k]];
            r[e] = s;
        }
        return r;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Construction
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private readonly record struct Block(int Layer, int X0, int X1, int Y0, int Y1)
    {
        public bool IsSingle => X0 == X1 && Y0 == Y1;
    }

    /// <summary>
    /// Groups each row's cells into x-runs and each column's into y-runs, and builds the
    /// macro-rooftops between neighbouring runs.
    /// </summary>
    /// <param name="targetX">Per cell: the longest x-run it may sit in, in metres — its own width
    /// where x is not spread.</param>
    /// <param name="targetY">The same for y-runs.</param>
    /// <param name="mergeable">Per cell: whether it may join a run of more than one cell.</param>
    public static PlanarRooftopMerge Build(PlanarMesh mesh, IReadOnlyList<double> targetX,
                                           IReadOnlyList<double> targetY, bool[] mergeable,
                                           int portProtected = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        mesh.EnsureNoBlocks("rooftop merging");   // PMS-4 — refused by name until it reads blocks
        var gx = mesh.GridX; var gy = mesh.GridY;
        int nx = gx.Count - 1, ny = gy.Count - 1, layers = mesh.LayerNames.Count;
        int cellCount = mesh.Cells.Count;

        // Position lookups — plain int arrays, R-msh-2 (no dictionary or set iteration on the path).
        var at = new int[layers][];
        var xb = new int[layers][];
        var yb = new int[layers][];
        for (int l = 0; l < layers; l++)
        {
            at[l] = new int[nx * ny]; xb[l] = new int[nx * ny]; yb[l] = new int[nx * ny];
            Array.Fill(at[l], -1); Array.Fill(xb[l], -1); Array.Fill(yb[l], -1);
        }
        for (int c = 0; c < cellCount; c++)
        {
            var cell = mesh.Cells[c];
            at[cell.LayerIndex][cell.IY * nx + cell.IX] = c;
        }
        for (int e = 0; e < mesh.Bases.Count; e++)
        {
            var b = mesh.Bases[e];
            if (b.Direction == PlanarBasisDirection.Z) continue;
            var a = mesh.Cells[b.CellA];
            (b.Direction == PlanarBasisDirection.X ? xb : yb)[b.LayerIndex][a.IY * nx + a.IX] = e;
        }

        // A run grows along its own axis only: the other axis's target is the cell's own extent.
        var width  = mesh.Cells.Select(c => c.Width).ToArray();
        var height = mesh.Cells.Select(c => c.Height).ToArray();
        var ok = (bool[])mergeable.Clone();
        for (int attempt = 0; ; attempt++)
        {
            var (xRuns, xRunOf) = Group(mesh, at, nx, ny, layers, targetX, height, ok);
            var (yRuns, yRunOf) = Group(mesh, at, nx, ny, layers, width, targetY, ok);
            var failed = new List<Block>();
            var cols = Macros(mesh, xRuns, xRunOf, yRuns, yRunOf, xb, yb, nx, failed);
            if (failed.Count == 0 || attempt >= 4)
            {
                if (failed.Count > 0)
                    throw new InvalidOperationException(
                        "Rooftop merging could not express a run boundary in elemental rooftops after " +
                        "four attempts. This is a defect in the merge, not in the design; turn " +
                        "RooftopMerging off to solve the elemental mesh.");
                int merged = xRuns.Count(r => !r.IsSingle) + yRuns.Count(r => !r.IsSingle);
                return new PlanarRooftopMerge(mesh.Bases.Count, cols, merged, portProtected);
            }
            // A boundary that needs an elemental rooftop the mesh does not have: keep the runs on
            // both sides elemental and group again.
            foreach (var bk in failed)
                for (int iy = bk.Y0; iy <= bk.Y1; iy++)
                    for (int ix = bk.X0; ix <= bk.X1; ix++)
                        if (at[bk.Layer][iy * nx + ix] is var c && c >= 0) ok[c] = false;
        }
    }

    /// <summary>Part 1's greedy: grow along x as far as every covered cell's x-target allows, then
    /// along y a whole row segment at a time. A cell that may not merge is its own block. Handed a
    /// target equal to the cells' own extent in one axis, it builds runs along the other.</summary>
    private static (List<Block>, int[]) Group(PlanarMesh mesh, int[][] at, int nx, int ny, int layers,
                                              IReadOnlyList<double> tx, IReadOnlyList<double> ty, bool[] ok)
    {
        var gx = mesh.GridX; var gy = mesh.GridY;
        var blockOf = new int[mesh.Cells.Count];
        Array.Fill(blockOf, -1);
        var blocks = new List<Block>();
        const double slack = 1 + 1e-9;

        for (int l = 0; l < layers; l++)
        for (int iy = 0; iy < ny; iy++)
        for (int ix = 0; ix < nx; ix++)
        {
            int c0 = at[l][iy * nx + ix];
            if (c0 < 0 || blockOf[c0] >= 0) continue;
            int ix2 = ix, iy2 = iy;
            if (ok[c0])
            {
                // EM-TFR — cells of different sheet class never share a run, unconditionally: a run
                // half on film and half on metal would make the resistor's length depend on this decision.
                int film0 = mesh.Cells[c0].Film;
                double maxW = tx[c0], maxH = ty[c0];
                while (ix2 + 1 < nx)
                {
                    int c = at[l][iy * nx + ix2 + 1];
                    if (c < 0 || blockOf[c] >= 0 || !ok[c] || mesh.Cells[c].Film != film0) break;
                    double mw = Math.Min(maxW, tx[c]), mh = Math.Min(maxH, ty[c]);
                    if (gx[ix2 + 2] - gx[ix] > mw * slack || gy[iy + 1] - gy[iy] > mh * slack) break;
                    maxW = mw; maxH = mh; ix2++;
                }
                while (iy2 + 1 < ny)
                {
                    double mw = maxW, mh = maxH; bool fits = true;
                    for (int k = ix; k <= ix2; k++)
                    {
                        int c = at[l][(iy2 + 1) * nx + k];
                        if (c < 0 || blockOf[c] >= 0 || !ok[c] || mesh.Cells[c].Film != film0) { fits = false; break; }
                        mw = Math.Min(mw, tx[c]); mh = Math.Min(mh, ty[c]);
                    }
                    if (!fits || gy[iy2 + 2] - gy[iy] > mh * slack || gx[ix2 + 1] - gx[ix] > mw * slack) break;
                    maxW = mw; maxH = mh; iy2++;
                }
            }
            int id = blocks.Count;
            blocks.Add(new Block(l, ix, ix2, iy, iy2));
            for (int j = iy; j <= iy2; j++)
                for (int k = ix; k <= ix2; k++) blockOf[at[l][j * nx + k]] = id;
        }
        return (blocks, blockOf);
    }

    /// <summary>One macro per pair of neighbouring runs, in order of the first elemental rooftop
    /// across their shared edge — x-rooftops between x-runs, y-rooftops between y-runs; vertical
    /// rooftops follow as identity columns (R-via-5's horizontal prefix). A pair whose weights need an
    /// elemental rooftop the mesh lacks is reported in <paramref name="failed"/>. The weights are
    /// written for a block of any shape; a run is the case where the shared edge is a whole side.</summary>
    private static List<List<(int, double)>> Macros(PlanarMesh mesh, List<Block> xRuns, int[] xRunOf,
                                                    List<Block> yRuns, int[] yRunOf,
                                                    int[][] xb, int[][] yb, int nx, List<Block> failed)
    {
        var gx = mesh.GridX; var gy = mesh.GridY;
        var cols = new List<List<(int, double)>>();
        var seen = new HashSet<(int, int, int)>();       // membership only, never iterated
        var vertical = new List<int>();

        for (int e = 0; e < mesh.Bases.Count; e++)
        {
            var b = mesh.Bases[e];
            if (b.Direction == PlanarBasisDirection.Z) { vertical.Add(e); continue; }
            bool alongX = b.Direction == PlanarBasisDirection.X;
            var (blocks, blockOf) = alongX ? (xRuns, xRunOf) : (yRuns, yRunOf);
            int pa = blockOf[b.CellA], qb = blockOf[b.CellB];
            if (pa == qb) continue;
            if (!seen.Add((pa, qb, alongX ? 0 : 1))) continue;

            var p = blocks[pa]; var q = blocks[qb];
            if (p.IsSingle && q.IsSingle) { cols.Add([(e, 1.0)]); continue; }

            var col = new List<(int, double)>();
            bool missing = false;
            if (alongX)
            {
                int rLo = Math.Max(p.Y0, q.Y0), rHi = Math.Min(p.Y1, q.Y1);
                double xa = gx[p.X0], x1 = gx[p.X1 + 1], xbEnd = gx[q.X1 + 1];
                double hFace = gy[rHi + 1] - gy[rLo];
                for (int r = rLo; r <= rHi && !missing; r++)
                {
                    double h = gy[r + 1] - gy[r];
                    for (int k = p.X0 + 1; k <= q.X1; k++)
                    {
                        int ei = xb[p.Layer][r * nx + (k - 1)];
                        if (ei < 0) { missing = true; break; }
                        double xk = gx[k];
                        double j = k <= p.X1 + 1 ? (xk - xa) / ((x1 - xa) * hFace)
                                                 : (xbEnd - xk) / ((xbEnd - x1) * hFace);
                        col.Add((ei, h * j));
                    }
                }
            }
            else
            {
                int cLo = Math.Max(p.X0, q.X0), cHi = Math.Min(p.X1, q.X1);
                double ya = gy[p.Y0], y1 = gy[p.Y1 + 1], ybEnd = gy[q.Y1 + 1];
                double wFace = gx[cHi + 1] - gx[cLo];
                for (int c = cLo; c <= cHi && !missing; c++)
                {
                    double w = gx[c + 1] - gx[c];
                    for (int k = p.Y0 + 1; k <= q.Y1; k++)
                    {
                        int ei = yb[p.Layer][(k - 1) * nx + c];
                        if (ei < 0) { missing = true; break; }
                        double yk = gy[k];
                        double j = k <= p.Y1 + 1 ? (yk - ya) / ((y1 - ya) * wFace)
                                                 : (ybEnd - yk) / ((ybEnd - y1) * wFace);
                        col.Add((ei, w * j));
                    }
                }
            }
            if (missing) { if (!p.IsSingle) failed.Add(p); if (!q.IsSingle) failed.Add(q); continue; }
            col.Sort(static (u, v) => u.Item1.CompareTo(v.Item1));
            cols.Add(col);
        }
        foreach (int e in vertical) cols.Add([(e, 1.0)]);
        return cols;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // What the mesher hands in: the localised size field and the port end runs
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The size field with each attractor's fan localised to the feature that raised it</b> —
    /// PM-1 Part 2's and PM-2 Part 1's classifier, which is what the measured counts are counts of.
    /// <c>h(p) = min(bulk, minᵢ c₀ᵢ + gᵢ·dᵢ(p))</c>, with dᵢ the 2-D distance to every axis-parallel
    /// conductor edge lying on attractor i's gridline (and every vertex on it, for a corner fan).
    /// The 1-D tensor field uses the distance ACROSS the gridline only, which is why a fan line is
    /// fine along its whole length.
    /// </summary>
    internal sealed class LocalSizeField
    {
        private readonly record struct Attractor(double C0, double G, (double Ax, double Ay, double Bx, double By)[] Segs);

        private readonly Attractor[] _x, _y;
        private readonly double _hMaxX, _hMaxY;
        private readonly Func<double, double>? _bulkX, _bulkY;

        internal LocalSizeField(PlanarProblem problem, IReadOnlyList<GradedAttractor> gradedX,
                                IReadOnlyList<GradedAttractor> gradedY, double hMaxX, double hMaxY,
                                Func<double, double>? bulkX, Func<double, double>? bulkY)
        {
            _hMaxX = hMaxX; _hMaxY = hMaxY; _bulkX = bulkX; _bulkY = bulkY;
            var (x0, y0, x1, y1) = problem.Bounds();
            double tolX = (x1 - x0) * 1e-9, tolY = (y1 - y0) * 1e-9;
            var rings = new List<IReadOnlyList<EmPoint>>();
            foreach (var l in problem.Layers)
                foreach (var p in l.Polygons) { rings.Add(p.Outer); foreach (var h in p.HoleRings) rings.Add(h); }
            _x = [.. gradedX.Where(a => a.C0 > 0).Select(a => new Attractor(a.C0, a.Growth, Feature(rings, a.Coord, tolX, true)))];
            _y = [.. gradedY.Where(a => a.C0 > 0).Select(a => new Attractor(a.C0, a.Growth, Feature(rings, a.Coord, tolY, false)))];
        }

        private static (double, double, double, double)[] Feature(List<IReadOnlyList<EmPoint>> rings,
                                                                   double at, double tol, bool vertical)
        {
            var segs = new List<(double, double, double, double)>();
            foreach (var r in rings)
                for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
                {
                    var a = r[j]; var b = r[i];
                    double ca = vertical ? a.X : a.Y, cb = vertical ? b.X : b.Y;
                    if (Math.Abs(ca - at) <= tol && Math.Abs(cb - at) <= tol) segs.Add((a.X, a.Y, b.X, b.Y));
                    else if (Math.Abs(cb - at) <= tol) segs.Add((b.X, b.Y, b.X, b.Y));
                }
            // An attractor on no drawn edge (none is expected) keeps its whole gridline.
            if (segs.Count == 0) segs.Add(vertical ? (at, -1e9, at, 1e9) : (-1e9, at, 1e9, at));
            return [.. segs];
        }

        private static double Dist(double px, double py, (double Ax, double Ay, double Bx, double By) s)
        {
            double dx = s.Bx - s.Ax, dy = s.By - s.Ay, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((px - s.Ax) * dx + (py - s.Ay) * dy) / l2, 0, 1) : 0;
            double qx = s.Ax + t * dx - px, qy = s.Ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        private static double Min(double h, Attractor[] atts, double x, double y)
        {
            foreach (var a in atts)
            {
                double d = double.MaxValue;
                foreach (var s in a.Segs) d = Math.Min(d, Dist(x, y, s));
                h = Math.Min(h, a.C0 + a.G * d);
            }
            return h;
        }

        internal double X(double x, double y) => Min(_bulkX is null ? _hMaxX : Math.Min(_hMaxX, _bulkX(x)), _x, x, y);
        internal double Y(double x, double y) => Min(_bulkY is null ? _hMaxY : Math.Min(_hMaxY, _bulkY(y)), _y, x, y);
    }

    /// <summary>
    /// Marks every cell within <paramref name="radiusM"/> of a port's end face as not mergeable and
    /// returns how many were newly marked. An edge port's face is found from the mesh — walking
    /// outward from the label through metal, so a feed lead grown onto the port is included — and
    /// the run is the face's own transverse extent widened by the radius. Anything else (an internal
    /// port, a label the walk cannot place) protects a square of that half-size about the label.
    /// </summary>
    internal static int ProtectPorts(PlanarMesh mesh, IReadOnlyList<PlanarPort>? ports, double radiusM,
                                     bool[] mergeable)
    {
        if (ports is null || ports.Count == 0) return 0;
        var gx = mesh.GridX; var gy = mesh.GridY;
        int nx = gx.Count - 1, ny = gy.Count - 1;
        int layers = mesh.LayerNames.Count;
        var at = new int[layers][];
        for (int l = 0; l < layers; l++) { at[l] = new int[nx * ny]; Array.Fill(at[l], -1); }
        foreach (var (c, i) in mesh.Cells.Select((c, i) => (c, i))) at[c.LayerIndex][c.IY * nx + c.IX] = i;

        int Index(IReadOnlyList<double> g, double v)
        {
            int lo = 0, hi = g.Count - 2;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (g[mid] <= v) lo = mid; else hi = mid - 1; }
            return lo;
        }

        int marked = 0;
        void Box(double xLo, double xHi, double yLo, double yHi)
        {
            for (int c = 0; c < mesh.Cells.Count; c++)
            {
                var cell = mesh.Cells[c];
                if (!mergeable[c]) continue;
                if (cell.XMax < xLo || cell.XMin > xHi || cell.YMax < yLo || cell.YMin > yHi) continue;
                mergeable[c] = false; marked++;
            }
        }

        foreach (var port in ports)
        {
            double px = port.Location.X, py = port.Location.Y;
            bool alongX = port.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            int outward = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY ? -1 : 1;
            int layer = Math.Clamp(port.LayerIndex ?? 0, 0, layers - 1);

            bool placed = false;
            if (port.Kind == PlanarPortKind.Edge)
            {
                int ix = Index(gx, px), iy = Index(gy, py);
                // The label sits on the metal, possibly on its very edge: look one cell either side.
                int sx = -1, sy = -1;
                for (int d = 0; d <= 2 && sx < 0; d++)
                    foreach (int s in d == 0 ? [0] : new[] { -d, d })
                    {
                        int jx = alongX ? ix + s : ix, jy = alongX ? iy : iy + s;
                        if (jx < 0 || jx >= nx || jy < 0 || jy >= ny) continue;
                        if (at[layer][jy * nx + jx] >= 0) { sx = jx; sy = jy; break; }
                    }
                if (sx >= 0)
                {
                    int f = alongX ? sx : sy, n = alongX ? nx : ny;
                    bool Metal(int a, int t) => alongX ? at[layer][t * nx + a] >= 0 : at[layer][a * nx + t] >= 0;
                    int t0 = alongX ? sy : sx, tn = alongX ? ny : nx;
                    while (f + outward >= 0 && f + outward < n && Metal(f + outward, t0)) f += outward;
                    int tLo = t0, tHi = t0;
                    while (tLo - 1 >= 0 && Metal(f, tLo - 1)) tLo--;
                    while (tHi + 1 < tn && Metal(f, tHi + 1)) tHi++;
                    var ga = alongX ? gx : gy; var gt = alongX ? gy : gx;
                    double face = outward < 0 ? ga[f] : ga[f + 1];
                    double aLo = outward < 0 ? face : face - radiusM, aHi = outward < 0 ? face + radiusM : face;
                    double bLo = gt[tLo] - radiusM, bHi = gt[tHi + 1] + radiusM;
                    if (alongX) Box(aLo, aHi, bLo, bHi); else Box(bLo, bHi, aLo, aHi);
                    placed = true;
                }
            }
            if (!placed) Box(px - radiusM, px + radiusM, py - radiusM, py + radiusM);
        }
        return marked;
    }
}
