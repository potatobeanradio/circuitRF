// ================================================================
//  Multitone.cs  —  tones and noise: N-tone, notched multitone,
//  band-limited Gaussian noise (flat or notched), CW + AWGN, and the
//  noise power ratio (NPR) read from a received record
//
//  The record is built in the frequency domain on the grid 1 / record
//  and transformed once, so it is periodic and every tone sits exactly
//  on a bin. A tone is one bin of amplitude sqrt(p) at its phase; the
//  noise is a complex Gaussian (NoiseDraw: seeded, platform-independent)
//  on every bin of its band. A notch empties bins: the tones in it and
//  the noise bins in it, so the clean record's notch is empty to the
//  numerical floor of one transform.
//
//  Tone k (0-based, of N) sits at (k - (N - 1)/2) x spacing. Phases:
//    Zero        0 for every tone: the peak is 10 log N.
//    Newman      pi k^2 / N: a low PAPR (2.4-3.7 dB) for equal tones.
//    Schroeder   -2 pi sum_{l<k} (k - l) p_l, p the tones' powers over
//                their sum (empty tones 0): Schroeder's rule, which keeps
//                the PAPR low for any power profile; for equal tones it
//                is -pi k (k + 1) / N.
//    Random      uniform on [0, 2 pi), seeded.
//
//  The record is unit mean power. The native rate is N_fft / record,
//  N_fft the smallest power of two (at least 64) holding the occupied
//  span Oversample times over.
// ================================================================

using System;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public enum TonePhase { Zero, Newman, Schroeder, Random }

    /// <summary>A tones-and-noise record. Tones = 0 is noise alone; NoiseBandwidthHz = 0 is tones alone.</summary>
    public sealed record MultitoneShape
    {
        public int Tones { get; init; } = 2;
        public double SpacingHz { get; init; } = 1e6;
        public TonePhase Phase { get; init; } = TonePhase.Newman;
        /// <summary>Each tone's power (dB, relative); null = equal.</summary>
        public double[]? TonePowersDb { get; init; }
        /// <summary>Gaussian band-limited noise over this band, centred on 0; 0 = none.</summary>
        public double NoiseBandwidthHz { get; init; }
        /// <summary>With tones: the noise's total power over the tones' total (dB), -C/N for CW + AWGN.</summary>
        public double NoiseDb { get; init; }
        /// <summary>The notch: its centre and width (Hz); width 0 = none. Tones and noise bins inside it are empty.</summary>
        public double NotchCentreHz { get; init; }
        public double NotchWidthHz { get; init; }
        /// <summary>The notch as a list instead: tone indices (0-based) to leave empty.</summary>
        public int[]? NotchTones { get; init; }
        /// <summary>The record; null = 2 / spacing for tones (whole bins for odd and even N), 1 ms for noise alone.</summary>
        public double? RecordSeconds { get; init; }
        public ulong Seed { get; init; } = 1;
    }

    public static class Multitone
    {
        public const int MaxTones = 100_000;
        /// <summary>Samples per hertz of occupied span the native rate holds at least (4: peaks between samples stay small).</summary>
        public const int Oversample = 4;
        public const int MaxSamples = 1 << 24;

        public static double RecordSeconds(MultitoneShape s)
            => s.RecordSeconds ?? (s.Tones > 0 ? 2 / s.SpacingHz : 1e-3);

        /// <summary>Tone k's offset from the centre (Hz).</summary>
        public static double[] Offsets(MultitoneShape s)
            => [.. Enumerable.Range(0, s.Tones).Select(k => (k - (s.Tones - 1) / 2.0) * s.SpacingHz)];

        public static bool InNotch(MultitoneShape s, double f)
            => s.NotchWidthHz > 0 && Math.Abs(f - s.NotchCentreHz) < s.NotchWidthHz / 2;

        /// <summary>Each tone's linear power, the notched tones 0, before normalisation.</summary>
        public static double[] Powers(MultitoneShape s)
        {
            var off = Offsets(s);
            var p = new double[s.Tones];
            for (int k = 0; k < s.Tones; k++)
                p[k] = InNotch(s, off[k]) || (s.NotchTones?.Contains(k) ?? false) ? 0
                     : s.TonePowersDb is { } db ? Math.Pow(10, db[k] / 10) : 1;
            return p;
        }

        /// <summary>Each tone's starting phase (rad).</summary>
        public static double[] Phases(MultitoneShape s)
        {
            int n = s.Tones;
            var ph = new double[n];
            switch (s.Phase)
            {
                case TonePhase.Newman:
                    for (int k = 0; k < n; k++) ph[k] = Math.PI * ((double)k * k % (2.0 * n)) / n;
                    break;
                case TonePhase.Schroeder:
                {
                    var p = Powers(s);
                    double tot = p.Sum();
                    if (tot <= 0) break;
                    // phi_k = -2 pi sum_{l<k} (k - l) p_l, so phi_{k+1} = phi_k - 2 pi sum_{l<=k} p_l
                    double run = 0, phi = 0;
                    for (int k = 0; k < n; k++)
                    {
                        ph[k] = phi;
                        run += p[k] / tot;
                        phi = (phi - 2 * Math.PI * run) % (2 * Math.PI);
                    }
                    break;
                }
                case TonePhase.Random:
                {
                    ulong key = NoiseDraw.Key(s.Seed, NoiseDraw.Hash("multitone-phase"));
                    for (int k = 0; k < n; k++) ph[k] = 2 * Math.PI * NoiseDraw.Uniform(key, k);
                    break;
                }
            }
            return ph;
        }

        /// <summary>The occupied span, centred on 0 (Hz): the tones' extent plus a spacing, or the noise band.</summary>
        public static double SpanHz(MultitoneShape s)
        {
            double tones = s.Tones > 0 ? s.Tones * s.SpacingHz : 0;
            return Math.Max(tones, s.NoiseBandwidthHz);
        }

        /// <summary>The carrier's channel bandwidth: the span, or one spacing for a lone tone.</summary>
        public static double BandwidthHz(MultitoneShape s) => SpanHz(s);

        public static int Length(MultitoneShape s)
        {
            double rec = RecordSeconds(s);
            int n = 64;
            while (n < Oversample * SpanHz(s) * rec) n <<= 1;
            return n;
        }

        public static double NativeRateHz(MultitoneShape s) => Length(s) / RecordSeconds(s);

        /// <summary>Refuses a record that cannot be built, with a sentence naming the field.</summary>
        public static MultitoneShape Validate(MultitoneShape s)
        {
            if (s.Tones < 0 || s.Tones > MaxTones) throw new FormatException($"tones: N must be 0 to {MaxTones} (got {s.Tones})");
            if (s.Tones == 0 && !(s.NoiseBandwidthHz > 0)) throw new FormatException("tones: no tones and no noise band");
            if (s.Tones > 0 && !(s.SpacingHz > 0 && double.IsFinite(s.SpacingHz))) throw new FormatException("tones: Spacing must be a positive frequency");
            if (!(s.NoiseBandwidthHz >= 0 && double.IsFinite(s.NoiseBandwidthHz))) throw new FormatException("tones: NoiseBW must be 0 or a positive frequency");
            if (!(s.NotchWidthHz >= 0)) throw new FormatException("tones: the notch width must be 0 or positive");
            if (s.TonePowersDb is { } db && db.Length != s.Tones)
                throw new FormatException($"tones: {db.Length} powers for {s.Tones} tones");
            if (s.NotchTones?.Any(k => k < 0 || k >= s.Tones) == true) throw new FormatException("tones: a notched tone index is outside 0 .. N-1");
            double rec = RecordSeconds(s);
            if (!(rec > 0 && double.IsFinite(rec))) throw new FormatException("tones: Record must be a positive time");
            foreach (double f in Offsets(s))
                if (Math.Abs(f * rec - Math.Round(f * rec)) > 1e-6)
                    throw new FormatException($"tones: a tone at {f:R} Hz is not on the record's 1 / Record grid; make Record a multiple of 2 / Spacing");
            if (Oversample * SpanHz(s) * rec > MaxSamples) throw new FormatException("tones: the record needs more than 2^24 samples; shorten Record");
            return s;
        }

        /// <summary>The record at <see cref="NativeRateHz"/>, unit mean power.</summary>
        public static Complex[] Generate(MultitoneShape s)
        {
            Validate(s);
            double rec = RecordSeconds(s);
            int n = Length(s);
            var X = new Complex[n];
            int Bin(double f) => (int)(((long)Math.Round(f * rec) % n + n) % n);
            double tonesTotal = 0;
            if (s.Tones > 0)
            {
                var off = Offsets(s);
                var p = Powers(s);
                var ph = Phases(s);
                for (int k = 0; k < s.Tones; k++)
                    if (p[k] > 0) X[Bin(off[k])] += Complex.FromPolarCoordinates(Math.Sqrt(p[k]), ph[k]);
                tonesTotal = p.Sum();
            }
            if (s.NoiseBandwidthHz > 0)
            {
                long half = (long)Math.Floor(s.NoiseBandwidthHz / 2 * rec + 1e-9);
                double noiseTotal = s.Tones > 0 ? tonesTotal * Math.Pow(10, s.NoiseDb / 10) : 1;
                double sigma = Math.Sqrt(noiseTotal / (2 * half + 1));   // per bin, before the notch empties some
                ulong key = NoiseDraw.Key(s.Seed, NoiseDraw.Hash("multitone-noise"));
                for (long k = -half; k <= half; k++)
                    if (!InNotch(s, k / rec)) X[Bin(k / rec)] += sigma * NoiseDraw.Complex(key, k);
            }
            var x = Fft.Inverse(X);
            OfdmGenerator.Normalise(x);
            return x;
        }

        /// <summary>
        /// The noise power ratio (dB) of a received record <paramref name="y"/> at <paramref name="fsHz"/>: the power
        /// density in the band (centre and width) outside the notch, over the density in the notch's middle half (clear of
        /// the notch's edges, where a filter's or a window's skirt lands). <paramref name="nseg"/> = 0 reads one
        /// rectangular transform of the whole of <paramref name="y"/>, exact for whole periodic records; otherwise
        /// <see cref="SignalMeasure.Welch"/> with that segment.
        /// </summary>
        public static double NprDb(ReadOnlySpan<Complex> y, double fsHz, double bandCentreHz, double bandwidthHz,
                                   double notchCentreHz, double notchWidthHz, int nseg = 0)
        {
            double[] f, psd;
            if (nseg > 0) (f, psd) = SignalMeasure.Welch(y, fsHz, nseg);
            else
            {
                var Y = Fft.Forward(y);
                int n = Y.Length;
                f = new double[n]; psd = new double[n];
                for (int k = 0; k < n; k++) { f[k] = (k < (n + 1) / 2 ? k : k - n) * fsHz / n; psd[k] = Y[k].Real * Y[k].Real + Y[k].Imaginary * Y[k].Imaginary; }
            }
            double inSum = 0, notchSum = 0; int inN = 0, notchN = 0;
            for (int i = 0; i < f.Length; i++)
            {
                double dn = Math.Abs(f[i] - notchCentreHz);
                if (dn <= notchWidthHz / 4) { notchSum += psd[i]; notchN++; }
                else if (dn >= notchWidthHz / 2 && Math.Abs(f[i] - bandCentreHz) <= bandwidthHz / 2) { inSum += psd[i]; inN++; }
            }
            if (inN == 0 || notchN == 0) throw new ArgumentException("NPR: the band or the notch holds no frequency bin at this rate and length");
            return 10 * Math.Log10(inSum / inN / Math.Max(notchSum / notchN, 1e-300));
        }
    }
}
