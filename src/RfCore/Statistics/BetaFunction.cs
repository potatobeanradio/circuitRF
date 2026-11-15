// The regularized incomplete beta function and its inverse, for the exact (Clopper–Pearson) binomial interval a
// counted bit error rate is stated with (brief PCH-4).
//
//   I_x(a, b) = B(x; a, b) / B(a, b), by its continued fraction (the modified Lentz evaluation), on the side of
//   x < (a + 1)/(a + b + 2) where it converges fast, by symmetry I_x(a, b) = 1 − I_(1−x)(b, a) on the other.
//   The inverse by bisection: I_x is increasing in x, and 200 halvings reach double precision.

using System;

namespace RfCore.Statistics;

public static class BetaFunction
{
    /// <summary>I_x(a, b), a and b above 0, x in [0, 1].</summary>
    public static double Regularized(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lnFront = GammaFunction.LogGamma(a + b) - GammaFunction.LogGamma(a) - GammaFunction.LogGamma(b)
                       + a * Math.Log(x) + b * Math.Log(1 - x);
        return x < (a + 1) / (a + b + 2)
            ? Math.Exp(lnFront) * ContinuedFraction(x, a, b) / a
            : 1 - Math.Exp(lnFront) * ContinuedFraction(1 - x, b, a) / b;
    }

    /// <summary>The continued fraction of I_x(a, b) (without its front factor), modified Lentz.</summary>
    private static double ContinuedFraction(double x, double a, double b)
    {
        const double tiny = 1e-300, eps = 1e-16;
        double c = 1, d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d;
        double f = d;
        for (int m = 1; m <= 10000; m++)
        {
            // the even step, then the odd one
            double num = m * (b - m) * x / ((a + 2 * m - 1) * (a + 2 * m));
            d = 1 + num * d; if (Math.Abs(d) < tiny) d = tiny; d = 1 / d;
            c = 1 + num / c; if (Math.Abs(c) < tiny) c = tiny;
            f *= d * c;
            num = -(a + m) * (a + b + m) * x / ((a + 2 * m) * (a + 2 * m + 1));
            d = 1 + num * d; if (Math.Abs(d) < tiny) d = tiny; d = 1 / d;
            c = 1 + num / c; if (Math.Abs(c) < tiny) c = tiny;
            double step = d * c;
            f *= step;
            if (Math.Abs(step - 1) < eps) break;
        }
        return f;
    }

    /// <summary>The x at which I_x(a, b) = <paramref name="p"/>.</summary>
    public static double Inverse(double p, double a, double b)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        double lo = 0, hi = 1;
        for (int i = 0; i < 200 && hi - lo > 0; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid <= lo || mid >= hi) break;
            if (Regularized(mid, a, b) < p) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>The exact (Clopper–Pearson) interval of a binomial proportion: <paramref name="k"/> events in
    /// <paramref name="n"/> trials, at <paramref name="confidence"/> (two-sided). (NaN, NaN) for no trials.</summary>
    public static (double Low, double High) BinomialInterval(long k, long n, double confidence = 0.95)
    {
        if (n <= 0) return (double.NaN, double.NaN);
        double alpha = 1 - confidence;
        double lo = k == 0 ? 0 : Inverse(alpha / 2, k, n - k + 1);
        double hi = k == n ? 1 : Inverse(1 - alpha / 2, k + 1, n - k);
        return (lo, hi);
    }
}
