// brief-paa-2 R-paa2-2a — the array factor in direction cosines, (u, v) = (sinθ·cosφ, sinθ·sinφ), as the 2-D FFT of the lattice's
// weights zero-padded to at least 8× the array in each direction (note §6.2), sampled onto the visible circle.
//
// With x_c = (c − (C−1)/2)·dx, the factor Σ_c w_c·exp(j·k·x_c·u) at u_m = −m·λ/(M·dx) is exp(−j·k·(C−1)/2·dx·u_m) times the
// forward DFT of the weights at bin m, which is what RfCore's FFT computes. So bin (m, l) is the point (u_m, v_l), aliased into the
// visible region by the FFT's periodicity in u (period λ/dx) — the grating lobes, exactly where the lattice puts them.
//
// A TRIANGULAR LATTICE IS ITS TWO RECTANGULAR SUB-LATTICES (even rows, odd rows), each of row pitch 2·dy, transformed on the SAME
// (u, v) bins and added with the odd rows' shift as an exact phase per bin. That is the lattice's own skewed grid, on one set of points.

using System.Numerics;
using RfCore.Signals;

using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>The array factor at points of the visible region, from the weights' FFT.</summary>
/// <param name="U">u of each point.</param>
/// <param name="V">v of each point.</param>
/// <param name="Factor">The array factor there, on the same scale as <see cref="ArrayPattern.Factor"/>.</param>
/// <param name="SizeU">The transform's length in u.</param>
/// <param name="SizeV">The transform's length in v.</param>
public sealed record ArrayUvMap(double[] U, double[] V, Complex[] Factor, int SizeU, int SizeV);

/// <summary>R-paa2-2a — the (u, v) evaluation.</summary>
public static class ArrayUv
{
    /// <summary>The smallest zero padding, in multiples of the array's side.</summary>
    public const int Padding = 8;

    /// <summary>The array factor of <paramref name="weights"/> (row-major; zero where switched off) at every FFT bin inside the
    /// visible circle u² + v² ≤ 1.</summary>
    public static ArrayUvMap Map(ArrayLattice lattice, IReadOnlyList<Complex> weights, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        double lambda = Mom.EmConstants.C0 / hz, k = 2 * Math.PI / lambda;
        bool tri = lattice.Kind == ArrayLatticeKind.Tri && lattice.Rows > 1;
        int rowsPerSub = tri ? (lattice.Rows + 1) / 2 : lattice.Rows;
        double dy = tri ? 2 * lattice.Dy : lattice.Dy;
        int mu = PowerOfTwoAtLeast(Padding * lattice.Cols), mv = PowerOfTwoAtLeast(Padding * rowsPerSub);
        // A single column or row has no pitch along it: one bin, u or v taken as 0 there.
        double du = lattice.Cols > 1 ? lambda / (mu * lattice.Dx) : 0;
        double dv = lattice.Rows > 1 ? lambda / (mv * dy) : 0;
        if (lattice.Cols == 1) mu = 1;
        if (lattice.Rows == 1) mv = 1;

        var w = new Complex[lattice.Count];
        for (int i = 0; i < w.Length; i++) w[i] = lattice.IsOn(i) ? weights[i] : Complex.Zero;

        // Each sub-lattice's transform, its rows reversed so row index runs along +y (row 0 is the top).
        Complex[] Transform(int parity)
        {
            var a = new Complex[mu * mv];
            for (int r = 0; r < lattice.Rows; r++)
            {
                if (tri && r % 2 != parity) continue;
                int sr = tri ? r / 2 : r;
                int ry = rowsPerSub - 1 - sr;
                for (int c = 0; c < lattice.Cols; c++) a[ry * mu + c] = w[r * lattice.Cols + c];
            }
            var row = new Complex[mu];
            for (int y = 0; y < mv; y++)
            {
                Array.Copy(a, y * mu, row, 0, mu);
                Fft.Transform(row, inverse: false);
                Array.Copy(row, 0, a, y * mu, mu);
            }
            var col = new Complex[mv];
            for (int x = 0; x < mu; x++)
            {
                for (int y = 0; y < mv; y++) col[y] = a[y * mu + x];
                Fft.Transform(col, inverse: false);
                for (int y = 0; y < mv; y++) a[y * mu + x] = col[y];
            }
            return a;
        }

        var even = Transform(0);
        var odd = tri ? Transform(1) : null;
        var us = new List<double>();
        var vs = new List<double>();
        var af = new List<Complex>();
        double x0 = lattice.ColumnX(0);
        for (int l = 0; l < mv; l++)
        {
            int ls = l <= mv / 2 ? l : l - mv;           // signed bin
            double v = -ls * dv;
            for (int m = 0; m < mu; m++)
            {
                int ms = m <= mu / 2 ? m : m - mu;
                double u = -ms * du;
                if (u * u + v * v > 1 + 1e-12) continue;
                // Sub-lattice 0 holds the even rows (0, 2, …); its stored row 0 is where its LOWEST row would be.
                Complex sum = even[l * mu + m] * Phase(k, x0 + lattice.RowShift(0), StoredRowZeroY(lattice, tri, rowsPerSub, 0), u, v);
                if (odd is not null) sum += odd[l * mu + m] * Phase(k, x0 + lattice.RowShift(1), StoredRowZeroY(lattice, tri, rowsPerSub, 1), u, v);
                us.Add(u);
                vs.Add(v);
                af.Add(sum);
            }
        }
        return new ArrayUvMap([.. us], [.. vs], [.. af], mu, mv);
    }

    /// <summary>exp(j·k·(x·u + y·v)): the position of a sub-lattice's first stored element.</summary>
    private static Complex Phase(double k, double x, double y, double u, double v)
    {
        var (s, c) = Math.SinCos(k * (x * u + y * v));
        return new Complex(c, s);
    }

    /// <summary>y of a sub-lattice's stored row 0: the row 2·(rowsPerSub − 1) + parity, which RowY extends past the array when the
    /// sub-lattice is a row short (every row when not triangular).</summary>
    private static double StoredRowZeroY(ArrayLattice lattice, bool tri, int rowsPerSub, int parity)
        => lattice.RowY(tri ? 2 * (rowsPerSub - 1) + parity : lattice.Rows - 1);

    private static int PowerOfTwoAtLeast(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
