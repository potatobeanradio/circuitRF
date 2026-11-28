// ================================================================
//  OfdmPresets.cs  —  a numerology record, and OFDM standards as data
//
//  An OfdmNumerology is everything a pilot-bearing OFDM standard needs
//  for its RF-relevant signal: FFT size and sample rate, the cyclic
//  prefix of every symbol of one period, the used carriers (with or
//  without the DC carrier), the pilots (a pattern that repeats every
//  PilotPeriod symbols with a pitch in carriers, plus continual
//  pilots), their boost and reference sequence, signalling carriers,
//  symbol on/off (a null symbol), and the data modulation.  Build()
//  turns it into an OfdmGrid, which synthesises and measures EVM like
//  every other OFDM signal here.
//
//  Simplifications for every preset: data cells are seeded random
//  constellation points (no FEC, interleaving or framing); pilots and
//  reference symbols are as the standard defines them unless noted.
//    DVB-T (ETSI EN 300 744 V1.6.2 cl. 4.4-4.6): 2K and 8K, 8 MHz,
//      GI 1/4, scattered + continual pilots (tables 7) at 4/3 from the
//      PRBS x^11+x^2+1; TPS carriers (table 8) carry the reference
//      BPSK of their first symbol in every symbol (TPS bits not coded).
//    DVB-T2 (ETSI EN 302 755 V1.4.1 cl. 9.2): 8K normal carrier mode,
//      8 MHz, GI 1/8, PP2 (table 57) at 4/3, edge pilots, the frame PN
//      (table 56) on each symbol; data symbols only (no P1, P2 or
//      frame-closing symbol); the continual pilots of annex G are NOT
//      included (the table's PP columns did not extract reliably).
//    ISDB-T (ARIB STD-B31): mode 3, 6 MHz, GI 1/8, 13 synchronous
//      segments; scattered pilots every 12 carriers moving 3 per
//      symbol at 4/3 and the upper-edge continual pilot.  The standard
//      was not readable here: its per-segment PRBS initial values are
//      replaced by one PRBS from the lowest carrier, and TMCC / AC
//      carriers carry data — unverified.
//    DAB mode I (ETSI EN 300 401 V2.2.1 cl. 14): 1536 carriers at
//      1 kHz, null symbol, phase reference symbol (tables 23, 24), 75
//      pi/4-DQPSK symbols: one 96 ms frame.
//    WiMAX 802.16e OFDMA, 10 MHz DL PUSC: 1024 FFT at 11.2 MS/s, CP
//      1/8, 840 used carriers in 60 clusters of 14, pilots 4 and 8
//      (even symbols) / 0 and 12 (odd) at 4/3 from the PRBS
//      x^11+x^9+1 started all ones.  From the published profile, the
//      standard itself not read here — unverified.
// ================================================================

using System;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>The reference sequence a numerology's pilots take their sign from.</summary>
    public enum PilotSequence
    {
        /// <summary>PRBS x^11 + x^2 + 1 started all ones (DVB-T cl. 4.5.2, DVB-T2 cl. 9.2.2.1): w_n = w_(n-9) xor w_(n-11).</summary>
        Dvb,
        /// <summary>PRBS x^11 + x^9 + 1 started all ones: w_n = w_(n-2) xor w_(n-11).</summary>
        Wimax,
    }

    public sealed record OfdmNumerology
    {
        public required string Name { get; init; }
        /// <summary>The standard, its version and clause.</summary>
        public required string Reference { get; init; }
        public required int Nfft { get; init; }
        public required double SampleRateHz { get; init; }
        /// <summary>Used carriers; carrier 0 is the lowest.</summary>
        public required int Carriers { get; init; }
        /// <summary>The DC carrier is one of <see cref="Carriers"/> (false: the carriers sit either side of an unused DC).</summary>
        public bool DcUsed { get; init; } = true;
        /// <summary>Cyclic prefix of each symbol of one period (samples); the record repeats it.</summary>
        public required int[] CpPattern { get; init; }
        /// <summary>Symbols of the period that are transmitted (null = all): a null symbol is off.</summary>
        public bool[]? OnPattern { get; init; }
        /// <summary>Pilot carriers by symbol: on symbol l, carrier k is a pilot when k mod <see cref="PilotPitch"/> is in
        /// <c>PilotPositions[l mod PilotPositions.Length]</c>.</summary>
        public int PilotPitch { get; init; }
        public int[][] PilotPositions { get; init; } = [];
        public double PilotAmplitude { get; init; } = 1;
        public int[] ContinualPilots { get; init; } = [];
        public double ContinualAmplitude { get; init; } = 1;
        /// <summary>Signalling carriers (TPS): BPSK of the reference sequence at amplitude 1; neither data nor pilot.</summary>
        public int[] SignallingCarriers { get; init; } = [];
        public PilotSequence Sequence { get; init; } = PilotSequence.Dvb;
        /// <summary>Index into the reference sequence of carrier 0 (DVB-T2 normal carrier mode: K_ext).</summary>
        public int SequenceOffset { get; init; }
        /// <summary>A frame-level PN (hex, MSB first) whose bit l inverts the pilots of symbol l (DVB-T2 cl. 9.2.2.2).</summary>
        public string? FramePn { get; init; }
        public QamOrder Modulation { get; init; } = QamOrder.Qam64;
        /// <summary>DAB: the symbol after the null symbol is the phase reference, and every later symbol pi/4-DQPSK on it.</summary>
        public bool DabDifferential { get; init; }

        public int SymbolsPerPeriod => CpPattern.Length;
        public double ScsHz => SampleRateHz / Nfft;

        /// <summary>Signed FFT bin of carrier <paramref name="i"/>.</summary>
        public int Bin(int i) => DcUsed ? i - Carriers / 2 : i < Carriers / 2 ? i - Carriers / 2 : i - Carriers / 2 + 1;

        /// <summary>A record of <paramref name="symbols"/> symbols (the pattern repeats; a whole number of
        /// <see cref="SymbolsPerPeriod"/> and of the pilot period makes it loop seamlessly in its pilots).</summary>
        public OfdmGrid Build(int symbols, int seed)
        {
            if (symbols < 1) throw new ArgumentException($"{Name}: at least one symbol");
            var rng = new Random(seed);
            int k = Carriers, bpa = (int)Modulation / 2, lv = 1 << bpa;
            double qn = 1.0 / Math.Sqrt(2.0 * (lv * lv - 1) / 3.0);
            var w = Prbs(Sequence, SequenceOffset + k);
            var pn = FramePn is null ? null : HexBits(FramePn);
            var bins = new int[k];
            for (int i = 0; i < k; i++) bins[i] = ((Bin(i) % Nfft) + Nfft) % Nfft;
            var cont = new bool[k];
            foreach (int c in ContinualPilots) cont[c] = true;
            var sig = new bool[k];
            foreach (int c in SignallingCarriers) sig[c] = true;
            var x = new Complex[symbols][];
            var role = new ReRole[symbols][];
            var cp = new int[symbols];
            var on = new bool[symbols];
            for (int s = 0; s < symbols; s++)
            {
                int l = s % SymbolsPerPeriod;
                cp[s] = CpPattern[l];
                on[s] = OnPattern?[l] ?? true;
                var row = x[s] = new Complex[k];
                var r = role[s] = new ReRole[k];
                if (!on[s]) continue;
                if (DabDifferential)
                {
                    // EN 300 401 cl. 14.3.2: z(1,k) = e^(j phi_k), phi_k = pi/2 (h(i, k - k') + n); cl. 14.5: z(l,k) = z(l-1,k) y(l,k)
                    bool reference = l == 1;
                    for (int i = 0; i < k; i++)
                    {
                        if (reference) { row[i] = DabPrs(Bin(i)); r[i] = ReRole.Pilot; }
                        else { row[i] = x[s - 1][i] * Complex.FromPolarCoordinates(1, Math.PI / 4 + Math.PI / 2 * rng.Next(4)); r[i] = ReRole.Data; }
                    }
                    continue;
                }
                int sign = pn is not null && pn[s % pn.Length] ? -1 : 1;
                var pos = PilotPositions.Length > 0 ? PilotPositions[s % PilotPositions.Length] : [];
                for (int i = 0; i < k; i++)
                {
                    double wv = sign * 2 * (0.5 - w[SequenceOffset + i]);
                    bool sp = PilotPitch > 0 && Array.IndexOf(pos, i % PilotPitch) >= 0;
                    if (sp) { row[i] = PilotAmplitude * wv; r[i] = ReRole.Pilot; }
                    else if (cont[i]) { row[i] = ContinualAmplitude * wv; r[i] = ReRole.Pilot; }
                    else if (sig[i]) { row[i] = 2 * (0.5 - w[SequenceOffset + i]); r[i] = ReRole.Other; }
                    else
                    {
                        row[i] = new Complex((2 * rng.Next(lv) - (lv - 1)) * qn, (2 * rng.Next(lv) - (lv - 1)) * qn);
                        r[i] = ReRole.Data;
                    }
                }
            }
            return new OfdmGrid
            {
                Label = Name, Nfft = Nfft, SampleRateHz = SampleRateHz, Cp = cp, Bins = bins, X = x, Role = role, On = on,
            };
        }

        /// <summary>The reference PRBS w_0 .. w_(n-1).</summary>
        public static byte[] Prbs(PilotSequence seq, int n)
        {
            var w = new byte[Math.Max(n, 11)];
            for (int i = 0; i < 11; i++) w[i] = 1;
            int tap = seq == PilotSequence.Dvb ? 9 : 2;
            for (int i = 11; i < w.Length; i++) w[i] = (byte)(w[i - tap] ^ w[i - 11]);
            return w;
        }

        private static bool[] HexBits(string hex)
        {
            var b = new bool[hex.Length * 4];
            for (int i = 0; i < hex.Length; i++)
            {
                int v = Convert.ToInt32(hex[i].ToString(), 16);
                for (int j = 0; j < 4; j++) b[4 * i + j] = ((v >> (3 - j)) & 1) == 1;
            }
            return b;
        }

        // EN 300 401 V2.2.1 table 23 (mode I): first carrier k' of each 32-carrier block, i, n; table 24: h(i, j), j = 0..15
        // (columns 16..31 repeat 0..15).
        private static readonly (int K0, int I, int N)[] DabBlocks =
        [
            (-768, 0, 1), (-736, 1, 2), (-704, 2, 0), (-672, 3, 1), (-640, 0, 3), (-608, 1, 2), (-576, 2, 2), (-544, 3, 3),
            (-512, 0, 2), (-480, 1, 1), (-448, 2, 2), (-416, 3, 3), (-384, 0, 1), (-352, 1, 2), (-320, 2, 3), (-288, 3, 3),
            (-256, 0, 2), (-224, 1, 2), (-192, 2, 2), (-160, 3, 1), (-128, 0, 1), (-96, 1, 3), (-64, 2, 1), (-32, 3, 2),
            (1, 0, 3), (33, 3, 1), (65, 2, 1), (97, 1, 1), (129, 0, 2), (161, 3, 2), (193, 2, 1), (225, 1, 0),
            (257, 0, 2), (289, 3, 2), (321, 2, 3), (353, 1, 3), (385, 0, 0), (417, 3, 2), (449, 2, 1), (481, 1, 3),
            (513, 0, 3), (545, 3, 3), (577, 2, 3), (609, 1, 0), (641, 0, 3), (673, 3, 0), (705, 2, 1), (737, 1, 1),
        ];
        private static readonly int[][] DabH =
        [
            [0, 2, 0, 0, 0, 0, 1, 1, 2, 0, 0, 0, 2, 2, 1, 1], [0, 3, 2, 3, 0, 1, 3, 0, 2, 1, 2, 3, 2, 3, 3, 0],
            [0, 0, 0, 2, 0, 2, 1, 3, 2, 2, 0, 2, 2, 0, 1, 3], [0, 1, 2, 1, 0, 3, 3, 2, 2, 3, 2, 1, 2, 1, 3, 2],
        ];

        /// <summary>The DAB mode I phase reference symbol on carrier <paramref name="kk"/> (-768..768, not 0).</summary>
        public static Complex DabPrs(int kk)
        {
            foreach (var (k0, i, n) in DabBlocks)
                if (kk >= k0 && kk < k0 + 32) return Complex.FromPolarCoordinates(1, Math.PI / 2 * (DabH[i][(kk - k0) % 16] + n));
            throw new ArgumentOutOfRangeException(nameof(kk));
        }
    }

    public static class OfdmPresets
    {
        // EN 300 744 V1.6.2 table 7 (continual pilots) and table 8 (TPS carriers), 8K; the 2K lists are the entries <= 1704.
        private static readonly int[] DvbTCp8K =
        [
            0, 48, 54, 87, 141, 156, 192, 201, 255, 279, 282, 333, 432, 450, 483, 525, 531, 618, 636, 714, 759, 765, 780, 804,
            873, 888, 918, 939, 942, 969, 984, 1050, 1101, 1107, 1110, 1137, 1140, 1146, 1206, 1269, 1323, 1377, 1491, 1683, 1704, 1752, 1758, 1791,
            1845, 1860, 1896, 1905, 1959, 1983, 1986, 2037, 2136, 2154, 2187, 2229, 2235, 2322, 2340, 2418, 2463, 2469, 2484, 2508, 2577, 2592, 2622, 2643,
            2646, 2673, 2688, 2754, 2805, 2811, 2814, 2841, 2844, 2850, 2910, 2973, 3027, 3081, 3195, 3387, 3408, 3456, 3462, 3495, 3549, 3564, 3600, 3609,
            3663, 3687, 3690, 3741, 3840, 3858, 3891, 3933, 3939, 4026, 4044, 4122, 4167, 4173, 4188, 4212, 4281, 4296, 4326, 4347, 4350, 4377, 4392, 4458,
            4509, 4515, 4518, 4545, 4548, 4554, 4614, 4677, 4731, 4785, 4899, 5091, 5112, 5160, 5166, 5199, 5253, 5268, 5304, 5313, 5367, 5391, 5394, 5445,
            5544, 5562, 5595, 5637, 5643, 5730, 5748, 5826, 5871, 5877, 5892, 5916, 5985, 6000, 6030, 6051, 6054, 6081, 6096, 6162, 6213, 6219, 6222, 6249,
            6252, 6258, 6318, 6381, 6435, 6489, 6603, 6795, 6816,
        ];
        private static readonly int[] DvbTTps8K =
        [
            34, 50, 209, 346, 413, 569, 595, 688, 790, 901, 1073, 1219, 1262, 1286, 1469, 1594, 1687, 1738, 1754, 1913, 2050, 2117, 2273, 2299,
            2392, 2494, 2605, 2777, 2923, 2966, 2990, 3173, 3298, 3391, 3442, 3458, 3617, 3754, 3821, 3977, 4003, 4096, 4198, 4309, 4481, 4627, 4670, 4694,
            4877, 5002, 5095, 5146, 5162, 5321, 5458, 5525, 5681, 5707, 5800, 5902, 6013, 6185, 6331, 6374, 6398, 6581, 6706, 6799,
        ];

        private static int[] Repeat(int v, int n) { var a = new int[n]; Array.Fill(a, v); return a; }

        /// <summary>DVB-T, 8 MHz channel (T = 7/64 us), guard 1/4, 64QAM; 68 symbols a frame, the pilot pattern repeats every 4.</summary>
        private static OfdmNumerology DvbT(bool eightK)
        {
            int n = eightK ? 8192 : 2048, kmax = eightK ? 6816 : 1704;
            return new OfdmNumerology
            {
                Name = eightK ? "DVB-T 8K" : "DVB-T 2K",
                Reference = "ETSI EN 300 744 V1.6.2 cl. 4.4, 4.5 (tables 7, 8)",
                Nfft = n, SampleRateHz = 64e6 / 7, Carriers = kmax + 1, CpPattern = Repeat(n / 4, 68),
                // cl. 4.5.3: k = Kmin + 3 (l mod 4) + 12 p
                PilotPitch = 12, PilotPositions = [[0], [3], [6], [9]], PilotAmplitude = 4.0 / 3,
                ContinualPilots = Array.FindAll(DvbTCp8K, c => c <= kmax), ContinualAmplitude = 4.0 / 3,
                SignallingCarriers = Array.FindAll(DvbTTps8K, c => c <= kmax),
            };
        }

        public static OfdmNumerology DvbT2K { get; } = DvbT(false);
        public static OfdmNumerology DvbT8K { get; } = DvbT(true);

        /// <summary>DVB-T2 8K normal carrier mode, 8 MHz, GI 1/8 (1024T, table 67), PP2 (D_X 6, D_Y 2: k mod 12 = 6 (l mod 2)),
        /// A_SP 4/3 (table 60), edge pilots at A_SP, K_ext 48 (table 66), the frame PN of table 56.</summary>
        public static OfdmNumerology DvbT2 { get; } = new()
        {
            Name = "DVB-T2 8K PP2",
            Reference = "ETSI EN 302 755 V1.4.1 cl. 9.2 (tables 57, 60, 66, 67, 56)",
            Nfft = 8192, SampleRateHz = 64e6 / 7, Carriers = 6817, CpPattern = Repeat(1024, 2),
            PilotPitch = 12, PilotPositions = [[0], [6]], PilotAmplitude = 4.0 / 3,
            ContinualPilots = [0, 6816], ContinualAmplitude = 4.0 / 3,
            SequenceOffset = 48,
            FramePn = "4DC2AF7BD8C3C9A1E76C9A090AF1C3114F07FCA2808E",
        };

        /// <summary>ISDB-T mode 3, 6 MHz (512/63 MS/s, 5617 carriers at 0.992 kHz), GI 1/8, 64QAM, 204 symbols a frame.</summary>
        public static OfdmNumerology IsdbT { get; } = new()
        {
            Name = "ISDB-T mode 3",
            Reference = "ARIB STD-B31 (mode 3, synchronous segments); PRBS initial values and TMCC/AC unverified",
            Nfft = 8192, SampleRateHz = 512e6 / 63, Carriers = 5617, CpPattern = Repeat(1024, 4),
            PilotPitch = 12, PilotPositions = [[0], [3], [6], [9]], PilotAmplitude = 4.0 / 3,
            ContinualPilots = [5616], ContinualAmplitude = 4.0 / 3,
        };

        /// <summary>DAB mode I: 2.048 MS/s, 2048 FFT, 1536 carriers at 1 kHz (DC unused), null symbol 2656 T, guard 504 T,
        /// 76 symbols (phase reference + 75 pi/4-DQPSK): 196608 samples, 96 ms.</summary>
        public static OfdmNumerology DabModeI { get; } = new()
        {
            Name = "DAB mode I",
            Reference = "ETSI EN 300 401 V2.2.1 cl. 14.2, 14.3.2 (tables 23, 24), 14.5",
            Nfft = 2048, SampleRateHz = 2.048e6, Carriers = 1536, DcUsed = false,
            CpPattern = [2656 - 2048, .. Repeat(504, 76)],
            OnPattern = [false, .. Enumerable.Repeat(true, 76)],
            DabDifferential = true, Modulation = QamOrder.Qpsk,
        };

        /// <summary>Mobile WiMAX 10 MHz DL PUSC: 1024 FFT at 11.2 MS/s, CP 128, 840 used carriers (DC unused), clusters of 14 with
        /// pilots at 4, 8 (even symbols) and 0, 12 (odd), boosted 2.5 dB (4/3), 64QAM.</summary>
        public static OfdmNumerology Wimax10 { get; } = new()
        {
            Name = "WiMAX 10 MHz PUSC",
            Reference = "IEEE 802.16-2009 cl. 8.4.6.1.2.1 (DL PUSC) from the published profile; unverified",
            Nfft = 1024, SampleRateHz = 11.2e6, Carriers = 840, DcUsed = false, CpPattern = Repeat(128, 2),
            PilotPitch = 14, PilotPositions = [[4, 8], [0, 12]], PilotAmplitude = 4.0 / 3,
            Sequence = PilotSequence.Wimax,
        };

        public static OfdmNumerology[] All => [DvbT2K, DvbT8K, DvbT2, IsdbT, DabModeI, Wimax10];
    }
}
