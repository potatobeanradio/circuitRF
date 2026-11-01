// P7 — the in-place, blocked, parallel complex-symmetric factorisation that replaces NumFlat's LU
// on the dense planar path.
//
// ── WHY THIS EXISTS ─────────────────────────────────────────────────────────────────────────────
//
// Z is complex-symmetric BY CONSTRUCTION: PlanarFill computes m ≤ n and mirrors, so Z[i,j] and
// Z[j,i] are the same bits (R-fil-2, and PlanarExcitation's header depends on it). A general LU
// knows none of that. It does 2N³/3 complex multiply-adds where an LDLᵀ does N³/3, and NumFlat's
// implementation additionally stores L and U as two SEPARATE full N×N matrices beside the one
// PlanarSystem already holds — P1 measured the result: 48·N² bytes resident, of which 32 are the
// factors, and 42.8 s of single-threaded wall clock at N = 4,933 against a 21.8 s fill that
// parallelises 5.4×.
//
// This does the same job as A = L·D·Lᵀ with L unit lower triangular and D diagonal, written into
// the lower triangle of the matrix it was given. Three consequences, all of them the point:
//
//   * HALF THE ARITHMETIC. N³/3 complex multiply-adds.
//   * ONE MATRIX, NOT THREE. The factors overwrite Z. The only new storage is D — one length-N
//     complex vector, 16·N bytes, i.e. nothing.
//   * IT PARALLELISES. A right-looking block algorithm spends nearly all of its time in the
//     trailing update, and that update is one independent write per trailing COLUMN.
//
// ── THE PRICE, STATED PLAINLY ───────────────────────────────────────────────────────────────────
//
// There is NO PIVOTING here, and an unpivoted symmetric factorisation is not backward stable in
// general — a symmetric matrix can need a 2×2 pivot (Bunch–Kaufman) that this cannot express, and
// D_j can be small for reasons that have nothing to do with the conditioning of A. MoM impedance
// matrices are strongly diagonally dominant in their self terms and unpivoted complex-symmetric
// factorisation is the standard practice for them, but "standard practice" is not a proof. So the
// factorisation carries its own instruments and the gates read them rather than trusting the class:
//
//   * <see cref="Residual"/> — ‖Zx − b‖/‖b‖, the honest backward error, computable whenever the
//     caller still holds Z. It is a STATIC helper taking Z rather than a property, because the
//     factorisation has consumed Z: see PlanarSystem's own note on the diagnostic that keeps a copy.
//   * <see cref="GrowthFactor"/> and <see cref="SmallestPivotRatio"/> — matrix-free, computed
//     during the factorisation at no cost, and available on EVERY solve including the ones where Z
//     is long gone. Growth is what pivoting exists to bound, so a growth factor near 1 is the
//     evidence that not pivoting cost nothing on this matrix.
//
// ── THE ALGORITHM, RIGHT-LOOKING AND BLOCKED ────────────────────────────────────────────────────
//
// For each block of nb columns starting at k:
//
//   1. Factor the PANEL — columns k … k+nb-1, all rows below — with the unblocked algorithm.
//      O(N·nb²), a few percent of the work.
//   2. Update the TRAILING submatrix A₂₂ ← A₂₂ − L₂₁·D₁·L₂₁ᵀ, over columns k+nb … N-1.
//      O((N−k)²·nb), which is where the cubic lives, and where the cores go.
//
// Blocking is not decoration: the unblocked form touches the whole trailing submatrix once per
// COLUMN, so at N = 5,000 it streams 400 MB through the caches 5,000 times. The blocked form does
// it once per BLOCK, i.e. nb times less often, and the panel's nb source columns stay resident
// while the trailing columns are walked.
//
// ── COLUMN-MAJOR, AND WHY EVERY LOOP IS OVER i ──────────────────────────────────────────────────
//
// NumFlat's Mat<T> is COLUMN-MAJOR with element (i,j) at memory[i + j·stride] — verified directly,
// not assumed. Every inner loop here therefore runs over i with j fixed, so both the source and the
// destination column are walked contiguously. That is also what makes the trailing update safe to
// parallelise over j with no locks and no false-sharing beyond a cache line at a column boundary:
// iteration j writes column j and reads only columns k … k+nb-1, which nothing writes.
//
// R-fil-11's rule is preserved exactly — the parallelism is over destinations each written by one
// iteration, so the answer does not depend on the schedule and cap 1 and cap 10 agree BIT FOR BIT.
//
// ── CAP-2 (2026-10-31): THE MATRIX IS A BlockLowerMatrix ────────────────────────────────────────
//
// The factorisation now runs on the block-column lower triangle (BlockLowerMatrix's header) rather
// than on a square Mat<Complex> whose upper half it never read. The panel IS one of that type's
// arrays, and a trailing destination column is one contiguous span of another, so every loop below
// is the loop it was — same operations, same order, same parallel decomposition — over different
// addresses. The factors are bit-identical to the square's (gated). The block size is the matrix's
// own: the layout and the blocking are one decision.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>An in-place, blocked, parallel LDLᵀ of a COMPLEX-SYMMETRIC matrix</b> — no pivoting, no
/// conjugation, and no second copy of the matrix anywhere. See this file's header for the algorithm
/// and for the stability instruments the gates read.
///
/// <para><b>The <see cref="BlockLowerMatrix"/> handed to <see cref="Factor(BlockLowerMatrix, PlanarFillSettings?)"/>
/// is CONSUMED</b>: it comes back holding L. A caller that needs Z afterwards copies it BEFORE
/// factoring. The <see cref="Mat{T}"/> overload copies the lower triangle into a new
/// <see cref="BlockLowerMatrix"/> first and leaves its argument as it was.</para>
/// </summary>
public sealed class SymmetricFactorization
{
    /// <summary>
    /// Columns per block. 64 is the brief's own figure and it is a cache decision rather than an
    /// arithmetic one: at 16 bytes an entry, 64 source columns of a 5,000-row trailing submatrix are
    /// 5 MB, which is the working set one thread keeps hot while it walks the trailing columns.
    /// </summary>
    public const int DefaultBlockSize = 64;

    private readonly BlockLowerMatrix _a;
    private readonly Complex[] _d;

    private SymmetricFactorization(BlockLowerMatrix a, Complex[] d,
                                   double growth, double smallestPivotRatio, double largestEntry)
    {
        _a                  = a;
        _d                  = d;
        GrowthFactor        = growth;
        SmallestPivotRatio  = smallestPivotRatio;
        LargestEntry        = largestEntry;
    }

    /// <summary>N.</summary>
    public int Size => _d.Length;

    /// <summary>
    /// The diagonal D, in order. Exposed because it is what says whether the factorisation was
    /// ill-advised on THIS matrix: an entry orders of magnitude below the others is the signature of
    /// a pivot this form cannot take.
    /// </summary>
    public IReadOnlyList<Complex> Diagonal => _d;

    /// <summary>
    /// <b>max |L_ij| over the strict lower triangle</b> — the classic element-growth diagnostic, and
    /// the quantity pivoting exists to bound. A value near 1 says the unpivoted factorisation was
    /// benign on this matrix; a large one is a warning that the residual is the only thing left
    /// standing between the answer and nonsense.
    /// </summary>
    public double GrowthFactor { get; }

    /// <summary>min |D_j| / max |A_ij| of the ORIGINAL matrix — how close the factorisation came to
    /// a pivot it cannot take. Matrix-free: <see cref="LargestEntry"/> is measured before the first
    /// block is touched.</summary>
    public double SmallestPivotRatio { get; }

    /// <summary>max |A_ij| of the matrix as handed in, kept so the two ratios above mean something
    /// after the matrix is gone.</summary>
    public double LargestEntry { get; }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The factorisation
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Factor <paramref name="a"/> — the dense square's LOWER triangle, copied into a
    /// <see cref="BlockLowerMatrix"/> at <paramref name="blockSize"/> and factored there.
    /// <paramref name="a"/> itself is not modified (CAP-2: the in-place form is the
    /// <see cref="BlockLowerMatrix"/> overload, which is what the solver calls).
    /// </summary>
    /// <param name="settings">Supplies the ONE parallel cap, exactly as the fill's own row loop
    /// reads it: a <see cref="PlanarFillSettings.Budget"/> if a fanned-out run attached one,
    /// otherwise <see cref="PlanarFillSettings.MaxDegreeOfParallelism"/>, otherwise unbounded. Null
    /// is the default settings object, i.e. unbounded.</param>
    /// <param name="blockSize">Columns per block; <see cref="DefaultBlockSize"/> unless a
    /// measurement is sweeping it.</param>
    public static SymmetricFactorization Factor(Mat<Complex> a, PlanarFillSettings? settings = null,
                                                int blockSize = DefaultBlockSize)
    {
        int n = a.RowCount;
        if (n == 0 || a.ColCount != n)
            throw new ArgumentException(
                $"A symmetric factorisation needs a square, non-empty matrix; this one is " +
                $"{a.RowCount}×{a.ColCount}.", nameof(a));
        if (blockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                "A block of no columns would make no progress.");
        return Factor(BlockLowerMatrix.FromLower(a, blockSize), settings);
    }

    /// <summary>
    /// <b>Factor <paramref name="a"/> in place</b> — the shipped path. <b>The matrix is consumed</b>:
    /// it comes back holding L (unit lower triangular, diagonal not stored as such) — see the class
    /// note. The block size is <paramref name="a"/>'s own panel width.
    /// </summary>
    /// <param name="settings">The ONE parallel cap; see the <see cref="Mat{T}"/> overload.</param>
    public static SymmetricFactorization Factor(BlockLowerMatrix a, PlanarFillSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        int n = a.Size;
        if (n == 0)
            throw new ArgumentException(
                "A symmetric factorisation needs a square, non-empty matrix; this one is 0×0.", nameof(a));

        var st  = settings ?? PlanarFillSettings.Default;
        var d   = new Complex[n];

        // Before anything is overwritten: the scale the two stability ratios are measured against.
        // One pass over the lower triangle, O(N²) reads and no writes — invisible beside the cubic.
        double largest = 0;
        for (int j = 0; j < n; j++)
        {
            var col = a.Column(j);
            for (int i = 0; i < col.Length; i++)
            {
                double m = Magnitude(col[i]);
                if (m > largest) largest = m;
            }
        }

        int nb = a.BlockSize;

        for (int pk = 0, k = 0; k < n; pk++, k += nb)
        {
            int jb  = a.PanelWidth(pk);
            int lda = a.PanelLeading(pk);
            FactorPanel(a.Panel(pk), n, lda, d, k, jb);

            int start = k + jb;
            int count = n - start;
            if (count <= 0) break;

            // R-fil-11's shape: one destination column per iteration, written by that iteration
            // alone. The source columns k … k+jb-1 — this panel — are read-only for the whole loop.
            // Element (i, p) of the panel is s[(i − k) + (p − k)·lda]; the destination column's
            // span starts at its diagonal, so dst[i − col] is element (i, col).
            int panel = pk, kk = k;
            PlanarFill.ForRowsOf(st, count, t =>
            {
                var s   = a.Panel(panel);
                int col = start + t;
                var dst = a.Column(col);
                int len = n - col;
                for (int p = kk; p < start; p++)
                {
                    int cp = (p - kk) * lda + (col - kk);         // element (col, p)
                    Complex c = d[p] * s[cp];
                    if (c == Complex.Zero) continue;
                    double cr = c.Real, ci = c.Imaginary;
                    var src = s.Slice(cp, len);                   // rows col … n−1 of column p
                    for (int i = 0; i < len; i++)
                    {
                        Complex l = src[i];
                        double lr = l.Real, li = l.Imaginary;
                        dst[i] -= new Complex(lr * cr - li * ci, lr * ci + li * cr);
                    }
                }
            });
        }

        // Growth, after the fact: one pass over the strict lower triangle, which now holds L.
        double growth = 0;
        for (int j = 0; j < n; j++)
        {
            var col = a.Column(j);
            for (int i = 1; i < col.Length; i++)
            {
                double m = Magnitude(col[i]);
                if (m > growth) growth = m;
            }
        }

        double smallest = double.PositiveInfinity;
        for (int j = 0; j < n; j++) smallest = Math.Min(smallest, Magnitude(d[j]));

        return new SymmetricFactorization(
            a, d, growth, largest > 0 ? smallest / largest : double.NaN, largest);
    }

    /// <summary>
    /// The unblocked right-looking LDLᵀ of one panel — columns <paramref name="k"/> …
    /// <paramref name="k"/>+<paramref name="jb"/>-1, every row below the diagonal, and the rank-1
    /// updates that stay INSIDE the panel. Everything outside it is the caller's trailing update.
    /// <paramref name="s"/> is the panel's own array: element (i, j) at (i − k) + (j − k)·lda.
    /// </summary>
    private static void FactorPanel(Span<Complex> s, int n, int lda, Complex[] d, int k, int jb)
    {
        int end = k + jb;
        for (int j = k; j < end; j++)
        {
            int cj = (j - k) * lda - k;                           // s[i + cj] is element (i, j)
            Complex dj = s[j + cj];
            if (dj == Complex.Zero)
                throw new InvalidOperationException(
                    $"The unpivoted complex-symmetric factorisation met an exactly zero pivot at " +
                    $"row {j} of {n}. This matrix needs a pivoted factorisation (Bunch–Kaufman); " +
                    "it is not a symptom of a bad mesh and it cannot be worked around by refining " +
                    "one. Set PlanarFillSettings.UseSymmetricFactorization = false to fall back to " +
                    "the general LU.");
            d[j] = dj;

            // ONE division per column, then multiplies. The alternative — a complex division per
            // entry — is O(N²) divisions across the factorisation for at most an ulp of accuracy,
            // and .NET's complex division is Smith-scaled and correspondingly slow.
            Complex inv = Complex.One / dj;
            double ir = inv.Real, ii = inv.Imaginary;
            for (int i = j + 1; i < n; i++)
            {
                Complex v = s[i + cj];
                double vr = v.Real, vi = v.Imaginary;
                s[i + cj] = new Complex(vr * ir - vi * ii, vr * ii + vi * ir);
            }

            for (int q = j + 1; q < end; q++)
            {
                Complex c = dj * s[q + cj];
                if (c == Complex.Zero) continue;
                double cr = c.Real, ci = c.Imaginary;
                int cq = (q - k) * lda - k;
                for (int i = q; i < n; i++)
                {
                    Complex l = s[i + cj];
                    double lr = l.Real, li = l.Imaginary;
                    s[i + cq] -= new Complex(lr * cr - li * ci, lr * ci + li * cr);
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Substitution
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One right-hand side: L y = b, D z = y, Lᵀ x = z.</summary>
    public Vec<Complex> Solve(Vec<Complex> b)
    {
        var x = new Vec<Complex>(Size);
        SolveInto(b, x);
        return x;
    }

    /// <summary>
    /// <b>P right-hand sides against one factorisation</b> — the shape <c>Y = BᵀZ⁻¹B</c> actually
    /// asks for. Not a loop over <see cref="Solve(Vec{Complex})"/>: each column of L is read once and
    /// applied to all P vectors, so the O(P·N²) substitution streams the factor through the cache
    /// once instead of P times. With P = 2 that is a rounding error on a run; with a 16-port block
    /// it is not, and there is no reason to write the slow one.
    /// </summary>
    public Vec<Complex>[] Solve(IReadOnlyList<Vec<Complex>> rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        int n = Size, p = rhs.Count;
        var x = new Complex[p][];
        for (int r = 0; r < p; r++)
        {
            if (rhs[r].Count != n)
                throw new ArgumentException(
                    $"Right-hand side {r} has {rhs[r].Count} entries against a factorisation of " +
                    $"{n}.", nameof(rhs));
            var col = new Complex[n];
            for (int i = 0; i < n; i++) col[i] = rhs[r][i];
            x[r] = col;
        }

        // Column j of L from its diagonal down: lj[i − j] is L[i, j] (CAP-2's block-column store).
        // Forward: L y = b, unit lower triangular, column sweep.
        for (int j = 0; j < n; j++)
        {
            var lj = _a.Column(j);
            for (int r = 0; r < p; r++)
            {
                Complex yj = x[r][j];
                if (yj == Complex.Zero) continue;
                var col = x[r];
                for (int i = j + 1; i < n; i++) col[i] -= lj[i - j] * yj;
            }
        }

        for (int r = 0; r < p; r++)
        {
            var col = x[r];
            for (int i = 0; i < n; i++) col[i] /= _d[i];
        }

        // Backward: Lᵀ x = z. Column j of L is row j of Lᵀ, so this is a dot product down a column.
        for (int j = n - 1; j >= 0; j--)
        {
            var lj = _a.Column(j);
            for (int r = 0; r < p; r++)
            {
                var col = x[r];
                Complex acc = Complex.Zero;
                for (int i = j + 1; i < n; i++) acc += lj[i - j] * col[i];
                col[j] -= acc;
            }
        }

        var outv = new Vec<Complex>[p];
        for (int r = 0; r < p; r++)
        {
            var v = new Vec<Complex>(n);
            for (int i = 0; i < n; i++) v[i] = x[r][i];
            outv[r] = v;
        }
        return outv;
    }

    private void SolveInto(Vec<Complex> b, Vec<Complex> x)
    {
        int n = Size;
        if (b.Count != n)
            throw new ArgumentException(
                $"This right-hand side has {b.Count} entries against a factorisation of {n}.",
                nameof(b));

        var work = new Complex[n];
        for (int i = 0; i < n; i++) work[i] = b[i];

        for (int j = 0; j < n; j++)
        {
            Complex yj = work[j];
            if (yj == Complex.Zero) continue;
            var lj = _a.Column(j);
            for (int i = j + 1; i < n; i++) work[i] -= lj[i - j] * yj;
        }

        for (int i = 0; i < n; i++) work[i] /= _d[i];

        for (int j = n - 1; j >= 0; j--)
        {
            var lj = _a.Column(j);
            Complex acc = Complex.Zero;
            for (int i = j + 1; i < n; i++) acc += lj[i - j] * work[i];
            work[j] -= acc;
        }

        for (int i = 0; i < n; i++) x[i] = work[i];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The instrument the gate reads
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>‖Zx − b‖₂ / ‖b‖₂</c> — the backward error of a solve, against the ORIGINAL matrix.
    ///
    /// <para><b>Static, and taking Z, because the factorisation no longer has it.</b> That is the
    /// whole point of factoring in place, and pretending otherwise by holding a copy would give back
    /// exactly the memory this phase exists to recover. A caller that wants a residual on every
    /// solve keeps the copy itself and knows what it is paying — see
    /// <c>PlanarFillSettings.TrackFactorizationResidual</c>, which is off by default and is a
    /// diagnostic, not a safety net.</para>
    ///
    /// <para>Only the LOWER triangle of <paramref name="z"/> is read, and it is reflected. Z is
    /// symmetric bit for bit by construction, so this is the same product either way — and it means
    /// the residual can be taken against a matrix whose upper triangle a factorisation has
    /// scribbled on, which is a thing the tests want.</para>
    /// </summary>
    public static double Residual(Mat<Complex> z, Vec<Complex> x, Vec<Complex> b)
    {
        int n = z.RowCount;
        if (z.ColCount != n || x.Count != n || b.Count != n)
            throw new ArgumentException(
                $"A residual needs a square matrix and two vectors of its size; got {z.RowCount}×" +
                $"{z.ColCount}, x[{x.Count}], b[{b.Count}].", nameof(z));

        var s   = z.Memory.Span;
        int lda = z.Stride;

        var ax = new Complex[n];
        for (int j = 0; j < n; j++)
        {
            int cj = j * lda;
            Complex xj = x[j];
            // Column j below the diagonal serves twice: as Z[i,j] into row i, and — by symmetry —
            // as Z[j,i] into row j.
            Complex acc = s[j + cj] * xj;
            for (int i = j + 1; i < n; i++)
            {
                Complex zij = s[i + cj];
                ax[i] += zij * xj;
                acc   += zij * x[i];
            }
            ax[j] += acc;
        }

        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            Complex r = ax[i] - b[i];
            num += r.Real * r.Real + r.Imaginary * r.Imaginary;
            double bi2 = b[i].Real * b[i].Real + b[i].Imaginary * b[i].Imaginary;
            den += bi2;
        }
        return den > 0 ? Math.Sqrt(num / den) : Math.Sqrt(num);
    }

    /// <summary>
    /// The same backward error against Z held as CAP-2's <see cref="BlockLowerMatrix"/> — the same
    /// summation, column by column, as the <see cref="Mat{T}"/> overload.
    /// </summary>
    public static double Residual(BlockLowerMatrix z, Vec<Complex> x, Vec<Complex> b)
    {
        ArgumentNullException.ThrowIfNull(z);
        int n = z.Size;
        if (x.Count != n || b.Count != n)
            throw new ArgumentException(
                $"A residual needs two vectors of the matrix's size; got {n}×{n}, x[{x.Count}], " +
                $"b[{b.Count}].", nameof(z));

        var xs = new Complex[n];
        for (int i = 0; i < n; i++) xs[i] = x[i];
        var ax = z.Multiply(xs);

        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            Complex r = ax[i] - b[i];
            num += r.Real * r.Real + r.Imaginary * r.Imaginary;
            double bi2 = b[i].Real * b[i].Real + b[i].Imaginary * b[i].Imaginary;
            den += bi2;
        }
        return den > 0 ? Math.Sqrt(num / den) : Math.Sqrt(num);
    }

    /// <summary>|z| without the overflow guard <see cref="Complex.Abs"/> pays for — these are
    /// diagnostics over quantities the factorisation has already multiplied.</summary>
    private static double Magnitude(Complex z)
        => Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
}
