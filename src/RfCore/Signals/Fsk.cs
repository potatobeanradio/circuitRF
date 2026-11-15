// ================================================================
//  Fsk.cs  —  the FSK carrier: continuous-phase binary FSK, GFSK, GMSK
//
//  Each bit holds the instantaneous frequency at +Dev (a 1) or -Dev
//  (a 0) for one bit period; the phase is the running integral of it,
//  so it never jumps (CPFSK). A Gaussian filter of bandwidth-time
//  product BT on the frequency pulse makes it GFSK, and GMSK when the
//  modulation index h = 2 Dev / Rb is 0.5. The envelope is constant,
//  so the record is unit power everywhere and its PAPR is 0 dB.
//
//  The data is a PN sequence (PN9, PN15: ITU-T O.150 polynomials,
//  all-ones start) or a fixed bit pattern, and the record is ONE
//  period of it: 511 bits for PN9, the pattern's length otherwise.
//
//  Closing the record. The phase gathers pi h per 1 and loses it per
//  0, so over a record it gathers pi h (ones - zeros) — 0.9 pi for
//  PN9 at h = 0.9 — and a record repeated as it stands would jump by
//  that much at every seam. The residual (wrapped to +-pi) is taken
//  out as a linear phase ramp across the record: the whole carrier
//  moves by at most half a record bin, 1 / (2 x record) — 98 Hz for
//  PN9 at 100 kbps — which is the same snapping to the record's
//  frequency grid every carrier offset already gets.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    /// <summary>An FSK carrier's settings with every default applied: bit rate (b/s), peak deviation (Hz), the Gaussian
    /// filter's BT (0 = none: rectangular CPFSK) and the data name as written (PN9, PN15 or a pattern of 0s and 1s).</summary>
    public sealed record FskShape(double BitRate, double Deviation, double Bt, string Data)
    {
        /// <summary>Modulation index: 2 Dev / Rb.</summary>
        public double ModulationIndex => 2 * Deviation / BitRate;

        /// <summary>What the settings make: MSK / GMSK at h = 0.5, GFSK with a filter, FSK without.</summary>
        public string Kind => Math.Abs(ModulationIndex - 0.5) < 1e-9 ? (Bt > 0 ? "GMSK" : "MSK") : Bt > 0 ? "GFSK" : "FSK";

        public int Bits => Fsk.DataLength(Data);
        public double RecordSeconds => Bits / BitRate;
    }

    public static class Fsk
    {
        public const double DefaultBitRate = 100e3, DefaultDeviation = 45e3;
        public const string DefaultData = "PN9";

        /// <summary>Fewest samples per bit; more when the deviation needs them (<see cref="NativeRateHz"/>).</summary>
        public const int MinSamplesPerBit = 32;

        /// <summary>Most samples a record may hold (2^23, 128 MB of I/Q): PN15 at a low bit rate and a wide deviation
        /// would otherwise ask for gigabytes without saying so.</summary>
        public const int MaxSamples = 1 << 23;

        public static FskShape Resolve(CarrierSpec c)
            => new(c.BitRateBps ?? DefaultBitRate, c.DeviationHz ?? DefaultDeviation, c.GaussianBt ?? 0,
                   (c.Data ?? DefaultData).ToUpperInvariant());

        /// <summary>Refuses an FSK carrier that cannot be generated, with a sentence naming the field; returns the shape.</summary>
        public static FskShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            var inv = CultureInfo.InvariantCulture;
            if (!(s.BitRate > 0) || !double.IsFinite(s.BitRate))
                throw new FormatException($"FSK carrier: Rb must be a positive bit rate (got {s.BitRate.ToString(inv)} b/s)");
            if (!(s.Deviation > 0) || !double.IsFinite(s.Deviation))
                throw new FormatException($"FSK carrier: Dev must be a positive frequency deviation (got {s.Deviation.ToString(inv)} Hz)");
            if (!(s.Bt >= 0) || !double.IsFinite(s.Bt))
                throw new FormatException($"FSK carrier: BT must be positive, or 0 for no filter (got {s.Bt.ToString(inv)})");
            if (s.Data is not ("PN9" or "PN15") && (s.Data.Length == 0 || s.Data.Any(ch => ch is not ('0' or '1'))))
                throw new FormatException($"FSK carrier: Data must be PN9, PN15 or a pattern of 0s and 1s (got '{c.Data}')");
            double rate = NativeRateHz(s);
            double n = s.Bits * (rate / s.BitRate);
            if (n > MaxSamples)
                throw new FormatException(string.Format(inv,
                    "FSK carrier: Data {0} is {1:N0} bits, {2:N0} samples at {3:0.###} MS/s, over the {4:N0} a record may hold; choose PN9, a shorter pattern or a higher bit rate",
                    s.Data.Length > 12 ? s.Data[..12] + "…" : s.Data, s.Bits, n, rate / 1e6, MaxSamples));
            return s;
        }

        /// <summary>Carson's bandwidth for binary FSK, 2 (Dev + Rb): the carrier's channel width, from which the preview
        /// span, the envelope rate and the ACLR bands follow.</summary>
        public static double BandwidthHz(FskShape s) => 2 * (s.Deviation + s.BitRate);

        /// <summary>Rb x 2^k: at least <see cref="MinSamplesPerBit"/> samples per bit and eight times the bandwidth, so the
        /// slowly falling sidelobes of rectangular CPFSK are well down before the native Nyquist edge.</summary>
        public static double NativeRateHz(FskShape s)
        {
            double sps = MinSamplesPerBit;
            while (sps * s.BitRate < 8 * BandwidthHz(s)) sps *= 2;
            return sps * s.BitRate;
        }

        public static int DataLength(string data) => data switch
        {
            "PN9" => 511,
            "PN15" => 32767,
            _ => data.Length,
        };

        /// <summary>One period of the data as bits.</summary>
        public static byte[] DataBits(string data) => data switch
        {
            "PN9" => Prbs(9, 5),
            "PN15" => Prbs(15, 14),
            "PN23" => Prbs(23, 18),
            _ => data.Select(ch => (byte)(ch - '0')).ToArray(),
        };

        /// <summary>One period of the maximal-length sequence of x^<paramref name="order"/> + x^<paramref name="tap"/> + 1
        /// (ITU-T O.150), from the all-ones state.</summary>
        public static byte[] Prbs(int order, int tap)
        {
            int n = (1 << order) - 1, state = n;
            var bits = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int b = ((state >> (order - 1)) ^ (state >> (tap - 1))) & 1;
                bits[i] = (byte)b;
                state = ((state << 1) | b) & n;
            }
            return bits;
        }

        /// <summary>
        /// One data period at <paramref name="rateHz"/> (a whole number of samples per bit), unit magnitude everywhere,
        /// and the instantaneous frequency (Hz) it was integrated from. A Gaussian filter is applied in the frequency
        /// domain, H(f) = exp(-ln2 / 2 (f / BT Rb)^2), so the record stays exactly periodic with no truncated taps.
        /// </summary>
        public static (Complex[] Samples, double[] FrequencyHz) Generate(FskShape s, double rateHz)
        {
            var bits = DataBits(s.Data);
            int sps = (int)Math.Round(rateHz / s.BitRate);
            int n = bits.Length * sps;
            var f = new double[n];
            for (int i = 0; i < n; i++) f[i] = bits[i / sps] == 1 ? s.Deviation : -s.Deviation;
            if (s.Bt > 0)
            {
                var spec = Fft.Forward(f.Select(v => new Complex(v, 0)).ToArray());
                double b = s.Bt * s.BitRate;
                for (int k = 0; k < n; k++)
                {
                    double fk = (k <= n / 2 ? k : k - n) * rateHz / n;
                    spec[k] *= Math.Exp(-Math.Log(2) / 2 * (fk / b) * (fk / b));
                }
                var back = Fft.Inverse(spec);
                for (int i = 0; i < n; i++) f[i] = back[i].Real;
            }
            // phase before each sample; the residual over the record (wrapped to +-pi) is ramped out so the seam is smooth
            double total = 2 * Math.PI * f.Sum() / rateHz;
            double residual = total - 2 * Math.PI * Math.Round(total / (2 * Math.PI));
            var x = new Complex[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                x[i] = Complex.FromPolarCoordinates(1.0, phase - residual * i / n);
                phase += 2 * Math.PI * f[i] / rateHz;
            }
            return (x, f);
        }

        /// <summary>A bit rate as the card shows it: 100 kb/s, 32.768 kb/s, 1.2 Mb/s.</summary>
        public static string FormatBitRate(double bps)
        {
            var inv = CultureInfo.InvariantCulture;
            return bps >= 1e6 ? string.Format(inv, "{0:0.######} Mb/s", bps / 1e6)
                 : bps >= 1e3 ? string.Format(inv, "{0:0.######} kb/s", bps / 1e3)
                 : string.Format(inv, "{0:0.######} b/s", bps);
        }

        /// <summary>A frequency as a label reads it: 45 kHz, 1.2 MHz.</summary>
        public static string FormatHz(double hz)
        {
            var inv = CultureInfo.InvariantCulture;
            return Math.Abs(hz) >= 1e6 ? string.Format(inv, "{0:0.######} MHz", hz / 1e6)
                 : Math.Abs(hz) >= 1e3 ? string.Format(inv, "{0:0.######} kHz", hz / 1e3)
                 : string.Format(inv, "{0:0.######} Hz", hz);
        }
    }
}
