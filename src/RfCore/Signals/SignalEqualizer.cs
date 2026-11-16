// ================================================================
//  SignalEqualizer.cs  —  frequency offset, and the equalizer a receiver applies (brief PCH-4)
//
//  SignalEvm fits one complex gain over the record, on purpose: a circuit's in-band ripple is EVM. Through a
//  channel with delay spread that reads a frequency-selective fade as distortion, and a carrier offset rotates
//  the constellation across the record. Equalizer= says how the far end reads it:
//
//    none   today's single gain, unchanged (the offset is reported, not removed)
//    genie  OFDM: per subcarrier from the true channel — a propagation channel's H(f, t) when the run supplies
//           one (IChannelResponse) times one fitted gain; otherwise the per-subcarrier response of the received
//           against the played record, averaged over the record. DBPSK: a linear MMSE equalizer from that
//           response, in the frequency domain. Never a per-element fit, which would fit the error away.
//    pilot  OFDM: from a comb of resource elements only (every PilotSubcarriers-th subcarrier of every
//           PilotSymbols-th symbol), the response at each pilot a least-squares fit over the PilotAverage pilot
//           symbols either side, interpolated in frequency (extrapolated along the end segments), then in time; EVM
//           on the rest.
//           DBPSK: a symbol-spaced FIR of Taps taps, least-squares trained on the first Training symbols and
//           applied to the remainder.
//
//  The offset is data-aided: each symbol's (OFDM) or bit centre's (DBPSK) phase against the played record,
//  unwrapped in time and fitted as φ = a + 2π·f·τ + π·ḟ·τ², τ from the window's middle. With an equalizer it
//  is removed BEFORE the equalizer, in the time domain (the window demodulated again at the corrected offset,
//  three times, each removing what the last left), so no inter-carrier interference is left by it. The phase
//  step between successive OFDM symbols must stay inside ±π: an offset beyond half the subcarrier spacing
//  cannot be told from one a subcarrier away.
//
//  Each element's post-equalizer SINR (the per-element cube, and the semi-analytic BER) is
//  |H_eff|²·Es / N0, H_eff the response that maps an ideal point to what was received and N0 the
//  pre-equalizer error power, |error·H_eff|² averaged over the elements: a faded subcarrier's noise is
//  enhanced by the equalizer, and the fade is in the cube. With none, H_eff is the one gain.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public enum EqualizerMode { None, Genie, Pilot }

    /// <param name="PilotSubcarriers">The pilot comb's spacing in subcarriers.</param>
    /// <param name="PilotSymbols">The pilot comb's spacing in OFDM symbols (1: every symbol carries pilots).</param>
    /// <param name="Taps">Single carrier, pilot: the FIR's taps (odd; an even count gains one).</param>
    /// <param name="Training">Single carrier, pilot: the symbols the FIR is trained on.</param>
    public sealed record EqualizerSettings(EqualizerMode Mode = EqualizerMode.None, int PilotSubcarriers = 6, int PilotSymbols = 4,
                                           int Taps = 11, int Training = 64)
    {
        public static readonly EqualizerSettings Off = new();

        /// <summary>The words <c>Equalizer=</c> takes.</summary>
        public static readonly string[] Words = ["none", "genie", "pilot"];

        public static EqualizerMode? ParseMode(string? text) => (text ?? "none").Trim().ToLowerInvariant() switch
        {
            "" or "none" => EqualizerMode.None,
            "genie" => EqualizerMode.Genie,
            "pilot" => EqualizerMode.Pilot,
            _ => null,
        };

        /// <summary><c>Pilots=</c>: "subcarriers,symbols", or one number (every symbol carries pilots). Null when not that.</summary>
        public static (int Subcarriers, int Symbols)? ParsePilots(string? text)
        {
            var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries);
            static int? Whole(string s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) && v >= 1 ? v : null;
            return parts.Length switch
            {
                1 when Whole(parts[0]) is { } f => (f, 1),
                2 when Whole(parts[0]) is { } f && Whole(parts[1]) is { } t => (f, t),
                _ => null,
            };
        }
    }

    /// <summary>The true channel a run supplies (a Propagation Channel's H(f, t), PCH-6): the response at an offset from the
    /// carrier's centre (Hz) at a window time (s).</summary>
    public interface IChannelResponse
    {
        Complex Response(double offsetHz, double timeS);
    }

    /// <summary>A carrier offset and its drift, Hz and Hz/s.</summary>
    public readonly record struct CarrierOffset(double FrequencyHz, double DriftHzps)
    {
        public static readonly CarrierOffset Zero = new(0, 0);
        public CarrierOffset Plus(CarrierOffset o) => new(FrequencyHz + o.FrequencyHz, DriftHzps + o.DriftHzps);
    }

    /// <summary>
    /// An equalized carrier: the measured points on the ideal points' scale beside the ideal points, the EVM, and each
    /// element's SINR (linear) — <see cref="Sinr"/> beside <see cref="Points"/>, and <see cref="SinrGrid"/> over the
    /// carrier's grid, [symbol, subcarrier] row-major (one column for a single carrier), NaN where nothing was measured
    /// (a pilot). <see cref="Note"/> says what the equalizer could not do.
    /// </summary>
    public sealed record Equalized(EqualizerMode Mode, Complex[] Points, Complex[] Ideal, double RmsPercent, double PeakPercent,
                                   double[] Sinr, double[] SinrGrid, int Rows, int Columns, string? Note = null)
    {
        /// <summary>A single carrier's points: the window index of each one's instant (DBPSK's differential bits pair
        /// successive bits by it); null for OFDM.</summary>
        public int[]? Instants { get; init; }
    }

    public static class SignalEqualizer
    {
        /// <summary>How many times the offset is estimated and taken out again.</summary>
        public const int OffsetPasses = 3;

        /// <summary>OFDM offset: how many times each subcarrier's response is rebuilt from the latest estimate.</summary>
        public const int ResponsePasses = 3;

        /// <summary>Pilot OFDM: the pilot symbols either side of one that its response is averaged with.</summary>
        public const int PilotAverage = 4;

        // ── the offset ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>OFDM: each whole symbol's phase, Σ_k received·conj(H_k·sent) with H_k the subcarrier's response over the
        /// record, fitted over the symbols' times.</summary>
        public static CarrierOffset EstimateOffset(ReceivedSymbols d)
        {
            if (d.SymbolTimeS is not { } times) return CarrierOffset.Zero;
            int nsc = d.SubcarrierHz.Length;
            // a coarse offset first, from each symbol's Σ_k rx·conj(sent) alone: biased by a frequency-selective channel,
            // but it turns with the offset, which the response average below must not
            var tc = new List<double>(); var pc = new List<double>();
            for (int s = 0; s < d.Symbols; s++)
            {
                if (d.WrapsWindow?[s] == true) continue;
                Complex acc = 0;
                for (int k = 0; k < nsc; k++) acc += d.Received[s * nsc + k] * Complex.Conjugate(d.Sent[s * nsc + k]);
                if (acc == Complex.Zero) continue;
                tc.Add(times[s]); pc.Add(acc.Phase);
            }
            var est = Fit(tc, pc);
            double tMid = tc.Count > 0 ? tc.Average() : 0;
            // each subcarrier's response over the record, with the offset's rotation taken out of every symbol first, so a
            // frequency-selective channel weighs every symbol's phase alike whatever its data (Σ_k H_k·|s_k|² would turn
            // with the data otherwise). Averaged as it came, an offset of a cycle or more over the record cancels the
            // average to noise: 5 kHz over 1 ms read 56 Hz. The coarse offset is itself biased by the channel, and a
            // rotation taken out wrongly gives each response a phase error that turns with the data again, so the
            // responses are rebuilt from each refined estimate (ResponsePasses).
            var hk = new Complex[nsc];
            for (int pass = 0; pass < ResponsePasses; pass++)
            {
                Array.Clear(hk);
                for (int s = 0; s < d.Symbols; s++)
                {
                    if (d.WrapsWindow?[s] == true) continue;   // as the phases below: a symbol across the window's seam is not one symbol
                    double tau = times[s] - tMid;
                    var undo = Complex.FromPolarCoordinates(1.0, -(2 * Math.PI * est.FrequencyHz * tau + Math.PI * est.DriftHzps * tau * tau));
                    for (int k = 0; k < nsc; k++) hk[k] += d.Received[s * nsc + k] * Complex.Conjugate(d.Sent[s * nsc + k]) * undo;
                }
                var t = new List<double>(); var ph = new List<double>();
                for (int s = 0; s < d.Symbols; s++)
                {
                    if (d.WrapsWindow?[s] == true) continue;
                    Complex acc = 0;
                    for (int k = 0; k < nsc; k++) acc += d.Received[s * nsc + k] * Complex.Conjugate(hk[k] * d.Sent[s * nsc + k]);
                    if (acc == Complex.Zero) continue;
                    t.Add(times[s]); ph.Add(acc.Phase);
                }
                est = Fit(t, ph);
            }
            return est;
        }

        /// <summary>DBPSK: each bit centre's phase, received·conj(sent) (the sent sign takes the data out), over time; SC
        /// the same at every symbol instant, data and known.</summary>
        /// <para>Each point is ONE noisy symbol (OFDM's sums a symbol's subcarriers), so unwrapped step to step a pair of
        /// noisy phases more than π apart adds a cycle to every later point: 16QAM at Es/N0 12 dB read hundreds of Hz rms.
        /// So the offset is acquired first as the peak of the products' spectrum on the instants' own grid, and the
        /// phases left after it are unwrapped against a running sum of their neighbours (a phase track ±<see cref="TrackHalf"/>
        /// points wide, which noise cannot slip and which still follows a drift), then fitted.</para>
        public static CarrierOffset EstimateOffset(ReceivedWaveform w)
        {
            if (w.Kind is not (QualityKind.Dbpsk or QualityKind.Sc)) return CarrierOffset.Zero;
            var idx = new List<int>(); var v = new List<Complex>();
            foreach (int i in w.At.Concat(w.KnownAt ?? []).Distinct().Order())
            {
                var z = w.X[i] * Complex.Conjugate(w.S[i]);
                if (z == Complex.Zero) continue;
                idx.Add(i); v.Add(z);
            }
            int n = idx.Count;
            if (n < 2) return CarrierOffset.Zero;

            // the acquisition: a zero-padded spectrum over the instants' grid (their spacing's greatest common divisor)
            int step = 0;
            for (int j = 1; j < n; j++) step = Gcd(step, idx[j] - idx[0]);
            long len = (idx[^1] - idx[0]) / step + 1;
            double f0 = 0;
            if (len <= 1 << 21)
            {
                int pad = 8;   // eight times the span where that is at most 2^21 points (32 MB), the span's own at least
                while (pad < len || pad < 8 * len && pad < 1 << 21) pad <<= 1;
                var g = new Complex[pad];
                for (int j = 0; j < n; j++) g[(idx[j] - idx[0]) / step] += v[j];
                Fft.Transform(g, inverse: false);   // e^(−j): a tone at +f peaks at +k
                int best = 0;
                for (int b = 1; b < pad; b++) if (g[b].Magnitude > g[best].Magnitude) best = b;
                f0 = (best <= pad / 2 ? best : best - pad) * w.NativeHz / ((double)step * pad);
            }

            // the phases left, about the middle instant, unwrapped against their running phase track
            double tm = idx.Average() / w.NativeHz;
            var t = new double[n]; var r = new Complex[n];
            for (int j = 0; j < n; j++)
            {
                t[j] = idx[j] / w.NativeHz;
                r[j] = v[j] * Complex.FromPolarCoordinates(1.0, -2 * Math.PI * f0 * (t[j] - tm));
            }
            var prefix = new Complex[n + 1];
            for (int j = 0; j < n; j++) prefix[j + 1] = prefix[j] + r[j];
            var ph = new double[n];
            double track = 0;
            for (int j = 0; j < n; j++)
            {
                var sum = prefix[Math.Min(n, j + TrackHalf + 1)] - prefix[Math.Max(0, j - TrackHalf)];
                double tj = sum == Complex.Zero ? track : sum.Phase;
                if (j > 0) tj += 2 * Math.PI * Math.Round((track - tj) / (2 * Math.PI));
                track = tj;
                ph[j] = track + Wrap(r[j].Phase - track);
            }
            var fine = Fit(t, ph, unwrap: false);
            return new CarrierOffset(f0 + fine.FrequencyHz, fine.DriftHzps);
        }

        /// <summary>The single-carrier phase track's half width, in points.</summary>
        internal const int TrackHalf = 16;

        private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

        private static double Wrap(double x) => x - 2 * Math.PI * Math.Round(x / (2 * Math.PI));

        /// <summary>The phases, sorted by time and unwrapped step to step, fitted as a + b·τ + c·τ² (a line under five
        /// points, nothing under two), τ from the middle: f = b/2π, ḟ = c/π.</summary>
        internal static CarrierOffset Fit(IReadOnlyList<double> time, IReadOnlyList<double> phase, bool unwrap = true)
        {
            int n = time.Count;
            if (n < 2) return CarrierOffset.Zero;
            var order = Enumerable.Range(0, n).OrderBy(i => time[i]).ToArray();
            var t = order.Select(i => time[i]).ToArray();
            var p = order.Select(i => phase[i]).ToArray();
            if (unwrap) for (int i = 1; i < n; i++) p[i] += 2 * Math.PI * Math.Round((p[i - 1] - p[i]) / (2 * Math.PI));
            double tm = t.Average();
            for (int i = 0; i < n; i++) t[i] -= tm;
            int deg = n >= 5 ? 2 : 1;
            // normal equations of the polynomial fit, (deg+1)², small
            int m = deg + 1;
            var a = new double[m, m]; var r = new double[m];
            for (int i = 0; i < n; i++)
            {
                double tp = 1;
                var pow = new double[2 * m];
                for (int j = 0; j < 2 * m; j++) { pow[j] = tp; tp *= t[i]; }
                for (int j = 0; j < m; j++)
                {
                    r[j] += pow[j] * p[i];
                    for (int k = 0; k < m; k++) a[j, k] += pow[j + k];
                }
            }
            var c = Solve(a, r);
            return new CarrierOffset(c[1] / (2 * Math.PI), deg == 2 ? c[2] / Math.PI : 0);
        }

        private static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone(); var x = (double[])b.Clone();
            for (int i = 0; i < n; i++)
            {
                int piv = i;
                for (int k = i + 1; k < n; k++) if (Math.Abs(m[k, i]) > Math.Abs(m[piv, i])) piv = k;
                for (int k = 0; k < n; k++) (m[i, k], m[piv, k]) = (m[piv, k], m[i, k]);
                (x[i], x[piv]) = (x[piv], x[i]);
                for (int r = i + 1; r < n; r++)
                {
                    double f = m[r, i] / m[i, i];
                    for (int k = i; k < n; k++) m[r, k] -= f * m[i, k];
                    x[r] -= f * x[i];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                for (int k = i + 1; k < n; k++) x[i] -= m[i, k] * x[k];
                x[i] /= m[i, i];
            }
            return x;
        }

        /// <summary>
        /// The offset taken out: <paramref name="demodulate"/> (the window read at an extra offset and drift) called again at
        /// each estimate, <see cref="OffsetPasses"/> times, each removing what the last left. Returns the last reading and
        /// the total offset removed; the first reading when a pass cannot be read.
        /// </summary>
        public static T RemoveOffset<T>(T first, Func<CarrierOffset, T?> demodulate, Func<T, CarrierOffset> estimate, out CarrierOffset total)
            where T : class
        {
            total = estimate(first);
            var best = first;
            for (int pass = 0; pass < OffsetPasses; pass++)
            {
                if (demodulate(total) is not { } next) { total = CarrierOffset.Zero; return first; }
                best = next;
                var left = estimate(next);
                if (pass == OffsetPasses - 1) break;
                total = total.Plus(left);
            }
            return best;
        }

        // ── OFDM ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The OFDM carrier <paramref name="d"/> equalized as <paramref name="s"/> says.</summary>
        public static Equalized Ofdm(ReceivedSymbols d, EqualizerSettings s, IChannelResponse? channel = null)
        {
            int nsc = d.SubcarrierHz.Length, ns = d.Symbols;
            if (s.Mode == EqualizerMode.None)
            {
                var e = SignalEvm.Evaluate(d);
                var g = Enumerable.Repeat(e.Gain, e.Received.Length).ToArray();
                return Finish(EqualizerMode.None, e.Received, e.Ideal, e, g, null, ns, nsc);
            }
            var h = new Complex[ns * nsc];                          // maps the reference (sent, or ideal at pilots) to received
            bool[]? measured = null;
            Complex[] rx = d.Received;
            string? note = null;
            if (s.Mode == EqualizerMode.Genie)
            {
                if (channel is not null && d.SymbolTimeS is { } times)
                {
                    var hc = new Complex[ns * nsc];
                    Complex num = 0; double den = 0;
                    for (int r = 0; r < ns; r++)
                        for (int k = 0; k < nsc; k++)
                        {
                            int i = r * nsc + k;
                            hc[i] = channel.Response(d.SubcarrierHz[k], times[r]);
                            var hs = hc[i] * d.Sent[i];
                            num += rx[i] * Complex.Conjugate(hs);
                            den += hs.Real * hs.Real + hs.Imaginary * hs.Imaginary;
                        }
                    var g = den > 0 ? num / den : Complex.One;
                    for (int i = 0; i < h.Length; i++) h[i] = g * hc[i];
                }
                else
                {
                    var hk = new Complex[nsc]; var pk = new double[nsc];
                    for (int i = 0; i < rx.Length; i++)
                    {
                        hk[i % nsc] += rx[i] * Complex.Conjugate(d.Sent[i]);
                        pk[i % nsc] += d.Sent[i].Real * d.Sent[i].Real + d.Sent[i].Imaginary * d.Sent[i].Imaginary;
                    }
                    for (int i = 0; i < h.Length; i++) h[i] = pk[i % nsc] > 0 ? hk[i % nsc] / pk[i % nsc] : Complex.One;
                }
                var z = new Complex[rx.Length];
                for (int i = 0; i < z.Length; i++) z[i] = h[i] == Complex.Zero ? rx[i] : rx[i] / h[i];
                var e = SignalEvm.Score(d.Carrier, z, (Complex[])d.Ideal.Clone(), ns, nsc);
                // H_eff maps the ideal point to the received one: H times the gain from the ideal scale to the sent one
                var heff = h.Select(v => v * e.Gain).ToArray();
                return Finish(EqualizerMode.Genie, e.Received, e.Ideal, e, heff, null, ns, nsc);
            }

            // pilot: the delay out first (as the none measurement does), so the comb interpolates a slowly varying response
            var hDelay = new Complex[nsc];
            for (int i = 0; i < rx.Length; i++) hDelay[i % nsc] += rx[i] * Complex.Conjugate(d.Sent[i]);
            double tau = SignalEvm.Delay(hDelay, d.SubcarrierHz);
            if (tau != 0)
            {
                rx = (Complex[])rx.Clone();
                for (int i = 0; i < rx.Length; i++) rx[i] *= Complex.FromPolarCoordinates(1.0, 2 * Math.PI * d.SubcarrierHz[i % nsc] * tau);
            }
            int pf = Math.Max(1, s.PilotSubcarriers), pt = Math.Max(1, s.PilotSymbols);
            var row = d.SymbolIndex ?? [.. Enumerable.Range(0, ns)];
            var order = Enumerable.Range(0, ns).OrderBy(r => d.SymbolTimeS?[r] ?? r).ToArray();
            var pilotRows = order.Where(r => row[r] % pt == 0).ToArray();
            var pilotCols = Enumerable.Range(0, nsc).Where(k => k % pf == 0).ToArray();
            if (pilotRows.Length == 0 || pilotCols.Length == 0)
            {
                note = $"no symbol in the window carries the pilot comb (every {pt}th symbol); measured with one gain instead";
                return Ofdm(d, s with { Mode = EqualizerMode.None }) with { Note = note };
            }
            measured = new bool[ns * nsc];
            // the response at each pilot, least squares over the PilotAverage pilot symbols either side of it,
            // Σ rx·conj(s) / Σ |s|² (a pilot is one noisy element, and dividing by an inner QAM point would amplify its
            // noise; a receiver filters its estimates over time), then interpolated in frequency along each pilot symbol
            var hp = new Complex[pilotRows.Length][];
            for (int pi = 0; pi < pilotRows.Length; pi++)
            {
                int a0 = Math.Max(0, pi - PilotAverage), a1 = Math.Min(pilotRows.Length - 1, pi + PilotAverage);
                var at = new Complex[pilotCols.Length];
                for (int c = 0; c < pilotCols.Length; c++)
                {
                    int k = pilotCols[c];
                    Complex num = 0; double den = 0;
                    for (int q = a0; q <= a1; q++)
                    {
                        int i = pilotRows[q] * nsc + k;
                        num += rx[i] * Complex.Conjugate(d.Ideal[i]);
                        den += d.Ideal[i].Real * d.Ideal[i].Real + d.Ideal[i].Imaginary * d.Ideal[i].Imaginary;
                    }
                    at[c] = den > 0 ? num / den : Complex.One;
                }
                hp[pi] = Interpolate(pilotCols, at, nsc);
            }
            // then in time, each subcarrier between the pilot symbols around it (held beyond the first and the last)
            double T(int r) => d.SymbolTimeS?[r] ?? r;
            for (int oi = 0; oi < order.Length; oi++)
            {
                int r = order[oi];
                double tr = T(r);
                int after = Array.FindIndex(pilotRows, pr => T(pr) >= tr);
                for (int k = 0; k < nsc; k++)
                {
                    Complex v;
                    if (after < 0) v = hp[^1][k];
                    else if (after == 0 || T(pilotRows[after]) == tr) v = hp[after][k];
                    else
                    {
                        double t0 = T(pilotRows[after - 1]), t1 = T(pilotRows[after]), u = (tr - t0) / (t1 - t0);
                        v = hp[after - 1][k] * (1 - u) + hp[after][k] * u;
                    }
                    h[r * nsc + k] = v;
                    measured[r * nsc + k] = !(row[r] % pt == 0 && k % pf == 0);
                }
            }
            var data = Enumerable.Range(0, rx.Length).Where(i => measured[i]).ToArray();
            var zd = data.Select(i => h[i] == Complex.Zero ? rx[i] : rx[i] / h[i]).ToArray();
            var ed = SignalEvm.Score(d.Carrier, zd, data.Select(i => d.Ideal[i]).ToArray(), ns, nsc);
            var heffAll = h.Select(v => v * ed.Gain).ToArray();
            return Finish(EqualizerMode.Pilot, ed.Received, ed.Ideal, ed, data.Select(i => heffAll[i]).ToArray(), data, ns, nsc) with { Note = note };
        }

        /// <summary>Linear interpolation of <paramref name="v"/> known at <paramref name="at"/> (increasing) over 0..n−1,
        /// extrapolated along the end segments beyond the first and last (a held value there would leave the band edge's
        /// phase slope as error); one known value is held.</summary>
        private static Complex[] Interpolate(int[] at, Complex[] v, int n)
        {
            var o = new Complex[n];
            if (at.Length == 1) { Array.Fill(o, v[0]); return o; }
            int j = 0;
            for (int k = 0; k < n; k++)
            {
                while (j < at.Length - 2 && at[j + 1] <= k) j++;
                double u = (double)(k - at[j]) / (at[j + 1] - at[j]);
                o[k] = v[j] * (1 - u) + v[j + 1] * u;
            }
            return o;
        }

        /// <summary>The measured points, their EVM and each one's SINR: |H_eff|²·Es / N0, N0 the mean of |error·H_eff|².
        /// <paramref name="where"/> places each point on the grid (null: every element, in order).</summary>
        private static Equalized Finish(EqualizerMode mode, Complex[] points, Complex[] ideal, EvmResult e, Complex[] heff,
                                        int[]? where, int rows, int cols)
        {
            double es = 0, n0 = 0;
            for (int i = 0; i < points.Length; i++)
            {
                es += ideal[i].Real * ideal[i].Real + ideal[i].Imaginary * ideal[i].Imaginary;
                var pre = (points[i] - ideal[i]) * heff[i];
                n0 += pre.Real * pre.Real + pre.Imaginary * pre.Imaginary;
            }
            es /= Math.Max(1, points.Length); n0 /= Math.Max(1, points.Length);
            var sinr = new double[points.Length];
            for (int i = 0; i < sinr.Length; i++)
            {
                double g2 = heff[i].Real * heff[i].Real + heff[i].Imaginary * heff[i].Imaginary;
                sinr[i] = n0 > 0 ? g2 * es / n0 : double.PositiveInfinity;
            }
            var grid = new double[rows * cols];
            Array.Fill(grid, double.NaN);
            for (int i = 0; i < sinr.Length; i++)
            {
                int at = where?[i] ?? i;
                if (at < grid.Length) grid[at] = sinr[i];
            }
            return new Equalized(mode, points, ideal, e.RmsPercent, e.PeakPercent, sinr, grid, rows, cols);
        }

        // ── DBPSK ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The DBPSK carrier <paramref name="w"/> equalized as <paramref name="s"/> says, read at its bit centres.</summary>
        public static Equalized Dbpsk(ReceivedWaveform w, EqualizerSettings s)
        {
            if (w.Kind != QualityKind.Dbpsk) throw new ArgumentException("only a DBPSK carrier is equalized here", nameof(w));
            var at = w.At.Order().ToArray();
            int m = w.X.Length;
            Complex[] Ref(IEnumerable<int> idx)
            {
                var id = idx.Select(i => w.S[(i % m + m) % m]).ToArray();
                double rms = Math.Sqrt(w.At.Select(i => w.S[(i % m + m) % m]).Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary));
                for (int k = 0; k < id.Length; k++) id[k] /= rms;
                return id;
            }
            switch (s.Mode)
            {
                case EqualizerMode.None:
                {
                    var q = SignalQuality.Evaluate(w);
                    var e = new EvmResult(q.ErrorPercent, q.PeakPercent, 0, q.Points!, q.Reference!, q.Points!.Length, 1);
                    return SingleCarrier(EqualizerMode.None, e, null) with { Instants = w.At };
                }
                case EqualizerMode.Genie:
                {
                    var z = GenieMmse(w.X, w.S);
                    var e = SignalEvm.Score(w.Carrier.Spec, at.Select(i => z[i]).ToArray(), Ref(at), at.Length, 1);
                    return SingleCarrier(EqualizerMode.Genie, e, null) with { Instants = at };
                }
                default:
                {
                    int taps = Math.Max(1, s.Taps | 1), sps = RfCore.Signals.Dbpsk.SamplesPerBit;
                    int train = Math.Min(s.Training, at.Length - 1);
                    if (train < taps)
                        return Dbpsk(w, s with { Mode = EqualizerMode.None }) with
                        { Note = $"{at.Length} bit centres leave {train} to train a {taps}-tap FIR on; measured with one gain instead" };
                    var ideal = Ref(at);
                    var rest = at.Skip(train).ToArray();
                    var z = Fir(w.X, at[..train], ideal[..train], taps, sps, rest);
                    var e = SignalEvm.Score(w.Carrier.Spec, z, ideal.Skip(train).ToArray(), rest.Length, 1);
                    return SingleCarrier(EqualizerMode.Pilot, e, null) with { Instants = rest };
                }
            }
        }

        /// <summary>
        /// MMSE from the response of the received <paramref name="x"/> against the played record <paramref name="sent"/>, each
        /// bin's response and noise averaged over its neighbours (the noise floor from what the response does not explain).
        /// </summary>
        private static Complex[] GenieMmse(Complex[] x, Complex[] sent)
        {
            int m = x.Length;
            var xs = Fft.Forward(x); var ss = Fft.Forward(sent);
            const int half = 4;
            var hb = new Complex[m]; var nb = new double[m]; var pb = new double[m];
            for (int b = 0; b < m; b++)
            {
                Complex xsSum = 0; double sp = 0;
                for (int j = -half; j <= half; j++) { int q = ((b + j) % m + m) % m; xsSum += xs[q] * Complex.Conjugate(ss[q]); sp += ss[q].Magnitude * ss[q].Magnitude; }
                hb[b] = sp > 0 ? xsSum / sp : Complex.Zero;
                pb[b] = sp / (2 * half + 1);
            }
            for (int b = 0; b < m; b++)
            {
                double nsum = 0;
                for (int j = -half; j <= half; j++) { int q = ((b + j) % m + m) % m; nsum += (xs[q] - hb[q] * ss[q]).Magnitude * (xs[q] - hb[q] * ss[q]).Magnitude; }
                nb[b] = nsum / (2 * half + 1);
            }
            var zs = new Complex[m];
            for (int b = 0; b < m; b++)
            {
                double den = hb[b].Magnitude * hb[b].Magnitude * pb[b] + nb[b];
                zs[b] = den > 0 ? Complex.Conjugate(hb[b]) * pb[b] / den * xs[b] : Complex.Zero;
            }
            return Fft.Inverse(zs);
        }

        /// <summary>
        /// A symbol-spaced FIR of <paramref name="taps"/> taps (odd), trained by least squares on the instants
        /// <paramref name="trainAt"/> of <paramref name="x"/> against <paramref name="trainIdeal"/> —
        /// (AᴴA + δI) c = Aᴴd, δ a whisper of the diagonal — and applied at <paramref name="applyAt"/>.
        /// </summary>
        private static Complex[] Fir(Complex[] x, int[] trainAt, Complex[] trainIdeal, int taps, int sps, int[] applyAt)
        {
            int m = x.Length, l = taps / 2;
            Complex X(int i) => x[(i % m + m) % m];
            var a = new Complex[taps, taps]; var rhs = new Complex[taps];
            for (int k = 0; k < trainAt.Length; k++)
            {
                var row = new Complex[taps];
                for (int j = 0; j < taps; j++) row[j] = X(trainAt[k] + (j - l) * sps);
                for (int j = 0; j < taps; j++)
                {
                    rhs[j] += Complex.Conjugate(row[j]) * trainIdeal[k];
                    for (int q = 0; q < taps; q++) a[j, q] += Complex.Conjugate(row[j]) * row[q];
                }
            }
            double tr = 0;
            for (int j = 0; j < taps; j++) tr += a[j, j].Real;
            for (int j = 0; j < taps; j++) a[j, j] += 1e-12 * tr / taps;
            var c = SolveComplex(a, rhs);
            return [.. applyAt.Select(i => Enumerable.Range(0, taps).Aggregate(Complex.Zero, (acc, j) => acc + c[j] * X(i + (j - l) * sps)))];
        }

        // ── a single carrier (brief PCH-5) ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The SC carrier <paramref name="w"/> equalized as <paramref name="s"/> says, read at its data symbols against their
        /// ideal points (EVM over the constellation's mean power). Pilot: the symbol-spaced FIR trained on the preamble and
        /// pilot blocks when the carrier has them, otherwise on the first Training data symbols, which are then not measured.
        /// </summary>
        public static Equalized Sc(ReceivedWaveform w, EqualizerSettings s)
        {
            if (w.Kind != QualityKind.Sc) throw new ArgumentException("only an SC carrier is equalized here", nameof(w));
            var order = Enumerable.Range(0, w.At.Length).OrderBy(i => w.At[i]).ToArray();
            var at = order.Select(i => w.At[i]).ToArray();
            var ideal = order.Select(i => w.Ideal![i]).ToArray();
            int m = w.X.Length;
            Complex X(int i) => w.X[(i % m + m) % m];
            EvmResult Score(Complex[] rx, Complex[] id) => SignalEvm.Score(w.Carrier.Spec, rx, id, rx.Length, 1, meanPower: 1);
            switch (s.Mode)
            {
                case EqualizerMode.None:
                    return SingleCarrier(EqualizerMode.None, Score([.. at.Select(X)], ideal), null) with { Instants = at };
                case EqualizerMode.Genie:
                {
                    var z = GenieMmse(w.X, w.S);
                    return SingleCarrier(EqualizerMode.Genie, Score([.. at.Select(i => z[i])], ideal), null) with { Instants = at };
                }
                default:
                {
                    int taps = Math.Max(1, s.Taps | 1), sps = ScQam.SamplesPerSymbol;
                    int[] trainAt; Complex[] trainId; int[] rest; Complex[] restId;
                    if (w.KnownAt is { Length: > 0 } known)
                    {
                        trainAt = known; trainId = w.KnownIdeal!; rest = at; restId = ideal;
                    }
                    else
                    {
                        int train = Math.Min(s.Training, at.Length - 1);
                        trainAt = at[..Math.Max(0, train)]; trainId = ideal[..Math.Max(0, train)];
                        rest = at[Math.Max(0, train)..]; restId = ideal[Math.Max(0, train)..];
                    }
                    if (trainAt.Length < taps)
                        return Sc(w, s with { Mode = EqualizerMode.None }) with
                        { Note = $"{trainAt.Length} training symbols are too few for a {taps}-tap FIR; measured with one gain instead" };
                    var z = Fir(w.X, trainAt, trainId, taps, sps, rest);
                    return SingleCarrier(EqualizerMode.Pilot, Score(z, restId), null) with { Instants = rest };
                }
            }
        }

        /// <summary>A single carrier's SINR, symbol by symbol: Es over the error power of the 65 symbols around it.</summary>
        private static Equalized SingleCarrier(EqualizerMode mode, EvmResult e, string? note)
        {
            int n = e.Received.Length;
            const int half = 32;
            var err = new double[n + 1];
            double es = 0;
            for (int i = 0; i < n; i++)
            {
                var d = e.Received[i] - e.Ideal[i];
                err[i + 1] = err[i] + d.Real * d.Real + d.Imaginary * d.Imaginary;
                es += e.Ideal[i].Real * e.Ideal[i].Real + e.Ideal[i].Imaginary * e.Ideal[i].Imaginary;
            }
            es /= Math.Max(1, n);
            var sinr = new double[n];
            for (int i = 0; i < n; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(n, i + half + 1);
                double p = (err[b] - err[a]) / (b - a);
                sinr[i] = p > 0 ? es / p : double.PositiveInfinity;
            }
            return new Equalized(mode, e.Received, e.Ideal, e.RmsPercent, e.PeakPercent, sinr, (double[])sinr.Clone(), n, 1, note);
        }

        private static Complex[] SolveComplex(Complex[,] a, Complex[] b)
        {
            int n = b.Length;
            var m = (Complex[,])a.Clone(); var x = (Complex[])b.Clone();
            for (int i = 0; i < n; i++)
            {
                int piv = i;
                for (int k = i + 1; k < n; k++) if (m[k, i].Magnitude > m[piv, i].Magnitude) piv = k;
                for (int k = 0; k < n; k++) (m[i, k], m[piv, k]) = (m[piv, k], m[i, k]);
                (x[i], x[piv]) = (x[piv], x[i]);
                if (m[i, i] == Complex.Zero) continue;
                for (int r = i + 1; r < n; r++)
                {
                    var f = m[r, i] / m[i, i];
                    for (int k = i; k < n; k++) m[r, k] -= f * m[i, k];
                    x[r] -= f * x[i];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                for (int k = i + 1; k < n; k++) x[i] -= m[i, k] * x[k];
                x[i] = m[i, i] == Complex.Zero ? Complex.Zero : x[i] / m[i, i];
            }
            return x;
        }
    }
}
