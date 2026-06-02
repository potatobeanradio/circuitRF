namespace CircuitRF.Core.Expressions;

/// <summary>
/// Central-difference finite differentiation helper.
/// Used as the AD oracle in the Step-1 gate test (§2.4) and as the production FD fallback tier.
/// </summary>
public static class FiniteDiff
{
    /// <summary>
    /// Compute the gradient of f at the point <paramref name="at"/> by central differences.
    /// Step size h = max(relH * |at[i]|, absH) per dimension.
    /// </summary>
    public static double[] Gradient(
        Func<double[], double> f,
        double[] at,
        double relH = 1e-6,
        double absH = 1e-9)
    {
        int n = at.Length;
        var grad = new double[n];
        var x = (double[])at.Clone();
        for (int i = 0; i < n; i++)
        {
            double h = Math.Max(relH * Math.Abs(at[i]), absH);
            x[i] = at[i] + h; double fp = f(x);
            x[i] = at[i] - h; double fm = f(x);
            x[i] = at[i];     // restore
            grad[i] = (fp - fm) / (2.0 * h);
        }
        return grad;
    }
}
