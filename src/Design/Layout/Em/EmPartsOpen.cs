// A run with component ports solves each part's pads as a port of its own, so its S-matrix has one more port per part. Read raw,
// every one of those ports is terminated in its reference impedance: a series part's gap is bridged by 50 ohms, and S21 across a
// series capacitor reads about -4 dB where the board with the part not fitted reads about -50 dB. The board a 3D view or a layout
// draws has nothing across those pads. This file adds that board to the result: the other ports' S-matrix with every part's port
// open, as a network group beside the run's own S, which the automatic plot reads. The run's S and its Touchstone are unchanged.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Design.Layout.Em;

public static class EmPartsOpen
{
    /// <summary>The result's group holding the board with every part's port open: <c>S</c> and <c>Z0</c> over the other ports.</summary>
    public const string Group = "PartsOpen";

    /// <summary>
    /// Adds <see cref="Group"/> to <paramref name="data"/>: the S-matrix of every port not in <paramref name="openPorts"/>, with
    /// each port in it left open. Nothing is added when no port is opened, when every port would be, or when the result's S is
    /// not a frequency-by-port matrix with a per-port Z0.
    /// </summary>
    /// <param name="openPorts">1-based positions in the result's port order: the S cube's port axes in index order.</param>
    /// <returns>True when the group was added.</returns>
    public static bool Add(DataSet data, IReadOnlyCollection<int> openPorts)
    {
        if (openPorts.Count == 0) return false;
        if (data.Groups.FirstOrDefault(g => g != Group && data.CubesIn(g).ContainsKey(NetworkMetrics.SCubeName)) is not { } group) return false;
        var cubes = data.CubesIn(group);
        var s = cubes[NetworkMetrics.SCubeName];
        if (s.Rank != 3 || s.DataKind != DataKind.Complex || s.Axes[1].Name != "i" || s.Axes[2].Name != "j") return false;
        if (!cubes.TryGetValue(NetworkMetrics.Z0CubeName, out var z0Cube)) return false;

        int n = s.Axes[1].Length;
        if (s.Axes[2].Length != n) return false;
        Complex[] z0 = z0Cube.DataKind == DataKind.Complex ? z0Cube.ComplexValues : [.. z0Cube.RealValues.Select(v => new Complex(v, 0))];
        if (z0.Length != n) return false;

        double[] numbers = s.Axes[1].Values;
        int[] open = [.. Enumerable.Range(0, n).Where(k => openPorts.Contains(k + 1))];
        int[] kept = [.. Enumerable.Range(0, n).Except(open)];
        if (open.Length == 0 || kept.Length == 0) return false;

        int nf = s.Axes[0].Length, m = kept.Length;
        var values = s.ComplexValues;
        var reduced = new Complex[nf * m * m];
        var full = new Complex[n, n];
        for (int f = 0; f < nf; f++)
        {
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++) full[a, b] = values[(f * n + a) * n + b];
            var r = Open(full, kept, open);
            for (int a = 0; a < m; a++)
                for (int b = 0; b < m; b++) reduced[(f * m + a) * m + b] = r?[a, b] ?? new Complex(double.NaN, double.NaN);
        }

        Axis Kept(Axis axis) => new(axis.Name, [.. kept.Select(k => axis.Values[k])], axis.Unit,
                                    axis.Labels is { } labels ? [.. kept.Select(k => labels[k])] : null);
        data.AddToGroup(Group, NetworkMetrics.SCubeName, new DataCube([s.Axes[0], Kept(s.Axes[1]), Kept(s.Axes[2])], reduced) { Unit = s.Unit });
        var portAxis = z0Cube.Rank == 1 ? Kept(z0Cube.Axes[0]) : new Axis("port", [.. kept.Select(k => numbers[k])], "port");
        data.AddToGroup(Group, NetworkMetrics.Z0CubeName, new DataCube([portAxis], [.. kept.Select(k => z0[k])]) { Unit = z0Cube.Unit });
        return true;
    }

    /// <summary>The 1-based positions of the ports in <paramref name="ports"/> (in the result's port order) that are parts.</summary>
    public static IReadOnlyCollection<int> PositionsOf<T>(IReadOnlyList<T> ports, Func<T, bool> isPart)
        => [.. Enumerable.Range(0, ports.Count).Where(k => isPart(ports[k])).Select(k => k + 1)];

    /// <summary>
    /// The kept ports' S-matrix with every opened port open (Γ = 1, which is an open circuit in any reference):
    /// S' = S_kk + S_ko (I − S_oo)⁻¹ S_ok. Null when I − S_oo is singular at this frequency.
    /// </summary>
    internal static Complex[,]? Open(Complex[,] s, int[] kept, int[] open)
    {
        int m = kept.Length, q = open.Length;
        // Solve (I − S_oo) X = S_ok for X (q × m), by elimination with partial pivoting.
        var a = new Complex[q, q + m];
        for (int r = 0; r < q; r++)
        {
            for (int c = 0; c < q; c++) a[r, c] = (r == c ? Complex.One : Complex.Zero) - s[open[r], open[c]];
            for (int c = 0; c < m; c++) a[r, q + c] = s[open[r], kept[c]];
        }
        for (int col = 0; col < q; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < q; r++)
                if (a[r, col].Magnitude > a[pivot, col].Magnitude) pivot = r;
            if (a[pivot, col].Magnitude < 1e-14) return null;
            if (pivot != col)
                for (int c = 0; c < q + m; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            for (int r = 0; r < q; r++)
            {
                if (r == col || a[r, col] == Complex.Zero) continue;
                var factor = a[r, col] / a[col, col];
                for (int c = col; c < q + m; c++) a[r, c] -= factor * a[col, c];
            }
        }

        var result = new Complex[m, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
            {
                var sum = s[kept[i], kept[j]];
                for (int k = 0; k < q; k++) sum += s[kept[i], open[k]] * a[k, q + j] / a[k, k];
                result[i, j] = sum;
            }
        return result;
    }
}
