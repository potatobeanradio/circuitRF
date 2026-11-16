// brief-paa-2 R-paa2-2 — the pattern of an array: the element pattern(s) times the array factor (note §2.2, eq. 2),
//
//     E(û) = Σ_p  E_p(û) · Σ_{n of class p}  a_n · exp(j·k·û·r_n)
//
// with E_p an element pattern referred to its own position (R-paa2-3) and r_n element n's position from the array's centre.
//
// NO EXPONENTIAL PER CALL. exp(j·k·(x·u + y·v)) is an x factor times a y factor, so the tables X[d,c] = exp(j·k·x_c·u_d) and
// Y[d,r] = exp(j·k·(y_r·v_d + s_r·u_d)) — with s_r a triangular row's half-pitch shift, so a triangular lattice is its two
// interleaved rectangular row sets in one table — depend on the lattice's geometry, the directions and the frequency and never on the
// weights. They are built once per (geometry, direction set, frequency), kept with the direction set and released with it
// (TablesBuilt counts the builds). A sum is then Σ_r Y[d,r]·(Σ_c w_rc·X[d,c]) over the non-zero weights, and a separable weighting
// (a steer times a separable taper, nothing switched off) is (Σ_r p_r·Y[d,r])·(Σ_c q_c·X[d,c]): O(D·(R+C)).

using System.Numerics;
using System.Runtime.CompilerServices;

using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>The phase tables of one lattice geometry over one direction set at one frequency.</summary>
public sealed class ArrayPhaseTables
{
    internal ArrayPhaseTables(ArrayLattice lattice, ArrayDirections directions, double hz)
    {
        Rows = lattice.Rows;
        Cols = lattice.Cols;
        Key = lattice.GeometryKey;
        Hz = hz;
        int n = directions.Count;
        double k = 2 * Math.PI * hz / Mom.EmConstants.C0;
        X = new Complex[n * Cols];
        Y = new Complex[n * Rows];
        var xc = new double[Cols];
        for (int c = 0; c < Cols; c++) xc[c] = k * lattice.ColumnX(c);
        var yr = new double[Rows];
        var sr = new double[Rows];
        for (int r = 0; r < Rows; r++) { yr[r] = k * lattice.RowY(r); sr[r] = k * lattice.RowShift(r); }
        Parallel.For(0, n, d =>
        {
            double u = directions.U[d], v = directions.V[d];
            for (int c = 0; c < Cols; c++)
            {
                var (s, co) = Math.SinCos(xc[c] * u);
                X[d * Cols + c] = new Complex(co, s);
            }
            for (int r = 0; r < Rows; r++)
            {
                var (s, co) = Math.SinCos(yr[r] * v + sr[r] * u);
                Y[d * Rows + r] = new Complex(co, s);
            }
        });
    }

    internal int Rows { get; }
    internal int Cols { get; }
    internal (int, int, double, double, ArrayLatticeKind) Key { get; }
    internal double Hz { get; }

    /// <summary>[direction, column].</summary>
    internal Complex[] X { get; }

    /// <summary>[direction, row], the triangular shift included.</summary>
    internal Complex[] Y { get; }

    /// <summary>The bytes the two tables hold.</summary>
    public long Bytes => 16L * (X.Length + Y.Length);
}

/// <summary>Which element pattern each element takes: one for all, one per position class, or each port's own.</summary>
public sealed class ArrayElements
{
    private ArrayElements(ElementPattern[] patterns, int[] patternOf)
    {
        Patterns = patterns;
        PatternOf = patternOf;
    }

    public IReadOnlyList<ElementPattern> Patterns { get; }

    /// <summary>The index into <see cref="Patterns"/> of each element, row-major.</summary>
    public IReadOnlyList<int> PatternOf { get; }

    /// <summary>Every element takes <paramref name="pattern"/>.</summary>
    public static ArrayElements Single(ElementPattern pattern, int count) => new([pattern], new int[count]);

    /// <summary>Element n takes pattern n: an array that was solved whole, each port its own.</summary>
    public static ArrayElements Each(IReadOnlyList<ElementPattern> patterns) => new([.. patterns], [.. Enumerable.Range(0, patterns.Count)]);

    /// <summary>Element n takes <paramref name="patterns"/>[<paramref name="patternOf"/>[n]].</summary>
    public static ArrayElements Classes(IReadOnlyList<ElementPattern> patterns, IReadOnlyList<int> patternOf)
    {
        foreach (int p in patternOf)
            if (p < 0 || p >= patterns.Count) throw new ArgumentOutOfRangeException(nameof(patternOf), p, $"There are {patterns.Count} patterns.");
        return new([.. patterns], [.. patternOf]);
    }
}

/// <summary>A pattern over a direction set: Eθ and Eφ in the elements' units times the weights'.</summary>
public sealed record ArrayField(ArrayDirections Directions, Complex[] ETheta, Complex[] EPhi)
{
    /// <summary>|Eθ|² + |Eφ|² at direction <paramref name="d"/>.</summary>
    public double Power(int d) => ETheta[d].Magnitude * ETheta[d].Magnitude + EPhi[d].Magnitude * EPhi[d].Magnitude;
}

/// <summary>R-paa2-2 — the array factor and the pattern sum.</summary>
public static partial class ArrayPattern
{
    private static readonly ConditionalWeakTable<ArrayDirections, List<ArrayPhaseTables>> _tables = new();
    private static long _built, _separable;

    /// <summary>Phase tables kept per direction set (geometry and frequency), the oldest dropped first.</summary>
    public const int KeptTables = 8;

    /// <summary>Phase tables built in this process: a second sum with new weights over the same directions must not add to it.</summary>
    public static long TablesBuilt => Interlocked.Read(ref _built);

    /// <summary>Sums that took the separable O(D·(R+C)) path.</summary>
    public static long SeparableSums => Interlocked.Read(ref _separable);

    /// <summary>The tables for <paramref name="lattice"/>'s geometry over <paramref name="directions"/> at <paramref name="hz"/>, built once.</summary>
    public static ArrayPhaseTables Tables(ArrayLattice lattice, ArrayDirections directions, double hz)
    {
        var list = _tables.GetValue(directions, _ => []);
        lock (list)
        {
            foreach (var t in list)
                if (t.Key == lattice.GeometryKey && t.Hz == hz) return t;
            var built = new ArrayPhaseTables(lattice, directions, hz);
            Interlocked.Increment(ref _built);
            // brief-paa-8 — the direction set of a grid now lives as long as the grid, so a sweep of dx or of the frequency must not grow
            // its tables without bound: the oldest go first
            if (list.Count >= KeptTables) list.RemoveAt(0);
            list.Add(built);
            return built;
        }
    }

    /// <summary>The array factor Σ a_n·exp(j·k·û·r_n) at each direction, the weights row-major; an element switched off contributes nothing.</summary>
    public static Complex[] Factor(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayDirections directions, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        var t = Tables(lattice, directions, hz);
        var w = new Complex[lattice.Count];
        for (int i = 0; i < w.Length; i++) w[i] = lattice.IsOn(i) ? weights[i] : Complex.Zero;
        return Sum(t, w, directions.Count);
    }

    /// <summary>R-paa2-2 — the pattern: each element pattern times the array factor of the elements that take it.</summary>
    public static ArrayField Sum(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements, ArrayDirections directions, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        return SumWith(Tables(lattice, directions, hz), lattice, weights, elements, directions);
    }

    /// <summary>brief-paa-9 — <see cref="Sum(ArrayLattice, IReadOnlyList{Complex}, ArrayElements, ArrayDirections, double)"/> over a
    /// TRANSIENT direction set — a beam search's patch, a cut through a peak that moves with every drag step — whose tables are built for
    /// this call and dropped: they are never kept against the set nor counted in <see cref="TablesBuilt"/>, since nothing could reuse them.</summary>
    public static ArrayField SumOnce(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements, ArrayDirections directions, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        return SumWith(new ArrayPhaseTables(lattice, directions, hz), lattice, weights, elements, directions);
    }

    /// <summary>brief-paa-9 — <see cref="Factor"/> over a transient direction set, as <see cref="SumOnce"/>.</summary>
    public static Complex[] FactorOnce(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayDirections directions, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        var w = new Complex[lattice.Count];
        for (int i = 0; i < w.Length; i++) w[i] = lattice.IsOn(i) ? weights[i] : Complex.Zero;
        return Sum(new ArrayPhaseTables(lattice, directions, hz), w, directions.Count);
    }

    /// <summary>brief-pch-9 — <see cref="FactorOnce(ArrayLattice, IReadOnlyList{Complex}, ArrayDirections, double)"/> over tables the caller
    /// built once for its transient direction set (<see cref="TransientTables"/>): a codebook's thousands of weightings over one ray set
    /// rebuilt the same K·(R + C) exponentials for every entry.</summary>
    public static Complex[] FactorOnce(ArrayPhaseTables tables, ArrayLattice lattice, IReadOnlyList<Complex> weights, int directions)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        if (tables.Key != lattice.GeometryKey) throw new ArgumentException("The tables were built for another lattice.");
        var w = new Complex[lattice.Count];
        for (int i = 0; i < w.Length; i++) w[i] = lattice.IsOn(i) ? weights[i] : Complex.Zero;
        return Sum(tables, w, directions);
    }

    /// <summary>brief-pch-9 — the phase tables of a transient direction set, for the caller to keep for one computation; never kept here nor
    /// counted in <see cref="TablesBuilt"/>.</summary>
    public static ArrayPhaseTables TransientTables(ArrayLattice lattice, ArrayDirections directions, double hz) => new(lattice, directions, hz);

    private static ArrayField SumWith(ArrayPhaseTables t, ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements, ArrayDirections directions)
    {
        if (elements.PatternOf.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {elements.PatternOf.Count} pattern assignments.");
        int n = directions.Count;
        var eTheta = new Complex[n];
        var ePhi = new Complex[n];
        var w = new Complex[lattice.Count];
        for (int p = 0; p < elements.Patterns.Count; p++)
        {
            bool any = false;
            for (int i = 0; i < w.Length; i++)
            {
                w[i] = elements.PatternOf[i] == p && lattice.IsOn(i) ? weights[i] : Complex.Zero;
                any |= w[i] != Complex.Zero;
            }
            if (!any) continue;
            var af = Sum(t, w, n);
            var (et, ep) = elements.Patterns[p].Sample(directions);
            for (int d = 0; d < n; d++)
            {
                eTheta[d] += et[d] * af[d];
                ePhi[d] += ep[d] * af[d];
            }
        }
        return new ArrayField(directions, eTheta, ePhi);
    }

    /// <summary>R-paa2-2a — the OVERVIEW: the pattern on the record's own grid, for the whole-sphere view and the <c>farfield</c> cubes,
    /// with the label that says which grid it is. No metric is read from it (ArrayBeam).</summary>
    public static (ArrayField Field, string Label) Overview(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements,
                                                            Mom.PlanarFarFieldGrid grid, double hz)
    {
        var th = grid.ThetaDeg;
        var ph = grid.PhiDeg;
        string Step(IReadOnlyList<double> axis) => axis.Count > 1 ? $"{axis[1] - axis[0]:G4}°" : "one";
        string label = $"overview on the record's {Step(th)} × {Step(ph)} grid (θ {th[0]:G4}…{th[^1]:G4}°); beamwidth, sidelobes and pointing " +
                       "come from the local evaluation, not from this grid";
        return (Sum(lattice, weights, elements, ArrayDirections.OnGrid(grid), hz), label);
    }

    /// <summary>The factor of the weights <paramref name="w"/> (zero where off) from the tables: separable when it is, else row by row
    /// over the non-zero weights.</summary>
    private static Complex[] Sum(ArrayPhaseTables t, Complex[] w, int n)
    {
        int rows = t.Rows, cols = t.Cols;
        var af = new Complex[n];
        if (Separable(w, rows, cols) is { } sep)
        {
            Interlocked.Increment(ref _separable);
            var (p, q) = sep;
            Parallel.For(0, n, d =>
            {
                Complex a = 0, b = 0;
                for (int r = 0; r < rows; r++) a += p[r] * t.Y[d * rows + r];
                for (int c = 0; c < cols; c++) b += q[c] * t.X[d * cols + c];
                af[d] = a * b;
            });
            return af;
        }
        // Each row's non-zero columns, so a sparse weighting (a class, one element) costs its own non-zeros.
        var rowCols = new int[rows][];
        var rowW = new Complex[rows][];
        for (int r = 0; r < rows; r++)
        {
            var cs = new List<int>();
            for (int c = 0; c < cols; c++) if (w[r * cols + c] != Complex.Zero) cs.Add(c);
            rowCols[r] = [.. cs];
            rowW[r] = [.. cs.Select(c => w[r * cols + c])];
        }
        Parallel.For(0, n, d =>
        {
            Complex sum = 0;
            for (int r = 0; r < rows; r++)
            {
                var cs = rowCols[r];
                if (cs.Length == 0) continue;
                var ws = rowW[r];
                Complex row = 0;
                int at = d * cols;
                for (int m = 0; m < cs.Length; m++) row += ws[m] * t.X[at + cs[m]];
                sum += row * t.Y[d * rows + r];
            }
            af[d] = sum;
        });
        return af;
    }

    /// <summary>The factors p_r·q_c of a rank-one weighting, or null: the pivot's row and column, checked against every weight.</summary>
    internal static (Complex[] P, Complex[] Q)? Separable(Complex[] w, int rows, int cols)
    {
        if (rows == 1 || cols == 1) return null;
        int pivot = 0;
        double max = 0;
        for (int i = 0; i < w.Length; i++)
            if (w[i].Magnitude > max) { max = w[i].Magnitude; pivot = i; }
        if (max == 0) return null;
        int r0 = pivot / cols, c0 = pivot % cols;
        var p = new Complex[rows];
        var q = new Complex[cols];
        for (int r = 0; r < rows; r++) p[r] = w[r * cols + c0];
        for (int c = 0; c < cols; c++) q[c] = w[r0 * cols + c] / w[pivot];
        // A steer written as one exponential per element differs from its own row × column product by the rounding of a phase of
        // up to ~100 rad, ~2e-14; 1e-13 admits that and bounds the factor's error at 1e-13 of Σ|w|.
        double tol = 1e-13 * max;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if ((w[r * cols + c] - p[r] * q[c]).Magnitude > tol) return null;
        return (p, q);
    }
}
