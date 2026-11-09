// ================================================================
//  SignalQuality.cs  —  the received quality of a DBPSK, FSK or CSS carrier (brief NZ-8)
//
//  SignalEvm demodulates OFDM against its QAM points. The other carriers
//  circuitRF generates carry known data too, but a QAM EVM fits only one
//  of them, so each is measured the way its receiver reads it:
//
//    DBPSK  EVM at the bit centres, where every symbol of the record
//           peaks at +-1 and its neighbours are zero: the same EVM,
//           MER and constellation as OFDM's.
//    FSK    The instantaneous frequency at each bit centre against the
//           sent one, fitted as f_rx = a f_sent + b: the deviation is
//           a Dev, the carrier offset b, and the FSK error the RMS of
//           what is left over the nominal deviation, in %. The envelope
//           is constant, so an I/Q constellation would be a circle.
//    CSS    De-chirped: each chirp of the received record times the
//           conjugate of the chirp sent, one complex gain per chirp (a
//           receiver reads each symbol's FFT bin on its own). The error
//           is the energy left over the energy at the symbol's bin.
//
//  Common to the three. The received and the sent record pass through
//  ONE measurement filter, a cos² roll-off from the channel edge, so
//  the measurement bandwidth is the channel's and the reference carries
//  the same filtering: a matched pad reads zero. The window is brought
//  to the carrier's own rate (SignalEvm.ToNative), the delay fitted from
//  the received-against-sent response H = X conj(S) over the passband
//  (exactly zero for a pad) and taken out in the frequency domain. A
//  window that is not whole records leaves out the instants within the
//  filter's ringing of either end.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>How a carrier's received quality is measured (<see cref="SignalQuality"/>).</summary>
    public enum QualityKind { Dbpsk, Fsk, Css }

    /// <summary>A received DBPSK, FSK or CSS record on its carrier's grid: <see cref="X"/> the received window, filtered and
    /// aligned, <see cref="S"/> the sent record through the same filter at the same samples, <see cref="At"/> the window
    /// indices of the instants measured (bit centres, or chirp starts), <see cref="Span"/> the samples one instant reads,
    /// and the interior [<see cref="From"/>, <see cref="To"/>) clear of the window's ends.</summary>
    public sealed record ReceivedWaveform(CarrierRecord Carrier, QualityKind Kind, Complex[] X, Complex[] S, int[] At, int Span,
                                          int From, int To, double NativeHz, double DelayS, bool WholeRecords);

    /// <summary>A carrier's received quality. <see cref="ErrorPercent"/> is the EVM (DBPSK), the FSK error or the de-chirped
    /// error (CSS); <see cref="Points"/> / <see cref="Reference"/> the DBPSK constellation, <see cref="FreqHz"/> /
    /// <see cref="FreqRefHz"/> FSK's frequency at each bit centre (the offset taken out) and the frequency sent there.</summary>
    public sealed record WaveformQuality(QualityKind Kind, double ErrorPercent, double PeakPercent, double DeviationHz,
                                         double OffsetHz, Complex[]? Points, Complex[]? Reference, double[]? FreqHz,
                                         double[]? FreqRefHz, double DelayS);

    public static class SignalQuality
    {
        /// <summary>The kind a carrier is measured as; null for LTE / NR (SignalEvm) and for a carrier with no symbols.</summary>
        public static QualityKind? KindOf(CarrierStandard s) => s switch
        {
            CarrierStandard.Dbpsk => QualityKind.Dbpsk,
            CarrierStandard.Fsk => QualityKind.Fsk,
            CarrierStandard.Css => QualityKind.Css,
            _ => null,
        };

        /// <summary>
        /// Brings <paramref name="y"/> (sample 0 at record sample <paramref name="startSample"/> at <paramref name="fsHz"/>,
        /// the carrier at <paramref name="offsetHz"/>) to <paramref name="carrier"/>'s grid, filtered and aligned beside the
        /// sent record. Null, with <paramref name="why"/>, for any other carrier, a rate that is not a ratio of whole numbers
        /// to the carrier's, or a window holding no whole instant.
        /// </summary>
        public static ReceivedWaveform? Receive(CarrierRecord carrier, double recordSeconds, ReadOnlySpan<Complex> y, double fsHz,
                                                long startSample, double offsetHz, out string? why)
        {
            why = null;
            var c = carrier.Spec;
            if (KindOf(c.Standard) is not { } kind)
            {
                why = c.Standard switch
                {
                    CarrierStandard.Cw => "a CW carrier has no symbols",
                    CarrierStandard.Pcw => "a pulse has no symbols",
                    CarrierStandard.File => "an imported I/Q file carries no symbol data",
                    _ => $"{c.Standard} is measured by its own EVM",
                };
                return null;
            }
            double native = carrier.NativeRateHz;
            int nTot = carrier.Native.Length;
            // the instants of one record (window-independent) and the samples each reads; the channel's edge
            int[] instants; int span; double pass;
            switch (kind)
            {
                case QualityKind.Dbpsk:
                {
                    var d = Dbpsk.Resolve(c);
                    const int sps = Dbpsk.SamplesPerBit;
                    instants = [.. Enumerable.Range(0, nTot / sps).Select(k => k * sps + sps / 2)];
                    span = 1;
                    pass = d.BitRate;
                    break;
                }
                case QualityKind.Fsk:
                {
                    var f = Fsk.Resolve(c);
                    int sps = (int)Math.Round(native / f.BitRate);
                    // the frequency at a bit centre is read from the samples either side of it
                    instants = [.. Enumerable.Range(0, nTot / sps).Select(k => k * sps + sps / 2 - 1)];
                    span = 3;
                    pass = f.Deviation + f.BitRate;
                    break;
                }
                default:
                {
                    var s = Css.Resolve(c);
                    int ns = s.Chips * Css.SamplesPerChip;
                    var starts = new List<int>();
                    for (int k = 0; k < Css.PreambleSymbols + Css.SyncSymbols.Length; k++) starts.Add(k * ns);
                    int payload = (Css.PreambleSymbols + Css.SyncSymbols.Length + 2) * ns + ns / 4;
                    for (int k = 0; k < Css.PayloadSymbols; k++) starts.Add(payload + k * ns);
                    instants = [.. starts];
                    span = ns;
                    pass = s.BandwidthHz / 2;
                    break;
                }
            }
            double stop = Math.Min(1.5 * pass, native / 2);
            pass = Math.Min(pass, 0.9 * stop);

            if (!SignalEvm.ToNative(y, fsHz, startSample, offsetHz, native, recordSeconds, span + 2, pass, stop, filterWhole: true,
                                    out var x, out long n0, out bool whole, out why))
                return null;
            int m = x.Length;

            // the sent record through the same filter (periodic: exact), read at the window's samples
            var sent = Fft.Forward(carrier.Native);
            for (int k = 0; k < nTot; k++) sent[k] *= SignalEvm.RollOff((k <= nTot / 2 ? k : k - nTot) * native / nTot, pass, stop);
            var sentF = Fft.Inverse(sent);
            int w0 = (int)(((n0 % nTot) + nTot) % nTot);
            var sw = new Complex[m];
            for (int i = 0; i < m; i++) sw[i] = sentF[(w0 + i) % nTot];

            // the delay, from the response over the passband, taken out in the frequency domain
            var xs = Fft.Forward(x);
            var ss = Fft.Forward(sw);
            var h = new List<Complex>(); var hz = new List<double>();
            for (int k = -(m - 1) / 2; k <= m / 2; k++)
            {
                double f = k * native / m;
                if (Math.Abs(f) > pass) continue;
                int b = (k % m + m) % m;
                h.Add(xs[b] * Complex.Conjugate(ss[b]));
                hz.Add(f);
            }
            double tau = h.Count > 1 ? SignalEvm.Delay([.. h], [.. hz]) : 0;
            if (tau != 0)
            {
                for (int b = 0; b < m; b++)
                {
                    double f = (b <= m / 2 ? b : b - m) * native / m;
                    xs[b] *= Complex.FromPolarCoordinates(1.0, 2 * Math.PI * f * tau);
                }
                x = Fft.Inverse(xs);
            }

            // the instants held whole, clear of the ends of a window that is not whole records
            int guard = whole ? 0 : 4 * (int)Math.Ceiling(native / (2 * (stop - pass)));
            var at = new List<int>();
            if (whole)
            {
                for (long r = 0; r < m / nTot; r++)
                    foreach (int i in instants) at.Add((int)(((i - w0 + r * nTot) % m + m) % m));
            }
            else
                for (long r = (long)Math.Floor((double)n0 / nTot); r * nTot < n0 + m; r++)
                    foreach (int i in instants)
                    {
                        long i0 = r * nTot + i - n0;
                        if (i0 >= guard && i0 + span <= m - guard) at.Add((int)i0);
                    }
            if (at.Count == 0)
            {
                why = $"the run holds no whole {(kind == QualityKind.Css ? "chirp" : "bit")} clear of its ends; lengthen Stop";
                return null;
            }
            return new ReceivedWaveform(carrier, kind, x, sw, [.. at], span, guard, m - guard, native, tau, whole);
        }

        /// <summary>The quality of <paramref name="r"/>, as its kind is measured (the file header).</summary>
        public static WaveformQuality Evaluate(ReceivedWaveform r)
        {
            int m = r.X.Length;
            Complex X(int i) => r.X[(i % m + m) % m];
            Complex S(int i) => r.S[(i % m + m) % m];
            switch (r.Kind)
            {
                case QualityKind.Dbpsk:
                {
                    var rx = r.At.Select(X).ToArray();
                    var id = r.At.Select(S).ToArray();
                    double rms = Math.Sqrt(id.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary));
                    for (int k = 0; k < id.Length; k++) id[k] /= rms;
                    var e = SignalEvm.Score(r.Carrier.Spec, rx, id, id.Length, 1);
                    return new WaveformQuality(r.Kind, e.RmsPercent, e.PeakPercent, double.NaN, double.NaN, e.Received, e.Ideal,
                                               null, null, r.DelayS);
                }
                case QualityKind.Fsk:
                {
                    var shape = Fsk.Resolve(r.Carrier.Spec);
                    double k2 = r.NativeHz / (4 * Math.PI);
                    double Freq(Func<int, Complex> v, int i) => (v(i + 2) * Complex.Conjugate(v(i))).Phase * k2;
                    var fr = r.At.Select(i => Freq(X, i)).ToArray();
                    var fs = r.At.Select(i => Freq(S, i)).ToArray();
                    // f_rx = a f_sent + b, least squares
                    int n = fr.Length;
                    double mx = fs.Average(), my = fr.Average(), sxx = 0, sxy = 0;
                    for (int k = 0; k < n; k++) { sxx += (fs[k] - mx) * (fs[k] - mx); sxy += (fs[k] - mx) * (fr[k] - my); }
                    double a = sxx > 0 ? sxy / sxx : 1, b = my - a * mx;
                    double err = 0, peak = 0;
                    for (int k = 0; k < n; k++)
                    {
                        double e = fr[k] - a * fs[k] - b;
                        err += e * e;
                        peak = Math.Max(peak, Math.Abs(e));
                    }
                    return new WaveformQuality(r.Kind, 100 * Math.Sqrt(err / n) / shape.Deviation, 100 * peak / shape.Deviation,
                                               a * shape.Deviation, b, null, null, [.. fr.Select(v => v - b)], fs, r.DelayS);
                }
                default:
                {
                    double err = 0, sig = 0, peak = 0;
                    foreach (int i0 in r.At)
                    {
                        Complex num = 0; double den = 0;
                        for (int j = 0; j < r.Span; j++) { num += X(i0 + j) * Complex.Conjugate(S(i0 + j)); den += S(i0 + j).Magnitude * S(i0 + j).Magnitude; }
                        var g = num / den;
                        double e = 0, s = 0;
                        for (int j = 0; j < r.Span; j++)
                        {
                            var d = X(i0 + j) - g * S(i0 + j);
                            e += d.Real * d.Real + d.Imaginary * d.Imaginary;
                            s += (g * S(i0 + j)).Magnitude * (g * S(i0 + j)).Magnitude;
                        }
                        err += e; sig += s;
                        peak = Math.Max(peak, Math.Sqrt(e / s));
                    }
                    return new WaveformQuality(r.Kind, 100 * Math.Sqrt(err / sig), 100 * peak, double.NaN, double.NaN, null, null,
                                               null, null, r.DelayS);
                }
            }
        }

        /// <summary>In-channel signal over in-channel noise: <paramref name="quiet"/>'s power over the power of what
        /// <paramref name="noisy"/> adds to it — at the bit centres for DBPSK (what its EVM reads), over the filtered interior
        /// otherwise. NaN when the two do not match.</summary>
        public static double SignalToNoise(ReceivedWaveform noisy, ReceivedWaveform quiet)
        {
            if (noisy.X.Length != quiet.X.Length || noisy.Kind != quiet.Kind) return double.NaN;
            IEnumerable<int> idx = noisy.Kind == QualityKind.Dbpsk ? quiet.At : Enumerable.Range(quiet.From, quiet.To - quiet.From);
            double sig = 0, noise = 0;
            foreach (int i in idx)
            {
                var a = quiet.X[i]; var e = noisy.X[i] - a;
                sig += a.Real * a.Real + a.Imaginary * a.Imaginary;
                noise += e.Real * e.Real + e.Imaginary * e.Imaginary;
            }
            return sig / noise;
        }
    }
}
