namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>
/// Gauss-Legendre quadrature, written here rather than taken from the engine so the Tier 0 checks
/// of <c>Kernel2D</c>'s closed forms are against something genuinely independent of them.
/// </summary>
public static class Quadrature
{
    private static readonly Dictionary<int, (double[] X, double[] W)> Cache = new();

    public static (double[] X, double[] W) Nodes(int n)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(n, out var cached)) return cached;

            var x = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
                double pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1;
                        p1 = p0;
                        p0 = ((2 * j + 1) * z * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (z * p0 - p1) / (z * z - 1);
                    double dz = p0 / pp;
                    z -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i] = z;
                w[i] = 2.0 / ((1 - z * z) * pp * pp);
            }
            var res = (x, w);
            Cache[n] = res;
            return res;
        }
    }

    public static double Integrate(Func<double, double> f, double a, double b, int n = 64)
    {
        var (x, w) = Nodes(n);
        double half = 0.5 * (b - a), mid = 0.5 * (a + b);
        double s = 0;
        for (int i = 0; i < n; i++) s += w[i] * f(mid + half * x[i]);
        return s * half;
    }

    /// <summary>
    /// Integrate a function with an integrable logarithmic singularity at <paramref name="a"/> by
    /// splitting [a, b] into geometrically shrinking panels toward a. The integrand is analytic on
    /// each panel, so ordinary Gauss-Legendre converges to round-off on every one of them.
    /// </summary>
    public static double IntegrateLogSingularAt(Func<double, double> f, double a, double b,
                                                int levels = 60, int n = 24)
    {
        double total = 0;
        double hi = b;
        for (int k = 0; k < levels; k++)
        {
            double lo = a + (b - a) * Math.Pow(0.5, k + 1);
            total += Integrate(f, lo, hi, n);
            hi = lo;
        }
        return total;   // the remaining sliver is 2^-levels of the range; at 60 it is below eps
    }
}
