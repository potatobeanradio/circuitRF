namespace CircuitRF.Core.Expressions;

/// <summary>
/// Least-squares polynomial fit. Fit(v, c, order) returns coefficients [a0, a1, …, a_order]
/// (lowest power first) minimizing Σ (Σ aₖ·vᵢᵏ − cᵢ)². Solves the normal equations VᵀV a = Vᵀc
/// via Gaussian elimination with partial pivoting. Used by the schematic editor's CV→coefficients
/// "Apply" (docs/design/nonlinear-in-linear-engines.md §4.2). UI→Core; engine never fits.
/// </summary>
public static class PolynomialFit
{
    /// <param name="v">bias points (V)</param>
    /// <param name="c">measured capacitance at each v (F)</param>
    /// <param name="order">polynomial order n (≥0); needs at least order+1 distinct points</param>
    /// <returns>order+1 coefficients, lowest power first</returns>
    public static double[] Fit(double[] v, double[] c, int order)
    {
        if (v is null || c is null) throw new ArgumentNullException();
        if (v.Length != c.Length)   throw new ArgumentException("v and c must be the same length");
        if (order < 0)              throw new ArgumentOutOfRangeException(nameof(order));
        int m = v.Length, n = order + 1;
        if (m < n) throw new ArgumentException($"need ≥ {n} points to fit order {order}, got {m}");

        // Vandermonde V (m×n): V[i,k] = v[i]^k.
        var vand = new double[m, n];
        for (int i = 0; i < m; i++)
        {
            double p = 1.0;
            for (int k = 0; k < n; k++) { vand[i, k] = p; p *= v[i]; }
        }

        // Normal equations: A = VᵀV (n×n), b = Vᵀc (n).
        var a = new double[n, n];
        var b = new double[n];
        for (int r = 0; r < n; r++)
        {
            for (int s = 0; s < n; s++)
            {
                double sum = 0.0;
                for (int i = 0; i < m; i++) sum += vand[i, r] * vand[i, s];
                a[r, s] = sum;
            }
            double bsum = 0.0;
            for (int i = 0; i < m; i++) bsum += vand[i, r] * c[i];
            b[r] = bsum;
        }

        return SolveGauss(a, b, n);
    }

    private static double[] SolveGauss(double[,] a, double[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-300)
                throw new InvalidOperationException("PolynomialFit: singular normal matrix (degenerate/duplicate data?)");
            if (piv != col)
            {
                for (int k = 0; k < n; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
