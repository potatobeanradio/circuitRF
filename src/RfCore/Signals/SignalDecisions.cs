// ================================================================
//  SignalDecisions.cs  —  decided symbols, counted errors, and the semi-analytic BER (brief PCH-4)
//
//  After equalization each symbol is decided, and the decision compared
//  with what was sent:
//
//    QAM (LTE / NR)  nearest point, per axis: the level index of the
//                    received I and Q against the generator's own. The
//                    generator draws each axis's level index from its seed
//                    (OfdmGenerator.DrawRow) and maps no bits; the bits of
//                    an index are taken as its Gray code, i ^ (i >> 1), the
//                    per-axis Gray mapping square QAM is specified with, so
//                    a nearest-neighbour error costs one bit.
//    DBPSK           the sign of the in-phase sample at each bit centre;
//                    the bit is differential (the same sign as the previous
//                    bit's), so a bit is wrong when exactly one of its two
//                    symbols is.
//    FSK             the sign of the frequency at each bit centre (its
//                    offset taken out) against the sign sent.
//    CSS             each chirp de-chirped against the base up-chirp and
//                    decimated to the chip rate, its value the FFT bin of
//                    the peak, received against sent; SF bits a chirp.
//
//  Counted:  SER = symbol errors / symbols, BER = bit errors / bits, with
//  the exact (Clopper–Pearson) 95 % interval of the BER.
//
//  Semi-analytic: each element's post-equalizer SINR γ, then the
//  modulation's conditional BER at γ, averaged over the elements —
//      QPSK and square M-QAM, Gray   (4/k)(1 − 1/√M)·Q(√(3γ/(M − 1)))
//                                    (QPSK: Q(√γ), exact)
//      DBPSK, coherent, differential 2p(1 − p),  p = Q(√(2γ))
//  noise and distortion taken as Gaussian, which the result says.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Signals
{
    /// <summary>Counted decision errors: symbols, bits, and the BER's exact 95 % interval.</summary>
    public sealed record ErrorCount(long SymbolErrors, long Symbols, long BitErrors, long Bits)
    {
        public double Ser => Symbols > 0 ? (double)SymbolErrors / Symbols : double.NaN;
        public double Ber => Bits > 0 ? (double)BitErrors / Bits : double.NaN;
        public (double Low, double High) BerInterval => BetaFunction.BinomialInterval(BitErrors, Bits);
    }

    public static class SignalDecisions
    {
        // ── QAM ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The levels per axis and the scale of a square QAM of unit mean power.</summary>
        public static (int Levels, double Norm) Grid(QamOrder m)
        {
            int levels = 1 << ((int)m / 2);
            return (levels, 1.0 / Math.Sqrt(2.0 * (levels * levels - 1) / 3.0));
        }

        private static int Level(double v, int levels, double norm)
            => Math.Clamp((int)Math.Round((v / norm + levels - 1) / 2), 0, levels - 1);

        private static int Gray(int i) => i ^ (i >> 1);

        /// <summary>Nearest-point decisions of <paramref name="points"/> (on the ideal points' scale) against
        /// <paramref name="ideal"/>, each axis's level index Gray-coded.</summary>
        public static ErrorCount Qam(QamOrder m, IReadOnlyList<Complex> points, IReadOnlyList<Complex> ideal)
        {
            var (levels, norm) = Grid(m);
            int bitsPerAxis = (int)m / 2;
            long se = 0, be = 0;
            for (int k = 0; k < points.Count; k++)
            {
                int ri = Level(points[k].Real, levels, norm), qi = Level(points[k].Imaginary, levels, norm);
                int rs = Level(ideal[k].Real, levels, norm), qs = Level(ideal[k].Imaginary, levels, norm);
                if (ri != rs || qi != qs) se++;
                be += System.Numerics.BitOperations.PopCount((uint)(Gray(ri) ^ Gray(rs)))
                    + System.Numerics.BitOperations.PopCount((uint)(Gray(qi) ^ Gray(qs)));
            }
            return new ErrorCount(se, points.Count, be, (long)points.Count * 2 * bitsPerAxis);
        }

        // ── DBPSK, FSK, CSS ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>DBPSK: the sign of each bit centre's in-phase sample against the sign sent; a bit is the agreement of
        /// two successive symbols, counted where <paramref name="follows"/>[k] says symbol k follows symbol k − 1.</summary>
        public static ErrorCount Dbpsk(IReadOnlyList<Complex> points, IReadOnlyList<Complex> ideal, IReadOnlyList<bool> follows)
        {
            long se = 0, be = 0, bits = 0;
            for (int k = 0; k < points.Count; k++)
            {
                bool d = points[k].Real >= 0, s = ideal[k].Real >= 0;
                if (d != s) se++;
                if (k == 0 || !follows[k]) continue;
                bool db = d == (points[k - 1].Real >= 0), sb = s == (ideal[k - 1].Real >= 0);
                if (db != sb) be++;
                bits++;
            }
            return new ErrorCount(se, points.Count, be, bits);
        }

        /// <summary>FSK: the sign of the received frequency at each bit centre against the sign sent; one bit a symbol.</summary>
        public static ErrorCount Fsk(IReadOnlyList<double> received, IReadOnlyList<double> sent)
        {
            long e = 0;
            for (int k = 0; k < received.Count; k++) if (received[k] >= 0 != sent[k] >= 0) e++;
            return new ErrorCount(e, received.Count, e, received.Count);
        }

        /// <summary>CSS: each chirp of <paramref name="w"/> de-chirped and decided by its peak bin, received against sent.</summary>
        public static ErrorCount Css(ReceivedWaveform w)
        {
            var shape = RfCore.Signals.Css.Resolve(w.Carrier.Spec);
            int chips = shape.Chips, ns = w.Span, m = w.X.Length;
            int step = ns / chips;
            // the base up-chirp's conjugate, at the chip rate (every step-th native sample)
            var conj = new Complex[chips];
            double phase = 0, rate = w.NativeHz, bw = shape.BandwidthHz;
            for (int i = 0, c = 0; i < ns; i++)
            {
                if (i % step == 0) conj[c++] = Complex.FromPolarCoordinates(1.0, -phase);
                phase += 2 * Math.PI * (-bw / 2 + (double)i / ns * bw) / rate;
            }
            int Value(Complex[] x, int i0)
            {
                var buf = new Complex[chips];
                for (int c = 0; c < chips; c++) buf[c] = x[((i0 + c * step) % m + m) % m] * conj[c];
                Fft.Transform(buf, inverse: false);
                int best = 0;
                for (int c = 1; c < chips; c++) if (buf[c].Magnitude > buf[best].Magnitude) best = c;
                return best;
            }
            long se = 0, be = 0;
            foreach (int i0 in w.At)
            {
                int d = Value(w.X, i0), s = Value(w.S, i0);
                if (d != s) se++;
                be += System.Numerics.BitOperations.PopCount((uint)(d ^ s));
            }
            return new ErrorCount(se, w.At.Length, be, (long)w.At.Length * shape.Sf);
        }

        // ── the semi-analytic BER ───────────────────────────────────────────────────────────────────────────

        private static double Q(double x) => 0.5 * Normal.Erfc(x / Math.Sqrt(2));

        /// <summary>A square QAM's BER at a symbol SNR γ = Es/N0 (linear), Gray-coded.</summary>
        public static double QamBer(QamOrder m, double gamma)
        {
            int k = (int)m;
            double mm = Math.Pow(2, k);
            return k == 2 ? Q(Math.Sqrt(gamma)) : 4.0 / k * (1 - 1 / Math.Sqrt(mm)) * Q(Math.Sqrt(3 * gamma / (mm - 1)));
        }

        /// <summary>DBPSK decided coherently and decoded differentially, at γ = Es/N0 (linear).</summary>
        public static double DbpskBer(double gamma)
        {
            double p = Q(Math.Sqrt(2 * gamma));
            return 2 * p * (1 - p);
        }

        /// <summary>The mean of <paramref name="ber"/> over each element's SINR (linear), NaN ones left out.</summary>
        public static double SemiAnalytic(IEnumerable<double> sinr, Func<double, double> ber)
        {
            double sum = 0; long n = 0;
            foreach (var g in sinr)
            {
                if (!double.IsFinite(g)) continue;
                sum += ber(g);
                n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        /// <summary>The excess kurtosis of an error's I and Q components together (0 for a Gaussian); NaN with too few.</summary>
        public static double ExcessKurtosis(IReadOnlyList<Complex> error)
        {
            if (error.Count < 8) return double.NaN;
            var v = error.SelectMany(e => new[] { e.Real, e.Imaginary }).ToArray();
            double mean = v.Average(), m2 = 0, m4 = 0;
            foreach (var x in v) { double d = x - mean, d2 = d * d; m2 += d2; m4 += d2 * d2; }
            m2 /= v.Length; m4 /= v.Length;
            return m2 > 0 ? m4 / (m2 * m2) - 3 : double.NaN;
        }

        /// <summary>The kurtosis test the notes state: a distortion is far from Gaussian when its excess kurtosis is at
        /// least 0.5 and five of its standard errors (√(24/n), n the components counted).</summary>
        public static bool FarFromGaussian(double excess, int components)
            => double.IsFinite(excess) && Math.Abs(excess) >= Math.Max(0.5, 5 * Math.Sqrt(24.0 / Math.Max(1, components)));
    }
}
