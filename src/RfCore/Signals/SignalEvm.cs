// ================================================================
//  SignalEvm.cs  —  EVM of a generated LTE / NR carrier
//
//  Demodulates a carrier's own native record (the one the source
//  plays: after clipping and spectral shaping) symbol by symbol — FFT
//  over the useful part right after each cyclic prefix, where the
//  WOLA ramps do not reach — and compares every resource element with
//  the QAM point the generator put there (data-aided: the reference is
//  regenerated from the carrier's seed, not decided).  One complex
//  gain is fitted over the whole record (no per-subcarrier equaliser),
//  so EVM reads the in-band distortion the clipping added and nothing
//  else; an unclipped record reads ~0 %.
//
//  EVM rms = sqrt(sum |r/g - s|^2 / sum |s|^2), peak = max |r/g - s|
//  over the rms of the ideal points.  Limits are the base-station EVM
//  requirements per modulation (QPSK 17.5 %, 16QAM 12.5 %, 64QAM 8 %,
//  256QAM 3.5 %, 1024QAM 2.5 %).
//
//  A RECEIVED record (brief NZ-8: a probe's envelope after a circuit)
//  is demodulated the same way, after three steps the native record
//  does not need:
//    1. back to the carrier's own rate: the window at the envelope rate
//       is moved to baseband, transformed, cut to the carrier's band and
//       transformed back at the native rate.  A window of whole records
//       is periodic, so a brick wall at the native Nyquist is exact (it
//       is Fft.ResamplePeriodic); any other window is not, and a cosine
//       roll-off from the occupied band's edge to the native Nyquist
//       keeps the wrap's ringing to a few samples at each end, where a
//       guard leaves those symbols out.
//    2. the symbols the window holds whole, at their place in the record
//       (the record is periodic, so a run longer than one record reads
//       its symbols again).
//    3. the delay, fitted from the record the source played: the
//       per-subcarrier response H = sum_s Y conj(D) of received against
//       sent is a line in phase against frequency, and its slope is the
//       delay (the peak of H's transform, refined to sub-sample).  Then
//       the one gain, against the ideal points, as above.  No per-
//       subcarrier equaliser: a filter's in-band ripple is EVM, as it is
//       NMSE.  On a matched pad the delay is zero and the result is the
//       native record's own EVM.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public sealed record EvmResult(double RmsPercent, double PeakPercent, double LimitPercent,
                                   Complex[] Received, Complex[] Ideal, int Symbols, int Subcarriers)
    {
        public double RmsDb => 20 * Math.Log10(Math.Max(RmsPercent, 1e-12) / 100);
        public bool Passes => RmsPercent <= LimitPercent;
        /// <summary>The complex gain fitted against the ideal points (received / ideal).</summary>
        public Complex Gain { get; init; } = Complex.One;
        /// <summary>The delay taken out before the gain (s): zero for a native record.</summary>
        public double DelayS { get; init; }
    }

    public static class SignalEvm
    {
        public static double LimitPercent(QamOrder m) => m switch
        {
            QamOrder.Qpsk => 17.5,
            QamOrder.Qam16 => 12.5,
            QamOrder.Qam64 => 8.0,
            QamOrder.Qam256 => 3.5,
            _ => 2.5,
        };

        /// <summary>EVM of an LTE / NR carrier's <paramref name="native"/> record (any scale) against its ideal symbols.</summary>
        public static EvmResult Measure(CarrierSpec c, ReadOnlySpan<Complex> native, double recordSeconds)
        {
            var nm = OfdmGenerator.Numerology(c);
            int halfMs = (int)Math.Round(recordSeconds / 0.5e-3);
            if (native.Length != nm.SamplesPerHalfMs * halfMs)
                throw new ArgumentException($"{c.Label}: the record holds {native.Length} samples, not the {nm.SamplesPerHalfMs * halfMs} its numerology gives");
            var ideal = OfdmGenerator.IdealSymbols(c, halfMs);
            int n = nm.Nfft, nsc = nm.NSc, total = native.Length;
            var rx = new Complex[ideal.Length * nsc];
            var id = new Complex[rx.Length];
            var buf = new Complex[n];
            int pos = 0;
            for (int s = 0; s < ideal.Length; s++)
            {
                int cp = s % nm.SymbolsPerHalfMs == 0 ? nm.CpLong : nm.CpNormal;
                for (int i = 0; i < n; i++) buf[i] = native[(pos + cp + i) % total];
                Fft.Transform(buf, inverse: false);
                for (int i = 0; i < nsc; i++)
                {
                    rx[s * nsc + i] = buf[OfdmGenerator.BinOf(nm, i)];
                    id[s * nsc + i] = ideal[s][i];
                }
                pos += cp + n;
            }
            return Score(c, rx, id, ideal.Length, nsc);
        }

        /// <summary>The one complex gain against the ideal points, then rms and peak EVM — <see cref="Measure"/>'s, and a
        /// received record's after its delay is taken out. <paramref name="rx"/> is divided by the gain in place.</summary>
        internal static EvmResult Score(CarrierSpec c, Complex[] rx, Complex[] id, int symbols, int nsc)
        {
            Complex num = Complex.Zero; double den = 0;
            for (int k = 0; k < rx.Length; k++) { num += rx[k] * Complex.Conjugate(id[k]); den += id[k].Real * id[k].Real + id[k].Imaginary * id[k].Imaginary; }
            var g = num / den;
            double err = 0, peak = 0;
            for (int k = 0; k < rx.Length; k++)
            {
                rx[k] /= g;
                double e = (rx[k] - id[k]).Magnitude;
                err += e * e;
                peak = Math.Max(peak, e);
            }
            double rmsRef = Math.Sqrt(den / rx.Length);
            return new EvmResult(100 * Math.Sqrt(err / den), 100 * peak / rmsRef, LimitPercent(c.Modulation),
                                 rx, id, symbols, nsc) { Gain = g };
        }
        // ── a received record (brief NZ-8) ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Demodulates <paramref name="y"/> — a received envelope at <paramref name="fsHz"/>, its sample 0 at record sample
        /// <paramref name="startSample"/> (at <paramref name="fsHz"/>), the carrier at <paramref name="offsetHz"/> from its
        /// centre — on <paramref name="carrier"/>'s grid: every OFDM symbol the window holds whole, beside the same symbols
        /// of the record the source played and the ideal points. Null, with <paramref name="why"/>, when the carrier is not
        /// LTE or NR, the rate is not a ratio of whole numbers to the carrier's own, or the window holds no whole symbol.
        /// </summary>
        public static ReceivedSymbols? Demodulate(CarrierRecord carrier, double recordSeconds, ReadOnlySpan<Complex> y, double fsHz,
                                                  long startSample, double offsetHz, out string? why)
        {
            var c = carrier.Spec;
            why = null;
            if (c.Standard is not (CarrierStandard.Lte or CarrierStandard.Nr))
            {
                why = c.Standard switch
                {
                    CarrierStandard.Cw => "a CW carrier has no symbols",
                    CarrierStandard.Pcw => "a pulse has no symbols",
                    CarrierStandard.File => "an imported I/Q file carries no symbol data",
                    _ => $"{c.Standard} carries no QAM symbols",
                };
                return null;
            }
            var nm = OfdmGenerator.Numerology(c);
            double native = nm.SampleRateHz;
            int n = nm.Nfft, nsc = nm.NSc;
            int halfMs = (int)Math.Round(recordSeconds / 0.5e-3);
            int nTot = nm.SamplesPerHalfMs * halfMs;
            if (carrier.Native.Length != nTot)
            {
                why = $"its record holds {carrier.Native.Length} samples, not the {nTot} its numerology gives";
                return null;
            }

            // 1. back to the carrier's own rate, on a window that starts on a native sample. Whole records are periodic and
            // resampled exactly; any other window rolls off from the channel edge (PassEdge).
            if (ToNative(y, fsHz, startSample, offsetHz, native, recordSeconds, n, PassEdge(c, nm), native / 2, filterWhole: false,
                         out var x, out long n0, out bool whole, out why) is false)
            {
                why ??= $"less than one {1e6 / nm.ScsHz:0.#} us symbol; lengthen Stop";
                return null;
            }
            long mNative = x.Length;
            int w0 = (int)(((n0 % nTot) + nTot) % nTot);

            // 2. the symbols held whole, at their place in the record
            var useful = new int[nm.SymbolsPerHalfMs * halfMs];
            for (int s = 0, at = 0; s < useful.Length; s++)
            {
                int cp = s % nm.SymbolsPerHalfMs == 0 ? nm.CpLong : nm.CpNormal;
                useful[s] = at + cp;
                at += cp + n;
            }
            var picks = new List<(int s, long start)>();          // record symbol, window index of its useful part
            if (whole)
            {
                long copies = mNative / nTot;
                for (long r = 0; r < copies; r++)
                    for (int s = 0; s < useful.Length; s++)
                        picks.Add((s, (((useful[s] - w0 + r * nTot) % mNative) + mNative) % mNative));
            }
            else
            {
                double fp = PassEdge(c, nm), band = native / 2 - fp;
                int guard = band > 0 ? 4 * (int)Math.Ceiling(native / (2 * band)) : n;
                for (long r = (long)Math.Floor((double)n0 / nTot); r * nTot < n0 + mNative; r++)
                    for (int s = 0; s < useful.Length; s++)
                    {
                        long i0 = r * nTot + useful[s] - n0;
                        if (i0 >= guard && i0 + n <= mNative - guard) picks.Add((s, i0));
                    }
            }
            if (picks.Count == 0)
            {
                why = $"the run holds no whole {1e6 / nm.ScsHz:0.#} us symbol clear of its ends; lengthen Stop";
                return null;
            }

            var ideal = OfdmGenerator.IdealSymbols(c, halfMs);
            var rx = new Complex[picks.Count * nsc];
            var sent = new Complex[rx.Length];
            var id = new Complex[rx.Length];
            var buf = new Complex[n];
            var bins = new int[nsc];
            var hz = new double[nsc];
            for (int i = 0; i < nsc; i++)
            {
                bins[i] = OfdmGenerator.BinOf(nm, i);
                hz[i] = (bins[i] < n / 2 ? bins[i] : bins[i] - n) * nm.ScsHz;
            }
            for (int k = 0; k < picks.Count; k++)
            {
                var (s, start) = picks[k];
                for (int i = 0; i < n; i++) buf[i] = x[(int)((start + i) % mNative)];
                Fft.Transform(buf, inverse: false);
                for (int i = 0; i < nsc; i++) rx[k * nsc + i] = buf[bins[i]];
                for (int i = 0; i < n; i++) buf[i] = carrier.Native[(useful[s] + i) % nTot];
                Fft.Transform(buf, inverse: false);
                for (int i = 0; i < nsc; i++) { sent[k * nsc + i] = buf[bins[i]]; id[k * nsc + i] = ideal[s][i]; }
            }
            return new ReceivedSymbols(c, rx, sent, id, hz, picks.Count, whole);
        }

        /// <summary>
        /// The EVM of <paramref name="d"/>: its delay fitted against the record the source played and taken out, then the
        /// one gain against the ideal points, as <see cref="Measure"/> fits it.
        /// </summary>
        public static EvmResult Evaluate(ReceivedSymbols d)
        {
            int nsc = d.SubcarrierHz.Length;
            var h = new Complex[nsc];
            for (int k = 0; k < d.Received.Length; k++) h[k % nsc] += d.Received[k] * Complex.Conjugate(d.Sent[k]);
            double tau = Delay(h, d.SubcarrierHz);
            var rx = (Complex[])d.Received.Clone();
            if (tau != 0)
                for (int k = 0; k < rx.Length; k++) rx[k] *= Complex.FromPolarCoordinates(1.0, 2 * Math.PI * d.SubcarrierHz[k % nsc] * tau);
            return Score(d.Carrier, rx, (Complex[])d.Ideal.Clone(), d.Symbols, nsc) with { DelayS = tau };
        }

        /// <summary>In-channel signal over in-channel noise: <paramref name="quiet"/>'s power over the power of what
        /// <paramref name="noisy"/> adds to it, on the same resource elements. NaN when they do not match.</summary>
        public static double SignalToNoise(ReceivedSymbols noisy, ReceivedSymbols quiet)
        {
            if (noisy.Received.Length != quiet.Received.Length) return double.NaN;
            double sig = 0, noise = 0;
            for (int k = 0; k < quiet.Received.Length; k++)
            {
                var a = quiet.Received[k]; var e = noisy.Received[k] - a;
                sig += a.Real * a.Real + a.Imaginary * a.Imaginary;
                noise += e.Real * e.Real + e.Imaginary * e.Imaginary;
            }
            return sig / noise;
        }

        /// <summary>The delay (s) whose phase line best fits <paramref name="h"/> across <paramref name="hz"/>: the peak of
        /// |sum h e^(j 2 pi f tau)|, found on an 8x zero-padded transform and refined by golden section to a fraction of
        /// that grid. Zero when h is a constant (no delay), exactly.</summary>
        internal static double Delay(Complex[] h, double[] hz)
        {
            int nsc = h.Length;
            double scs = Math.Abs(hz[1] - hz[0]);
            for (int i = 2; i < nsc && scs <= 0; i++) scs = Math.Abs(hz[i] - hz[i - 1]);
            if (!(scs > 0)) return 0;
            int span = 1;
            double hi = hz.Max(), lo = hz.Min();
            while (span < (hi - lo) / scs + 1) span <<= 1;
            int pp = 8 * span;
            var g = new Complex[pp];
            for (int i = 0; i < nsc; i++)
            {
                long b = (long)Math.Round(hz[i] / scs);
                g[(int)(((b % pp) + pp) % pp)] += h[i];
            }
            Fft.Transform(g, inverse: true);                         // g[m] = sum h e^(+j 2 pi f m / (pp scs))
            int best = 0;
            for (int i = 1; i < pp; i++) if (g[i].Magnitude > g[best].Magnitude) best = i;
            if (best == 0 && IsFlatPhase(h)) return 0;
            double step = 1.0 / (pp * scs);
            double centre = (best <= pp / 2 ? best : best - pp) * step;
            double F(double t)
            {
                Complex acc = 0;
                for (int i = 0; i < nsc; i++) acc += h[i] * Complex.FromPolarCoordinates(1.0, 2 * Math.PI * hz[i] * t);
                return acc.Magnitude;
            }
            double a = centre - step, b2 = centre + step, r = (Math.Sqrt(5) - 1) / 2;
            double c1 = b2 - r * (b2 - a), c2 = a + r * (b2 - a), f1 = F(c1), f2 = F(c2);
            for (int it = 0; it < 80; it++)
            {
                if (f1 > f2) { b2 = c2; c2 = c1; f2 = f1; c1 = b2 - r * (b2 - a); f1 = F(c1); }
                else { a = c1; c1 = c2; f1 = f2; c2 = a + r * (b2 - a); f2 = F(c2); }
            }
            return 0.5 * (a + b2);
        }

        /// <summary>Every element of <paramref name="h"/> at one phase (to round-off of the largest): no delay to fit.</summary>
        private static bool IsFlatPhase(Complex[] h)
        {
            Complex sum = 0;
            foreach (var v in h) sum += v;
            if (sum == Complex.Zero) return false;
            var u = Complex.Conjugate(sum) / sum.Magnitude;
            // against the largest element: a bin near a null of the spectrum carries round-off at any phase
            double top = h.Max(v => v.Magnitude);
            foreach (var v in h) if (Math.Abs((v * u).Imaginary) > 1e-12 * top) return false;
            return true;
        }

        /// <summary>
        /// <paramref name="y"/> (sample 0 at record sample <paramref name="startSample"/>, at <paramref name="fsHz"/>) moved
        /// down by <paramref name="offsetHz"/> and brought to <paramref name="nativeHz"/>: <paramref name="x"/>, its sample 0 at
        /// native record sample <paramref name="n0"/>. The window starts on a native sample and is a whole number of records
        /// when it can be (<paramref name="whole"/>). A whole window is cut at the native Nyquist exactly
        /// (Fft.ResamplePeriodic) unless <paramref name="filterWhole"/>; any other window, and a whole one with it, rolls off as
        /// cos² from <paramref name="passHz"/> to <paramref name="stopHz"/>. False, with <paramref name="why"/>, when the rate
        /// is not a ratio of whole numbers to the native one or the window holds fewer than <paramref name="minSamples"/>.
        /// </summary>
        internal static bool ToNative(ReadOnlySpan<Complex> y, double fsHz, long startSample, double offsetHz, double nativeHz,
                                      double recordSeconds, int minSamples, double passHz, double stopHz, bool filterWhole,
                                      out Complex[] x, out long n0, out bool whole, out string? why)
        {
            x = []; n0 = 0; whole = false; why = null;
            if (!Ratio(fsHz / nativeHz, out long p, out long q))
            {
                why = $"the envelope rate {fsHz / 1e6:0.####} MHz is not a ratio of small whole numbers to the carrier's {nativeHz / 1e6:0.####} MHz";
                return false;
            }
            long a0 = ((-startSample) % p + p) % p;
            long avail = y.Length - a0;
            long recFs = (long)Math.Round(recordSeconds * fsHz);
            whole = recFs > 0 && avail >= recFs;
            long m = whole ? avail / recFs * recFs : avail / p * p;
            long mNative = m / p * q;
            if (mNative < minSamples)
            {
                why = $"the run holds {avail / fsHz * 1e6:0.#} us of it, {(avail <= 0 ? "nothing" : "too little")} to measure; lengthen Stop";
                return false;
            }
            var z = new Complex[m];
            double w = -2 * Math.PI * offsetHz / fsHz;
            for (long i = 0; i < m; i++) z[i] = y[(int)(a0 + i)] * Complex.FromPolarCoordinates(1.0, w * i);
            if (whole && !filterWhole) x = Fft.ResamplePeriodic(z, (int)mNative);
            else
            {
                var spec = Fft.Forward(z);
                var cut = new Complex[mNative];
                int keep = (int)mNative, pos = (keep + 1) / 2, neg = keep / 2;
                for (int k = 0; k < pos; k++) cut[k] = spec[k] * RollOff(k * fsHz / m, passHz, stopHz);
                for (int k = 1; k <= neg; k++) cut[keep - k] = spec[m - k] * RollOff(-k * fsHz / m, passHz, stopHz);
                x = Fft.Inverse(cut);
                double scale = (double)mNative / m;
                for (int i = 0; i < x.Length; i++) x[i] *= scale;
            }
            n0 = (startSample + a0) / p * q;
            return true;
        }

        /// <summary>1 to <paramref name="passHz"/>, cos² down to 0 at <paramref name="stopHz"/>, 0 beyond.</summary>
        internal static double RollOff(double fHz, double passHz, double stopHz)
        {
            double f = Math.Abs(fHz);
            if (f <= passHz) return 1;
            if (f >= stopHz) return 0;
            double u = Math.Cos(0.5 * Math.PI * (f - passHz) / (stopHz - passHz));
            return u * u;
        }

        /// <summary>Where the roll-off of a window that is not whole records starts: the channel's edge, where the record's
        /// own content ends, or just past the occupied subcarriers when the channel edge is at the native Nyquist.</summary>
        private static double PassEdge(CarrierSpec c, CarrierNumerology nm)
        {
            double edge = c.BandwidthHz / 2, fn = nm.SampleRateHz / 2, occ = nm.OccupiedBwHz / 2 + nm.ScsHz;
            return edge > occ && edge < 0.95 * fn ? edge : occ;
        }

        /// <summary>p/q = <paramref name="r"/> in whole numbers, q at most 4096.</summary>
        private static bool Ratio(double r, out long p, out long q)
        {
            for (q = 1; q <= 4096; q++)
            {
                double pq = r * q;
                p = (long)Math.Round(pq);
                if (p > 0 && Math.Abs(pq - p) < 1e-9 * Math.Max(1, pq)) return true;
            }
            p = 0;
            return false;
        }
    }

    /// <summary>A received record demodulated on one LTE / NR carrier's grid (<see cref="SignalEvm.Demodulate"/>): for every
    /// whole symbol the window held, row-major [symbol, subcarrier], the received point, the point the source's own record
    /// carries there, and the ideal QAM point.</summary>
    public sealed record ReceivedSymbols(CarrierSpec Carrier, Complex[] Received, Complex[] Sent, Complex[] Ideal,
                                         double[] SubcarrierHz, int Symbols, bool WholeRecords);
}
