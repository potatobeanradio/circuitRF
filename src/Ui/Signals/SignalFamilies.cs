// ================================================================
//  SignalFamilies.cs  —  the Signal Designer's cards, presets and
//  quality readouts for the signal families added after the first
//  cards (Wi-Fi, GNSS, WCDMA, 802.11b, 802.15.4, analog, tones and
//  noise, FMCW, OFDM numerologies) and for the new options of the
//  LTE / NR, SC, FSK and pulsed-CW cards.
//
//  A family's card is a list of fields, each one carrier key typed as
//  the netlist writes it (80MHz, 0.8us, 1@1.8kHz@12@0dB): the carrier
//  is the card's own fields plus these, read back through the
//  netlist's own parser, so the card, the netlist and the cache key can
//  never disagree. An empty field is the key left out: its default.
//  Rarely used fields sit behind the card's Advanced expander.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore.Signals;

namespace CircuitRF.Ui.Signals;

/// <summary>One carrier key on a card: its label, the default an empty field means, a tip, and the choices offered.</summary>
public sealed partial class SignalField(string key, string label, string @default, string tip,
                                        IReadOnlyList<string>? options = null, bool advanced = false, string initial = "") : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    /// <summary>What an empty field means, shown as its placeholder.</summary>
    public string Default { get; } = @default;
    public string Tip { get; } = tip;
    public IReadOnlyList<string>? Options { get; } = options;
    public bool HasOptions => Options is not null;
    public bool IsText => Options is null;
    public bool Advanced { get; } = advanced;
    /// <summary>The value as the netlist writes it; empty = the key left out.</summary>
    [ObservableProperty] private string _text = initial;
}

/// <summary>A preset in a family's menu: its line, and what it sets.</summary>
public sealed record PresetItem(string Header, ICommand Command);

/// <summary>One preset button: its name, its tip and its menu.</summary>
public sealed record PresetMenu(string Name, string Tip, IReadOnlyList<PresetItem> Items);

/// <summary>The fields each standard's card takes beyond the ones it types itself.</summary>
public static class SignalFields
{
    private static readonly string[] Flag = ["0", "1"];

    /// <summary>A card made of fields only: a family with no typed fields of its own.</summary>
    public static bool IsFamily(string standard) => standard is "WLAN" or "GNSS" or "WCDMA" or "WLAN11B" or "OQPSK154"
        or "ANALOG" or "TONES" or "FMCW" or "OFDM";

    private static SignalField F(string key, string label, string def, string tip, IReadOnlyList<string>? options = null, string initial = "")
        => new(key, label, def, tip, options, advanced: false, initial);

    private static SignalField A(string key, string label, string def, string tip, IReadOnlyList<string>? options = null)
        => new(key, label, def, tip, options, advanced: true);

    private static SignalField Seed() => A("Seed", "Seed", "1", "The seed of the random data (and noise) the carrier draws");

    /// <summary>A fresh set of <paramref name="standard"/>'s fields, at their Designer defaults.</summary>
    public static List<SignalField> For(string standard) => standard switch
    {
        "LTE" or "NR" =>
        [
            A("Link", "Link", "DL", "DL: the full-load downlink. UL: LTE's SC-FDMA, NR's PUSCH (CP-OFDM, or DFT-s-OFDM below).", ["DL", "UL"]),
            .. standard == "NR"
                ? new[]
                {
                    A("DFTs", "DFT-s-OFDM", "0", "NR uplink transform precoding (TS 38.211 cl. 6.3.1.4)", Flag),
                    A("Pi2BPSK", "π/2-BPSK", "0", "π/2-BPSK in place of the modulation (NR DFT-s-OFDM only)", Flag),
                }
                : [],
            A("RbStart", "RB start", "0", "First resource block of a contiguous allocation"),
            A("RbCount", "RB count", "all", "Resource blocks allocated; a DFT-s-OFDM count is 2^a·3^b·5^c"),
            A("TM", "Test model", "none", "A 3GPP test model's allocation and levels: NR-FR1-TM (TS 38.141-1), E-TM (TS 36.141)",
              standard == "NR" ? Ofdm3gpp.NrTestModels : Ofdm3gpp.LteTestModels),
            A("TDD", "TDD", "FDD", "A TDD pattern, slots and the special slot's symbols (DDDSU:10,2), or TM for the test model's own"),
            A("CellId", "Cell ID", "1", "Physical cell identity: the scrambling, the reference and synchronisation signals"),
        ],
        "SC" =>
        [
            A("Pulse", "Pulse", "RRC", "The pulse each symbol is shaped by; any but RRC rings in a window that is not whole records", ScQam.PulseNames),
            A("BT", "BT", "0.3", "Gaussian pulse: the filter's 3 dB bandwidth times the symbol period"),
            A("Rotation", "Rotation (°)", "0", "Degrees each symbol turns by from the one before: 90 for π/2-BPSK, 67.5 for EDGE 8PSK"),
            A("Differential", "Differential", "0", "Each point is the previous one times the label's point (π/4-DQPSK, 8DPSK)", Flag),
            A("OffsetQ", "Offset Q", "0", "Q half a symbol after I (O-QPSK)", Flag),
            A("FrameData", "Frame data", "0", "Data symbols per frame, each with its own preamble and pilots; 0 = one frame"),
            A("Guard", "Guard (symbols)", "0", "Symbol periods of silence after each frame (a burst)"),
            A("Ramp", "Ramp (symbols)", "0", "Symbol periods of each raised-cosine ramp into and out of a frame, inside the guard"),
        ],
        "FSK" =>
        [
            A("Preset", "Preset", "none", "A standard's burst or packet (Bluetooth BR / EDR, DECT, Z-Wave, …); the fields above override it",
              [.. FskPresets.All.Select(FskPresets.Key)]),
            A("Levels", "Levels", "2", "2, 4 or 8 frequencies; the deviation is the outer level's", ["2", "4", "8"]),
            A("Mapping", "Mapping", "default", "The odd level of each symbol value, comma-separated (4FSK default 1,3,-1,-3)"),
            A("Pulse", "Pulse", "NRZ", "The frequency pulse: NRZ, raised cosine, root raised cosine, C4FM, LREC or LRC",
              ["NRZ", "RC", "RRC", "C4FM", "LREC", "LRC"]),
            A("RollOff", "Roll-off", "0.2", "Roll-off of the RC, RRC and C4FM pulses"),
            A("PulseLength", "Pulse length", "1", "L, symbols, of an LREC or LRC pulse"),
            A("Manchester", "Manchester", "0", "Each bit sent as two chips (1 → 10, 0 → 01); the bit rate is then the chip rate", Flag),
        ],
        "PCW" =>
        [
            A("Code", "Phase code", "none", "An intra-pulse code, kind:length[:root]; the chip is PW over the chips",
              ["Barker:13", "Barker:7", "Frank:4", "P1:4", "P2:4", "P3:64", "P4:64", "ZadoffChu:63", "Mls:7"]),
            A("ChipSmoothing", "Chip smoothing", "0", "Fraction of a chip (0 to 1) over which the phase moves to the next chip's"),
            A("Fm", "Sweep law", "Linear", "The law Chirp is swept with: linear, or tangent or sine NLFM", ["Linear", "Tangent", "Sine"]),
            A("FmParam", "Law shape", "1.2 / 0.5", "Tangent β (rad, 0 to π/2; 1.2) or sine k (0 to 1; 0.5)"),
            A("Gaussian", "Gaussian", "0", "A Gaussian envelope of half-amplitude width PW in place of the flat top and edges", Flag),
            A("PRIs", "PRIs", "PRI", "A staggered train: the PRIs cycled pulse by pulse, comma-separated (100us,125us,150us)"),
            A("Jitter", "Jitter", "0", "± fraction of each PRI, drawn from Seed"),
            A("Pulses", "Pulses", "auto", "Pulses (slots) before the dwell"),
            A("Dwell", "Dwell", "0", "Off-time after the burst"),
            A("Step", "Step", "0", "Δf between stepped pulses"),
            A("Steps", "Steps", "1", "How many stepped frequencies"),
            A("Pair", "Pair spacing", "0", "A pulse pair: the second pulse this long after the first, peak to peak (DME)"),
            Seed(),
        ],
        "WLAN" =>
        [
            F("Std", "Standard", "ac", "802.11a, n (HT), ac (VHT), ax (HE) or be (EHT)", ["a", "n", "ac", "ax", "be"]),
            F("BW", "Bandwidth", "20MHz", "The channel: 20 MHz (a), up to 40 (n), 160 (ac, ax) or 320 MHz (be)",
              ["20MHz", "40MHz", "80MHz", "160MHz", "320MHz"]),
            F("MCS", "MCS", "7", "0 to 7 (a, n), 9 (ac), 11 (ax), 13 (be); sets the EVM limit", [.. Enumerable.Range(0, 14).Select(i => i.ToString(CultureInfo.InvariantCulture))]),
            F("GI", "Guard interval", "0.8us", "0.8 µs (a); 0.4 or 0.8 µs (n, ac); 0.8, 1.6 or 3.2 µs (ax, be)", ["0.4us", "0.8us", "1.6us", "3.2us"]),
            A("Symbols", "Data symbols", "20", "OFDM data symbols in the packet"),
            A("Idle", "Idle", "16us", "The gap after the packet: the record is the packet and its gap"),
            A("Oversample", "Oversample", "1", "The rate over the bandwidth. 4, the Designer's, holds the mask to 1.5 BW; at 1 an envelope run reads ~0.36 % EVM.",
              ["1", "2", "4", "8"]),
            A("Window", "Window", "100ns", "The raised-cosine overlap between symbols"),
            Seed(),
        ],
        "GNSS" =>
        [
            F("Signal", "Signal", "L1CA", "GPS L1 C/A, GPS L5, Galileo E1 (OS) or BeiDou B1I", ["L1CA", "L5", "E1", "B1I"]),
            F("Sats", "Satellites", "1", "PRN@Doppler@code phase@power, comma-separated: 1@1.8kHz@12@0dB,5@-2kHz@300@-3dB"),
            F("CN0", "C/N0 (dB-Hz)", "no noise", "The strongest satellite's carrier to noise density; empty = no noise"),
            A("Record", "Record", "20ms", "Whole milliseconds of the codes"),
            Seed(),
        ],
        "WCDMA" =>
        [
            F("Model", "Test model", "Tm1x64", "TS 25.141 test models 1-6 (downlink), or the 12.2 kb/s uplink reference channel",
              [.. Enum.GetValues<WcdmaModel>().Select(m => m.ToString())]),
            F("Code", "Scrambling code", "0", "The primary scrambling code, 0 to 7 (group 0)"),
        ],
        "WLAN11B" =>
        [
            F("Rb", "Rate", "11Mbps", "1 (DBPSK), 2 (DQPSK), 5.5 or 11 Mb/s (CCK)", ["1Mbps", "2Mbps", "5.5Mbps", "11Mbps"]),
            F("PSDU", "PSDU (bytes)", "1000", "The frame's payload, 1 to 4095 bytes"),
        ],
        "OQPSK154" =>
        [
            F("PSDU", "PSDU (bytes)", "127", "The frame's payload, 1 to 127 bytes"),
        ],
        "ANALOG" =>
        [
            F("Kind", "Kind", "AM", "AM, DSB-SC, USB, LSB, FM or PM; VOR, ILS localizer (LOC) or glide path (GS)",
              ["AM", "DSBSC", "USB", "LSB", "FM", "PM", "VOR", "LOC", "GS"]),
            F("Tones", "Tones", "1kHz", "The message: frequency@level@phase, comma-separated (700Hz,1900Hz for SSB two-tone)"),
            F("Depth", "AM depth", "0.3", "The AM modulation depth m, 0 to 1"),
            F("Dev", "FM deviation", "5kHz", "The FM peak deviation"),
            F("Bearing", "Bearing (°)", "0", "VOR: the radial the 30 Hz phases encode"),
            F("DDM", "DDM", "0", "ILS: the difference in depth of modulation, 90 Hz over 150 Hz"),
            A("NoiseBW", "Noise BW", "0 (tones)", "Gaussian noise to this bandwidth as the message, in place of the tones"),
            A("Index", "FM index", "from Dev", "Sets the deviation as index × the top message frequency"),
            A("PeakPhase", "PM peak (rad)", "1", "The PM peak phase"),
            A("SDM", "SDM", "0.4 LOC, 0.8 GS", "ILS: the sum of the depths of modulation"),
            A("Ident", "Ident", "none", "VOR, LOC: the station's Morse ident at 1020 Hz"),
            A("Record", "Record", "100ms", "Whole 100 ms of the message, lengthened to hold the ident"),
            Seed(),
        ],
        "TONES" =>
        [
            F("N", "Tones", "2", "Equal tones centred on Fc (up to 100,000); 0 = noise only"),
            F("Spacing", "Spacing", "1MHz", "Tone-to-tone spacing"),
            F("Phase", "Phases", "Newman", "Zero, Newman, Schroeder or Random (from Seed)", ["Zero", "Newman", "Schroeder", "Random"]),
            F("Notch", "Notch", "none", "centre/width: an empty band for the NPR, 0/1MHz"),
            F("NoiseBW", "Noise BW", "none", "Band-limited Gaussian noise over this band, centred on Fc"),
            F("NoiseDb", "Noise (dB)", "0", "With tones: the noise's total over the tones' total (−20 for C/N 20 dB)"),
            A("Powers", "Tone powers (dB)", "equal", "Each tone's power, comma-separated"),
            A("NotchTones", "Notch tones", "none", "The notch as tone indices (0-based) to leave out, comma-separated"),
            A("Record", "Record", "2 / spacing", "The record; noise alone defaults to 1 ms"),
            Seed(),
        ],
        "FMCW" =>
        [
            F("BW", "Sweep", "20MHz", "The ramp's sweep, centred on Fc"),
            F("Ramp", "Ramp", "50us", "The up-ramp's duration (each half of a triangle)"),
            F("Idle", "Idle", "0", "A sawtooth's return to the start (0 = a jump)"),
            F("Triangle", "Triangle", "0", "Up and down ramps of equal length", Flag),
        ],
        "OFDM" =>
        [
            F("Preset", "Numerology", "", "DVB-T 2K / 8K, DVB-T2 8K, ISDB-T mode 3, DAB mode I, WiMAX 10 MHz, or the NB-IoT uplink",
              SignalSpec.OfdmPresetNames, initial: SignalSpec.OfdmPresetNames[0]),
            F("Symbols", "Symbols", "68", "OFDM symbols in the record"),
            A("SCS", "NB-IoT SCS", "15kHz", "NB-IoT uplink: 15 or 3.75 kHz subcarriers", ["15kHz", "3.75kHz"]),
            A("NbTones", "NB-IoT tones", "12", "NB-IoT uplink: 1, 3, 6 or 12 tones", ["1", "3", "6", "12"]),
            A("FirstTone", "First tone", "0", "NB-IoT uplink: the allocation's first tone"),
            A("Pi2BPSK", "π/2-BPSK", "0", "NB-IoT single tone: π/2-BPSK in place of QPSK", Flag),
            A("Slots", "Slots", "2", "NB-IoT uplink: slots in the record"),
            A("CellId", "Cell ID", "0", "NB-IoT uplink: the cell identity of its DMRS"),
            Seed(),
        ],
        _ => [],
    };
}

public sealed partial class CarrierRowViewModel
{
    public CarrierRowViewModel() => RebuildFields();

    /// <summary>The card's carrier keys beyond its typed fields (all of them for a family card).</summary>
    public ObservableCollection<SignalField> Fields { get; } = new();
    public IReadOnlyList<SignalField> MainFields { get; private set; } = [];
    public IReadOnlyList<SignalField> AdvancedFields { get; private set; } = [];
    public bool HasMainFields => MainFields.Count > 0;
    public bool HasAdvancedFields => AdvancedFields.Count > 0;
    /// <summary>A card made of fields only (Wi-Fi, GNSS, WCDMA, …).</summary>
    public bool IsFamily => SignalFields.IsFamily(Standard);

    /// <summary>Raised when a field's value moves: the carrier it makes moved, so the preview follows.</summary>
    public int FieldValues { get; private set; }

    /// <summary>The last carrier the fields made, kept while a field is being typed into something that does not parse.</summary>
    private CarrierSpec? _fieldsSpec;

    private void RebuildFields()
    {
        foreach (var f in Fields) f.PropertyChanged -= OnFieldChanged;
        Fields.Clear();
        foreach (var f in SignalFields.For(Standard)) { f.PropertyChanged += OnFieldChanged; Fields.Add(f); }
        MainFields = [.. Fields.Where(f => !f.Advanced)];
        AdvancedFields = [.. Fields.Where(f => f.Advanced)];
        _fieldsSpec = null;
        foreach (var n in new[] { nameof(MainFields), nameof(AdvancedFields), nameof(HasMainFields), nameof(HasAdvancedFields), nameof(IsFamily) })
            OnPropertyChanged(n);
        FieldsChanged();
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SignalField.Text)) return;
        FieldValues++;
        OnPropertyChanged(nameof(FieldValues));
        FieldsChanged();
        PulseChanged(); FskChanged(); ScChanged();
    }

    private void FieldsChanged()
    {
        OnPropertyChanged(nameof(FieldsError));
        OnPropertyChanged(nameof(FieldsInfoText));
    }

    /// <summary><paramref name="typed"/> with the fields' keys in place of its own, read by the netlist's parser.</summary>
    private CarrierSpec WithFields(CarrierSpec typed)
    {
        var keys = Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = SignalSpec.FormatCarrier(typed).Split(';').Where((p, i) => i == 0 || !keys.Contains(p.Split('=')[0]))
            .Concat(Fields.Where(f => f.Text.Trim().Length > 0).Select(f => f.Key + "=" + f.Text.Trim()));
        return SignalSpec.ParseCarrier(string.Join(";", parts));
    }

    private CarrierSpec ApplyFields(CarrierSpec typed)
    {
        if (Fields.Count == 0) return typed;
        try { return _fieldsSpec = WithFields(typed); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return _fieldsSpec ?? typed; }
    }

    /// <summary>The card's carrier with no fallback: a field that does not parse throws, so a refusal names it.</summary>
    private CarrierSpec StrictSpec() => Fields.Count == 0 ? TypedSpec() : WithFields(TypedSpec());

    /// <summary>Why the fields do not make a carrier, as the netlist would say it, or null.</summary>
    public string? FieldsError
    {
        get
        {
            if (Fields.Count == 0) return null;
            try { WithFields(TypedSpec()); return null; }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return ex.Message; }
        }
    }

    /// <summary>A family card's carrier as its label reads it (the netlist's own summary).</summary>
    public string FieldsInfoText
    {
        get
        {
            if (!IsFamily || FieldsError is not null) return "";
            try { return ToSpec().Label; }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { return ex.Message; }
        }
    }

    /// <summary>Each field from <paramref name="c"/> as the netlist writes it; a key it does not state leaves the field empty.</summary>
    private void LoadFields(CarrierSpec c)
    {
        if (Fields.Count == 0) return;
        var stated = SignalSpec.FormatCarrier(c).Split(';').Skip(1).Select(p => p.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fields) f.Text = stated.GetValueOrDefault(f.Key, "");
        _fieldsSpec = null;
    }
}

public sealed partial class SignalDesignerViewModel
{
    // ── presets: one menu per standard, one row per family ────────────────

    private PresetItem Item(string header, CarrierSpec c, double? fcHz = null)
        => new(header, new RelayCommand(() => { if (fcHz is double fc) PresetAt(fc, c); else Preset(c); }));

    private static PresetMenu Menu(string name, string tip, IEnumerable<PresetItem> items) => new(name, tip, [.. items]);

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A standard's single carrier as the SC card's fields hold it.</summary>
    public static CarrierSpec ScSpec(ScShape s) => new()
    {
        Standard = CarrierStandard.Sc, SymbolRateHz = s.SymbolRate, ScModulation = s.Mod, RollOff = s.RollOff, Span = s.Span,
        Rings = s.Rings.Length > 0 ? string.Join(",", s.Rings.Select(R)) : null, Data = s.Data,
        Preamble = s.Preamble > 0 ? s.Preamble : null, PilotBlock = s.PilotLength > 0 ? $"{s.PilotLength}/{s.PilotEvery}" : null,
        ScPulse = s.Pulse != ScPulse.Rrc ? s.Pulse : null, GaussianBt = s.Bt != ScQam.DefaultBt ? s.Bt : null,
        RotationDeg = s.RotationDeg != 0 ? s.RotationDeg : null, Differential = s.Differential ? true : null, OffsetQ = s.Offset ? true : null,
        FrameData = s.FrameData > 0 ? s.FrameData : null, GuardSymbols = s.Guard > 0 ? s.Guard : null, RampSymbols = s.Ramp > 0 ? s.Ramp : null,
    };

    /// <summary>An analog preset's carrier: each field that differs from the kind's default.</summary>
    public static CarrierSpec AnalogSpec(AnalogShape s)
    {
        var d = new AnalogShape(s.Kind);
        return new CarrierSpec
        {
            Standard = CarrierStandard.Analog, AnalogKind = s.Kind,
            Tones = s.Tones is { } t ? string.Join(",", t.Select(x => $"{R(x.FrequencyHz)}Hz@{R(x.LevelDb)}@{R(x.PhaseDeg)}")) : null,
            NoiseBandwidthHz = s.NoiseBandwidthHz > 0 ? s.NoiseBandwidthHz : null, Depth = s.Depth != d.Depth ? s.Depth : null,
            DeviationHz = s.DeviationHz != d.DeviationHz ? s.DeviationHz : null, ModIndex = s.Index,
            PeakPhaseRad = s.PeakPhaseRad != d.PeakPhaseRad ? s.PeakPhaseRad : null, BearingDeg = s.BearingDeg != 0 ? s.BearingDeg : null,
            Ddm = s.Ddm != 0 ? s.Ddm : null, Sdm = s.Sdm, Ident = s.Ident.Length > 0 ? s.Ident : null,
            RecordS = s.RecordSeconds != d.RecordSeconds ? s.RecordSeconds : null,
        };
    }

    /// <summary>A tones-and-noise preset's carrier.</summary>
    public static CarrierSpec TonesSpec(MultitoneShape s) => new()
    {
        Standard = CarrierStandard.Tones, ToneCount = s.Tones, ToneSpacingHz = s.Tones > 1 ? s.SpacingHz : null, TonePhase = s.Tones > 1 ? s.Phase : null,
        TonePowers = s.TonePowersDb is { } p ? string.Join(",", p.Select(R)) : null,
        NoiseBandwidthHz = s.NoiseBandwidthHz > 0 ? s.NoiseBandwidthHz : null, NoiseDb = s.NoiseDb != 0 ? s.NoiseDb : null,
        NotchCentreHz = s.NotchWidthHz > 0 ? s.NotchCentreHz : null, NotchWidthHz = s.NotchWidthHz > 0 ? s.NotchWidthHz : null,
        NotchTones = s.NotchTones is { } nt ? string.Join(",", nt) : null, RecordS = s.RecordSeconds,
    };

    /// <summary>A code's <c>kind:n</c> from its chip phases (a preset holds the phases).</summary>
    public static string? CodeKey(double[] code)
    {
        int len = code.Length;
        foreach (var kind in Enum.GetValues<RadarCode>())
            foreach (int n in new[] { len, (int)Math.Round(Math.Sqrt(len)), (int)Math.Round(Math.Log2(len + 1)) }.Distinct())
            {
                try { if (RadarCodes.Phases(kind, n).SequenceEqual(code)) return $"{kind}:{n}"; }
                catch (Exception e) when (e is ArgumentException or FormatException) { }
            }
        return null;
    }

    /// <summary><paramref name="s"/> as the literal its µs reads (50 × 1e-6 is not 50e-6), so the card shows 50us.</summary>
    private static double Micro(double s) => double.Parse((s * 1e6).ToString("G12", CultureInfo.InvariantCulture) + "e-6", CultureInfo.InvariantCulture);

    /// <summary>A radar preset's pulse as the PCW card's fields hold it, or its FMCW ramp.</summary>
    public static CarrierSpec RadarSpec(RadarPreset p)
    {
        if (p.Ramp is { } f)
            return new CarrierSpec
            {
                Standard = CarrierStandard.Fmcw, BandwidthHz = f.BandwidthHz, RampS = Micro(f.Ramp), IdleS = f.Idle > 0 ? Micro(f.Idle) : null,
                Triangle = f.Triangle ? true : null,
            };
        var s = p.Pulse!;
        var t = s.Train;
        return new CarrierSpec
        {
            Standard = CarrierStandard.Pcw, PriSeconds = s.Pri, PulseWidthSeconds = s.Width, RiseSeconds = s.Rise, FallSeconds = s.Fall,
            ChirpHz = s.ChirpHz != 0 ? s.ChirpHz : null, PulseCode = s.Code is { } code ? CodeKey(code) : null,
            ChipSmoothing = s.ChipSmoothing != 0 ? s.ChipSmoothing : null, PulseFm = s.Fm != PulseFm.Linear ? s.Fm : null, FmParam = s.FmParam,
            GaussianPulse = s.Gaussian ? true : null,
            TrainPris = t?.Pris is { } pris ? string.Join(",", pris.Select(x => R(x * 1e6) + "us")) : null,
            Jitter = t is { Jitter: > 0 } ? t.Jitter : null, Pulses = t?.Pulses, DwellS = t is { Dwell: > 0 } ? t.Dwell : null,
            StepHz = t is { StepHz: not 0 } ? t.StepHz : null, Steps = t is { Steps: > 1 } ? t.Steps : null,
            PairS = t is { PairSpacing: > 0 } ? t.PairSpacing : null, Seed = t?.Seed ?? 1,
        };
    }

    private PresetItem FskItem(FskPreset p) => Item(p.Name, new CarrierSpec { Standard = CarrierStandard.Fsk, Preset = FskPresets.Key(p) }, p.CentreHz);
    private IEnumerable<PresetItem> FskFamily(string family) => FskPresets.All.Where(p => p.Family == family).Select(FskItem);
    private PresetItem ScItem(string name, string? header = null)
        => Item(header ?? name, ScSpec(ScQamPresets.All.Single(p => p.Name == name).Shape));
    private PresetItem AnalogItem(string name)
    {
        var a = Analog.Presets.Single(p => p.Name == name);
        return Item(a.Name, AnalogSpec(a.Shape), a.CenterHz);
    }
    private IEnumerable<PresetItem> RadarFamily(Func<RadarPreset, bool> pick)
        => RadarPresets.All.Where(pick).Select(p => Item(p.Name, RadarSpec(p)));

    private static CarrierSpec Nr20 => new() { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam64 };
    private static CarrierSpec Lte20 => new() { Standard = CarrierStandard.Lte, BandwidthHz = 20e6, Modulation = QamOrder.Qam64 };

    private IReadOnlyList<PresetMenu>? _cellular, _connectivity, _navigation, _broadcast, _radio, _radar, _tones;

    /// <summary>Cellular: the uplinks, the 3GPP test models, NB-IoT and LTE-M, WCDMA.</summary>
    public IReadOnlyList<PresetMenu> CellularMenus => _cellular ??=
    [
        Menu("Uplink", "LTE SC-FDMA and NR PUSCH uplinks (TS 36.211, TS 38.211)",
        [
            Item("LTE 20 MHz uplink, SC-FDMA", Lte20 with { Uplink = true }),
            Item("NR 20 MHz uplink, CP-OFDM (30 kHz)", Nr20 with { Uplink = true }),
            Item("NR 20 MHz uplink, DFT-s-OFDM QPSK", Nr20 with { Uplink = true, TransformPrecoding = true, Modulation = QamOrder.Qpsk }),
            Item("NR 20 MHz uplink, DFT-s-OFDM π/2-BPSK", Nr20 with { Uplink = true, TransformPrecoding = true, Pi2Bpsk = true }),
            Item("NR 20 MHz uplink, DFT-s-OFDM π/2-BPSK, RB 10 + 24", Nr20 with { Uplink = true, TransformPrecoding = true, Pi2Bpsk = true, RbStart = 10, RbCount = 24 }),
        ]),
        Menu("Test models", "The 3GPP base-station test models on a 20 MHz carrier: NR-FR1-TM (TS 38.141-1 cl. 4.9.2), E-TM (TS 36.141 cl. 6.1.1)",
        [
            .. Ofdm3gpp.NrTestModels.Select(tm => Item(tm + "   NR 20 MHz, 30 kHz", Nr20 with { TestModel = tm })),
            .. Ofdm3gpp.LteTestModels.Select(tm => Item(tm + "   LTE 20 MHz", Lte20 with { TestModel = tm })),
            Item("E-TM3.1 TDD   LTE 20 MHz, the test model's TDD pattern", Lte20 with { TestModel = "E-TM3.1", Tdd = "TM" }),
        ]),
        Menu("NB-IoT / LTE-M", "NB-IoT uplink (TS 36.211 cl. 10.1) and the LTE-M uplink narrowband (TS 36.211 cl. 5.2.4)",
        [
            Item("NB-IoT uplink, 12 tones at 15 kHz", new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = SignalSpec.NbIotPreset }),
            Item("NB-IoT uplink, single tone at 15 kHz", new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = SignalSpec.NbIotPreset, NbTones = 1 }),
            Item("NB-IoT uplink, single tone at 3.75 kHz", new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = SignalSpec.NbIotPreset, ScsHz = 3.75e3, NbTones = 1 }),
            Item("NB-IoT uplink, single tone π/2-BPSK", new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = SignalSpec.NbIotPreset, NbTones = 1, Pi2Bpsk = true }),
            .. new[] { (1.4e6, 6, 0), (10e6, 50, 0), (10e6, 50, 4) }.Select(m =>
            {
                var o = Ofdm3gpp.LteM(m.Item2, m.Item3);
                return Item($"LTE-M uplink, narrowband {m.Item3} of LTE {m.Item1 / 1e6:0.#} MHz, 16QAM",
                    new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = m.Item1, Modulation = QamOrder.Qam16, Uplink = true, RbStart = o.RbStart, RbCount = o.RbCount });
            }),
        ]),
        Menu("WCDMA", "UTRA FDD test models (TS 25.141 cl. 6.1.1) at 2140 MHz, the 12.2 kb/s uplink reference channel (TS 25.141 Annex A) at 1950 MHz. Sets Centre Fc unless it is an expression.",
            Enum.GetValues<WcdmaModel>().Select(m => Item(Wcdma.ModelName(m) + (m == WcdmaModel.Uplink12k2 ? "   1950 MHz" : "   2140 MHz"),
                new CarrierSpec { Standard = CarrierStandard.Wcdma, WcdmaModel = m }, m == WcdmaModel.Uplink12k2 ? 1950e6 : 2140e6))),
    ];

    private PresetItem WlanItem(string header, WlanFormat f, double bwHz, int mcs, double fcHz)
        => Item(header, new CarrierSpec { Standard = CarrierStandard.Wlan, WlanFormat = f, BandwidthHz = bwHz, Mcs = mcs, Oversample = 4 }, fcHz);

    /// <summary>Connectivity: Wi-Fi, 802.11b, Bluetooth BR / EDR, 802.15.4, Z-Wave, Wi-SUN, DECT, 802.11ad, WiMAX.</summary>
    public IReadOnlyList<PresetMenu> ConnectivityMenus => _connectivity ??=
    [
        Menu("Wi-Fi", "One IEEE 802.11 OFDM packet and its gap at 4× oversampling (the mask out to 1.5 BW is in the record). Sets Centre Fc unless it is an expression.",
        [
            WlanItem("802.11a 20 MHz MCS7 (54 Mb/s)   5.18 GHz", WlanFormat.Legacy, 20e6, 7, 5.18e9),
            WlanItem("802.11n 40 MHz MCS7   5.19 GHz", WlanFormat.Ht, 40e6, 7, 5.19e9),
            WlanItem("802.11ac 80 MHz MCS9   5.21 GHz", WlanFormat.Vht, 80e6, 9, 5.21e9),
            WlanItem("802.11ax 80 MHz MCS11   5.21 GHz", WlanFormat.He, 80e6, 11, 5.21e9),
            WlanItem("802.11ax 160 MHz MCS11   5.25 GHz", WlanFormat.He, 160e6, 11, 5.25e9),
            WlanItem("802.11be 320 MHz MCS13   6.105 GHz", WlanFormat.Eht, 320e6, 13, 6.105e9),
        ]),
        Menu("802.11b", "One IEEE 802.11b DSSS / CCK frame of 1000 bytes at 2412 MHz (channel 1). Sets Centre Fc unless it is an expression.",
            new[] { 1e6, 2e6, 5.5e6, 11e6 }.Select(r => Item($"{r / 1e6:0.#} Mb/s   {(r < 5e6 ? "DSSS" : "CCK")}   2412 MHz",
                new CarrierSpec { Standard = CarrierStandard.Wlan11b, BitRateBps = r }, 2412e6))),
        Menu("Bluetooth BR / EDR", "Bluetooth Classic packets: BR GFSK, and EDR with its DPSK payload. Sets Centre Fc unless it is an expression.",
        [
            .. FskFamily("Bluetooth"),
            ScItem("Bluetooth EDR 2 Mb/s", "EDR 2 Mb/s payload only (SC π/4-DQPSK)"),
            ScItem("Bluetooth EDR 3 Mb/s", "EDR 3 Mb/s payload only (SC 8DPSK)"),
        ]),
        Menu("802.15.4", "IEEE 802.15.4 O-QPSK at 2405 MHz (channel 11): the DSSS PHY, or its chips as an SC carrier. Sets Centre Fc unless it is an expression.",
        [
            Item("O-QPSK 2.4 GHz, 127-byte PSDU (DSSS)   2405 MHz", new CarrierSpec { Standard = CarrierStandard.Oqpsk154 }, 2405e6),
            ScItem("IEEE 802.15.4 O-QPSK 2.4 GHz", "O-QPSK chips only, no spreading (SC half-sine)"),
        ]),
        Menu("Z-Wave", "ITU-T G.9959 FSK and GFSK data rates. Sets Centre Fc unless it is an expression.", FskFamily("Z-Wave")),
        Menu("Wi-SUN", "IEEE 802.15.4 SUN FSK modes (Centre Fc is left as it is)", FskFamily("Wi-SUN")),
        Menu("DECT", "ETSI EN 300 175-2 GFSK. Sets Centre Fc unless it is an expression.", FskFamily("DECT")),
        Menu("802.11ad", "The IEEE 802.11 DMG single carrier at 1.76 Gsym/s",
            ScQamPresets.All.Where(p => p.Name.StartsWith("IEEE 802.11ad", StringComparison.Ordinal)).Select(p => ScItem(p.Name))),
        Menu("WiMAX", "The WiMAX 10 MHz OFDMA numerology (unverified)",
            OfdmPresets.All.Where(p => p.Name.StartsWith("WiMAX", StringComparison.Ordinal))
                .Select(p => Item(p.Name, new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = p.Name }))),
    ];

    private static readonly (string Name, GnssSignal Signal, double Fc)[] GnssPresets =
    [
        ("GPS L1 C/A", GnssSignal.GpsL1Ca, 1575.42e6), ("GPS L5", GnssSignal.GpsL5, 1176.45e6),
        ("Galileo E1", GnssSignal.GalileoE1, 1575.42e6), ("BeiDou B1I", GnssSignal.BeidouB1I, 1561.098e6),
    ];

    /// <summary>The GNSS presets' two satellites: PRN 1 and PRN 5, Doppler-shifted and code-delayed, the second 3 dB down.</summary>
    public const string GnssPresetSatellites = "1@1.8kHz@12@0dB,5@-2kHz@300@-3dB";

    /// <summary>Navigation: GNSS, VOR and ILS, DME.</summary>
    public IReadOnlyList<PresetMenu> NavigationMenus => _navigation ??=
    [
        Menu("GNSS", "Two satellites at C/N0 45 dB-Hz, 20 ms, at the signal's centre. Sets Centre Fc unless it is an expression.",
            GnssPresets.Select(g => Item($"{g.Name}   {g.Fc / 1e6:0.###} MHz",
                new CarrierSpec { Standard = CarrierStandard.Gnss, GnssSignal = g.Signal, Satellites = GnssPresetSatellites, Cn0DbHz = 45 }, g.Fc))),
        Menu("VOR / ILS", "ICAO Annex 10 Vol. I VOR and ILS signals. Sets Centre Fc unless it is an expression.",
            new[] { "VOR 45°", "ILS LOC", "ILS GS" }.Select(AnalogItem)),
        Menu("DME", "DME pulse pairs (ICAO Annex 10 Vol. I 3.5; unverified)", RadarFamily(p => p.Family == "DME")),
    ];

    /// <summary>Broadcast and satellite: DVB-T / T2, ISDB-T and DAB, DVB-S2 / S2X, telemetry.</summary>
    public IReadOnlyList<PresetMenu> BroadcastMenus => _broadcast ??=
    [
        Menu("DVB-T / ISDB-T / DAB", "OFDM broadcast numerologies (ISDB-T unverified); Centre Fc is left as it is",
            OfdmPresets.All.Where(p => !p.Name.StartsWith("WiMAX", StringComparison.Ordinal))
                .Select(p => Item(p.Name, new CarrierSpec { Standard = CarrierStandard.Ofdm, OfdmPreset = p.Name }))),
        Menu("DVB-S2 / S2X", "DVB-S2 and DVB-S2X PL frames at 27.5 Msym/s: header, FECFRAME, pilot blocks",
            ScQamPresets.All.Where(p => p.Name.StartsWith("DVB-S2", StringComparison.Ordinal)).Select(p => ScItem(p.Name))),
        Menu("Telemetry", "π/2-BPSK and SOQPSK-TG single carriers", [ScItem("π/2-BPSK"), ScItem("SOQPSK-TG 10 Mb/s")]),
    ];

    /// <summary>Radio and LMR: P25, DMR, dPMR, TETRA, POCSAG, GSM and EDGE, analog voice.</summary>
    public IReadOnlyList<PresetMenu> RadioMenus => _radio ??=
    [
        Menu("P25 / DMR / dPMR", "4FSK land mobile radio (Centre Fc is left as it is)", FskFamily("LMR")),
        Menu("TETRA", "ETSI EN 300 392-2 π/4-DQPSK", [ScItem("TETRA π/4-DQPSK")]),
        Menu("POCSAG", "ITU-R M.584-2 paging FSK", FskFamily("Paging")),
        Menu("GSM / EDGE", "The GSM GMSK normal burst, and EDGE 8PSK and 16QAM bursts",
        [
            .. FskFamily("GSM").Select(i => i with { Header = "GSM " + i.Header }),
            ScItem("EDGE 8PSK"), ScItem("EDGE Evolution 16QAM"),
        ]),
        Menu("FM / AM / SSB", "Analog voice carriers on a 1 kHz tone (SSB: two tones). Sets Centre Fc unless it is an expression.",
            new[] { "AM broadcast", "FM broadcast", "NBFM 12.5 kHz", "NBFM 25 kHz", "SSB two-tone" }.Select(AnalogItem)),
    ];

    /// <summary>Radar: phase codes, FM, PRI patterns, DFS test radars, FMCW.</summary>
    public IReadOnlyList<PresetMenu> RadarMenus => _radar ??=
    [
        Menu("Codes", "Phase-coded pulses: the code's PSL, ISL and mainlobe are in the stats", RadarFamily(p => p.Family == "Codes")),
        Menu("FM", "Nonlinear FM and stepped-frequency pulses", RadarFamily(p => p.Family == "FM" && p.Ramp is null)),
        Menu("PRI", "Staggered, jittered and burst trains", RadarFamily(p => p.Family == "PRI")),
        Menu("DFS ETSI", "ETSI EN 301 893 V2.1.1 Annex D radar test signals, one member of each range", RadarFamily(p => p.Family == "DFS ETSI")),
        Menu("DFS FCC", "FCC 47 CFR 15.407 / KDB 905462 D02 radar types, one member of each range (unverified)", RadarFamily(p => p.Family == "DFS FCC")),
        Menu("FMCW", "One FMCW period: sawtooth or triangle", RadarFamily(p => p.Ramp is not null)),
    ];

    /// <summary>Tones and noise: multitone, NPR and AWGN.</summary>
    public IReadOnlyList<PresetMenu> TonesMenus => _tones ??=
    [
        Menu("Multitone / NPR / AWGN", "Tones with chosen phases, noise with a notch for the NPR, band-limited noise",
            MultitonePresets.All.Select(p => Item(p.Name + "   " + p.Description, TonesSpec(p.Shape)))),
    ];

    // ── quality readouts ──────────────────────────────────────────────────

    /// <summary>A carrier's quality reading, or why it could not be read.</summary>
    private sealed record Readout(WaveformQuality? Quality, string? Error);

    private static readonly ConditionalWeakTable<CarrierRecord, Readout> Readouts = new();

    /// <summary>The quality of a carrier other than LTE / NR (their EVM is SignalEvm's), read on its own record as a
    /// receiver reads it; computed once per generated carrier.</summary>
    private static Readout ReadoutOf(ModulatedSignal sig, CarrierRecord c)
        => Readouts.GetValue(c, _ =>
        {
            if (c.Spec.Standard is CarrierStandard.Lte or CarrierStandard.Nr or CarrierStandard.Dbpsk or CarrierStandard.Css
                || !SignalQuality.Reads(c.Spec)) return new Readout(null, null);
            try
            {
                var w = SignalQuality.Receive(c, sig.RecordSeconds, c.Native, c.NativeRateHz, 0, 0, out string? why);
                return w is null ? new Readout(null, why) : new Readout(SignalQuality.Evaluate(w), null);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                return new Readout(null, ex.Message);
            }
        });

    private static string Pct(double v) => v.ToString(v < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " %";
    private static string Db(double v) => v.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

    private static string Limit(WaveformQuality q)
        => double.IsNaN(q.LimitPercent) ? "" : $"   limit {Pct(q.LimitPercent)}   {(q.ErrorPercent <= q.LimitPercent ? "PASS" : "FAIL")}";

    /// <summary>
    /// The readout lines of one carrier (empty for a carrier with none): Wi-Fi EVM against its MCS limit; GNSS code phase
    /// and C/N0 per PRN; WCDMA EVM, peak code-domain error and (with <paramref name="full"/>) each channel's code-domain
    /// power; 802.11b EVM and mask margin; NPR; a pulse's PSL / ISL / mainlobe; analog depth, deviation, bearing, DDM; FSK
    /// deviation and error, an EDR payload's EVM.
    /// </summary>
    public static IEnumerable<string> QualityLines(ModulatedSignal sig, CarrierRecord c, bool full = false)
    {
        var r = ReadoutOf(sig, c);
        if (r.Error is { } why) { yield return "quality: " + why; yield break; }
        if (r.Quality is not { } q) yield break;
        var inv = CultureInfo.InvariantCulture;
        switch (q.Kind)
        {
            case QualityKind.Wlan:
            {
                var w = Wlan.Resolve(c.Spec);
                yield return $"Wi-Fi EVM {Pct(q.ErrorPercent)} ({Db(20 * Math.Log10(Math.Max(q.ErrorPercent, 1e-12) / 100))})   peak {Pct(q.PeakPercent)}"
                             + $"   limit {Pct(q.LimitPercent)} (MCS{w.Mcs}, {Wlan.EvmLimitDb(w).ToString("0", inv)} dB)   {(q.ErrorPercent <= q.LimitPercent ? "PASS" : "FAIL")}";
                break;
            }
            case QualityKind.Gnss:
                foreach (var g in q.Gnss!)
                    yield return string.Format(inv, "PRN {0,-3} code phase {1:0.00} chips   peak / floor {2:0.0} dB   C/N0 {3:0.0} dB-Hz",
                        g.Prn, g.CodePhaseChips, g.PeakToFloorDb, g.Cn0DbHz);
                break;
            case QualityKind.Wcdma:
                yield return $"WCDMA EVM {Pct(q.ErrorPercent)}{Limit(q)}   peak code-domain error {Db(q.Wcdma!.PeakCdeDb)}";
                if (full)
                    foreach (var ch in q.Wcdma.Channels)
                        yield return string.Format(inv, "  {0,-16} {1,7:0.00} dB", ch.Channel.Name, ch.PowerDb);
                break;
            case QualityKind.Wlan11b:
                yield return $"802.11b EVM {Pct(q.ErrorPercent)}   peak {Pct(q.PeakPercent)}   mask margin {Db(q.MaskMarginDb)}";
                break;
            case QualityKind.Oqpsk154:
                yield return $"802.15.4 EVM {Pct(q.ErrorPercent)}   peak {Pct(q.PeakPercent)}{Limit(q)}";
                break;
            case QualityKind.Sc or QualityKind.Ofdm:
                yield return $"EVM {Pct(q.ErrorPercent)}   peak {Pct(q.PeakPercent)}{Limit(q)}";
                break;
            case QualityKind.Npr:
                yield return $"NPR {Db(q.NprDb)} (the band's density over the notch's)";
                break;
            case QualityKind.Pulse when q.Compression is { } p:
                yield return string.Format(inv, "compression   mainlobe {0} (−3 dB)   null to null {1}   PSL {2:0.0} dB   ISL {3:0.0} dB",
                    PulsedCw.FormatTime(p.MainlobeSeconds), PulsedCw.FormatTime(p.NullToNullSeconds), p.PslDb, p.IslDb);
                break;
            case QualityKind.Analog when q.Analog is { } a:
            {
                var parts = new List<string> { string.Format(inv, "message {0} at {1:0.###}", Fsk.FormatHz(a.MessageHz), a.MessageLevel) };
                if (!double.IsNaN(a.Depth)) parts.Add(string.Format(inv, "depth {0:0.0} %", 100 * a.Depth));
                if (!double.IsNaN(a.DeviationHz)) parts.Add("deviation " + Fsk.FormatHz(a.DeviationHz));
                if (!double.IsNaN(a.PeakPhaseRad)) parts.Add(string.Format(inv, "peak phase {0:0.###} rad", a.PeakPhaseRad));
                if (!double.IsNaN(a.BearingDeg)) parts.Add(string.Format(inv, "bearing {0:0.0}°", a.BearingDeg));
                if (!double.IsNaN(a.Ddm)) parts.Add(string.Format(inv, "DDM {0:0.0000}", a.Ddm));
                if (!double.IsNaN(a.Sdm)) parts.Add(string.Format(inv, "SDM {0:0.000}", a.Sdm));
                yield return string.Join("   ", parts);
                break;
            }
            case QualityKind.Fsk:
                yield return $"deviation ±{Fsk.FormatHz(q.DeviationHz)}   offset {Fsk.FormatHz(q.OffsetHz)}   FSK error {Pct(q.ErrorPercent)}";
                if (q.Switched is { } e) yield return $"EDR payload EVM {Pct(e.RmsPercent)}   peak {Pct(e.PeakPercent)}";
                break;
        }
    }

    /// <summary>The EVM row of a carrier other than LTE / NR whose reading is an EVM (Wi-Fi, SC, WCDMA, 802.11b, 802.15.4,
    /// an OFDM numerology), or null.</summary>
    private static EvmRow? FamilyEvmRow(ModulatedSignal sig, int index)
    {
        var c = sig.Carriers[index - 1];
        if (ReadoutOf(sig, c).Quality is not { } q || q.Kind == QualityKind.Fsk || double.IsNaN(q.ErrorPercent)) return null;
        return new EvmRow(index, $"C{index}  {c.Spec.Label}",
            new EvmResult(q.ErrorPercent, q.PeakPercent, q.LimitPercent, q.Points ?? [], q.Reference ?? [], q.Points?.Length ?? 0, 1));
    }

    /// <summary>The overlays a family draws on the spectrum: Wi-Fi's transmit mask, a GNSS signal's expected PSD.</summary>
    private static void AddSpectrumOverlays(DataSetBuilder add, ModulatedSignal sig, double[] f, int[] keep, double fScale)
    {
        if (sig.Carriers.Count != 1) return;
        var c = sig.Carriers[0];
        if (c.Spec.Standard == CarrierStandard.Wlan)
        {
            var m = Wlan.Mask(Wlan.Resolve(c.Spec));
            var x = m.OffsetHz.Reverse().Select(o => -o).Concat(m.OffsetHz).Select(o => (c.OffsetHz + o) / fScale).ToArray();
            add("Mask", x, [.. m.Dbr.Reverse(), .. m.Dbr]);
        }
        if (c.Spec.Standard == CarrierStandard.Gnss)
        {
            var g = Gnss.Resolve(c.Spec);
            var p = keep.Select(i => Gnss.Psd(g.Signal, f[i] - c.OffsetHz)).ToArray();
            double pk = p.Max();
            add("Expected", [.. keep.Select(i => f[i] / fScale)], [.. p.Select(v => Math.Max(10 * Math.Log10(v / pk), -120))]);
        }
    }

    private delegate void DataSetBuilder(string name, double[] x, double[] y);
}
