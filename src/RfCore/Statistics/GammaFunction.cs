using System;

namespace RfCore.Statistics;

/// <summary>
/// The gamma function's logarithm and the regularized lower incomplete gamma function P(a, x) = γ(a, x)/Γ(a), with
/// its inverse in x. P(a, x) is the CDF of a gamma-distributed variable, which is what a Nakagami-m intensity is
/// (docs/design/propagation-channel.md §3.2, ITU-R P.531 eq (11)).
/// </summary>
public static class GammaFunction
{
    private static readonly double[] Lanczos =
    {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
        12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    };

    /// <summary>ln Γ(a) for a &gt; 0, to about 1e-15 relative.</summary>
    public static double LogGamma(double a)
    {
        if (a < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * a)) - LogGamma(1 - a);
        a -= 1;
        double x = Lanczos[0];
        for (int i = 1; i < 9; i++) x += Lanczos[i] / (a + i);
        double t = a + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (a + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    /// <summary>The regularized lower incomplete gamma function P(a, x): the series below a + 1, the continued fraction
    /// of Q = 1 − P above it.</summary>
    public static double P(double a, double x) => Regularized(a, x, upper: false);

    /// <summary>The regularized upper incomplete gamma function Q(a, x) = 1 − P(a, x), read from the continued fraction
    /// directly above a + 1, so a far upper tail keeps its precision (1 − P is 0 there).</summary>
    public static double Q(double a, double x) => Regularized(a, x, upper: true);

    private static double Regularized(double a, double x, bool upper)
    {
        if (x <= 0) return upper ? 1 : 0;
        double lead = Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        if (x < a + 1)
        {
            double sum = 1 / a, term = sum;
            for (int n = 1; n < 1000; n++)
            {
                term *= x / (a + n);
                sum += term;
                if (Math.Abs(term) < 1e-17 * Math.Abs(sum)) break;
            }
            double pl = Math.Min(1, sum * lead);
            return upper ? 1 - pl : pl;
        }
        const double tiny = 1e-300;
        double b = x + 1 - a, c = 1 / tiny, d = 1 / b, h = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < tiny) d = tiny;
            c = b + an / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-16) break;
        }
        double q = Math.Clamp(lead * h, 0, 1);
        return upper ? q : 1 - q;
    }

    /// <summary>The x at which P(a, x) = p, by bisection on log x (0 &lt; p &lt; 1).</summary>
    public static double InverseP(double a, double p)
    {
        double lo = 1e-300, hi = Math.Max(1, a) * 10;
        while (P(a, hi) < p) hi *= 2;
        for (int i = 0; i < 2000 && hi / lo > 1 + 1e-15; i++)
        {
            double mid = Math.Sqrt(lo * hi);
            if (P(a, mid) < p) lo = mid; else hi = mid;
        }
        return Math.Sqrt(lo * hi);
    }
}
