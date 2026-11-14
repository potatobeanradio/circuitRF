// brief-paa-2 R-paa2-5 — a large array's coupling from a small one's (note §3.2).
//
// In a regular array the coupling between two elements depends, to good approximation, on their offset (Δrow, Δcol) alone. A solved
// R×C sub-array holds every offset to ±(R−1, C−1); for each, the pair NEAREST THE SUB-ARRAY'S CENTRE is used (it has the most
// interior-like surroundings), the first such pair in row-major order on a tie. An offset the sub-array does not hold is zero. The
// diagonal is the centre element's S_cc, or, with position classes, the S_cc of the sub-array element of the element's class.
//
// SPARSE BY CONSTRUCTION: at most (2R−1)·(2C−1) non-zeros per row whatever the array's size — 81 from a 5×5, ~330,000 entries at
// 4096 elements where a dense matrix would hold 16.8 million. Nothing here forms an N×N array.
//
// On a TRIANGULAR lattice an odd row offset displaces an element by a different vector from an even and an odd row, so the offset
// is keyed with the starting row's parity whenever Δrow is odd.
//
// The selection is PAA-1's (testdata/phased-array/harness/compare.py, extend_S and sub_index), so the engine reproduces the numbers
// that brief measured; ArrayEngineTests holds it to them.

using System.Numerics;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>A square sparse complex matrix, compressed by rows, each row's columns ascending.</summary>
public sealed class ArraySparseMatrix
{
    internal ArraySparseMatrix(int n, int[] rowStart, int[] columns, Complex[] values)
    {
        Size = n;
        RowStart = rowStart;
        Columns = columns;
        Values = values;
    }

    public int Size { get; }
    internal int[] RowStart { get; }
    internal int[] Columns { get; }
    internal Complex[] Values { get; }

    public int NonZeros => Values.Length;

    public int NonZerosInRow(int row) => RowStart[row + 1] - RowStart[row];

    public int MaxNonZerosPerRow => Enumerable.Range(0, Size).Max(NonZerosInRow);

    /// <summary>The entry at (<paramref name="row"/>, <paramref name="col"/>), zero when it is not stored.</summary>
    public Complex this[int row, int col]
    {
        get
        {
            int at = Array.BinarySearch(Columns, RowStart[row], RowStart[row + 1] - RowStart[row], col);
            return at >= 0 ? Values[at] : Complex.Zero;
        }
    }

    /// <summary><paramref name="y"/> = this · <paramref name="x"/>.</summary>
    public void Multiply(ReadOnlySpan<Complex> x, Span<Complex> y)
    {
        for (int i = 0; i < Size; i++)
        {
            Complex s = 0;
            for (int k = RowStart[i]; k < RowStart[i + 1]; k++) s += Values[k] * x[Columns[k]];
            y[i] = s;
        }
    }

    /// <summary>A dense matrix held sparse: an array solved whole, whose N is its record's port count.</summary>
    public static ArraySparseMatrix FromDense(Complex[,] m)
    {
        int n = m.GetLength(0);
        if (m.GetLength(1) != n) throw new ArgumentException("The matrix must be square.");
        var rows = new int[n + 1];
        var cols = new List<int>();
        var vals = new List<Complex>();
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                if (m[i, j] != Complex.Zero) { cols.Add(j); vals.Add(m[i, j]); }
            rows[i + 1] = cols.Count;
        }
        return new ArraySparseMatrix(n, rows, [.. cols], [.. vals]);
    }
}

/// <summary>R-paa2-5 — the extended coupling, and the offset range it was measured over (which a run's notes state).</summary>
public sealed record ArrayCouplingExtension(ArraySparseMatrix S, int RowOffsets, int ColumnOffsets, bool Coupled, string Note);

/// <summary>R-paa2-5 — coupling extension and position classes.</summary>
public static class ArrayCoupling
{
    /// <summary>
    /// The array's S from a sub-array's: each offset from the sub-array's pair nearest its centre with that offset, zero beyond what it
    /// holds; the diagonal the centre's (or, with <paramref name="classes"/>, the element's class's) S_cc. <paramref name="coupled"/>
    /// false keeps the diagonal only (<c>Coupling=off</c>).
    /// </summary>
    public static ArrayCouplingExtension Extend(Complex[,] sSub, ArrayLattice subLattice, ArrayLattice lattice, bool coupled = true, bool classes = false)
    {
        int ns = subLattice.Count;
        if (sSub.GetLength(0) != ns || sSub.GetLength(1) != ns)
            throw new ArgumentException($"A {subLattice.Rows} × {subLattice.Cols} sub-array has a {ns}-port S; this one is {sSub.GetLength(0)} × {sSub.GetLength(1)}.");
        if (subLattice.Kind != lattice.Kind)
            throw new ArgumentException("A sub-array extends to an array of its own lattice kind.");
        bool tri = lattice.Kind == ArrayLatticeKind.Tri;
        int rs = subLattice.Rows, cs = subLattice.Cols;

        // The pair nearest the sub-array's centre for each offset; the first in row-major order on a tie.
        var best = new Dictionary<(int, int, int), (double Distance, Complex S)>();
        double cr = (rs - 1) / 2.0, cc = (cs - 1) / 2.0;
        for (int i = 0; i < ns; i++)
        {
            int ri = i / cs, ci = i % cs;
            for (int j = 0; j < ns; j++)
            {
                int rj = j / cs, cj = j % cs;
                var key = Key(rj - ri, cj - ci, ri, tri);
                double dist = Math.Pow((ri + rj) / 2.0 - cr, 2) + Math.Pow((ci + cj) / 2.0 - cc, 2);
                if (!best.TryGetValue(key, out var held) || dist < held.Distance - 1e-12) best[key] = (dist, sSub[i, j]);
            }
        }

        int n = lattice.Count, rows = lattice.Rows, cols = lattice.Cols;
        int centre = SourceIndex(1, 1, 3, 3, subLattice);   // an interior element of any array: the sub-array's centre
        var rowStart = new int[n + 1];
        var columns = new List<int>(n * Math.Min(n, coupled ? (2 * rs - 1) * (2 * cs - 1) : 1));
        var values = new List<Complex>(columns.Capacity);
        for (int a = 0; a < n; a++)
        {
            int ra = a / cols, ca = a % cols;
            int own = classes ? SourceIndex(ra, ca, rows, cols, subLattice) : centre;
            if (!coupled)
            {
                columns.Add(a);
                values.Add(sSub[own, own]);
            }
            else
                for (int dr = -(rs - 1); dr <= rs - 1; dr++)
                {
                    int rb = ra + dr;
                    if (rb < 0 || rb >= rows) continue;
                    for (int dc = -(cs - 1); dc <= cs - 1; dc++)
                    {
                        int cb = ca + dc;
                        if (cb < 0 || cb >= cols) continue;
                        Complex s;
                        if (dr == 0 && dc == 0) s = sSub[own, own];
                        else if (best.TryGetValue(Key(dr, dc, ra, tri), out var held)) s = held.S;
                        else continue;
                        columns.Add(rb * cols + cb);
                        values.Add(s);
                    }
                }
            rowStart[a + 1] = columns.Count;
        }
        string note = coupled
            ? $"Coupling extended from the {rs} × {cs} sub-array: offsets to ±{rs - 1} rows and ±{cs - 1} columns, each from the pair nearest its centre; " +
              (rows > rs || cols > cs ? "every coupling beyond that is taken as zero." : "the array holds no offset beyond it.")
            : "Coupling off: each element reflects its own S, and every coupling between elements is dropped.";
        return new ArrayCouplingExtension(new ArraySparseMatrix(n, rowStart, [.. columns], [.. values]), rs - 1, cs - 1, coupled, note);
    }

    private static (int, int, int) Key(int dr, int dc, int startRow, bool tri) => (dr, dc, tri && dr % 2 != 0 ? startRow % 2 : 0);

    /// <summary>
    /// The 0-based sub-array element whose pattern and S_cc an element at 0-based (<paramref name="row"/>, <paramref name="col"/>) of
    /// a <paramref name="rows"/> × <paramref name="cols"/> array takes: the sub-array's first or last row (column) for the array's
    /// first or last, its centre row (column) otherwise — the upper-left of the middle four on an even side. A 5×5 gives the nine
    /// position classes of note §3.1.
    /// </summary>
    public static int SourceIndex(int row, int col, int rows, int cols, ArrayLattice subLattice)
        => Side(row, rows, subLattice.Rows) * subLattice.Cols + Side(col, cols, subLattice.Cols);

    private static int Side(int i, int n, int ns)
    {
        if (ns == 1 || i == 0) return 0;
        if (i == n - 1) return ns - 1;
        return ns % 2 == 1 ? ns / 2 : ns / 2 - 1;
    }

    /// <summary>The class (sub-array element) of every element of <paramref name="lattice"/>, row-major.</summary>
    public static int[] Classes(ArrayLattice lattice, ArrayLattice subLattice)
        => [.. Enumerable.Range(0, lattice.Count).Select(a => SourceIndex(a / lattice.Cols, a % lattice.Cols, lattice.Rows, lattice.Cols, subLattice))];
}
