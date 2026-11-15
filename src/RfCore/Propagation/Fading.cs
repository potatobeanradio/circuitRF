// ================================================================
//  Fading.cs  —  a tap's small-scale fading as a sum of sinusoids (brief PCH-7, design note §4.3)
//
//  A tap's complex gain is a stationary process of unit mean power:
//
//    Rayleigh   g(t) = (1/√N) Σ_k e^(j(2π f_k t + φ_k))
//    Rician     √(K/(K+1)) e^(j(2π f_L t + φ_L)) + √(1/(K+1)) g(t),  f_L = 0.7 f_D
//    Nakagami   g(t) with its envelope mapped through the Nakagami-m quantile of the Rayleigh one, its phase kept
//
//  The N frequencies come from the Doppler spectrum, at N equally spaced points of its quantile function turned by one
//  drawn offset (so every realization has exactly the spectrum's moments, and the union over drops is the spectrum):
//
//    classical  f_k = f_D cos α_k,  α_k = (2πk + θ)/N        (arrival uniform in azimuth)
//    flat       f_k = f_D (2(k + θ/2π)/N − 1)                 (uniform on ±f_D)
//    gaussian   f_k = σ Φ⁻¹((k + θ/2π)/N),  σ = f_D/√2        (the classical spectrum's RMS spread)
//    los        f_k = f_D                                      (one line)
//
//  and each sinusoid has a phase φ_k drawn uniformly. Equally spaced angles make the classical spectrum's time
//  autocorrelation of ONE realization the trapezoid rule for J0, exact to J_N(2πf_Dτ): the error is below 1e-4 over
//  J0's first three zeros with N = 16. The envelope distribution of N equal phasors departs from Rayleigh by O(1/N);
//  Sinusoids= sets N.
//
//  Every draw is StatDraws.Hash(Seed, Drop, stream, k), the stream named by the channel instance, the tap and the
//  quantity (angle, phase, los): a realization is a function of those alone, evaluated at any t without state, and a
//  tap's gain does not depend on which other taps exist.
//
//  The 0.7 f_D of the line-of-sight component is 3GPP TR 38.901 V18.0.0 §7.7.2's (the Doppler of TDL-D's and TDL-E's
//  line-of-sight tap), used for every Rician tap.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    /// <summary>A tap's amplitude distribution.</summary>
    public enum FadingKind { Static, Rayleigh, Rician, Nakagami }

    /// <summary>A tap's Doppler spectrum shape.</summary>
    public enum DopplerShape { Classical, Flat, Gaussian, Los }

    /// <summary>
    /// What a tap's fading is: its distribution, its Doppler spectrum and maximum Doppler f_D (Hz), the Rician K (linear,
    /// line-of-sight power over the diffuse power) and the Nakagami m.
    /// </summary>
    public sealed record TapFading(FadingKind Kind, DopplerShape Spectrum, double FdHz, double K = 0, double M = 1)
    {
        /// <summary>The words <c>Fading=</c> takes.</summary>
        public static readonly string[] FadingWords = ["rayleigh", "rician", "nakagami"];

        /// <summary>The words <c>Doppler=</c> takes.</summary>
        public static readonly string[] DopplerWords = ["classical", "flat", "gaussian", "los"];

        /// <summary>A static tap: gain 1 at every instant.</summary>
        public static readonly TapFading Static = new(FadingKind.Static, DopplerShape.Classical, 0);

        /// <summary>The Doppler of a Rician tap's line-of-sight component, 0.7 f_D (TR 38.901 §7.7.2).</summary>
        public double LosDopplerHz => Spectrum == DopplerShape.Los ? FdHz : 0.7 * FdHz;

        public static bool TryParseKind(string word, out FadingKind kind)
        {
            kind = word.Trim().ToLowerInvariant() switch
            {
                "rayleigh" => FadingKind.Rayleigh,
                "rician" or "ricean" => FadingKind.Rician,
                "nakagami" => FadingKind.Nakagami,
                _ => FadingKind.Static,
            };
            return kind != FadingKind.Static;
        }

        public static bool TryParseSpectrum(string word, out DopplerShape shape)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "classical": shape = DopplerShape.Classical; return true;
                case "flat": shape = DopplerShape.Flat; return true;
                case "gaussian": shape = DopplerShape.Gaussian; return true;
                case "los": shape = DopplerShape.Los; return true;
                default: shape = DopplerShape.Classical; return false;
            }
        }

        /// <summary>The ensemble autocorrelation E[g(t + τ) g*(t)] of the tap's gain at lag <paramref name="tau"/> (s): the
        /// spectrum's own (J0, sinc, Gaussian, a line), with a Rician tap's line-of-sight line added; the underlying Gaussian
        /// process's for a Nakagami tap.</summary>
        public Complex Autocorrelation(double tau)
        {
            if (Kind == FadingKind.Static) return Complex.One;
            double x = 2 * Math.PI * FdHz * tau;
            Complex diffuse = Spectrum switch
            {
                DopplerShape.Classical => FadingMath.J0(x),
                DopplerShape.Flat => x == 0 ? 1 : Math.Sin(x) / x,
                DopplerShape.Gaussian => Math.Exp(-x * x / 4),          // σ = f_D/√2: e^(−2π²σ²τ²)
                _ => Complex.FromPolarCoordinates(1, x),
            };
            if (Kind != FadingKind.Rician) return diffuse;
            return (K * Complex.FromPolarCoordinates(1, 2 * Math.PI * LosDopplerHz * tau) + diffuse) / (K + 1);
        }
    }

    /// <summary>
    /// One realization of a tap's fading: a deterministic function of (Seed, Drop, stream) evaluated at any time. See the file
    /// header for the construction.
    /// </summary>
    public sealed class FadingProcess
    {
        /// <summary>The number of sinusoids when <c>Sinusoids=</c> is not stated: the smallest whose envelope passes brief
        /// PCH-7's goodness-of-fit gate with margin (src/RfCore/RESOLVED.md records the measurement).</summary>
        public const int DefaultSinusoids = 64;

        public TapFading Spec { get; }
        private readonly double[] _w, _phi;
        private readonly double _losW, _losPhi, _a, _b;

        /// <param name="spec">The distribution and spectrum.</param>
        /// <param name="seed">The run's seed.</param>
        /// <param name="drop">The drop (the realization's index).</param>
        /// <param name="stream">The stream's name: the channel instance and the tap, e.g. <c>CH1:tap3</c>.</param>
        /// <param name="sinusoids">N, at least 4.</param>
        public FadingProcess(TapFading spec, ulong seed, long drop, string stream, int sinusoids = DefaultSinusoids)
        {
            Spec = spec;
            int n = spec.Kind == FadingKind.Static ? 0 : Math.Max(4, sinusoids);
            _w = new double[n];
            _phi = new double[n];
            if (n == 0) { _a = 0; _b = 1; return; }
            ulong angle = StatDraws.Id(stream + ":angle"), phase = StatDraws.Id(stream + ":phase");
            double u = StatDraws.Uniform(seed, drop, angle, 0);      // the one offset, in (0, 1)
            for (int k = 0; k < n; k++)
            {
                double q = (k + u) / n;                              // (0, 1)
                double f = spec.Spectrum switch
                {
                    DopplerShape.Classical => spec.FdHz * Math.Cos(2 * Math.PI * q),
                    DopplerShape.Flat => spec.FdHz * (2 * q - 1),
                    DopplerShape.Gaussian => spec.FdHz / Math.Sqrt(2) * Normal.InverseCdf(q),
                    _ => spec.FdHz,
                };
                _w[k] = 2 * Math.PI * f;
                _phi[k] = 2 * Math.PI * StatDraws.Uniform(seed, drop, phase, k);
            }
            if (spec.Kind == FadingKind.Rician)
            {
                _losW = 2 * Math.PI * spec.LosDopplerHz;
                _losPhi = 2 * Math.PI * StatDraws.Uniform(seed, drop, StatDraws.Id(stream + ":los"), 0);
                _a = Math.Sqrt(spec.K / (spec.K + 1));
                _b = Math.Sqrt(1 / (spec.K + 1)) / Math.Sqrt(n);
            }
            else { _a = 0; _b = 1 / Math.Sqrt(n); }
        }

        /// <summary>The number of sinusoids (0 for a static tap).</summary>
        public int Sinusoids => _w.Length;

        /// <summary>The tap's complex gain at time <paramref name="t"/> (s); 1 for a static tap.</summary>
        public Complex Gain(double t)
        {
            if (_w.Length == 0) return Complex.One;
            double re = 0, im = 0;
            for (int k = 0; k < _w.Length; k++)
            {
                var (s, c) = Math.SinCos(_w[k] * t + _phi[k]);
                re += c; im += s;
            }
            var g = new Complex(re * _b, im * _b);
            switch (Spec.Kind)
            {
                case FadingKind.Rician:
                    return Complex.FromPolarCoordinates(_a, _losW * t + _losPhi) + g;
                case FadingKind.Nakagami:
                {
                    double r2 = g.Real * g.Real + g.Imaginary * g.Imaginary;
                    if (r2 == 0) return Complex.Zero;
                    double x = FadingMath.NakagamiQuantileX(Spec.M, r2);
                    return g * Math.Sqrt(x / Spec.M / r2);
                }
                default:
                    return g;
            }
        }

        /// <summary>The realization's spectral lines: each sinusoid's Doppler (Hz) and its share of the tap's power, and a
        /// Rician tap's line-of-sight line. A static tap is one line at 0 Hz.</summary>
        public IEnumerable<(double Hz, double Power)> Lines()
        {
            if (_w.Length == 0) { yield return (0, 1); yield break; }
            double diffuse = Spec.Kind == FadingKind.Rician ? 1 / (Spec.K + 1) : 1;
            foreach (double w in _w) yield return (w / (2 * Math.PI), diffuse / _w.Length);
            if (Spec.Kind == FadingKind.Rician) yield return (Spec.LosDopplerHz, Spec.K / (Spec.K + 1));
        }
    }

    /// <summary>What a channel's taps say of its time and frequency regime (note §2.4, §5.6).</summary>
    public static class SmallScale
    {
        /// <summary>
        /// The coherence time: the first lag at which the power-weighted correlation of the taps' gains, |Σ p_i R_i(τ)| / Σ p_i,
        /// falls to one half (0.2421/f_D for the classical spectrum). Infinite when no tap fades, f_D is 0, or it never does
        /// within 200/f_D (a strong line-of-sight tap).
        /// </summary>
        public static double CoherenceTime(IReadOnlyList<ChannelRay> rays, IReadOnlyList<FadingProcess> fades, double fdHz)
        {
            if (!(fdHz > 0) || !fades.Any(f => f.Spec.Kind != FadingKind.Static)) return double.PositiveInfinity;
            double total = 0;
            var w = new double[rays.Count];
            for (int i = 0; i < w.Length; i++) { w[i] = rays[i].Gain.Magnitude * rays[i].Gain.Magnitude; total += w[i]; }
            double Rho(double tau)
            {
                Complex acc = 0;
                for (int i = 0; i < w.Length; i++)
                    acc += w[i] * (i < fades.Count ? fades[i].Spec.Autocorrelation(tau) : Complex.One)
                                * Complex.FromPolarCoordinates(1, 2 * Math.PI * rays[i].DopplerHz * tau);
                return acc.Magnitude / total;
            }
            double step = 0.002 / fdHz, lo = 0;
            for (double tau = step; tau <= 200 / fdHz; tau += step)
            {
                if (Rho(tau) <= 0.5)
                {
                    double hi = tau;
                    for (int k = 0; k < 60; k++) { double mid = 0.5 * (lo + hi); if (Rho(mid) <= 0.5) hi = mid; else lo = mid; }
                    return 0.5 * (lo + hi);
                }
                lo = tau;
            }
            return double.PositiveInfinity;
        }

        /// <summary>
        /// The drop's Doppler spectrum: every tap's spectral lines (its fading's, shifted by its static Doppler), weighted by its
        /// power, summed into <paramref name="bins"/> bins across ±1.25 f_D (±1 Hz when f_D is 0), as power per bin (they sum to
        /// the taps' total power). Returns the bin centres (Hz) and the powers.
        /// </summary>
        public static (double[] Hz, double[] Power) DopplerSpectrum(IReadOnlyList<ChannelRay> rays, IReadOnlyList<FadingProcess> fades,
                                                                    double fdHz, int bins = 64)
        {
            var lines = new List<(double Hz, double P)>();
            for (int i = 0; i < rays.Count; i++)
            {
                double p = rays[i].Gain.Magnitude * rays[i].Gain.Magnitude;
                var f = i < fades.Count ? fades[i] : null;
                if (f is null) lines.Add((rays[i].DopplerHz, p));
                else foreach (var (hz, share) in f.Lines()) lines.Add((hz + rays[i].DopplerHz, p * share));
            }
            double reach = Math.Max(fdHz > 0 ? 1.25 * fdHz : 1, lines.Max(l => Math.Abs(l.Hz)) * 1.05);
            double width = 2 * reach / bins;
            var hzs = new double[bins];
            var pw = new double[bins];
            for (int b = 0; b < bins; b++) hzs[b] = -reach + (b + 0.5) * width;
            foreach (var (hz, p) in lines)
                pw[Math.Clamp((int)Math.Floor((hz + reach) / width), 0, bins - 1)] += p;
            return (hzs, pw);
        }
    }

    /// <summary>Small special functions the fading needs.</summary>
    public static class FadingMath
    {
        /// <summary>The Bessel function J0(x): (1/π)∫₀^π cos(x sin θ) dθ by the trapezoid rule on its period, exact to
        /// J_N(x), N above |x| by a margin.</summary>
        public static double J0(double x)
        {
            x = Math.Abs(x);
            int n = 64 + 2 * (int)Math.Ceiling(x);
            double s = 0;
            for (int k = 0; k < n; k++) s += Math.Cos(x * Math.Sin(2 * Math.PI * k / n));
            return s / n;
        }

        /// <summary>
        /// The x with P(m, x) = 1 − e^(−r²): the Nakagami-m quantile (as m·R², Ω = 1) of the probability a unit-power Rayleigh
        /// envelope of squared value <paramref name="r2"/> has. Newton on the regularized incomplete gamma function, reading the
        /// upper tail from Q so a large r² keeps its precision.
        /// </summary>
        public static double NakagamiQuantileX(double m, double r2)
        {
            if (Math.Abs(m - 1) < 1e-15) return r2;
            bool upper = r2 > 1;                         // P ≥ 0.63: solve Q(m, x) = e^(−r²)
            double target = upper ? Math.Exp(-r2) : r2 < 1e-5 ? r2 * (1 - r2 / 2 * (1 - r2 / 3)) : 1 - Math.Exp(-r2);
            double lg = GammaFunction.LogGamma(m);
            double x = Math.Max(1e-300, m * r2);
            for (int i = 0; i < 100; i++)
            {
                double p = GammaFunction.P(m, x);
                double f = upper ? (1 - p) - target : p - target;
                double d = Math.Exp((m - 1) * Math.Log(x) - x - lg);       // dP/dx
                if (!(d > 0)) break;
                double step = upper ? -f / -d : -f / d;
                double next = x + step;
                if (next <= 0) next = x / 2;
                if (Math.Abs(next - x) <= 1e-14 * x) { x = next; break; }
                x = next;
            }
            return x;
        }
    }
}
