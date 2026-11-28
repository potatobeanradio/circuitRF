// ================================================================
//  ModulatedSignal.cs  —  a generated multi-carrier signal
//
//  Holds every carrier SEPARATELY at its native rate (the envelope
//  engine gives widely spaced carriers their own fundamental and needs
//  each carrier's own narrow baseband), with relative powers applied
//  so the composite has unit mean power.  Any carrier, or the
//  composite, can be requested at any rate whose product with the
//  record length is a whole number of samples: resampling is exact
//  (band-limited) because every record is periodic.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace RfCore.Signals
{
    public sealed class CarrierRecord
    {
        public required CarrierSpec Spec { get; init; }
        /// <summary>Periodic baseband at <see cref="NativeRateHz"/>, scaled to the carrier's share of the unit total.</summary>
        public required Complex[] Native { get; init; }
        public required double NativeRateHz { get; init; }
        public required double OccupiedBwHz { get; init; }
        /// <summary>Carrier offset snapped to the record's frequency grid (1/record) so the composite stays periodic.</summary>
        public required double OffsetHz { get; init; }
        public double RelativePower { get; init; } = 1;
    }

    public sealed class ModulatedSignal
    {
        public SignalSpec Spec { get; }
        public IReadOnlyList<CarrierRecord> Carriers { get; }
        public double RecordSeconds { get; }

        private readonly ConcurrentDictionary<(int, long), Complex[]> _resampled = new();

        public ModulatedSignal(SignalSpec spec, IReadOnlyList<CarrierRecord> carriers, double recordSeconds)
        {
            Spec = spec; Carriers = carriers; RecordSeconds = recordSeconds;
        }

        public double InstantaneousBandwidthHz
        {
            get
            {
                double lo = Carriers.Min(c => c.OffsetHz - c.Spec.BandwidthHz / 2);
                double hi = Carriers.Max(c => c.OffsetHz + c.Spec.BandwidthHz / 2);
                return hi - lo;
            }
        }

        /// <summary>Samples in the record at <paramref name="rateHz"/>; throws if not a whole number.</summary>
        public int LengthAt(double rateHz)
        {
            double n = RecordSeconds * rateHz;
            long r = (long)Math.Round(n);
            if (Math.Abs(n - r) > 1e-6 * Math.Max(1, n))
                throw new ArgumentException($"rate {rateHz / 1e6:0.###} MHz does not give a whole number of samples over the {RecordSeconds * 1e3:0.###} ms record");
            return (int)r;
        }

        /// <summary>Carrier <paramref name="index"/> (at its own centre, no offset) resampled to <paramref name="rateHz"/>.</summary>
        public Complex[] CarrierAt(int index, double rateHz)
        {
            int n = LengthAt(rateHz);
            return _resampled.GetOrAdd((index, n), _ => Fft.ResamplePeriodic(Carriers[index].Native, n));
        }

        /// <summary>
        /// Sum of the carriers in <paramref name="indices"/> (all when null), each shifted by its offset minus
        /// <paramref name="centreHz"/>, at <paramref name="rateHz"/>.
        /// </summary>
        public Complex[] Composite(double rateHz, double centreHz = 0, IReadOnlyList<int>? indices = null)
        {
            int n = LengthAt(rateHz);
            var y = new Complex[n];
            foreach (int i in indices ?? Enumerable.Range(0, Carriers.Count).ToList())
            {
                var x = CarrierAt(i, rateHz);
                double w = 2 * Math.PI * (Carriers[i].OffsetHz - centreHz) / rateHz;
                for (int k = 0; k < n; k++)
                    y[k] += x[k] * Complex.FromPolarCoordinates(1.0, w * k);
            }
            return y;
        }

        /// <summary>
        /// A WINDOW of the composite: <paramref name="count"/> samples at <paramref name="rateHz"/> starting at record
        /// time <paramref name="startSample"/>/rate (any integer, negative or past the end: the record is periodic),
        /// each carrier shifted by its offset minus <paramref name="centreHz"/>. Interpolated from the carriers' native
        /// records with a Kaiser-windowed sinc (±<see cref="InterpHalfWidth"/> native samples, ~-100 dB stopband), so a
        /// short run never pays for resampling the whole record — the cost is O(count x taps), not O(record log record).
        /// </summary>
        public Complex[] CompositeWindow(double rateHz, double centreHz, long startSample, int count, IReadOnlyList<int>? indices = null)
        {
            var y = new Complex[count];
            foreach (int ci in indices ?? Enumerable.Range(0, Carriers.Count).ToList())
            {
                var c = Carriers[ci];
                var x = c.Native;
                int n = x.Length;
                double ratio = c.NativeRateHz / rateHz;
                double w = 2 * Math.PI * (c.OffsetHz - centreHz) / rateHz;
                double periodSamples = RecordSeconds * rateHz;
                bool exact = Math.Abs(ratio - Math.Round(ratio)) < 1e-12 && Math.Abs(ratio - 1) < 1e-12;
                for (int m = 0; m < count; m++)
                {
                    long gi = startSample + m;
                    double u = gi * ratio;                                     // position in native samples
                    u -= Math.Floor(u / n) * n;
                    Complex v;
                    if (exact) v = x[(int)Math.Round(u) % n];
                    else
                    {
                        int k0 = (int)Math.Floor(u);
                        double frac = u - k0;
                        if (frac < 1e-12) v = x[k0 % n];
                        else
                        {
                            v = Complex.Zero;
                            for (int j = -InterpHalfWidth + 1; j <= InterpHalfWidth; j++)
                            {
                                double d = frac - j;                            // u - (k0 + j)
                                double s = Math.Sin(Math.PI * d) / (Math.PI * d);
                                double kw = Kaiser(d / InterpHalfWidth);
                                int idx = ((k0 + j) % n + n) % n;
                                v += x[idx] * (s * kw);
                            }
                        }
                    }
                    // carrier offset; phase reduced modulo the record so long runs keep full precision
                    double ph = w * (gi - Math.Floor(gi / periodSamples) * periodSamples);
                    y[m] += v * Complex.FromPolarCoordinates(1.0, ph);
                }
            }
            return y;
        }

        public const int InterpHalfWidth = 24;
        private const double KaiserBeta = 10.0;
        private static readonly double KaiserNorm = BesselI0(KaiserBeta);

        private static double Kaiser(double r)
        {
            if (r <= -1 || r >= 1) return 0;
            return BesselI0(KaiserBeta * Math.Sqrt(1 - r * r)) / KaiserNorm;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 60; k++)
            {
                term *= q / (k * k);
                sum += term;
                if (term < 1e-17 * sum) break;
            }
            return sum;
        }

        /// <summary>Smallest 1.92 MHz x 2^k rate holding the composite plus +-2 channel bandwidths of regrowth (an
        /// imported file's own rate x 2^k when that ladder does not divide its record — <see cref="SignalRate"/>).</summary>
        public double SuggestedCompositeRateHz()
        {
            double lo = Carriers.Min(c => c.OffsetHz - 2.5 * c.Spec.BandwidthHz);
            double hi = Carriers.Max(c => c.OffsetHz + 2.5 * c.Spec.BandwidthHz);
            double need = 2 * Math.Max(Math.Abs(lo), Math.Abs(hi));
            double r = 1.92e6;
            while (r < need) r *= 2;
            r = Math.Max(r, Carriers.Max(c => c.NativeRateHz));
            // a narrowband signal (DBPSK at 100 b/s needs ~1 kHz) steps at its own rate, below the ladder's base
            double own = Math.Max(need, Carriers.Max(c => c.NativeRateHz));
            if (own < SignalRate.LadderBaseHz && SignalRate.AtLeast(own, [this]) is var low && low < r) return low;
            return SignalRate.Divides(this, r) ? r : SignalRate.AtLeast(r, [this]);
        }
    }

    /// <summary>
    /// The rate a periodic record is stepped at. Every generated OFDM record lasts whole 0.5 ms slots, which the LTE / NR
    /// ladder (1.92 MHz x 2^k) always divides. An imported I/Q file lasts N / fs, which that ladder need not divide
    /// (12,345 samples at 100 MS/s); its own rate x 2^k always does, giving N x 2^k whole samples. An FSK record is one
    /// data period, 511 bits / Rb for PN9, and is the same case: its native rate x 2^k always divides it; as do a DBPSK
    /// record and a CSS packet.
    ///
    /// A signal that needs less than the ladder's base (a 100 b/s DBPSK uplink needs ~3 kHz) steps at its own rate
    /// x 2^k instead: at 1.92 MHz its 10 s record would be 20 million envelope steps.
    /// </summary>
    public static class SignalRate
    {
        public const double LadderBaseHz = 1.92e6;

        /// <summary>The smallest ladder rate at or above <paramref name="needHz"/> that gives every signal a whole number
        /// of samples per record; failing that, the smallest imported file or FSK native rate x 2^k that does; failing
        /// both (two files at unrelated rates), the ladder rate, which the caller refuses as it always has.</summary>
        public static double AtLeast(double needHz, IReadOnlyCollection<ModulatedSignal> signals)
        {
            var fileRates = signals.SelectMany(s => s.Carriers)
                                   .Where(c => c.Spec.Standard is CarrierStandard.File or CarrierStandard.Fsk
                                                                 or CarrierStandard.Dbpsk or CarrierStandard.Css or CarrierStandard.Sc
                                                                 or CarrierStandard.Wlan or CarrierStandard.Gnss or CarrierStandard.Wcdma
                                                                 or CarrierStandard.Wlan11b or CarrierStandard.Oqpsk154 or CarrierStandard.Analog
                                                                 or CarrierStandard.Tones)
                                   .Select(c => c.NativeRateHz).Distinct().OrderBy(r => r).ToList();
            if (needHz < LadderBaseHz)
                foreach (double fs in fileRates)
                {
                    double r = Climb(fs, needHz);
                    if (r < LadderBaseHz && signals.All(s => Divides(s, r))) return r;
                }
            double ladder = Climb(LadderBaseHz, needHz);
            if (signals.All(s => Divides(s, ladder))) return ladder;
            foreach (double fs in fileRates)
            {
                double r = Climb(fs, needHz);
                if (signals.All(s => Divides(s, r))) return r;
            }
            return ladder;
        }

        /// <summary>True when <paramref name="rateHz"/> gives <paramref name="signal"/> a whole number of samples per record.</summary>
        public static bool Divides(ModulatedSignal signal, double rateHz)
        {
            double n = signal.RecordSeconds * rateHz;
            return Math.Abs(n - Math.Round(n)) <= 1e-6 * Math.Max(1, n);
        }

        private static double Climb(double r, double needHz)
        {
            while (r < needHz) r *= 2;
            return r;
        }
    }

    public static class SignalComposer
    {
        public static ModulatedSignal Generate(SignalSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (spec.Carriers.Count == 0) throw new ArgumentException("a signal needs at least one carrier");
            var pulse = PulseOf(spec);
            var fsk = FskOf(spec);
            var dbpsk = DbpskOf(spec);
            var css = CssOf(spec);
            var sc = ScOf(spec);
            var wlan = WlanOf(spec);
            var gnss = GnssOf(spec);
            var wcdma = WcdmaOf(spec);
            var w11b = Wlan11bOf(spec);
            var o154 = Oqpsk154Of(spec);
            var analog = AnalogOf(spec);
            var tones = TonesOf(spec);
            var file = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.File);
            IqFile? iq = file is null ? null : IqFile.Read(file.FilePath!, file.SampleRateHz);
            var fmcw = FmcwOf(spec);
            double record = (pulse is null ? (double?)null : PulsedCw.RecordSeconds(pulse)) ?? (fmcw is null ? (double?)null : fmcw.Period)
                            ?? fsk?.RecordSeconds ?? dbpsk?.RecordSeconds ?? css?.RecordSeconds ?? sc?.RecordSeconds
                            ?? (wlan is null ? (double?)null : Wlan.RecordSeconds(wlan)) ?? gnss?.RecordSeconds
                            ?? (wcdma is null ? (double?)null : Wcdma.RecordSeconds) ?? w11b?.RecordSeconds ?? o154?.RecordSeconds ?? analog?.RecordSeconds
                            ?? (tones is null ? (double?)null : Multitone.RecordSeconds(tones)) ?? iq?.DurationSeconds
                            ?? Math.Max(0.5e-3, Math.Round(spec.RecordSeconds / 0.5e-3) * 0.5e-3);
            int halfMs = (int)Math.Round(record / 0.5e-3);

            var raw = new List<(CarrierSpec s, Complex[] x, double rate, double occ)>();
            for (int i = 0; i < spec.Carriers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = spec.Carriers[i];
                switch (c.Standard)
                {
                    case CarrierStandard.Lte:
                    case CarrierStandard.Nr:
                    {
                        if (iq is not null && Math.Abs(iq.DurationSeconds / 0.5e-3 - Math.Round(iq.DurationSeconds / 0.5e-3)) > 1e-6)
                            throw new ArgumentException("an imported I/Q file mixed with generated carriers must last a whole number of 0.5 ms slots");
                        var nm = OfdmGenerator.Numerology(c);
                        var x = OfdmGenerator.Generate(c, halfMs, spec.WindowFraction, spec.ShapingFloorDbc);
                        if (spec.ClipPaprDb is double t)
                            OfdmGenerator.Clip(x, nm.SampleRateHz, nm.OccupiedBwHz, t);
                        raw.Add((c, x, nm.SampleRateHz, nm.OccupiedBwHz));
                        break;
                    }
                    case CarrierStandard.Cw:
                    {
                        double rate = 1.92e6;
                        int n = (int)Math.Round(record * rate);
                        var tone = c.PhaseDeg is double ph ? Complex.FromPolarCoordinates(1, ph * Math.PI / 180) : Complex.One;
                        raw.Add((c, Enumerable.Repeat(tone, n).ToArray(), rate, 0));
                        break;
                    }
                    case CarrierStandard.Pcw:
                    {
                        // Unit power on the flat top, never normalised to unit mean: Pavg states the pulse-top power.
                        var shape = pulse!;
                        double rate = PulsedCw.NativeRateHz(shape), bw = PulsedCw.BandwidthHz(shape);
                        raw.Add((c with { BandwidthHz = bw }, PulsedCw.Generate(shape, rate), rate, 0.9 * bw));
                        break;
                    }
                    case CarrierStandard.Fsk:
                    {
                        // constant envelope: unit power at every sample, so it is unit mean power as it stands. A burst is unit
                        // power while on (its switched DPSK payload unit mean power), as a pulse is on its top: Pavg is the on-power
                        var shape = fsk!;
                        double rate = Fsk.NativeRateHz(shape), bw = Fsk.BandwidthHz(shape);
                        raw.Add((c with { BandwidthHz = bw }, Fsk.Generate(shape, rate, ScQam.DpskPayload).Samples, rate, 0.9 * bw));
                        break;
                    }
                    case CarrierStandard.Dbpsk:
                    {
                        // normalised to unit mean power: the envelope dips at each phase reversal
                        var shape = dbpsk!;
                        double bw = Dbpsk.BandwidthHz(shape);
                        raw.Add((c with { BandwidthHz = bw }, Dbpsk.Generate(shape), Dbpsk.NativeRateHz(shape), 0.7 * bw));
                        break;
                    }
                    case CarrierStandard.Css:
                    {
                        // constant envelope, as FSK
                        var shape = css!;
                        raw.Add((c, Css.Generate(shape).Samples, Css.NativeRateHz(shape), 0.9 * shape.BandwidthHz));
                        break;
                    }
                    case CarrierStandard.Sc:
                    {
                        // unit mean power; clipped as an OFDM record is, over its occupied band Rs(1 + α)
                        var shape = sc!;
                        double rate = ScQam.NativeRateHz(shape), bw = ScQam.BandwidthHz(shape);
                        var x = ScQam.Generate(shape);
                        if (spec.ClipPaprDb is double t) OfdmGenerator.Clip(x, rate, bw, t);   // a burst's PAPR: against the record's mean
                        if (shape.Guard > 0)
                        {
                            // a burst is unit power over its frames, as a pulse is on its top: Pavg is the on-power
                            var w = ScQam.Window(shape);
                            double on = 0; int k = 0;
                            for (int j = 0; j < x.Length; j++) if (w[j] == 1) { on += x[j].Real * x[j].Real + x[j].Imaginary * x[j].Imaginary; k++; }
                            double a = 1 / Math.Sqrt(on / k);
                            for (int j = 0; j < x.Length; j++) x[j] *= a;
                        }
                        raw.Add((c with { BandwidthHz = bw }, x, rate, bw));
                        break;
                    }
                    case CarrierStandard.Wlan:
                    {
                        // unit power over the packet and zeros in the gap, as a pulse is unit power on its top: Pavg is the
                        // packet's power, and the record's mean is Pavg plus 10 log10 of the duty (the label states it)
                        var shape = wlan!;
                        raw.Add((c, Wlan.Generate(shape), Wlan.SampleRateHz(shape), Wlan.Numerology(shape).OccupiedBwHz));
                        break;
                    }
                    case CarrierStandard.Gnss:
                    {
                        // unit mean power, the satellites and the noise together; the noise fills the native band
                        var shape = gnss!;
                        double bw = Gnss.BandwidthHz(shape);
                        raw.Add((c with { BandwidthHz = bw }, Gnss.Generate(shape), Gnss.NativeRateHz(shape), 0.9 * bw));
                        break;
                    }
                    case CarrierStandard.Wcdma:
                    {
                        // one 10 ms frame, unit mean power; clipped as an OFDM record is, over its RRC band
                        var (model, code) = wcdma!.Value;
                        double rate = Wcdma.NativeRateHz, occ = Wcdma.ChipRateHz * (1 + Wcdma.RollOff);
                        var x = Wcdma.Generate(model, code);
                        if (spec.ClipPaprDb is double t) OfdmGenerator.Clip(x, rate, occ, t);
                        raw.Add((c with { BandwidthHz = Wcdma.ChannelBandwidthHz }, x, rate, occ));
                        break;
                    }
                    case CarrierStandard.Wlan11b:
                        // one frame, back to back with the next, unit mean power
                        raw.Add((c with { BandwidthHz = Ieee80211b.ChannelBandwidthHz }, Ieee80211b.Generate(w11b!),
                                 Ieee80211b.NativeRateHz, Ieee80211b.ChipRateHz * (1 + Ieee80211b.RollOff)));
                        break;
                    case CarrierStandard.Oqpsk154:
                        // one frame of half-sine chips: constant envelope, unit power
                        raw.Add((c with { BandwidthHz = Ieee802154.ChannelBandwidthHz }, Ieee802154.Generate(o154!),
                                 Ieee802154.NativeRateHz, 1.2 * Ieee802154.ChipRateHz));
                        break;
                    case CarrierStandard.Fmcw:
                    {
                        // unit magnitude throughout: Pavg is its power at every instant
                        var ramp = fmcw!;
                        double rate = ramp.NativeRateHz();
                        raw.Add((c with { BandwidthHz = ramp.OccupiedBandwidthHz }, ramp.Generate(rate), rate, Math.Abs(ramp.BandwidthHz)));
                        break;
                    }
                    case CarrierStandard.Tones:
                    {
                        // unit mean power; Clip applies (noise has peaks) over the occupied span, and fills a notch as a
                        // transmitter's crest-factor reduction would
                        var shape = tones!;
                        double rate = Multitone.NativeRateHz(shape), occ = Multitone.SpanHz(shape);
                        var x = Multitone.Generate(shape);
                        if (spec.ClipPaprDb is double t) OfdmGenerator.Clip(x, rate, occ, t);
                        raw.Add((c with { BandwidthHz = Multitone.BandwidthHz(shape) }, x, rate, occ));
                        break;
                    }
                    case CarrierStandard.Analog:
                    {
                        // unit mean power at its own rate, as DBPSK's
                        var shape = analog!;
                        double bw = Analog.BandwidthHz(shape);
                        raw.Add((c with { BandwidthHz = bw }, Analog.Generate(shape), Analog.NativeRateHz(shape), bw));
                        break;
                    }
                    default:
                    {
                        var f = IqFile.Read(c.FilePath ?? throw new ArgumentException("a FILE carrier needs File=<path>"), c.SampleRateHz);
                        var x = (Complex[])f.Samples.Clone();
                        OfdmGenerator.Normalise(x);
                        // The carrier's bandwidth is the FILE's, never the 20 MHz a CarrierSpec defaults to: the envelope
                        // rate, the preview's span and the ACLR bands are all read off it.
                        double bw = f.ChannelBandwidthHz > 0 ? f.ChannelBandwidthHz : f.EstimateChannelBandwidthHz();
                        double occ = 0.9 * bw;
                        if (spec.ClipPaprDb is double t)
                            OfdmGenerator.Clip(x, f.SampleRateHz, occ, t);
                        raw.Add((c with { BandwidthHz = bw }, x, f.SampleRateHz, occ));
                        break;
                    }
                }
                progress?.Report((i + 1.0) / spec.Carriers.Count);
            }

            double totalLin = raw.Sum(r => Math.Pow(10, r.s.PowerDb / 10));
            var recs = raw.Select(r =>
            {
                double share = Math.Pow(10, r.s.PowerDb / 10) / totalLin;
                double a = Math.Sqrt(share);
                var x = r.x.Select(v => v * a).ToArray();
                double grid = 1.0 / record;
                return new CarrierRecord
                {
                    Spec = r.s, Native = x, NativeRateHz = r.rate, OccupiedBwHz = r.occ,
                    OffsetHz = Math.Round(r.s.OffsetHz / grid) * grid, RelativePower = share,
                };
            }).ToList();
            return new ModulatedSignal(spec, recs, record);
        }

        /// <summary>The pulse of a pCW signal (null when there is none), refused when a pulsed carrier shares the source
        /// with any other carrier — a pulse beside a modulated carrier has no clear power or record — or is clipped.</summary>
        public static PulseShape? PulseOf(SignalSpec spec)
        {
            var pcw = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.Pcw);
            if (pcw is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException("a pulsed-CW (PCW) carrier must be the source's only carrier");
            if (spec.ClipPaprDb is not null)
                throw new ArgumentException("a pulsed-CW (PCW) carrier is not clipped: remove Clip from the source");
            return PulsedCw.Check(SignalSpec.ResolvePulse(pcw));
        }

        /// <summary>The FSK carrier's settings (null when there is none), refused — as a pulse is — when it shares the
        /// source with another carrier, since its record is one period of its data and no other carrier's, or when it is
        /// clipped: its envelope is constant, so there is no peak to clip.</summary>
        public static FskShape? FskOf(SignalSpec spec)
        {
            var c = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.Fsk);
            if (c is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException("an FSK carrier must be the source's only carrier");
            if (spec.ClipPaprDb is not null)
                throw new ArgumentException("an FSK carrier has a constant envelope and is not clipped: remove Clip from the source");
            return Fsk.Validate(c);
        }

        /// <summary>The DBPSK carrier's settings (null when there is none), refused as an FSK carrier is: its record is
        /// its data's, so it is the only carrier, and it is not clipped.</summary>
        public static DbpskShape? DbpskOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Dbpsk, "a DBPSK carrier", "its envelope only dips at a phase reversal");
            return c is null ? null : Dbpsk.Validate(c);
        }

        /// <summary>The CSS carrier's settings (null when there is none): its record is one packet, so it is the only
        /// carrier, and its envelope is constant, so it is not clipped.</summary>
        public static CssShape? CssOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Css, "a CSS carrier", "it has a constant envelope");
            return c is null ? null : Css.Validate(c);
        }

        /// <summary>The single carrier's settings (null when there is none): its record is its symbols', so it is the only
        /// carrier; unlike the other carriers with a record of their own it has peaks, and Clip applies to it.</summary>
        public static ScShape? ScOf(SignalSpec spec)
        {
            var c = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.Sc);
            if (c is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException("an SC carrier must be the source's only carrier");
            return ScQam.Validate(c);
        }

        /// <summary>The Wi-Fi packet (null when there is none): its record is one packet and its gap, so it is the only carrier;
        /// it is gated, so a PAPR target against the record's mean would count the gap, and it is not clipped.</summary>
        public static WlanSpec? WlanOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Wlan, "a Wi-Fi (WLAN) carrier", "the packet is gated, so the record's PAPR counts its gap");
            return c is null ? null : Wlan.Validate(Wlan.Resolve(c));
        }

        /// <summary>The GNSS carrier (null when there is none): its record is whole frames of its codes, so it is the only
        /// carrier, and it is not clipped.</summary>
        public static GnssShape? GnssOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Gnss, "a GNSS carrier", "it is far below its noise, which has no peak worth cutting");
            return c is null ? null : Gnss.Validate(Gnss.Resolve(c));
        }

        /// <summary>The WCDMA carrier's model and code (null when there is none): its record is one frame, so it is the only
        /// carrier; a test model's channels sum to peaks, and Clip applies to it, as to SC.</summary>
        public static (WcdmaModel Model, int Code)? WcdmaOf(SignalSpec spec)
        {
            var c = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.Wcdma);
            if (c is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException("a WCDMA carrier must be the source's only carrier");
            return Wcdma.Resolve(c);
        }

        /// <summary>The 802.11b frame (null when there is none): the only carrier, and not clipped.</summary>
        public static Wlan11bShape? Wlan11bOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Wlan11b, "an 802.11b (WLAN11B) carrier", "its chips are its mask's, not a PAPR target's");
            return c is null ? null : Ieee80211b.Resolve(c);
        }

        /// <summary>The 802.15.4 frame (null when there is none): the only carrier, and of constant envelope, so not clipped.</summary>
        public static Oqpsk154Shape? Oqpsk154Of(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Oqpsk154, "an 802.15.4 (OQPSK154) carrier", "it has a constant envelope");
            return c is null ? null : Ieee802154.Resolve(c);
        }

        /// <summary>The analog carrier (null when there is none): its record is whole 100 ms of its message, so it is the only
        /// carrier, and it is not clipped (a clipped AM is a distorted message, not a PAPR target).</summary>
        public static AnalogShape? AnalogOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Analog, "an ANALOG carrier", "its envelope is its message");
            return c is null ? null : Analog.Validate(Analog.Resolve(c));
        }

        /// <summary>The tones-and-noise carrier (null when there is none): its record is its own grid's, so it is the only
        /// carrier; Clip applies to it.</summary>
        public static MultitoneShape? TonesOf(SignalSpec spec)
        {
            var c = spec.Carriers.FirstOrDefault(c => c.Standard == CarrierStandard.Tones);
            if (c is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException("a tones-and-noise (TONES) carrier must be the source's only carrier");
            return Multitone.Validate(SignalSpec.ResolveTones(c));
        }

        /// <summary>The FMCW ramp (null when there is none): its record is one period, so it is the only carrier, and its
        /// envelope is constant, so it is not clipped.</summary>
        public static Fmcw? FmcwOf(SignalSpec spec)
        {
            var c = OwnRecordCarrier(spec, CarrierStandard.Fmcw, "an FMCW carrier", "it has a constant envelope");
            return c is null ? null : SignalSpec.ResolveFmcw(c).Check();
        }

        private static CarrierSpec? OwnRecordCarrier(SignalSpec spec, CarrierStandard standard, string what, string whyNoClip)
        {
            var c = spec.Carriers.FirstOrDefault(c => c.Standard == standard);
            if (c is null) return null;
            if (spec.Carriers.Count > 1)
                throw new ArgumentException($"{what} must be the source's only carrier");
            if (spec.ClipPaprDb is not null)
                throw new ArgumentException($"{what} is not clipped ({whyNoClip}): remove Clip from the source");
            return c;
        }

        /// <summary>True when the signal is one carrier whose record is its own — a pulse train, FSK, DBPSK, CSS or SC — and
        /// not the record length the source states.</summary>
        public static bool HasOwnRecord(SignalSpec spec)
            => PulseOf(spec) is not null || FskOf(spec) is not null || DbpskOf(spec) is not null || CssOf(spec) is not null
               || ScOf(spec) is not null || WlanOf(spec) is not null || GnssOf(spec) is not null
               || WcdmaOf(spec) is not null || Wlan11bOf(spec) is not null || Oqpsk154Of(spec) is not null
               || AnalogOf(spec) is not null || TonesOf(spec) is not null || FmcwOf(spec) is not null;
    }

    /// <summary>
    /// I/Q text import: the Waveform_Generator format (<c># sample_rate_hz &lt;fs&gt;</c>, optional
    /// <c># channel_bw_hz</c>, then one <c>I Q</c> line per sample) or any two-column I Q text with a
    /// <c># sample_rate_hz</c> header.
    /// </summary>
    public sealed class IqFile
    {
        public required Complex[] Samples { get; init; }
        public required double SampleRateHz { get; init; }
        public double ChannelBandwidthHz { get; init; }
        public double DurationSeconds => Samples.Length / SampleRateHz;

        /// <summary>
        /// A channel bandwidth for a file that does not state one (<c># channel_bw_hz</c>): twice the furthest edge of
        /// the band holding 99 % of the power, over 0.9 — the occupied fraction every FILE carrier is measured with.
        /// Twice the furthest edge rather than the band's width, because the carrier sits at its offset from Fc and an
        /// off-centre file still has to fit in offset +- bandwidth / 2. At most the sample rate.
        /// </summary>
        public double EstimateChannelBandwidthHz()
        {
            var (f, psd) = SignalMeasure.Welch(Samples, SampleRateHz, 4096);
            double tot = psd.Sum(), tail = 0.005 * tot, acc = 0;
            int a = 0, b = psd.Length - 1;
            for (int i = 0; i < psd.Length; i++) { acc += psd[i]; if (acc >= tail) { a = i; break; } }
            acc = 0;
            for (int i = psd.Length - 1; i >= 0; i--) { acc += psd[i]; if (acc >= tail) { b = i; break; } }
            double edge = Math.Max(Math.Abs(f[a]), Math.Abs(f[b])) + SampleRateHz / psd.Length;
            return Math.Min(2 * edge / 0.9, SampleRateHz);
        }

        /// <summary>
        /// Writes <paramref name="samples"/> in the format <see cref="Read"/> takes: <paramref name="comments"/> as
        /// <c>#</c> lines, <c># sample_rate_hz</c>, optional <c># channel_bw_hz</c>, <c># samples</c>, <c># columns I Q</c>,
        /// then one <c>I Q</c> line per sample
        /// (9 significant digits, invariant culture), so an exported signal imports back as a FILE carrier.
        /// </summary>
        public static void Write(string path, ReadOnlySpan<Complex> samples, double sampleRateHz, double channelBwHz = 0,
                                 IEnumerable<string>? comments = null, IProgress<double>? progress = null,
                                 CancellationToken ct = default)
        {
            var inv = CultureInfo.InvariantCulture;
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
            foreach (var c in comments ?? Array.Empty<string>()) w.WriteLine("# " + c);
            w.WriteLine("# sample_rate_hz " + sampleRateHz.ToString("R", inv));
            if (channelBwHz > 0) w.WriteLine("# channel_bw_hz " + channelBwHz.ToString("R", inv));
            w.WriteLine("# samples " + samples.Length.ToString(inv));
            w.WriteLine("# columns I Q");
            int step = Math.Max(1, samples.Length / 100);
            for (int i = 0; i < samples.Length; i++)
            {
                w.Write(samples[i].Real.ToString("G9", inv));
                w.Write(' ');
                w.WriteLine(samples[i].Imaginary.ToString("G9", inv));
                if (i % step == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report((double)i / samples.Length);
                }
            }
            progress?.Report(1);
        }

        /// <summary>The <c># sample_rate_hz</c> and <c># channel_bw_hz</c> headers (0 when absent) and the number of
        /// samples, without keeping the samples: what the Signal Designer shows when a file is loaded.</summary>
        public static (double SampleRateHz, double ChannelBandwidthHz, int Samples) ReadHeader(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"I/Q file not found: {path}", path);
            double fs = 0, bw = 0;
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '#' || t[0] == '%')
                {
                    var p = t.TrimStart('#', '%').Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && p[0].Equals("sample_rate_hz", StringComparison.OrdinalIgnoreCase))
                        double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fs);
                    else if (p.Length >= 2 && p[0].Equals("channel_bw_hz", StringComparison.OrdinalIgnoreCase))
                        double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bw);
                    continue;
                }
                if (t.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2) n++;
            }
            return (fs, bw, n);
        }

        /// <summary>Reads <paramref name="path"/>; <paramref name="sampleRateHz"/>, when given, is the rate the samples
        /// were taken at and wins over the file's header. Without either the file is refused, never given a rate.</summary>
        public static IqFile Read(string path, double? sampleRateHz = null)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"I/Q file not found: {path}", path);
            double fs = 0, bw = 0;
            var list = new List<Complex>(1 << 16);
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '#' || t[0] == '%')
                {
                    var p = t.TrimStart('#', '%').Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 2 && p[0].Equals("sample_rate_hz", StringComparison.OrdinalIgnoreCase))
                        fs = double.Parse(p[1], CultureInfo.InvariantCulture);
                    else if (p.Length >= 2 && p[0].Equals("channel_bw_hz", StringComparison.OrdinalIgnoreCase))
                        bw = double.Parse(p[1], CultureInfo.InvariantCulture);
                    continue;
                }
                var q = t.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (q.Length < 2) continue;
                list.Add(new Complex(double.Parse(q[0], CultureInfo.InvariantCulture), double.Parse(q[1], CultureInfo.InvariantCulture)));
            }
            if (sampleRateHz is double given)
            {
                if (!(given > 0)) throw new FormatException($"{Path.GetFileName(path)}: the sample rate must be positive");
                fs = given;
            }
            if (fs <= 0) throw new FormatException($"{Path.GetFileName(path)}: no '# sample_rate_hz' header line, and no sample rate given (Fs=)");
            if (list.Count == 0) throw new FormatException($"{Path.GetFileName(path)}: no I Q samples");
            return new IqFile { Samples = list.ToArray(), SampleRateHz = fs, ChannelBandwidthHz = bw };
        }
    }
}
