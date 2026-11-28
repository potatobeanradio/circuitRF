// ================================================================
//  Wlan.cs  —  an IEEE 802.11 OFDM packet and its EVM
//
//  One packet, then an idle gap, as a periodic record at Oversample x
//  the channel's base rate (20 MS/s per 20 MHz). The packet is unit
//  mean power over its own length (gated); the gap is zeros.
//
//  Formats (IEEE Std 802.11-2020 unless noted):
//    Legacy  11a/g, Clause 17: 20 MHz, 64-point, 312.5 kHz, 48 data +
//            4 pilots (±7, ±21), 4 µs symbols, GI 0.8 µs.
//    HT      11n, Clause 19: 20/40 MHz, 56 / 114 tones, GI 0.8/0.4 µs.
//    VHT     11ac, Clause 21: 20/40/80/160 MHz, up to 256QAM.
//    HE      11ax, IEEE Std 802.11ax-2021 Clause 27 (HE SU):
//            20-160 MHz, 78.125 kHz, 12.8 µs symbols (4x), RU 242 /
//            484 / 996 / 2x996, GI 0.8/1.6/3.2 µs, up to 1024QAM.
//    EHT     11be, Clause 36: the HE tone plan, plus 320 MHz as four
//            996-tone segments, up to 4096QAM.
//  Packet: L-STF, L-LTF, L-SIG (duplicated per 20 MHz with the tone
//  rotation γ of 21.3.7.5), then the format's own fields: HT-SIG x2,
//  HT-STF, HT-LTF | VHT-SIG-A x2, VHT-STF, VHT-LTF, VHT-SIG-B | RL-SIG,
//  HE-SIG-A x2, HE-STF, HE-LTF | RL-SIG, U-SIG x2, EHT-SIG, EHT-STF,
//  EHT-LTF; then the data symbols. One spatial stream, one LTF.
//
//  Simplified (spectrum, PAPR, CCDF and EVM do not depend on these):
//    - no coding, scrambling or interleaving: every data tone carries a
//      random point of the MCS's constellation from the seed;
//    - SIG fields carry random BPSK / QBPSK, not an encoded rate and
//      length; L-SIG has no extra ±27, ±28 tones;
//    - every STF is the L-STF pattern (0.8 µs period) per 20 MHz; the
//      HT/VHT/HE/EHT LTF is ±1 from the pilot polarity sequence on the
//      format's tone plan, not the standard's sequence; HE/EHT use one
//      4x LTF with the data GI;
//    - legacy pilots are exact (17.3.5.10); the others are the polarity
//      sequence p_n shifted by one per pilot, not the Ψ rotation;
//    - no packet extension, no 2x/1x LTF compression, no puncturing;
//      the 320 MHz γ is from Clause 36 as recalled, not checked.
//  Window: a raised-cosine overlap of WindowS (17.3.2.5's T_TR, 100 ns
//  default) inside each guard interval, so the FFT never sees it.
//
//  EVM (17.3.9.7, 19.3.18.7, 21.3.17.4, 802.11ax-2021 27.3.19.4):
//  the frequency offset from the L-LTF's two copies taken out, the
//  channel from the LTF (L-LTF's two copies averaged for Legacy),
//  each data symbol equalised, its common phase from the pilots taken
//  out, each data tone decided to the nearest point; rms error over
//  the constellation's mean power. Limits by MCS from Tables 17-18,
//  19-?, 21-37 and 27-49 (EHT -38 dB for 4096QAM).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;

namespace RfCore.Signals
{
    public enum WlanFormat { Legacy, Ht, Vht, He, Eht }

    /// <summary>A Wi-Fi packet that cannot be generated: a FormatException, as every carrier's refusal is.</summary>
    public sealed class WlanRefusal : FormatException
    {
        public Diagnostic Diagnostic { get; }
        public WlanRefusal(string id, string message)
            : base(message) => Diagnostic = Diagnostic.Create("signal.wlan." + id, DiagnosticSeverity.Error, message);
    }

    /// <summary>One 802.11 packet and the gap after it.</summary>
    /// <param name="BandwidthHz">Channel bandwidth: 20, 40, 80, 160 MHz (320 for EHT).</param>
    /// <param name="Mcs">MCS index: Legacy 0-7 = 6, 9, 12, 18, 24, 36, 48, 54 Mb/s; HT 0-7, VHT 0-9, HE 0-11, EHT 0-13.</param>
    /// <param name="GuardS">Data guard interval (s): 0.8 µs; 0.4 µs for HT/VHT; 0.8, 1.6, 3.2 µs for HE/EHT.</param>
    /// <param name="DataSymbols">Data OFDM symbols in the packet.</param>
    /// <param name="IdleS">The gap after the packet (s), rounded to whole base-rate samples; 16 µs is SIFS.</param>
    /// <param name="Oversample">The record's rate over the base rate (BW): 1 is native.</param>
    /// <param name="WindowS">The raised-cosine transition between symbols (s); 0 = rectangular.</param>
    public sealed record WlanSpec(WlanFormat Format = WlanFormat.Vht, double BandwidthHz = 20e6, int Mcs = 7, double GuardS = 0.8e-6,
                                  int DataSymbols = 20, double IdleS = 16e-6, int Seed = 1, int Oversample = 1, double WindowS = 100e-9);

    /// <summary>The data field's tone plan. Tones are subcarrier indices k (f = k·Δf), lowest first.</summary>
    public sealed record WlanNumerology(double SubcarrierSpacingHz, int Fft, int[] DataTones, int[] PilotTones)
    {
        public double BaseRateHz => SubcarrierSpacingHz * Fft;
        /// <summary>From the lowest to the highest occupied tone, one Δf wide each.</summary>
        public double OccupiedBwHz => (Math.Max(DataTones[^1], PilotTones[^1]) - Math.Min(DataTones[0], PilotTones[0]) + 1) * SubcarrierSpacingHz;
    }

    /// <summary>The transmit spectral mask: dBr against the maximum spectral density at each |offset|, linear in dB between
    /// points, 0 dBr inside the first, the last value beyond.</summary>
    public sealed record WlanMask(string Clause, double[] OffsetHz, double[] Dbr)
    {
        public double At(double offsetHz)
        {
            double f = Math.Abs(offsetHz);
            if (f <= OffsetHz[0]) return Dbr[0];
            for (int i = 1; i < OffsetHz.Length; i++)
                if (f <= OffsetHz[i]) return Dbr[i - 1] + (Dbr[i] - Dbr[i - 1]) * (f - OffsetHz[i - 1]) / (OffsetHz[i] - OffsetHz[i - 1]);
            return Dbr[^1];
        }
    }

    public static class Wlan
    {
        private enum Kind { Other, Ltf, Data }

        /// <summary>One OFDM symbol (or field) at the base rate: an N-point grid, a cyclic prefix, the body repeated.</summary>
        private sealed record Sym(Kind K, int N, int Cp, int Reps, Complex[] X, double Power);

        private static readonly int[] Bits = [1, 2, 2, 4, 4, 6, 6, 6, 8, 8, 10, 10, 12, 12];
        private static readonly int[] LegacyBits = [1, 1, 2, 2, 4, 4, 6, 6];
        private static readonly int[] EvmDb = [-5, -10, -13, -16, -19, -22, -25, -27, -30, -32, -35, -35, -38, -38];
        private static readonly int[] LegacyEvmDb = [-5, -8, -10, -13, -16, -19, -22, -25];

        /// <summary>The pilot polarity p_0..p_126 (17.3.5.10): the scrambler x^7 + x^4 + 1 from all ones, 0 -> +1.</summary>
        public static readonly int[] Polarity = [.. Fsk.Prbs(7, 4).Select(b => 1 - 2 * b)];

        private static readonly double[] Lltf =
            [1, 1, -1, -1, 1, 1, -1, 1, -1, 1, 1, 1, 1, 1, 1, -1, -1, 1, 1, -1, 1, -1, 1, 1, 1, 1, 0,
             1, -1, -1, 1, 1, -1, 1, -1, 1, -1, -1, -1, -1, -1, 1, 1, -1, -1, 1, -1, 1, -1, 1, 1, 1, 1];
        private static readonly int[] LstfSign = [1, -1, 1, -1, -1, 1, -1, -1, 1, 1, 1, 1];   // k = -24..24 step 4, no 0
        private static readonly int[] LegacyPilots = [-21, -7, 7, 21];
        private static readonly int[] LegacyPilotSign = [1, 1, 1, -1];

        public static int MaxMcs(WlanFormat f) => f switch { WlanFormat.Vht => 9, WlanFormat.He => 11, WlanFormat.Eht => 13, _ => 7 };

        public static int[] BandwidthsMHz(WlanFormat f) => f switch
        {
            WlanFormat.Legacy => [20],
            WlanFormat.Ht => [20, 40],
            WlanFormat.Eht => [20, 40, 80, 160, 320],
            _ => [20, 40, 80, 160],
        };

        public static double[] GuardsS(WlanFormat f) => f switch
        {
            WlanFormat.Legacy => [0.8e-6],
            WlanFormat.Ht or WlanFormat.Vht => [0.8e-6, 0.4e-6],
            _ => [0.8e-6, 1.6e-6, 3.2e-6],
        };

        public static int BitsPerTone(WlanSpec s) => (s.Format == WlanFormat.Legacy ? LegacyBits : Bits)[s.Mcs];

        /// <summary>The EVM limit (dB) of the MCS.</summary>
        public static double EvmLimitDb(WlanSpec s) => (s.Format == WlanFormat.Legacy ? LegacyEvmDb : EvmDb)[s.Mcs];

        public static WlanSpec Validate(WlanSpec s)
        {
            int bw = (int)Math.Round(s.BandwidthHz / 1e6);
            if (Math.Abs(s.BandwidthHz - bw * 1e6) > 1 || !BandwidthsMHz(s.Format).Contains(bw))
                throw new WlanRefusal("bandwidth", $"Wi-Fi {s.Format}: BW must be one of {string.Join(", ", BandwidthsMHz(s.Format))} MHz (got {s.BandwidthHz / 1e6:0.###} MHz)");
            if (s.Mcs < 0 || s.Mcs > MaxMcs(s.Format))
                throw new WlanRefusal("mcs", $"Wi-Fi {s.Format}: MCS must be 0 to {MaxMcs(s.Format)} (got {s.Mcs})");
            if (!GuardsS(s.Format).Any(g => Math.Abs(g - s.GuardS) < 1e-12))
                throw new WlanRefusal("guard", $"Wi-Fi {s.Format}: GI must be one of {string.Join(", ", GuardsS(s.Format).Select(g => $"{g * 1e6:0.0} µs"))} (got {s.GuardS * 1e6:0.###} µs)");
            if (s.DataSymbols < 1) throw new WlanRefusal("symbols", $"Wi-Fi: Symbols must be 1 or more (got {s.DataSymbols})");
            if (!(s.IdleS >= 0) || !double.IsFinite(s.IdleS)) throw new WlanRefusal("idle", $"Wi-Fi: Idle must be 0 s or more (got {s.IdleS})");
            if (s.Oversample < 1) throw new WlanRefusal("oversample", $"Wi-Fi: Oversample must be 1 or more (got {s.Oversample})");
            double minCp = Math.Min(0.8e-6, s.GuardS);
            if (!(s.WindowS >= 0) || s.WindowS > minCp)
                throw new WlanRefusal("window", $"Wi-Fi: Window must be 0 to {minCp * 1e9:0} ns, inside the shortest guard interval (got {s.WindowS * 1e9:0.#} ns)");
            if ((long)RecordSamples(s) > Fsk.MaxSamples)
                throw new WlanRefusal("too-long", $"Wi-Fi: the record would hold {RecordSamples(s):N0} samples, over the {Fsk.MaxSamples:N0} a record may hold; lower Symbols or Oversample");
            return s;
        }

        // ── the tone plans ───────────────────────────────────────────────────────────────────────────────

        private static int[] Band(int lo, int hi) => [.. Enumerable.Range(-hi, hi - lo + 1), .. Enumerable.Range(lo, hi - lo + 1)];
        private static int[] Pm(params int[] p) => [.. p.Select(v => -v).Reverse(), .. p];
        private static int[] Shift(int[] t, params int[] by) => [.. by.SelectMany(o => t.Select(k => k + o))];
        private static int Mod(int k, int n) => (k % n + n) % n;
        private static int SubChannels(WlanSpec s) => (int)Math.Round(s.BandwidthHz / 20e6);

        public static WlanNumerology Numerology(WlanSpec s)
        {
            int n = SubChannels(s);
            int[] he80 = Band(3, 500), he80p = Pm(24, 92, 158, 226, 266, 334, 400, 468);
            (int[] occ, int[] pil) = (s.Format, n) switch
            {
                (WlanFormat.Legacy, _) => (Band(1, 26), LegacyPilots),
                (WlanFormat.Ht or WlanFormat.Vht, 1) => (Band(1, 28), Pm(7, 21)),
                (WlanFormat.Ht or WlanFormat.Vht, 2) => (Band(2, 58), Pm(11, 25, 53)),
                (WlanFormat.Vht, 4) => (Band(2, 122), Pm(11, 39, 75, 103)),
                (WlanFormat.Vht, _) => (Shift(Band(2, 122), -128, 128), Shift(Pm(11, 39, 75, 103), -128, 128)),
                (_, 1) => (Band(2, 122), Pm(22, 48, 90, 116)),
                (_, 2) => (Band(3, 244), Pm(10, 36, 78, 104, 144, 170, 212, 238)),
                (_, 4) => (he80, he80p),
                (_, 8) => (Shift(he80, -512, 512), Shift(he80p, -512, 512)),
                _ => (Shift(he80, -1536, -512, 512, 1536), Shift(he80p, -1536, -512, 512, 1536)),
            };
            int fft = (s.Format is WlanFormat.He or WlanFormat.Eht ? 256 : 64) * n;
            return new WlanNumerology(s.BandwidthHz / fft, fft, [.. occ.Except(pil)], pil);
        }

        /// <summary>The tone rotation γ of the 20 MHz subchannel holding <paramref name="fHz"/> (21.3.7.5; Clause 36 for 320 MHz).</summary>
        private static Complex Gamma(WlanSpec s, double fHz)
        {
            int n = SubChannels(s), i = Math.Clamp((int)Math.Floor((fHz + s.BandwidthHz / 2) / 20e6), 0, n - 1);
            if (n == 2) return i == 0 ? 1 : Complex.ImaginaryOne;
            int[] g = n switch
            {
                1 => [1], 4 => [1, -1, -1, -1], 8 => [1, -1, -1, -1, 1, -1, -1, -1],
                _ => [1, -1, -1, -1, 1, -1, -1, -1, -1, 1, 1, 1, -1, 1, 1, 1],
            };
            return g[i];
        }

        // ── the packet ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>A legacy-numerology field: <paramref name="tone20"/> (k = -32..31 of one 20 MHz) in every subchannel, rotated.</summary>
        private static Sym Legacy(WlanSpec s, int cp, int reps, Func<int, Complex> tone20)
        {
            int n = SubChannels(s), N = 64 * n;
            var x = new Complex[N];
            for (int i = 0; i < n; i++)
                for (int k = -32; k < 32; k++)
                {
                    int kk = k + 64 * i - 32 * (n - 1);
                    x[Mod(kk, N)] = tone20(k) * Gamma(s, kk * 312.5e3);
                }
            return new Sym(Kind.Other, N, cp * N / 64, reps, x, x.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary));
        }

        private static Complex Stf(int k) => k != 0 && k % 4 == 0 && Math.Abs(k) <= 24
            ? LstfSign[(k + 24) / 4 - (k > 0 ? 1 : 0)] * new Complex(1, 1) * Math.Sqrt(13.0 / 6) : 0;

        /// <summary>A legacy SIG symbol: random BPSK (QBPSK when <paramref name="q"/>) on the 48 data tones, pilots p_z.</summary>
        private static Sym Sig(WlanSpec s, Random rng, int z, bool q)
        {
            var v = new Complex[53];
            for (int k = -26; k <= 26; k++)
            {
                int pi = Array.IndexOf(LegacyPilots, k);
                v[k + 26] = k == 0 ? 0 : pi >= 0 ? LegacyPilotSign[pi] * Polarity[z % 127] : (1 - 2 * rng.Next(2)) * (q ? Complex.ImaginaryOne : 1);
            }
            return Legacy(s, 16, 1, k => Math.Abs(k) <= 26 ? v[k + 26] : 0);
        }

        /// <summary>The pilot of index <paramref name="i"/> on the symbol counted <paramref name="z"/> (L-SIG = 0).</summary>
        private static int Pilot(WlanSpec s, int i, int z)
            => s.Format == WlanFormat.Legacy ? LegacyPilotSign[i] * Polarity[z % 127] : Polarity[(z + i) % 127];

        private static (int LevelsI, int LevelsQ, double Norm) Grid(int bits)
        {
            int li = 1 << ((bits + 1) / 2), lq = 1 << (bits / 2);
            return (li, lq, 1 / Math.Sqrt((li * li - 1) / 3.0 + (lq * lq - 1) / 3.0));
        }

        /// <summary>A field on the format's own tone plan: data tones from <paramref name="data"/>, pilots for counter z.</summary>
        private static Sym Plan(WlanSpec s, WlanNumerology nm, Kind kind, int cp, int z, Func<Complex> data)
        {
            var x = new Complex[nm.Fft];
            Complex G(int k) => Gamma(s, k * nm.SubcarrierSpacingHz);
            if (kind == Kind.Ltf)
            {
                int j = 0;
                foreach (int k in nm.DataTones.Concat(nm.PilotTones).Order()) x[Mod(k, nm.Fft)] = Polarity[j++ % 127] * G(k);
            }
            else
            {
                foreach (int k in nm.DataTones) x[Mod(k, nm.Fft)] = data() * G(k);
                for (int i = 0; i < nm.PilotTones.Length; i++) x[Mod(nm.PilotTones[i], nm.Fft)] = Pilot(s, i, z) * G(nm.PilotTones[i]);
            }
            return new Sym(kind, nm.Fft, cp, 1, x, nm.DataTones.Length + nm.PilotTones.Length);
        }

        /// <summary>The packet's fields in order, from the seed: the generator's and the EVM receiver's one description.</summary>
        private static List<Sym> Layout(WlanSpec s)
        {
            var nm = Numerology(s);
            var rng = new Random(s.Seed);
            int fft = nm.Fft, gi = (int)Math.Round(s.GuardS * s.BandwidthHz), z = 0;
            var f = new List<Sym>
            {
                Legacy(s, 32, 2, Stf),
                Legacy(s, 32, 2, k => Math.Abs(k) <= 26 ? Lltf[k + 26] : 0) with { K = s.Format == WlanFormat.Legacy ? Kind.Ltf : Kind.Other },
            };
            var lsig = Sig(s, rng, z++, false);
            f.Add(lsig);
            Sym Stf4() => Legacy(s, 16, 1, Stf);
            Sym Ltf(int cp) => Plan(s, nm, Kind.Ltf, cp, 0, () => 0);
            switch (s.Format)
            {
                case WlanFormat.Ht:
                    f.AddRange([Sig(s, rng, z++, true), Sig(s, rng, z++, true), Stf4(), Ltf(fft / 4)]);
                    break;
                case WlanFormat.Vht:
                    f.AddRange([Sig(s, rng, z++, false), Sig(s, rng, z++, true), Stf4(), Ltf(fft / 4)]);
                    f.Add(Plan(s, nm, Kind.Other, fft / 4, z++, () => 1 - 2 * rng.Next(2)));
                    break;
                case WlanFormat.He:
                    f.AddRange([lsig, Sig(s, rng, z++, false), Sig(s, rng, z++, false), Stf4(), Ltf(gi)]);
                    break;
                case WlanFormat.Eht:
                    f.AddRange([lsig, Sig(s, rng, z++, false), Sig(s, rng, z++, false), Sig(s, rng, z++, false), Stf4(), Ltf(gi)]);
                    break;
            }
            var (li, lq, norm) = Grid(BitsPerTone(s));
            Complex Point() => new Complex((2 * rng.Next(li) - (li - 1)) * norm, (2 * rng.Next(lq) - (lq - 1)) * norm);
            for (int d = 0; d < s.DataSymbols; d++) f.Add(Plan(s, nm, Kind.Data, gi, z++, Point));
            return f;
        }

        private static int Idle(WlanSpec s) => (int)Math.Round(s.IdleS * s.BandwidthHz) * s.Oversample;

        public static double SampleRateHz(WlanSpec s) => s.BandwidthHz * s.Oversample;

        public static int PacketSamples(WlanSpec s) => Layout(s).Sum(q => q.Cp + q.N * q.Reps) * s.Oversample;

        public static int RecordSamples(WlanSpec s) => PacketSamples(s) + Idle(s);

        public static double RecordSeconds(WlanSpec s) => RecordSamples(s) / SampleRateHz(s);

        /// <summary>The packet's share of the record: the gated mean power over the record's is 1 / duty.</summary>
        public static double DutyCycle(WlanSpec s) => (double)PacketSamples(s) / RecordSamples(s);

        /// <summary>The record at <see cref="SampleRateHz"/>: the packet from sample 0 at unit mean power over
        /// <see cref="PacketSamples"/>, then the idle gap (zeros, but for the last symbol's window tail).</summary>
        public static Complex[] Generate(WlanSpec s)
        {
            Validate(s);
            var layout = Layout(s);
            int os = s.Oversample, packet = layout.Sum(q => q.Cp + q.N * q.Reps) * os, total = packet + Idle(s);
            int w = (int)Math.Round(s.WindowS * SampleRateHz(s));
            var ramp = new double[w];
            for (int i = 0; i < w; i++) ramp[i] = 0.5 * (1 - Math.Cos(Math.PI * (i + 0.5) / w));
            var rec = new Complex[total];
            int pos = 0;
            foreach (var q in layout)
            {
                int m = q.N * os, cp = q.Cp * os, len = cp + m * q.Reps;
                var y = new Complex[m];
                for (int k = -q.N / 2; k < q.N / 2; k++) y[Mod(k, m)] = q.X[Mod(k, q.N)];
                Fft.Transform(y, inverse: true);
                double sc = 1 / Math.Sqrt(q.Power);
                for (int i = 0; i < len + w; i++)
                {
                    double wt = i < w ? ramp[i] : i >= len ? ramp[w - 1 - (i - len)] : 1;
                    rec[(pos + i) % total] += y[Mod(i - cp, m)] * (sc * wt);
                }
                pos += len;
            }
            double p = 0;
            for (int i = 0; i < packet; i++) p += rec[i].Real * rec[i].Real + rec[i].Imaginary * rec[i].Imaginary;
            double a = 1 / Math.Sqrt(p / packet);
            for (int i = 0; i < total; i++) rec[i] *= a;
            return rec;
        }

        // ── the EVM ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// EVM of the data field of <paramref name="record"/>: a record at <see cref="SampleRateHz"/> whose packet starts at
        /// sample 0 (any scale). A frequency offset (up to ±156 kHz) comes from the L-LTF, the channel from the LTF, the
        /// common phase of each symbol from its pilots, and every data tone is decided to its nearest constellation point. <see cref="EvmResult.Ideal"/> holds those decisions.
        /// </summary>
        public static EvmResult Measure(WlanSpec s, ReadOnlySpan<Complex> record)
        {
            Validate(s);
            var x = record.ToArray();
            var nm = Numerology(s);
            var layout = Layout(s);
            // the frequency offset from the L-LTF's two copies (one 3.2 µs apart), taken out of the whole record
            int n0 = (layout[0].Cp + 2 * layout[0].N + layout[1].Cp) * s.Oversample, lag = layout[1].N * s.Oversample;
            Complex acc = 0;
            for (int i = 0; i < lag; i++) acc += x[(n0 + i + lag) % x.Length] * Complex.Conjugate(x[(n0 + i) % x.Length]);
            double dphi = acc.Phase / lag;
            for (int i = 0; i < x.Length; i++) x[i] *= Complex.FromPolarCoordinates(1, -dphi * i);
            int os = s.Oversample, pos = 0;
            var h = new Complex[nm.Fft];
            var tones = nm.DataTones.Concat(nm.PilotTones).ToArray();
            var (li, lq, norm) = Grid(BitsPerTone(s));
            var rx = new List<Complex>();
            var id = new List<Complex>();
            double err = 0, peak = 0;
            Complex[] Spectrum(int at, int n)
            {
                var b = new Complex[n * os];
                for (int i = 0; i < b.Length; i++) b[i] = x[(at + i) % x.Length];
                Fft.Transform(b, inverse: false);
                return b;
            }
            Complex Bin(Complex[] b, int k) => b[Mod(k, b.Length)];
            static double Slice(double v, int levels, double norm)
                => (2 * Math.Clamp(Math.Round((v / norm + levels - 1) / 2), 0, levels - 1) - (levels - 1)) * norm;
            foreach (var q in layout)
            {
                int cp = q.Cp * os, m = q.N * os;
                if (q.K == Kind.Ltf)
                    for (int r = 0; r < q.Reps; r++)
                    {
                        var b = Spectrum(pos + cp + r * m, q.N);
                        foreach (int k in tones) h[Mod(k, nm.Fft)] += Bin(b, k) / q.X[Mod(k, q.N)] / q.Reps;
                    }
                else if (q.K == Kind.Data)
                {
                    var b = Spectrum(pos + cp, q.N);
                    Complex c = 0;
                    foreach (int k in nm.PilotTones) c += Bin(b, k) / h[Mod(k, q.N)] * Complex.Conjugate(q.X[Mod(k, q.N)]);
                    var rot = Complex.FromPolarCoordinates(1, -c.Phase);
                    foreach (int k in nm.DataTones)
                    {
                        // the received point, rotation γ and common phase taken out, against its decision
                        var g = Gamma(s, k * nm.SubcarrierSpacingHz);
                        var v = Bin(b, k) / h[Mod(k, q.N)] * rot / g;
                        var d = new Complex(Slice(v.Real, li, norm), Slice(v.Imaginary, lq, norm));
                        double e = (v - d).Magnitude;
                        err += e * e; peak = Math.Max(peak, e);
                        rx.Add(v); id.Add(d);
                    }
                }
                pos += cp + m * q.Reps;
            }
            int n = rx.Count;
            return new EvmResult(100 * Math.Sqrt(err / n), 100 * peak, 100 * Math.Pow(10, EvmLimitDb(s) / 20),
                                 [.. rx], [.. id], s.DataSymbols, nm.DataTones.Length);
        }

        // ── the spectral mask ────────────────────────────────────────────────────────────────────────────

        public static WlanMask Mask(WlanSpec s)
        {
            double b = s.BandwidthHz, h = b / 2;
            (string clause, double inner, double outer) = s.Format switch
            {
                WlanFormat.Legacy => ("IEEE Std 802.11-2020, 17.3.9.3", h - 1e6, h + 1e6),
                WlanFormat.Ht => ("IEEE Std 802.11-2020, 19.3.18.1 (5 GHz; -45 dBr floor at 2.4 GHz)", h - 1e6, h + 1e6),
                WlanFormat.Vht => ("IEEE Std 802.11-2020, 21.3.17.1", h - 1e6, h + 1e6),
                WlanFormat.He => ("IEEE Std 802.11ax-2021, 27.3.19.1", h - (b == 20e6 ? 0.25e6 : 0.5e6), h + 0.5e6),
                _ => ("IEEE Std 802.11be-2024, Clause 36 (the HE mask scaled)", h - (b == 20e6 ? 0.25e6 : 0.5e6), h + 0.5e6),
            };
            return new WlanMask(clause, [inner, outer, b, 1.5 * b], [0, -20, -28, -40]);
        }
    }
}
