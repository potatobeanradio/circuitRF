// ================================================================
//  ScQam.cs  —  the single-carrier PSK / QAM / APSK carrier (brief PCH-5)
//
//  Symbols at Rs, shaped by a root-raised-cosine filter of roll-off α,
//  so the matched filter at the receiver makes the raised cosine and
//  every symbol instant is free of its neighbours.
//
//  The shaping is applied in the frequency domain over the whole
//  periodic record (the filter's span is the record): the spectrum is
//  exactly zero beyond Rs(1 + α)/2, and the matched-filtered record has
//  no intersymbol interference at all, to round-off. Span=n truncates
//  the impulse response to n symbols (n/2 either side, rectangular),
//  as a transmitter's FIR does; the receiver's matched filter is then
//  the same truncated filter, and the eye keeps the ISI it leaves.
//
//  Constellations, unit mean power, the label of a point being the
//  bits it carries, first bit first:
//    BPSK          0 -> +1, 1 -> -1
//    QPSK          Gray: first bit I, second Q, (±1 ± j)/√2
//    8PSK          Gray round the circle: the point at kπ/4 carries
//                  k ^ (k >> 1)
//    16/64/256QAM  per-axis Gray, the first half of the bits I, the
//                  second Q (the OFDM carriers' mapping)
//    16APSK        4 + 12 points on two rings, radii 1 : Rings
//                  (default 2.85), phase offsets Phases (default 45°,
//                  15°); the natural ring-wise mapping: labels 0-3 the
//                  inner ring counter-clockwise from its offset, 4-15
//                  the outer ring the same way
//    32APSK        4 + 12 + 16, radii 1 : Rings (default 2.84, 5.27),
//                  Phases (default 45°, 15°, 0°), the same mapping
//  APSK is normalised to the constellation's mean power (every point
//  equally likely), which is what its EVM is read against.
//
//  The record. The data (PN9, PN15, PN23 or a fixed pattern) is read
//  k bits a symbol, first bit first, cyclically; the record holds one
//  whole data period — lcm(period, k)/k symbols, k data periods when
//  k shares no factor with the period — capped at 65,536 data symbols
//  (PN23's period would be 8.4 million), or Symbols= of them. An
//  optional preamble of Preamble= known symbols starts the record, and
//  PilotBlock=L/P puts L known symbols after every P data symbols
//  (none after the last). Known symbols are QPSK, (±1 ± j)/√2, read
//  from PN9 two bits a symbol in order through the record. The record
//  is whole symbols and periodic, as every carrier's is.
//
//  Options, each off unless set (the record is then the above, sample
//  for sample):
//    Pulse         RRC (above), RC, Gaussian (rectangular pulse through
//                  a Gaussian filter of bandwidth-time product BT), Rect,
//                  HalfSine (cos(πt/T), |t| < T/2) or LinearGmsk, the
//                  linearised GMSK pulse C0 of 3GPP TS 45.004 (8PSK
//                  clause): Laurent's main pulse of GMSK at BT 0.3, 5
//                  symbols long. Rect, HalfSine and LinearGmsk are
//                  sampled taps, the rest whole-record responses.
//    Rotation      degrees each symbol turns by from the one before,
//                  over the whole record: 90 is π/2-BPSK (and the
//                  π/2-QPSK/16QAM of IEEE 802.11 clause 20), 45 on QPSK
//                  π/4-QPSK, 67.5 on 8PSK the 3π/8-8PSK of TS 45.004.
//    Differential  each point is the previous one times the label's
//                  point (BPSK, QPSK, 8PSK): QPSK gives π/4-DQPSK (steps
//                  ±π/4, ±3π/4), 8PSK D8PSK. Known symbols then step by
//                  labels read from PN9, k bits each.
//    Offset        Q half a symbol after I (OQPSK); HalfSine makes it
//                  MSK-like, of constant envelope.
//    SOQPSK-TG     a modulation of its own (IRIG 106, Chapter 2): the
//                  bits, I and Q alternately, ternary-precoded and sent
//                  as continuous phase of index 1/2 with the TG
//                  frequency pulse (ρ 0.7, B 1.25, 8 bits long); Rs is
//                  half the bit rate, as for OQPSK.
//    Frames        FrameData=F cuts the data into frames of F symbols,
//                  each with its own preamble (a header) and pilot
//                  blocks: DVB-S2's PL frames, 802.11ad's guard
//                  intervals. Guard=G symbol periods of silence follow
//                  each frame (a burst), entered and left by raised-
//                  cosine ramps Ramp=R symbols long inside the guard.
//                  The record holds whole frames.
//  A rotation or a differential chain must come back to its start for
//  the record to repeat: the data symbols are multiplied (up to 16
//  times) until it does.
//
//  The measurement. An RRC carrier is read through its matched filter
//  against the ideal points (rotated and chained as sent). Any other
//  pulse, SOQPSK-TG or a burst is read through a band filter (flat to
//  half the bandwidth, nothing beyond) against the clean record through
//  the same filter at the symbol instants, scaled to unit mean power:
//  the reference-waveform EVM of TS 45.005 for EDGE. An offset carrier
//  has its Q moved back half a symbol in either receiver.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;

namespace RfCore.Signals
{
    /// <summary>An SC carrier that cannot be generated: a FormatException, as every carrier's refusal is (the netlist
    /// reader and the Signal Designer catch that), carrying its coded <see cref="CircuitRF.Diagnostics.Diagnostic"/>.</summary>
    public sealed class ScCarrierRefusal : FormatException
    {
        public Diagnostic Diagnostic { get; }
        public ScCarrierRefusal(Diagnostic diagnostic) : base(diagnostic.Render()) { Diagnostic = diagnostic; }
    }

    /// <summary>The SC carrier's coded refusals, each naming the field to change.</summary>
    public static class ScDiagnostics
    {
        private static Diagnostic E(string id, string template, params (string, object?)[] args)
            => Diagnostic.Create("signal.sc." + id, DiagnosticSeverity.Error, template, args);

        private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        public static Diagnostic Modulation(string value)
            => E("modulation", "SC carrier: unknown modulation '{value}' ({choices})", ("value", value), ("choices", string.Join(", ", ScQam.ModNames)));
        public static Diagnostic NotAList(string key, string text)
            => E("list", "SC carrier: {key}={text} is not a list of numbers separated by commas", ("key", key), ("text", text));
        public static Diagnostic PilotBlock(string text)
            => E("pilot-block", "SC carrier: PilotBlock={text} is not L/P (L known symbols after every P data symbols, both whole numbers from 1)", ("text", text));
        public static Diagnostic SymbolRate(double value)
            => E("symbol-rate", "SC carrier: Rs must be a positive symbol rate (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic RollOff(double value)
            => E("roll-off", "SC carrier: RollOff must be 0 to 1 (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic Span(int value)
            => E("span", "SC carrier: Span must be 2 symbols or more (got {value}); leave it out to shape over the whole record", ("value", value));
        public static Diagnostic Data(string? value)
            => E("data", "SC carrier: Data must be PN9, PN15, PN23 or a pattern of 0s and 1s (got '{value}')", ("value", value));
        public static Diagnostic Symbols(int value)
            => E("symbols", "SC carrier: Symbols must be 1 or more (got {value})", ("value", value));
        public static Diagnostic Preamble(int value)
            => E("preamble", "SC carrier: Preamble must be 0 or more symbols (got {value})", ("value", value));
        public static Diagnostic RingCount(string mod, int rings, int got)
            => E("rings-count", "SC carrier: {mod} has {rings} rings, so Rings takes {need} radius ratios to the inner ring, separated by commas (got {got})",
                 ("mod", mod), ("rings", rings), ("need", rings - 1), ("got", got));
        public static Diagnostic RingOrder(string? text)
            => E("rings-order", "SC carrier: Rings must grow outwards from 1 (got {text})", ("text", text));
        public static Diagnostic PhaseCount(string mod, int rings, int got)
            => E("phases-count", "SC carrier: {mod} has {rings} rings, so Phases takes {rings} offsets in degrees (got {got})",
                 ("mod", mod), ("rings", rings), ("got", got));
        public static Diagnostic NotApsk(string mod)
            => E("rings-not-apsk", "SC carrier: Rings and Phases belong to 16APSK and 32APSK, not {mod}", ("mod", mod));
        public static Diagnostic Pulse(string value)
            => E("pulse", "SC carrier: unknown pulse '{value}' ({choices})", ("value", value), ("choices", string.Join(", ", ScQam.PulseNames)));
        public static Diagnostic Bt(double value)
            => E("bt", "SC carrier: BT must be a positive bandwidth-time product (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic SpanPulse(string pulse)
            => E("span-pulse", "SC carrier: Span truncates the RRC pulse; leave it out with Pulse={pulse}", ("pulse", pulse));
        public static Diagnostic Rotation(double value)
            => E("rotation", "SC carrier: Rotation must be a number of degrees (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic Differential(string mod)
            => E("differential", "SC carrier: Differential takes BPSK, QPSK or 8PSK (got {mod})", ("mod", mod));
        public static Diagnostic SoqpskFixed()
            => E("soqpsk", "SC carrier: SOQPSK-TG sets its own pulse, offset and phase; leave Pulse, Rotation, Differential and Offset out");
        public static Diagnostic Frame(int value)
            => E("frame", "SC carrier: FrameData must be 0 (one frame) or more data symbols (got {value})", ("value", value));
        public static Diagnostic Guard(double value)
            => E("guard", "SC carrier: Guard must be 0 or more symbol periods (got {valueText})", ("value", value), ("valueText", F(value)));
        public static Diagnostic Ramp(double ramp, double guard)
            => E("ramp", "SC carrier: Ramp={rampText} needs a Guard of at least twice its length (got Guard={guardText})",
                 ("ramp", ramp), ("guard", guard), ("rampText", F(ramp)), ("guardText", F(guard)));
        public static Diagnostic TooLong(int symbols, long samples, int max)
            => E("too-long", "SC carrier: {symbolsText} symbols make {samplesText} samples, over the {maxText} a record may hold; lower Symbols",
                 ("symbols", symbols), ("samples", samples), ("max", max),
                 ("symbolsText", symbols.ToString("N0", CultureInfo.InvariantCulture)),
                 ("samplesText", samples.ToString("N0", CultureInfo.InvariantCulture)),
                 ("maxText", max.ToString("N0", CultureInfo.InvariantCulture)));
    }

    public enum ScModulation { Bpsk, Qpsk, Psk8, Qam16, Qam64, Qam256, Apsk16, Apsk32, SoqpskTg }

    public enum ScPulse { Rrc, Rc, Gaussian, Rect, HalfSine, LinearGmsk }

    /// <summary>A single-carrier carrier's settings with every default applied.</summary>
    /// <param name="SymbolRate">Rs, symbols/s.</param>
    /// <param name="Span">The shaping filter's span in symbols; null = the whole record (exact).</param>
    /// <param name="Rings">APSK: each outer ring's radius over the inner ring's.</param>
    /// <param name="PhasesDeg">APSK: each ring's phase offset, degrees, inner first.</param>
    /// <param name="DataSymbols">Data symbols in the record.</param>
    public sealed record ScShape(double SymbolRate, ScModulation Mod, double RollOff, int? Span, double[] Rings, double[] PhasesDeg,
                                 string Data, int Preamble, int PilotLength, int PilotEvery, int DataSymbols)
    {
        /// <summary>The pulse each symbol is shaped by.</summary>
        public ScPulse Pulse { get; init; } = ScPulse.Rrc;
        /// <summary>Gaussian pulse: the Gaussian filter's 3 dB bandwidth times the symbol period.</summary>
        public double Bt { get; init; } = ScQam.DefaultBt;
        /// <summary>Degrees each symbol turns by from the one before.</summary>
        public double RotationDeg { get; init; }
        /// <summary>Each point is the previous one times the label's point (BPSK, QPSK, 8PSK).</summary>
        public bool Differential { get; init; }
        /// <summary>Q half a symbol after I.</summary>
        public bool Offset { get; init; }
        /// <summary>Data symbols per frame, each frame with its own preamble and pilot blocks; 0 = the record is one frame.</summary>
        public int FrameData { get; init; }
        /// <summary>Symbol periods of silence after each frame.</summary>
        public double Guard { get; init; }
        /// <summary>Symbol periods of each raised-cosine ramp into and out of a frame, inside the guard.</summary>
        public double Ramp { get; init; }

        public int BitsPerSymbol => ScQam.BitsPerSymbol(Mod);
        public int Frames => FrameData > 0 ? Math.Max(1, DataSymbols / FrameData) : 1;
        public int FrameDataSymbols => FrameData > 0 ? FrameData : DataSymbols;
        /// <summary>Pilot blocks in a frame: one after every <see cref="PilotEvery"/> data symbols but the last.</summary>
        public int FramePilotBlocks => PilotLength > 0 && PilotEvery > 0 ? (FrameDataSymbols - 1) / PilotEvery : 0;
        public int PilotBlocks => Frames * FramePilotBlocks;
        public int KnownSymbols => Frames * Preamble + PilotBlocks * PilotLength;
        public int RecordSymbols => DataSymbols + KnownSymbols;
        public int FrameSymbols => Preamble + FrameDataSymbols + FramePilotBlocks * PilotLength;
        public int GuardSamples => (int)Math.Round(Guard * ScQam.SamplesPerSymbol);
        /// <summary>Samples in the record at the native rate.</summary>
        public int Samples => RecordSymbols * ScQam.SamplesPerSymbol + Frames * GuardSamples;
        public double RecordSeconds => (RecordSymbols + Frames * GuardSamples / (double)ScQam.SamplesPerSymbol) / SymbolRate;
    }

    /// <summary>The record's symbols: the ideal point at each, whether it is known (preamble or pilot), and a data symbol's
    /// label (-1 for a known one). <see cref="At"/>: each symbol's instant, in native samples (k·sps but for guards).</summary>
    public sealed record ScSymbols(Complex[] Points, bool[] Known, int[] Labels)
    {
        public int[] At { get; init; } = [];
    }

    public static class ScQam
    {
        public const double DefaultSymbolRate = 1e6;
        public const ScModulation DefaultModulation = ScModulation.Qpsk;
        public const double DefaultRollOff = 0.35;
        public const string DefaultData = "PN9";
        /// <summary>Most data symbols a record holds unless Symbols= says otherwise.</summary>
        public const int DefaultMaxSymbols = 1 << 16;

        /// <summary>Samples per symbol at the native rate: the band ends at Rs(1 + α)/2 ≤ Rs, a quarter of the native
        /// Nyquist, so a compressed amplifier's regrowth has room in the native record too.</summary>
        public const int SamplesPerSymbol = 8;

        public const double DefaultBt = 0.3;

        public static readonly double[] DefaultRings16 = [2.85];
        public static readonly double[] DefaultRings32 = [2.84, 5.27];
        public static readonly double[] DefaultPhases16 = [45, 15];
        public static readonly double[] DefaultPhases32 = [45, 15, 0];

        public static int BitsPerSymbol(ScModulation m) => m switch
        {
            ScModulation.Bpsk => 1,
            ScModulation.Qpsk or ScModulation.SoqpskTg => 2,
            ScModulation.Psk8 => 3,
            ScModulation.Qam16 or ScModulation.Apsk16 => 4,
            ScModulation.Apsk32 => 5,
            ScModulation.Qam64 => 6,
            _ => 8,
        };

        public static bool IsApsk(ScModulation m) => m is ScModulation.Apsk16 or ScModulation.Apsk32;

        public static int[] RingSizes(ScModulation m) => m == ScModulation.Apsk16 ? [4, 12] : [4, 12, 16];

        public static string ModName(ScModulation m) => m switch
        {
            ScModulation.Bpsk => "BPSK",
            ScModulation.Qpsk => "QPSK",
            ScModulation.Psk8 => "8PSK",
            ScModulation.Qam16 => "16QAM",
            ScModulation.Qam64 => "64QAM",
            ScModulation.Qam256 => "256QAM",
            ScModulation.Apsk16 => "16APSK",
            ScModulation.Apsk32 => "32APSK",
            _ => "SOQPSK-TG",
        };

        public static string PulseName(ScPulse p) => p switch
        {
            ScPulse.Rrc => "RRC",
            ScPulse.Rc => "RC",
            ScPulse.Gaussian => "Gaussian",
            ScPulse.Rect => "Rect",
            ScPulse.HalfSine => "HalfSine",
            _ => "LinearGMSK",
        };

        public static readonly string[] PulseNames = [.. Enum.GetValues<ScPulse>().Select(PulseName)];

        public static ScPulse ParsePulse(string v)
        {
            foreach (var p in Enum.GetValues<ScPulse>())
                if (PulseName(p).Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            throw new ScCarrierRefusal(ScDiagnostics.Pulse(v));
        }

        public static readonly string[] ModNames = [.. Enum.GetValues<ScModulation>().Select(ModName)];

        public static ScModulation ParseMod(string v)
        {
            foreach (var m in Enum.GetValues<ScModulation>())
                if (ModName(m).Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return m;
            throw new ScCarrierRefusal(ScDiagnostics.Modulation(v));
        }

        /// <summary>A comma-separated list of numbers (<c>Rings=</c>, <c>Phases=</c>), or null when empty.</summary>
        public static double[]? ParseList(string? text, string key)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    throw new ScCarrierRefusal(ScDiagnostics.NotAList(key, text));
            return v;
        }

        /// <summary><c>PilotBlock=L/P</c>: L known symbols after every P data symbols; null when not that.</summary>
        public static (int Length, int Every)? ParsePilotBlock(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var p = text.Split('/', StringSplitOptions.TrimEntries);
            if (p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l)
                && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) && l >= 1 && e >= 1)
                return (l, e);
            throw new ScCarrierRefusal(ScDiagnostics.PilotBlock(text));
        }

        public static ScShape Resolve(CarrierSpec c)
        {
            var mod = c.ScModulation ?? DefaultModulation;
            string data = (c.Data ?? DefaultData).ToUpperInvariant();
            int k = BitsPerSymbol(mod);
            var pilot = ParsePilotBlock(c.PilotBlock);
            int nd = c.Symbols ?? (int)Math.Min(DefaultMaxSymbols, PeriodSymbols(data, k));
            return Close(new ScShape(c.SymbolRateHz ?? DefaultSymbolRate, mod, c.RollOff ?? DefaultRollOff, c.Span,
                               ParseList(c.Rings, "Rings") ?? (mod == ScModulation.Apsk32 ? DefaultRings32 : IsApsk(mod) ? DefaultRings16 : []),
                               ParseList(c.RingPhases, "Phases") ?? (mod == ScModulation.Apsk32 ? DefaultPhases32 : IsApsk(mod) ? DefaultPhases16 : []),
                               data, c.Preamble ?? 0, pilot?.Length ?? 0, pilot?.Every ?? 0, nd)
            {
                Pulse = c.ScPulse ?? ScPulse.Rrc, Bt = c.GaussianBt ?? DefaultBt, RotationDeg = c.RotationDeg ?? 0,
                Differential = c.Differential ?? false, Offset = c.OffsetQ ?? false, FrameData = c.FrameData ?? 0,
                Guard = c.GuardSymbols ?? 0, Ramp = c.RampSymbols ?? 0,
            });
        }

        /// <summary>The shape with its data symbols made whole frames, then multiplied (up to 16 times) until a rotation or
        /// differential chain comes back to its start at the record's end; unchanged when neither is set.</summary>
        public static ScShape Close(ScShape s)
        {
            if (s.FrameData > 0) s = s with { DataSymbols = (s.DataSymbols + s.FrameData - 1) / s.FrameData * s.FrameData };
            if (s.RotationDeg == 0 && !s.Differential && s.Mod != ScModulation.SoqpskTg) return s;
            for (int r = 1; r <= 16; r++)
            {
                var t = s with { DataSymbols = s.DataSymbols * r };
                var sy = Modulate(t, out var end);
                double turns = t.RecordSymbols * t.RotationDeg / 360;
                bool closes = Math.Abs(turns - Math.Round(turns)) < 1e-9
                              && (t.Mod == ScModulation.SoqpskTg ? (SoqpskAlpha(sy.Points).Sum() % 4 + 4) % 4 == 0 : (end - 1).Magnitude < 1e-6);
                if (closes) return t;
            }
            // ponytail: no closure within 16 repeats (an odd rotation): one step at the record's wrap is off
            return s;
        }

        /// <summary>Symbols in one whole data period: lcm(period, k) / k.</summary>
        public static long PeriodSymbols(string data, int k)
        {
            long p = DataLength(data);
            long g = Gcd(p, k);
            return p / g;
        }

        private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }

        /// <summary>The data's period in bits.</summary>
        public static int DataLength(string data) => data switch
        {
            "PN9" => 511,
            "PN15" => 32767,
            "PN23" => 8388607,
            _ => data.Length,
        };

        /// <summary>Refuses a single-carrier carrier that cannot be generated, with a sentence naming the field; returns
        /// the shape.</summary>
        public static ScShape Validate(CarrierSpec c)
        {
            var s = Resolve(c);
            if (!(s.SymbolRate > 0) || !double.IsFinite(s.SymbolRate)) throw new ScCarrierRefusal(ScDiagnostics.SymbolRate(s.SymbolRate));
            if (!(s.RollOff >= 0 && s.RollOff <= 1)) throw new ScCarrierRefusal(ScDiagnostics.RollOff(s.RollOff));
            if (s.Span is int sp && sp < 2) throw new ScCarrierRefusal(ScDiagnostics.Span(sp));
            if (s.Data is not ("PN9" or "PN15" or "PN23") && (s.Data.Length == 0 || s.Data.Any(ch => ch is not ('0' or '1'))))
                throw new ScCarrierRefusal(ScDiagnostics.Data(c.Data));
            if (s.DataSymbols < 1) throw new ScCarrierRefusal(ScDiagnostics.Symbols(s.DataSymbols));
            if (s.Preamble < 0) throw new ScCarrierRefusal(ScDiagnostics.Preamble(s.Preamble));
            if (IsApsk(s.Mod))
            {
                int rings = RingSizes(s.Mod).Length;
                if (s.Rings.Length != rings - 1) throw new ScCarrierRefusal(ScDiagnostics.RingCount(ModName(s.Mod), rings, s.Rings.Length));
                for (int i = 0; i < s.Rings.Length; i++)
                    if (!(s.Rings[i] > (i == 0 ? 1 : s.Rings[i - 1])) || !double.IsFinite(s.Rings[i]))
                        throw new ScCarrierRefusal(ScDiagnostics.RingOrder(c.Rings));
                if (s.PhasesDeg.Length != rings) throw new ScCarrierRefusal(ScDiagnostics.PhaseCount(ModName(s.Mod), rings, s.PhasesDeg.Length));
            }
            else if (c.Rings is not null || c.RingPhases is not null)
                throw new ScCarrierRefusal(ScDiagnostics.NotApsk(ModName(s.Mod)));
            return Check(s);
        }

        /// <summary>Refuses the options a shape cannot be generated with, and a record too long; returns the shape.</summary>
        public static ScShape Check(ScShape s)
        {
            if (s.Pulse == ScPulse.Gaussian && !(s.Bt > 0 && double.IsFinite(s.Bt))) throw new ScCarrierRefusal(ScDiagnostics.Bt(s.Bt));
            if (s.Span is not null && s.Pulse != ScPulse.Rrc) throw new ScCarrierRefusal(ScDiagnostics.SpanPulse(PulseName(s.Pulse)));
            if (!double.IsFinite(s.RotationDeg)) throw new ScCarrierRefusal(ScDiagnostics.Rotation(s.RotationDeg));
            if (s.Mod == ScModulation.SoqpskTg && (s.Pulse != ScPulse.Rrc || s.RotationDeg != 0 || s.Differential || s.Offset))
                throw new ScCarrierRefusal(ScDiagnostics.SoqpskFixed());
            if (s.Differential && s.Mod is not (ScModulation.Bpsk or ScModulation.Qpsk or ScModulation.Psk8))
                throw new ScCarrierRefusal(ScDiagnostics.Differential(ModName(s.Mod)));
            if (s.FrameData < 0) throw new ScCarrierRefusal(ScDiagnostics.Frame(s.FrameData));
            if (!(s.Guard >= 0) || !double.IsFinite(s.Guard)) throw new ScCarrierRefusal(ScDiagnostics.Guard(s.Guard));
            if (!(s.Ramp >= 0) || !(s.Guard >= 2 * s.Ramp)) throw new ScCarrierRefusal(ScDiagnostics.Ramp(s.Ramp, s.Guard));
            long n = (long)s.RecordSymbols * SamplesPerSymbol + (long)s.Frames * s.GuardSamples;
            if (n > Fsk.MaxSamples) throw new ScCarrierRefusal(ScDiagnostics.TooLong(s.RecordSymbols, n, Fsk.MaxSamples));
            return s;
        }

        /// <summary>The channel bandwidth: Rs(1 + α), where an RRC or RC spectrum ends; SOQPSK-TG's 99 % power bandwidth,
        /// 0.78 times the bit rate (IRIG 106, Chapter 2); any other pulse's 99 % power bandwidth within the native band.</summary>
        public static double BandwidthHz(ScShape s)
        {
            if (s.Mod == ScModulation.SoqpskTg) return 1.56 * s.SymbolRate;
            if (s.Pulse is ScPulse.Rrc or ScPulse.Rc) return s.SymbolRate * (1 + s.RollOff);
            const int steps = 4000;
            double top = NativeRateHz(s) / 2, df = top / steps, total = 0, acc = 0;
            var p = new double[steps];
            var taps = Taps(s);
            for (int i = 0; i < steps; i++) { double m = Response(s, taps, (i + 0.5) * df).Magnitude; p[i] = m * m; total += p[i]; }
            for (int i = 0; i < steps; i++) if ((acc += p[i]) >= 0.99 * total) return 2 * (i + 1) * df;
            return 2 * top;
        }

        public static double NativeRateHz(ScShape s) => SamplesPerSymbol * s.SymbolRate;

        // ── the constellation ─────────────────────────────────────────────────────────────────────────────

        private static int Gray(int i) => i ^ (i >> 1);

        /// <summary>The points by label, unit mean power (the file header).</summary>
        public static Complex[] Constellation(ScShape s)
        {
            int k = s.BitsPerSymbol, m = 1 << k;
            var p = new Complex[m];
            switch (s.Mod)
            {
                case ScModulation.Bpsk:
                    p[0] = 1; p[1] = -1;
                    return p;
                case ScModulation.Qpsk or ScModulation.SoqpskTg:
                    for (int l = 0; l < 4; l++) p[l] = new Complex(1 - 2 * (l >> 1), 1 - 2 * (l & 1)) / Math.Sqrt(2);
                    return p;
                case ScModulation.Psk8:
                    for (int i = 0; i < 8; i++) p[Gray(i)] = Complex.FromPolarCoordinates(1, i * Math.PI / 4);
                    return p;
                case ScModulation.Qam16 or ScModulation.Qam64 or ScModulation.Qam256:
                {
                    int h = k / 2, levels = 1 << h;
                    double norm = 1.0 / Math.Sqrt(2.0 * (levels * levels - 1) / 3.0);
                    for (int i = 0; i < levels; i++)
                        for (int q = 0; q < levels; q++)
                            p[(Gray(i) << h) | Gray(q)] = new Complex((2 * i - levels + 1) * norm, (2 * q - levels + 1) * norm);
                    return p;
                }
                default:
                {
                    var sizes = RingSizes(s.Mod);
                    int l = 0;
                    for (int r = 0; r < sizes.Length; r++)
                    {
                        double radius = r == 0 ? 1 : s.Rings[r - 1], ph = s.PhasesDeg[r] * Math.PI / 180;
                        for (int j = 0; j < sizes[r]; j++) p[l++] = Complex.FromPolarCoordinates(radius, ph + 2 * Math.PI * j / sizes[r]);
                    }
                    double mean = p.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary), a = 1 / Math.Sqrt(mean);
                    for (int i = 0; i < m; i++) p[i] *= a;
                    return p;
                }
            }
        }

        /// <summary>The label of the point of <paramref name="constellation"/> nearest <paramref name="v"/>.</summary>
        public static int Decide(Complex[] constellation, Complex v)
        {
            int best = 0; double bd = double.MaxValue;
            for (int l = 0; l < constellation.Length; l++)
            {
                double dr = v.Real - constellation[l].Real, di = v.Imaginary - constellation[l].Imaginary, d = dr * dr + di * di;
                if (d < bd) { bd = d; best = l; }
            }
            return best;
        }

        /// <summary>A known symbol's point: QPSK from PN9, two bits per known symbol, in order through the record.</summary>
        private static Complex KnownPoint(byte[] pn9, int j)
        {
            int b0 = pn9[2 * j % pn9.Length], b1 = pn9[(2 * j + 1) % pn9.Length];
            return new Complex(1 - 2 * b0, 1 - 2 * b1) / Math.Sqrt(2);
        }

        /// <summary>The record's symbols, in order (the file header): the points sent, or for a carrier read against its
        /// clean waveform (<see cref="IsReference"/>) that waveform through the receiver at the instants, unit mean power
        /// over the data symbols.</summary>
        public static ScSymbols Symbols(ScShape s)
        {
            var sy = Modulate(s, out _);
            if (!IsReference(s)) return sy;
            var y = Filter(s, Generate(s), NativeRateHz(s), matched: true);
            var pts = sy.At.Select(a => y[a]).ToArray();
            double p = 0; int nd = 0;
            for (int i = 0; i < pts.Length; i++)
                if (!sy.Known[i]) { p += pts[i].Real * pts[i].Real + pts[i].Imaginary * pts[i].Imaginary; nd++; }
            double g = 1 / Math.Sqrt(p / nd);
            return sy with { Points = [.. pts.Select(v => v * g)] };
        }

        /// <summary>Whether the carrier is read against its clean waveform rather than the ideal points: any pulse but RRC,
        /// SOQPSK-TG, or a burst.</summary>
        public static bool IsReference(ScShape s) => s.Pulse != ScPulse.Rrc || s.Mod == ScModulation.SoqpskTg || s.Guard > 0;

        /// <summary>The points sent, frame by frame, rotated and chained; <paramref name="end"/> is the differential chain's
        /// state after the last symbol (1 when it closes).</summary>
        private static ScSymbols Modulate(ScShape s, out Complex end)
        {
            var bits = Fsk.DataBits(s.Data);
            var pn9 = Fsk.Prbs(9, 5);
            var cons = Constellation(s);
            int k = s.BitsPerSymbol, n = s.RecordSymbols, fd = s.FrameDataSymbols, g = s.GuardSamples;
            const int sps = SamplesPerSymbol;
            var pts = new Complex[n]; var known = new bool[n]; var labels = new int[n]; var instants = new int[n];
            int at = 0, kn = 0, f = 0;
            long kbit = 0;
            Complex st = 1;
            Complex Step(int l) { st *= cons[l]; return st /= st.Magnitude; }
            void Known(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (s.Differential)
                    {
                        int l = 0;
                        for (int j = 0; j < k; j++) l = (l << 1) | pn9[(int)(kbit++ % pn9.Length)];
                        pts[at] = Step(l);
                    }
                    else pts[at] = KnownPoint(pn9, kn++);
                    known[at] = true; labels[at] = -1; instants[at] = at * sps + f * g; at++;
                }
            }
            long bit = 0;
            for (f = 0; f < s.Frames; f++)
            {
                Known(s.Preamble);
                for (int d = 0; d < fd; d++)
                {
                    if (d > 0 && s.PilotLength > 0 && d % s.PilotEvery == 0) Known(s.PilotLength);
                    int l = 0;
                    for (int j = 0; j < k; j++) l = (l << 1) | bits[(int)(bit++ % bits.Length)];
                    pts[at] = s.Differential ? Step(l) : cons[l]; labels[at] = l; instants[at] = at * sps + f * g; at++;
                }
            }
            end = st;
            if (s.RotationDeg != 0)
                for (int i = 0; i < n; i++) pts[i] *= Complex.FromPolarCoordinates(1, Math.IEEERemainder(i * s.RotationDeg, 360) * Math.PI / 180);
            return new ScSymbols(pts, known, labels) { At = instants };
        }

        // ── shaping ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The root-raised-cosine response at <paramref name="fHz"/>, 1 in the flat part — the whole-record filter's.</summary>
        public static double RootRaisedCosine(double fHz, double rs, double alpha)
        {
            double f = Math.Abs(fHz), lo = (1 - alpha) * rs / 2, hi = (1 + alpha) * rs / 2;
            if (f <= lo) return 1;
            if (f >= hi) return 0;
            return Math.Cos(Math.PI / (2 * alpha * rs) * (f - lo));
        }

        /// <summary>The root-raised-cosine impulse response at <paramref name="t"/> symbols (peak 1 − α + 4α/π).</summary>
        public static double ImpulseResponse(double t, double alpha)
        {
            if (Math.Abs(t) < 1e-12) return 1 - alpha + 4 * alpha / Math.PI;
            if (alpha > 0 && Math.Abs(Math.Abs(t) - 1 / (4 * alpha)) < 1e-9)
                return alpha / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * alpha)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * alpha)));
            double x = 4 * alpha * t;
            return (Math.Sin(Math.PI * t * (1 - alpha)) + x * Math.Cos(Math.PI * t * (1 + alpha))) / (Math.PI * t * (1 - x * x));
        }

        /// <summary>
        /// The shaping filter's response at <paramref name="fHz"/>, 1 at DC: the root raised cosine for a whole-record
        /// filter, its square for RC, sinc(f/Rs) times the Gaussian exp(−ln2·f²/(2B²)), B = BT·Rs, for Gaussian; for a
        /// pulse of sampled taps (Span=n, Rect, HalfSine, LinearGmsk) the transform of the taps at the native rate.
        /// </summary>
        public static Complex Response(ScShape s, double fHz) => Response(s, Taps(s), fHz);

        private static Complex Response(ScShape s, double[]? taps, double fHz)
        {
            if (taps is not { } h)
                return s.Pulse switch
                {
                    ScPulse.Rc => Math.Pow(RootRaisedCosine(fHz, s.SymbolRate, s.RollOff), 2),
                    ScPulse.Gaussian => Sinc(fHz / s.SymbolRate) * Math.Exp(-Math.Log(2) / 2 * Math.Pow(fHz / (s.Bt * s.SymbolRate), 2)),
                    _ => RootRaisedCosine(fHz, s.SymbolRate, s.RollOff),
                };
            int half = h.Length / 2;
            double fs = NativeRateHz(s), dc = 0;
            Complex acc = 0;
            for (int n = -half; n <= half; n++)
            {
                dc += h[n + half];
                acc += h[n + half] * Complex.FromPolarCoordinates(1, -2 * Math.PI * fHz * n / fs);
            }
            return acc / dc;
        }

        private static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

        /// <summary>A pulse of sampled taps at the native rate, centred (index Length/2 at t = 0); null for a whole-record
        /// response.</summary>
        private static double[]? Taps(ScShape s)
        {
            const int sps = SamplesPerSymbol;
            (int half, Func<double, double>? h) = s.Pulse switch
            {
                ScPulse.Rrc when s.Span is int span => (span * sps / 2, (Func<double, double>)(t => ImpulseResponse(t, s.RollOff))),
                // the ends shared with the neighbours' at half weight, so the pulses tile to a flat 1
                ScPulse.Rect => (sps / 2, (Func<double, double>)(t => Math.Abs(t) < 0.5 - 1e-12 ? 1 : 0.5)),
                ScPulse.HalfSine => (sps / 2, t => Math.Cos(Math.PI * t)),
                ScPulse.LinearGmsk => (5 * sps / 2, t => LinearGmsk(t + 2.5)),
                _ => (0, null),
            };
            if (h is null) return null;
            var taps = new double[2 * half + 1];
            for (int n = -half; n <= half; n++) taps[n + half] = h((double)n / sps);
            return taps;
        }

        /// <summary>
        /// The linearised GMSK pulse of 3GPP TS 45.004 (8PSK clause), t in symbols: c0(t) = Π_{i=0..3} S(t + i) on
        /// [0, 5], S(t) = sin(π G(t)) for t ≤ 4 and sin(π/2 − π G(t − 4)) for 4 &lt; t ≤ 8, G the integral from 0 of
        /// g(t) = ½[Q(a(t − 5/2)) − Q(a(t − 3/2))], a = 2π·0.3/√ln2, taken in closed form: ∫Q(u) du = u Q(u) − φ(u).
        /// </summary>
        public static double LinearGmsk(double t)
        {
            if (t < 0 || t > 5) return 0;
            double a = 2 * Math.PI * 0.3 / Math.Sqrt(Math.Log(2));
            double F(double u) => u * Q(u) - Math.Exp(-u * u / 2) / Math.Sqrt(2 * Math.PI);
            double G(double x) => (F(a * (x - 2.5)) - F(a * (x - 1.5)) - F(-2.5 * a) + F(-1.5 * a)) / (2 * a);
            double S(double x) => x < 0 || x > 8 ? 0 : x <= 4 ? Math.Sin(Math.PI * G(x)) : Math.Sin(Math.PI / 2 - Math.PI * G(x - 4));
            return S(t) * S(t + 1) * S(t + 2) * S(t + 3);
        }

        /// <summary><paramref name="x"/> (periodic, at <paramref name="rateHz"/>) through the shaping filter, or with
        /// <paramref name="matched"/> through the receiver: the conjugate RRC for an RRC pulse, the band filter
        /// (<see cref="Band"/>) for any other; an offset carrier's Q is then moved back half a symbol.</summary>
        public static Complex[] Filter(ScShape s, Complex[] x, double rateHz, bool matched)
        {
            bool rrc = s.Pulse == ScPulse.Rrc && s.Mod != ScModulation.SoqpskTg;
            var y = matched && !rrc ? Band(s, x, rateHz) : Shape(s, x, rateHz, matched);
            return matched && (s.Offset || s.Mod == ScModulation.SoqpskTg) ? AdvanceQ(y, rateHz / (2 * s.SymbolRate)) : y;
        }

        private static Complex[] Shape(ScShape s, Complex[] x, double rateHz, bool matched)
        {
            int n = x.Length;
            var spec = Fft.Forward(x);
            var h = Taps(s);
            if (h is not null && Math.Abs(rateHz / NativeRateHz(s) - 1) < 1e-12 && h.Length <= n)
            {
                // the taps laid circularly at the native rate: their transform is Response at every bin
                var taps = new Complex[n];
                double dc = 0;
                int half = h.Length / 2;
                for (int t = -half; t <= half; t++)
                {
                    taps[(t + n) % n] += h[t + half];
                    dc += h[t + half];
                }
                var hs = Fft.Forward(taps);
                for (int b = 0; b < n; b++) spec[b] *= (matched ? Complex.Conjugate(hs[b]) : hs[b]) / dc;
                return Fft.Inverse(spec);
            }
            for (int b = 0; b < n; b++)
            {
                var r = Response(s, h, (b <= n / 2 ? b : b - n) * rateHz / n);
                spec[b] *= matched ? Complex.Conjugate(r) : r;
            }
            return Fft.Inverse(spec);
        }

        /// <summary>The band filter a carrier of any pulse but RRC is read through: flat to half its bandwidth and to 0.45
        /// of the native rate (inside the receiver's own channel filter), nothing beyond.</summary>
        public static Complex[] Band(ScShape s, Complex[] x, double rateHz)
        {
            double edge = Math.Min(BandwidthHz(s) / 2, 0.45 * NativeRateHz(s));
            int n = x.Length;
            var spec = Fft.Forward(x);
            for (int b = 0; b < n; b++)
                if (Math.Abs((b <= n / 2 ? b : b - n) * rateHz / n) > edge) spec[b] = 0;
            return Fft.Inverse(spec);
        }

        /// <summary>Q moved earlier by <paramref name="samples"/> (in the frequency domain), I as it is.</summary>
        private static Complex[] AdvanceQ(Complex[] y, double samples)
        {
            int n = y.Length;
            var q = Fft.Forward([.. y.Select(v => new Complex(v.Imaginary, 0))]);
            for (int b = 0; b < n; b++) q[b] *= Complex.FromPolarCoordinates(1, 2 * Math.PI * (b <= n / 2 ? b : b - n) * samples / n);
            var qa = Fft.Inverse(q);
            return [.. y.Select((v, i) => new Complex(v.Real, qa[i].Real))];
        }

        /// <summary>One record at the native rate (<see cref="SamplesPerSymbol"/> per symbol), unit mean power: symbol k
        /// is an impulse at its instant (Q half a symbol later when offset) through the shaping filter, so its
        /// matched-filtered peak is on that sample; a burst is then gated by <see cref="Window"/>.</summary>
        public static Complex[] Generate(ScShape s)
        {
            var sy = Modulate(s, out _);
            const int sps = SamplesPerSymbol;
            int n = s.Samples;
            Complex[] x;
            if (s.Mod == ScModulation.SoqpskTg) x = Soqpsk(s, sy);
            else
            {
                var z = new Complex[n];
                for (int k = 0; k < sy.Points.Length; k++)
                    if (s.Offset)
                    {
                        z[sy.At[k]] += sy.Points[k].Real;
                        z[(sy.At[k] + sps / 2) % n] += new Complex(0, sy.Points[k].Imaginary);
                    }
                    else z[sy.At[k]] = sy.Points[k];
                x = Filter(s, z, NativeRateHz(s), matched: false);
            }
            if (s.Guard > 0)
            {
                var w = Window(s);
                for (int i = 0; i < n; i++) x[i] *= w[i];
            }
            OfdmGenerator.Normalise(x);
            return x;
        }

        /// <summary>The shape of a burst's switched DPSK payload (Bluetooth EDR): <paramref name="t"/>'s symbols at its rate,
        /// RRC of its roll-off, differential (QPSK for π/4-DQPSK, 8PSK for 8DPSK), the reference symbol and the 10 sync
        /// symbols known, the rest <paramref name="data"/>. Not closed: the payload is exactly its symbols.</summary>
        public static ScShape DpskShape(FskSwitch t, string data)
        {
            var mod = t.Modulation.ToUpperInvariant() switch
            {
                "PI/4-DQPSK" or "π/4-DQPSK" or "DQPSK" => ScModulation.Qpsk,
                "8DPSK" or "D8PSK" => ScModulation.Psk8,
                _ => throw new FormatException($"FSK carrier: the switched payload is PI/4-DQPSK or 8DPSK (got '{t.Modulation}')"),
            };
            if (t.Symbols <= 11) throw new FormatException($"FSK carrier: the switched payload needs more than its 11 known symbols (got {t.Symbols})");
            return new ScShape(t.SymbolRate, mod, t.RollOff, null, [], [], data.ToUpperInvariant(), 11, 0, 0, t.Symbols - 11) { Differential = true };
        }

        /// <summary>The payload function <see cref="Fsk.Generate"/> takes for a switch: <see cref="DpskShape"/>'s record at
        /// <paramref name="rateHz"/>, exactly t.Symbols · rate / Rs samples at unit mean power, symbol k's matched-filter peak
        /// on sample k · rate / Rs.</summary>
        public static Complex[] DpskPayload(FskSwitch t, string data, double rateHz)
        {
            var y = Fft.ResamplePeriodic(Generate(DpskShape(t, data)), (int)Math.Round(t.Symbols * rateHz / t.SymbolRate));
            OfdmGenerator.Normalise(y);
            return y;
        }

        /// <summary>The burst gate at the native rate: 1 over each frame's symbol periods, a sin² ramp of Ramp symbols
        /// either side, 0 in the rest of the guard.</summary>
        public static double[] Window(ScShape s)
        {
            const int sps = SamplesPerSymbol;
            int n = s.Samples, fs = s.FrameSymbols * sps, g = s.GuardSamples, r = (int)Math.Round(s.Ramp * sps);
            var w = new double[n];
            for (int f = 0; f < s.Frames; f++)
            {
                int a0 = f * (fs + g) - sps / 2;
                for (int i = -r; i < fs + r; i++)
                {
                    double v = i < 0 ? Math.Sin(Math.PI / 2 * (i + r) / r) : i >= fs ? Math.Sin(Math.PI / 2 * (fs + r - i) / r) : 1;
                    w[((a0 + i) % n + n) % n] = i < 0 || i >= fs ? v * v : 1;
                }
            }
            return w;
        }

        // ── SOQPSK-TG ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The ternary precoder of IRIG 106: bit n (I for even n, Q for odd) as a ∈ {±1},
        /// α_n = (−1)^(n+1) a_(n−1) (a_n − a_(n−2)) / 2, cyclic over the record.</summary>
        private static int[] SoqpskAlpha(Complex[] pts)
        {
            int nb = 2 * pts.Length;
            int A(int m) { var p = pts[(m % nb + nb) % nb / 2]; return (m % 2 == 0 ? p.Real : p.Imaginary) > 0 ? 1 : -1; }
            var alpha = new int[nb];
            for (int m = 0; m < nb; m++) alpha[m] = (m % 2 == 1 ? 1 : -1) * A(m - 1) * (A(m) - A(m - 2)) / 2;
            return alpha;
        }

        /// <summary>The TG frequency pulse's phase steps per native sample (4 a bit), 32 of them, summing to ½: the step
        /// into sample j − 15 from the pulse's centre. g(t) ∝ cos(πρBu)/(1 − 4(ρBu)²) · sinc(Bu) · w(u), u = t/2T_b,
        /// w = 1 to |u| = T1 and a raised-cosine taper to T1 + T2; ρ 0.7, B 1.25, T1 1.5, T2 0.5.</summary>
        private static double[] SoqpskSteps()
        {
            const double rho = 0.7, bw = 1.25, t1 = 1.5, t2 = 0.5;
            const int half = 16, sub = 64;   // samples either side; integration steps per sample
            static double G(double t)
            {
                double u = t / 8, x = rho * bw * u, d = 1 - 4 * x * x, au = Math.Abs(u);
                double c = Math.Abs(d) < 1e-9 ? Math.PI / 4 : Math.Cos(Math.PI * x) / d;
                double w = au < t1 ? 1 : au <= t1 + t2 ? 0.5 * (1 + Math.Cos(Math.PI * (au - t1) / t2)) : 0;
                return c * Sinc(bw * u) * w;
            }
            var steps = new double[2 * half];
            double total = 0;
            for (int j = 0; j < 2 * half; j++)
            {
                double t0 = j - half, h = 1.0 / sub, acc = G(t0) + G(t0 + 1);
                for (int i = 1; i < sub; i++) acc += G(t0 + i * h) * (i % 2 == 1 ? 4 : 2);
                steps[j] = acc * h / 3;
                total += steps[j];
            }
            for (int j = 0; j < steps.Length; j++) steps[j] *= 0.5 / total;
            return steps;
        }

        /// <summary>SOQPSK-TG at the native rate: the phase π Σ α_n q(t − c_n), α_n's pulse centred where its bit starts
        /// to be held (I_k at its instant − T/2, Q_k at its instant), turned so the first symbol sits on its QPSK point.</summary>
        private static Complex[] Soqpsk(ScShape s, ScSymbols sy)
        {
            const int sps = SamplesPerSymbol, half = 16;
            int n = s.Samples;
            var alpha = SoqpskAlpha(sy.Points);
            var steps = SoqpskSteps();
            var dphi = new double[n];
            for (int m = 0; m < alpha.Length; m++)
            {
                if (alpha[m] == 0) continue;
                int c = sy.At[m / 2] - (m % 2 == 0 ? sps / 2 : 0);
                for (int j = 0; j < steps.Length; j++) dphi[((c + j - half + 1) % n + n) % n] += Math.PI * alpha[m] * steps[j];
            }
            var phi = new double[n];
            double acc = 0;
            for (int i = 0; i < n; i++) phi[i] = acc += dphi[i];
            int probe = sy.At[0] + sps / 4;
            double target = Math.Atan2(sy.Points[0].Imaginary, sy.Points[0].Real);
            double turn = Math.PI / 4 + Math.PI / 2 * Math.Round((target - Math.PI / 4 - phi[probe]) / (Math.PI / 2));
            return [.. phi.Select(p => Complex.FromPolarCoordinates(1, p + turn))];
        }

        // ── the semi-analytic BER ─────────────────────────────────────────────────────────────────────────

        private static double Q(double x) => 0.5 * RfCore.Statistics.Normal.Erfc(x / Math.Sqrt(2));

        /// <summary>
        /// The BER at a symbol SNR γ = Es/N0 (linear) in additive white Gaussian noise: BPSK Q(√(2γ)); QPSK and square QAM
        /// as <see cref="SignalDecisions.QamBer"/>; 8PSK exact (<see cref="Psk8Ber"/>); APSK by the union bound over every
        /// pair of points, (1/(M k)) Σ_i Σ_j d_H(i, j) Q(|s_i − s_j| √(γ/2)), tight at the SNRs it is read at.
        /// </summary>
        public static double Ber(ScShape s, double gamma)
        {
            switch (s.Mod)
            {
                case ScModulation.Bpsk: return Q(Math.Sqrt(2 * gamma));
                // ponytail: SOQPSK-TG read as OQPSK, and differential carriers, take the coherent QPSK/PSK figure; a
                // differential decision roughly doubles it
                case ScModulation.Qpsk or ScModulation.SoqpskTg: return SignalDecisions.QamBer(QamOrder.Qpsk, gamma);
                case ScModulation.Qam16: return SignalDecisions.QamBer(QamOrder.Qam16, gamma);
                case ScModulation.Qam64: return SignalDecisions.QamBer(QamOrder.Qam64, gamma);
                case ScModulation.Qam256: return SignalDecisions.QamBer(QamOrder.Qam256, gamma);
                case ScModulation.Psk8: return Psk8Ber(gamma);
            }
            var c = Constellation(s);
            int m = c.Length, k = s.BitsPerSymbol;
            double sum = 0, r = Math.Sqrt(gamma / 2);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    if (i != j) sum += System.Numerics.BitOperations.PopCount((uint)(i ^ j)) * Q((c[i] - c[j]).Magnitude * r);
            return Math.Min(0.5, sum / (m * k));
        }

        /// <summary>
        /// Gray-coded 8PSK's BER at γ = Es/N0, exact: the received phase's density about the sent point,
        /// p(θ) = e^(−γ)/2π + √γ/(2√π) cos θ e^(−γ sin²θ) (1 + erf(√γ cos θ)), integrated over each other point's
        /// decision sector (Simpson, 400 panels a sector), each weighted by the bits it differs in, over 3.
        /// </summary>
        public static double Psk8Ber(double gamma)
        {
            double sg = Math.Sqrt(gamma);
            double P(double th)
            {
                double c = Math.Cos(th), s = Math.Sin(th);
                return Math.Exp(-gamma) / (2 * Math.PI)
                       + sg / (2 * Math.Sqrt(Math.PI)) * c * Math.Exp(-gamma * s * s) * RfCore.Statistics.Normal.Erfc(-sg * c);
            }
            double ber = 0;
            for (int j = 1; j < 8; j++)
            {
                double a = j * Math.PI / 4 - Math.PI / 8, b = a + Math.PI / 4;
                const int panels = 400;
                double h = (b - a) / panels, sum = P(a) + P(b);
                for (int i = 1; i < panels; i++) sum += P(a + i * h) * (i % 2 == 1 ? 4 : 2);
                ber += System.Numerics.BitOperations.PopCount((uint)(j ^ (j >> 1))) * sum * h / 3;
            }
            return ber / 3;
        }
    }
}
