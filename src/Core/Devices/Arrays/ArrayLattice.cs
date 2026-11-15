// brief-paa-2 R-paa2-1 — the lattice of a phased array: rows, columns, pitch, rectangular or triangular, the element numbering of the
// series' D6, and the elements switched off. docs/design/phased-array.md §4.3.
//
// IN src/Core, NOT src/Engine (brief-paa-3): the PhasedArray component builds its S from this lattice at elaboration, and src/Core
// references no layer above it. The array factor that also reads it is in src/Engine/Array.
//
// THE NAMESPACE IS NOT "Array": a namespace of that name would capture every Array.Empty or Array.Sort written beside it.
//
// THE ORIENTATION IS FIXED (D6). Seen from boresight (+z), rows run DOWN the y axis from the top and columns along x from the left;
// element n = (row − 1)·Cols + col, 1-based. Positions are from the array's centre, so a lattice and its array factor need no origin.

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>Rectangular, or triangular: every other row offset by half a column pitch.</summary>
public enum ArrayLatticeKind { Rect, Tri }

/// <summary>R-paa2-1 — where each element of a regular planar array is. Pure: no record, no units beyond metres.</summary>
public sealed class ArrayLattice
{
    /// <summary>The largest side the series builds for: 64 × 64 = 4096 elements (D9).</summary>
    public const int MaxSide = 64;

    private readonly HashSet<int> _off;

    public ArrayLattice(int rows, int cols, double dx, double dy, ArrayLatticeKind kind = ArrayLatticeKind.Rect, IEnumerable<int>? off = null)
    {
        if (rows < 1 || rows > MaxSide || cols < 1 || cols > MaxSide)
            throw new ArgumentOutOfRangeException(nameof(rows), $"An array is 1 to {MaxSide} rows by 1 to {MaxSide} columns; this one is {rows} × {cols}.");
        if (!(dx > 0) && cols > 1) throw new ArgumentOutOfRangeException(nameof(dx), dx, "The column pitch must be positive.");
        if (!(dy > 0) && rows > 1) throw new ArgumentOutOfRangeException(nameof(dy), dy, "The row pitch must be positive.");
        Rows = rows;
        Cols = cols;
        Dx = cols > 1 || kind == ArrayLatticeKind.Tri ? dx : Math.Max(dx, 0);
        Dy = rows > 1 ? dy : Math.Max(dy, 0);
        Kind = kind;
        _off = [];
        foreach (int n in off ?? [])
        {
            if (n < 1 || n > rows * cols)
                throw new ArgumentOutOfRangeException(nameof(off), n, $"Element {n} is not in a {rows} × {cols} array (1…{rows * cols}).");
            _off.Add(n);
        }
    }

    public int Rows { get; }
    public int Cols { get; }

    /// <summary>Column pitch, metres.</summary>
    public double Dx { get; }

    /// <summary>Row pitch, metres.</summary>
    public double Dy { get; }

    public ArrayLatticeKind Kind { get; }

    /// <summary>The 1-based numbers of the elements switched off: weight zero, terminated in Z0.</summary>
    public IReadOnlySet<int> Off => _off;

    public int Count => Rows * Cols;

    /// <summary>D6's element number of a 1-based row and column.</summary>
    public int Number(int row, int col) => (row - 1) * Cols + col;

    /// <summary>The 1-based row and column of element <paramref name="n"/>.</summary>
    public (int Row, int Col) RowCol(int n) => ((n - 1) / Cols + 1, (n - 1) % Cols + 1);

    /// <summary>Whether the element at 0-based <paramref name="index"/> is on.</summary>
    public bool IsOn(int index) => !_off.Contains(index + 1);

    /// <summary>x of 0-based column <paramref name="c"/> before a triangular row's shift, from the centre.</summary>
    public double ColumnX(int c) => (c - (Cols - 1) / 2.0) * Dx;

    /// <summary>y of 0-based row <paramref name="r"/>, from the centre: row 0 is the top.</summary>
    public double RowY(int r) => ((Rows - 1) / 2.0 - r) * Dy;

    /// <summary>The x shift of 0-based row <paramref name="r"/>: half a pitch on every odd row of a triangular lattice, less the mean
    /// shift so the array stays centred; zero on a rectangular one.</summary>
    public double RowShift(int r)
    {
        if (Kind != ArrayLatticeKind.Tri) return 0;
        double mean = 0.5 * (Rows / 2) / Rows;
        return ((r % 2 == 1 ? 0.5 : 0.0) - mean) * Dx;
    }

    /// <summary>The position of 0-based element <paramref name="index"/> from the array's centre, metres.</summary>
    public (double X, double Y) Position(int index)
    {
        int r = index / Cols, c = index % Cols;
        return (ColumnX(c) + RowShift(r), RowY(r));
    }

    /// <summary>What a phase table depends on: the geometry and not the weights or the elements switched off.</summary>
    public (int, int, double, double, ArrayLatticeKind) GeometryKey => (Rows, Cols, Dx, Dy, Kind);

    /// <summary>The same lattice at another pitch, or with other elements off.</summary>
    public ArrayLattice With(double? dx = null, double? dy = null, IEnumerable<int>? off = null)
        => new(Rows, Cols, dx ?? Dx, dy ?? Dy, Kind, off ?? _off);

    public override string ToString()
        => $"{Rows} × {Cols} {(Kind == ArrayLatticeKind.Tri ? "triangular" : "rectangular")}, {Dx * 1e3:G6} × {Dy * 1e3:G6} mm";
}
