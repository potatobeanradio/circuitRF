// ================================================================
//  Dsss.cs  —  direct-sequence spread spectrum: WCDMA test models and
//  uplink, IEEE 802.11b (Barker and CCK) and IEEE 802.15.4 O-QPSK chips
//
//  Common. Chips are shaped by a root-raised cosine applied in the
//  frequency domain over the whole periodic record (as the SC carrier
//  is), at a whole number of samples a chip; the receiver applies the
//  same filter, so a clean record reads back every chip exactly. The
//  quality is measured at the chip instants after that matched filter
//  against the chips sent, one complex gain fitted (the window is taken
//  aligned to the record: the composer's delay fit does that).
//
//  WCDMA downlink (3GPP TS 25.141 V19.0.0 §6.1.1, TS 25.213 V19.0.0).
//  3.84 Mcps, RRC α = 0.22, one 10 ms frame (38,400 chips). Each
//  channel: QPSK (16QAM / 64QAM for HS-PDSCH) symbols, spread by its
//  OVSF code C(SF, k), delayed by its timing offset (x 256 chips),
//  summed and scrambled by the primary scrambling code (the 18-bit Gold
//  construction of TS 25.213 §5.2.2, code number 16 k). P-SCH and S-SCH
//  (TS 25.213 §5.2.3; the S-SCH sequence of scrambling code group 0)
//  are added unscrambled in the first 256 chips of each slot, each at
//  half the P-CCPCH+SCH level; P-CCPCH is off there. Channel contents
//  per TS 25.141 §6.1.1.5-6.1.1.9: the DPCHs in slot format 10 (SF 128)
//  or 6 (SF 256) with the pilot and TPC patterns of Tables 6.7-6.8 and
//  PN9 data seeded from the code; P-CCPCH, S-CCPCH (slot format 0),
//  HS-SCCH and HS-PDSCH PN9 seeded as the clause says; PICH the 18
//  paging indicators, its last 12 bits off. The Home BS variants (4 and
//  8 DPCH) are not made. The 16QAM and 64QAM maps are per-axis Gray at
//  unit mean power (RF-equivalent to TS 25.213's; the label order
//  differs). Measured: composite EVM, code-domain power per channel
//  (descrambled, despread, over the channel's on symbols, relative to
//  the total power) and the peak code-domain error (the error projected
//  onto every SF 256 code).
//
//  WCDMA uplink (TS 25.213 §4.2.1.1, §4.3.2.2): the 12.2 kb/s
//  reference channel, DPDCH (SF 64, code 16, βd = 15/15) on I, DPCCH
//  (SF 256, code 0, βc = 8/15) on Q, PN9 bits on both, scrambled by the
//  long code n with its HPSK form C1(i)(1 + j(-1)^i C2(2⌊i/2⌋)).
//
//  IEEE 802.11b (IEEE Std 802.11-2020 Clauses 15 and 16; checked against
//  the HR/DSSS text of IEEE Std 802.11b-1999 clause 18, which Clause 16
//  carries on: the SFD, SIGNAL values, SERVICE bits, LENGTH and its
//  extension bit (Table 98), the CRC-16 worked example, the scrambler
//  and its long-preamble seed Z1..Z7 = 1101100, the DQPSK and CCK
//  tables 107-110, the transmit mask): 11 Mchip/s;
//  the long preamble (128 scrambled 1s and the SFD F3A0h) and the PLCP
//  header (SIGNAL, SERVICE, LENGTH, CRC-16) at 1 Mb/s DBPSK on the
//  11-chip Barker code, then the PSDU (PN9) at 1 Mb/s DBPSK, 2 Mb/s
//  DQPSK (Barker) or 5.5 / 11 Mb/s CCK (8 chips a symbol, the odd
//  symbols turned by π). Everything after the SFD's first bit goes
//  through the scrambler z^-7 + z^-4 + 1 (the first bit too: the seed
//  makes the SYNC field). The standard sets a transmit
//  mask, not a filter: here RRC α = 0.5 (the band ends at ±8.25 MHz).
//  Frames follow back to back, with no gap.
//
//  IEEE 802.15.4 2.4 GHz O-QPSK (IEEE Std 802.15.4-2020, the 2450 MHz
//  O-QPSK PHY; the 16 chip sequences, the SFD and the bit order checked
//  against IEEE Std 802.15.4-2003 Table 20 and Figure 17 — that later
//  editions keep them unchanged is unverified): each 4-bit symbol (low
//  nibble first) is one of 16 32-chip
//  sequences, 2 Mchip/s, even chips on I and odd chips on Q half a chip
//  later, each a half-sine pulse two chips long: MSK, constant envelope.
//  The PPDU: preamble (8 zero symbols), SFD A7h, PHR (length) and the
//  PSDU (PN9). The pulse is made here; when the SC carrier gains its
//  half-sine / O-QPSK shaping, this can use that instead.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>The shared chip machinery: OVSF codes, PN9, RRC chip shaping and its matched filter, chip-level EVM.</summary>
    public static class Dsss
    {
        /// <summary>The OVSF code C(SF, k), ±1: the tree C(2n,2k) = [C(n,k), C(n,k)], C(2n,2k+1) = [C(n,k), -C(n,k)].</summary>
        public static int[] Ovsf(int sf, int k)
        {
            var c = new[] { 1 };
            for (int bit = sf >> 1; bit >= 1; bit >>= 1)
                c = (k & bit) == 0 ? [.. c, .. c] : [.. c, .. c.Select(v => -v)];
            return c;
        }

        /// <summary>A PN9 sequence (x^9 + x^5 + 1) whose first 9 bits are <paramref name="seed"/>, <paramref name="n"/> bits.</summary>
        public static byte[] Pn9(IReadOnlyList<int> seed, int n)
        {
            var b = new byte[Math.Max(n, 9)];
            for (int i = 0; i < 9; i++) b[i] = (byte)seed[i];
            for (int i = 9; i < b.Length; i++) b[i] = (byte)(b[i - 9] ^ b[i - 5]);
            return b[..n];
        }

        /// <summary>The bits of <paramref name="v"/>, least significant first, then <paramref name="ones"/> 1s: a PN9 seed.</summary>
        public static int[] Seed(int v, int bits, int ones) => [.. Enumerable.Range(0, bits).Select(i => (v >> i) & 1), .. Enumerable.Repeat(1, ones)];

        /// <summary>The chips as a periodic record at <paramref name="spc"/> samples a chip, RRC-shaped (roll-off
        /// <paramref name="alpha"/>; a clean matched filter reads each chip back exactly).</summary>
        public static Complex[] Shape(IReadOnlyList<Complex> chips, int spc, double alpha)
        {
            var x = new Complex[chips.Count * spc];
            for (int k = 0; k < chips.Count; k++) x[k * spc] = chips[k] * spc;
            return Rrc(x, spc, alpha);
        }

        /// <summary>The matched filter, then the value at each chip instant.</summary>
        public static Complex[] Matched(ReadOnlySpan<Complex> x, int spc, double alpha)
        {
            var y = Rrc(x.ToArray(), spc, alpha);
            return [.. Enumerable.Range(0, y.Length / spc).Select(k => y[k * spc])];
        }

        private static Complex[] Rrc(Complex[] x, int spc, double alpha)
        {
            var X = Fft.Forward(x);
            int n = X.Length;
            for (int i = 0; i < n; i++) X[i] *= ScQam.RootRaisedCosine((i <= n / 2 ? i : i - n) * (double)spc / n, 1, alpha);
            return Fft.Inverse(X);
        }

        /// <summary>The complex gain from <paramref name="sent"/> to <paramref name="rx"/> (least squares).</summary>
        public static Complex Gain(IReadOnlyList<Complex> rx, IReadOnlyList<Complex> sent)
        {
            Complex num = 0; double den = 0;
            for (int i = 0; i < sent.Count; i++) { num += rx[i] * Complex.Conjugate(sent[i]); den += sent[i].Magnitude * sent[i].Magnitude; }
            return num / den;
        }

        /// <summary>RMS and peak EVM (%) of <paramref name="rx"/> against <paramref name="sent"/> once the gain is out,
        /// the RMS over the mean power sent, the peak over its RMS level.</summary>
        public static (double RmsPercent, double PeakPercent) Evm(IReadOnlyList<Complex> rx, IReadOnlyList<Complex> sent)
        {
            var g = Gain(rx, sent);
            double e = 0, p = 0, peak = 0;
            for (int i = 0; i < sent.Count; i++)
            {
                double d = (rx[i] / g - sent[i]).Magnitude;
                e += d * d; p += sent[i].Magnitude * sent[i].Magnitude; peak = Math.Max(peak, d);
            }
            return (100 * Math.Sqrt(e / p), 100 * peak / Math.Sqrt(p / sent.Count));
        }

        /// <summary>The fast Walsh-Hadamard transform in place (length a power of two): the sums against every Walsh row.</summary>
        public static void Fwht(Complex[] a)
        {
            for (int h = 1; h < a.Length; h <<= 1)
                for (int i = 0; i < a.Length; i += 2 * h)
                    for (int j = i; j < i + h; j++) (a[j], a[j + h]) = (a[j] + a[j + h], a[j] - a[j + h]);
        }
    }

    // =================================================================== WCDMA

    public enum WcdmaModel { Tm1x64, Tm1x32, Tm1x16, Tm2, Tm3x32, Tm3x16, Tm4, Tm5x8, Tm5x4, Tm5x2, Tm6, Uplink12k2 }

    public enum WcdmaChannelKind { Cpich, Pccpch, Pich, Sccpch, Dpch, HsScch, HsPdsch16Qam, HsPdsch64Qam }

    /// <summary>One downlink channel: kind, spreading factor, OVSF code, level (dB, against the maximum output) and timing
    /// offset (x 256 chips).</summary>
    public sealed record WcdmaChannel(WcdmaChannelKind Kind, int Sf, int Code, double LevelDb, int OffsetX256)
    {
        public string Name => $"{Kind} SF{Sf}/{Code}";
    }

    /// <summary>A channel's code-domain power, read (dB against the total power) beside the level it was set to.</summary>
    public sealed record WcdmaCodePower(WcdmaChannel Channel, double PowerDb);

    /// <summary>A WCDMA record's quality: composite EVM (%), peak code-domain error (dB) and each channel's code-domain
    /// power (the downlink only).</summary>
    public sealed record WcdmaQuality(double EvmPercent, double PeakCdeDb, WcdmaCodePower[] Channels);

    public static class Wcdma
    {
        public const double ChipRateHz = 3.84e6, RollOff = 0.22, ChannelBandwidthHz = 5e6;
        public const int FrameChips = 38400, SlotChips = 2560, SamplesPerChip = 8;
        public static double NativeRateHz => SamplesPerChip * ChipRateHz;
        public static double RecordSeconds => 10e-3;

        /// <summary>A WCDMA carrier's test model and scrambling code (TM1 64 DPCH and code 0 unstated).</summary>
        public static (WcdmaModel Model, int Code) Resolve(CarrierSpec c)
        {
            int code = c.ScramblingCode ?? 0;
            if (code is < 0 or > 7) throw new FormatException($"WCDMA carrier: the scrambling code must be 0 to 7 (group 0; got {code})");
            return (c.WcdmaModel ?? WcdmaModel.Tm1x64, code);
        }

        /// <summary>A model as a label names it: TM1x64, ..., Uplink12k2.</summary>
        public static string ModelName(WcdmaModel m) => m.ToString().Replace("Tm", "TM", StringComparison.Ordinal);

        public static WcdmaModel ParseModel(string v)
        {
            foreach (var m in Enum.GetValues<WcdmaModel>())
                if (m.ToString().Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return m;
            throw new FormatException($"WCDMA carrier: Model must be one of {string.Join(", ", Enum.GetValues<WcdmaModel>().Select(ModelName))} (got '{v}')");
        }

        // TS 25.141 Table 6.2 (TM1 DPCH: code, offset, level with 16 / 32 / 64 DPCH; 0 = not in that variant)
        private static readonly (int Code, int Off, int L16, int L32, int L64)[] Tm1Dpch =
        [
            (2, 86, -10, -13, -16), (11, 134, -12, -13, -16), (17, 52, -12, -14, -16), (23, 45, -14, -15, -17), (31, 143, -11, -17, -18),
            (38, 112, -13, -14, -20), (47, 59, -17, -16, -16), (55, 23, -16, -18, -17), (62, 1, -13, -16, -16), (69, 88, -15, -19, -19),
            (78, 30, -14, -17, -22), (85, 18, -18, -15, -20), (94, 30, -19, -17, -16), (102, 61, -17, -22, -17), (113, 128, -15, -20, -19),
            (119, 143, -9, -24, -21), (7, 83, 0, -20, -19), (13, 25, 0, -18, -21), (20, 103, 0, -14, -18), (27, 97, 0, -14, -20),
            (35, 56, 0, -16, -24), (41, 104, 0, -19, -24), (51, 51, 0, -18, -22), (58, 26, 0, -17, -21), (64, 137, 0, -22, -18),
            (74, 65, 0, -19, -20), (82, 37, 0, -19, -17), (88, 125, 0, -16, -18), (97, 149, 0, -18, -19), (108, 123, 0, -15, -23),
            (117, 83, 0, -17, -22), (125, 5, 0, -12, -21), (4, 91, 0, 0, -17), (9, 7, 0, 0, -18), (12, 32, 0, 0, -20), (14, 21, 0, 0, -17),
            (19, 29, 0, 0, -19), (22, 59, 0, 0, -21), (26, 22, 0, 0, -19), (28, 138, 0, 0, -23), (34, 31, 0, 0, -22), (36, 17, 0, 0, -19),
            (40, 9, 0, 0, -24), (44, 69, 0, 0, -23), (49, 49, 0, 0, -22), (53, 20, 0, 0, -19), (56, 57, 0, 0, -22), (61, 121, 0, 0, -21),
            (63, 127, 0, 0, -18), (66, 114, 0, 0, -19), (71, 100, 0, 0, -22), (76, 76, 0, 0, -21), (80, 141, 0, 0, -19), (84, 82, 0, 0, -21),
            (87, 64, 0, 0, -19), (91, 149, 0, 0, -21), (95, 87, 0, 0, -20), (99, 98, 0, 0, -25), (105, 46, 0, 0, -25), (110, 37, 0, 0, -25),
            (116, 87, 0, 0, -24), (118, 149, 0, 0, -22), (122, 85, 0, 0, -20), (126, 69, 0, 0, -15),
        ];

        // Table 6.5 (TM3 DPCH, SF 256: code, offset, level with 16 / 32 DPCH)
        private static readonly (int Code, int Off, int L16, int L32)[] Tm3Dpch =
        [
            (64, 86, -14, -16), (69, 134, -14, -16), (74, 52, -14, -16), (78, 45, -14, -16), (83, 143, -14, -16), (89, 112, -14, -16),
            (93, 59, -14, -16), (96, 23, -14, -16), (100, 1, -14, -16), (105, 88, -14, -16), (109, 30, -14, -16), (111, 18, -14, -16),
            (115, 30, -14, -16), (118, 61, -14, -16), (122, 128, -14, -16), (125, 143, -14, -16), (67, 83, 0, -16), (71, 25, 0, -16),
            (76, 103, 0, -16), (81, 97, 0, -16), (86, 56, 0, -16), (90, 104, 0, -16), (95, 51, 0, -16), (98, 26, 0, -16), (103, 137, 0, -16),
            (108, 65, 0, -16), (110, 37, 0, -16), (112, 125, 0, -16), (117, 149, 0, -16), (119, 123, 0, -16), (123, 83, 0, -16), (126, 5, 0, -16),
        ];

        // Tables 6.6B and 6.6F (TM5 DPCH with 30 / 14 / 6 codes, TM6 with 30: code, offset, levels)
        private static readonly (int Code, int Off, int L30, int L14, int L6, int Tm6)[] Tm56Dpch =
        [
            (15, 86, -20, -17, -17, -17), (23, 134, -20, -19, -15, -17), (68, 52, -21, -19, -15, -18), (76, 45, -22, -20, -18, -19),
            (82, 143, -24, -18, -16, -21), (90, 112, -21, -20, -17, -18), (5, 59, -23, -25, 0, -20), (11, 23, -25, -23, 0, -22),
            (17, 1, -23, -20, 0, -20), (27, 88, -26, -22, 0, -23), (64, 30, -24, -21, 0, -21), (72, 18, -22, -22, 0, -19),
            (86, 30, -24, -19, 0, -21), (94, 61, -28, -20, 0, -25), (3, 128, -27, 0, 0, -24), (7, 143, -26, 0, 0, -23), (13, 83, -27, 0, 0, -24),
            (19, 25, -25, 0, 0, -22), (21, 103, -21, 0, 0, -18), (25, 97, -21, 0, 0, -18), (31, 56, -23, 0, 0, -20), (66, 104, -26, 0, 0, -23),
            (70, 51, -25, 0, 0, -22), (74, 26, -24, 0, 0, -21), (78, 137, -27, 0, 0, -24), (80, 65, -26, 0, 0, -23), (84, 37, -23, 0, 0, -22),
            (88, 125, -25, 0, 0, -22), (89, 149, -22, 0, 0, -22), (92, 123, -24, 0, 0, -21),
        ];

        private static WcdmaChannel[] Common(double pccpch, double cpich, double pich, double sccpch) =>
        [
            new(WcdmaChannelKind.Pccpch, 256, 1, pccpch, 0), new(WcdmaChannelKind.Cpich, 256, 0, cpich, 0),
            new(WcdmaChannelKind.Pich, 256, 16, pich, 120), new(WcdmaChannelKind.Sccpch, 256, 3, sccpch, 0),
        ];

        private static IEnumerable<WcdmaChannel> Dpch(int sf, IEnumerable<(int Code, int Off, int L)> rows)
            => rows.Where(r => r.L != 0).Select(r => new WcdmaChannel(WcdmaChannelKind.Dpch, sf, r.Code, r.L, r.Off));

        private static IEnumerable<WcdmaChannel> Hs(int count, double scchLow, double pdsch, WcdmaChannelKind kind)
        {
            yield return new(WcdmaChannelKind.HsScch, 128, 9, -15, 0);
            yield return new(WcdmaChannelKind.HsScch, 128, 29, scchLow, 0);
            int[] codes = count switch { 8 => [4, 5, 6, 7, 12, 13, 14, 15], 4 => [4, 5, 12, 13], _ => [4, 12] };
            foreach (int c in codes) yield return new(kind, 16, c, pdsch, 0);
        }

        /// <summary>The downlink test model's channels (TS 25.141 Tables 6.1-6.6H); empty for the uplink.</summary>
        public static WcdmaChannel[] Channels(WcdmaModel m) => m switch
        {
            WcdmaModel.Tm1x64 => [.. Common(-10, -10, -18, -18), .. Dpch(128, Tm1Dpch.Select(r => (r.Code, r.Off, r.L64)))],
            WcdmaModel.Tm1x32 => [.. Common(-10, -10, -18, -18), .. Dpch(128, Tm1Dpch.Select(r => (r.Code, r.Off, r.L32)))],
            WcdmaModel.Tm1x16 => [.. Common(-10, -10, -18, -18), .. Dpch(128, Tm1Dpch.Select(r => (r.Code, r.Off, r.L16)))],
            WcdmaModel.Tm2 => [.. Common(-10, -10, -13, -13), .. Dpch(128, [(24, 1, -10), (72, 7, -10), (120, 2, -3)])],
            WcdmaModel.Tm3x32 => [.. Common(-11, -11, -18, -18), .. Dpch(256, Tm3Dpch.Select(r => (r.Code, r.Off, r.L32)))],
            WcdmaModel.Tm3x16 => [.. Common(-9, -9, -13, -13), .. Dpch(256, Tm3Dpch.Select(r => (r.Code, r.Off, r.L16)))],
            // TM4 at X = 0 with the optional P-CPICH enabled: both at -X - 3
            WcdmaModel.Tm4 => [new(WcdmaChannelKind.Pccpch, 256, 1, -3, 0), new(WcdmaChannelKind.Cpich, 256, 0, -3, 0)],
            WcdmaModel.Tm5x8 => [.. Common(-11, -11, -19, -19), .. Dpch(128, Tm56Dpch.Select(r => (r.Code, r.Off, r.L30))),
                                 .. Hs(8, -21, -11, WcdmaChannelKind.HsPdsch16Qam)],
            WcdmaModel.Tm5x4 => [.. Common(-11, -11, -19, -19), .. Dpch(128, Tm56Dpch.Select(r => (r.Code, r.Off, r.L14))),
                                 .. Hs(4, -21, -8, WcdmaChannelKind.HsPdsch16Qam)],
            WcdmaModel.Tm5x2 => [.. Common(-11, -11, -19, -19), .. Dpch(128, Tm56Dpch.Select(r => (r.Code, r.Off, r.L6))),
                                 .. Hs(2, -21, -5, WcdmaChannelKind.HsPdsch16Qam)],
            WcdmaModel.Tm6 => [.. Common(-11, -11, -19, -19), .. Dpch(128, Tm56Dpch.Select(r => (r.Code, r.Off, r.Tm6))),
                               .. Hs(8, -21, -12, WcdmaChannelKind.HsPdsch64Qam)],
            _ => [],
        };

        // ------------------------------------------------------------------ codes

        /// <summary>The downlink scrambling code n (TS 25.213 §5.2.2), one frame: Z(i) + j Z(i + 131072), Z = 1 - 2z.</summary>
        public static Complex[] DownlinkScrambling(int n)
        {
            const int len = (1 << 18) - 1;
            var x = new byte[len]; var y = new byte[len];
            x[0] = 1;
            for (int i = 0; i < 18; i++) y[i] = 1;
            for (int i = 0; i + 18 < len; i++)
            {
                x[i + 18] = (byte)(x[i + 7] ^ x[i]);
                y[i + 18] = (byte)(y[i + 10] ^ y[i + 7] ^ y[i + 5] ^ y[i]);
            }
            int Z(int i) => 1 - 2 * (x[(i + n) % len] ^ y[i]);
            return [.. Enumerable.Range(0, FrameChips).Select(i => new Complex(Z(i), Z((i + 131072) % len)))];
        }

        /// <summary>The uplink long scrambling code n (TS 25.213 §4.3.2.2), one frame:
        /// C1(i) (1 + j (-1)^i C2(2⌊i/2⌋)), C2 being C1 16,777,232 chips on.</summary>
        public static Complex[] UplinkScrambling(int n)
        {
            const int shift = 16777232;
            uint xs = (uint)(n & 0xFFFFFF) | (1u << 24), ys = (1u << 25) - 1;   // bit k = x(i + k)
            var c1 = new int[FrameChips]; var c2 = new int[FrameChips];
            for (int i = 0; i < shift + FrameChips; i++)
            {
                int z = 1 - 2 * (int)((xs ^ ys) & 1);
                if (i < FrameChips) c1[i] = z;
                if (i >= shift) c2[i - shift] = z;
                uint xb = (xs ^ (xs >> 3)) & 1, yb = (ys ^ (ys >> 1) ^ (ys >> 2) ^ (ys >> 3)) & 1;
                xs = (xs >> 1) | (xb << 24); ys = (ys >> 1) | (yb << 24);
            }
            return [.. Enumerable.Range(0, FrameChips).Select(i => c1[i] * new Complex(1, (i % 2 == 0 ? 1 : -1) * c2[2 * (i / 2)]))];
        }

        private static readonly int[] PscA = [1, 1, 1, 1, 1, 1, -1, -1, 1, -1, 1, -1, 1, -1, -1, 1];

        /// <summary>The primary synchronisation code's real part (TS 25.213 §5.2.3.1; the code is (1 + j) times it).</summary>
        public static int[] Psc()
        {
            int[] s = [1, 1, 1, -1, -1, 1, -1, -1, 1, 1, 1, -1, 1, -1, 1, 1];
            return [.. s.SelectMany(v => PscA.Select(a => v * a))];
        }

        /// <summary>The secondary synchronisation code k = 1..16's real part: h_m(i) z(i), m = 16 (k - 1).</summary>
        public static int[] Ssc(int k)
        {
            int[] b = [.. PscA.Select((a, i) => i < 8 ? a : -a)];
            int[] s = [1, 1, 1, -1, 1, 1, -1, -1, 1, -1, 1, -1, -1, -1, -1, -1];
            var z = s.SelectMany(v => b.Select(a => v * a)).ToArray();
            int m = 16 * (k - 1);
            return [.. z.Select((v, i) => (System.Numerics.BitOperations.PopCount((uint)(m & i)) % 2 == 0 ? 1 : -1) * v)];
        }

        // TS 25.213 Table 4, scrambling code group 0 (primary codes 0-7)
        private static readonly int[] SscGroup0 = [1, 1, 2, 8, 9, 10, 15, 8, 10, 16, 2, 7, 15, 7, 16];

        // ------------------------------------------------------------------ symbols

        private static readonly double R2 = Math.Sqrt(0.5);
        private static Complex Qpsk(int b0, int b1) => new Complex(1 - 2 * b0, 1 - 2 * b1) * R2;

        /// <summary>16QAM / 64QAM, per-axis Gray at unit mean power: the first bits the signs, the rest the level.</summary>
        private static Complex Qam(byte[] b, int at, int k)
        {
            int half = k / 2;
            double Axis(int off)
            {
                int g = 0;
                for (int i = 1; i < half; i++) g = (g << 1) | (g & 1 ^ b[at + 2 * i + off]);
                return (1 - 2 * b[at + off]) * (2 * g + 1);
            }
            double norm = k == 4 ? Math.Sqrt(10) : Math.Sqrt(42);
            return new Complex(Axis(0), Axis(1)) / norm;
        }

        // TS 25.141 Table 6.8: pilot symbols 1 and 3 (Npilot = 8) per slot; symbols 0 and 2 are 11
        private static readonly string[] Pilot1 = ["11", "00", "01", "00", "10", "11", "11", "10", "01", "11", "01", "10", "10", "00", "00"];
        private static readonly string[] Pilot3 = ["10", "10", "01", "00", "01", "10", "00", "00", "10", "11", "01", "11", "00", "11", "11"];

        /// <summary>One frame of a channel's symbols from its own frame start; 0 where it is off.</summary>
        public static Complex[] Symbols(WcdmaChannel c)
        {
            int n = FrameChips / c.Sf, perSlot = n / 15;
            var s = new Complex[n];
            switch (c.Kind)
            {
                case WcdmaChannelKind.Cpich:
                    Array.Fill(s, Qpsk(0, 0));
                    break;
                case WcdmaChannelKind.Pccpch:
                {
                    var b = Dsss.Pn9(Dsss.Seed(c.Code, 8, 1), 270);
                    for (int i = 0, k = 0; i < n; i++) if (i % perSlot != 0) { s[i] = Qpsk(b[k], b[k + 1]); k += 2; }
                    break;
                }
                case WcdmaChannelKind.Pich:
                {
                    int[] pi = [1, 0, 1, 1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 0, 1, 0];
                    for (int i = 0; i < 144; i++) s[i] = Qpsk(pi[i / 8], pi[i / 8]);   // 16 bits per indicator; the last 12 bits off
                    break;
                }
                case WcdmaChannelKind.Sccpch:
                {
                    var b = Dsss.Pn9(Dsss.Seed(c.Code, 8, 1), 300);
                    for (int i = 0; i < n; i++) s[i] = Qpsk(b[2 * i], b[2 * i + 1]);
                    break;
                }
                case WcdmaChannelKind.Dpch:
                {
                    // slot format 10 (SF 128): Data1 3, TPC 1, Data2 12, pilot 4 symbols; 6 (SF 256): 1, 1, 4, 4
                    bool sf128 = c.Sf == 128;
                    int d1 = sf128 ? 3 : 1, d2 = sf128 ? 12 : 4;
                    var b = Dsss.Pn9(sf128 ? Dsss.Seed(c.Code, 7, 2) : Dsss.Seed(c.Code, 8, 1), 15 * 2 * (d1 + d2));
                    int k = 0;
                    for (int slot = 0; slot < 15; slot++)
                    {
                        int at = slot * perSlot, tpc = slot % 2;
                        for (int i = 0; i < d1; i++, k += 2) s[at++] = Qpsk(b[k], b[k + 1]);
                        s[at++] = Qpsk(tpc, tpc);
                        for (int i = 0; i < d2; i++, k += 2) s[at++] = Qpsk(b[k], b[k + 1]);
                        foreach (var p in new[] { "11", Pilot1[slot], "11", Pilot3[slot] }) s[at++] = Qpsk(p[0] - '0', p[1] - '0');
                    }
                    break;
                }
                case WcdmaChannelKind.HsScch:
                {
                    var b = Dsss.Pn9(Dsss.Seed(c.Code, 7, 2), 511);
                    for (int i = 0; i < n; i++) s[i] = Qpsk(b[2 * i % 511], b[(2 * i + 1) % 511]);
                    break;
                }
                default:
                {
                    int k = c.Kind == WcdmaChannelKind.HsPdsch16Qam ? 4 : 6;
                    var pn = Dsss.Pn9(Dsss.Seed(c.Code * 23, 9, 0), 511);
                    var b = Enumerable.Range(0, n * k).Select(i => pn[i % 511]).ToArray();
                    for (int i = 0; i < n; i++) s[i] = Qam(b, i * k, k);
                    break;
                }
            }
            return s;
        }

        /// <summary>A channel's chips over the frame, unscrambled, at unit symbol amplitude, from its timing offset.</summary>
        private static Complex[] Spread(WcdmaChannel c)
        {
            var sym = Symbols(c);
            var code = Dsss.Ovsf(c.Sf, c.Code);
            var x = new Complex[FrameChips];
            int tau = c.OffsetX256 * 256;
            for (int s = 0; s < sym.Length; s++)
                for (int j = 0; j < c.Sf; j++) x[(tau + s * c.Sf + j) % FrameChips] = sym[s] * code[j];
            return x;
        }

        /// <summary>The chips sent, one frame: everything, and the SCH alone (the measurement takes it out before
        /// despreading).</summary>
        public static (Complex[] Chips, Complex[] Sch) ReferenceChips(WcdmaModel m, int scramblingCode = 0)
        {
            if (m == WcdmaModel.Uplink12k2) return (UplinkChips(scramblingCode), new Complex[FrameChips]);
            var sum = new Complex[FrameChips];
            foreach (var c in Channels(m))
            {
                double g = Math.Sqrt(Math.Pow(10, c.LevelDb / 10) / 2);   // |S|² = 2: a level's power per chip after scrambling
                var x = Spread(c);
                for (int i = 0; i < FrameChips; i++) sum[i] += g * x[i];
            }
            var scr = DownlinkScrambling(16 * scramblingCode);
            var sch = new Complex[FrameChips];
            double gs = Math.Sqrt(Math.Pow(10, (Channels(m).First(c => c.Kind == WcdmaChannelKind.Pccpch).LevelDb - 3) / 10) / 2);
            var psc = Psc();
            for (int slot = 0; slot < 15; slot++)
            {
                var ssc = Ssc(SscGroup0[slot]);
                for (int i = 0; i < 256; i++) sch[slot * SlotChips + i] = gs * (psc[i] + ssc[i]) * new Complex(1, 1);
            }
            return ([.. sum.Select((v, i) => v * scr[i] + sch[i])], sch);
        }

        /// <summary>The 12.2 kb/s uplink reference channel's chips (DPDCH on I, DPCCH on Q, long code n).</summary>
        private static Complex[] UplinkChips(int n)
        {
            var dCode = Dsss.Ovsf(64, 16); var cCode = Dsss.Ovsf(256, 0);
            var dBits = Dsss.Pn9(Dsss.Seed(16, 7, 2), 600);
            var cBits = Dsss.Pn9(Dsss.Seed(1, 8, 1), 150);
            var scr = UplinkScrambling(n);
            return [.. Enumerable.Range(0, FrameChips).Select(i =>
                new Complex((1 - 2 * dBits[i / 64]) * dCode[i % 64], 8.0 / 15 * (1 - 2 * cBits[i / 256]) * cCode[i % 256]) * scr[i])];
        }

        /// <summary>One frame at the native rate (8 samples a chip), RRC-shaped, unit mean power.</summary>
        public static Complex[] Generate(WcdmaModel m, int scramblingCode = 0)
        {
            if (scramblingCode is < 0 or > 7) throw new FormatException($"WCDMA carrier: the scrambling code must be 0 to 7 (group 0; got {scramblingCode})");
            var x = Dsss.Shape(ReferenceChips(m, scramblingCode).Chips, SamplesPerChip, RollOff);
            double rms = Math.Sqrt(x.Average(v => v.Magnitude * v.Magnitude));
            return [.. x.Select(v => v / rms)];
        }

        /// <summary>The record's quality: <paramref name="x"/> one frame at the native rate, aligned to it.</summary>
        public static WcdmaQuality Measure(WcdmaModel m, ReadOnlySpan<Complex> x, int scramblingCode = 0)
        {
            var (sent, sch) = ReferenceChips(m, scramblingCode);
            var rx = Dsss.Matched(x, SamplesPerChip, RollOff);
            var g = Dsss.Gain(rx, sent);
            for (int i = 0; i < rx.Length; i++) rx[i] /= g;
            var (evm, _) = Dsss.Evm(rx, sent);
            if (m == WcdmaModel.Uplink12k2) return new(evm, double.NaN, []);

            double total = rx.Average(v => v.Magnitude * v.Magnitude), sentPower = sent.Average(v => v.Magnitude * v.Magnitude);
            var scr = DownlinkScrambling(16 * scramblingCode);
            var d = rx.Select((v, i) => (v - sch[i]) * Complex.Conjugate(scr[i]) / 2).ToArray();
            var powers = Channels(m).Select(c =>
            {
                var ideal = Symbols(c); var code = Dsss.Ovsf(c.Sf, c.Code);
                int tau = c.OffsetX256 * 256, on = 0; double p = 0;
                for (int s = 0; s < ideal.Length; s++)
                {
                    if (ideal[s] == Complex.Zero) continue;
                    Complex y = 0;
                    for (int j = 0; j < c.Sf; j++) y += d[(tau + s * c.Sf + j) % FrameChips] * code[j];
                    y /= c.Sf; p += y.Magnitude * y.Magnitude; on++;
                }
                return new WcdmaCodePower(c, 10 * Math.Log10(2 * p / on / total));
            }).ToArray();

            // the error vector projected onto every SF 256 code, symbol by symbol
            var power = new double[256];
            for (int blk = 0; blk < FrameChips / 256; blk++)
            {
                var e = new Complex[256];
                for (int j = 0; j < 256; j++) { int i = blk * 256 + j; e[j] = (rx[i] - sent[i]) * Complex.Conjugate(scr[i]) / 2; }
                Dsss.Fwht(e);
                for (int k = 0; k < 256; k++) power[k] += (e[k] / 256).Magnitude * (e[k] / 256).Magnitude / (FrameChips / 256);
            }
            double pcde = 10 * Math.Log10(Math.Max(2 * power.Max() / sentPower, 1e-30));
            return new(evm, pcde, powers);
        }
    }

    // =================================================================== IEEE 802.11b

    public enum Wlan11bRate { Dbpsk1M, Dqpsk2M, Cck5M5, Cck11M }

    /// <summary>An 802.11b frame: the PSDU's rate and length (bytes).</summary>
    public sealed record Wlan11bShape(Wlan11bRate Rate = Wlan11bRate.Cck11M, int PsduBytes = 1000)
    {
        public double RateMbps => Rate switch { Wlan11bRate.Dbpsk1M => 1, Wlan11bRate.Dqpsk2M => 2, Wlan11bRate.Cck5M5 => 5.5, _ => 11 };
        /// <summary>Preamble 144 µs + header 48 µs + the PSDU.</summary>
        public int Chips => 11 * 192 + Rate switch
        {
            Wlan11bRate.Dbpsk1M => 11 * 8 * PsduBytes, Wlan11bRate.Dqpsk2M => 11 * 4 * PsduBytes,
            Wlan11bRate.Cck5M5 => 8 * 2 * PsduBytes, _ => 8 * PsduBytes,
        };
        public double RecordSeconds => Chips / Ieee80211b.ChipRateHz;
    }

    public static class Ieee80211b
    {
        public const double ChipRateHz = 11e6, RollOff = 0.5, ChannelBandwidthHz = 22e6;
        public const int SamplesPerChip = 8;
        public static double NativeRateHz => SamplesPerChip * ChipRateHz;

        /// <summary>An 802.11b carrier's frame: the PSDU's rate (Rb: 1, 2, 5.5 or 11 Mb/s) and length (11 Mb/s, 1000 B unstated).</summary>
        public static Wlan11bShape Resolve(CarrierSpec c)
        {
            var rate = (c.BitRateBps ?? 11e6) switch
            {
                1e6 => Wlan11bRate.Dbpsk1M, 2e6 => Wlan11bRate.Dqpsk2M, 5.5e6 => Wlan11bRate.Cck5M5, 11e6 => Wlan11bRate.Cck11M,
                var r => throw new FormatException($"802.11b carrier: Rb must be 1, 2, 5.5 or 11 Mbps (got {r / 1e6} Mb/s)"),
            };
            int bytes = c.PsduBytes ?? 1000;
            if (bytes is < 1 or > 4095) throw new FormatException($"802.11b carrier: the PSDU must be 1 to 4095 bytes (got {bytes})");
            return new Wlan11bShape(rate, bytes);
        }

        /// <summary>The 11-chip Barker code, first chip first.</summary>
        public static readonly int[] Barker = [1, -1, 1, 1, -1, 1, 1, 1, -1, -1, -1];

        /// <summary>The CCK code word (8 chips, c0 first) of the four phases.</summary>
        public static Complex[] Cck(double p1, double p2, double p3, double p4)
        {
            Complex E(double a) => Complex.FromPolarCoordinates(1, a);
            return [E(p1 + p2 + p3 + p4), E(p1 + p3 + p4), E(p1 + p2 + p4), -E(p1 + p4), E(p1 + p2 + p3), E(p1 + p3), -E(p1 + p2), E(p1)];
        }

        private static IEnumerable<int> Lsb(int v, int bits) => Enumerable.Range(0, bits).Select(i => (v >> i) & 1);

        /// <summary>The PLCP CRC-16 of <paramref name="bits"/> (transmit order): x^16 + x^12 + x^5 + 1, preset to 1s, the
        /// ones complement, 16 bits in transmit order (the high bit first).</summary>
        public static int[] Crc16(IEnumerable<int> bits)
        {
            int reg = 0xFFFF;
            foreach (int b in bits)
            {
                int fb = ((reg >> 15) & 1) ^ b;
                reg = (reg << 1) & 0xFFFF;
                if (fb == 1) reg ^= 0x1021;
            }
            reg ^= 0xFFFF;
            return [.. Enumerable.Range(0, 16).Select(i => (reg >> (15 - i)) & 1)];
        }

        /// <summary>The PLCP header's 48 bits in transmit order: SIGNAL (the rate in 100 kb/s), SERVICE (bit 2 locked clocks,
        /// bit 7 the length extension), LENGTH (µs), each least significant bit first, then the CRC-16.</summary>
        public static int[] Header(Wlan11bShape s)
        {
            int signal = (int)Math.Round(s.RateMbps * 10), bits = 8 * s.PsduBytes;
            int length = (int)Math.Ceiling(bits / s.RateMbps - 1e-9);
            int service = 0x04 | (s.Rate == Wlan11bRate.Cck11M && length * 11 - bits >= 8 ? 0x80 : 0);
            var h = Lsb(signal, 8).Concat(Lsb(service, 8)).Concat(Lsb(length, 16)).ToList();
            return [.. h, .. Crc16(h)];
        }

        /// <summary>The frame's bits before scrambling: SYNC (128 1s), SFD (F3A0h, least significant bit first), header, PSDU.</summary>
        public static int[] Bits(Wlan11bShape s)
        {
            var pn = Fsk.DataBits("PN9");
            return [.. Enumerable.Repeat(1, 128), .. Lsb(0xF3A0, 16), .. Header(s), .. Enumerable.Range(0, 8 * s.PsduBytes).Select(i => (int)pn[i % pn.Length])];
        }

        /// <summary>The self-synchronising scrambler z^-7 + z^-4 + 1 from the long preamble's seed 1101100.</summary>
        public static int[] Scramble(IReadOnlyList<int> d)
        {
            var reg = new List<int> { 1, 1, 0, 1, 1, 0, 0 };   // the last 7 scrambled bits, newest first
            var o = new int[d.Count];
            for (int i = 0; i < d.Count; i++)
            {
                o[i] = d[i] ^ reg[3] ^ reg[6];
                reg.Insert(0, o[i]); reg.RemoveAt(7);
            }
            return o;
        }

        private static readonly double[] Dq = [0, Math.PI / 2, 3 * Math.PI / 2, Math.PI];   // DQPSK by (d0 d1): 00, 01, 10, 11
        private static readonly double[] Qp = [0, Math.PI / 2, Math.PI, 3 * Math.PI / 2];   // CCK φ2-φ4 by (d0 d1)

        /// <summary>The frame's chips at 11 Mchip/s, unit magnitude.</summary>
        public static Complex[] Chips(Wlan11bShape s)
        {
            if (s.PsduBytes is < 1 or > 4095) throw new FormatException($"802.11b carrier: the PSDU must be 1 to 4095 bytes (got {s.PsduBytes})");
            var b = Scramble(Bits(s));
            var c = new List<Complex>(s.Chips);
            double phase = 0;
            void Barkered() { foreach (int v in Barker) c.Add(Complex.FromPolarCoordinates(v, phase)); }
            for (int i = 0; i < 192; i++) { phase += b[i] * Math.PI; Barkered(); }
            int at = 192, end = b.Length;
            switch (s.Rate)
            {
                case Wlan11bRate.Dbpsk1M:
                    for (; at < end; at++) { phase += b[at] * Math.PI; Barkered(); }
                    break;
                case Wlan11bRate.Dqpsk2M:
                    for (; at < end; at += 2) { phase += Dq[2 * b[at] + b[at + 1]]; Barkered(); }
                    break;
                default:
                    bool eleven = s.Rate == Wlan11bRate.Cck11M;
                    for (int sym = 0; at < end; sym++)
                    {
                        phase += Dq[2 * b[at] + b[at + 1]] + (sym % 2 == 1 ? Math.PI : 0);
                        double p2, p3, p4;
                        if (eleven) { p2 = Qp[2 * b[at + 2] + b[at + 3]]; p3 = Qp[2 * b[at + 4] + b[at + 5]]; p4 = Qp[2 * b[at + 6] + b[at + 7]]; at += 8; }
                        else { p2 = b[at + 2] * Math.PI + Math.PI / 2; p3 = 0; p4 = b[at + 3] * Math.PI; at += 4; }
                        c.AddRange(Cck(phase, p2, p3, p4));
                    }
                    break;
            }
            return [.. c];
        }

        /// <summary>The frame at the native rate (8 samples a chip), RRC-shaped, unit mean power.</summary>
        public static Complex[] Generate(Wlan11bShape s)
        {
            var x = Dsss.Shape(Chips(s), SamplesPerChip, RollOff);
            double rms = Math.Sqrt(x.Average(v => v.Magnitude * v.Magnitude));
            return [.. x.Select(v => v / rms)];
        }

        /// <summary>RMS and peak chip EVM (%) after the matched filter; the clause's limit is a 35 % peak over 1000 chips.</summary>
        public static (double RmsPercent, double PeakPercent) Evm(Wlan11bShape s, ReadOnlySpan<Complex> x)
            => Dsss.Evm(Dsss.Matched(x, SamplesPerChip, RollOff), Chips(s));

        /// <summary>The worst margin (dB, positive = inside) against the transmit spectrum mask: -30 dBr from 11 to 22 MHz
        /// off the carrier and -50 dBr beyond, in a 100 kHz resolution bandwidth. The clause refers dBr to the sin x / x
        /// peak; here to the measured spectrum's own 100 kHz peak.</summary>
        public static double MaskMarginDb(ReadOnlySpan<Complex> x, double rateHz)
        {
            var X = Fft.Forward(x);
            int n = X.Length;
            double df = rateHz / n;
            int w = Math.Max(1, (int)Math.Round(100e3 / df));
            var f = Enumerable.Range(0, n).Select(i => (i <= n / 2 ? i : i - n) * df).ToArray();
            var order = Enumerable.Range(0, n).OrderBy(i => f[i]).ToArray();
            var p = order.Select(i => X[i].Magnitude * X[i].Magnitude).ToArray();
            var cum = new double[n + 1];
            for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + p[i];
            var rbw = Enumerable.Range(0, n - w).Select(i => (F: f[order[i + w / 2]], P: cum[i + w] - cum[i])).ToArray();
            double peak = rbw.Max(v => v.P), margin = double.PositiveInfinity;
            foreach (var (fc, pw) in rbw)
            {
                double a = Math.Abs(fc);
                if (a < 11e6) continue;
                double limit = a < 22e6 ? -30 : -50, level = 10 * Math.Log10(Math.Max(pw / peak, 1e-30));
                margin = Math.Min(margin, limit - level);
            }
            return margin;
        }
    }

    // =================================================================== IEEE 802.15.4 O-QPSK

    /// <summary>An 802.15.4 2.4 GHz O-QPSK frame: the PSDU's length (bytes, up to 127).</summary>
    public sealed record Oqpsk154Shape(int PsduBytes = 127)
    {
        /// <summary>Preamble 8 + SFD 2 + PHR 2 + PSDU 2 per byte, 4-bit symbols.</summary>
        public int Symbols => 12 + 2 * PsduBytes;
        public double RecordSeconds => Symbols * 32 / Ieee802154.ChipRateHz;
    }

    public static class Ieee802154
    {
        public const double ChipRateHz = 2e6, ChannelBandwidthHz = 5e6;
        public const int SamplesPerChip = 8;
        public static double NativeRateHz => SamplesPerChip * ChipRateHz;

        /// <summary>An 802.15.4 carrier's frame (127 B unstated).</summary>
        public static Oqpsk154Shape Resolve(CarrierSpec c)
        {
            int bytes = c.PsduBytes ?? 127;
            if (bytes is < 1 or > 127) throw new FormatException($"802.15.4 carrier: the PSDU must be 1 to 127 bytes (got {bytes})");
            return new Oqpsk154Shape(bytes);
        }

        private const string Symbol0 = "11011001110000110101001000101110";

        /// <summary>The 32 chips of a 4-bit symbol, c0 first: symbol 0 turned right by 4 chips per step for 1-7; 8-15 those
        /// with the odd chips inverted.</summary>
        public static byte[] ChipSequence(int symbol)
        {
            int k = symbol & 7;
            return [.. Enumerable.Range(0, 32).Select(i => (byte)((Symbol0[((i - 4 * k) % 32 + 32) % 32] - '0') ^ (symbol >= 8 && i % 2 == 1 ? 1 : 0)))];
        }

        /// <summary>The frame's 4-bit symbols: preamble, SFD A7h, PHR (the length), PSDU (PN9), each byte low nibble first.</summary>
        public static int[] SymbolsOf(Oqpsk154Shape s)
        {
            var pn = Fsk.DataBits("PN9");
            var bytes = new List<int> { 0, 0, 0, 0, 0xA7, s.PsduBytes };
            for (int i = 0; i < s.PsduBytes; i++) bytes.Add(Enumerable.Range(0, 8).Sum(j => pn[(8 * i + j) % pn.Length] << j));
            return [.. bytes.SelectMany(v => new[] { v & 15, v >> 4 })];
        }

        public static byte[] Chips(Oqpsk154Shape s)
        {
            if (s.PsduBytes is < 1 or > 127) throw new FormatException($"802.15.4 carrier: the PSDU must be 1 to 127 bytes (got {s.PsduBytes})");
            return [.. SymbolsOf(s).SelectMany(ChipSequence)];
        }

        /// <summary>The frame at the native rate (8 samples a chip): chip m a half-sine two chips long from m Tc, on I when
        /// m is even, on Q when odd. Unit magnitude throughout.</summary>
        public static Complex[] Generate(Oqpsk154Shape s)
        {
            var c = Chips(s);
            const int spc = SamplesPerChip;
            int n = c.Length * spc;
            var x = new Complex[n];
            for (int m = 0; m < c.Length; m++)
            {
                Complex a = (2 * c[m] - 1) * (m % 2 == 0 ? Complex.One : Complex.ImaginaryOne);
                for (int t = 0; t < 2 * spc; t++) x[(m * spc + t) % n] += a * Math.Sin(Math.PI * t / (2 * spc));
            }
            return x;
        }

        /// <summary>RMS and peak chip EVM (%): each chip read at its pulse's peak, (m + 1) Tc, where the other rail is zero.</summary>
        public static (double RmsPercent, double PeakPercent) Evm(Oqpsk154Shape s, ReadOnlySpan<Complex> x)
        {
            var c = Chips(s);
            var rx = new Complex[c.Length];
            var sent = new Complex[c.Length];
            for (int m = 0; m < c.Length; m++)
            {
                rx[m] = x[((m + 1) * SamplesPerChip) % x.Length];
                sent[m] = (2 * c[m] - 1) * (m % 2 == 0 ? Complex.One : Complex.ImaginaryOne);
            }
            return Dsss.Evm(rx, sent);
        }
    }
}
