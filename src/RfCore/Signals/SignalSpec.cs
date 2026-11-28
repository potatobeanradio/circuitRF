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
    public enum CarrierStandard { Lte, Nr, Cw, File, Pcw, Fsk, Dbpsk, Css, Sc }

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

        // A pulsed-CW carrier's timing (s); null = the default PulsedCw.Resolve applies. Null rather than defaulted so
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

        // An FSK carrier's settings; null = the default Fsk.Resolve applies, for the same reason as the pulse's.
        /// <summary>Bit (chip) rate, b/s; a DBPSK carrier's too.</summary>
        public double? BitRateBps { get; init; }
        /// <summary>Peak frequency deviation (Hz): a 1 sits at +Dev, a 0 at -Dev.</summary>
        public double? DeviationHz { get; init; }
        /// <summary>The Gaussian filter's bandwidth-time product; null or 0 = no filter (rectangular CPFSK).</summary>
        public double? GaussianBt { get; init; }
        /// <summary>PN9, PN15, or a fixed pattern of 0s and 1s; the record is one period of it. A DBPSK carrier's too.</summary>
        public string? Data { get; init; }

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

        public string Label => Standard switch
        {
            CarrierStandard.Lte => $"LTE {BandwidthHz / 1e6:0.#} MHz {ModName(Modulation)}",
            CarrierStandard.Nr => $"NR {BandwidthHz / 1e6:0.#} MHz {ScsHz / 1e3:0} kHz {ModName(Modulation)}",
            CarrierStandard.Cw => "CW",
            CarrierStandard.Pcw => $"pCW {PulsedCw.FormatTime(PulsedCw.Resolve(this).Width)} / {PulsedCw.FormatTime(PulsedCw.Resolve(this).Pri)}",
            CarrierStandard.Fsk => FskLabel(Fsk.Resolve(this)),
            CarrierStandard.Dbpsk => $"DBPSK {Fsk.FormatBitRate(Dbpsk.Resolve(this).BitRate)} {DataLabel(Dbpsk.Resolve(this).Data)}",
            CarrierStandard.Css => $"CSS SF{Css.Resolve(this).Sf} {Fsk.FormatHz(BandwidthHz)}",
            CarrierStandard.Sc => ScLabel(ScQam.Resolve(this)),
            _ => $"File {System.IO.Path.GetFileName(FilePath ?? "")}",
        };

        private static string FskLabel(FskShape s)
            => $"{s.Kind} {Fsk.FormatBitRate(s.BitRate)} ±{Fsk.FormatHz(s.Deviation)}{(s.Bt > 0 ? $" BT {s.Bt.ToString("0.###", CultureInfo.InvariantCulture)}" : "")} "
             + DataLabel(s.Data);

        private static string DataLabel(string data) => data is "PN9" or "PN15" or "PN23" ? data : $"{data.Length}-bit pattern";

        private static string ScLabel(ScShape s)
            => $"SC {SignalSpec.FormatSymbolRate(s.SymbolRate)} {ScQam.ModName(s.Mod)} α {s.RollOff.ToString("0.###", CultureInfo.InvariantCulture)}";

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
        /// <c>"PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"</c>, or for FSK
        /// <c>"FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"</c>, or for DBPSK <c>"DBPSK;Rb=100bps;Data=PN9;Off=0;P=0"</c>,
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
                    _ => throw new FormatException($"unknown carrier standard '{parts[0]}' (LTE, NR, CW, PCW, FSK, DBPSK, CSS, SC or FILE)"),
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
                    "RB" => c with { BitRateBps = FskField(c, k, () => ParseBitRate(k, v), alsoDbpsk: true) },
                    "DEV" => c with { DeviationHz = FskField(c, k, () => ParseHz(k, v)) },
                    "BT" => c with { GaussianBt = FskField(c, k, Num) },
                    "DATA" => c with { Data = FskField(c, k, () => v, alsoDbpsk: true) },
                    "RS" => c with { SymbolRateHz = ScField(c, k, () => ParseSymbolRate(k, v)) },
                    "ROLLOFF" => c with { RollOff = ScField(c, k, Num) },
                    "SPAN" => c with { Span = ScField(c, k, () => WholeField(k, v)) },
                    "RINGS" => c with { Rings = ScField(c, k, () => v) },
                    "PHASES" => c with { RingPhases = ScField(c, k, () => v) },
                    "PREAMBLE" => c with { Preamble = ScField(c, k, () => WholeField(k, v)) },
                    "PILOTBLOCK" => c with { PilotBlock = ScField(c, k, () => v) },
                    "SYMBOLS" => c with { Symbols = ScField(c, k, () => WholeField(k, v)) },
                    "SF" => c with { SpreadingFactor = CssField(c, k, v) },
                    _ => throw new FormatException($"unknown carrier field '{k}'"),
                };
            }
            // A pulse's bandwidth is its edges', never the 20 MHz a CarrierSpec defaults to.
            if (c.Standard == CarrierStandard.Pcw) c = c with { BandwidthHz = PulsedCw.BandwidthHz(PulsedCw.Validate(c)) };
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
            return c;
        }

        private static T FskField<T>(CarrierSpec c, string key, Func<T> read, bool alsoDbpsk = false)
        {
            if (c.Standard != CarrierStandard.Fsk && !(alsoDbpsk && c.Standard is CarrierStandard.Dbpsk or CarrierStandard.Sc))
                throw new FormatException($"carrier field {key} belongs to an FSK{(alsoDbpsk ? ", DBPSK or SC" : "")} carrier");
            return read();
        }

        private static T ScField<T>(CarrierSpec c, string key, Func<T> read)
        {
            if (c.Standard != CarrierStandard.Sc) throw new FormatException($"carrier field {key} belongs to an SC carrier");
            return read();
        }

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
        private static double ParseSeconds(string key, string v)
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
        private static double ParseHz(string key, string v)
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
            }
            if (c.Standard == CarrierStandard.Fsk)
            {
                if (c.BitRateBps is double rb) sb.Append(";Rb=").Append(FormatBitRate(rb));
                if (c.DeviationHz is double dev) sb.Append(";Dev=").Append(FormatHz(dev, "kHz"));
                if (c.GaussianBt is double bt) sb.Append(";BT=").Append(bt.ToString("R", inv));
                if (c.Data is { } data) sb.Append(";Data=").Append(data);
            }
            sb.Append(";Off=").Append(FormatHz(c.OffsetHz, "MHz"));
            sb.Append(";P=").Append(c.PowerDb.ToString("R", inv));
            // a pulse, and the PN data of an FSK, DBPSK or CSS carrier, draw no random symbols
            if (c.Standard is not (CarrierStandard.Pcw or CarrierStandard.Fsk or CarrierStandard.Dbpsk or CarrierStandard.Css or CarrierStandard.Sc))
                sb.Append(";Seed=").Append(c.Seed.ToString(inv));
            return sb.ToString();
        }
    }
}
