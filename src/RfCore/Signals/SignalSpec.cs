// ================================================================
//  SignalSpec.cs  —  the description of a modulated test signal
//
//  A signal is a list of carriers (LTE, NR, CW, pulsed CW, FSK, DBPSK,
//  CSS, single carrier PSK/QAM/APSK or an imported I/Q file), each with its own bandwidth, modulation, frequency offset
//  from the source's centre frequency and relative power, plus record
//  length, clipping and spectral-shaping settings.  The spec is plain
//  data: it serialises to the .cmod JSON file the Signal Designer
//  writes and the netlist references, and its canonical JSON is the
//  cache key of the generated waveform.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfCore.Signals
{
    public enum CarrierStandard { Lte, Nr, Cw, File, Pcw, Fsk, Dbpsk, Css, Sc, Wlan, Gnss, Wcdma, Wlan11b, Oqpsk154, Analog, Tones, Fmcw }

    public enum QamOrder { Qpsk = 2, Qam16 = 4, Qam64 = 6, Qam256 = 8, Qam1024 = 10 }

    public sealed record CarrierSpec
    {
        public CarrierStandard Standard { get; init; } = CarrierStandard.Lte;
        /// <summary>Channel bandwidth (Hz): 1.4/3/5/10/15/20 MHz for LTE; 5..100 MHz for NR (FR1), 50..400 MHz (FR2).</summary>
        public double BandwidthHz { get; init; } = 20e6;
        /// <summary>NR subcarrier spacing (Hz): 15, 30, 60 or 120 kHz.  Ignored for LTE (15 kHz).</summary>
        public double ScsHz { get; init; } = 30e3;
        public QamOrder Modulation { get; init; } = QamOrder.Qam64;
        /// <summary>Carrier offset from the source centre frequency (Hz).</summary>
        public double OffsetHz { get; init; }
        /// <summary>Carrier power relative to the other carriers (dB); the source's Pavg sets the total.</summary>
        public double PowerDb { get; init; }
        public int Seed { get; init; } = 1;
        /// <summary>I/Q file for <see cref="CarrierStandard.File"/> (Waveform_Generator .txt, or two-column I Q text).</summary>
        public string? FilePath { get; init; }
        /// <summary>Sample rate (Hz) of a <see cref="CarrierStandard.File"/> carrier's I/Q; null = the file's own
        /// <c># sample_rate_hz</c> header. Stated, it wins over the header — a file with none needs it.</summary>
        public double? SampleRateHz { get; init; }

        // A pulsed-CW carrier's timing (s); null = the default SignalSpec.ResolvePulse applies. Null rather than defaulted so
        // the JSON, and with it the cache key, of every other carrier is what it was before pCW existed.
        /// <summary>Pulse repetition interval: the record is one PRI.</summary>
        public double? PriSeconds { get; init; }
        /// <summary>Pulse width, 50 % to 50 %.</summary>
        public double? PulseWidthSeconds { get; init; }
        /// <summary>10-90 % times of the raised-cosine edges.</summary>
        public double? RiseSeconds { get; init; }
        public double? FallSeconds { get; init; }
        /// <summary>Off-time before the rising edge leaves 0; null centres the pulse in the PRI.</summary>
        public double? DelaySeconds { get; init; }
        /// <summary>The droop window, as fractions of the flat top (null = 0.1 and 0.9).</summary>
        public double? MeasureStart { get; init; }
        public double? MeasureEnd { get; init; }
        /// <summary>A linear-FM chirp across the pulse width (Hz): positive sweeps up, negative down, centred on the
        /// carrier. Null (never 0) for a plain pulse, so its JSON and cache key are what they were.</summary>
        public double? ChirpHz { get; init; }
        // A pulse's radar options (SignalSpec.ResolvePulse); null = off, so a plain pulse's JSON and cache key are what they were.
        /// <summary>An intra-pulse phase code, kind:length[:root] (<c>Barker:13</c>, <c>P3:64</c>, <c>ZadoffChu:63:5</c>).</summary>
        public string? PulseCode { get; init; }
        public double? ChipSmoothing { get; init; }
        /// <summary>The law the chirp is swept with (null = linear), and its tangent β or sine k.</summary>
        public PulseFm? PulseFm { get; init; }
        public double? FmParam { get; init; }
        /// <summary>A Gaussian envelope of half-amplitude width PW.</summary>
        public bool? GaussianPulse { get; init; }
        /// <summary>A train: staggered PRIs (times, comma-separated), jitter (fraction; drawn from Seed), pulses before
        /// the dwell, the dwell (s), frequency steps (Hz, count) and a pair's spacing (s).</summary>
        public string? TrainPris { get; init; }
        public double? Jitter { get; init; }
        public int? Pulses { get; init; }
        public double? DwellS { get; init; }
        public double? StepHz { get; init; }
        public int? Steps { get; init; }
        public double? PairS { get; init; }
        /// <summary>An FMCW carrier's ramp (s) and whether it is a triangle; its sweep is BandwidthHz, its reset IdleS.</summary>
        public double? RampS { get; init; }
        public bool? Triangle { get; init; }

        // An FSK carrier's settings; null = the default Fsk.Resolve applies, for the same reason as the pulse's.
        /// <summary>Bit (chip) rate, b/s; a DBPSK carrier's too.</summary>
        public double? BitRateBps { get; init; }
        /// <summary>Peak frequency deviation (Hz): a 1 sits at +Dev, a 0 at -Dev.</summary>
        public double? DeviationHz { get; init; }
        /// <summary>The Gaussian filter's bandwidth-time product; null or 0 = no filter (rectangular CPFSK).</summary>
        public double? GaussianBt { get; init; }
        /// <summary>PN9, PN15, or a fixed pattern of 0s and 1s; the record is one period of it. A DBPSK carrier's too.</summary>
        public string? Data { get; init; }
        /// <summary>FSK levels: 2, 4 or 8 (null = 2).</summary>
        public int? Levels { get; init; }
        /// <summary>FSK: the odd level of each symbol value, comma-separated (null = Fsk.DefaultMapping).</summary>
        public string? Mapping { get; init; }
        /// <summary>FSK: the frequency pulse (null = NRZ); its roll-off is <see cref="RollOff"/>.</summary>
        public FskPulse? FskPulse { get; init; }
        /// <summary>FSK: L, symbols, of an LREC / LRC pulse.</summary>
        public int? PulseLength { get; init; }
        public bool? Manchester { get; init; }
        /// <summary>FSK: a standard's settings and burst (FskPresets.Key), the fields stated over it.</summary>
        public string? Preset { get; init; }

        /// <summary>A CSS carrier's spreading factor (null = <see cref="Css.DefaultSf"/>); its chirp bandwidth is
        /// <see cref="BandwidthHz"/>.</summary>
        public int? SpreadingFactor { get; init; }

        // A single-carrier (SC) carrier's settings (brief PCH-5); null = ScQam.Resolve's default, so no other carrier's
        // JSON or cache key changes.
        /// <summary>Symbol rate Rs, symbols/s.</summary>
        public double? SymbolRateHz { get; init; }
        public ScModulation? ScModulation { get; init; }
        /// <summary>The root-raised-cosine roll-off α, 0 to 1.</summary>
        public double? RollOff { get; init; }
        /// <summary>The shaping filter's span in symbols; null = the whole record.</summary>
        public int? Span { get; init; }
        /// <summary>APSK: each outer ring's radius over the inner ring's, comma-separated.</summary>
        public string? Rings { get; init; }
        /// <summary>APSK: each ring's phase offset in degrees, inner first, comma-separated.</summary>
        public string? RingPhases { get; init; }
        /// <summary>Known symbols at the start of the record.</summary>
        public int? Preamble { get; init; }
        /// <summary>"L/P": L known symbols after every P data symbols.</summary>
        public string? PilotBlock { get; init; }
        /// <summary>Data symbols in the record; null = one data period, at most <see cref="ScQam.DefaultMaxSymbols"/>.</summary>
        public int? Symbols { get; init; }
        /// <summary>The pulse (null = RRC); a Gaussian pulse's BT is <see cref="GaussianBt"/>.</summary>
        public ScPulse? ScPulse { get; init; }
        /// <summary>Degrees each symbol turns by from the one before.</summary>
        public double? RotationDeg { get; init; }
        /// <summary>Each point the previous one times the label's (BPSK, QPSK, 8PSK).</summary>
        public bool? Differential { get; init; }
        /// <summary>Q half a symbol after I.</summary>
        public bool? OffsetQ { get; init; }
        /// <summary>Data symbols per frame, each with its own preamble and pilot blocks.</summary>
        public int? FrameData { get; init; }
        /// <summary>Symbol periods of silence after each frame, and of each raised-cosine ramp inside it.</summary>
        public double? GuardSymbols { get; init; }
        public double? RampSymbols { get; init; }

        /// <summary>A CW carrier's phase at the start of the record (degrees); null = 0. The N-tone preset sets it.</summary>
        public double? PhaseDeg { get; init; }

        // A Wi-Fi (WLAN) packet's settings; null = Wlan.Resolve's default (WlanSpec's). Its BW is BandwidthHz, its data
        // symbols Symbols, and its random points come from Seed.
        public WlanFormat? WlanFormat { get; init; }
        public int? Mcs { get; init; }
        /// <summary>The data guard interval (s).</summary>
        public double? GuardIntervalS { get; init; }
        /// <summary>The gap after the packet (s).</summary>
        public double? IdleS { get; init; }
        /// <summary>The record's rate over the channel's base rate.</summary>
        public int? Oversample { get; init; }
        /// <summary>The raised-cosine transition between OFDM symbols (s).</summary>
        public double? WindowS { get; init; }

        // A GNSS carrier's settings; null = Gnss.Resolve's default (GPS L1 C/A, PRN 1, no noise, 20 ms). Seed draws its data,
        // carrier phases and noise.
        public GnssSignal? GnssSignal { get; init; }
        /// <summary>The satellites in view, comma-separated, each PRN@Doppler@code phase (chips)@power (dB), the last three
        /// optional: <c>1@1.8kHz@12@0dB,5@-2kHz</c>.</summary>
        public string? Satellites { get; init; }
        /// <summary>The reference satellite's C/N0 (dB-Hz); null = no noise.</summary>
        public double? Cn0DbHz { get; init; }
        /// <summary>The record (s) of a carrier that states its own (GNSS).</summary>
        public double? RecordS { get; init; }

        // DSSS carriers: a WCDMA test model and its scrambling code; an 802.11b or 802.15.4 frame's PSDU (an 802.11b
        // carrier's rate is BitRateBps). Null = the family's default (TM1 64 DPCH, code 0; 11 Mb/s, 1000 B; 127 B).
        public WcdmaModel? WcdmaModel { get; init; }
        public int? ScramblingCode { get; init; }
        public int? PsduBytes { get; init; }

        // An analog or navigation carrier's settings; null = Analog.Resolve's default (AnalogShape's: AM of one 1 kHz tone).
        // Its FM deviation is DeviationHz, its record RecordS, its noise message's draw Seed.
        public AnalogKind? AnalogKind { get; init; }
        /// <summary>The message tones, comma-separated, each frequency@level (dB)@phase (degrees), the last two optional.</summary>
        public string? Tones { get; init; }
        /// <summary>A Gaussian noise message to this bandwidth (Hz) in place of the tones.</summary>
        public double? NoiseBandwidthHz { get; init; }
        public double? Depth { get; init; }
        /// <summary>FM: the deviation as Index times the top message frequency.</summary>
        public double? ModIndex { get; init; }
        public double? PeakPhaseRad { get; init; }
        public double? BearingDeg { get; init; }
        public double? Ddm { get; init; }
        public double? Sdm { get; init; }
        public string? Ident { get; init; }

        // A tones-and-noise (TONES) carrier's settings; null = MultitoneShape's default (two tones 1 MHz apart, Newman
        // phases). Its noise band is NoiseBandwidthHz, its record RecordS, its random phases' and noise's draw Seed.
        public int? ToneCount { get; init; }
        public double? ToneSpacingHz { get; init; }
        public TonePhase? TonePhase { get; init; }
        /// <summary>Each tone's power (dB, relative), comma-separated; null = equal.</summary>
        public string? TonePowers { get; init; }
        /// <summary>With tones: the noise's total over the tones' total (dB).</summary>
        public double? NoiseDb { get; init; }
        public double? NotchCentreHz { get; init; }
        public double? NotchWidthHz { get; init; }
        /// <summary>The notch as tone indices (0-based), comma-separated.</summary>
        public string? NotchTones { get; init; }

        public string Label => Standard switch
        {
            CarrierStandard.Lte => $"LTE {BandwidthHz / 1e6:0.#} MHz {ModName(Modulation)}",
            CarrierStandard.Nr => $"NR {BandwidthHz / 1e6:0.#} MHz {ScsHz / 1e3:0} kHz {ModName(Modulation)}",
            CarrierStandard.Cw => PhaseDeg is double ph ? $"CW {ph.ToString("0.##", CultureInfo.InvariantCulture)}°" : "CW",
            CarrierStandard.Pcw => $"pCW {PulsedCw.FormatTime(SignalSpec.ResolvePulse(this).Width)} / {PulsedCw.FormatTime(SignalSpec.ResolvePulse(this).Pri)}"
                                   + (ChirpHz is double ch ? $" chirp {(ch > 0 ? "up" : "down")} {Fsk.FormatHz(Math.Abs(ch))}" : "")
                                   + (PulseCode is { } code ? $" {code}" : "") + (GaussianPulse == true ? " Gaussian" : "")
                                   + (SignalSpec.ResolvePulse(this).Train is { } tr ? $" train of {tr.PulseCount}" : ""),
            CarrierStandard.Fmcw => $"FMCW {(Triangle == true ? "triangle" : "sawtooth")} {Fsk.FormatHz(BandwidthHz)} / {PulsedCw.FormatTime(RampS ?? SignalSpec.DefaultFmcwRamp)}",
            CarrierStandard.Fsk => (Preset is { } pre ? pre + ": " : "") + FskLabel(Fsk.Resolve(this)),
            CarrierStandard.Dbpsk => $"DBPSK {Fsk.FormatBitRate(Dbpsk.Resolve(this).BitRate)} {DataLabel(Dbpsk.Resolve(this).Data)}",
            CarrierStandard.Css => $"CSS SF{Css.Resolve(this).Sf} {Fsk.FormatHz(BandwidthHz)}",
            CarrierStandard.Sc => ScLabel(ScQam.Resolve(this)),
            CarrierStandard.Wlan => WlanLabel(Signals.Wlan.Resolve(this)),
            CarrierStandard.Gnss => GnssLabel(Signals.Gnss.Resolve(this)),
            CarrierStandard.Wcdma => $"WCDMA {Signals.Wcdma.ModelName(Signals.Wcdma.Resolve(this).Model)}" + (ScramblingCode is int sc ? $" code {sc}" : ""),
            CarrierStandard.Wlan11b => $"802.11b {Ieee80211b.Resolve(this).RateMbps.ToString(CultureInfo.InvariantCulture)} Mb/s {Ieee80211b.Resolve(this).PsduBytes} B",
            CarrierStandard.Oqpsk154 => $"802.15.4 O-QPSK {Ieee802154.Resolve(this).PsduBytes} B",
            CarrierStandard.Tones => TonesLabel(SignalSpec.ResolveTones(this)),
            CarrierStandard.Analog => $"{Signals.Analog.Label(Signals.Analog.Resolve(this).Kind)} {Fsk.FormatHz(Signals.Analog.BandwidthHz(Signals.Analog.Validate(Signals.Analog.Resolve(this))))}",
            _ => $"File {System.IO.Path.GetFileName(FilePath ?? "")}",
        };

        private static string FskLabel(FskShape s)
            => $"{s.Kind} {Fsk.FormatBitRate(s.BitRate)}{(s.Levels > 2 ? $" ({SignalSpec.FormatSymbolRate(s.SymbolRate)})" : "")} ±{Fsk.FormatHz(s.Deviation)}{(s.Bt > 0 ? $" BT {s.Bt.ToString("0.###", CultureInfo.InvariantCulture)}" : "")} "
             + DataLabel(s.Data);

        private static string DataLabel(string data) => data is "PN9" or "PN15" or "PN23" ? data : $"{data.Length}-bit pattern";

        private static string ScLabel(ScShape s)
            => $"SC {SignalSpec.FormatSymbolRate(s.SymbolRate)} {ScQam.ModName(s.Mod)} α {s.RollOff.ToString("0.###", CultureInfo.InvariantCulture)}"
               + (s.Pulse == Signals.ScPulse.Rrc ? "" : $" {ScQam.PulseName(s.Pulse)}");

        // Pavg is the packet's power: the duty says what the record's mean is
        private static string WlanLabel(WlanSpec s)
            => $"Wi-Fi 802.11{Signals.Wlan.StdName(s.Format)} {s.BandwidthHz / 1e6:0} MHz MCS{s.Mcs} " +
               $"({(100 * Signals.Wlan.DutyCycle(s)).ToString("0.#", CultureInfo.InvariantCulture)} % duty)";

        private static string GnssLabel(GnssShape s)
            => $"{s.Band.Name} {s.Satellites.Length} SV" + (s.Cn0DbHz is double n ? $" {n.ToString("0.#", CultureInfo.InvariantCulture)} dB-Hz" : "");

        private static string TonesLabel(MultitoneShape s)
            => string.Join(" + ", new[]
               {
                   s.Tones > 0 ? $"{s.Tones} tone{(s.Tones == 1 ? "" : "s")}" + (s.Tones > 1 ? $" × {Fsk.FormatHz(s.SpacingHz)}" : "") : null,
                   s.NoiseBandwidthHz > 0 ? $"noise {Fsk.FormatHz(s.NoiseBandwidthHz)}" : null,
               }.Where(t => t is not null))
               + (s.NotchWidthHz > 0 ? $", notch {Fsk.FormatHz(s.NotchWidthHz)}" : s.NotchTones is { Length: > 0 } nt ? $", {nt.Length} tones out" : "");

        public static string ModName(QamOrder m) => m switch
        {
            QamOrder.Qpsk => "QPSK",
            QamOrder.Qam16 => "16QAM",
            QamOrder.Qam64 => "64QAM",
            QamOrder.Qam256 => "256QAM",
            _ => "1024QAM",
        };
    }

    public sealed record SignalSpec
    {
        public List<CarrierSpec> Carriers { get; init; } = new() { new CarrierSpec() };
        /// <summary>Periodic record length (s); rounded to whole 0.5 ms slots for LTE/NR.</summary>
        public double RecordSeconds { get; init; } = 1e-3;
        /// <summary>Clip to this PAPR (dB) with iterative clip-and-filter; null = unclipped (natural OFDM PAPR).</summary>
        public double? ClipPaprDb { get; init; }
        /// <summary>Spectral-shaping floor (dBc per bin, relative to the in-band mean) outside the channel; null = off.</summary>
        public double? ShapingFloorDbc { get; init; } = -80;
        /// <summary>Raised-cosine symbol window as a fraction of the normal cyclic prefix.</summary>
        public double WindowFraction { get; init; } = 0.2;

        // -2: clipping meets its target as the PEAK at 4x oversampling (it clipped at the native rate before)
        // -3: peak cancellation instead of clip-and-filter (same PAPR, lower EVM)
        // -4: the clipping target is judged at 8x oversampling (4x let NR 100 peaks through between samples)
        // -5: a FILE carrier takes its bandwidth from the file (the cache stores it) and is clipped like a generated one
        public const string GeneratorVersion = "crf-signals-5";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

        public static SignalSpec FromJson(string json)
            => JsonSerializer.Deserialize<SignalSpec>(json, JsonOpts)
               ?? throw new FormatException("empty signal description");

        /// <summary>Cache key: SHA-256 over the canonical JSON and the generator version.</summary>
        public string CacheKey()
        {
            var canon = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }) + "|" + GeneratorVersion + FileStamps();
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(canon));
            return Convert.ToHexString(h, 0, 12).ToLowerInvariant();
        }

        /// <summary>Each I/Q file's length and write time, so a file edited in place is read again rather than served
        /// from the cache under its unchanged path.</summary>
        private string FileStamps()
        {
            var sb = new StringBuilder();
            foreach (var c in Carriers.Where(c => c.Standard == CarrierStandard.File && !string.IsNullOrEmpty(c.FilePath)))
            {
                try
                {
                    var fi = new System.IO.FileInfo(c.FilePath!);
                    sb.Append('|').Append(fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "missing");
                }
                catch (Exception) { sb.Append("|unreadable"); }
            }
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="carrierText"/> with its <c>File=</c> value passed through <paramref name="map"/> and every
        /// other field left exactly as written — how a stored (workspace-relative) path is resolved for a run, without
        /// re-formatting the description and dropping what the formatter does not write.
        /// </summary>
        public static string MapFilePath(string carrierText, Func<string, string> map)
        {
            var parts = carrierText.Split(';');
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq > 0 && parts[i][..eq].Trim().Equals("File", StringComparison.OrdinalIgnoreCase))
                    parts[i] = parts[i][..(eq + 1)] + map(parts[i][(eq + 1)..].Trim().Trim('"'));
            }
            return string.Join(';', parts);
        }

        /// <summary>Instantaneous bandwidth: lowest carrier edge to highest carrier edge (Hz).</summary>
        public double InstantaneousBandwidthHz
        {
            get
            {
                if (Carriers.Count == 0) return 0;
                double lo = Carriers.Min(c => c.OffsetHz - c.BandwidthHz / 2);
                double hi = Carriers.Max(c => c.OffsetHz + c.BandwidthHz / 2);
                return hi - lo;
            }
        }

        public string Summary
            => Carriers.Count == 1
                ? Carriers[0].Label
                : $"{Carriers.Count} carriers, IBW {InstantaneousBandwidthHz / 1e6:0.#} MHz";

        /// <summary>
        /// Parses the compact inline carrier form used in netlists:
        /// <c>"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-200MHz;P=0;Seed=3"</c>, or for an imported I/Q file
        /// <c>"FILE;File=iq/capture.txt;Fs=122.88MHz;Off=0;P=0"</c>, or for a pulse train
        /// <c>"PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"</c> (<c>Chirp=-20MHz</c> adds a linear-FM
        /// sweep down over the width; <c>Chirp=</c> is a frequency, signed), or for FSK
        /// <c>"FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"</c>, or for DBPSK <c>"DBPSK;Rb=100bps;Data=PN9;Off=0;P=0"</c>,
        /// or for a CW tone with its starting phase in degrees <c>"CW;Phase=90;Off=0.5MHz;P=0;Seed=1"</c>,
        /// or for chirp spread spectrum <c>"CSS;SF=7;BW=125kHz;Off=0;P=0"</c>, or for a single carrier
        /// <c>"SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;Rings=2.85;Phases=45,15;Data=PN15;Preamble=64;PilotBlock=36/1440;Off=0;P=0"</c>
        /// (Rs takes sps, ksps or Msps; <see cref="ScQam"/> states every default). BW, SCS, Fs, Dev and Off take Hz, kHz,
        /// MHz or GHz after the number; a bare number is Hz (<c>BW=20e6</c>). PRI, PW, Rise, Fall and Delay take s, ms,
        /// us (or µs) or ns; a bare number is seconds. Rb takes bps, kbps or Mbps; a bare number is b/s.
        /// </summary>
        public static CarrierSpec ParseCarrier(string text)
        {
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) throw new FormatException("empty carrier description");
            var c = new CarrierSpec
            {
                Standard = parts[0].ToUpperInvariant() switch
                {
                    "LTE" => CarrierStandard.Lte,
                    "NR" => CarrierStandard.Nr,
                    "CW" => CarrierStandard.Cw,
                    "FILE" => CarrierStandard.File,
                    "PCW" => CarrierStandard.Pcw,
                    "FSK" => CarrierStandard.Fsk,
                    "DBPSK" => CarrierStandard.Dbpsk,
                    "CSS" => CarrierStandard.Css,
                    "SC" => CarrierStandard.Sc,
                    "WLAN" => CarrierStandard.Wlan,
                    "GNSS" => CarrierStandard.Gnss,
                    "WCDMA" => CarrierStandard.Wcdma,
                    "WLAN11B" => CarrierStandard.Wlan11b,
                    "OQPSK154" => CarrierStandard.Oqpsk154,
                    "ANALOG" => CarrierStandard.Analog,
                    "TONES" => CarrierStandard.Tones,
                    "FMCW" => CarrierStandard.Fmcw,
                    _ => throw new FormatException($"unknown carrier standard '{parts[0]}' (LTE, NR, CW, PCW, FSK, DBPSK, CSS, SC, WLAN, GNSS, WCDMA, WLAN11B, OQPSK154, ANALOG, TONES, FMCW or FILE)"),
                },
            };
            bool bwStated = parts.Skip(1).Any(p => p.Split('=')[0].Trim().Equals("BW", StringComparison.OrdinalIgnoreCase));
            foreach (var p in parts.Skip(1))
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) throw new FormatException($"carrier field '{p}' is not key=value");
                string k = p[..eq].Trim().ToUpperInvariant(), v = p[(eq + 1)..].Trim();
                double Num() => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
                c = k switch
                {
                    "BW" => c with { BandwidthHz = ParseHz(k, v) },
                    "SCS" => c with { ScsHz = ParseHz(k, v) },
                    "OFF" or "OFFSET" => c with { OffsetHz = ParseHz(k, v) },
                    "P" or "POWER" => c with { PowerDb = Num() },
                    "SEED" => c with { Seed = (int)Num() },
                    "FILE" => c with { FilePath = v.Trim('"') },
                    "FS" => c with { SampleRateHz = ParseHz(k, v) },
                    "MOD" when c.Standard == CarrierStandard.Sc => c with { ScModulation = ScQam.ParseMod(v) },
                    "MOD" => c with { Modulation = ParseMod(v) },
                    "PRI" => c with { PriSeconds = PulseField(c, k, v) },
                    "PW" => c with { PulseWidthSeconds = PulseField(c, k, v) },
                    "RISE" => c with { RiseSeconds = PulseField(c, k, v) },
                    "FALL" => c with { FallSeconds = PulseField(c, k, v) },
                    "DELAY" => c with { DelaySeconds = PulseField(c, k, v) },
                    "MEASURESTART" => c with { MeasureStart = PulseFraction(c, k, v) },
                    "MEASUREEND" => c with { MeasureEnd = PulseFraction(c, k, v) },
                    "CHIRP" => c with { ChirpHz = c.Standard == CarrierStandard.Pcw ? ParseHz(k, v) is var ch && ch != 0 ? ch : null
                                                  : throw new FormatException($"carrier field {k} belongs to a PCW carrier") },
                    "RB" when c.Standard == CarrierStandard.Wlan11b => c with { BitRateBps = ParseBitRate(k, v) },
                    "RB" => c with { BitRateBps = FskField(c, k, () => ParseBitRate(k, v), alsoDbpsk: true) },
                    "DEV" when c.Standard == CarrierStandard.Analog => c with { DeviationHz = ParseHz(k, v) },
                    "DEV" => c with { DeviationHz = FskField(c, k, () => ParseHz(k, v)) },
                    "BT" when c.Standard == CarrierStandard.Sc => c with { GaussianBt = Num() },
                    "BT" => c with { GaussianBt = FskField(c, k, Num) },
                    "DATA" => c with { Data = FskField(c, k, () => v, alsoDbpsk: true) },
                    "RS" => c with { SymbolRateHz = ScField(c, k, () => ParseSymbolRate(k, v)) },
                    "ROLLOFF" when c.Standard == CarrierStandard.Fsk => c with { RollOff = Num() },
                    "ROLLOFF" => c with { RollOff = ScField(c, k, Num) },
                    "SPAN" => c with { Span = ScField(c, k, () => WholeField(k, v)) },
                    "RINGS" => c with { Rings = ScField(c, k, () => v) },
                    "PHASES" => c with { RingPhases = ScField(c, k, () => v) },
                    "PREAMBLE" => c with { Preamble = ScField(c, k, () => WholeField(k, v)) },
                    "PILOTBLOCK" => c with { PilotBlock = ScField(c, k, () => v) },
                    "SYMBOLS" when c.Standard == CarrierStandard.Wlan => c with { Symbols = WholeField(k, v) },
                    "SYMBOLS" => c with { Symbols = ScField(c, k, () => WholeField(k, v)) },
                    "STD" => c with { WlanFormat = Only(c, k, () => Signals.Wlan.ParseStd(v), CarrierStandard.Wlan) },
                    "MCS" => c with { Mcs = Only(c, k, () => WholeField(k, v), CarrierStandard.Wlan) },
                    "GI" => c with { GuardIntervalS = Only(c, k, () => ParseSeconds(k, v), CarrierStandard.Wlan) },
                    "IDLE" => c with { IdleS = Only(c, k, () => ParseSeconds(k, v), CarrierStandard.Wlan, CarrierStandard.Fmcw) },
                    "CODE" when c.Standard == CarrierStandard.Pcw => c with { PulseCode = Checked(v, () => ParsePulseCode(v)) },
                    "CHIPSMOOTHING" => c with { ChipSmoothing = PulseOnly(c, k, Num) },
                    "FM" => c with { PulseFm = PulseOnly(c, k, () => ParsePulseFm(v)) },
                    "FMPARAM" => c with { FmParam = PulseOnly(c, k, Num) },
                    "GAUSSIAN" => c with { GaussianPulse = PulseOnly(c, k, () => Flag(k, v)) },
                    "PRIS" => c with { TrainPris = PulseOnly(c, k, () => Checked(v, () => ParseTimes(v))) },
                    "JITTER" => c with { Jitter = PulseOnly(c, k, Num) },
                    "PULSES" => c with { Pulses = PulseOnly(c, k, () => WholeField(k, v)) },
                    "DWELL" => c with { DwellS = PulseOnly(c, k, () => ParseSeconds(k, v)) },
                    "STEP" => c with { StepHz = PulseOnly(c, k, () => ParseHz(k, v)) },
                    "STEPS" => c with { Steps = PulseOnly(c, k, () => WholeField(k, v)) },
                    "PAIR" => c with { PairS = PulseOnly(c, k, () => ParseSeconds(k, v)) },
                    "TRIANGLE" => c with { Triangle = Only(c, k, () => Flag(k, v), CarrierStandard.Fmcw) },
                    "RAMP" when c.Standard == CarrierStandard.Fmcw => c with { RampS = ParseSeconds(k, v) },
                    "OVERSAMPLE" => c with { Oversample = Only(c, k, () => WholeField(k, v), CarrierStandard.Wlan) },
                    "SIGNAL" => c with { GnssSignal = Only(c, k, () => Signals.Gnss.ParseSignal(v), CarrierStandard.Gnss) },
                    "SATS" => c with { Satellites = Only(c, k, () => v, CarrierStandard.Gnss) },
                    "CN0" => c with { Cn0DbHz = Only(c, k, () => double.Parse(v.Replace("dBHz", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture), CarrierStandard.Gnss) },
                    "RECORD" => c with { RecordS = Only(c, k, () => ParseSeconds(k, v), CarrierStandard.Gnss, CarrierStandard.Analog, CarrierStandard.Tones) },
                    "KIND" => c with { AnalogKind = Only(c, k, () => Signals.Analog.ParseKind(v), CarrierStandard.Analog) },
                    "TONES" => c with { Tones = Only(c, k, () => v, CarrierStandard.Analog) },
                    "NOISEBW" => c with { NoiseBandwidthHz = Only(c, k, () => ParseHz(k, v), CarrierStandard.Analog, CarrierStandard.Tones) },
                    "N" => c with { ToneCount = Only(c, k, () => WholeField(k, v), CarrierStandard.Tones) },
                    "SPACING" => c with { ToneSpacingHz = Only(c, k, () => ParseHz(k, v), CarrierStandard.Tones) },
                    "POWERS" => c with { TonePowers = Only(c, k, () => v, CarrierStandard.Tones) },
                    "NOISEDB" => c with { NoiseDb = Only(c, k, Num, CarrierStandard.Tones) },
                    "NOTCH" => Only(c, k, () => v.Split('/') is [var nc, var nw]
                        ? c with { NotchCentreHz = ParseHz(k, nc.Trim()), NotchWidthHz = ParseHz(k, nw.Trim()) }
                        : throw new FormatException($"carrier field {k}={v} is centre/width: 0/1MHz"), CarrierStandard.Tones),
                    "NOTCHTONES" => c with { NotchTones = Only(c, k, () => v, CarrierStandard.Tones) },
                    "DEPTH" => c with { Depth = Only(c, k, Num, CarrierStandard.Analog) },
                    "INDEX" => c with { ModIndex = Only(c, k, Num, CarrierStandard.Analog) },
                    "PEAKPHASE" => c with { PeakPhaseRad = Only(c, k, Num, CarrierStandard.Analog) },
                    "BEARING" => c with { BearingDeg = Only(c, k, Num, CarrierStandard.Analog) },
                    "DDM" => c with { Ddm = Only(c, k, Num, CarrierStandard.Analog) },
                    "SDM" => c with { Sdm = Only(c, k, Num, CarrierStandard.Analog) },
                    "IDENT" => c with { Ident = Only(c, k, () => v, CarrierStandard.Analog) },
                    "MODEL" => c with { WcdmaModel = Only(c, k, () => Signals.Wcdma.ParseModel(v), CarrierStandard.Wcdma) },
                    "CODE" => c with { ScramblingCode = Only(c, k, () => WholeField(k, v), CarrierStandard.Wcdma) },
                    "PSDU" => c with { PsduBytes = Only(c, k, () => WholeField(k, v), CarrierStandard.Wlan11b, CarrierStandard.Oqpsk154) },
                    "PULSE" when c.Standard == CarrierStandard.Sc => c with { ScPulse = ScQam.ParsePulse(v) },
                    "PULSE" => c with { FskPulse = Only(c, k, () => Fsk.ParsePulse(v), CarrierStandard.Fsk, CarrierStandard.Sc) },
                    "LEVELS" => c with { Levels = FskField(c, k, () => WholeField(k, v)) },
                    "MAPPING" => c with { Mapping = FskField(c, k, () => v) },
                    "PULSELENGTH" => c with { PulseLength = FskField(c, k, () => WholeField(k, v)) },
                    "MANCHESTER" => c with { Manchester = FskField(c, k, () => Flag(k, v)) },
                    "PRESET" => c with { Preset = FskField(c, k, () => FskPresets.Find(v) is { } fp ? FskPresets.Key(fp) : v) },
                    "ROTATION" => c with { RotationDeg = ScField(c, k, Num) },
                    "DIFFERENTIAL" => c with { Differential = ScField(c, k, () => Flag(k, v)) },
                    "OFFSETQ" => c with { OffsetQ = ScField(c, k, () => Flag(k, v)) },
                    "FRAMEDATA" => c with { FrameData = ScField(c, k, () => WholeField(k, v)) },
                    "GUARD" => c with { GuardSymbols = ScField(c, k, Num) },
                    "RAMP" => c with { RampSymbols = ScField(c, k, Num) },
                    "WINDOW" => c with { WindowS = Only(c, k, () => ParseSeconds(k, v), CarrierStandard.Wlan) },
                    "SF" => c with { SpreadingFactor = CssField(c, k, v) },
                    "PHASE" when c.Standard == CarrierStandard.Cw => c with { PhaseDeg = Num() },
                    "PHASE" when c.Standard == CarrierStandard.Tones => c with { TonePhase = ParseTonePhase(v) },
                    "PHASE" => throw new FormatException($"carrier field {k} belongs to a CW carrier"),
                    _ => throw new FormatException($"unknown carrier field '{k}'"),
                };
            }
            // A pulse's bandwidth is its edges', never the 20 MHz a CarrierSpec defaults to.
            if (c.Standard == CarrierStandard.Pcw) c = c with { BandwidthHz = PulsedCw.BandwidthHz(PulsedCw.Check(ResolvePulse(c))) };
            // an FMCW carrier's BW is its sweep, as stated (the composer's record carries the occupied band, the sweep over 0.9)
            if (c.Standard == CarrierStandard.Fmcw) ResolveFmcw(c).Check();
            // an FSK carrier's is Carson's, from its rate and deviation
            if (c.Standard == CarrierStandard.Fsk) c = c with { BandwidthHz = Fsk.BandwidthHz(Fsk.Validate(c)) };
            // a DBPSK carrier's is its pulse's main lobe, from its rate
            if (c.Standard == CarrierStandard.Dbpsk) c = c with { BandwidthHz = Dbpsk.BandwidthHz(Dbpsk.Validate(c)) };
            // a CSS carrier's is its chirp's: BW when stated, the 125 kHz of an 868 MHz LPWAN channel otherwise
            if (c.Standard == CarrierStandard.Css)
            {
                if (!bwStated) c = c with { BandwidthHz = Css.DefaultBandwidthHz };
                Css.Validate(c);
            }
            // a single carrier's is Rs(1 + α), where its root-raised-cosine spectrum ends
            if (c.Standard == CarrierStandard.Sc) c = c with { BandwidthHz = ScQam.BandwidthHz(ScQam.Validate(c)) };
            // a Wi-Fi packet's is its channel's, BW as stated (20 MHz unstated)
            if (c.Standard == CarrierStandard.Wlan) Signals.Wlan.Validate(Signals.Wlan.Resolve(c));
            // a GNSS carrier's is its code's main lobe
            // a DSSS carrier's is its channel's: WCDMA 5 MHz, 802.11b 22 MHz, 802.15.4 5 MHz
            if (c.Standard == CarrierStandard.Wcdma) { Signals.Wcdma.Resolve(c); c = c with { BandwidthHz = Signals.Wcdma.ChannelBandwidthHz }; }
            if (c.Standard == CarrierStandard.Wlan11b) { Ieee80211b.Resolve(c); c = c with { BandwidthHz = Ieee80211b.ChannelBandwidthHz }; }
            if (c.Standard == CarrierStandard.Oqpsk154) { Ieee802154.Resolve(c); c = c with { BandwidthHz = Ieee802154.ChannelBandwidthHz }; }
            // an analog carrier's is its occupied band (Carson's for FM and PM)
            if (c.Standard == CarrierStandard.Analog) c = c with { BandwidthHz = Signals.Analog.BandwidthHz(Signals.Analog.Validate(Signals.Analog.Resolve(c))) };
            // tones and noise: the tones' extent plus a spacing, or the noise band
            if (c.Standard == CarrierStandard.Tones) c = c with { BandwidthHz = Multitone.BandwidthHz(Multitone.Validate(ResolveTones(c))) };
            if (c.Standard == CarrierStandard.Gnss) c = c with { BandwidthHz = Signals.Gnss.BandwidthHz(Signals.Gnss.Validate(Signals.Gnss.Resolve(c))) };
            return c;
        }

        private static T FskField<T>(CarrierSpec c, string key, Func<T> read, bool alsoDbpsk = false)
        {
            if (c.Standard != CarrierStandard.Fsk && !(alsoDbpsk && c.Standard is CarrierStandard.Dbpsk or CarrierStandard.Sc))
                throw new FormatException($"carrier field {key} belongs to an FSK{(alsoDbpsk ? ", DBPSK or SC" : "")} carrier");
            return read();
        }

        /// <summary>A field only the listed standards take; any other carrier refuses it, naming them.</summary>
        private static T Only<T>(CarrierSpec c, string key, Func<T> read, params CarrierStandard[] standards)
        {
            if (!standards.Contains(c.Standard))
            {
                string names = string.Join(" or ", standards.Select(s => s.ToString().ToUpperInvariant()));
                throw new FormatException($"carrier field {key} belongs to {("AEFHILMNORSX".Contains(names[0]) ? "an" : "a")} {names} carrier");
            }
            return read();
        }

        private static T ScField<T>(CarrierSpec c, string key, Func<T> read)
        {
            if (c.Standard != CarrierStandard.Sc) throw new FormatException($"carrier field {key} belongs to an SC carrier");
            return read();
        }

        /// <summary>An on/off field: 1 or 0 (yes / no, true / false read the same).</summary>
        private static bool Flag(string key, string v) => v.Trim().ToLowerInvariant() switch
        {
            "1" or "yes" or "true" or "on" => true,
            "0" or "no" or "false" or "off" => false,
            _ => throw new FormatException($"carrier field {key}={v} is 1 or 0"),
        };

        private static int WholeField(string key, string v)
        {
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                throw new FormatException($"carrier field {key}={v} is not a whole number");
            return n;
        }

        private static readonly (string Unit, string Exponent)[] SymbolRateUnits =
            [("Msps", "e6"), ("ksps", "e3"), ("sps", ""), ("MBd", "e6"), ("kBd", "e3"), ("Bd", ""),
             ("GHz", "e9"), ("MHz", "e6"), ("kHz", "e3"), ("Hz", "")];

        /// <summary>A symbol rate: a number, optionally followed by sps, ksps or Msps (Bd, kBd, MBd and the frequency units
        /// read the same), applied as <see cref="ParseHz"/> applies its prefix.</summary>
        private static double ParseSymbolRate(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in SymbolRateUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                throw new FormatException($"carrier field {key}={v} is not a symbol rate: a number with sps, ksps or Msps");
            return hasExponent && exp.Length > 0 ? r * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : r;
        }

        /// <summary>Writes a symbol rate in Msps or ksps when that reads back as the same double, as plain sps otherwise.</summary>
        public static string FormatSymbolRate(double sps)
        {
            var inv = CultureInfo.InvariantCulture;
            foreach (var (unit, div) in new[] { ("Msps", 1e6), ("ksps", 1e3) })
            {
                if (sps < div) continue;
                string scaled = (sps / div).ToString("R", inv);
                if (!scaled.Contains('E') && ParseSymbolRate("", scaled + unit) == sps) return scaled + unit;
            }
            return sps.ToString("R", inv) + "sps";
        }

        private static int CssField(CarrierSpec c, string key, string v)
        {
            if (c.Standard != CarrierStandard.Css) throw new FormatException($"carrier field {key} belongs to a CSS carrier");
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sf))
                throw new FormatException($"carrier field {key}={v} is not a spreading factor: a whole number, 7 to 12");
            return sf;
        }

        private static readonly (string Unit, string Exponent)[] BitRateUnits = [("Mbps", "e6"), ("kbps", "e3"), ("bps", "")];

        /// <summary>A bit rate: a number, optionally followed by bps, kbps or Mbps, read as <see cref="ParseHz"/> reads a
        /// frequency.</summary>
        private static double ParseBitRate(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in BitRateUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double bps))
                throw new FormatException($"carrier field {key}={v} is not a bit rate: a number with bps, kbps or Mbps");
            return hasExponent && exp.Length > 0 ? bps * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : bps;
        }

        /// <summary>Writes <paramref name="bps"/> in kbps when that reads back as the same double, as bps below 1 kb/s (a
        /// DBPSK uplink's 100bps), and as plain b/s otherwise.</summary>
        private static string FormatBitRate(double bps)
        {
            var inv = CultureInfo.InvariantCulture;
            if (bps < 1e3) return bps.ToString("R", inv) + "bps";
            string scaled = (bps / 1e3).ToString("R", inv);
            return !scaled.Contains('E') && ParseBitRate("", scaled + "kbps") == bps ? scaled + "kbps" : bps.ToString("R", inv);
        }

        private static double PulseField(CarrierSpec c, string key, string v)
        {
            if (c.Standard != CarrierStandard.Pcw) throw new FormatException($"carrier field {key} belongs to a PCW carrier");
            return ParseSeconds(key, v);
        }

        private static double PulseFraction(CarrierSpec c, string key, string v)
        {
            if (c.Standard != CarrierStandard.Pcw) throw new FormatException($"carrier field {key} belongs to a PCW carrier");
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                throw new FormatException($"carrier field {key}={v} is not a fraction of the pulse top");
            return f;
        }

        private static readonly (string Unit, string Exponent)[] TimeUnits =
            [("ms", "e-3"), ("us", "e-6"), ("µs", "e-6"), ("ns", "e-9"), ("s", "")];

        /// <summary>A time field: a number, optionally followed by s, ms, us (µs) or ns, exact case as for
        /// <see cref="ParseHz"/>, and applied the same way, by re-reading the literal with its exponent.</summary>
        /// <summary><paramref name="v"/> as written, once <paramref name="check"/> has read it without refusing.</summary>
        private static string Checked<T>(string v, Func<T> check) { check(); return v; }

        private static T PulseOnly<T>(CarrierSpec c, string key, Func<T> read)
        {
            if (c.Standard != CarrierStandard.Pcw) throw new FormatException($"carrier field {key} belongs to a PCW carrier");
            return read();
        }

        internal static double ParseSeconds(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in TimeUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                throw new FormatException($"carrier field {key}={v} is not a time: a number with s, ms, us or ns");
            return hasExponent && exp.Length > 0 ? s * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : s;
        }

        /// <summary>A time as a carrier's time fields read it (a number with s, ms, us, µs or ns; bare = seconds), or false.</summary>
        public static bool TryParseSeconds(string text, out double seconds)
        {
            try { seconds = ParseSeconds("", text.Trim()); return true; }
            catch (FormatException) { seconds = 0; return false; }
        }

        /// <summary>Writes <paramref name="s"/> in <paramref name="unit"/> when that reads back as the same double, and as
        /// plain seconds otherwise.</summary>
        private static string FormatSeconds(double s, string unit)
        {
            if (s == 0) return "0";
            var inv = CultureInfo.InvariantCulture;
            // 100e-6 / 1e-6 is 100.00000000000001: the shortest spelling that parses back to the same double is the one written
            double scaled = s / double.Parse("1" + TimeUnits.First(u => u.Unit == unit).Exponent, inv);
            foreach (var f in new[] { "G15", "G16", "R" })
            {
                string text = scaled.ToString(f, inv);
                if (!text.Contains('E') && ParseSeconds("", text + unit) == s) return text + unit;
            }
            return s.ToString("R", inv);
        }

        private static readonly (string Unit, string Exponent)[] FrequencyUnits =
            [("GHz", "e9"), ("MHz", "e6"), ("kHz", "e3"), ("Hz", "")];

        /// <summary>A frequency field: a number, optionally followed by Hz, kHz, MHz or GHz (exact case: m is milli
        /// everywhere else in circuitRF). The prefix is applied by re-reading the literal with its exponent rather than
        /// by multiplying, which is what lets <c>20MHz</c> and <c>20e6</c> parse to the same double.</summary>
        internal static double ParseHz(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in FrequencyUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz))
                throw new FormatException($"carrier field {key}={v} is not a frequency: a number with Hz, kHz, MHz or GHz");
            return hasExponent && exp.Length > 0 ? hz * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : hz;
        }

        /// <summary>Writes <paramref name="hz"/> in <paramref name="unit"/> when that reads back as the same double, and
        /// as plain Hz otherwise — a carrier written by the Signal Designer never changes value on its way through text.</summary>
        private static string FormatHz(double hz, string unit)
        {
            if (hz == 0) return "0";
            var inv = CultureInfo.InvariantCulture;
            string exp = FrequencyUnits.First(u => u.Unit == unit).Exponent;
            string scaled = (hz / double.Parse("1" + exp, inv)).ToString("R", inv);
            return !scaled.Contains('E') && ParseHz("", scaled + unit) == hz ? scaled + unit : hz.ToString("R", inv);
        }

        /// <summary>A PCW carrier's pulse: <see cref="PulsedCw.Resolve"/>'s timing, then each radar option it states (code,
        /// chip smoothing, NLFM, Gaussian envelope, train); a plain pulse is PulsedCw.Resolve's shape exactly.</summary>
        public static PulseShape ResolvePulse(CarrierSpec c)
        {
            var s = PulsedCw.Resolve(c);
            if (c.PulseCode is { } code) { var (kind, n, root) = ParsePulseCode(code); s = s with { Code = RadarCodes.Phases(kind, n, root) }; }
            if (c.ChipSmoothing is double cs) s = s with { ChipSmoothing = cs };
            if (c.PulseFm is { } fm) s = s with { Fm = fm, FmParam = c.FmParam };
            // a Gaussian's span is Delay to Delay + 4 PW (and a pair's spacing): centred in the PRI unless Delay is stated
            if (c.GaussianPulse == true)
                s = s with { Gaussian = true, Delay = c.DelaySeconds ?? 0.5 * (s.Pri - 4 * s.Width - (c.PairS ?? 0)) };
            if (c.TrainPris is not null || c.Jitter is not null || c.Pulses is not null || c.DwellS is not null || c.StepHz is not null
                || c.Steps is not null || c.PairS is not null)
                s = s with
                {
                    Train = new PulseTrain
                    {
                        Pris = c.TrainPris is { } p ? ParseTimes(p) : null, Jitter = c.Jitter ?? 0, Seed = c.Seed, Pulses = c.Pulses,
                        Dwell = c.DwellS ?? 0, StepHz = c.StepHz ?? 0, Steps = c.Steps ?? 1, PairSpacing = c.PairS ?? 0,
                    },
                };
            return s;
        }

        /// <summary>A pulse's <c>Code=kind:length[:root]</c>: Barker:13, Frank:4 (16 chips), P1:4, P2:4, P3:64, P4:64,
        /// ZadoffChu:63:5, Mls:7 (127 chips); the length as <see cref="RadarCodes.Phases"/> takes it.</summary>
        public static (RadarCode Kind, int N, int Root) ParsePulseCode(string text)
        {
            var p = text.Split(':', StringSplitOptions.TrimEntries);
            var inv = CultureInfo.InvariantCulture;
            RadarCode? kind = p[0].Equals("ZC", StringComparison.OrdinalIgnoreCase) ? RadarCode.ZadoffChu : null;
            foreach (var k in Enum.GetValues<RadarCode>())
                if (k.ToString().Equals(p[0], StringComparison.OrdinalIgnoreCase)) kind = k;
            int n = 0, root = 1;
            if (kind is null || p.Length is < 2 or > 3 || !int.TryParse(p[1], NumberStyles.Integer, inv, out n)
                || p.Length == 3 && !int.TryParse(p[2], NumberStyles.Integer, inv, out root))
                throw new FormatException($"pCW carrier: Code is kind:length[:root], the kind one of {string.Join(", ", Enum.GetValues<RadarCode>())} (got '{text}')");
            try { RadarCodes.Phases(kind.Value, n, root); }
            catch (ArgumentException e) { throw new FormatException($"pCW carrier: Code {text}: {e.Message}"); }
            return (kind.Value, n, root);
        }

        private static PulseFm ParsePulseFm(string v)
        {
            foreach (var f in Enum.GetValues<PulseFm>())
                if (f.ToString().Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return f;
            throw new FormatException($"pCW carrier: Fm must be Linear, Tangent or Sine (got '{v}')");
        }

        /// <summary>Times, comma-separated, each as a carrier's time fields read them.</summary>
        private static double[] ParseTimes(string text) => [.. text.Split(',', StringSplitOptions.TrimEntries).Select(t => ParseSeconds("PRIs", t))];

        public const double DefaultFmcwRamp = 50e-6;

        /// <summary>An FMCW carrier's ramp: its sweep (BW), ramp, idle and form, each stated or the default (50 µs, no idle,
        /// sawtooth).</summary>
        public static Fmcw ResolveFmcw(CarrierSpec c) => new(c.BandwidthHz, c.RampS ?? DefaultFmcwRamp, c.IdleS ?? 0, c.Triangle ?? false);

        /// <summary>A TONES carrier's shape: each field it states, MultitoneShape's default for the rest.</summary>
        public static MultitoneShape ResolveTones(CarrierSpec c)
        {
            var d = new MultitoneShape();
            return new MultitoneShape
            {
                Tones = c.ToneCount ?? d.Tones, SpacingHz = c.ToneSpacingHz ?? d.SpacingHz, Phase = c.TonePhase ?? d.Phase,
                TonePowersDb = NumberList(c.TonePowers, "Powers", double.Parse), NoiseBandwidthHz = c.NoiseBandwidthHz ?? 0,
                NoiseDb = c.NoiseDb ?? 0, NotchCentreHz = c.NotchCentreHz ?? 0, NotchWidthHz = c.NotchWidthHz ?? 0,
                NotchTones = NumberList(c.NotchTones, "NotchTones", int.Parse), RecordSeconds = c.RecordS, Seed = (ulong)c.Seed,
            };
        }

        private static T[]? NumberList<T>(string? text, string key, Func<string, IFormatProvider, T> parse)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return [.. text.Split(',', StringSplitOptions.TrimEntries).Select(v => parse(v, CultureInfo.InvariantCulture))]; }
            catch (FormatException) { throw new FormatException($"tones: {key} must be numbers, comma-separated (got '{text}')"); }
        }

        private static TonePhase ParseTonePhase(string v)
        {
            foreach (var p in Enum.GetValues<TonePhase>())
                if (p.ToString().Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            throw new FormatException($"tones: Phase must be Zero, Newman, Schroeder or Random (got '{v}')");
        }

        public static QamOrder ParseMod(string v) => v.ToUpperInvariant() switch
        {
            "QPSK" => QamOrder.Qpsk,
            "16QAM" => QamOrder.Qam16,
            "64QAM" => QamOrder.Qam64,
            "256QAM" => QamOrder.Qam256,
            "1024QAM" => QamOrder.Qam1024,
            _ => throw new FormatException($"unknown modulation '{v}' (QPSK, 16QAM, 64QAM, 256QAM, 1024QAM)"),
        };

        public static string FormatCarrier(CarrierSpec c)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(c.Standard.ToString().ToUpperInvariant());
            if (c.Standard is CarrierStandard.Lte or CarrierStandard.Nr)
            {
                sb.Append(";BW=").Append(FormatHz(c.BandwidthHz, "MHz"));
                if (c.Standard == CarrierStandard.Nr) sb.Append(";SCS=").Append(FormatHz(c.ScsHz, "kHz"));
                sb.Append(";Mod=").Append(CarrierSpec.ModName(c.Modulation));
            }
            if (c.Standard == CarrierStandard.File)
            {
                sb.Append(";File=").Append(c.FilePath);
                if (c.SampleRateHz is double fs) sb.Append(";Fs=").Append(FormatHz(fs, "MHz"));
            }
            if (c.Standard == CarrierStandard.Pcw)
            {
                // only what is stated: an unstated Delay stays centred when PW or PRI is edited in the netlist
                foreach (var (key, value) in new[] { ("PRI", c.PriSeconds), ("PW", c.PulseWidthSeconds), ("Rise", c.RiseSeconds),
                                                     ("Fall", c.FallSeconds), ("Delay", c.DelaySeconds) })
                    if (value is double t) sb.Append(';').Append(key).Append('=').Append(FormatSeconds(t, "us"));
                if (c.MeasureStart is double ms) sb.Append(";MeasureStart=").Append(ms.ToString("R", inv));
                if (c.MeasureEnd is double me) sb.Append(";MeasureEnd=").Append(me.ToString("R", inv));
                if (c.ChirpHz is double ch) sb.Append(";Chirp=").Append(FormatHz(ch, "MHz"));
                if (c.PulseCode is { } code) sb.Append(";Code=").Append(code);
                if (c.ChipSmoothing is double cs) sb.Append(";ChipSmoothing=").Append(cs.ToString("R", inv));
                if (c.PulseFm is { } fm) sb.Append(";Fm=").Append(fm.ToString());
                if (c.FmParam is double fp) sb.Append(";FmParam=").Append(fp.ToString("R", inv));
                if (c.GaussianPulse is bool g) sb.Append(";Gaussian=").Append(g ? '1' : '0');
                if (c.TrainPris is { } pris) sb.Append(";PRIs=").Append(pris);
                if (c.Jitter is double j) sb.Append(";Jitter=").Append(j.ToString("R", inv));
                if (c.Pulses is int np) sb.Append(";Pulses=").Append(np.ToString(inv));
                if (c.DwellS is double dw) sb.Append(";Dwell=").Append(FormatSeconds(dw, "us"));
                if (c.StepHz is double st) sb.Append(";Step=").Append(FormatHz(st, "MHz"));
                if (c.Steps is int sts) sb.Append(";Steps=").Append(sts.ToString(inv));
                if (c.PairS is double pr) sb.Append(";Pair=").Append(FormatSeconds(pr, "us"));
            }
            if (c.Standard == CarrierStandard.Fmcw)
            {
                sb.Append(";BW=").Append(FormatHz(c.BandwidthHz, "MHz"));
                if (c.RampS is double rs) sb.Append(";Ramp=").Append(FormatSeconds(rs, "us"));
                if (c.IdleS is double idle) sb.Append(";Idle=").Append(FormatSeconds(idle, "us"));
                if (c.Triangle is bool tri) sb.Append(";Triangle=").Append(tri ? '1' : '0');
            }
            if (c.Standard == CarrierStandard.Css)
            {
                if (c.SpreadingFactor is int sf) sb.Append(";SF=").Append(sf.ToString(inv));
                sb.Append(";BW=").Append(FormatHz(c.BandwidthHz, "kHz"));
            }
            if (c.Standard == CarrierStandard.Dbpsk)
            {
                if (c.BitRateBps is double rb) sb.Append(";Rb=").Append(FormatBitRate(rb));
                if (c.Data is { } data) sb.Append(";Data=").Append(data);
            }
            if (c.Standard == CarrierStandard.Sc)
            {
                // only what is stated: an unstated field keeps following its default (Rings with Mod, among them)
                if (c.SymbolRateHz is double rs) sb.Append(";Rs=").Append(FormatSymbolRate(rs));
                if (c.ScModulation is { } m) sb.Append(";Mod=").Append(ScQam.ModName(m));
                if (c.RollOff is double a) sb.Append(";RollOff=").Append(a.ToString("R", inv));
                if (c.Span is int sp) sb.Append(";Span=").Append(sp.ToString(inv));
                if (c.Rings is { } rg) sb.Append(";Rings=").Append(rg);
                if (c.RingPhases is { } ph) sb.Append(";Phases=").Append(ph);
                if (c.Data is { } data) sb.Append(";Data=").Append(data);
                if (c.Symbols is int ns) sb.Append(";Symbols=").Append(ns.ToString(inv));
                if (c.Preamble is int pre) sb.Append(";Preamble=").Append(pre.ToString(inv));
                if (c.PilotBlock is { } pb) sb.Append(";PilotBlock=").Append(pb);
                if (c.ScPulse is { } pulse) sb.Append(";Pulse=").Append(ScQam.PulseName(pulse));
                if (c.GaussianBt is double sbt) sb.Append(";BT=").Append(sbt.ToString("R", inv));
                if (c.RotationDeg is double rot) sb.Append(";Rotation=").Append(rot.ToString("R", inv));
                if (c.Differential is bool dif) sb.Append(";Differential=").Append(dif ? '1' : '0');
                if (c.OffsetQ is bool oq) sb.Append(";OffsetQ=").Append(oq ? '1' : '0');
                if (c.FrameData is int fd) sb.Append(";FrameData=").Append(fd.ToString(inv));
                if (c.GuardSymbols is double gs) sb.Append(";Guard=").Append(gs.ToString("R", inv));
                if (c.RampSymbols is double rp) sb.Append(";Ramp=").Append(rp.ToString("R", inv));
            }
            if (c.Standard == CarrierStandard.Fsk)
            {
                if (c.BitRateBps is double rb) sb.Append(";Rb=").Append(FormatBitRate(rb));
                if (c.DeviationHz is double dev) sb.Append(";Dev=").Append(FormatHz(dev, "kHz"));
                if (c.GaussianBt is double bt) sb.Append(";BT=").Append(bt.ToString("R", inv));
                if (c.Data is { } data) sb.Append(";Data=").Append(data);
                if (c.Preset is { } pre) sb.Append(";Preset=").Append(pre);
                if (c.Levels is int lv) sb.Append(";Levels=").Append(lv.ToString(inv));
                if (c.Mapping is { } map) sb.Append(";Mapping=").Append(map);
                if (c.FskPulse is { } fpu) sb.Append(";Pulse=").Append(Fsk.PulseName(fpu));
                if (c.RollOff is double fro) sb.Append(";RollOff=").Append(fro.ToString("R", inv));
                if (c.PulseLength is int pl) sb.Append(";PulseLength=").Append(pl.ToString(inv));
                if (c.Manchester is bool mc) sb.Append(";Manchester=").Append(mc ? '1' : '0');
            }
            if (c.Standard == CarrierStandard.Wlan)
            {
                if (c.WlanFormat is { } std) sb.Append(";Std=").Append(Signals.Wlan.StdName(std));
                sb.Append(";BW=").Append(FormatHz(c.BandwidthHz, "MHz"));
                if (c.Mcs is int mcs) sb.Append(";MCS=").Append(mcs.ToString(inv));
                if (c.GuardIntervalS is double gi) sb.Append(";GI=").Append(FormatSeconds(gi, "us"));
                if (c.Symbols is int ns) sb.Append(";Symbols=").Append(ns.ToString(inv));
                if (c.IdleS is double idle) sb.Append(";Idle=").Append(FormatSeconds(idle, "us"));
                if (c.Oversample is int os) sb.Append(";Oversample=").Append(os.ToString(inv));
                if (c.WindowS is double win) sb.Append(";Window=").Append(FormatSeconds(win, "ns"));
            }
            if (c.Standard == CarrierStandard.Gnss)
            {
                if (c.GnssSignal is { } gs) sb.Append(";Signal=").Append(Signals.Gnss.SignalName(gs));
                if (c.Satellites is { } sats) sb.Append(";Sats=").Append(sats);
                if (c.Cn0DbHz is double cn0) sb.Append(";CN0=").Append(cn0.ToString("R", inv));
                if (c.RecordS is double rs) sb.Append(";Record=").Append(FormatSeconds(rs, "ms"));
            }
            if (c.Standard == CarrierStandard.Wcdma)
            {
                if (c.WcdmaModel is { } wm) sb.Append(";Model=").Append(wm.ToString());
                if (c.ScramblingCode is int code) sb.Append(";Code=").Append(code.ToString(inv));
            }
            if (c.Standard is CarrierStandard.Wlan11b or CarrierStandard.Oqpsk154)
            {
                if (c.BitRateBps is double rb) sb.Append(";Rb=").Append((rb / 1e6).ToString("R", inv)).Append("Mbps");
                if (c.PsduBytes is int psdu) sb.Append(";PSDU=").Append(psdu.ToString(inv));
            }
            if (c.Standard == CarrierStandard.Analog)
            {
                if (c.AnalogKind is { } ak) sb.Append(";Kind=").Append(ak.ToString().ToUpperInvariant());
                if (c.Tones is { } tones) sb.Append(";Tones=").Append(tones);
                if (c.NoiseBandwidthHz is double nbw) sb.Append(";NoiseBW=").Append(FormatHz(nbw, "kHz"));
                foreach (var (key, value) in new[] { ("Depth", c.Depth), ("Index", c.ModIndex), ("PeakPhase", c.PeakPhaseRad),
                                                     ("Bearing", c.BearingDeg), ("DDM", c.Ddm), ("SDM", c.Sdm) })
                    if (value is double d) sb.Append(';').Append(key).Append('=').Append(d.ToString("R", inv));
                if (c.DeviationHz is double adev) sb.Append(";Dev=").Append(FormatHz(adev, "kHz"));
                if (c.Ident is { } id) sb.Append(";Ident=").Append(id);
                if (c.RecordS is double ar) sb.Append(";Record=").Append(FormatSeconds(ar, "ms"));
            }
            if (c.Standard == CarrierStandard.Tones)
            {
                if (c.ToneCount is int n) sb.Append(";N=").Append(n.ToString(inv));
                if (c.ToneSpacingHz is double sp) sb.Append(";Spacing=").Append(FormatHz(sp, "kHz"));
                if (c.TonePhase is { } tph) sb.Append(";Phase=").Append(tph.ToString());
                if (c.TonePowers is { } pw) sb.Append(";Powers=").Append(pw);
                if (c.NotchWidthHz is double nw) sb.Append(";Notch=").Append(FormatHz(c.NotchCentreHz ?? 0, "MHz")).Append('/').Append(FormatHz(nw, "MHz"));
                if (c.NotchTones is { } nts) sb.Append(";NotchTones=").Append(nts);
                if (c.NoiseBandwidthHz is double nbw) sb.Append(";NoiseBW=").Append(FormatHz(nbw, "MHz"));
                if (c.NoiseDb is double ndb) sb.Append(";NoiseDb=").Append(ndb.ToString("R", inv));
                if (c.RecordS is double tr) sb.Append(";Record=").Append(FormatSeconds(tr, "ms"));
            }
            if (c.Standard == CarrierStandard.Cw && c.PhaseDeg is double phase) sb.Append(";Phase=").Append(phase.ToString("R", inv));
            sb.Append(";Off=").Append(FormatHz(c.OffsetHz, "MHz"));
            sb.Append(";P=").Append(c.PowerDb.ToString("R", inv));
            // a pulse, and the PN data of an FSK, DBPSK or CSS carrier, draw no random symbols
            // (a jittered train draws its PRIs from Seed)
            if (c.Standard == CarrierStandard.Pcw && c.Jitter is not null) sb.Append(";Seed=").Append(c.Seed.ToString(inv));
            if (c.Standard is not (CarrierStandard.Pcw or CarrierStandard.Fmcw or CarrierStandard.Fsk or CarrierStandard.Dbpsk or CarrierStandard.Css or CarrierStandard.Sc
                                   or CarrierStandard.Wcdma or CarrierStandard.Wlan11b or CarrierStandard.Oqpsk154))
                sb.Append(";Seed=").Append(c.Seed.ToString(inv));
            return sb.ToString();
        }
    }
}
