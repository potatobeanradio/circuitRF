// ================================================================
//  Gnss.cs  —  GNSS ranging signals as a receiver front end sees them:
//  several satellites at once, each far below the thermal noise
//
//    GPS L1 C/A   BPSK(1): 1023-chip Gold codes at 1.023 Mcps (1 ms),
//                 G1 = 1 + x^3 + x^10, G2 = 1 + x^2 + x^3 + x^6 + x^8
//                 + x^9 + x^10, the PRN's G2 phase by its two taps;
//                 50 b/s data. 1575.42 MHz. IS-GPS-200N, Table 3-Ia.
//    GPS L5       QPSK of I5 and Q5 at 10.23 Mcps (10230 chips, 1 ms):
//                 XA (1 + x^9 + x^10 + x^12 + x^13, short-cycled at
//                 8190) plus XB (1 + x + x^3 + x^4 + x^6 + x^7 + x^8
//                 + x^12 + x^13) advanced the PRN's XB code advance;
//                 I5 carries 100 sps symbols under the 10-bit
//                 Neuman-Hofman code, Q5 (the pilot) the 20-bit one.
//                 1176.45 MHz. IS-GPS-705J, Table 3-Ia.
//    Galileo E1   CBOC(6,1,1/11) of E1-B (data, 250 sps, one symbol
//                 per 4 ms code) and E1-C (pilot, secondary code
//                 CS25_1): (1/√2)[e_B(α sc_A + β sc_B) − e_C(α sc_A −
//                 β sc_B)], α = √(10/11), β = √(1/11), sc_A and sc_B
//                 sine-phased square waves at 1.023 and 6.138 MHz.
//                 1575.42 MHz. Galileo OS SIS ICD, Issue 2.1. The
//                 primary codes are 4092-chip memory codes the ICD lists
//                 in hexadecimal; here each is a FIXED pseudo-random
//                 code of the same length and rate, drawn from the PRN
//                 and the channel: the spectrum, the correlation and the
//                 C/N0 are those of the real signal, the chips are not.
//                 The E1-A (PRS) component is not public and not made.
//    BeiDou B1I   BPSK(2): 2046-chip truncated Gold codes at 2.046 Mcps
//                 (1 ms), G1 = 1 + X + X^7 + X^8 + X^9 + X^10 + X^11,
//                 G2 = 1 + X + X^2 + X^3 + X^4 + X^5 + X^8 + X^9 + X^11,
//                 both from 01010101010, the PRN's G2 phase by its two
//                 or three taps; MEO/IGSO satellites send D1 (50 b/s
//                 under the 20-bit NH code), GEO satellites (PRN 1-5 and
//                 59-63) D2 (500 b/s). 1561.098 MHz. BDS-SIS-ICD-B1I-3.0,
//                 Table 4-1.
//  The data bits are random, from the seed (no frame structure: a front
//  end responds to the chips, not the message). The quadrature
//  components that are not open (L1 P(Y), B1Q) are not made, so L1 C/A,
//  E1 and B1I are real.
//
//  The record holds whole frames (one 20 ms data bit, or one 4 ms code
//  for E1) and is periodic, as every carrier's is. Each satellite has
//  its PRN, a Doppler offset (rounded to a whole number of cycles per
//  record: a 50 Hz grid for 20 ms), a code phase (chips, rounded to
//  the sample) and a power relative to the reference (0 dB) satellite;
//  its carrier phase is drawn from the seed. The chips are rectangular
//  at a whole number of samples (the native rate), and the code is not
//  stretched by the Doppler (3 ppm at 5 kHz: 0.06 chips over 20 ms).
//  With C/N0 set, white Gaussian noise over the native band is added at
//  the N0 that gives the reference satellite that C/N0. The record is
//  then normalised to unit mean power, signal and noise together.
//
//  The measurement is a receiver's acquisition, not an EVM: the Doppler
//  wiped off, the window correlated against the PRN's replica one code
//  period at a time (coherent) and the periods summed in power
//  (non-coherent, so a data bit or secondary chip costs nothing); the
//  code phase is the peak's lag; the C/N0 is ρ / T per component, ρ the
//  peak's power over the mean power off the peak and T the code period,
//  summed over the components (data and pilot).
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public enum GnssSignal { GpsL1Ca, GpsL5, GalileoE1, BeidouB1I }

    /// <summary>A signal's fixed facts: carrier (Hz), chip rate (chips/s), chips per code, code period (ms), samples per
    /// chip at the native rate, the highest PRN, the frame a record is a whole number of (ms), and its ICD.</summary>
    public sealed record GnssBand(string Name, double CarrierHz, double ChipRateHz, int CodeChips, int CodeMs, int SamplesPerChip,
                                  int MaxPrn, int FrameMs, string Reference)
    {
        public double NativeRateHz => SamplesPerChip * ChipRateHz;
        /// <summary>Samples in one code period at the native rate.</summary>
        public int CodeSamples => CodeChips * SamplesPerChip;
    }

    /// <summary>One satellite in view.</summary>
    /// <param name="Prn">PRN, 1 to the signal's <see cref="GnssBand.MaxPrn"/>.</param>
    /// <param name="DopplerHz">Carrier offset, Hz; rounded to a whole number of cycles per record.</param>
    /// <param name="CodePhaseChips">Where its code epoch sits in the record, chips; rounded to the sample.</param>
    /// <param name="PowerDb">Its power over the reference satellite's, dB.</param>
    public sealed record GnssSatellite(int Prn, double DopplerHz = 0, double CodePhaseChips = 0, double PowerDb = 0);

    /// <summary>A GNSS carrier: the signal, the satellites in view, the reference satellite's C/N0 (dB-Hz; null = no
    /// noise), the record (ms) and the seed of the data, carrier phases and noise.</summary>
    public sealed record GnssShape(GnssSignal Signal, GnssSatellite[] Satellites, double? Cn0DbHz = null,
                                   int RecordMs = Gnss.DefaultRecordMs, ulong Seed = 1)
    {
        public GnssBand Band => Gnss.Band(Signal);
        public double RecordSeconds => RecordMs * 1e-3;
        public int Samples => RecordMs / Band.CodeMs * Band.CodeSamples;
    }

    /// <summary>One PRN's acquisition: the code phase found (chips), the peak's correlation magnitude over the window's
    /// (1 for a clean lone satellite), the peak's power over the mean off it (dB), and the C/N0 estimate (dB-Hz).</summary>
    public sealed record GnssCorrelation(int Prn, double CodePhaseChips, double Peak, double PeakToFloorDb, double Cn0DbHz);

    public static class Gnss
    {
        public const int DefaultRecordMs = 20;
        public const double MaxDopplerHz = 50e3;

        public static GnssBand Band(GnssSignal s) => s switch
        {
            GnssSignal.GpsL1Ca => new("GPS L1 C/A", 1575.42e6, 1.023e6, 1023, 1, 16, 32, 20, "IS-GPS-200N"),
            GnssSignal.GpsL5 => new("GPS L5", 1176.45e6, 10.23e6, 10230, 1, 4, 37, 20, "IS-GPS-705J"),
            GnssSignal.GalileoE1 => new("Galileo E1", 1575.42e6, 1.023e6, 4092, 4, 24, 50, 4, "Galileo OS SIS ICD Issue 2.1"),
            _ => new("BeiDou B1I", 1561.098e6, 2.046e6, 2046, 1, 8, 63, 20, "BDS-SIS-ICD-B1I-3.0"),
        };

        public static double NativeRateHz(GnssShape s) => s.Band.NativeRateHz;

        /// <summary>A GNSS carrier's shape: each field it states, the default (GPS L1 C/A, PRN 1, no noise, 20 ms) for the rest.</summary>
        public static GnssShape Resolve(CarrierSpec c)
            => new(c.GnssSignal ?? GnssSignal.GpsL1Ca, ParseSatellites(c.Satellites) ?? [new GnssSatellite(1)], c.Cn0DbHz,
                   c.RecordS is double r ? (int)Math.Round(r * 1e3) : DefaultRecordMs, (ulong)c.Seed);

        /// <summary><c>PRN@Doppler@code phase@power</c>, comma-separated; the Doppler a frequency (Hz, kHz), the power in dB.</summary>
        public static GnssSatellite[]? ParseSatellites(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var inv = CultureInfo.InvariantCulture;
            return [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(sv =>
            {
                var f = sv.Split('@', StringSplitOptions.TrimEntries);
                if (f.Length > 4 || !int.TryParse(f[0], NumberStyles.Integer, inv, out int prn))
                    throw new FormatException($"GNSS carrier: a satellite is PRN@Doppler@code phase@power (got '{sv}')");
                double Num(int i) => f.Length > i && f[i].Length > 0
                    ? double.TryParse(f[i].Replace("dB", "", StringComparison.Ordinal), NumberStyles.Float, inv, out double v) ? v
                      : throw new FormatException($"GNSS carrier: '{f[i]}' in satellite '{sv}' is not a number")
                    : 0;
                return new GnssSatellite(prn, f.Length > 1 && f[1].Length > 0 ? SignalSpec.ParseHz("Sats", f[1]) : 0, Num(2), Num(3));
            })];
        }

        /// <summary>The signal as a carrier's <c>Signal=</c> names it: L1CA, L5, E1, B1I.</summary>
        public static string SignalName(GnssSignal s) => s switch
        {
            GnssSignal.GpsL1Ca => "L1CA", GnssSignal.GpsL5 => "L5", GnssSignal.GalileoE1 => "E1", _ => "B1I",
        };

        public static GnssSignal ParseSignal(string v)
        {
            string t = v.Trim().Replace("/", "");
            foreach (var s in Enum.GetValues<GnssSignal>())
                if (SignalName(s).Equals(t, StringComparison.OrdinalIgnoreCase)) return s;
            throw new FormatException($"GNSS carrier: Signal must be L1CA, L5, E1 or B1I (got '{v}')");
        }

        /// <summary>The channel bandwidth: the main lobe, null to null — 2 Rc for BPSK, 4 Rc for E1's BOC(1,1) (its
        /// BOC(6,1) eleventh lies outside, at ±6.1 MHz). The preview's span and the ACPR bands follow from it.</summary>
        public static double BandwidthHz(GnssShape s) => (s.Signal == GnssSignal.GalileoE1 ? 4 : 2) * s.Band.ChipRateHz;

        /// <summary>Refuses a GNSS carrier that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static GnssShape Validate(GnssShape s)
        {
            var b = s.Band;
            var inv = CultureInfo.InvariantCulture;
            if (s.Satellites is null || s.Satellites.Length == 0)
                throw new FormatException($"GNSS carrier: {b.Name} needs at least one satellite");
            foreach (var sat in s.Satellites)
            {
                if (sat.Prn < 1 || sat.Prn > b.MaxPrn)
                    throw new FormatException($"GNSS carrier: {b.Name} PRN must be 1 to {b.MaxPrn} (got {sat.Prn})");
                if (!double.IsFinite(sat.DopplerHz) || Math.Abs(sat.DopplerHz) > MaxDopplerHz)
                    throw new FormatException($"GNSS carrier: PRN {sat.Prn} Doppler must be within ±{MaxDopplerHz / 1e3:0} kHz (got {sat.DopplerHz.ToString(inv)} Hz)");
                if (!double.IsFinite(sat.CodePhaseChips) || !double.IsFinite(sat.PowerDb))
                    throw new FormatException($"GNSS carrier: PRN {sat.Prn} code phase and power must be numbers");
            }
            if (s.Satellites.Select(x => x.Prn).Distinct().Count() < s.Satellites.Length)
                throw new FormatException("GNSS carrier: each satellite needs its own PRN");
            if (s.Cn0DbHz is double c && (!double.IsFinite(c) || c is < 0 or > 100))
                throw new FormatException($"GNSS carrier: C/N0 must be 0 to 100 dB-Hz (got {c.ToString(inv)})");
            if (s.RecordMs <= 0 || s.RecordMs % b.FrameMs != 0)
                throw new FormatException($"GNSS carrier: {b.Name} Record must be a whole number of {b.FrameMs} ms frames (got {s.RecordMs} ms)");
            long n = (long)s.RecordMs / b.CodeMs * b.CodeSamples;
            if (n > Fsk.MaxSamples)
                throw new FormatException(string.Format(inv, "GNSS carrier: a {0} ms {1} record is {2:N0} samples, over the {3:N0} a record may hold; shorten Record",
                    s.RecordMs, b.Name, n, Fsk.MaxSamples));
            return s;
        }

        /// <summary>The Doppler a record of <paramref name="recordSeconds"/> carries: whole cycles per record.</summary>
        public static double DopplerOnGrid(double dopplerHz, double recordSeconds) => Math.Round(dopplerHz * recordSeconds) / recordSeconds;

        // ---------------------------------------------------------------- codes

        // C/A G2 taps, PRN 1-32 (IS-GPS-200N Table 3-Ia)
        private static readonly int[][] CaTaps =
        [
            [2, 6], [3, 7], [4, 8], [5, 9], [1, 9], [2, 10], [1, 8], [2, 9], [3, 10], [2, 3], [3, 4], [5, 6], [6, 7], [7, 8], [8, 9], [9, 10],
            [1, 4], [2, 5], [3, 6], [4, 7], [5, 8], [6, 9], [1, 3], [4, 6], [5, 7], [6, 8], [7, 9], [8, 10], [1, 6], [2, 7], [3, 8], [4, 9],
        ];

        // L5 XB code advance in chips, PRN 1-37 (IS-GPS-705J Table 3-Ia)
        private static readonly int[] I5Advance =
        [
            266, 365, 804, 1138, 1509, 1559, 1756, 2084, 2170, 2303, 2527, 2687, 2930, 3471, 3940, 4132, 4332, 4924, 5343,
            5443, 5641, 5816, 5898, 5918, 5955, 6243, 6345, 6477, 6518, 6875, 7168, 7187, 7329, 7577, 7720, 7777, 8057,
        ];
        private static readonly int[] Q5Advance =
        [
            1701, 323, 5292, 2020, 5429, 7136, 1041, 5947, 4315, 148, 535, 1939, 5206, 5910, 3595, 5135, 6082, 6990, 3546,
            1523, 4548, 4484, 1893, 3961, 7106, 5299, 4660, 276, 4389, 3783, 1591, 1601, 749, 1387, 1661, 3210, 708,
        ];

        // B1I G2 taps, PRN 1-63 (BDS-SIS-ICD-B1I-3.0 Table 4-1)
        private static readonly int[][] B1iTaps =
        [
            [1, 3], [1, 4], [1, 5], [1, 6], [1, 8], [1, 9], [1, 10], [1, 11], [2, 7], [3, 4], [3, 5], [3, 6], [3, 8], [3, 9], [3, 10], [3, 11],
            [4, 5], [4, 6], [4, 8], [4, 9], [4, 10], [4, 11], [5, 6], [5, 8], [5, 9], [5, 10], [5, 11], [6, 8], [6, 9], [6, 10], [6, 11], [8, 9],
            [8, 10], [8, 11], [9, 10], [9, 11], [10, 11], [1, 2, 7], [1, 3, 4], [1, 3, 6], [1, 3, 8], [1, 3, 10], [1, 3, 11], [1, 4, 5], [1, 4, 9],
            [1, 5, 6], [1, 5, 8], [1, 5, 10], [1, 5, 11], [1, 6, 9], [1, 8, 9], [1, 9, 10], [1, 9, 11], [2, 3, 7], [2, 5, 7], [2, 7, 9], [3, 4, 5],
            [3, 4, 9], [3, 5, 6], [3, 5, 8], [3, 5, 10], [3, 5, 11], [3, 6, 9],
        ];

        private const string Nh10 = "0000110101", Nh20 = "00000100110101001110", Cs25 = "0011100000001010110110010";

        /// <summary>A Fibonacci shift register of <paramref name="m"/> stages (stage 1 first in <paramref name="init"/>),
        /// shifting from lower to higher stage, stage 1 fed the sum of the <paramref name="feedback"/> stages; each output
        /// is the sum of the <paramref name="outTaps"/> stages before the shift.</summary>
        private static byte[] Register(int m, int[] feedback, byte[] init, int n, int[] outTaps)
        {
            var s = (byte[])init.Clone();
            var o = new byte[n];
            for (int k = 0; k < n; k++)
            {
                int b = 0, f = 0;
                foreach (int t in outTaps) b ^= s[t - 1];
                foreach (int t in feedback) f ^= s[t - 1];
                o[k] = (byte)b;
                Array.Copy(s, 0, s, 1, m - 1);
                s[0] = (byte)f;
            }
            return o;
        }

        private static byte[] Ones(int m) => Enumerable.Repeat((byte)1, m).ToArray();

        /// <summary>A PRN's primary code as logic 0s and 1s, chip 1 first. <paramref name="channel"/> 1 is L5's Q5 or E1's
        /// E1-C; 0 the other (and the only one of L1 C/A and B1I).</summary>
        public static byte[] CodeBits(GnssSignal signal, int prn, int channel = 0)
        {
            switch (signal)
            {
                case GnssSignal.GpsL1Ca:
                {
                    var g1 = Register(10, [3, 10], Ones(10), 1023, [10]);
                    var g2 = Register(10, [2, 3, 6, 8, 9, 10], Ones(10), 1023, CaTaps[prn - 1]);
                    return [.. g1.Zip(g2, (a, b) => (byte)(a ^ b))];
                }
                case GnssSignal.GpsL5:
                {
                    var xa = Register(13, [9, 10, 12, 13], Ones(13), 8190, [13]);
                    var xb = Register(13, [1, 3, 4, 6, 7, 8, 12, 13], Ones(13), 8191, [13]);
                    int adv = (channel == 0 ? I5Advance : Q5Advance)[prn - 1];
                    return [.. Enumerable.Range(0, 10230).Select(k => (byte)(xa[k % 8190] ^ xb[(adv + k) % 8191]))];
                }
                case GnssSignal.GalileoE1:
                {
                    // ponytail: a fixed seeded stand-in for the ICD's hexadecimal memory codes; load the tables to match real chips
                    ulong key = NoiseDraw.Key(NoiseDraw.Hash("Galileo E1"), (ulong)(2 * prn + channel));
                    return [.. Enumerable.Range(0, 4092).Select(k => (byte)(NoiseDraw.Uniform(key, k) <= 0.5 ? 1 : 0))];
                }
                default:
                {
                    byte[] init = [0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0];
                    var g1 = Register(11, [1, 7, 8, 9, 10, 11], init, 2046, [11]);
                    var g2 = Register(11, [1, 2, 3, 4, 5, 8, 9, 11], init, 2046, B1iTaps[prn - 1]);
                    return [.. g1.Zip(g2, (a, b) => (byte)(a ^ b))];
                }
            }
        }

        /// <summary>One component of a satellite's signal: one code period's replica at the native rate (real, unit
        /// power, subcarrier included), its complex weight, the secondary code (±1 per code period, cyclic), and the data
        /// symbol length (ms; 0 = a pilot).</summary>
        private sealed record Component(double[] Replica, Complex Weight, double[] Secondary, int DataMs);

        private static double[] Pm(string bits) => [.. bits.Select(ch => ch == '1' ? -1.0 : 1.0)];

        private static double[] Replica(GnssBand b, byte[] code, Func<int, double> sub)
        {
            var r = new double[b.CodeSamples];
            for (int n = 0; n < r.Length; n++) r[n] = (1 - 2 * code[n / b.SamplesPerChip]) * sub(n % b.SamplesPerChip);
            return r;
        }

        private static Component[] Components(GnssSignal signal, int prn)
        {
            var b = Band(signal);
            double h = Math.Sqrt(0.5);
            switch (signal)
            {
                case GnssSignal.GpsL1Ca:
                    return [new(Replica(b, CodeBits(signal, prn), _ => 1), 1, [1], 20)];
                case GnssSignal.GpsL5:
                    return [new(Replica(b, CodeBits(signal, prn, 0), _ => 1), h, Pm(Nh10), 10),
                            new(Replica(b, CodeBits(signal, prn, 1), _ => 1), new Complex(0, h), Pm(Nh20), 0)];
                case GnssSignal.GalileoE1:
                {
                    // 24 samples a chip: sc_A one period (12 + 12), sc_B six (2 + 2 each)
                    double al = Math.Sqrt(10.0 / 11), be = Math.Sqrt(1.0 / 11);
                    double ScA(int m) => m < 12 ? 1 : -1;
                    double ScB(int m) => (m / 2) % 2 == 0 ? 1 : -1;
                    return [new(Replica(b, CodeBits(signal, prn, 0), m => al * ScA(m) + be * ScB(m)), h, [1], 4),
                            new(Replica(b, CodeBits(signal, prn, 1), m => al * ScA(m) - be * ScB(m)), -h, Pm(Cs25), 0)];
                }
                default:
                    bool geo = prn <= 5 || prn >= 59;
                    return [new(Replica(b, CodeBits(signal, prn), _ => 1), 1, geo ? [1] : Pm(Nh20), geo ? 2 : 20)];
            }
        }

        // ---------------------------------------------------------------- the record

        /// <summary>One record at the native rate, unit mean power: every satellite plus, with C/N0 set, the noise.</summary>
        public static Complex[] Generate(GnssShape s)
        {
            Validate(s);
            var b = s.Band;
            int n = s.Samples, len = b.CodeSamples, periods = n / len;
            var x = new Complex[n];
            foreach (var sat in s.Satellites)
            {
                var own = new Complex[n];
                var comps = Components(s.Signal, sat.Prn);
                for (int ci = 0; ci < comps.Length; ci++)
                {
                    var c = comps[ci];
                    ulong key = NoiseDraw.Key(s.Seed, (ulong)(16 * sat.Prn + ci));
                    for (int p = 0; p < periods; p++)
                    {
                        double sign = c.Secondary[p % c.Secondary.Length];
                        if (c.DataMs > 0 && NoiseDraw.Uniform(key, p * b.CodeMs / c.DataMs) <= 0.5) sign = -sign;
                        Complex w = c.Weight * sign;
                        for (int m = 0; m < len; m++) own[p * len + m] += w * c.Replica[m];
                    }
                }
                double amp = Math.Pow(10, sat.PowerDb / 20);
                int d = (int)(((long)Math.Round(sat.CodePhaseChips * b.SamplesPerChip) % n + n) % n);
                double cycles = Math.Round(sat.DopplerHz * s.RecordSeconds);
                double phi = 2 * Math.PI * NoiseDraw.Uniform(NoiseDraw.Key(s.Seed, 1000UL + (ulong)sat.Prn), 0);
                for (int m = 0; m < n; m++)
                {
                    int k = (m + d) % n;
                    x[k] += amp * own[m] * Complex.FromPolarCoordinates(1, phi + 2 * Math.PI * cycles * k / n);
                }
            }
            if (s.Cn0DbHz is double cn0)
            {
                // N0 against the reference satellite's unit power, over the native band
                double sigma = Math.Sqrt(Math.Pow(10, -cn0 / 10) * b.NativeRateHz);
                ulong key = NoiseDraw.Key(s.Seed, 0x6E5EUL);
                for (int m = 0; m < n; m++) x[m] += sigma * NoiseDraw.Complex(key, m);
            }
            double rms = Math.Sqrt(x.Average(v => v.Magnitude * v.Magnitude));
            for (int m = 0; m < n; m++) x[m] /= rms;
            return x;
        }

        // ---------------------------------------------------------------- the measurement

        /// <summary>Acquires <paramref name="prn"/> in <paramref name="x"/> (native rate, from a code epoch of the record
        /// on, whole code periods; a part period at the end is left out), at the Doppler the shape gives that PRN (0 when
        /// it is not in view).</summary>
        public static GnssCorrelation Correlate(GnssShape s, ReadOnlySpan<Complex> x, int prn)
        {
            var b = s.Band;
            if (prn < 1 || prn > b.MaxPrn) throw new ArgumentException($"{b.Name} PRN must be 1 to {b.MaxPrn} (got {prn})");
            int len = b.CodeSamples, periods = x.Length / len, n = periods * len;
            if (periods == 0) throw new ArgumentException($"a {b.Name} correlation needs at least one code period ({len} samples)");
            double fd = DopplerOnGrid(s.Satellites.FirstOrDefault(v => v.Prn == prn)?.DopplerHz ?? 0, s.RecordSeconds);
            var y = new Complex[n];
            for (int m = 0; m < n; m++) y[m] = x[m] * Complex.FromPolarCoordinates(1, -2 * Math.PI * fd * m / b.NativeRateHz);
            double rms = Math.Sqrt(y.Average(v => v.Magnitude * v.Magnitude));
            var refs = Components(s.Signal, prn).Select(c => Fft.Forward([.. c.Replica.Select(v => new Complex(v, 0))])).ToArray();

            // mean |correlation|² over the periods, per component and lag
            double[][] Profile(Complex[] z)
            {
                var acc = refs.Select(_ => new double[len]).ToArray();
                for (int p = 0; p < periods; p++)
                {
                    var Z = Fft.Forward(z.AsSpan(p * len, len));
                    for (int c = 0; c < refs.Length; c++)
                    {
                        var r = Fft.Inverse([.. Z.Zip(refs[c], (u, v) => u * Complex.Conjugate(v))]);
                        for (int l = 0; l < len; l++) acc[c][l] += r[l].Magnitude * r[l].Magnitude / periods;
                    }
                }
                return acc;
            }

            var first = Profile(y);
            int lag = 0;
            for (int l = 1; l < len; l++)
                if (first.Sum(a => a[l]) > first.Sum(a => a[lag])) lag = l;
            // again with the periods cut at the code epoch found, so no data bit or secondary chip falls inside one
            var aligned = Profile([.. Enumerable.Range(0, n).Select(m => y[(m + lag) % n])]);
            int guard = 2 * b.SamplesPerChip;
            double rho = 0, peak = 0, floor = 0;
            foreach (var a in aligned)
            {
                double f = 0;
                for (int l = guard; l <= len - guard; l++) f += a[l];
                f /= len - 2 * guard + 1;
                rho += (a[0] - f) / f;
                peak += a[0];
                floor += f;
            }
            double cn0 = 10 * Math.Log10(Math.Max(rho, 1e-30) / (b.CodeMs * 1e-3));
            return new(prn, (double)lag / b.SamplesPerChip, Math.Sqrt(peak) / (len * rms), 10 * Math.Log10(peak / floor), cn0);
        }

        // ---------------------------------------------------------------- the expected spectrum

        /// <summary>The signal's power spectral density at <paramref name="fHz"/> from the carrier, per Hz, unit total power:
        /// Tc sinc²(π f Tc) for BPSK(n) (L1 C/A, B1I, and L5's I5 and Q5 alike), (10/11) BOC(1,1) + (1/11) BOC(6,1) for E1,
        /// the sine-phased BOC(m,n) being Tc [tan(π f / 2fsc) sin(π f Tc) / (π f Tc)]².</summary>
        public static double Psd(GnssSignal signal, double fHz)
        {
            var b = Band(signal);
            double tc = 1 / b.ChipRateHz;
            double f = fHz + 1e-3;   // 1 mHz off: every 0/0 (DC, and odd multiples of fsc) is then its limit
            double sinc = Math.Sin(Math.PI * f * tc) / (Math.PI * f * tc);
            if (signal != GnssSignal.GalileoE1) return tc * sinc * sinc;
            double Boc(double fsc)
            {
                double t = Math.Tan(Math.PI * f / (2 * fsc)) * sinc;
                return tc * t * t;
            }
            return 10.0 / 11 * Boc(b.ChipRateHz) + 1.0 / 11 * Boc(6 * b.ChipRateHz);
        }
    }
}
