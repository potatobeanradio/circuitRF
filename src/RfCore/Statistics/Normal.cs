using System;

namespace RfCore.Statistics;

/// <summary>
/// The standard normal distribution — its CDF Φ, its inverse Φ⁻¹, its density φ, and the complementary error
/// function they rest on (docs/design/yield.md §5).
///
/// <para><b>Here, in RfCore, because every layer needs it</b>: the expression engine (<c>normq</c>), the yield
/// engine, and the propagation models (shadowing margins, location percentages: docs/design/propagation-channel.md
/// §3). <c>CircuitRF.Core.Expressions.NormalDistribution</c> and <c>Engine.Statistics.SpecialFunctions</c> forward
/// here, so there is one implementation.</para>
/// </summary>
public static class Normal
{
    /// <summary>
    /// erfc x to near machine precision, RELATIVE precision in the upper tail — which is where a
    /// probability of a non-physical draw (~1e-9) is read. Below x = 2 the Maclaurin series of erf, whose
    /// terms are all well scaled there; above it the classical continued fraction
    /// erfc x = e^(−x²)/√π · 1/(x + ½/(x + 1/(x + 3⁄2/(x + …)))), evaluated by the modified Lentz method.
    /// </summary>
    public static double Erfc(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x < 0) return 2 - Erfc(-x);
        if (x < 2)
        {
            double sum = 0, term = x, x2 = x * x;
            for (int n = 0; n < 200; n++)
            {
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
                term *= -x2 / (n + 1);
            }
            return 1 - 2 / Math.Sqrt(Math.PI) * sum;
        }
        if (x > 27) return 0;

        // f = x + a1/(x + a2/(x + …)), a_k = k/2.
        const double tiny = 1e-300;
        double f = x, c = x, d = 0;
        for (int k = 1; k < 500; k++)
        {
            double a = k / 2.0;
            d = x + a * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = x + a / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            double delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        return Math.Exp(-x * x) / Math.Sqrt(Math.PI) / f;
    }

    /// <summary>The standard normal CDF Φ(z), accurate relatively in the lower tail.</summary>
    public static double Cdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

    /// <summary>
    /// Φ⁻¹(p), the standard normal quantile, to full double precision: Acklam's rational approximation
    /// (relative error 1.15e-9) refined by one Halley step against <see cref="Cdf"/>. The upper
    /// half is taken by symmetry, Φ⁻¹(p) = −Φ⁻¹(1 − p), where 1 − p is exact, so both tails are read
    /// where <see cref="Cdf"/> is relatively accurate. −∞ at 0, +∞ at 1.
    /// </summary>
    public static double InverseCdf(double p)
    {
        if (double.IsNaN(p) || p < 0 || p > 1) return double.NaN;
        if (p == 0) return double.NegativeInfinity;
        if (p == 1) return double.PositiveInfinity;
        if (p > 0.5) return -InverseCdf(1 - p);

        double x;
        if (p < 0.02425)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q
                    - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
              / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q
                    + 3.754408661907416e+00) * q + 1);
        }
        else
        {
            double q = p - 0.5, r = q * q;
            x = (((((-3.969683028665376e+01 * r + 2.209460984245205e+02) * r - 2.759285104469687e+02) * r
                    + 1.383577518672690e+02) * r - 3.066479806614716e+01) * r + 2.506628277459239e+00) * q
              / (((((-5.447609879822406e+01 * r + 1.615858368580409e+02) * r - 1.556989798598866e+02) * r
                    + 6.680131188771972e+01) * r - 1.328068155288572e+01) * r + 1);
        }

        // Halley: e = Φ(x) − p, u = e/φ(x), x ← x − u/(1 + x·u/2).
        double e = Cdf(x) - p;
        double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }

    /// <summary>The standard normal density φ(z).</summary>
    public static double Pdf(double z) => Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI);

    /// <summary>The inverse complementary normal distribution Q⁻¹(x) = Φ⁻¹(1 − x): the z exceeded with probability
    /// x. Q⁻¹(0.01) = 2.326…; Q⁻¹(0.5) = 0.</summary>
    public static double InverseQ(double x) => -InverseCdf(x);
}
