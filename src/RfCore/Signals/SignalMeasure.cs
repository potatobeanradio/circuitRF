// ================================================================
//  SignalMeasure.cs  —  measurements on complex-envelope records
//
//  PSD:   Welch, Hann window, 50 % overlap, two-sided, density scale
//         (|x|^2 per Hz for a unit-impedance phasor scale; ratios and
//         dBc are what the ACLR uses).
//  ACLR:  square integration over the channel measurement bandwidth
//         (LTE: 12 x N_RB x 15 kHz = occupied; NR: N_RB x 12 x SCS =
//         BW_Config), adjacent channel at +-1 x channel BW, alternate
//         at +-2 x channel BW — the definitions the Waveform_Generator
//         tool uses, so the two read the same numbers.
//  Cross:  the same Welch on two records, X Y* per bin: (1/2) Re of the
//         cross-spectrum of a voltage and a current is the power density.
//  PAPR / CCDF: of |x|^2 over the whole record, or of a power record.
//  NMSE:  output vs input after best integer delay and complex gain.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public sealed record AclrResult(double ChannelPower, double Acpr1L, double Acpr1U, double Acpr2L, double Acpr2U,
                                    double MeasBwHz, double SpacingHz)
    {
        public double WorstAcpr1 => Math.Max(Acpr1L, Acpr1U);
        public double WorstAcpr2 => Math.Max(Acpr2L, Acpr2U);
    }

    public static class SignalMeasure
    {
        public static (double[] f, double[] psd) Welch(ReadOnlySpan<Complex> x, double fs, int nseg = 8192)
        {
            var (f, s) = CrossWelch(x, x, fs, nseg);
            var psd = new double[s.Length];
            for (int i = 0; i < s.Length; i++) psd[i] = s[i].Real;
            return (f, psd);
        }

        /// <summary>The cross-spectral density of <paramref name="x"/> and <paramref name="y"/> (X·Y* per hertz), with
        /// <see cref="Welch"/>'s segmenting, window and scale: on <c>y = x</c> its real part is Welch's density, bin for
        /// bin. <paramref name="y"/> is read for as many samples as <paramref name="x"/> has.</summary>
        public static (double[] f, Complex[] csd) CrossWelch(ReadOnlySpan<Complex> x, ReadOnlySpan<Complex> y, double fs, int nseg = 8192)
        {
            nseg = Math.Min(nseg, x.Length);
            if ((nseg & (nseg - 1)) != 0)
            {
                int p = 1;
                while (p * 2 <= nseg) p *= 2;
                nseg = p;
            }
            var w = new double[nseg];
            double wss = 0;
            for (int i = 0; i < nseg; i++) { w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / nseg)); wss += w[i] * w[i]; }
            bool same = x == y;
            var acc = new Complex[nseg];
            var buf = new Complex[nseg];
            var buy = same ? buf : new Complex[nseg];
            int step = nseg / 2, count = 0;
            for (int s = 0; s + nseg <= x.Length; s += step)
            {
                for (int i = 0; i < nseg; i++) buf[i] = x[s + i] * w[i];
                Fft.Transform(buf, inverse: false);
                if (!same)
                {
                    for (int i = 0; i < nseg; i++) buy[i] = y[s + i] * w[i];
                    Fft.Transform(buy, inverse: false);
                }
                for (int i = 0; i < nseg; i++) acc[i] += buf[i] * Complex.Conjugate(buy[i]);
                count++;
            }
            if (count == 0) count = 1;
            var f = new double[nseg];
            var csd = new Complex[nseg];
            for (int i = 0; i < nseg; i++)
            {
                int k = (i + nseg / 2) % nseg;                 // fftshift
                csd[i] = acc[k] / count / (fs * wss);
                f[i] = (k < nseg / 2 ? k : k - nseg) * fs / nseg;
            }
            return (f, csd);
        }

        public static double BandPower(double[] f, double[] psd, double f0, double bw)
        {
            double df = f.Length > 1 ? f[1] - f[0] : 1, p = 0;
            for (int i = 0; i < f.Length; i++)
                if (f[i] >= f0 - bw / 2 && f[i] <= f0 + bw / 2) p += psd[i];
            return p * df;
        }

        public static (double measBw, double spacing) AclrBands(CarrierSpec c)
        {
            if (c.Standard is CarrierStandard.Lte or CarrierStandard.Nr)
            {
                var nm = OfdmGenerator.Numerology(c);
                return (nm.OccupiedBwHz, c.BandwidthHz);
            }
            if (c.Standard == CarrierStandard.Wlan) return (Wlan.Numerology(Wlan.Resolve(c)).OccupiedBwHz, c.BandwidthHz);
            return (c.BandwidthHz * 0.9, c.BandwidthHz);
        }

        /// <summary>ACLR of the channel centred at <paramref name="f0"/> (offset from the envelope's carrier).</summary>
        public static AclrResult Aclr(double[] f, double[] psd, double fs, double f0, double measBw, double spacing)
        {
            double pc = BandPower(f, psd, f0, measBw);
            double Rel(double off) => Math.Abs(f0 + off) + measBw / 2 <= fs / 2
                ? 10 * Math.Log10(BandPower(f, psd, f0 + off, measBw) / pc)
                : double.NaN;
            return new AclrResult(pc, Rel(-spacing), Rel(spacing), Rel(-2 * spacing), Rel(2 * spacing), measBw, spacing);
        }

        public static AclrResult Aclr(ReadOnlySpan<Complex> x, double fs, CarrierSpec c, double f0 = double.NaN, int nseg = 8192)
        {
            var (f, p) = Welch(x, fs, nseg);
            var (mb, sp) = AclrBands(c);
            return Aclr(f, p, fs, double.IsNaN(f0) ? c.OffsetHz : f0, mb, sp);
        }

        /// <summary>PAPR (dB) at CCDF probability <paramref name="prob"/> (0 = absolute peak).</summary>
        public static double PaprDb(ReadOnlySpan<Complex> x, double prob = 1e-4) => PaprDb(Power(x), prob);

        /// <summary>PAPR (dB) of an instantaneous power record <paramref name="power"/>.</summary>
        public static double PaprDb(double[] power, double prob = 1e-4)
        {
            var p = (double[])power.Clone();
            double mean = 0;
            for (int i = 0; i < p.Length; i++) mean += p[i];
            mean /= p.Length;
            if (mean <= 0) return double.NaN;
            if (prob <= 0) return 10 * Math.Log10(p.Max() / mean);
            Array.Sort(p);
            int idx = Math.Clamp((int)Math.Floor((1 - prob) * (p.Length - 1)), 0, p.Length - 1);
            return 10 * Math.Log10(p[idx] / mean);
        }

        /// <summary>CCDF: for each probability, the power above the mean (dB) exceeded with that probability.</summary>
        public static (double[] dbAboveMean, double[] prob) Ccdf(ReadOnlySpan<Complex> x, int points = 200, double minProb = 1e-5)
            => Ccdf(Power(x), points, minProb);

        /// <summary>CCDF of an instantaneous power record <paramref name="power"/>.</summary>
        public static (double[] dbAboveMean, double[] prob) Ccdf(double[] power, int points = 200, double minProb = 1e-5)
        {
            var p = (double[])power.Clone();
            double mean = 0;
            for (int i = 0; i < p.Length; i++) mean += p[i];
            mean /= p.Length;
            Array.Sort(p);
            minProb = Math.Max(minProb, 1.0 / p.Length);
            var db = new double[points];
            var pr = new double[points];
            for (int i = 0; i < points; i++)
            {
                double q = Math.Pow(10, Math.Log10(minProb) * i / (points - 1));   // 1 .. minProb, log spaced
                pr[i] = q;
                int idx = Math.Clamp((int)Math.Floor((1 - q) * (p.Length - 1)), 0, p.Length - 1);
                db[i] = 10 * Math.Log10(Math.Max(p[idx], 1e-300) / mean);
            }
            return (db, pr);
        }

        private static double[] Power(ReadOnlySpan<Complex> x)
        {
            var p = new double[x.Length];
            for (int i = 0; i < x.Length; i++) p[i] = x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary;
            return p;
        }

        /// <summary>Occupied (99 %) bandwidth from a PSD.</summary>
        public static double Obw(double[] f, double[] psd, double fraction = 0.99)
        {
            double tot = psd.Sum(), lo = (1 - fraction) / 2 * tot, acc = 0;
            int a = 0, b = psd.Length - 1;
            for (int i = 0; i < psd.Length; i++) { acc += psd[i]; if (acc >= lo) { a = i; break; } }
            acc = 0;
            for (int i = psd.Length - 1; i >= 0; i--) { acc += psd[i]; if (acc >= lo) { b = i; break; } }
            return f[b] - f[a];
        }

        /// <summary>Best integer delay (0..maxLag) and complex gain of y vs x; NMSE in dB.</summary>
        public static (Complex gain, int lag, double nmseDb) Align(ReadOnlySpan<Complex> x, ReadOnlySpan<Complex> y, int maxLag = 32)
        {
            (Complex g, int lag, double e) best = (Complex.Zero, 0, double.MaxValue);
            for (int lag = 0; lag <= maxLag && lag < y.Length; lag++)
            {
                int n = Math.Min(x.Length, y.Length - lag);
                Complex num = 0; double den = 0;
                for (int i = 0; i < n; i++) { num += Complex.Conjugate(x[i]) * y[i + lag]; den += x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary; }
                if (den <= 0) continue;
                Complex g = num / den;
                double err = 0, sig = 0;
                for (int i = 0; i < n; i++)
                {
                    Complex r = g * x[i];
                    Complex d = y[i + lag] - r;
                    err += d.Real * d.Real + d.Imaginary * d.Imaginary;
                    sig += r.Real * r.Real + r.Imaginary * r.Imaginary;
                }
                double e = err / Math.Max(sig, 1e-300);
                if (e < best.e) best = (g, lag, e);
            }
            return (best.g, best.lag, 10 * Math.Log10(best.e));
        }

        public static double MeanPowerDbm(ReadOnlySpan<Complex> vPeak, double r = 50.0)
        {
            double p = 0;
            foreach (var v in vPeak) p += v.Real * v.Real + v.Imaginary * v.Imaginary;
            p /= Math.Max(1, vPeak.Length);
            return 10 * Math.Log10(p / (2 * r) * 1e3);
        }
    }
}
