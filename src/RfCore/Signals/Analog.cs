// ================================================================
//  Analog.cs  —  analog modulation and the VHF/UHF navigation
//  composites, as a receiver or its front end sees them
//
//  The message is a sum of tones (frequency, level, phase each) or
//  Gaussian noise flat from 1/T to a set bandwidth (seeded), built in
//  the frequency domain on the record's 1/T grid and scaled to a peak
//  of 1 on the record. Every line sits on a bin, so the record is
//  periodic and every operation below is exact over it:
//    AM      1 + m·msg, m the depth (0 to 1), the carrier kept
//    DSB-SC  msg
//    SSB     msg ± j·H{msg}: the analytic signal, made by clearing the
//            message spectrum's negative bins and doubling its positive
//            ones (USB; LSB is its conjugate). On a periodic record
//            whose lines are bins this IS the Hilbert transform, so the
//            other sideband is zero to rounding.
//    FM      exp(j 2π Δf ∫msg): the integral taken bin by bin
//            (divide by j 2π f), so it closes over the record; Δf the
//            peak deviation, or Index × the top message frequency
//    PM      exp(j φpk msg)
//
//  Navigation composites, ICAO Annex 10 Vol. I, 7th edition (2018):
//    VOR (3.3.5)    AM 30 % by the 30 Hz reference, and 30 % by a
//                   9960 Hz subcarrier FM ±480 Hz (index 16) by the
//                   30 Hz variable signal, which leads the reference by
//                   the bearing: the Doppler-VOR form; a conventional
//                   VOR swaps which tone is called reference, the
//                   receiver reads the same phase difference.
//    ILS LOC (3.1.3.5) and GS (3.1.5.5)  AM by 90 and 150 Hz,
//                   zero-crossing together every 1/30 s; SDM = m90 +
//                   m150 (0.40 LOC, 0.80 GS on course), DDM = m90 − m150.
//    Ident (3.3.6, 3.1.3.9)  a 1020 Hz tone at 10 % depth keyed in
//                   Morse at 7 words per minute (VOR, LOC). The record
//                   holds the code once plus a word gap.
//  DME pulse pairs (3.5) are pulsed, not analog: they are not made here.
//
//  The record is whole 100 ms (it holds the 30, 90, 150, 1020 and
//  9960 Hz tones whole) and the native rate a multiple of 10 Hz, at
//  4x the occupied bandwidth (Carson's 2(Δf + fm) for FM and PM). The
//  record is normalised to unit mean power. Measure reads it back.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace RfCore.Signals
{
    public enum AnalogKind { Am, DsbSc, Usb, Lsb, Fm, Pm, Vor, Loc, Gs }

    /// <summary>One message tone: frequency (Hz, rounded to the record's 1/T grid), level (dB over the others) and phase
    /// (degrees, of a cosine).</summary>
    public sealed record AnalogTone(double FrequencyHz, double LevelDb = 0, double PhaseDeg = 0);

    /// <summary>An analog carrier. <paramref name="Tones"/> null = one 1 kHz tone; <paramref name="NoiseBandwidthHz"/>
    /// &gt; 0 = Gaussian noise to that bandwidth instead (seeded by <paramref name="Seed"/>). <paramref name="Depth"/> is
    /// the AM depth m (0 to 1); <paramref name="DeviationHz"/> the FM peak deviation unless <paramref name="Index"/> sets
    /// it as Index × the top message frequency; <paramref name="PeakPhaseRad"/> the PM peak phase. VOR: <paramref
    /// name="BearingDeg"/>; LOC and GS: <paramref name="Ddm"/> and <paramref name="Sdm"/> (null = 0.40 LOC, 0.80 GS);
    /// VOR and LOC: <paramref name="Ident"/> in Morse ("" = none). <paramref name="RecordSeconds"/> is rounded up to
    /// whole 100 ms, and lengthened to hold the ident.</summary>
    public sealed record AnalogShape(AnalogKind Kind, AnalogTone[]? Tones = null, double NoiseBandwidthHz = 0, ulong Seed = 1,
        double Depth = Analog.DefaultDepth, double DeviationHz = Analog.DefaultDeviationHz, double? Index = null,
        double PeakPhaseRad = 1, double BearingDeg = 0, double Ddm = 0, double? Sdm = null, string Ident = "",
        double RecordSeconds = Analog.Step);

    /// <summary>What <see cref="Analog.Measure"/> reads back from a record; NaN where it does not apply. MessageHz and
    /// MessageLevel are the demodulated message's strongest line (level against the message's peak of 1); Depth is the
    /// AM depth (VOR: the 30 Hz AM's), DeviationHz the FM peak deviation (VOR: the subcarrier's).</summary>
    public sealed record AnalogReading(double MessageHz, double MessageLevel, double Depth, double DeviationHz,
        double PeakPhaseRad, double BearingDeg, double Ddm, double Sdm);

    /// <summary>A preset: its name, a representative centre frequency (Hz) and the carrier.</summary>
    public sealed record AnalogPreset(string Name, double CenterHz, AnalogShape Shape);

    public static class Analog
    {
        public const double DefaultToneHz = 1e3, DefaultDepth = 0.3, DefaultDeviationHz = 5e3;
        /// <summary>The record is whole steps of 100 ms, the native rate whole 10 Hz.</summary>
        public const double Step = 0.1;
        public const int Oversample = 4;

        // ICAO Annex 10 Vol. I (7th ed. 2018): VOR 3.3.5, ident 3.3.6, LOC 3.1.3.5, GS 3.1.5.5
        public const double VorToneHz = 30, VorSubcarrierHz = 9960, VorDeviationHz = 480, VorDepth = 0.3;
        public const double IlsLowHz = 90, IlsHighHz = 150, LocSdm = 0.4, GsSdm = 0.8;
        public const double IdentHz = 1020, IdentDepth = 0.1, IdentUnitSeconds = 1.2 / 7;   // 7 words per minute

        public static readonly AnalogPreset[] Presets =
        [
            new("AM broadcast", 1e6, new(AnalogKind.Am, Depth: 0.3)),
            new("FM broadcast", 98e6, new(AnalogKind.Fm, DeviationHz: 75e3)),
            new("NBFM 12.5 kHz", 446e6, new(AnalogKind.Fm, DeviationHz: 2.5e3)),
            new("NBFM 25 kHz", 156.8e6, new(AnalogKind.Fm, DeviationHz: 5e3)),
            new("SSB two-tone", 14.2e6, new(AnalogKind.Usb, [new(700), new(1900)])),
            new("VOR 45°", 113e6, new(AnalogKind.Vor, BearingDeg: 45)),
            new("ILS LOC", 110.1e6, new(AnalogKind.Loc)),
            new("ILS GS", 334.4e6, new(AnalogKind.Gs)),
        ];

        public static string Label(AnalogKind k) => k switch
        {
            AnalogKind.Am => "AM", AnalogKind.DsbSc => "DSB-SC", AnalogKind.Usb => "USB", AnalogKind.Lsb => "LSB",
            AnalogKind.Fm => "FM", AnalogKind.Pm => "PM", AnalogKind.Vor => "VOR", AnalogKind.Loc => "ILS LOC", _ => "ILS GS",
        };

        private static bool IsNav(AnalogKind k) => k is AnalogKind.Vor or AnalogKind.Loc or AnalogKind.Gs;

        private const string MorseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        private static readonly string[] MorseCodes =
            (".- -... -.-. -.. . ..-. --. .... .. .--- -.- .-.. -- -. --- .--. --.- .-. ... - ..- ...- .-- -..- -.-- --.. " +
             "----- .---- ..--- ...-- ....- ..... -.... --... ---.. ----.").Split(' ');

        /// <summary>The ident's keying, one entry per Morse unit: dot 1 on, dash 3, 1 off within a letter, 3 between
        /// letters, 7 after the word.</summary>
        public static bool[] MorseUnits(string ident)
        {
            var u = new List<bool>();
            foreach (char ch in ident)
            {
                foreach (char e in MorseCodes[MorseChars.IndexOf(ch)]) { u.AddRange(Enumerable.Repeat(true, e == '.' ? 1 : 3)); u.Add(false); }
                u.AddRange([false, false]);
            }
            if (u.Count > 0) u.AddRange([false, false, false, false]);
            return u.ToArray();
        }

        /// <summary>Refuses a carrier that cannot be generated, with a sentence naming the field; returns it with every
        /// default applied, the record rounded and tones on its grid, FM's Index turned into DeviationHz.</summary>
        public static AnalogShape Validate(AnalogShape s)
        {
            var inv = CultureInfo.InvariantCulture;
            string who = Label(s.Kind) + " carrier";
            if (!(s.RecordSeconds > 0) || !double.IsFinite(s.RecordSeconds))
                throw new FormatException($"{who}: Record must be a positive time (got {s.RecordSeconds.ToString(inv)} s)");
            string id = (s.Ident ?? "").ToUpperInvariant();
            if (id.Length > 0 && s.Kind is not (AnalogKind.Vor or AnalogKind.Loc))
                throw new FormatException($"{who}: Ident belongs to a VOR or ILS LOC carrier");
            if (id.Any(c => !MorseChars.Contains(c)))
                throw new FormatException($"{who}: Ident must be letters and digits (got '{s.Ident}')");
            double t = Math.Max(s.RecordSeconds, MorseUnits(id).Length * IdentUnitSeconds);
            t = Math.Max(1, Math.Ceiling(t / Step - 1e-9)) / 10;
            s = s with { Ident = id, RecordSeconds = t };

            if (IsNav(s.Kind))
            {
                if (!double.IsFinite(s.BearingDeg))
                    throw new FormatException($"{who}: Bearing must be a number of degrees");
                double sdm = s.Sdm ?? (s.Kind == AnalogKind.Gs ? GsSdm : LocSdm);
                if (s.Kind != AnalogKind.Vor && (!(sdm > 0) || sdm + (id.Length > 0 ? IdentDepth : 0) > 1 || !(Math.Abs(s.Ddm) <= sdm)))
                    throw new FormatException(string.Format(inv, "{0}: SDM must be above 0, with the ident at most 1, and |DDM| at most SDM (got SDM {1}, DDM {2})", who, sdm, s.Ddm));
                return Check(s with { Sdm = s.Kind == AnalogKind.Vor ? null : sdm, BearingDeg = ((s.BearingDeg % 360) + 360) % 360 }, who);
            }

            if (s.NoiseBandwidthHz != 0 && (!(s.NoiseBandwidthHz >= 1 / t) || !double.IsFinite(s.NoiseBandwidthHz)))
                throw new FormatException(string.Format(inv, "{0}: Noise bandwidth must be at least 1/Record, {1} Hz (got {2} Hz)", who, 1 / t, s.NoiseBandwidthHz));
            var tones = s.Tones ?? [new AnalogTone(DefaultToneHz)];
            if (s.NoiseBandwidthHz == 0)
            {
                if (tones.Length == 0) throw new FormatException($"{who}: the message needs a tone or a noise bandwidth");
                foreach (var tn in tones)
                    if (!(Math.Round(tn.FrequencyHz * t) >= 1) || !double.IsFinite(tn.FrequencyHz) || !double.IsFinite(tn.LevelDb) || !double.IsFinite(tn.PhaseDeg))
                        throw new FormatException(string.Format(inv, "{0}: a tone must be at least 1/Record, {1} Hz, with a finite level and phase (got {2} Hz)", who, 1 / t, tn.FrequencyHz));
                tones = tones.Select(tn => tn with { FrequencyHz = Math.Round(tn.FrequencyHz * t) / t }).ToArray();
            }
            s = s with { Tones = tones };
            if (s.Kind == AnalogKind.Am && !(s.Depth > 0 && s.Depth <= 1))
                throw new FormatException($"{who}: Depth must be above 0 and at most 1 (got {s.Depth.ToString(inv)})");
            if (s.Kind == AnalogKind.Fm)
            {
                if (s.Index is double ix) s = s with { DeviationHz = ix * TopMessageHz(s), Index = null };
                if (!(s.DeviationHz > 0) || !double.IsFinite(s.DeviationHz))
                    throw new FormatException($"{who}: the deviation (or Index) must be positive (got {s.DeviationHz.ToString(inv)} Hz)");
            }
            if (s.Kind == AnalogKind.Pm && (!(s.PeakPhaseRad > 0) || !double.IsFinite(s.PeakPhaseRad)))
                throw new FormatException($"{who}: the peak phase must be positive (got {s.PeakPhaseRad.ToString(inv)} rad)");
            return Check(s, who);
        }

        private static AnalogShape Check(AnalogShape s, string who)
        {
            long n = Samples(s);
            if (n > Fsk.MaxSamples)
                throw new FormatException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: a {1} s record at {2} kS/s is {3:N0} samples, over the {4:N0} a record may hold; shorten Record", who,
                    s.RecordSeconds, NativeRateHz(s) / 1e3, n, Fsk.MaxSamples));
            return s;
        }

        /// <summary>The message's top frequency: the highest tone or the noise bandwidth.</summary>
        public static double TopMessageHz(AnalogShape s)
            => s.NoiseBandwidthHz > 0 ? s.NoiseBandwidthHz : (s.Tones ?? [new AnalogTone(DefaultToneHz)]).Max(t => t.FrequencyHz);

        /// <summary>The occupied bandwidth: 2 fm for AM and DSB-SC, fm for SSB, Carson's 2(Δf + fm) for FM and PM (Δf =
        /// φpk fm), the VOR subcarrier's outer Carson edge times two, and twice the top tone for ILS. The preview's span,
        /// the envelope rate and the ACPR bands follow from it.</summary>
        public static double BandwidthHz(AnalogShape s) => s.Kind switch
        {
            AnalogKind.Usb or AnalogKind.Lsb => TopMessageHz(s),
            AnalogKind.Fm => 2 * (s.DeviationHz + TopMessageHz(s)),
            AnalogKind.Pm => 2 * (s.PeakPhaseRad + 1) * TopMessageHz(s),
            AnalogKind.Vor => 2 * (VorSubcarrierHz + VorDeviationHz + VorToneHz),
            AnalogKind.Loc or AnalogKind.Gs => 2 * (string.IsNullOrEmpty(s.Ident) ? IlsHighHz : IdentHz),
            _ => 2 * TopMessageHz(s),
        };

        private static int SamplesPerStep(AnalogShape s) => (int)Math.Ceiling(Oversample * BandwidthHz(s) * Step - 1e-9);
        public static double NativeRateHz(AnalogShape s) => SamplesPerStep(s) / Step;
        public static long Samples(AnalogShape s) => (long)Math.Round(s.RecordSeconds / Step) * SamplesPerStep(s);

        /// <summary>The frequency of bin <paramref name="k"/> of an <paramref name="n"/>-point record of T seconds.</summary>
        private static double BinHz(int k, int n, double t) => (k <= n / 2 ? k : k - n) / t;

        /// <summary>The message's spectrum (length n, conjugate-symmetric), scaled so the message peaks at 1.</summary>
        public static Complex[] MessageSpectrum(AnalogShape s, int n)
        {
            double t = s.RecordSeconds;
            var m = new Complex[n];
            if (s.NoiseBandwidthHz > 0)
            {
                ulong key = NoiseDraw.Key(s.Seed, NoiseDraw.Hash("analog message"));
                for (int k = 1; k <= (int)Math.Floor(s.NoiseBandwidthHz * t + 1e-9); k++) m[k] = NoiseDraw.Complex(key, k);
            }
            else
                foreach (var tn in s.Tones!)
                    m[(int)Math.Round(tn.FrequencyHz * t)] += Complex.FromPolarCoordinates(0.5 * n * Math.Pow(10, tn.LevelDb / 20), tn.PhaseDeg * Math.PI / 180);
            for (int k = 1; k < n / 2; k++) m[n - k] = Complex.Conjugate(m[k]);
            double peak = Fft.Inverse(m).Max(v => Math.Abs(v.Real));
            for (int k = 0; k < n; k++) m[k] /= peak;
            return m;
        }

        /// <summary>One record at <see cref="NativeRateHz"/>, unit mean power.</summary>
        public static Complex[] Generate(AnalogShape s)
        {
            s = Validate(s);
            int n = (int)Samples(s);
            double fs = NativeRateHz(s), t = s.RecordSeconds;
            Complex[] x;
            if (IsNav(s.Kind))
            {
                var key = MorseUnits(s.Ident);
                double b = s.BearingDeg * Math.PI / 180, sdm = s.Sdm ?? 0;
                x = new Complex[n];
                for (int i = 0; i < n; i++)
                {
                    double ti = i / fs, w = 2 * Math.PI * ti;
                    double am = s.Kind == AnalogKind.Vor
                        ? VorDepth * Math.Cos(w * VorToneHz)
                          + VorDepth * Math.Cos(w * VorSubcarrierHz + VorDeviationHz / VorToneHz * Math.Sin(w * VorToneHz + b))
                        : (sdm + s.Ddm) / 2 * Math.Sin(w * IlsLowHz) + (sdm - s.Ddm) / 2 * Math.Sin(w * IlsHighHz);
                    int u = (int)(ti / IdentUnitSeconds);
                    if (u < key.Length && key[u]) am += IdentDepth * Math.Sin(w * IdentHz);
                    x[i] = 1 + am;
                }
            }
            else
            {
                var m = MessageSpectrum(s, n);
                switch (s.Kind)
                {
                    case AnalogKind.Usb or AnalogKind.Lsb:
                        for (int k = 1; k < n; k++) m[k] = k < n / 2 ? 2 * m[k] : 0;
                        x = Fft.Inverse(m);
                        if (s.Kind == AnalogKind.Lsb) x = x.Select(Complex.Conjugate).ToArray();
                        break;
                    case AnalogKind.Fm:
                        for (int k = 1; k < n; k++) m[k] /= new Complex(0, 2 * Math.PI * BinHz(k, n, t));
                        x = Fft.Inverse(m).Select(v => Complex.FromPolarCoordinates(1, 2 * Math.PI * s.DeviationHz * v.Real)).ToArray();
                        break;
                    default:
                        var msg = Fft.Inverse(m);
                        x = msg.Select(v => (Complex)(s.Kind switch
                        {
                            AnalogKind.Am => 1 + s.Depth * v.Real,
                            AnalogKind.Pm => Complex.FromPolarCoordinates(1, s.PeakPhaseRad * v.Real),
                            _ => v.Real,
                        })).ToArray();
                        break;
                }
            }
            double rms = Math.Sqrt(x.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary));
            for (int i = 0; i < n; i++) x[i] /= rms;
            return x;
        }

        private static double[] UnwrappedPhase(ReadOnlySpan<Complex> z)
        {
            var p = new double[z.Length];
            for (int i = 0; i < z.Length; i++)
            {
                p[i] = z[i].Phase;
                if (i > 0) p[i] -= 2 * Math.PI * Math.Round((p[i] - p[i - 1]) / (2 * Math.PI));
            }
            return p;
        }

        /// <summary>Demodulates a record made by <see cref="Generate"/> for <paramref name="s"/> (any gain) and reads back the
        /// message and the modulation: a check that the generator does what its settings say.</summary>
        public static AnalogReading Measure(AnalogShape s, ReadOnlySpan<Complex> x)
        {
            s = Validate(s);
            int n = x.Length;
            double t = s.RecordSeconds, nan = double.NaN;
            int Bin(double hz) => (int)Math.Round(hz * t);
            var env = new Complex[n];
            for (int i = 0; i < n; i++) env[i] = x[i].Magnitude;
            double dc = env.Average(v => v.Real);

            if (IsNav(s.Kind))
            {
                var e = Fft.Forward(env);
                double Depth(double hz) => 2 * e[Bin(hz)].Magnitude / n / dc;
                if (s.Kind != AnalogKind.Vor)
                {
                    double m90 = Depth(IlsLowHz), m150 = Depth(IlsHighHz);
                    return new(nan, nan, nan, nan, nan, nan, m90 - m150, m90 + m150);
                }
                // the subcarrier's band, shifted to 0 Hz: its unwrapped phase is 16 sin(ω30 t + bearing)
                int ksc = Bin(VorSubcarrierHz), kb = Bin(1500);
                var z = new Complex[n];
                for (int d = -kb; d <= kb; d++) z[(d + n) % n] = e[ksc + d];
                var ph = Fft.Forward(UnwrappedPhase(Fft.Inverse(z)).Select(v => (Complex)v).ToArray())[Bin(VorToneHz)];
                double dev = 2 * ph.Magnitude / n * VorToneHz;
                double bearing = (ph.Phase + Math.PI / 2 - e[Bin(VorToneHz)].Phase) * 180 / Math.PI;
                return new(VorToneHz, nan, Depth(VorToneHz), dev, nan, ((bearing % 360) + 360) % 360, nan, nan);
            }

            double depth = nan, deviation = nan, peakPhase = nan;
            double[] msg;
            switch (s.Kind)
            {
                case AnalogKind.Am:
                    msg = env.Select(v => v.Real / dc - 1).ToArray();
                    depth = msg.Max(Math.Abs);
                    break;
                case AnalogKind.Fm:
                    var p = Fft.Forward(UnwrappedPhase(x).Select(v => (Complex)v).ToArray());
                    p[0] = 0;
                    for (int k = 1; k < n; k++) p[k] *= new Complex(0, BinHz(k, n, t));   // d/dt / 2π
                    msg = Fft.Inverse(p).Select(v => v.Real).ToArray();
                    deviation = msg.Max(Math.Abs);
                    break;
                case AnalogKind.Pm:
                    var ph = UnwrappedPhase(x);
                    double mean = ph.Average();
                    msg = ph.Select(v => v - mean).ToArray();
                    peakPhase = msg.Max(Math.Abs);
                    break;
                default:
                    msg = x.ToArray().Select(v => v.Real).ToArray();
                    break;
            }
            double peak = msg.Max(Math.Abs);
            var spec = Fft.Forward(msg.Select(v => (Complex)(v / peak)).ToArray());
            int top = Enumerable.Range(1, n / 2 - 1).MaxBy(k => spec[k].Magnitude);
            return new(top / t, 2 * spec[top].Magnitude / n, depth, deviation, peakPhase, nan, nan, nan);
        }
    }
}
