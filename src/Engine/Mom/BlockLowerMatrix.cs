// CAP-2 — the lower triangle of a complex-symmetric matrix, stored in BLOCK-COLUMN panels.
//
// ── WHY ─────────────────────────────────────────────────────────────────────────────────────────
//
// The dense planar Z is complex-symmetric by construction (R-fil-2: the fill computes m ≤ n and
// used to mirror), and P7's LDLᵀ reads and writes only the lower triangle. Up to CAP-2 the matrix was
// a full Mat<Complex> anyway, so half of the largest allocation in a frequency point held a mirror
// nothing read. This type holds the lower triangle only: ~8·N² bytes where the square held 16·N².
//
// ── THE LAYOUT, AND WHY NOT THE CLASSIC PACKED TRIANGLE ─────────────────────────────────────────
//
// For each block of nb columns starting at k (nb = SymmetricFactorization.DefaultBlockSize), ONE
// array: rows k … N−1 × nb columns, column-major with leading dimension N − k. Element (i, j), i ≥ j,
// of the panel holding j lives at (i − k) + (j − k)·(N − k). The top nb×nb block of each panel keeps
// its strictly-upper corner (nb(nb−1)/2 dead entries), which is what costs the N·nb/2 over N²/2.
//
// That is exactly the shape P7's blocked right-looking factorisation walks: the panel factor touches
// one panel, and the trailing update writes column j while reading the current panel — every inner
// loop runs over i with both columns contiguous, as it did on the square. The classic row/column
// packed triangle (column j starting at j·N − j(j−1)/2) would hold N(N+1)/2 entries, but its column
// starts are not block-aligned and the panel is no longer one contiguous strip, so the trailing
// update would lose its cache behaviour for a saving of N·nb/2 entries (0.15 % at N = 44,000).
//
// ── ONE ARRAY PER PANEL, NOT ONE ARRAY ──────────────────────────────────────────────────────────
//
// A .NET array tops out at 2,147,483,591 elements. One array for the whole triangle (N²/2 + N·nb/2)
// would fit to N ≈ 65,500 — past the series' 44,000 and CAP-4's ~60,000, but with no margin, and a
// complex-single successor halves the element and not the count. Per-panel arrays have no such wall
// (the largest is N·nb elements), and each is a large-object-heap block of ~N·1 kB, so the GC never
// has to find one 15 GB hole.
//
// ── WHAT IS NOT HERE ────────────────────────────────────────────────────────────────────────────
//
// No upper triangle, ever. The element accessor answers (i, j) for either triangle BY SYMMETRY, and
// ToDense() expands the mirror for a small-N caller that wants a Mat<Complex> — the copy is the
// whole cost this type removes, so it is named and never taken on a production path.

using System.Numerics;
using System.Runtime.CompilerServices;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>CAP-2 — the lower triangle of a complex-symmetric N×N matrix, in block-column panels</b>: one
/// contiguous column-major array per block of <see cref="BlockSize"/> columns, rows from the block's
/// first column down. See this file's header for the layout and why it was chosen.
/// </summary>
public sealed class BlockLowerMatrix
{
    private readonly Complex[][] _panels;

    /// <summary>A zero matrix of size <paramref name="n"/>.</summary>
    /// <param name="blockSize">Columns per panel; <see cref="SymmetricFactorization.DefaultBlockSize"/>
    /// unless a measurement is sweeping it. The factorisation's block size is this.</param>
    public BlockLowerMatrix(int n, int blockSize = SymmetricFactorization.DefaultBlockSize)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "A matrix size cannot be negative.");
        if (blockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                "A block of no columns would make no progress.");
        Size      = n;
        BlockSize = blockSize;
        int panels = n == 0 ? 0 : (n + blockSize - 1) / blockSize;
        _panels = new Complex[panels][];
        for (int p = 0; p < panels; p++)
        {
            int k = p * blockSize, jb = Math.Min(blockSize, n - k);
            _panels[p] = new Complex[(long)(n - k) * jb];
        }
    }

    /// <summary>N.</summary>
    public int Size { get; }

    /// <summary>Columns per panel (the last panel may hold fewer).</summary>
    public int BlockSize { get; }

    /// <summary>How many panels there are.</summary>
    public int PanelCount => _panels.Length;

    /// <summary>
    /// <b>Entries stored for an <paramref name="n"/>×<paramref name="n"/> triangle at block size
    /// <paramref name="blockSize"/></b> — Σ over panels of (N − k)·jb, i.e. N²/2 + N·nb/2 exactly when
    /// nb divides N.
    /// </summary>
    public static long StoredEntries(int n, int blockSize = SymmetricFactorization.DefaultBlockSize)
    {
        long total = 0;
        for (int k = 0; k < n; k += blockSize) total += (long)(n - k) * Math.Min(blockSize, n - k);
        return total;
    }

    /// <summary>Bytes of complex-double storage <see cref="StoredEntries"/> needs.</summary>
    public static long Bytes(int n, int blockSize = SymmetricFactorization.DefaultBlockSize)
        => 16L * StoredEntries(n, blockSize);

    /// <summary>First column of panel <paramref name="p"/>.</summary>
    public int PanelStart(int p) => p * BlockSize;

    /// <summary>Leading dimension of panel <paramref name="p"/>: N − its first column.</summary>
    public int PanelLeading(int p) => Size - p * BlockSize;

    /// <summary>Columns in panel <paramref name="p"/>.</summary>
    public int PanelWidth(int p) => Math.Min(BlockSize, Size - p * BlockSize);

    /// <summary>Panel <paramref name="p"/>'s whole array, column-major with leading dimension
    /// <see cref="PanelLeading"/>; row r of the array is matrix row <see cref="PanelStart"/> + r.</summary>
    public Span<Complex> Panel(int p) => _panels[p];

    /// <summary>
    /// <b>Column <paramref name="j"/> from its diagonal down</b> — rows j … N−1, contiguous, so
    /// element (i, j) is <c>Column(j)[i − j]</c>. Every writer fills through this: a fill row loop
    /// over i ≥ j is one column, written by one iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Complex> Column(int j)
    {
        int p = j / BlockSize, c = j - p * BlockSize, ld = Size - p * BlockSize;
        return _panels[p].AsSpan(c * ld + c, Size - j);
    }

    /// <summary>
    /// A writable reference to stored element (<paramref name="i"/>, <paramref name="j"/>) with
    /// <paramref name="i"/> ≥ <paramref name="j"/>. The upper triangle is not stored; asking for it is
    /// a caller bug, not a symmetry to resolve silently, because a WRITE there would land on the lower
    /// entry and double-count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Complex Lower(int i, int j)
    {
        if ((uint)i >= (uint)Size || (uint)j > (uint)i)
            throw new ArgumentOutOfRangeException(nameof(i),
                $"({i}, {j}) is not in the stored lower triangle of a {Size}×{Size} matrix.");
        int p = j / BlockSize, k = p * BlockSize;
        return ref _panels[p][(i - k) + (long)(j - k) * (Size - k)];
    }

    /// <summary>Element (<paramref name="i"/>, <paramref name="j"/>) of the SYMMETRIC matrix — either
    /// triangle, answered from the stored lower one.</summary>
    public Complex this[int i, int j] => i >= j ? Lower(i, j) : Lower(j, i);

    /// <summary>
    /// <b>The full square, mirror included — expensive, and named so.</b> 16·N² bytes, i.e. twice what
    /// this type holds. For small-N callers (tests, the general-LU reference path) only. The upper
    /// triangle is a COPY of the lower, so the result is symmetric bit for bit, exactly as the fill's
    /// own mirror made it before CAP-2.
    /// </summary>
    public Mat<Complex> ToDense()
    {
        int n = Size;
        var m = new Mat<Complex>(n, n);
        var s = m.Memory.Span;
        int lda = m.Stride;
        for (int j = 0; j < n; j++)
        {
            var col = Column(j);
            for (int i = j; i < n; i++)
            {
                Complex v = col[i - j];
                s[i + j * lda] = v;
                s[j + i * lda] = v;
            }
        }
        return m;
    }

    /// <summary>The lower triangle of <paramref name="a"/>, copied in. The upper triangle of
    /// <paramref name="a"/> is not read — a symmetric matrix's mirror carries nothing — and
    /// <paramref name="a"/> itself is left as it was.</summary>
    public static BlockLowerMatrix FromLower(Mat<Complex> a,
                                             int blockSize = SymmetricFactorization.DefaultBlockSize)
    {
        int n = a.RowCount;
        if (a.ColCount != n)
            throw new ArgumentException(
                $"Only a square matrix has a lower triangle to store; this one is {a.RowCount}×{a.ColCount}.",
                nameof(a));
        var z = new BlockLowerMatrix(n, blockSize);
        var s = a.Memory.Span;
        int lda = a.Stride;
        for (int j = 0; j < n; j++)
        {
            var col = z.Column(j);
            int cj = j * lda;
            for (int i = j; i < n; i++) col[i - j] = s[i + cj];
        }
        return z;
    }

    /// <summary>A deep copy — the same N·nb/2-padded triangle, not a square.</summary>
    public BlockLowerMatrix Copy()
    {
        var c = new BlockLowerMatrix(Size, BlockSize);
        for (int p = 0; p < _panels.Length; p++) _panels[p].CopyTo(c._panels[p], 0);
        return c;
    }

    /// <summary>
    /// <b>y = A·x for the symmetric A</b>, read from the lower triangle: column j below the diagonal
    /// serves twice, as A[i,j] into row i and by symmetry as A[j,i] into row j. The same summation
    /// <see cref="SymmetricFactorization.Residual(BlockLowerMatrix, Vec{Complex}, Vec{Complex})"/> uses.
    /// </summary>
    public Complex[] Multiply(ReadOnlySpan<Complex> x)
    {
        int n = Size;
        if (x.Length != n)
            throw new ArgumentException($"x has {x.Length} entries against a {n}×{n} matrix.", nameof(x));
        var y = new Complex[n];
        for (int j = 0; j < n; j++)
        {
            var col = Column(j);
            Complex xj = x[j];
            Complex acc = col[0] * xj;
            for (int i = j + 1; i < n; i++)
            {
                Complex a = col[i - j];
                y[i] += a * xj;
                acc  += a * x[i];
            }
            y[j] += acc;
        }
        return y;
    }
}
