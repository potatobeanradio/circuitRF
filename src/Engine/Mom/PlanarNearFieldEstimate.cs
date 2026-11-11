// EMB-4 R-emb4-5(a) and R-emb4-2 — what the accelerated solve's NEAR FIELD will cost, said before anything is filled.
//
// The accelerated operators (PlanarAimOperator, PlanarBorderedAimOperator) hold the near field exactly and precondition
// GMRES with its exact sparse LU (FactorNear). That factor is one CSparse SparseLU with an AMD ordering, single-threaded,
// and its cost is set by the near set's PATTERN, not by N: P8 measured 5 s at 951 near entries per row and no answer
// after 8 minutes at 1,246, and on an imported board whose bulk cells are large the near radius (six of the LARGEST
// basis supports, floored at 2h) covers most of the board, so the "sparse" near matrix is close to dense.
//
// So the estimate is not a fit. It builds the near set exactly as AimProjection.NearSet does (same centres, same radius,
// same stencil-overlap rule), orders it with the same AMD the factor uses, and counts the factor's column counts on the
// elimination tree — the symbolic half of a Cholesky factorisation, no numbers. On a near matrix whose pivots stay on the
// diagonal that IS the LU's fill: measured on synthetic near patterns of 3,120 to 19,800 unknowns, 2·Σc − n equalled
// SparseLU.NonZerosCount exactly every time (src/Engine/Mom/RESOLVED.md §EMB-4). The factor's WORK is Σc², and the time
// is that over a measured rate. Only the pivoting is not modelled, and pivoting can only add fill.

using CSparse;
using CSparse.Ordering;
using CSparse.Storage;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>EMB-4 — the accelerated solve's near field and its factor, estimated from the mesh alone.</b> See the file header.
/// </summary>
/// <param name="HorizontalUnknowns">N_h — the accelerated (near-field) block's size.</param>
/// <param name="VerticalUnknowns">N_z — the dense border's width (vias).</param>
/// <param name="Sheets">How many metal levels carry horizontal bases.</param>
/// <param name="NearRadiusM">The near radius, max(NearRadiusFactor·largest support, floor).</param>
/// <param name="NearRadiusFloorM">The floor (2h on the shipped settings).</param>
/// <param name="LargestSupportM">The largest basis support the radius is a multiple of.</param>
/// <param name="NearEntries">The near matrix's non-zeros, counted exactly as the operator's near set is built.</param>
/// <param name="FactorNonZeros">L and U together, from the symbolic analysis; null when the near set was too large to
/// analyse (<see cref="PlanarNearFieldEstimate.AnalysedNearEntriesCap"/>) — it is then at least <see cref="NearEntries"/>.</param>
/// <param name="FactorWork">Σ c_j² over the factor's columns — the work the factor does, in the unit the measured
/// rate is in; null with <see cref="FactorNonZeros"/>.</param>
/// <param name="GridNodes">The auxiliary grid's nodes (Nx·Ny) and its padded FFT size (Px·Py).</param>
public sealed record PlanarNearFieldEstimate(
    int    HorizontalUnknowns,
    int    VerticalUnknowns,
    int    Sheets,
    double NearRadiusM,
    double NearRadiusFloorM,
    double LargestSupportM,
    long   NearEntries,
    long?  FactorNonZeros,
    double? FactorWork,
    (long Nodes, long Padded) GridNodes,
    int    RowsCounted = 0)
{
    /// <summary>
    /// <b>PMS-1 R-pms1-6 — the count stopped past <see cref="AnalysedNearEntriesCap"/></b>, so
    /// <see cref="NearEntries"/> is a LOWER BOUND over the first <see cref="RowsCounted"/> rows. Past the cap nothing is
    /// analysed anyway, and on a chip-scale mesh counting the rest cost 58 s to report 89.6·10⁹ entries the sentence
    /// already says are too many. Rows are counted in fixed blocks, so where it stops does not depend on the schedule.
    /// </summary>
    public bool CountStopped => RowsCounted > 0 && RowsCounted < HorizontalUnknowns;

    /// <summary>PMS-1 — rows per block of the near count; the stop is decided between blocks.</summary>
    public const int CountBlockRows = 4096;

    /// <summary>
    /// The factor's measured rate, in Σc² per second: CSparse's complex SparseLU with AMD, one core, Release, on
    /// synthetic near patterns of 3,120 to 19,800 unknowns and 24 to 214 entries per row — 5.4e8 to 7.5e8, flat from
    /// 2.5e8 of work upward (src/Engine/Mom/RESOLVED.md §EMB-4). The larger figure is used, so a time estimated from it
    /// errs short. A Debug build is slower.
    /// </summary>
    public const double MeasuredFactorWorkPerSecond = 7.5e8;

    /// <summary>
    /// Past this estimated factor time the estimate is a WARNING (<see cref="Large"/>): a minute per frequency point on
    /// one core, before GMRES has run once — at ~41 points that is most of an hour spent factoring.
    /// </summary>
    public const double LargeFactorSeconds = 60;

    /// <summary>
    /// Above this many near entries the pattern is not built for the symbolic analysis (it is 4 bytes an entry, and the
    /// ordering forms A + Aᵀ beside it): P8 measured a near matrix of 17.4 million entries whose factor had not returned
    /// after 8 minutes, so a near set this size is <see cref="Large"/> on its count alone.
    /// </summary>
    public const long AnalysedNearEntriesCap = 20_000_000;

    /// <summary>Near entries per horizontal row.</summary>
    public double EntriesPerRow => (double)NearEntries / Math.Max(1, CountStopped ? RowsCounted : HorizontalUnknowns);

    /// <summary>The near matrix's density, near entries over N_h².</summary>
    public double NearDensity => CountStopped
        ? EntriesPerRow / Math.Max(1.0, HorizontalUnknowns)
        : (double)NearEntries / Math.Max(1.0, (double)HorizontalUnknowns * HorizontalUnknowns);

    /// <summary>Whether the radius is the floor rather than the support multiple.</summary>
    public bool NearRadiusIsFloored => NearRadiusFloorM >= NearRadiusM && NearRadiusFloorM > 0;

    /// <summary>The factor's time on one core at <see cref="MeasuredFactorWorkPerSecond"/>; null when not analysed.</summary>
    public double? FactorSeconds => FactorWork is { } w ? w / MeasuredFactorWorkPerSecond : null;

    /// <summary>The factor's bytes as the operator holds them (20 B a non-zero: a complex value and its row index).</summary>
    public long FactorBytes => 20L * (FactorNonZeros ?? NearEntries);

    /// <summary>
    /// <b>R-emb4-2 — what the bordered accelerated operator holds at one frequency</b>, on
    /// <see cref="PlanarBorderedAimReport.ResidentBytes"/>' own terms: the near correction, the grid kernel tables and
    /// their transforms per level pairing, the product's scratch, the factor, the dense border and the stencils. It leaves
    /// out the class-keyed core store, which is small beside these; with an unanalysed factor it is a lower bound.
    /// </summary>
    public long ResidentBytes
    {
        get
        {
            long pairings = (long)Sheets * (Sheets + 1) / 2;
            long side = PlanarAimSettings.Default.ProjectionOrder + 1;
            return 16L * NearEntries
                 + 16L * GridNodes.Nodes * 2 * pairings
                 + 16L * GridNodes.Padded * 2 * pairings
                 + 16L * GridNodes.Padded * (3L * Sheets + 3)
                 + FactorBytes + 8L * (HorizontalUnknowns + 1)
                 + 16L * HorizontalUnknowns * VerticalUnknowns + 32L * VerticalUnknowns * VerticalUnknowns
                 + 16L * side * side * HorizontalUnknowns + 4L * NearEntries + 4L * (HorizontalUnknowns + 1);
        }
    }

    /// <summary>Past <see cref="LargeFactorSeconds"/>, or too large to analyse at all.</summary>
    public bool Large => FactorSeconds is { } s ? s > LargeFactorSeconds : NearEntries > AnalysedNearEntriesCap;

    /// <summary>
    /// The one sentence <c>check</c>, the run and the panel say about it: the near set, the radius and what set it, the
    /// factor's fill and time, and — when <see cref="Large"/> — what acts on it.
    /// </summary>
    public string Describe(SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        var fmt = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        var c = System.Globalization.CultureInfo.CurrentCulture;
        string radius = NearRadiusIsFloored
            ? $"{fmt(NearRadiusM)} (the floor of twice the substrate height)"
            : $"{fmt(NearRadiusM)} (six of the largest basis support, {fmt(LargestSupportM)})";
        string head = CountStopped
            ? $"Accelerated solve: the near field holds more than {NearEntries.ToString("N0", c)} entries — about " +
              $"{EntriesPerRow.ToString("N0", c)} per row ({NearDensity.ToString("P1", c)} dense) over the first " +
              $"{RowsCounted.ToString("N0", c)} of {HorizontalUnknowns.ToString("N0", c)} unknowns, where the count " +
              $"stopped — within a near radius of {radius}. "
            : $"Accelerated solve: the near field holds {NearEntries.ToString("N0", c)} entries, about " +
              $"{EntriesPerRow.ToString("N0", c)} per row ({NearDensity.ToString("P1", c)} dense) over " +
              $"{HorizontalUnknowns.ToString("N0", c)} unknowns, within a near radius of {radius}. ";
        string factor = FactorNonZeros is { } nnz
            ? $"Its exact sparse factor, the solve's preconditioner, is estimated at {nnz.ToString("N0", c)} non-zeros " +
              $"({Size(FactorBytes)}) and {Duration(FactorSeconds!.Value)} per frequency point on one core, estimated."
            : $"The near set is too large to analyse before the run; its exact sparse factor, the solve's " +
              $"preconditioner, holds at least {Size(FactorBytes)}, and a near set of " +
              "17 million entries was measured not to factor in 8 minutes.";
        if (!Large) return head + factor;
        string remedy = NearRadiusIsFloored
            ? " The radius here is set by the substrate, so a coarser mesh or a smaller analysed region acts on it."
            : " The radius is six of the LARGEST basis support, so the largest bulk cells set it: more Cells per " +
              "wavelength makes them smaller and shrinks it (at more unknowns), and a smaller analysed region shrinks " +
              "both. With the accelerated solve off, the dense path holds the whole matrix instead.";
        return head + factor + " That factor is single-threaded and runs before the first GMRES iteration at every " +
               "frequency point." + remedy;
    }

    private static string Duration(double s)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        return s < 1 ? "under a second"
             : s < 90 ? $"about {s.ToString("0", c)} s"
             : s < 90 * 60 ? $"about {(s / 60).ToString("0", c)} min"
             : $"about {(s / 3600).ToString("0.0", c)} h";
    }

    /// <summary>Bytes as the memory sentences give them (<see cref="SurfaceMesher.FormatBytes"/>, decimal GB), in MB
    /// below a tenth of a gigabyte, where "0.0 GB" would say nothing.</summary>
    public static string Size(long bytes)
        => bytes < 100_000_000
            ? $"{Math.Max(1, Math.Round(bytes / 1e6)).ToString("0", System.Globalization.CultureInfo.CurrentCulture)} MB"
            : SurfaceMesher.FormatBytes(bytes);

    /// <summary>
    /// <b>The estimate for <paramref name="mesh"/></b> under <paramref name="aim"/> (null = the shipped settings) over a
    /// slab of <paramref name="slabHeightM"/> — the same three inputs <see cref="PlanarAimGeometry.Build"/> takes. Builds
    /// no cores and fills nothing: O(near entries) to count, plus an AMD ordering and an elimination-tree count when the
    /// near set is under <see cref="AnalysedNearEntriesCap"/>.
    /// </summary>
    public static PlanarNearFieldEstimate For(PlanarMesh mesh, double slabHeightM, PlanarAimSettings? aim = null,
                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        mesh.EnsureNoBlocks("the accelerated solve's estimate");   // PMS-4 — refused by name until it reads blocks
        var st = aim ?? PlanarAimSettings.Default;
        var bases = mesh.Bases;
        int nh = 0;
        while (nh < bases.Count && bases[nh].Direction != PlanarBasisDirection.Z) nh++;
        int nz = bases.Count - nh;
        var layers = new HashSet<int>();
        for (int i = 0; i < nh; i++) layers.Add(bases[i].LayerIndex);

        // ── PlanarAimGeometry's own construction, without the projection ─────────────────────────
        var centres = new (double X, double Y)[nh];
        var spans = new double[nh];
        double maxSpan = 0;
        for (int i = 0; i < nh; i++)
        {
            var b = bases[i];
            var ca = mesh.Cells[b.CellA];
            var cb = mesh.Cells[b.CellB];
            double xa = Math.Min(ca.XMin, cb.XMin), xb = Math.Max(ca.XMax, cb.XMax);
            double ya = Math.Min(ca.YMin, cb.YMin), yb = Math.Max(ca.YMax, cb.YMax);
            centres[i] = (0.5 * (xa + xb), 0.5 * (ya + yb));
            spans[i] = Math.Max(xb - xa, yb - ya);
            maxSpan = Math.Max(maxSpan, spans[i]);
        }
        if (!(maxSpan > 0)) maxSpan = 1.0;
        int m = st.ProjectionOrder;
        double h = st.GridSpacingFactor * maxSpan;
        double floor = slabHeightM > 0 ? st.NearRadiusFloorFor(slabHeightM) : 0;
        double radius = Math.Max(st.NearRadiusFactor * maxSpan, floor);

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var cell in mesh.Cells)
        {
            x0 = Math.Min(x0, cell.XMin); y0 = Math.Min(y0, cell.YMin);
            x1 = Math.Max(x1, cell.XMax); y1 = Math.Max(y1, cell.YMax);
        }
        double pad = (m + 1) * h, gx0 = x0 - pad, gy0 = y0 - pad;
        int nx = (int)Math.Ceiling((x1 + pad - gx0) / h) + 1;
        int ny = (int)Math.Ceiling((y1 + pad - gy0) / h) + 1;
        long padded = (long)PlanarAimGeometry.NextPow2(2 * nx) * PlanarAimGeometry.NextPow2(2 * ny);
        var p0 = new int[nh];
        var q0 = new int[nh];
        for (int i = 0; i < nh; i++)
        {
            p0[i] = Math.Clamp((int)Math.Round((centres[i].X - gx0) / h - 0.5 * m), 0, nx - 1 - m);
            q0[i] = Math.Clamp((int)Math.Round((centres[i].Y - gy0) / h - 0.5 * m), 0, ny - 1 - m);
        }

        var near = new NearPattern(nh, m, centres, spans, p0, q0, radius, h);
        var (entries, rowsCounted) = near.Count(AnalysedNearEntriesCap, ct);

        long? factorNnz = null;
        double? work = null;
        if (nh > 0 && entries <= AnalysedNearEntriesCap)
        {
            var (colPtr, rowIdx) = near.Build(entries, ct);
            (factorNnz, work) = SymbolicFactor(nh, colPtr, rowIdx, ct);
        }

        return new PlanarNearFieldEstimate(nh, nz, layers.Count, radius, floor, maxSpan, entries, factorNnz, work,
                                           ((long)nx * ny, padded), rowsCounted);
    }

    /// <summary>
    /// L+U non-zeros (2·Σc − n) and the work Σc² of the factor of a symmetric pattern under CSparse's own AMD ordering:
    /// the elimination tree of the permuted upper triangle, then each row's subtree walked once to count the columns. The
    /// LU with its pivots on the diagonal has exactly this pattern (src/Engine/Mom/RESOLVED.md §EMB-4).
    /// </summary>
    internal static (long FactorNonZeros, double Work) SymbolicFactor(int n, int[] colPtr, int[] rowIdx,
                                                                       CancellationToken ct = default)
    {
        var pattern = new SymbolicColumnStorage(n, n, colPtr, rowIdx, false);
        var perm = AMD.Generate(pattern, ColumnOrdering.MinimumDegreeAtPlusA) ?? Enumerable.Range(0, n).ToArray();
        ct.ThrowIfCancellationRequested();
        var pinv = new int[n];
        for (int k = 0; k < n; k++) pinv[perm[k]] = k;

        // The permuted pattern's upper triangle, by column: column k holds the rows i < k.
        var start = new int[n + 1];
        for (int j = 0; j < n; j++)
            for (int p = colPtr[j]; p < colPtr[j + 1]; p++)
                if (pinv[rowIdx[p]] < pinv[j]) start[pinv[j] + 1]++;
        for (int k = 0; k < n; k++) start[k + 1] += start[k];
        var upper = new int[start[n]];
        var next = (int[])start.Clone();
        for (int j = 0; j < n; j++)
            for (int p = colPtr[j]; p < colPtr[j + 1]; p++)
                if (pinv[rowIdx[p]] < pinv[j]) upper[next[pinv[j]]++] = pinv[rowIdx[p]];

        // The elimination tree (path compression through `ancestor`).
        var parent = new int[n];
        var ancestor = new int[n];
        for (int k = 0; k < n; k++)
        {
            parent[k] = -1; ancestor[k] = -1;
            for (int p = start[k]; p < start[k + 1]; p++)
                for (int i = upper[p]; i != -1 && i < k;)
                {
                    int up = ancestor[i];
                    ancestor[i] = k;
                    if (up == -1) parent[i] = k;
                    i = up;
                }
        }

        // Row k of L is the union of the tree paths from each of its entries up to k: walk each once.
        var column = new long[n];
        var mark = new int[n];
        Array.Fill(mark, -1);
        for (int k = 0; k < n; k++)
        {
            if ((k & 1023) == 0) ct.ThrowIfCancellationRequested();
            mark[k] = k;
            column[k]++;
            for (int p = start[k]; p < start[k + 1]; p++)
                for (int i = upper[p]; mark[i] != k; i = parent[i])
                {
                    mark[i] = k;
                    column[i]++;
                }
        }
        long sum = 0;
        double work = 0;
        foreach (long c in column) { sum += c; work += (double)c * c; }
        return (2 * sum - n, work);
    }

    /// <summary>
    /// <see cref="AimProjection.NearSet"/>'s criterion, verbatim — within the radius, or stencils overlapping — in a
    /// form that can COUNT without holding a list per row, and build the pattern once the count says it is affordable.
    /// </summary>
    private sealed class NearPattern
    {
        private readonly int _n, _m;
        private readonly (double X, double Y)[] _c;
        private readonly int[] _p0, _q0;
        private readonly double _r2, _search;
        private readonly Dictionary<(int, int), List<int>> _buckets = [];

        public NearPattern(int n, int m, (double X, double Y)[] centres, double[] spans, int[] p0, int[] q0,
                           double radius, double h)
        {
            _n = n; _m = m; _c = centres; _p0 = p0; _q0 = q0;
            _r2 = radius * radius;
            double stencilReach = (m + 1.5) * h;
            double search = Math.Max(radius, stencilReach * 1.5);
            foreach (double s in spans) search = Math.Max(search, s);
            _search = search;
            for (int i = 0; i < n; i++)
            {
                var key = Key(i);
                if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = [];
                list.Add(i);
            }
        }

        private (int, int) Key(int i) => ((int)Math.Floor(_c[i].X / _search), (int)Math.Floor(_c[i].Y / _search));

        private void Row(int i, Action<int> near)
        {
            var (bx, by) = Key(i);
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (!_buckets.TryGetValue((bx + ox, by + oy), out var list)) continue;
                    foreach (int j in list)
                    {
                        double dx = _c[i].X - _c[j].X, dy = _c[i].Y - _c[j].Y;
                        if (dx * dx + dy * dy <= _r2
                            || Math.Abs(_p0[i] - _p0[j]) <= _m && Math.Abs(_q0[i] - _q0[j]) <= _m)
                            near(j);
                    }
                }
        }

        /// <summary>The entries, counted in blocks of <see cref="CountBlockRows"/> rows and stopped after the first block
        /// that takes the total past <paramref name="cap"/> — and the rows counted (all of them when it did not stop).</summary>
        public (long Entries, int Rows) Count(long cap, CancellationToken ct)
        {
            long total = 0;
            var options = new ParallelOptions { CancellationToken = ct };
            for (int b0 = 0; b0 < _n; b0 += CountBlockRows)
            {
                int b1 = Math.Min(_n, b0 + CountBlockRows);
                Parallel.For(b0, b1, options, () => 0L, (i, _, local) =>
                {
                    long k = 0;
                    Row(i, _ => k++);
                    return local + k;
                }, local => Interlocked.Add(ref total, local));
                if (total > cap) return (total, b1);
            }
            return (total, _n);
        }

        /// <summary>The pattern as CSC (it is symmetric, so rows and columns are one), each column's rows in the
        /// bucket order NearSet visits them.</summary>
        public (int[] ColPtr, int[] RowIdx) Build(long entries, CancellationToken ct)
        {
            var counts = new int[_n];
            Parallel.For(0, _n, new ParallelOptions { CancellationToken = ct }, i =>
            {
                int k = 0;
                Row(i, _ => k++);
                counts[i] = k;
            });
            var colPtr = new int[_n + 1];
            for (int i = 0; i < _n; i++) colPtr[i + 1] = colPtr[i] + counts[i];
            var rowIdx = new int[entries];
            Parallel.For(0, _n, new ParallelOptions { CancellationToken = ct }, i =>
            {
                int at = colPtr[i];
                Row(i, j => rowIdx[at++] = j);
            });
            return (colPtr, rowIdx);
        }
    }
}

/// <summary>
/// <b>The accelerated operators' near-field factor, in ONE place</b> — <see cref="PlanarAimOperator"/>'s and
/// <see cref="PlanarBorderedAimOperator"/>'s FactorNear were two copies of these lines. EMB-4 R-emb4-5(b): it reports
/// its progress as COLUMNS done of the total, from CSparse's own progress (which it gives as a fraction, a hundred times
/// a factor), and always reports the last column — so a run's stage can say what it is doing for the minutes a large
/// factor takes instead of sitting on "solving the structure".
/// </summary>
public static class PlanarNearFactor
{
    /// <summary>Factors the CSR near matrix (<paramref name="rowPtr"/>, <paramref name="colIdx"/>,
    /// <paramref name="values"/>) with CSparse's SparseLU over an AMD ordering. A failed factorisation returns a null
    /// factor (GMRES then runs unpreconditioned) rather than throwing; running out of memory still throws.</summary>
    /// <param name="progress">Columns done and the total, or null.</param>
    /// <returns>The factor, the near matrix's non-zeros, and the factor's (L and U together; 0 on failure).</returns>
    public static (CSparse.Complex.Factorization.SparseLU? Lu, long Nnz, long FactorNnz) Factor(
        int n, int[] rowPtr, int[] colIdx, System.Numerics.Complex[] values, Action<long, long>? progress = null)
    {
        var tri = new CoordinateStorage<System.Numerics.Complex>(n, n, Math.Max(1, colIdx.Length));
        for (int i = 0; i < n; i++)
            for (int k = rowPtr[i]; k < rowPtr[i + 1]; k++)
                tri.At(i, colIdx[k], values[k]);
        var csc = CSparse.Complex.SparseMatrix.OfIndexed(tri);
        try
        {
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtPlusA);
            var lu = progress is null
                ? CSparse.Complex.Factorization.SparseLU.Create(csc, perm, 1.0)
                : CSparse.Complex.Factorization.SparseLU.Create(csc, perm, 1.0, new Columns(n, progress));
            progress?.Invoke(n, n);
            return (lu, csc.NonZerosCount, lu.NonZerosCount);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, csc.NonZerosCount, 0);
        }
    }

    private sealed class Columns(int n, Action<long, long> report) : IProgress<double>
    {
        public void Report(double value) => report(Math.Clamp((long)Math.Round(value * n), 0, n), n);
    }
}
