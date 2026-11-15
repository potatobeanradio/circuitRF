// brief-paa-4 R-paa4-3 / R-paa4-4 — the arithmetic of a Feed-mode array's weights: the three separable amplitude tapers and the
// quantisation a phase shifter and an attenuator put on each weight (docs/design/phased-array.md §4.2-§4.3).
//
// THE TAPERS ARE ONE-DIMENSIONAL AND MULTIPLIED. taylor(sll, nbar), chebyshev(sll) and cosq(q) are each a line taper evaluated on the
// rows and on the columns and multiplied, so a principal-plane cut of the array is exactly the line's own pattern; each line is
// normalised to 1 at its largest element, so the product is 1 at the peak.
//
//   taylor    the continuous n̄ Taylor line source sampled at the element centres, p = (i − (M−1)/2)/M over an aperture of length 1:
//             g(p) = 1 + 2·Σ_{m<n̄} F_m·cos(2πmp), F_m = (−1)^(m+1)/2 · Π_k(1 − m²/z_k²) / Π_{k≠m}(1 − m²/k²),
//             z_k² = σ²·(A² + (k − ½)²), σ² = n̄²/(A² + (n̄ − ½)²), A = acosh(R)/π, R the sidelobe ratio;
//   chebyshev the Dolph weights of an M-element line: the array factor T_{M−1}(x0·cos(ψ/2)), x0 = cosh(acosh(R)/(M−1)), sampled at M
//             points after the factor e^{j(M−1)ψ/2} that makes it a 2π-periodic polynomial of degree M−1, so the M-point inverse DFT
//             is exact (an expansion of T in powers of x cancels catastrophically at M = 64);
//   cosq      cos^q(πp) on the same element centres.
//
// THE SIDELOBE LEVEL is accepted as −30 or 30: either means 30 dB down.
//
// QUANTISATION (R-paa4-4) rounds a phase to 2π/2^b and an amplitude's attenuation, measured from the largest amplitude, to a step in dB;
// 0 means continuous for both. A negative amplitude is a magnitude and half a turn of phase, so it is quantised as one.

using System.Collections.Concurrent;

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>R-paa4-3 / R-paa4-4 — line tapers and weight quantisation.</summary>
public static class ArrayWeights
{
    private static readonly ConcurrentDictionary<(char Kind, int M, double A, double B), double[]> _lines = new();

    /// <summary>The n̄ Taylor line taper of <paramref name="m"/> elements, peak 1.</summary>
    public static double[] Taylor(int m, double sllDb, int nbar) => _lines.GetOrAdd(('t', m, Math.Abs(sllDb), nbar), _ => TaylorLine(m, Math.Abs(sllDb), nbar));

    /// <summary>The Dolph-Chebyshev line taper of <paramref name="m"/> elements, peak 1.</summary>
    public static double[] Chebyshev(int m, double sllDb) => _lines.GetOrAdd(('c', m, Math.Abs(sllDb), 0), _ => ChebyshevLine(m, Math.Abs(sllDb)));

    /// <summary>The cos^q line taper of <paramref name="m"/> elements, peak 1.</summary>
    public static double[] CosQ(int m, double q) => _lines.GetOrAdd(('q', m, q, 0), _ => CosQLine(m, q));

    /// <summary>The element centre of index <paramref name="i"/> (0-based) of <paramref name="m"/>, on an aperture of length 1.</summary>
    private static double Centre(int i, int m) => (i - (m - 1) / 2.0) / m;

    private static double[] TaylorLine(int m, double sllDb, int nbar)
    {
        if (m == 1) return [1];
        double r = Math.Pow(10, sllDb / 20);
        double a = Math.Log(r + Math.Sqrt(r * r - 1)) / Math.PI;
        double sigma2 = nbar * nbar / (a * a + (nbar - 0.5) * (nbar - 0.5));
        var f = new double[Math.Max(0, nbar - 1)];
        for (int k = 1; k < nbar; k++)
        {
            double num = 1, den = 1;
            for (int i = 1; i < nbar; i++)
            {
                num *= 1 - k * k / (sigma2 * (a * a + (i - 0.5) * (i - 0.5)));
                if (i != k) den *= 1 - (double)k * k / ((double)i * i);
            }
            f[k - 1] = (k % 2 == 1 ? 0.5 : -0.5) * num / den;
        }
        var w = new double[m];
        for (int i = 0; i < m; i++)
        {
            double p = Centre(i, m), g = 1;
            for (int k = 1; k < nbar; k++) g += 2 * f[k - 1] * Math.Cos(2 * Math.PI * k * p);
            w[i] = g;
        }
        return Normalised(w);
    }

    private static double[] ChebyshevLine(int m, double sllDb)
    {
        if (m == 1) return [1];
        int order = m - 1;
        double r = Math.Pow(10, sllDb / 20);
        double x0 = Math.Cosh(Math.Log(r + Math.Sqrt(r * r - 1)) / order);
        var p = new System.Numerics.Complex[m];
        for (int k = 0; k < m; k++)
        {
            double psi = 2 * Math.PI * k / m;
            p[k] = T(order, x0 * Math.Cos(psi / 2)) * System.Numerics.Complex.FromPolarCoordinates(1, order * psi / 2);
        }
        var w = new double[m];
        for (int i = 0; i < m; i++)
        {
            System.Numerics.Complex s = 0;
            for (int k = 0; k < m; k++) s += p[k] * System.Numerics.Complex.FromPolarCoordinates(1, -i * 2 * Math.PI * k / m);
            w[i] = s.Real / m;
        }
        return Normalised(w);
    }

    /// <summary>The Chebyshev polynomial T_n(x) at any real x.</summary>
    private static double T(int n, double x)
    {
        if (Math.Abs(x) <= 1) return Math.Cos(n * Math.Acos(x));
        double c = Math.Cosh(n * Math.Log(Math.Abs(x) + Math.Sqrt(x * x - 1)));
        return x > 0 || n % 2 == 0 ? c : -c;
    }

    private static double[] CosQLine(int m, double q)
    {
        var w = new double[m];
        for (int i = 0; i < m; i++) w[i] = Math.Pow(Math.Cos(Math.PI * Centre(i, m)), q);
        return Normalised(w);
    }

    private static double[] Normalised(double[] w)
    {
        double peak = w.Max(Math.Abs);
        if (peak > 0) for (int i = 0; i < w.Length; i++) w[i] /= peak;
        return w;
    }

    // ── quantisation ────────────────────────────────────────────────────────────────────────────

    /// <summary>R-paa4-4 — <paramref name="phaseRad"/> rounded to 2π/2^<paramref name="bits"/>; unchanged at 0 bits.</summary>
    public static double QuantisePhase(double phaseRad, int bits)
    {
        if (bits <= 0) return phaseRad;
        double step = 2 * Math.PI / Math.Pow(2, bits);
        return step * Math.Round(phaseRad / step, MidpointRounding.AwayFromZero);
    }

    /// <summary>R-paa4-4 — each magnitude's attenuation from <paramref name="largest"/> rounded to <paramref name="stepDb"/>; unchanged at 0.
    /// A zero magnitude stays zero.</summary>
    public static double QuantiseAmplitude(double magnitude, double largest, double stepDb)
    {
        if (!(stepDb > 0) || !(magnitude > 0) || !(largest > 0)) return magnitude;
        double att = -20 * Math.Log10(magnitude / largest);
        return largest * Math.Pow(10, -stepDb * Math.Round(att / stepDb, MidpointRounding.AwayFromZero) / 20);
    }
}
