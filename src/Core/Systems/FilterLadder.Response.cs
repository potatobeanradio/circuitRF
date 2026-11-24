using System.Numerics;

namespace CircuitRF.Core.Systems;

public static partial class FilterLadder
{
    /// <summary>
    /// The insertion loss, dB, of a ladder of ideal L and C between <paramref name="rs"/> and
    /// <paramref name="rl"/>, at each of <paramref name="hz"/>: a small nodal solve per frequency,
    /// the same circuit <see cref="ToCnl"/> writes, fast enough to judge thousands of value choices.
    /// </summary>
    public static double[] InsertionLossDb(IReadOnlyList<FilterElement> elements, double rs, double rl,
                                           IReadOnlyList<double> hz)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { ["p1"] = 0 };
        int Node(string name)
        {
            if (name == "0") return -1;
            if (!index.TryGetValue(name, out int i)) index[name] = i = index.Count;
            return i;
        }
        var ends = new (int A, int B)[elements.Count];
        for (int k = 0; k < elements.Count; k++) ends[k] = (Node(elements[k].NodeA), Node(elements[k].NodeB));
        int load = Node(LoadNode(elements));
        int n = index.Count;
        var y = new Complex[n, n];
        var v = new Complex[n];
        var loss = new double[hz.Count];
        for (int f = 0; f < hz.Count; f++)
        {
            double w = 2 * Math.PI * hz[f];
            Array.Clear(y);
            Array.Clear(v);
            y[0, 0] += 1 / rs;
            if (load >= 0) y[load, load] += 1 / rl;
            v[0] = 1 / rs;   // a 1 V source behind rs, as its Norton equivalent
            for (int k = 0; k < elements.Count; k++)
            {
                var e = elements[k];
                Complex g = e.Inductor ? 1 / new Complex(0, w * e.Value) : new Complex(0, w * e.Value);
                var (a, b) = ends[k];
                if (a >= 0) y[a, a] += g;
                if (b >= 0) y[b, b] += g;
                if (a >= 0 && b >= 0) { y[a, b] -= g; y[b, a] -= g; }
            }
            Solve(y, v, n);
            double s21 = 2 * (load >= 0 ? v[load] : 0).Magnitude * Math.Sqrt(rs / rl);
            loss[f] = s21 > 0 ? -20 * Math.Log10(s21) : double.PositiveInfinity;
        }
        return loss;
    }

    /// <summary>Gaussian elimination with partial pivoting, in place; the solution lands in <paramref name="b"/>.</summary>
    private static void Solve(Complex[,] a, Complex[] b, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = c;
            double best = a[c, c].Magnitude;
            for (int r = c + 1; r < n; r++)
                if (a[r, c].Magnitude > best) { best = a[r, c].Magnitude; p = r; }
            if (best == 0) { Array.Fill(b, Complex.Zero); return; }
            if (p != c)
            {
                for (int k = c; k < n; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
                (b[c], b[p]) = (b[p], b[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                Complex m = a[r, c] / a[c, c];
                if (m == Complex.Zero) continue;
                for (int k = c; k < n; k++) a[r, k] -= m * a[c, k];
                b[r] -= m * b[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            Complex sum = b[r];
            for (int k = r + 1; k < n; k++) sum -= a[r, k] * b[k];
            b[r] = sum / a[r, r];
        }
    }
}
