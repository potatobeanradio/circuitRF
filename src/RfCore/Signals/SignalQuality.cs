// ================================================================
//  SignalQuality.cs  —  the received quality of a DBPSK, FSK, CSS or SC carrier (brief NZ-8, PCH-5)
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
//    SC     (brief PCH-5) The matched filter, applied to the received
//           and the sent record alike after the measurement filter
//           (which is flat over the carrier's band, so a pad still reads
//           zero), then EVM at each data symbol's instant against the
//           ideal point, normalised to the constellation's mean power
//           (unit). The preamble and pilot symbols are not measured: they
//           train the pilot equalizer.
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
    public enum QualityKind { Dbpsk, Fsk, Css, Sc }

    /// <summary>A received DBPSK, FSK or CSS record on its carrier's grid: <see cref="X"/> the received window, filtered and
    /// aligned, <see cref="S"/> the sent record through the same filter at the same samples, <see cref="At"/> the window
    /// indices of the instants measured (bit centres, or chirp starts), <see cref="Span"/> the samples one instant reads,
    /// and the interior [<see cref="From"/>, <see cref="To"/>) clear of the window's ends.</summary>
    public sealed record ReceivedWaveform(CarrierRecord Carrier, QualityKind Kind, Complex[] X, Complex[] S, int[] At, int Span,
                                          int From, int To, double NativeHz, double DelayS, bool WholeRecords)
    {
        /// <summary>SC: the ideal point at each of <see cref="At"/> (the data symbols' instants).</summary>
        public Complex[]? Ideal { get; init; }
        /// <summary>SC: the instants of the known (preamble and pilot) symbols the window holds, and their points.</summary>
        public int[]? KnownAt { get; init; }
        public Complex[]? KnownIdeal { get; init; }
    }

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
            CarrierStandard.Sc => QualityKind.Sc,
            _ => null,
        };

        /// <summary>
        /// Brings <paramref name="y"/> (sample 0 at record sample <paramref name="startSample"/> at <paramref name="fsHz"/>,
        /// the carrier at <paramref name="offsetHz"/>) to <paramref name="carrier"/>'s grid, filtered and aligned beside the
        /// sent record. Null, with <paramref name="why"/>, for any other carrier, a rate that is not a ratio of whole numbers
        /// to the carrier's, or a window holding no whole instant.
        /// </summary>
        public static ReceivedWaveform? Receive(CarrierRecord carrier, double recordSeconds, ReadOnlySpan<Complex> y, double fsHz,
                                                long startSample, double offsetHz, out string? why, double driftHzps = 0)
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
            ScShape? sc = null; ScSymbols? scSyms = null;
            switch (kind)
            {
                case QualityKind.Sc:
                {
                    sc = ScQam.Resolve(c);
                    scSyms = ScQam.Symbols(sc);
                    instants = [.. Enumerable.Range(0, scSyms.Points.Length).Select(k => k * ScQam.SamplesPerSymbol)];
                    span = 1;
                    pass = ScQam.BandwidthHz(sc) / 2;
                    break;
                }
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
                                    out var x, out long n0, out bool whole, out why, driftHzps))
                return null;
            int m = x.Length;

            // the sent record through the same filter (periodic: exact), read at the window's samples
            var sent = Fft.Forward(carrier.Native);
            for (int k = 0; k < nTot; k++) sent[k] *= SignalEvm.RollOff((k <= nTot / 2 ? k : k - nTot) * native / nTot, pass, stop);
            var sentF = Fft.Inverse(sent);
            if (sc is not null)
            {
                // an SC carrier is read through its matched filter, received and sent alike
                x = ScQam.Filter(sc, x, native, matched: true);
                sentF = ScQam.Filter(sc, sentF, native, matched: true);
            }
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
            // The sent record repeats every nTot samples, so the delay is known only to a whole record: the one nearest zero is
            // taken. Any other is invisible with no offset, but the circular shift moves the window's wrap, and with it an
            // offset's phase ramp's jump, into the middle of the window: a 500 Hz offset fitted as 0 Hz (brief PCH-12).
            double period = nTot / native;
            tau -= Math.Round(tau / period) * period;
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
            // and of the matched filter's own ringing: its span, or ~4/α symbols of a whole-record filter's tail
            if (!whole && sc is not null)
                guard += ScQam.SamplesPerSymbol * (sc.Span is int sp ? sp / 2 + 1 : (int)Math.Ceiling(4 / Math.Max(sc.RollOff, 0.1)));
            var at = new List<int>(); var which = new List<int>();
            if (whole)
            {
                for (long r = 0; r < m / nTot; r++)
                    for (int j = 0; j < instants.Length; j++) { at.Add((int)(((instants[j] - w0 + r * nTot) % m + m) % m)); which.Add(j); }
            }
            else
                for (long r = (long)Math.Floor((double)n0 / nTot); r * nTot < n0 + m; r++)
                    for (int j = 0; j < instants.Length; j++)
                    {
                        long i0 = r * nTot + instants[j] - n0;
                        if (i0 >= guard && i0 + span <= m - guard) { at.Add((int)i0); which.Add(j); }
                    }
            if (scSyms is not null)
            {
                // the data symbols are measured; the known ones are kept apart, for the pilot equalizer
                var data = Enumerable.Range(0, at.Count).Where(i => !scSyms.Known[which[i]]).ToArray();
                var known = Enumerable.Range(0, at.Count).Where(i => scSyms.Known[which[i]]).ToArray();
                if (data.Length == 0)
                {
                    why = "the run holds no data symbol clear of its ends; lengthen Stop";
                    return null;
                }
                return new ReceivedWaveform(carrier, kind, x, sw, [.. data.Select(i => at[i])], span, guard, m - guard, native, tau, whole)
                {
                    Ideal = [.. data.Select(i => scSyms.Points[which[i]])],
                    KnownAt = [.. known.Select(i => at[i])],
                    KnownIdeal = [.. known.Select(i => scSyms.Points[which[i]])],
                };
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
                case QualityKind.Sc:
                {
                    // against the ideal points, normalised to the constellation's mean power (unit)
                    var e = SignalEvm.Score(r.Carrier.Spec, [.. r.At.Select(X)], (Complex[])r.Ideal!.Clone(), r.At.Length, 1, meanPower: 1);
                    return new WaveformQuality(r.Kind, e.RmsPercent, e.PeakPercent, double.NaN, double.NaN, e.Received, e.Ideal,
                                               null, null, r.DelayS);
                }
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

        /// <summary>
        /// A DBPSK or SC carrier's frequency offset to a fraction of an FFT bin of the window, found before anything is aligned.
        /// <see cref="Receive"/> fits the delay from the received-against-sent response bin by bin, and an offset of more
        /// than about one bin (1/window: 5 kHz over 0.2 ms) turns that response across the band, so the delay, every symbol's
        /// phase and the offset fitted from them come out as noise: a low-orbit downlink's 400 kHz read as BER 0.5.
        /// <para>Two steps, each one transform. The delay first, from the power envelopes |x|² and |s|², which an offset does
        /// not touch: their circular cross-correlation peaks at the delay. Then, at that delay, x·conj(s) is the offset alone,
        /// a tone, and its zero-padded spectrum peaks at it, refined between bins by a parabola. The peak is taken only where it
        /// stands well clear of the rest of the spectrum (four times its mean), and only beyond a bin (0 otherwise), so a
        /// carrier within a bin of its frequency is read exactly as before. Searched to half the carrier's half-bandwidth either
        /// side. (It once correlated the two power spectra's line-by-line detail, which a periodic PN record without a preamble
        /// leaves nearly flat: QPSK at 73 kHz read 1.9 MHz.)</para>
        /// </summary>
        public static double CoarseOffset(CarrierRecord carrier, double recordSeconds, ReadOnlySpan<Complex> y, double fsHz,
                                          long startSample, double offsetHz)
        {
            var c = carrier.Spec;
            double pass;
            switch (KindOf(c.Standard))
            {
                case QualityKind.Sc: pass = ScQam.BandwidthHz(ScQam.Resolve(c)) / 2; break;
                case QualityKind.Dbpsk: pass = Dbpsk.Resolve(c).BitRate; break;
                default: return 0;
            }
            double native = carrier.NativeRateHz;
            int nTot = carrier.Native.Length;
            double range = pass / 2;
            double stop = native / 2, wide = Math.Min(pass + range, 0.9 * stop);
            if (!SignalEvm.ToNative(y, fsHz, startSample, offsetHz, native, recordSeconds, 8, wide, stop, filterWhole: true,
                                    out var x, out long n0, out _, out _))
                return 0;
            int m = x.Length;
            var sent = Fft.Forward(carrier.Native);
            for (int k = 0; k < nTot; k++) sent[k] *= SignalEvm.RollOff((k <= nTot / 2 ? k : k - nTot) * native / nTot, wide, stop);
            var sentF = Fft.Inverse(sent);
            int w0 = (int)(((n0 % nTot) + nTot) % nTot);
            var sw = new Complex[m];
            for (int i = 0; i < m; i++) sw[i] = sentF[(w0 + i) % nTot];

            // the delay: the power envelopes' circular cross-correlation (means out), at its peak
            int padT = 1;
            while (padT < 2 * m) padT <<= 1;
            var px = new Complex[padT]; var ps = new Complex[padT];
            double mx = 0, ms = 0;
            for (int i = 0; i < m; i++) { mx += x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary; ms += sw[i].Real * sw[i].Real + sw[i].Imaginary * sw[i].Imaginary; }
            mx /= m; ms /= m;
            for (int i = 0; i < m; i++)
            {
                px[i] = x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary - mx;
                ps[i] = sw[i].Real * sw[i].Real + sw[i].Imaginary * sw[i].Imaginary - ms;
            }
            Fft.Transform(px, inverse: false);
            Fft.Transform(ps, inverse: false);
            for (int k = 0; k < padT; k++) px[k] *= Complex.Conjugate(ps[k]);
            Fft.Transform(px, inverse: true);                  // Σ_i px(i + τ)·ps(i) at τ
            int lag = 0;
            int maxLag = Math.Min(m / 2, nTot / 2);
            for (int t = -maxLag; t <= maxLag; t++)
                if (px[(t + padT) % padT].Real > px[(lag + padT) % padT].Real) lag = t;

            // the offset: x·conj(s) at that delay, a tone, its spectrum's peak within the range
            int padF = 1;
            while (padF < 4 * m) padF <<= 1;
            var z = new Complex[padF];
            for (int i = 0; i < m; i++)
            {
                int j = i - lag;
                if (j < 0 || j >= m) continue;
                z[i] = x[i] * Complex.Conjugate(sw[j]);
            }
            Fft.Transform(z, inverse: false);                  // e^(−j): a tone at +f peaks at +k
            double fineBin = native / padF;
            int kmax = Math.Min((int)Math.Floor(range / fineBin), padF / 2 - 2);
            double P(int k) => z[(k % padF + padF) % padF].Magnitude;
            int best = 0;
            double mean = 0;
            for (int k = -kmax; k <= kmax; k++) { double v = P(k); mean += v; if (v > P(best)) best = k; }
            mean /= 2 * kmax + 1;
            if (!(P(best) > 4 * mean)) return 0;
            double cm = P(best - 1), c0 = P(best), cp = P(best + 1), den = cm - 2 * c0 + cp;
            double frac = den < 0 ? Math.Clamp(0.5 * (cm - cp) / den, -0.5, 0.5) : 0;
            double f = (best + frac) * fineBin;
            return Math.Abs(f) <= native / m ? 0 : f;
        }

        /// <summary>In-channel signal over in-channel noise: <paramref name="quiet"/>'s power over the power of what
        /// <paramref name="noisy"/> adds to it — at the bit centres for DBPSK and the data symbols for SC (what their EVM reads), over the filtered interior
        /// otherwise. NaN when the two do not match.</summary>
        public static double SignalToNoise(ReceivedWaveform noisy, ReceivedWaveform quiet)
        {
            if (noisy.X.Length != quiet.X.Length || noisy.Kind != quiet.Kind) return double.NaN;
            IEnumerable<int> idx = noisy.Kind is QualityKind.Dbpsk or QualityKind.Sc ? quiet.At : Enumerable.Range(quiet.From, quiet.To - quiet.From);
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
