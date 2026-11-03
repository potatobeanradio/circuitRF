// ================================================================
//  SignalDesignerViewModel.cs  —  the MOD_Source Signal Designer
//
//  Edits a MOD_Source's carriers (LTE / NR / CW / pulsed CW / FSK /
//  imported I/Q file),
//  record length, clipping and spectral shaping, previews the signal
//  (generated in the BACKGROUND through SignalCache — the same cache a
//  simulation reads, so a previewed signal is never generated twice),
//  and writes the result back as ONE SetParametersCommand on the
//  owning schematic's undo stack (Carrier[i] / Record / Clip / Shaping,
//  plus Fc and Pavg). Opened from Tools ▸ Signal Designer it belongs to
//  no MOD_Source (SetStandalone): a signal is designed or a custom I/Q
//  file read, previewed and exported, and there is nothing to Apply to.
//
//  The plots are Data Display PlotControls fed from a DataSet (PSD in
//  dB re peak against the offset from Fc, CCDF against dB above the
//  mean with the Gaussian-noise reference; on the EVM tab the
//  constellation of each LTE / NR carrier over its ideal points, and
//  a few envelope cycles of power around the record's peak with the
//  average and peak lines) — never a bespoke chart.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Ui.Signals;

public sealed partial class CarrierRowViewModel : ObservableObject
{
    public static IReadOnlyList<string> Standards { get; } = ["LTE", "NR", "CW", "PCW", "FSK", "DBPSK", "CSS", "FILE"];
    public static IReadOnlyList<string> Modulations { get; } = ["QPSK", "16QAM", "64QAM", "256QAM", "1024QAM"];
    public static IReadOnlyList<double> ScsOptions { get; } = [15, 30, 60];

    [ObservableProperty] private int _index = 1;
    [ObservableProperty] private string _standard = "LTE";
    [ObservableProperty] private double _bandwidthMHz = 20;
    [ObservableProperty] private double _scsKHz = 30;
    [ObservableProperty] private string _modulation = "64QAM";
    [ObservableProperty] private double _offsetMHz;
    [ObservableProperty] private double _powerDb;
    [ObservableProperty] private int _seed = 1;
    [ObservableProperty] private string _filePath = "";
    /// <summary>A FILE carrier's sample rate (MHz); 0 = the file's own <c># sample_rate_hz</c> header.</summary>
    [ObservableProperty] private double _sampleRateMHz;

    /// <summary>
    /// The FILE sample rate as typed on the Custom I/Q card (the rate the samples were recorded at — not the Source
    /// box's preview / export rate, which a custom file is resampled to): a number in MHz, or a number with its own frequency unit, read by
    /// the parser every typed value-with-unit field uses (<see cref="MatchValueFormat.TryParseWithUnit"/>). A valid entry
    /// sets <see cref="SampleRateMHz"/>; an invalid or empty one marks the field (<see cref="SampleRateError"/>) and goes
    /// no further, so the signal keeps the last valid rate and nothing is generated from a half-typed one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleRateError), nameof(HasSampleRateError), nameof(SampleRateTip))]
    private string _sampleRateText = "";

    /// <summary>Why <see cref="SampleRateText"/> is not a sample rate, or null.</summary>
    public string? SampleRateError => TryParseRateMHz(SampleRateText, out _);
    public bool HasSampleRateError => SampleRateError is not null;
    public string SampleRateTip => SampleRateError
        ?? "The rate the file's samples were taken at: a number in MHz, or with its unit (1.5 GHz, 30720000 Hz). "
         + "Filled from the file's # sample_rate_hz header when it has one; a file without one needs it entered.";

    partial void OnSampleRateTextChanged(string value)
    {
        if (TryParseRateMHz(value, out double mhz) is null && mhz != SampleRateMHz) SampleRateMHz = mhz;
    }

    // Set from code (loading a source, a file's header): the field shows it, unless it already reads that value.
    partial void OnSampleRateMHzChanged(double value)
    {
        if (TryParseRateMHz(SampleRateText, out double shown) is null && shown == value) return;
        SampleRateText = value > 0 ? value.ToString("0.#########", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Null and the rate in MHz, or the reason <paramref name="text"/> is not a sample rate.</summary>
    public static string? TryParseRateMHz(string? text, out double mhz)
    {
        mhz = 0;
        if (string.IsNullOrWhiteSpace(text)) return "Enter the rate the samples were taken at, in MHz (e.g. 122.88).";
        if (!MatchValueFormat.TryParseWithUnit(text, MatchQuantity.Frequency, "MHz", out double hz, out _))
            return "Not a sample rate: a number in MHz (122.88), or with its unit (1.5 GHz, 30720000 Hz).";
        if (hz <= 0) return "The sample rate must be greater than zero.";
        mhz = hz / 1e6;
        return null;
    }

    /// <summary>The card picked on the left; the EVM tab shows this carrier.</summary>
    [ObservableProperty] private bool _isSelected;

    public bool IsOfdm => Standard is "LTE" or "NR";
    public bool IsNr => Standard == "NR";
    public bool IsFile => Standard == "FILE";
    public bool IsPcw => Standard == "PCW";
    public bool IsFsk => Standard == "FSK";
    public bool IsDbpsk => Standard == "DBPSK";
    public bool IsCss => Standard == "CSS";
    /// <summary>The card holds a Data choice: FSK and DBPSK both send PN9, PN15 or a fixed pattern.</summary>
    public bool HasData => IsFsk || IsDbpsk;

    // ── an FSK carrier ────────────────────────────────────────────────────
    // The rate and deviation are numbers in kb/s and kHz; the data is picked (PN9, PN15) or, for a fixed pattern,
    // typed as 0s and 1s and validated as typed: a valid pattern sets Data, an invalid one marks the field.
    public static IReadOnlyList<string> FilterOptions { get; } = ["None (CPFSK)", "Gaussian"];
    public static IReadOnlyList<string> DataOptions { get; } = ["PN9", "PN15", "Fixed bits"];

    [ObservableProperty] private double _bitRateKbps = Fsk.DefaultBitRate / 1e3;
    [ObservableProperty] private double _deviationKHz = Fsk.DefaultDeviation / 1e3;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsGaussian))] private string _filter = FilterOptions[0];
    [ObservableProperty] private double _bt = 0.5;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFixedBits))] private string _dataChoice = Fsk.DefaultData;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BitsError), nameof(HasBitsError))] private string _bitsText = "10110100";
    /// <summary>What the signal is built from: PN9, PN15 or the last valid pattern.</summary>
    [ObservableProperty] private string _data = Fsk.DefaultData;

    public bool IsGaussian => Filter == FilterOptions[1];
    public bool IsFixedBits => DataChoice == DataOptions[2];
    public string? BitsError => IsFixedBits && !IsBitPattern(BitsText) ? "A pattern of 0s and 1s, e.g. 10110100." : null;
    public bool HasBitsError => BitsError is not null;

    private static bool IsBitPattern(string? t) => !string.IsNullOrEmpty(t) && t.All(ch => ch is '0' or '1');

    partial void OnDataChoiceChanged(string value) => SyncData();
    partial void OnBitsTextChanged(string value) => SyncData();

    private void SyncData()
    {
        if (!IsFixedBits) Data = DataChoice;
        else if (IsBitPattern(BitsText.Trim())) Data = BitsText.Trim();
    }

    partial void OnBitRateKbpsChanged(double value) => FskChanged();
    partial void OnDeviationKHzChanged(double value) => FskChanged();
    partial void OnFilterChanged(string value) => FskChanged();
    partial void OnBtChanged(double value) => FskChanged();
    partial void OnDataChanged(string value) { FskChanged(); DbpskChanged(); }

    private void FskChanged()
    {
        OnPropertyChanged(nameof(FskInfoText));
        OnPropertyChanged(nameof(FskRefusal));
    }

    // ── a DBPSK carrier ───────────────────────────────────────────────────
    // A rate in b/s (100 for a UNB uplink in the 868 MHz band, 600 elsewhere) and the same Data as an FSK card.
    [ObservableProperty] private double _dbpskRateBps = Dbpsk.DefaultBitRate;

    partial void OnDbpskRateBpsChanged(double value) => DbpskChanged();

    private void DbpskChanged()
    {
        OnPropertyChanged(nameof(DbpskInfoText));
        OnPropertyChanged(nameof(DbpskRefusal));
    }

    /// <summary>Why the DBPSK carrier cannot be generated, naming its field, or null.</summary>
    public string? DbpskRefusal
    {
        get
        {
            if (!IsDbpsk) return null;
            try { Dbpsk.Validate(ToSpec()); return null; }
            catch (FormatException ex) { return ex.Message; }
        }
    }

    /// <summary>Read-only under the fields: the bit period, the bits in the record (twice the data when it must be to
    /// repeat), the record and the bandwidth.</summary>
    public string DbpskInfoText
    {
        get
        {
            if (!IsDbpsk || DbpskRefusal is not null) return "";
            var s = Dbpsk.Resolve(ToSpec());
            int period = Fsk.DataLength(s.Data);
            return string.Format(CultureInfo.InvariantCulture, "bit {0}  ·  {1:N0} bits{2}  ·  record {3}  ·  bandwidth {4}",
                PulsedCw.FormatTime(1 / s.BitRate), s.Bits, s.Bits > period ? " (the data twice, to end in phase)" : "",
                PulsedCw.FormatTime(s.RecordSeconds), Fsk.FormatHz(Dbpsk.BandwidthHz(s)));
        }
    }

    // ── a CSS carrier ─────────────────────────────────────────────────────
    // The spreading factor and the chirp bandwidth, both picked: the values an 868 MHz LPWAN uses.
    public static IReadOnlyList<int> SfOptions { get; } = [7, 8, 9, 10, 11, 12];
    public static IReadOnlyList<double> CssBandwidthOptions { get; } = [125, 250, 500];

    [ObservableProperty] private int _sf = Css.DefaultSf;
    [ObservableProperty] private double _cssBandwidthKHz = Css.DefaultBandwidthHz / 1e3;

    partial void OnSfChanged(int value) => CssChanged();
    partial void OnCssBandwidthKHzChanged(double value) => CssChanged();

    private void CssChanged()
    {
        OnPropertyChanged(nameof(CssInfoText));
        OnPropertyChanged(nameof(CssRefusal));
    }

    /// <summary>Why the CSS carrier cannot be generated, naming its field, or null.</summary>
    public string? CssRefusal
    {
        get
        {
            if (!IsCss) return null;
            try { Css.Validate(ToSpec()); return null; }
            catch (FormatException ex) { return ex.Message; }
        }
    }

    /// <summary>Read-only under the fields: the symbol, the raw bit rate and the packet the record holds.</summary>
    public string CssInfoText
    {
        get
        {
            if (!IsCss || CssRefusal is not null) return "";
            var s = Css.Resolve(ToSpec());
            return string.Format(CultureInfo.InvariantCulture, "symbol {0}  ·  {1:0.###} kb/s raw  ·  packet {2:0.##} symbols, {3}",
                PulsedCw.FormatTime(s.SymbolSeconds), s.RawBitRate / 1e3, Css.PacketSymbols, PulsedCw.FormatTime(s.RecordSeconds));
        }
    }

    /// <summary>Why the FSK carrier cannot be generated, naming its field, or null.</summary>
    public string? FskRefusal
    {
        get
        {
            if (!IsFsk) return null;
            try { Fsk.Validate(ToSpec()); return null; }
            catch (FormatException ex) { return ex.Message; }
        }
    }

    /// <summary>Read-only under the fields: the modulation index, what the settings make, the data length and the record.</summary>
    public string FskInfoText
    {
        get
        {
            if (!IsFsk || FskRefusal is not null) return "";
            var s = Fsk.Resolve(ToSpec());
            return string.Format(CultureInfo.InvariantCulture, "h = {0:0.###}  ·  {1}  ·  {2:N0} bits  ·  record {3}",
                s.ModulationIndex, s.Kind, s.Bits, PulsedCw.FormatTime(s.RecordSeconds));
        }
    }

    /// <summary><paramref name="value"/> in a unit <paramref name="exponent"/> above the base, read back the way the
    /// netlist reads <c>32.768kbps</c>, so the value written is the one typed (32.768 x 1e3 is not 32768).</summary>
    private static double Scaled(double value, string exponent)
        => double.Parse(value.ToString("R", CultureInfo.InvariantCulture) + exponent, NumberStyles.Float, CultureInfo.InvariantCulture);

    // ── a pulsed-CW carrier's timing ─────────────────────────────────────
    // Typed as text, a bare number in µs or a number with its own unit (50us, 0.05 ms, 500 ns), validated as typed
    // the way the file sample rate is: a valid entry sets the value the signal is built from, an invalid one marks
    // the field and goes no further. A pulse that parses but cannot be generated (PW >= PRI, an edge longer than
    // half the width) marks the field its refusal names. An empty Delay centres the pulse in the PRI.
    [ObservableProperty] private double _priS = PulsedCw.DefaultPri;
    [ObservableProperty] private double _widthS = PulsedCw.DefaultWidth;
    [ObservableProperty] private double _riseS = PulsedCw.DefaultEdge;
    [ObservableProperty] private double _fallS = PulsedCw.DefaultEdge;
    [ObservableProperty] private double? _delayS;
    /// <summary>The droop window, written in the netlist only; carried through the Designer unchanged.</summary>
    private double? _measureStart, _measureEnd;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PriError), nameof(HasPriError))] private string _priText = Us(PulsedCw.DefaultPri);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WidthError), nameof(HasWidthError))] private string _widthText = Us(PulsedCw.DefaultWidth);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RiseError), nameof(HasRiseError))] private string _riseText = Us(PulsedCw.DefaultEdge);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FallError), nameof(HasFallError))] private string _fallText = Us(PulsedCw.DefaultEdge);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DelayError), nameof(HasDelayError), nameof(DelayInfo))] private string _delayText = "";

    private static string Us(double s) => (s * 1e6).ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>Null and the time in seconds, or why <paramref name="text"/> is not one (a bare number is µs).</summary>
    public static string? TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return "Enter a time: a number in µs, or with its unit (50 us, 0.5 ms, 500 ns).";
        string t = text.Trim().Replace(" ", "");
        // a bare number is µs, read as "<number>us" so 50 and 50us are the same double the netlist would hold
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) t += "us";
        return SignalSpec.TryParseSeconds(t, out seconds) ? null : "Not a time: a number in µs, or with its unit (50 us, 0.5 ms, 500 ns).";
    }

    /// <summary>Why the pulse cannot be generated, naming its field ("pCW carrier: PW (…) must be …"), or null.</summary>
    public string? PulseRefusal
    {
        get
        {
            if (!IsPcw) return null;
            try { PulsedCw.Validate(ToSpec()); return null; }
            catch (FormatException ex) { return ex.Message; }
        }
    }

    private string? FieldError(string field, string text, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(text)) return PulseRefusalNames(field);
        return TryParseTime(text, out _) ?? PulseRefusalNames(field);
    }

    private string? PulseRefusalNames(string field)
    {
        const string head = "pCW carrier: ";
        if (PulseRefusal is not { } why || !why.StartsWith(head, StringComparison.Ordinal)) return null;
        string named = why[head.Length..].Split(' ', '(')[0];
        return named == field ? why[head.Length..] : null;
    }

    public string? PriError => FieldError("PRI", PriText);
    public string? WidthError => FieldError("PW", WidthText);
    public string? RiseError => FieldError("Rise", RiseText);
    public string? FallError => FieldError("Fall", FallText);
    public string? DelayError => FieldError("Delay", DelayText, optional: true);
    public bool HasPriError => PriError is not null;
    public bool HasWidthError => WidthError is not null;
    public bool HasRiseError => RiseError is not null;
    public bool HasFallError => FallError is not null;
    public bool HasDelayError => DelayError is not null;

    /// <summary>Read-only beside PRI and PW: the repetition frequency and the 50 %-to-50 % duty.</summary>
    public string PrfText => PriS > 0 ? "PRF " + FormatFrequency(1 / PriS) : "";
    public string DutyText => PriS > 0 ? string.Format(CultureInfo.InvariantCulture, "duty {0:0.##} %", 100 * WidthS / PriS) : "";
    /// <summary>What an empty Delay means for the pulse as it stands, beside the field (the field itself is too narrow).</summary>
    public string DelayInfo => DelayText.Trim().Length == 0 && PriS > 0 && WidthS > 0
        ? "empty: centred, " + PulsedCw.FormatTime(PulsedCw.Resolve(ToSpec() with { DelaySeconds = null }).Delay)
        : "";

    private static string FormatFrequency(double hz)
    {
        var inv = CultureInfo.InvariantCulture;
        return hz >= 1e6 ? string.Format(inv, "{0:0.###} MHz", hz / 1e6)
             : hz >= 1e3 ? string.Format(inv, "{0:0.###} kHz", hz / 1e3)
             : string.Format(inv, "{0:0.###} Hz", hz);
    }

    partial void OnPriTextChanged(string value) { if (TryParseTime(value, out double s) is null) PriS = s; }
    partial void OnWidthTextChanged(string value) { if (TryParseTime(value, out double s) is null) WidthS = s; }
    partial void OnRiseTextChanged(string value) { if (TryParseTime(value, out double s) is null) RiseS = s; }
    partial void OnFallTextChanged(string value) { if (TryParseTime(value, out double s) is null) FallS = s; }
    partial void OnDelayTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) DelayS = null;
        else if (TryParseTime(value, out double s) is null) DelayS = s;
    }

    partial void OnPriSChanged(double value) => PulseChanged();
    partial void OnWidthSChanged(double value) => PulseChanged();
    partial void OnRiseSChanged(double value) => PulseChanged();
    partial void OnFallSChanged(double value) => PulseChanged();
    partial void OnDelaySChanged(double? value) => PulseChanged();

    /// <summary>One value moved: every field's refusal, the PRF, the duty and the centred Delay can move with it.</summary>
    private void PulseChanged()
    {
        foreach (var n in new[] { nameof(PriError), nameof(HasPriError), nameof(WidthError), nameof(HasWidthError),
                                  nameof(RiseError), nameof(HasRiseError), nameof(FallError), nameof(HasFallError),
                                  nameof(DelayError), nameof(HasDelayError), nameof(PrfText), nameof(DutyText),
                                  nameof(DelayInfo), nameof(PulseRefusal) })
            OnPropertyChanged(n);
    }

    public IReadOnlyList<double> BandwidthOptions => Standard switch
    {
        "LTE" => OfdmGenerator.LteBandwidths.Select(b => b / 1e6).ToList(),
        "NR" => OfdmGenerator.NrBandwidthsMHz(ScsKHz * 1e3).Select(b => (double)b).ToList(),
        _ => new List<double> { 20 },
    };

    partial void OnStandardChanged(string value)
    {
        OnPropertyChanged(nameof(IsOfdm)); OnPropertyChanged(nameof(IsNr)); OnPropertyChanged(nameof(IsFile)); OnPropertyChanged(nameof(IsPcw));
        OnPropertyChanged(nameof(IsFsk)); OnPropertyChanged(nameof(IsDbpsk)); OnPropertyChanged(nameof(IsCss)); OnPropertyChanged(nameof(HasData));
        PulseChanged();
        FskChanged();
        DbpskChanged();
        CssChanged();
        OnPropertyChanged(nameof(BandwidthOptions));
        if (!BandwidthOptions.Contains(BandwidthMHz)) BandwidthMHz = BandwidthOptions.Contains(20) ? 20 : BandwidthOptions.FirstOrDefault(20);
    }

    partial void OnScsKHzChanged(double value)
    {
        OnPropertyChanged(nameof(BandwidthOptions));
        if (Standard == "NR" && !BandwidthOptions.Contains(BandwidthMHz)) BandwidthMHz = BandwidthOptions.LastOrDefault(20);
    }

    public CarrierSpec ToSpec()
    {
        var c = BaseSpec();
        if (c.Standard == CarrierStandard.Fsk)
        {
            c = c with
            {
                BitRateBps = Scaled(BitRateKbps, "e3"), DeviationHz = Scaled(DeviationKHz, "e3"),
                GaussianBt = IsGaussian ? Bt : null, Data = Data,
            };
            // Carson's bandwidth, as the netlist's parse sets it; settings that cannot be generated keep the default
            try { return c with { BandwidthHz = Fsk.BandwidthHz(Fsk.Validate(c)) }; }
            catch (FormatException) { return c; }
        }
        if (c.Standard == CarrierStandard.Dbpsk)
        {
            c = c with { BitRateBps = DbpskRateBps, Data = Data };
            try { return c with { BandwidthHz = Dbpsk.BandwidthHz(Dbpsk.Validate(c)) }; }
            catch (FormatException) { return c; }
        }
        if (c.Standard == CarrierStandard.Css)
            return c with { SpreadingFactor = Sf, BandwidthHz = Scaled(CssBandwidthKHz, "e3") };
        if (c.Standard != CarrierStandard.Pcw) return c;
        c = c with
        {
            PriSeconds = PriS, PulseWidthSeconds = WidthS, RiseSeconds = RiseS, FallSeconds = FallS, DelaySeconds = DelayS,
            MeasureStart = _measureStart, MeasureEnd = _measureEnd,
        };
        // the bandwidth is the edges', as the netlist's parse sets it; a pulse that cannot be generated keeps the default
        try { return c with { BandwidthHz = PulsedCw.BandwidthHz(PulsedCw.Validate(c)) }; }
        catch (FormatException) { return c; }
    }

    private CarrierSpec BaseSpec() => new()
    {
        Standard = Standard switch
        {
            "NR" => CarrierStandard.Nr, "CW" => CarrierStandard.Cw, "PCW" => CarrierStandard.Pcw, "FSK" => CarrierStandard.Fsk,
            "DBPSK" => CarrierStandard.Dbpsk, "CSS" => CarrierStandard.Css, "FILE" => CarrierStandard.File, _ => CarrierStandard.Lte,
        },
        BandwidthHz = BandwidthMHz * 1e6,
        ScsHz = ScsKHz * 1e3,
        Modulation = SignalSpec.ParseMod(Modulation),
        OffsetHz = OffsetMHz * 1e6,
        PowerDb = PowerDb,
        Seed = Seed,
        FilePath = Standard == "FILE" ? FilePath : null,
        SampleRateHz = Standard == "FILE" && SampleRateMHz > 0 ? SampleRateMHz * 1e6 : null,
    };

    public static CarrierRowViewModel FromSpec(CarrierSpec c, int index)
    {
        var row = FromBaseSpec(c, index);
        if (c.Standard == CarrierStandard.Fsk)
        {
            var f = Fsk.Resolve(c);
            row.BitRateKbps = f.BitRate / 1e3; row.DeviationKHz = f.Deviation / 1e3;
            row.Filter = f.Bt > 0 ? FilterOptions[1] : FilterOptions[0];
            if (f.Bt > 0) row.Bt = f.Bt;
            if (f.Data is "PN9" or "PN15") row.DataChoice = f.Data;
            else { row.BitsText = f.Data; row.DataChoice = DataOptions[2]; }
            return row;
        }
        if (c.Standard == CarrierStandard.Dbpsk)
        {
            var b = Dbpsk.Resolve(c);
            row.DbpskRateBps = b.BitRate;
            if (b.Data is "PN9" or "PN15") row.DataChoice = b.Data;
            else { row.BitsText = b.Data; row.DataChoice = DataOptions[2]; }
            return row;
        }
        if (c.Standard == CarrierStandard.Css)
        {
            var css = Css.Resolve(c);
            row.Sf = css.Sf;
            row.CssBandwidthKHz = css.BandwidthHz / 1e3;
            return row;
        }
        if (c.Standard != CarrierStandard.Pcw) return row;
        // the stated values, and the defaults for the rest — except Delay, whose default (centred) is an empty field
        var s = PulsedCw.Resolve(c);
        row.PriText = Us(s.Pri); row.WidthText = Us(s.Width); row.RiseText = Us(s.Rise); row.FallText = Us(s.Fall);
        row.DelayText = c.DelaySeconds is double d ? Us(d) : "";
        row._measureStart = c.MeasureStart; row._measureEnd = c.MeasureEnd;
        return row;
    }

    private static CarrierRowViewModel FromBaseSpec(CarrierSpec c, int index) => new()
    {
        Index = index,
        Standard = c.Standard switch
        {
            CarrierStandard.Nr => "NR", CarrierStandard.Cw => "CW", CarrierStandard.Pcw => "PCW", CarrierStandard.Fsk => "FSK",
            CarrierStandard.Dbpsk => "DBPSK", CarrierStandard.Css => "CSS", CarrierStandard.File => "FILE", _ => "LTE",
        },
        ScsKHz = c.ScsHz / 1e3,
        BandwidthMHz = c.BandwidthHz / 1e6,
        Modulation = CarrierSpec.ModName(c.Modulation),
        OffsetMHz = c.OffsetHz / 1e6,
        PowerDb = c.PowerDb,
        Seed = c.Seed,
        FilePath = c.FilePath ?? "",
        SampleRateMHz = c.SampleRateHz is double fs ? fs / 1e6 : 0,
    };
}

/// <summary>A sample-rate choice: <see cref="Hz"/> = 0 is Auto (the signal's recommended rate); <see cref="IsOwn"/>
/// is the signal's own rate — a custom I/Q file's, or an FSK carrier's native rate — offered while it has one.</summary>
public sealed record SampleRateOption(double Hz, string Label, bool IsOwn = false)
{
    public override string ToString() => Label;
}

/// <summary>One LTE / NR carrier's EVM, as the EVM tab lists it.</summary>
public sealed record EvmRow(int CarrierIndex, string Label, EvmResult Result)
{
    public string RmsText => Result.RmsPercent.ToString(Result.RmsPercent < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " %";
    public string DbText => Result.RmsDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
    public string PeakText => Result.PeakPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %";
    public string LimitText => Result.LimitPercent.ToString("0.#", CultureInfo.InvariantCulture) + " %";
    public string Verdict => Result.Passes ? "PASS" : "FAIL";
    public bool Passes => Result.Passes;
    public override string ToString() => Label;
}

public sealed partial class SignalDesignerViewModel : ObservableObject, IDisposable
{
    private ViewModels.SchematicViewModel? _schematicVm;
    private EditableComponent? _target;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CarrierRowViewModel> Carriers { get; } = new();

    /// <summary>More than one carrier. With one, its Power (dB) means nothing (it is relative to the others, and Pavg
    /// is its power) and it cannot be removed (a source needs a carrier), so both controls are disabled.</summary>
    public bool HasSeveralCarriers => Carriers.Count > 1;

    /// <summary>
    /// The signal is ONE imported I/Q file (Load Custom). The presets, the carriers panel, the record length and the
    /// spectral shaping are then the file's and are disabled; Fc, Pavg, clipping and the sample rate still apply.
    /// Remove custom file is the way back.
    /// </summary>
    public bool IsCustom => Carriers.Count == 1 && Carriers[0].IsFile;
    public bool IsNotCustom => !IsCustom;

    /// <summary>The custom file's carrier, which the Custom I/Q card edits (its sample rate); null otherwise.</summary>
    public CarrierRowViewModel? CustomCarrier => IsCustom ? Carriers[0] : null;

    /// <summary>What the custom file holds, as read from it: samples, rate, record, bandwidth.</summary>
    [ObservableProperty] private string _customInfo = "";

    private void RaiseCustom()
    {
        OnPropertyChanged(nameof(IsCustom)); OnPropertyChanged(nameof(IsNotCustom)); OnPropertyChanged(nameof(CustomCarrier));
        foreach (var n in new[] { nameof(IsPulse), nameof(IsFsk), nameof(IsOwnModulated), nameof(HasTimePlot), nameof(HasEvmTab), nameof(CanEditRecord),
                                  nameof(CanClip), nameof(CanShape), nameof(CanAddCarrier), nameof(PowerLabel), nameof(PulsePowerInfo),
                                  nameof(SecondTabHeader) })
            OnPropertyChanged(n);
        SyncOwnRateOption();
    }

    /// <summary>
    /// The signal is ONE pulsed-CW carrier. Its record is the PRI, it is not clipped or spectrally shaped, and it takes
    /// no other carrier beside it, so Record, Clip, Shaping and Add carrier are disabled; Pavg is then the pulse-top
    /// power, and the preview shows the pulse in time where a modulated signal shows its CCDF.
    /// </summary>
    public bool IsPulse => Carriers.Count == 1 && Carriers[0].IsPcw;

    /// <summary>
    /// The signal is ONE FSK carrier. As for a pulse, its record is its own (one period of its data), it takes no other
    /// carrier beside it, and a constant envelope has nothing to clip or shape: Record, Clip, Shaping and Add carrier
    /// are disabled, and the second tab shows the instantaneous frequency where a modulated signal shows its EVM.
    /// </summary>
    public bool IsFsk => Carriers.Count == 1 && Carriers[0].IsFsk;

    /// <summary>The signal is ONE FSK, DBPSK or CSS carrier: a modulation whose record is its own data (or packet), with
    /// the FSK carrier's rules — Record, Clip, Shaping and Add carrier disabled, the native rate offered, a time plot
    /// in the EVM's place.</summary>
    public bool IsOwnModulated => Carriers.Count == 1 && (Carriers[0].IsFsk || Carriers[0].IsDbpsk || Carriers[0].IsCss);

    /// <summary>The second tab holds a plot against time (a pulse's envelope, an FSK or CSS carrier's frequency, a DBPSK
    /// carrier's waveform), not the EVM.</summary>
    public bool HasTimePlot => IsPulse || IsOwnModulated;
    public bool HasEvmTab => !HasTimePlot;
    public bool CanEditRecord => IsNotCustom && !HasTimePlot;
    public bool CanClip => !HasTimePlot;
    public bool CanShape => IsNotCustom && !HasTimePlot;
    public bool CanAddCarrier => IsNotCustom && !HasTimePlot;
    public string PowerLabel => IsPulse ? "Pulse-top power (dBm)" : "Pavg (dBm)";
    /// <summary>The second preview tab: a pulse or an FSK carrier carries no QAM symbols, so where a modulated signal's
    /// EVM is it shows the pulse, or the frequency.</summary>
    public string SecondTabHeader => IsPulse ? "Pulse"
        : IsOwnModulated && Carriers[0].IsDbpsk ? "Waveform"
        : IsOwnModulated ? "Frequency" : "EVM";

    /// <summary>For a pulse: the record average the top power gives, and the power duty they differ by.</summary>
    public string PulsePowerInfo
    {
        get
        {
            if (!IsPulse || Carriers[0].PulseRefusal is not null) return "";
            var inv = CultureInfo.InvariantCulture;
            double duty = PulsedCw.Resolve(Carriers[0].ToSpec()).PowerDuty, db = 10 * Math.Log10(duty);
            string avg = double.TryParse(PavgText, NumberStyles.Float, inv, out double top)
                ? string.Format(inv, "{0:0.##} dBm", top + db)
                : string.Format(inv, "{0} {1:+0.##;-0.##} dB", PavgText.Trim(), db);
            return string.Format(inv, "Record average {0}   ·   power duty {1:0.##} % ({2:0.##} dB)", avg, 100 * duty, db);
        }
    }

    /// <summary>Fc and Pavg as the user's own TEXT — a number (GHz / dBm) or an expression such as
    /// <c>RFfreq</c> or <c>Pin</c>, which is written back verbatim. A swept source must stay swept.</summary>
    [ObservableProperty] private string _fcText = "2";
    /// <summary>The unit <see cref="FcText"/> is read in: the source's own Fc unit, else the preferred frequency unit
    /// (Settings ▸ General ▸ Units), else GHz. A source placed in MHz is shown and written back in MHz.</summary>
    [ObservableProperty] private string _fcUnit = "GHz";
    [ObservableProperty] private string _pavgText = "10";
    private EditableParameter? _fcOrig, _pavgOrig;
    private string _fcLoaded = "", _pavgLoaded = "";
    [ObservableProperty] private double _recordMs = 1.0;
    [ObservableProperty] private bool _clipEnabled;
    [ObservableProperty] private double _clipPaprDb = 8;
    [ObservableProperty] private bool _shapingEnabled = true;
    [ObservableProperty] private double _shapingFloorDbc = -80;
    [ObservableProperty] private double _presetSpacingMHz = 400;
    /// <summary>
    /// A pulse's CCDF over its on-time only (50 % to 50 %), as a signal analyser gates a burst: the average it is
    /// measured against is then the pulse's own, not the record's. Off, it is over the whole PRI, where the duty cycle
    /// sets the step. A Designer setting: not written to the source.
    /// </summary>
    [ObservableProperty] private bool _gateCcdf = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Edit the carriers, then Preview.";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string _title = "Signal Designer";

    /// <summary>Set by the view: opens a file picker and returns a path, or null.</summary>
    public Func<Task<string?>>? PickIqFileAsync { get; set; }

    /// <summary>Set by the view: opens a save dialog (suggested file name in) and returns the chosen path, or null.</summary>
    public Func<string, Task<string?>>? PickExportPathAsync { get; set; }

    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);
    public PlotContainerViewModel PsdContainer { get; private set; } = null!;
    public PlotContainerViewModel CcdfContainer { get; private set; } = null!;
    public Plot PsdPlot => PsdContainer.PlotVM.Plot;
    public Plot CcdfPlot => CcdfContainer.PlotVM.Plot;
    public PlotContainerViewModel ConstellationContainer { get; private set; } = null!;
    public PlotContainerViewModel EnvelopeContainer { get; private set; } = null!;
    public Plot ConstellationPlot => ConstellationContainer.PlotVM.Plot;
    public Plot EnvelopePlot => EnvelopeContainer.PlotVM.Plot;
    /// <summary>The second tab's plot against time, where a modulated signal's EVM is: a pulsed-CW source's |envelope|
    /// over one PRI, or an FSK source's instantaneous frequency over its first bits.</summary>
    public PlotContainerViewModel TimeContainer { get; private set; } = null!;
    public Plot TimePlot => TimeContainer.PlotVM.Plot;

    /// <summary>0 = spectrum and CCDF, 1 = EVM.</summary>
    [ObservableProperty] private int _previewTab;

    /// <summary>
    /// EVM of every LTE / NR carrier of the last calculation. The EVM tab has no picker of its own: it shows the
    /// carrier selected on the left (<see cref="SelectedCarrier"/>) as <see cref="SelectedEvm"/>, and says which
    /// signal that was in <see cref="EvmSignalText"/>.
    /// </summary>
    public ObservableCollection<EvmRow> EvmRows { get; } = new();
    [ObservableProperty] private EvmRow? _selectedEvm;
    [ObservableProperty] private bool _hasEvm;
    [ObservableProperty] private string _evmNote = "";
    [ObservableProperty] private string _evmSignalText = "";
    [ObservableProperty] private CarrierRowViewModel? _selectedCarrier;
    private ModulatedSignal? _evmSig;
    [ObservableProperty] private string _paprText = "";

    /// <summary>
    /// Standard sample rates (the LTE / NR 1.92 MHz x 2^k ladder, plus 92.16 MS/s for LTE 15 MHz). Auto is the
    /// signal's recommended rate: the smallest of these holding the carriers plus +-2 channels of regrowth.
    /// </summary>
    public static IReadOnlyList<SampleRateOption> SampleRateOptions { get; } =
    [
        new(0, "Auto (recommended)"),
        .. new[] { 7.68, 15.36, 30.72, 61.44, 92.16, 122.88, 245.76, 491.52, 983.04, 1966.08 }
            .Select(m => new SampleRateOption(m * 1e6, m.ToString("0.##", CultureInfo.InvariantCulture) + " MS/s")),
    ];

    /// <summary>
    /// Sample rate of the preview analysis and of Export signal. A Designer setting: it is not written to the
    /// MOD_Source and does not change the simulation, which picks its own time step.
    /// </summary>
    [ObservableProperty] private SampleRateOption _sampleRate = SampleRateOptions[0];

    /// <summary>
    /// What the Sample rate list offers: <see cref="SampleRateOptions"/>, plus — while a custom I/Q file is loaded —
    /// <b>File rate</b>, the file's own sample rate, second, so the file can be previewed and exported without being
    /// resampled; or, for an FSK carrier, <b>Native rate</b>, which a record of 511 bits needs since none of the
    /// standard rates divides it. A stable collection, changed in place (<see cref="SyncOwnRateOption"/>).
    /// </summary>
    public ObservableCollection<SampleRateOption> SampleRates { get; } = new(SampleRateOptions);

    /// <summary>The rate in effect, its sample period and the samples per record, after the last preview.</summary>
    [ObservableProperty] private string _sampleRateInfo = "";

    partial void OnSampleRateChanged(SampleRateOption value)
    {
        // The list drops a File / Native rate entry the moment its signal goes, and a ComboBox clears a selection its items no
        // longer hold, writing null back here: the choice falls back to Auto rather than to nothing.
        if (value is null) { SampleRate = SampleRateOptions[0]; return; }
        SchedulePreview();
    }

    /// <summary>
    /// Keeps the File rate / Native rate entry in step with the signal: added when a custom file is loaded with a rate
    /// or the signal becomes one FSK carrier, replaced when that rate changes, removed with them. A selected one
    /// follows its replacement, or falls back to Auto.
    /// </summary>
    private void SyncOwnRateOption()
    {
        var inv = CultureInfo.InvariantCulture;
        double mhz = IsCustom ? Carriers[0].SampleRateMHz : 0;
        SampleRateOption? want = mhz > 0
            ? new SampleRateOption(mhz * 1e6, "File rate (" + mhz.ToString("0.######", inv) + " MS/s)", IsOwn: true)
            : null;
        if (IsOwnModulated && NativeRateOf(Carriers[0]) is double hz)
            want = new SampleRateOption(hz, "Native rate (" + FormatRate(hz) + ")", IsOwn: true);
        var have = SampleRates.FirstOrDefault(o => o.IsOwn);
        if (have == want) return;
        bool selected = SampleRate?.IsOwn == true;
        if (have is not null) SampleRates.Remove(have);
        if (want is not null) SampleRates.Insert(1, want);
        if (selected) SampleRate = want ?? SampleRateOptions[0];
    }

    /// <summary>An FSK, DBPSK or CSS carrier's native rate, or null when it cannot be generated as it stands.</summary>
    private static double? NativeRateOf(CarrierRowViewModel c)
    {
        if (c.IsFsk && c.FskRefusal is null) return Fsk.NativeRateHz(Fsk.Resolve(c.ToSpec()));
        if (c.IsDbpsk && c.DbpskRefusal is null) return Dbpsk.NativeRateHz(Dbpsk.Resolve(c.ToSpec()));
        if (c.IsCss && c.CssRefusal is null) return Css.NativeRateHz(Css.Resolve(c.ToSpec()));
        return null;
    }

    /// <summary>A sample rate as the list and the line under it read it: 122.88 MS/s, 3.2 kS/s.</summary>
    public static string FormatRate(double hz)
        => hz >= 1e6 ? (hz / 1e6).ToString("0.######", CultureInfo.InvariantCulture) + " MS/s"
                     : (hz / 1e3).ToString("0.######", CultureInfo.InvariantCulture) + " kS/s";

    /// <summary>Most samples a preview or export record may hold at the chosen rate (2^25, 512 MB of I/Q): a 10 s DBPSK
    /// record at a cellular rate would otherwise ask for gigabytes.</summary>
    public const int MaxRecordSamples = 1 << 25;

    /// <summary>
    /// The rate <paramref name="choice"/> gives for <paramref name="sig"/>. Refuses a rate that cannot hold the
    /// signal (below twice its furthest carrier edge) or one that does not give whole samples over the record.
    /// </summary>
    public static double RateFor(ModulatedSignal sig, SampleRateOption choice)
    {
        double rate = choice.Hz > 0 ? choice.Hz : sig.SuggestedCompositeRateHz();
        double edge = sig.Carriers.Max(c => Math.Abs(c.OffsetHz) + c.Spec.BandwidthHz / 2);
        if (rate < 2 * edge)
        {
            var ok = SampleRateOptions.Where(o => o.Hz >= 2 * edge).Select(o => o.Label).FirstOrDefault() ?? "a higher rate";
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "{0:0.##} MS/s cannot hold this signal (carriers reach {1:0.##} MHz from Fc): choose {2} or more.",
                rate / 1e6, edge / 1e6, ok));
        }
        // before LengthAt, whose int a 10 s record at a cellular rate overflows
        double n = Math.Round(sig.RecordSeconds * rate);
        if (n > MaxRecordSamples)
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "{0} gives {1:N0} samples over the {2} record: choose Auto or Native rate.",
                FormatRate(rate), n, PulsedCw.FormatTime(sig.RecordSeconds)));
        sig.LengthAt(rate);                       // throws when the record is not a whole number of samples
        return rate;
    }

    private static string DescribeRate(ModulatedSignal sig, double rate, bool auto)
        => string.Format(CultureInfo.InvariantCulture, "{0}{1}   ·   {2} per sample   ·   {3:N0} samples per record",
            FormatRate(rate), auto ? " (auto)" : "", PulsedCw.FormatTime(1 / rate), sig.LengthAt(rate));

    /// <summary>
    /// Raised after any plot's traces or axes change. The Data Display repaints on its own only when its Plot
    /// object is replaced or the user interacts with it; these four keep their Plot and change its content.
    /// </summary>
    public event EventHandler? PlotsUpdated;

    /// <summary>Where the peak-average arrow stands (time, in the envelope plot's own units) and its length (dB).</summary>
    public double GapArrowX { get; private set; }
    public double GapDb { get; private set; }

    /// <summary>Length of the envelope-power window (µs); editing it re-cuts the plot from the cached signal.</summary>
    [ObservableProperty] private double _envelopeWindowUs = DefaultEnvelopeWindowUs;
    private ModulatedSignal? _envSig;
    private EnvelopeStats _envStats;

    partial void OnEnvelopeWindowUsChanged(double value)
    {
        if (_envSig is not null) ShowEnvelope(EnvelopeChunk(_envSig, _envStats, value * 1e-6));
    }

    partial void OnSelectedEvmChanged(EvmRow? value) => ShowConstellation(value);

    partial void OnSelectedCarrierChanged(CarrierRowViewModel? value)
    {
        foreach (var c in Carriers) c.IsSelected = ReferenceEquals(c, value);
        PickEvm();
    }

    /// <summary>Selects a carrier card on the left; the EVM tab follows it.</summary>
    [RelayCommand]
    private void SelectCarrier(CarrierRowViewModel? row)
    {
        if (row is not null && Carriers.Contains(row)) SelectedCarrier = row;
    }

    /// <summary>Re-generates the signal from the left panel and measures it (the same as Preview).</summary>
    [RelayCommand]
    private Task CalculateEvm() => PreviewAsync();

    public SignalDesignerViewModel()
    {
        PsdContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 640, height: 300);
        CcdfContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 300, width: 640, height: 300);
        ConstellationContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 600, width: 400, height: 400);
        EnvelopeContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 1000, width: 640, height: 300);
        TimeContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 1300, width: 640, height: 300);
        foreach (var p in new[] { PsdPlot, CcdfPlot, ConstellationPlot, EnvelopePlot, TimePlot }) p.Axes.LockedPanning = true;
        foreach (var p in new[] { PsdPlot, CcdfPlot, ConstellationPlot, EnvelopePlot, TimePlot }) Readable(p);
        PlotHost.SelectOnly((PlotContainerViewModel?)null);

        // Live preview: any edit to a carrier or a waveform setting re-previews after a short pause (each
        // new edit cancels the pending one; generation itself is cancellable and cached).
        Carriers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (CarrierRowViewModel r in e.NewItems) r.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null)
                foreach (CarrierRowViewModel r in e.OldItems) r.PropertyChanged -= OnRowChanged;
            if (SelectedCarrier is null || !Carriers.Contains(SelectedCarrier)) SelectedCarrier = Carriers.FirstOrDefault();
            OnPropertyChanged(nameof(HasSeveralCarriers));
            RaiseCustom();
            SchedulePreview();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RecordMs) or nameof(ClipEnabled) or nameof(ClipPaprDb)
                or nameof(ShapingEnabled) or nameof(ShapingFloorDbc) or nameof(GateCcdf))
                SchedulePreview();
            if (e.PropertyName is nameof(PavgText)) OnPropertyChanged(nameof(PulsePowerInfo));
        };
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CarrierRowViewModel.Index) or nameof(CarrierRowViewModel.IsSelected)) return;
        if (e.PropertyName == nameof(CarrierRowViewModel.Standard)) RaiseCustom();
        if (e.PropertyName is nameof(CarrierRowViewModel.SampleRateMHz) or nameof(CarrierRowViewModel.BitRateKbps)
            or nameof(CarrierRowViewModel.DeviationKHz) or nameof(CarrierRowViewModel.DbpskRateBps)
            or nameof(CarrierRowViewModel.Sf) or nameof(CarrierRowViewModel.CssBandwidthKHz)) SyncOwnRateOption();
        if (e.PropertyName is nameof(CarrierRowViewModel.PriS) or nameof(CarrierRowViewModel.WidthS)
            or nameof(CarrierRowViewModel.RiseS) or nameof(CarrierRowViewModel.FallS) or nameof(CarrierRowViewModel.DelayS))
            OnPropertyChanged(nameof(PulsePowerInfo));
        // a derived read-out of the pulse moved, not a value the signal is built from
        if (e.PropertyName is nameof(CarrierRowViewModel.PrfText) or nameof(CarrierRowViewModel.DutyText)
            or nameof(CarrierRowViewModel.DelayInfo) or nameof(CarrierRowViewModel.PulseRefusal)
            or nameof(CarrierRowViewModel.FskRefusal) or nameof(CarrierRowViewModel.DbpskRefusal)
            or nameof(CarrierRowViewModel.CssRefusal) or nameof(CarrierRowViewModel.IsGaussian)
            or nameof(CarrierRowViewModel.IsFixedBits) or nameof(CarrierRowViewModel.DataChoice)
            || e.PropertyName?.EndsWith("Error", StringComparison.Ordinal) == true
            || e.PropertyName?.EndsWith("Text", StringComparison.Ordinal) == true) return;
        SchedulePreview();
    }

    /// <summary>Quiet time between the last edit and the automatic preview.</summary>
    public static TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(400);
    private CancellationTokenSource? _pending;
    private bool _loading;

    private void SchedulePreview()
    {
        if (_loading) return;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = DelayThenPreview(cts.Token);
    }

    private async Task DelayThenPreview(CancellationToken ct)
    {
        try { await Task.Delay(PreviewDelay, ct); }
        catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested) await PreviewAsync();
    }

    // ── load from / write to the component ────────────────────────────────

    /// <summary>A MOD_Source this Designer writes back to. False when opened from Tools ▸ Signal Designer: Apply and
    /// Apply &amp; Close are then hidden, and the signal leaves the window only through Export signal.</summary>
    public bool IsAttached => _target is not null;

    /// <summary>
    /// Tools ▸ Signal Designer: no MOD_Source and no schematic. Opens on one LTE 20 MHz carrier at the preferred
    /// frequency unit, as a newly placed source does; a custom I/Q file is read through the same Load custom, stored
    /// by its full path since there is no workspace to be relative to.
    /// </summary>
    public void SetStandalone(bool preview = true)
    {
        _schematicVm = null;
        _target = null;
        Title = "Signal Designer";
        _loading = true;
        Carriers.Clear();
        Carriers.Add(new CarrierRowViewModel());
        FcUnit = CircuitRF.Ui.Theming.PreferredUnits.Frequency ?? "GHz";
        FcText = (2e9 / UnitScale(FcUnit)).ToString("0.#########", CultureInfo.InvariantCulture);
        PavgText = "10";
        _loading = false;
        OnPropertyChanged(nameof(IsAttached));
        Status = "Design a signal or load an I/Q file, then Export signal.";
        if (preview) _ = PreviewAsync();
    }

    public void SetTarget(ViewModels.SchematicViewModel schematicVm, EditableComponent comp, bool preview = true)
    {
        _schematicVm = schematicVm;
        _target = comp;
        Title = $"Signal Designer — {comp.InstanceName}";
        _loading = true;
        Carriers.Clear();
        var inv = CultureInfo.InvariantCulture;
        int idx = 1;
        foreach (var p in comp.Parameters.Where(p => p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(p => ParseIndex(p.Name)))
        {
            try { Carriers.Add(CarrierRowViewModel.FromSpec(SignalSpec.ParseCarrier(p.Expression.Trim().Trim('"')), idx++)); }
            catch (FormatException ex) { Status = $"{p.Name}: {ex.Message}"; }
        }
        if (Carriers.Count == 0) Carriers.Add(new CarrierRowViewModel());
        EditableParameter? Find(string name) => comp.Parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        double Num(string name, double def, double scale = 1)
        {
            var p = Find(name);
            // the default is in the parameter's own unit, as a stored value is: a missing Record is 1 ms, not 0.001 ms
            if (p is null || !double.TryParse(p.Expression, NumberStyles.Float, inv, out double v)) return def / scale;
            return v * UnitScale(p.Unit) / scale;
        }
        // A number is shown in the field's own unit; anything else (a variable, an expression) verbatim.
        string Text(EditableParameter? p, string def, double fieldScale)
        {
            if (p is null) return def;
            if (double.TryParse(p.Expression, NumberStyles.Float, inv, out double v))
                return (v * UnitScale(p.Unit) / fieldScale).ToString("0.#########", inv);
            return p.Expression.Trim();
        }
        _fcOrig = Find("Fc"); _pavgOrig = Find("Pavg");
        FcUnit = _fcOrig is { } fo && CircuitRF.Ui.ViewModels.FreqUnitHelper.Units.Contains(fo.Unit) ? fo.Unit
               : CircuitRF.Ui.Theming.PreferredUnits.Frequency ?? "GHz";
        FcText = _fcLoaded = Text(_fcOrig, "2", UnitScale(FcUnit));
        PavgText = _pavgLoaded = Text(_pavgOrig, "10", 1);
        RecordMs = Num("Record", 1e-3, 1e-3);
        var clip = comp.Parameters.FirstOrDefault(x => x.Name.Equals("Clip", StringComparison.OrdinalIgnoreCase));
        ClipEnabled = clip is not null;
        if (clip is not null && double.TryParse(clip.Expression, NumberStyles.Float, inv, out double cv)) ClipPaprDb = cv;
        var sh = comp.Parameters.FirstOrDefault(x => x.Name.Equals("Shaping", StringComparison.OrdinalIgnoreCase));
        ShapingEnabled = sh is null || !sh.Expression.Trim('"').Equals("off", StringComparison.OrdinalIgnoreCase);
        if (sh is not null && double.TryParse(sh.Expression, NumberStyles.Float, inv, out double sv)) ShapingFloorDbc = sv;
        _loading = false;
        OnPropertyChanged(nameof(IsAttached));
        if (preview) _ = PreviewAsync();
    }

    /// <summary>
    /// Shows an already generated <paramref name="sig"/> synchronously — plots, numbers and status — for a
    /// caller that cannot await the dispatcher (the documentation's figure capture runs ON it).
    /// </summary>
    public void ShowPreview(ModulatedSignal sig)
    {
        double rate = RateFor(sig, SampleRate);
        var (ds, text, env) = AnalyseWithEnvelope(sig, rate, GateCcdf);
        SampleRateInfo = DescribeRate(sig, rate, SampleRate.Hz <= 0);
        ShowPlots(ds, sig);
        _envSig = sig; _envStats = env;
        ShowEnvelope(EnvelopeChunk(sig, env, EnvelopeWindowUs * 1e-6));
        _evmSig = sig;
        ShowEvm(AnalyseEvm(sig));
        Stats = text;
        CustomInfo = DescribeCustom(sig);
        Status = "Ready. Generated in the background and cached — the simulation reuses it.";
    }

    private static int ParseIndex(string name)
    {
        int a = name.IndexOf('['), b = name.IndexOf(']');
        return a >= 0 && b > a && int.TryParse(name[(a + 1)..b], out int i) ? i : 0;
    }

    private static double UnitScale(string unit) => unit switch
    {
        "GHz" => 1e9, "MHz" => 1e6, "kHz" => 1e3, "ms" => 1e-3, "us" or "µs" => 1e-6, _ => 1,
    };

    public SignalSpec BuildSpec() => new()
    {
        Carriers = Carriers.Select(c => c.ToSpec()).Select(c => c.FilePath is { Length: > 0 } f ? c with { FilePath = ResolveIqPath(f) } : c).ToList(),
        RecordSeconds = Math.Max(0.5, RecordMs) * 1e-3,
        ClipPaprDb = ClipEnabled && CanClip ? ClipPaprDb : null,
        ShapingFloorDbc = ShapingEnabled ? ShapingFloorDbc : null,
    };

    [RelayCommand]
    private void Apply()
    {
        if (_schematicVm is null || _target is null) return;
        var inv = CultureInfo.InvariantCulture;
        var keep = _target.Parameters
            .Where(p => !p.Name.StartsWith("Carrier[", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Equals("Record", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Equals("Clip", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Equals("Shaping", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Equals("Fc", StringComparison.OrdinalIgnoreCase)
                        && !p.Name.Equals("Pavg", StringComparison.OrdinalIgnoreCase))
            .Select(Clone).ToList();
        // Unchanged Fc / Pavg go back exactly as they came (expression AND unit); an edited number takes the
        // field's unit; an edited expression keeps the parameter's own unit, as the inspector would.
        EditableParameter Field(string name, string text, string loaded, EditableParameter? orig, string unit, UnitDimension dim)
        {
            if (orig is not null && text.Trim() == loaded) return Clone(orig);
            bool numeric = double.TryParse(text, NumberStyles.Float, inv, out _);
            return new EditableParameter
            {
                Name = name, Expression = text.Trim(), Unit = numeric ? unit : orig?.Unit ?? "",
                ShowOnSchematic = orig?.ShowOnSchematic ?? true, Dimension = dim,
            };
        }
        var updated = new List<EditableParameter>
        {
            Field("Fc", FcText, _fcLoaded, _fcOrig, FcUnit, UnitDimension.Frequency),
            Field("Pavg", PavgText, _pavgLoaded, _pavgOrig, "dBm", UnitDimension.Power),
        };
        updated.AddRange(keep);
        for (int i = 0; i < Carriers.Count; i++)
            updated.Add(new EditableParameter
            {
                Name = $"Carrier[{i + 1}]",
                Expression = "\"" + SignalSpec.FormatCarrier(Carriers[i].ToSpec()) + "\"",
                ShowOnSchematic = true,
                Dimension = UnitDimension.None,
            });
        updated.Add(new EditableParameter { Name = "Record", Expression = (Math.Max(0.5, RecordMs) * 1e-3).ToString("R", inv), ShowOnSchematic = false, Dimension = UnitDimension.None });
        if (ClipEnabled && CanClip)
            updated.Add(new EditableParameter { Name = "Clip", Expression = ClipPaprDb.ToString("R", inv), ShowOnSchematic = false, Dimension = UnitDimension.None });
        updated.Add(new EditableParameter
        {
            Name = "Shaping", Expression = ShapingEnabled ? ShapingFloorDbc.ToString("R", inv) : "off",
            ShowOnSchematic = false, Dimension = UnitDimension.None,
        });
        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, updated));
        Status = "Applied to " + _target.InstanceName + ".";
    }

    private static EditableParameter Clone(EditableParameter p) => new()
    {
        Name = p.Name, Expression = p.Expression, Unit = p.Unit, ShowOnSchematic = p.ShowOnSchematic, Dimension = p.Dimension,
    };

    /// <summary>The absolute path a stored I/Q reference names — the rule the elaborator resolves it by at Run
    /// (<see cref="SnpPathPolicy"/>), so the preview and the simulation read the same file and share one cache entry.</summary>
    private string ResolveIqPath(string stored)
        => SnpPathPolicy.Resolve(stored, _schematicVm?.WorkspaceRoot, _schematicVm?.EditModel.SchematicDirectory) ?? stored;

    /// <summary>How a picked I/Q file is stored in the <c>.csch</c>: workspace-relative when it can be, as an SnP's
    /// Touchstone is, so the design stays portable.</summary>
    private string StoreIqPath(string absolute)
        => SnpPathPolicy.ToStored(absolute, _schematicVm?.WorkspaceRoot, _schematicVm?.EditModel.SchematicDirectory);

    // ── carriers and presets ──────────────────────────────────────────────

    [RelayCommand]
    private void AddCarrier()
    {
        var last = Carriers.LastOrDefault();
        var c = last is null ? new CarrierRowViewModel() : CarrierRowViewModel.FromSpec(last.ToSpec() with { OffsetHz = last.OffsetMHz * 1e6 + last.BandwidthMHz * 1e6, Seed = last.Seed + 1 }, Carriers.Count + 1);
        Carriers.Add(c);
        Renumber();
    }

    [RelayCommand]
    private void RemoveCarrier(CarrierRowViewModel? row)
    {
        if (row is null || Carriers.Count <= 1) return;
        Carriers.Remove(row);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Carriers.Count; i++) Carriers[i].Index = i + 1;
    }

    private void Preset(params CarrierSpec[] cs)
    {
        _loading = true;
        Carriers.Clear();
        for (int i = 0; i < cs.Length; i++) Carriers.Add(CarrierRowViewModel.FromSpec(cs[i], i + 1));
        _loading = false;
        _pending?.Cancel();
        _ = PreviewAsync();
    }

    /// <summary>
    /// A standard's one carrier, with Centre Fc moved to its channel — unless Fc is an expression, which stays as typed
    /// (a swept source stays swept).
    /// </summary>
    private void PresetAt(double fcHz, CarrierSpec c)
    {
        var inv = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(FcText) || double.TryParse(FcText, NumberStyles.Float, inv, out _))
            FcText = (fcHz / UnitScale(FcUnit)).ToString("0.#########", inv);
        Preset(c);
    }

    /// <summary>
    /// Wireless M-Bus (EN 13757-4) as a metering front end is tested with it: one FSK carrier on PN9 at the mode's chip
    /// rate and deviation, at the mode's channel.
    /// </summary>
    private void PresetFsk(double fcHz, double rateBps, double devHz, double? bt)
        => PresetAt(fcHz, new CarrierSpec { Standard = CarrierStandard.Fsk, BitRateBps = rateBps, DeviationHz = devHz, GaussianBt = bt, Data = "PN9" });

    /// <summary>C1, meter to other: 868.95 MHz, 100 kchip/s, ±45 kHz, 2-FSK with no Gaussian filter.</summary>
    [RelayCommand] private void PresetWmbusC1() => PresetFsk(868.95e6, 100e3, 45e3, null);
    /// <summary>C2, other to meter: 869.525 MHz, 50 kchip/s, ±25 kHz, GFSK BT 0.5.</summary>
    [RelayCommand] private void PresetWmbusC2() => PresetFsk(869.525e6, 50e3, 25e3, 0.5);
    /// <summary>T1, meter to other: 868.95 MHz, 100 kchip/s, ±50 kHz, FSK.</summary>
    [RelayCommand] private void PresetWmbusT1() => PresetFsk(868.95e6, 100e3, 50e3, null);
    /// <summary>T2, other to meter: 868.3 MHz, 32.768 kchip/s, ±50 kHz, FSK.</summary>
    [RelayCommand] private void PresetWmbusT2() => PresetFsk(868.3e6, 32.768e3, 50e3, null);

    /// <summary>Bluetooth LE 1M at 2.44 GHz (mid-band): 1 Msym/s GFSK, h = 0.5 (±250 kHz), BT 0.5. LE Coded (125 and
    /// 500 kb/s) is the same air interface: its coding is in the bits, not in the RF.</summary>
    [RelayCommand] private void PresetBle1M() => PresetFsk(2.44e9, 1e6, 250e3, 0.5);
    /// <summary>Bluetooth LE 2M at 2.44 GHz: 2 Msym/s GFSK, h = 0.5 (±500 kHz), BT 0.5.</summary>
    [RelayCommand] private void PresetBle2M() => PresetFsk(2.44e9, 2e6, 500e3, 0.5);

    /// <summary>The Sigfox RC1 uplink, an ultra-narrowband (UNB) uplink in the 868 MHz band: 100 b/s DBPSK on PN9 at
    /// 868.13 MHz.</summary>
    [RelayCommand]
    private void PresetSigfox()
        => PresetAt(868.13e6, new CarrierSpec { Standard = CarrierStandard.Dbpsk, BitRateBps = 100, Data = "PN9" });

    /// <summary>A LoRaWAN packet (LoRa: chirp spread spectrum) on an EU868 channel: 868.1 MHz, 125 kHz, at
    /// <paramref name="sf"/> (7 is the fastest, 12 the longest reach).</summary>
    private void PresetLoRaWan(int sf)
        => PresetAt(868.1e6, new CarrierSpec { Standard = CarrierStandard.Css, SpreadingFactor = sf, BandwidthHz = 125e3 });

    [RelayCommand] private void PresetLoRaWanSf7() => PresetLoRaWan(7);
    [RelayCommand] private void PresetLoRaWanSf12() => PresetLoRaWan(12);

    /// <summary>One pulsed-CW carrier at the card's defaults, for a pulsed-radar amplifier.</summary>
    [RelayCommand] private void PresetPulse() => Preset(new CarrierSpec { Standard = CarrierStandard.Pcw });
    [RelayCommand] private void PresetLte20() => Preset(new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6, Modulation = QamOrder.Qam64 });
    [RelayCommand] private void PresetNr20() => Preset(new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam64 });
    [RelayCommand] private void PresetNr100() => Preset(new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 100e6, ScsHz = 30e3, Modulation = QamOrder.Qam256 });

    [RelayCommand]
    private void PresetTwoCarrier()
    {
        double half = PresetSpacingMHz * 1e6 / 2;
        Preset(new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, OffsetHz = -half, Seed = 1 },
               new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, OffsetHz = +half, Seed = 2 });
    }

    /// <summary>
    /// Load Custom: the signal becomes one I/Q file, at Fc, with the sample rate from its header when it has one.
    /// A file without one previews only once the rate is entered on the Custom I/Q card.
    /// </summary>
    [RelayCommand]
    private async Task LoadCustom()
    {
        if (PickIqFileAsync is null) return;
        var path = await PickIqFileAsync();
        if (path is not null) LoadCustomFile(path);
    }

    /// <summary>The carriers on the left before Load Custom replaced them: Remove custom file puts them back.</summary>
    private List<CarrierSpec>? _beforeCustom;

    public void LoadCustomFile(string path)
    {
        double fs;
        try { fs = IqFile.ReadHeader(path).SampleRateHz; }
        catch (Exception ex) { Status = "Cannot read the file: " + ex.Message; return; }
        if (!IsCustom) _beforeCustom = Carriers.Select(c => c.ToSpec()).ToList();
        Preset(new CarrierSpec
        {
            Standard = CarrierStandard.File, FilePath = StoreIqPath(path), SampleRateHz = fs > 0 ? fs : null,
        });
        if (fs <= 0) Status = "The file states no sample rate: enter it on the Custom I/Q card.";
    }

    /// <summary>
    /// Drops the custom file: the signal goes back to the carriers it had before Load Custom (one LTE 20 MHz carrier
    /// when the source was opened on a file), and every control the file disabled is enabled again. Apply writes
    /// it, removing the file's reference from the schematic.
    /// </summary>
    [RelayCommand]
    private void RemoveCustom()
    {
        if (!IsCustom) return;
        var back = _beforeCustom is { Count: > 0 } b ? b.ToArray()
            : [new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6, Modulation = QamOrder.Qam64 }];
        _beforeCustom = null;
        Preset(back);
    }

    [RelayCommand]
    private void PresetFourCarrierLte()
        => Preset(Enumerable.Range(0, 4).Select(i => new CarrierSpec
        {
            Standard = CarrierStandard.Lte, BandwidthHz = 20e6, OffsetHz = (i - 1.5) * 20e6, Seed = i + 1,
        }).ToArray());

    /// <summary>Asks where to save, then writes the signal on the left as a two-column I/Q text file.</summary>
    [RelayCommand]
    private async Task ExportSignal()
    {
        if (PickExportPathAsync is null) return;
        string name = (_target?.InstanceName is { Length: > 0 } n ? n : "signal") + "_iq.txt";
        var path = await PickExportPathAsync(name);
        if (path is null) return;
        await ExportSignalAsync(path);
    }

    /// <summary>
    /// Writes the signal defined on the left (the composite of every carrier, each at its offset, around Fc) to
    /// <paramref name="path"/> in the I/Q text format a FILE carrier imports: a header saying what it is, the sample
    /// rate, then one <c>I Q</c> line per sample over one whole record, unit mean power.
    /// </summary>
    public async Task<bool> ExportSignalAsync(string path)
    {
        SignalSpec spec;
        try { spec = BuildSpec(); }
        catch (Exception ex) { Status = ex.Message; return false; }
        IsBusy = true; Progress = 0; Status = "Exporting…";
        var cts = new CancellationTokenSource();
        try
        {
            var sig = await SignalCache.GetAsync(spec, null, cts.Token);
            var inv = CultureInfo.InvariantCulture;
            double rate = RateFor(sig, SampleRate);
            int n = sig.LengthAt(rate);
            var comments = new List<string>
            {
                "circuitRF Signal Designer export: complex baseband I/Q around Fc, one periodic record",
                string.Format(inv, "SAMPLE RATE {0:0.######} MS/s ({1} Hz), sample period {2:0.######} ns{3}",
                    rate / 1e6, rate.ToString("R", inv), 1e9 / rate, SampleRate.Hz > 0 ? "" : " (auto, recommended)"),
                string.Format(inv, "{0} samples = {1:0.######} ms record (periodic: the last sample wraps to the first)",
                    n, sig.RecordSeconds * 1e3),
                "Fc = " + FcText + " " + FcUnit + " (the I/Q is relative to it),  Pavg = " + PavgText + " dBm",
                SignalComposer.PulseOf(spec) is not null
                    ? "scale: unit power on the pulse top (|I + jQ| is 1 there); Pavg sets the pulse-top power"
                    : "scale: unit mean power (|I + jQ|^2 averages 1); set the level with the source's Pavg",
                string.Format(inv, "PAPR {0:0.00} dB at 0.01 %,  clipping {1},  shaping {2}",
                    SignalMeasure.PaprDb(sig.CompositeWindow(rate, 0, 0, Math.Min(n, 1 << 22)), 1e-4),
                    spec.ClipPaprDb is double c ? c.ToString("0.#", inv) + " dB PAPR" : "off",
                    spec.ShapingFloorDbc is double f ? f.ToString("0", inv) + " dBc" : "off"),
            };
            for (int i = 0; i < sig.Carriers.Count; i++)
                comments.Add(string.Format(inv, "carrier C{0}: {1}   ({2})", i + 1, DescribeEvmSignal(sig, i + 1).Split(":  ", 2)[1],
                    SignalSpec.FormatCarrier(sig.Carriers[i].Spec)));
            double bw = sig.Carriers.Count == 1 ? sig.Carriers[0].Spec.BandwidthHz : sig.InstantaneousBandwidthHz;
            var prog = new Progress<double>(v => Progress = v);
            await Task.Run(() =>
            {
                var x = sig.Composite(rate);
                IqFile.Write(path, x, rate, bw, comments, prog, cts.Token);
            }, cts.Token);
            Status = $"Exported {System.IO.Path.GetFileName(path)} ({sig.LengthAt(rate):N0} samples at {rate / 1e6:0.##} MS/s).";
            return true;
        }
        catch (Exception ex)
        {
            Status = "Cannot export: " + ex.Message;
            return false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task BrowseFile(CarrierRowViewModel? row)
    {
        if (row is null || PickIqFileAsync is null) return;
        var path = await PickIqFileAsync();
        if (path is not null) row.FilePath = StoreIqPath(path);
    }

    // ── preview ───────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Preview() => PreviewAsync();

    public async Task PreviewAsync()
    {
        // An explicit run (Calculate, Preview, a preset) supersedes a scheduled one. Without this, an edit's
        // auto-preview firing 400 ms later cancelled a Calculate still generating a new signal, which then
        // returned with the OLD signal's EVM and constellation on screen.
        _pending?.Cancel();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        SignalSpec spec;
        try { spec = BuildSpec(); }
        catch (Exception ex) { Status = ex.Message; return; }
        IsBusy = true; Progress = 0; Status = "Generating…";
        var sw = Stopwatch.StartNew();
        // A signal that is not cached yet takes seconds: it reports on a LIVE row of the Messages pane, the
        // application's one progress surface for background work, as well as in this window's footer.
        string what = string.Join(" + ", spec.Carriers.Select(c => c.Label));
        var live = SignalCache.IsCached(spec) ? null : _schematicVm?.MessageSink?.BeginProgress($"Signal Designer — generating {what}");
        try
        {
            var prog = new Progress<double>(v =>
            {
                Progress = v;
                live?.Update($"Signal Designer — generating {what}", $"{v * 100:0} %", v * 100);
            });
            var sig = await SignalCache.GetAsync(spec, prog, cts.Token);
            var choice = SampleRate;
            double rate = RateFor(sig, choice);
            bool gate = GateCcdf;
            var (ds, text, env) = await Task.Run(() => AnalyseWithEnvelope(sig, rate, gate), cts.Token);
            double window = EnvelopeWindowUs * 1e-6;
            var chunk = await Task.Run(() => EnvelopeChunk(sig, env, window), cts.Token);
            var evm = await Task.Run(() => AnalyseEvm(sig), cts.Token);
            if (cts.IsCancellationRequested) { live?.Finish(Messages.MessageLevel.Info, "cancelled", keepBar: false); return; }
            SampleRateInfo = DescribeRate(sig, rate, choice.Hz <= 0);
            ShowPlots(ds, sig);
            _envSig = sig; _envStats = env;
            ShowEnvelope(chunk);
            _evmSig = sig;
            ShowEvm(evm);
            Stats = text;
            CustomInfo = DescribeCustom(sig);
            Status = IsAttached
                ? $"Ready ({sw.Elapsed.TotalSeconds:0.0} s). Generated in the background and cached — the simulation reuses it."
                : $"Ready ({sw.Elapsed.TotalSeconds:0.0} s).";
            live?.Finish(Messages.MessageLevel.Success, $"ready in {sw.Elapsed.TotalSeconds:0.0} s", keepBar: false);
        }
        catch (OperationCanceledException) { live?.Finish(Messages.MessageLevel.Info, "cancelled", keepBar: false); }
        catch (Exception ex)
        {
            Status = "Cannot generate: " + ex.Message;
            live?.Finish(Messages.MessageLevel.Error, ex.Message, keepBar: false);
        }
        finally { if (ReferenceEquals(_cts, cts)) IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _pending?.Cancel();
        _cts?.Cancel();
        Status = "Cancelled.";
    }

    /// <summary>The Custom I/Q card's reading of a one-file signal: samples, rate, record and bandwidth.</summary>
    public static string DescribeCustom(ModulatedSignal sig)
    {
        if (sig.Carriers.Count != 1 || sig.Carriers[0].Spec.Standard != CarrierStandard.File) return "";
        var c = sig.Carriers[0];
        return string.Format(CultureInfo.InvariantCulture,
            "{0:N0} samples at {1:0.######} MS/s = {2:0.######} ms record   ·   channel bandwidth {3:0.###} MHz",
            c.Native.Length, c.NativeRateHz / 1e6, sig.RecordSeconds * 1e3, c.Spec.BandwidthHz / 1e6);
    }

    /// <summary>Composite spectrum, CCDF and per-carrier ideal ACLR of a signal (pure computation, any thread).</summary>
    public static (DataSet ds, string text) Analyse(ModulatedSignal sig, bool gate = true)
    {
        var (ds, text, _) = AnalyseWithEnvelope(sig, 0, gate);
        return (ds, text);
    }

    /// <summary><see cref="Analyse"/>, plus where the record peaks and its mean power for the EVM tab's envelope plot.
    /// <paramref name="gate"/> takes a pulse's CCDF over its on-time only (<see cref="GateCcdf"/>).</summary>
    public static (DataSet ds, string text, EnvelopeStats env) AnalyseWithEnvelope(ModulatedSignal sig, double rateHz = 0, bool gate = true)
    {
        double rate = rateHz > 0 ? rateHz : sig.SuggestedCompositeRateHz();
        int n = sig.LengthAt(rate);
        // keep the preview fast: at most ~4 M samples of the record. A pulse's record is one PRI, and a fixed FSK pattern's
        // a few bits, often shorter than one Welch segment: the window runs over several (the record is periodic), so the
        // spectrum is the TRAIN's.
        bool ownRecord = SignalComposer.HasOwnRecord(sig.Spec);
        int count = Math.Min(ownRecord ? Math.Max(n, 1 << 16) : n, 1 << 22);
        var x = sig.CompositeWindow(rate, 0, 0, count);
        var (f, p) = SignalMeasure.Welch(x, rate, 8192);
        double pk = p.Max();
        // PSD against the OFFSET from Fc in MHz (Fc may be an expression — the shape does not depend on it),
        // cropped to the occupied span plus two and a half channels each side so a 20 MHz carrier fills the
        // plot instead of being a line on a sample-rate-wide axis; floor at -120 dB re peak.
        double lo = sig.Carriers.Min(c => c.OffsetHz - 0.5 * c.Spec.BandwidthHz);
        double hi = sig.Carriers.Max(c => c.OffsetHz + 0.5 * c.Spec.BandwidthHz);
        // at least 5 MHz each side for the cellular carriers; an FSK, DBPSK or CSS carrier is framed on its own, a few
        // hundred kHz (FSK, CSS) or a few hundred Hz (DBPSK) — which reads in Hz rather than in millionths of a MHz
        double margin = 2.5 * sig.Carriers.Max(c => c.Spec.BandwidthHz);
        if (!ownRecord || SignalComposer.PulseOf(sig.Spec) is not null) margin = Math.Max(margin, 5e6);
        var keep = Enumerable.Range(0, f.Length).Where(i => f[i] >= lo - margin && f[i] <= hi + margin).ToArray();
        if (keep.Length < 2) keep = Enumerable.Range(0, f.Length).ToArray();
        var psd = keep.Select(i => Math.Max(10 * Math.Log10(Math.Max(p[i], 1e-300) / pk), -120)).ToArray();
        var ds = new DataSet();
        double fScale = PsdInHz(sig) ? 1 : 1e6;
        ds.Add("PSD", new DataCube([new Axis("offset from Fc", keep.Select(i => f[i] / fScale).ToArray(), "")], psd));
        // A gated pulse is measured over its on-time, 50 % to 50 %, against its own average, as an analyser gates a
        // burst; ungated, the off-time is in the average and the duty cycle sets the step.
        var pulseShape = SignalComposer.PulseOf(sig.Spec);
        Complex[]? onTime = null;
        if (pulseShape is { } ps && gate)
        {
            double t0 = ps.Delay + ps.RiseSpan / 2, t1 = t0 + ps.Width;
            onTime = [.. x.Where((_, i) => (i % n) / rate is var t && t >= t0 && t < t1)];
        }
        var (db, pr) = SignalMeasure.Ccdf(onTime ?? x, 160, 1e-5);   // the floor the Gaussian reference below runs to
        // The standard CCDF: probability (%) that the instantaneous power exceeds the average by more than
        // x dB, on a log probability axis, from 0 dB — plus, for a noise-like signal, the Gaussian-noise (Rayleigh
        // envelope) reference.
        var order = Enumerable.Range(0, db.Length).Where(i => db[i] >= 0 && pr[i] > 0).OrderBy(i => db[i]).ToArray();
        var dbAx = order.Select(i => db[i]).ToArray();
        for (int i = 1; i < dbAx.Length; i++) if (dbAx[i] <= dbAx[i - 1]) dbAx[i] = dbAx[i - 1] + 1e-9;
        var ccdfAxis = new Axis("power above average", dbAx, "dB");
        ds.Add("CCDF", new DataCube([ccdfAxis], order.Select(i => 100 * pr[i]).ToArray()));
        // The reference is the SAME curve whatever the signal: on its own axis, from 0 dB down to the probability the
        // CCDF is measured to (1e-5, ~10.6 dB). On the signal's axis it was cut where the signal ends, so a clipped
        // signal took the reference with it, the autoscale stretched both across the plot, and clipping looked like
        // no change at all.
        // Only beside a noise-like signal: OFDM, an I/Q file, several carriers. A pulse, FSK, DBPSK or CSS signal is no
        // kind of noise, and a reference it can never approach says nothing about it.
        if (!ownRecord)
        {
            const double ccdfFloor = 1e-5;
            double gaussTop = 10 * Math.Log10(-Math.Log(ccdfFloor));
            var gaussDb = Enumerable.Range(0, 213).Select(i => gaussTop * i / 212).ToArray();
            ds.Add("Gaussian", new DataCube([new Axis("power above average", gaussDb, "dB")],
                                            gaussDb.Select(d => 100 * Math.Exp(-Math.Pow(10, d / 10))).ToArray()));
        }

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (pulseShape is { } pulse)
        {
            // The pulse in time over one PRI, from its own native record (exact 0 and 1, no resampling ripple).
            var nat = sig.Carriers[0];
            double us = 1e6 / nat.NativeRateHz;
            ds.Add("Pulse", new DataCube([new Axis("time", [.. Enumerable.Range(0, nat.Native.Length).Select(i => i * us)], "us")],
                                         [.. nat.Native.Select(v => v.Magnitude)]));
            sb.AppendLine(string.Format(inv, "pCW   PRI {0}   PRF {1:0.###} kHz   PW {2}   duty {3:0.##} %   power duty {4:0.##} %",
                PulsedCw.FormatTime(pulse.Pri), pulse.Prf / 1e3, PulsedCw.FormatTime(pulse.Width), 100 * pulse.Duty, 100 * pulse.PowerDuty));
            sb.AppendLine(string.Format(inv, "rise {0}   fall {1}   delay {2}   bandwidth {3:0.###} MHz   rate {4:0.##} MS/s",
                PulsedCw.FormatTime(pulse.Rise), PulsedCw.FormatTime(pulse.Fall), PulsedCw.FormatTime(pulse.Delay),
                nat.Spec.BandwidthHz / 1e6, rate / 1e6));
            if (onTime is not null)
                sb.AppendLine(string.Format(inv, "on-time PAPR {0:0.00} dB at 0.01 %   peak {1:0.00} dB   whole PRI {2:0.00} dB (1 / power duty)",
                    SignalMeasure.PaprDb(onTime, 1e-4), SignalMeasure.PaprDb(onTime, 0), -10 * Math.Log10(pulse.PowerDuty)));
            else
                sb.AppendLine(string.Format(inv, "PAPR {0:0.00} dB at 0.01 %   peak {1:0.00} dB   (1 / power duty: {2:0.00} dB)",
                    SignalMeasure.PaprDb(x, 1e-4), SignalMeasure.PaprDb(x, 0), -10 * Math.Log10(pulse.PowerDuty)));
            return (ds, sb.ToString().TrimEnd(), MeasureEnvelope(x, rate));
        }
        if (SignalComposer.FskOf(sig.Spec) is { } fsk)
        {
            // The instantaneous frequency over the first bits, from the phase steps of the native record — what is
            // played, the closing ramp included — at most 64 bits, so a bit's shape and the filter's rounding show.
            var nat = sig.Carriers[0];
            int shown = Math.Min(nat.Native.Length, (int)Math.Round(Math.Min(fsk.Bits, 64) * nat.NativeRateHz / fsk.BitRate));
            double us = 1e6 / nat.NativeRateHz, toKHz = nat.NativeRateHz / (2 * Math.PI) / 1e3;
            ds.Add("Frequency", new DataCube([new Axis("time", [.. Enumerable.Range(0, shown).Select(i => i * us)], "us")],
                [.. Enumerable.Range(0, shown).Select(i => (nat.Native[(i + 1) % nat.Native.Length] * Complex.Conjugate(nat.Native[i])).Phase * toKHz)]));
            sb.AppendLine(string.Format(inv, "{0}   Rb {1}   Dev ±{2}   h {3:0.###}   {4}",
                fsk.Kind, Fsk.FormatBitRate(fsk.BitRate), Fsk.FormatHz(fsk.Deviation), fsk.ModulationIndex,
                fsk.Bt > 0 ? "BT " + fsk.Bt.ToString("0.###", inv) : "no filter"));
            sb.AppendLine(string.Format(inv, "data {0}, {1:N0} bits   record {2}   Carson bandwidth {3}",
                fsk.Data is "PN9" or "PN15" ? fsk.Data : "pattern", fsk.Bits, PulsedCw.FormatTime(fsk.RecordSeconds),
                Fsk.FormatHz(nat.Spec.BandwidthHz)));
        }
        if (SignalComposer.DbpskOf(sig.Spec) is { } dbpsk)
        {
            // I against time over the first 32 bits (DBPSK is real): flat between reversals, through zero at each one
            var nat = sig.Carriers[0];
            int shown = Math.Min(nat.Native.Length, 32 * Dbpsk.SamplesPerBit);
            double ms = 1e3 / nat.NativeRateHz, peak = nat.Native.Max(v => Math.Abs(v.Real));
            ds.Add("Waveform", new DataCube([new Axis("time", [.. Enumerable.Range(0, shown).Select(i => i * ms)], "ms")],
                [.. Enumerable.Range(0, shown).Select(i => nat.Native[i].Real / peak)]));
            sb.AppendLine(string.Format(inv, "DBPSK   Rb {0}   bit {1}   data {2}, {3:N0} bits   record {4}",
                Fsk.FormatBitRate(dbpsk.BitRate), PulsedCw.FormatTime(1 / dbpsk.BitRate),
                dbpsk.Data is "PN9" or "PN15" ? dbpsk.Data : "pattern", dbpsk.Bits, PulsedCw.FormatTime(dbpsk.RecordSeconds)));
            sb.AppendLine(string.Format(inv, "raised-cosine reversals   bandwidth {0} (main lobe)", Fsk.FormatHz(nat.Spec.BandwidthHz)));
        }
        if (SignalComposer.CssOf(sig.Spec) is { } css)
        {
            // the instantaneous frequency over the preamble, sync, start of frame and the first payload symbols, at most
            // ~8,000 points however long a symbol is
            var nat = sig.Carriers[0];
            int shown = Math.Min(nat.Native.Length, (int)Math.Round((Css.PreambleSymbols + 2 + 2.25 + 4) * css.Chips * Css.SamplesPerChip));
            int step = Math.Max(1, shown / 8000);
            double ms = 1e3 / nat.NativeRateHz, toKHz = nat.NativeRateHz / (2 * Math.PI) / 1e3;
            var idx = Enumerable.Range(0, (shown + step - 1) / step).Select(k => k * step).ToArray();
            ds.Add("Frequency", new DataCube([new Axis("time", [.. idx.Select(i => i * ms)], "ms")],
                [.. idx.Select(i => (nat.Native[(i + 1) % nat.Native.Length] * Complex.Conjugate(nat.Native[i])).Phase * toKHz)]));
            sb.AppendLine(string.Format(inv, "CSS   SF {0}   BW {1}   symbol {2}   raw {3:0.###} kb/s",
                css.Sf, Fsk.FormatHz(css.BandwidthHz), PulsedCw.FormatTime(css.SymbolSeconds), css.RawBitRate / 1e3));
            sb.AppendLine(string.Format(inv, "packet: {0} preamble + 2 sync + 2.25 SFD + {1} payload symbols   record {2}",
                Css.PreambleSymbols, Css.PayloadSymbols, PulsedCw.FormatTime(css.RecordSeconds)));
        }
        sb.AppendLine(string.Format(inv, "{0} carrier(s)   IBW {1}   record {2:0.###e0} s   rate {3}",
            sig.Carriers.Count, Fsk.FormatHz(sig.InstantaneousBandwidthHz), sig.RecordSeconds, FormatRate(rate)));
        sb.AppendLine(string.Format(inv, "PAPR {0:0.00} dB at 0.01 %   peak {1:0.00} dB", SignalMeasure.PaprDb(x, 1e-4), SignalMeasure.PaprDb(x, 0)));
        foreach (var c in sig.Carriers)
        {
            if (c.Spec.Standard == CarrierStandard.Cw) continue;
            var (mb, sp) = SignalMeasure.AclrBands(c.Spec);
            var a = SignalMeasure.Aclr(f, p, rate, c.OffsetHz, mb, sp);
            sb.AppendLine(string.Format(inv, "{0,-24} {1,6:+0.##;-0.##} MHz  ACPR1 {2:0.0}/{3:0.0}  ACPR2 {4:0.0}/{5:0.0} dBc",
                c.Spec.Label, c.OffsetHz / 1e6, a.Acpr1L, a.Acpr1U, a.Acpr2L, a.Acpr2U));
        }
        return (ds, sb.ToString().TrimEnd(), MeasureEnvelope(x, rate));
    }

    /// <summary>Default length of the EVM tab's envelope-power window; the field beside the plot changes it.</summary>
    public const double DefaultEnvelopeWindowUs = 2;

    /// <summary>Most points in that window: it is re-interpolated finely for a short window, capped for a long one.</summary>
    public const int EnvelopeWindowPoints = 20_000;

    /// <summary>Samples per 1 / IBW when the cap allows, so a short window draws as a smooth curve.</summary>
    public const int SamplesPerCycle = 64;

    /// <summary>Where the analysed stretch peaks (s), its mean power and its highest sample's power.</summary>
    public readonly record struct EnvelopeStats(double PeakTimeS, double Mean, double PeakSample);

    private static EnvelopeStats MeasureEnvelope(Complex[] x, double rate)
    {
        double mean = 0, pk = 0; int ipk = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i].Real * x[i].Real + x[i].Imaginary * x[i].Imaginary;
            mean += v;
            if (v > pk) { pk = v; ipk = i; }
        }
        return new EnvelopeStats(ipk / rate, mean / Math.Max(1, x.Length), pk);
    }

    /// <summary>
    /// <paramref name="windowSeconds"/> of instantaneous power centred on the record's peak, in dB re the average,
    /// with the average (0 dB) and the peak as flat lines: the gap between the two lines IS the peak-to-average
    /// ratio. Re-interpolated from the carriers' native records (<see cref="SamplesPerCycle"/> points per 1 / IBW, at
    /// most <see cref="EnvelopeWindowPoints"/>), so the peak line also takes the true peak between samples.
    /// </summary>
    public static DataSet EnvelopeChunk(ModulatedSignal sig, EnvelopeStats st, double windowSeconds)
    {
        var ds = new DataSet();
        if (st.Mean <= 0 || !(windowSeconds > 0)) return ds;
        double ibw = Math.Max(sig.InstantaneousBandwidthHz, 1e6);
        double fine = Math.Min(SamplesPerCycle * ibw, (EnvelopeWindowPoints - 1) / windowSeconds);
        int len = (int)Math.Round(windowSeconds * fine) + 1;
        long start = (long)Math.Round(st.PeakTimeS * fine) - len / 2;
        var w = sig.CompositeWindow(fine, 0, start, len);
        double pk = st.PeakSample;
        bool us = windowSeconds >= 10e-6;
        var t = new double[len];
        var p = new double[len];
        for (int k = 0; k < len; k++)
        {
            double v = w[k].Real * w[k].Real + w[k].Imaginary * w[k].Imaginary;
            pk = Math.Max(pk, v);
            t[k] = k / fine * (us ? 1e6 : 1e9);
            p[k] = Math.Max(10 * Math.Log10(Math.Max(v, 1e-300) / st.Mean), -30);
        }
        double papr = 10 * Math.Log10(pk / st.Mean);
        var axis = new Axis("time", t, us ? "us" : "ns");
        ds.Add("Envelope", new DataCube([axis], p));
        ds.Add("Average", new DataCube([axis], new double[len]));
        ds.Add("Peak", new DataCube([axis], Enumerable.Repeat(papr, len).ToArray()));
        return ds;
    }

    /// <summary>EVM of every LTE / NR carrier of a signal (pure computation, any thread).</summary>
    public static List<EvmRow> AnalyseEvm(ModulatedSignal sig)
    {
        var rows = new List<EvmRow>();
        for (int i = 0; i < sig.Carriers.Count; i++)
        {
            var c = sig.Carriers[i];
            if (c.Spec.Standard is not (CarrierStandard.Lte or CarrierStandard.Nr)) continue;
            rows.Add(new EvmRow(i + 1, $"C{i + 1}  {c.Spec.Label}", SignalEvm.Measure(c.Spec, c.Native, sig.RecordSeconds)));
        }
        return rows;
    }

    /// <summary>A CCDF spanning less than this (dB) is framed 0 to 12 dB rather than autoscaled onto its own width.</summary>
    public const double CcdfMinSpanDb = 2;

    /// <summary>The spectrum's offset axis reads in Hz for a signal a few hundred Hz wide (a DBPSK uplink), in MHz otherwise.</summary>
    public static bool PsdInHz(ModulatedSignal sig) => sig.Carriers.Max(c => Math.Abs(c.OffsetHz) + c.Spec.BandwidthHz) < 20e3;

    private void ShowPlots(DataSet ds, ModulatedSignal sig)
    {
        Fill(PsdPlot, ds, [("PSD", 0)], "Spectrum", "PSD (dB re peak)", PsdInHz(sig) ? "Offset from Fc (Hz)" : "Offset from Fc (MHz)");
        // A pulse train's CCDF is as true as any signal's: a step at its peak-to-average, which is its power duty.
        CcdfPlot.Axes.YScale = AxisScale.Log;
        // the legend names the reference when it is drawn; alone, the signal's curve needs none
        bool gated = SignalComposer.PulseOf(sig.Spec) is not null && GateCcdf;
        Fill(CcdfPlot, ds, [("CCDF", 0), ("Gaussian", 3)], gated ? "CCDF (pulse on-time)" : "CCDF", "Probability (%)", "Power above average (dB)");
        CcdfPlot.Legend = ds.Contains("Gaussian") ? LegendPlacement.Inside : LegendPlacement.Off;
        // A constant envelope (FSK, CSS, a gated pulse) is a step within a hair of 0 dB, and the autoscale framed that
        // hair: ticks in nano-dB. Without the Gaussian reference to widen it, a span under 2 dB is framed as the
        // reference would frame it, 0 to ~11 dB, so a step reads as a step. The probability axis keeps its autoscale.
        var w = CcdfPlot.Axes.Window;
        if (w.Width < CcdfMinSpanDb) CcdfPlot.Axes.Window = new PlotRect(-0.5, w.Y, 12.5, w.Height);
        Announce(PsdContainer, CcdfContainer);
        if (ds.Contains("Pulse"))
        {
            // |envelope| over one PRI, zoomable onto an edge like any Data Display plot
            Fill(TimePlot, ds, [("Pulse", 0)], "Pulse (one PRI)", "|Envelope| (re pulse top)", "Time (µs)");
            Announce(TimeContainer);
        }
        else if (ds.Contains("Frequency"))
        {
            bool css = SignalComposer.CssOf(sig.Spec) is not null;
            Fill(TimePlot, ds, [("Frequency", 0)], css ? "Instantaneous frequency (packet start)" : "Instantaneous frequency (first bits)",
                 "Frequency re Fc (kHz)", css ? "Time (ms)" : "Time (µs)");
            Announce(TimeContainer);
        }
        else if (ds.Contains("Waveform"))
        {
            Fill(TimePlot, ds, [("Waveform", 0)], "In-phase waveform (first 32 bits)", "I (re peak)", "Time (ms)");
            Announce(TimeContainer);
        }
    }

    private void ShowEnvelope(DataSet ds)
    {
        var inv = CultureInfo.InvariantCulture;
        string unit = ds.Contains("Envelope") && ds["Envelope"].Axes[0].Unit == "us" ? "µs" : "ns";
        double papr = ds.Contains("Peak") ? ds["Peak"].RealValues[0] : 0;
        PaprText = string.Format(inv, "{0:0.00} dB", papr);
        // Power in blue, the average a solid green line, the peak a dashed red one (the key above the plot).
        // No title: the peak - average gap is marked IN the plot, by the arrow below and its label (the view).
        Fill(EnvelopePlot, ds, [("Envelope", 1), ("Average", 3), ("Peak", 0)],
             "", "Power re average (dB)", $"Time ({unit})");
        EnvelopePlot.CustomTitleOn = false;
        foreach (var tr in EnvelopePlot.Traces)
        {
            tr.Properties.LineType = tr.CubeName == "Peak" ? LineType.Dashed : LineType.Solid;
            tr.Properties.LineWidth = tr.CubeName == "Envelope" ? 1.0 : 1.8;
            tr.QuantityName = tr.CubeName switch
            {
                "Average" => "Average (0 dB)",
                "Peak" => string.Format(inv, "Peak (+{0:0.00} dB)", papr),
                _ => "Instantaneous power",
            };
        }
        // A clear strip left of the data holds the arrow, so it crosses no trace; room above the peak line and
        // below the floor, so neither flat line runs along the frame.
        double t1 = ds.Contains("Envelope") ? ds["Envelope"].Axes[0].Values[^1] : 1;
        double left = -0.16 * t1;
        GapArrowX = 0.55 * left;
        GapDb = papr;
        AddGapArrow(EnvelopePlot, GapArrowX, papr);
        EnvelopePlot.Axes.Window = new PlotRect(left, -30, t1 - left + 0.02 * t1, Math.Ceiling((papr + 3) / 5) * 5 + 30);
        Readable(EnvelopePlot);                         // the arrow traces re-lay the margins out
        Announce(EnvelopeContainer);
    }

    /// <summary>A double-headed arrow from the average (0 dB) to the peak line at <paramref name="x"/>.</summary>
    private static void AddGapArrow(Plot plot, double x, double papr)
    {
        if (!(papr > 0)) return;
        var ds = new DataSet();
        var two = new Axis("i", [0.0, 1.0], "");
        ds.Add("GapY", new DataCube([two], [0.0, papr]));
        ds.Add("GapX", new DataCube([two], [x, x]));
        var one = new Axis("i", [0.0], "");
        ds.Add("TopY", new DataCube([one], [papr - 0.55]));
        ds.Add("TopX", new DataCube([one], [x]));
        ds.Add("BotY", new DataCube([one], [0.55]));
        ds.Add("BotX", new DataCube([one], [x]));
        AddXY(plot, ds, "GapY", "GapX", line: true, MarkerType.Circle, 0);
        AddXY(plot, ds, "TopY", "TopX", line: false, MarkerType.TriangleUp, 2.6);
        AddXY(plot, ds, "BotY", "BotX", line: false, MarkerType.TriangleDown, 2.6);
    }

    // The arrow's colour: solid black (position 9 of the trace colour order), as is the gap label in the view.
    private const int ArrowColour = 9;

    /// <summary>Tick numbers and axis names read at a glance in these small panels.</summary>
    public const double AxisFontSize = 9;

    /// <summary>
    /// Larger axis text than the Data Display's default (6), on margins widened to hold it. The axis names and the
    /// tick numbers both scale with <see cref="Axes.FontSizeTicks"/>, and the margins are FRACTIONS of the plot, so
    /// the larger text needs a wider left strip and a deeper bottom one. Re-applied after every autoscale, which
    /// recomputes the margins for the default font (<c>Plot.SetAxesViewport</c>).
    /// </summary>
    private static void Readable(Plot plot)
    {
        plot.Axes.FontSizeTicks = AxisFontSize;
        plot.Axes.Viewport = new PlotRect(0.22, 0.08, 0.74, 0.68);
    }

    private static void AddXY(Plot plot, DataSet ds, string y, string x, bool line, MarkerType shape, double size)
    {
        if (!CubeTraceSpecParser.TryParse(y, ds, out string cube, out var slice, out var transform, out _)) return;
        int index = TraceProperties.LineColorOrder[ArrowColour];
        var style = new TraceProperties
        {
            LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index,
            LineEnabled = line, LineWidth = 1.6, MarkerEnabled = !line, MarkerType = shape, MarkerSize = size,
        };
        var t = new CircuitRF.Render.DataDisplay.Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real, secondaryAxis: false, style)
        {
            CubeName = cube, Slice = slice, Transform = transform, Expression = y, XSpec = x,
        };
        t.QuantityName = "peak - average";
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        if (t.Points.Count > 0) plot.Traces.Add(t);
    }

    private void ShowEvm(List<EvmRow> rows)
    {
        EvmRows.Clear();
        foreach (var r in rows) EvmRows.Add(r);
        PickEvm();
    }

    /// <summary>Shows the calculated EVM of the carrier selected on the left, and says which signal it is.</summary>
    private void PickEvm()
    {
        int index = SelectedCarrier?.Index ?? 1;
        var pick = EvmRows.FirstOrDefault(r => r.CarrierIndex == index);
        HasEvm = pick is not null;
        EvmSignalText = DescribeEvmSignal(_evmSig, index);
        EvmNote = pick is not null || _evmSig is null ? ""
            : $"C{index} has no EVM: CW and imported I/Q carriers carry no reference symbols. Select an LTE or NR carrier on the left.";
        if (ReferenceEquals(pick, SelectedEvm)) ShowConstellation(pick);
        else SelectedEvm = pick;
    }

    /// <summary>What the EVM was measured on: carrier, standard, bandwidth, SCS, modulation, offset, record, clipping.</summary>
    public static string DescribeEvmSignal(ModulatedSignal? sig, int carrierIndex)
    {
        if (sig is null || carrierIndex < 1 || carrierIndex > sig.Carriers.Count) return "Not calculated yet.";
        var inv = CultureInfo.InvariantCulture;
        var c = sig.Carriers[carrierIndex - 1];
        var s = c.Spec;
        string what = s.Standard switch
        {
            CarrierStandard.Lte => string.Format(inv, "LTE {0:0.#} MHz, {1}", s.BandwidthHz / 1e6, CarrierSpec.ModName(s.Modulation)),
            CarrierStandard.Nr => string.Format(inv, "NR {0:0.#} MHz, {1:0} kHz SCS, {2}", s.BandwidthHz / 1e6, s.ScsHz / 1e3, CarrierSpec.ModName(s.Modulation)),
            CarrierStandard.Cw => "CW",
            CarrierStandard.Pcw or CarrierStandard.Fsk or CarrierStandard.Dbpsk or CarrierStandard.Css => s.Label,
            _ => "I/Q file " + System.IO.Path.GetFileName(s.FilePath ?? ""),
        };
        string clip = sig.Spec.ClipPaprDb is double t ? string.Format(inv, "clipped to {0:0.#} dB PAPR", t) : "unclipped";
        // the record in the Spectrum tab's own spelling ("record 1e-3 s")
        return string.Format(inv, "C{0} of {1}:  {2},  offset {3:+0.###;-0.###;0} MHz,  seed {4},  record {5:0.###e0} s,  {6}",
            carrierIndex, sig.Carriers.Count, what, c.OffsetHz / 1e6, s.Seed, sig.RecordSeconds, clip);
    }

    /// <summary>Most received points drawn on the constellation; the EVM itself uses every one.</summary>
    public const int ConstellationPoints = 6000;

    private void ShowConstellation(EvmRow? row)
    {
        var plot = ConstellationPlot;
        var markers = TakeMarkers(plot);
        plot.CustomTitleOn = true;
        plot.CustomXLabelOn = true;
        plot.CustomXLabel = "In-phase (I)";
        plot.CustomYLabelOn = true;
        plot.CustomYLabel = "Quadrature (Q)";
        if (row is null) { plot.CustomTitle = "Constellation"; Announce(ConstellationContainer); return; }
        var r = row.Result;
        plot.CustomTitle = $"Constellation C{row.CarrierIndex}: EVM {row.RmsText} rms";

        int stride = Math.Max(1, (r.Received.Length + ConstellationPoints - 1) / ConstellationPoints);
        var rx = Enumerable.Range(0, (r.Received.Length + stride - 1) / stride).Select(k => r.Received[k * stride]).ToArray();
        var ideal = r.Ideal.Select(z => (Math.Round(z.Real, 9), Math.Round(z.Imaginary, 9))).Distinct()
                     .Select(z => new Complex(z.Item1, z.Item2)).ToArray();
        var ds = new DataSet();
        var ra = new Axis("point", Enumerable.Range(0, rx.Length).Select(i => (double)i).ToArray(), "");
        var ia = new Axis("point", Enumerable.Range(0, ideal.Length).Select(i => (double)i).ToArray(), "");
        ds.Add("RxI", new DataCube([ra], rx.Select(z => z.Real).ToArray()));
        ds.Add("RxQ", new DataCube([ra], rx.Select(z => z.Imaginary).ToArray()));
        ds.Add("IdealI", new DataCube([ia], ideal.Select(z => z.Real).ToArray()));
        ds.Add("IdealQ", new DataCube([ia], ideal.Select(z => z.Imaginary).ToArray()));
        // Received in blue, the ideal points as small red crosses on top: small enough that the cloud
        // around each one stays visible (the key under the plot).
        Scatter(plot, ds, "RxQ", "RxI", "Received", 1, MarkerType.Circle, 0.8, 0.5);
        Scatter(plot, ds, "IdealQ", "IdealI", "Ideal", 0, MarkerType.Cross, 1.6, 1.0);

        // Square and centred: I and Q share one scale, so a circle of error looks like a circle.
        double lim = Math.Max(ideal.Max(z => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary))),
                              rx.Max(z => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary))));
        lim = Math.Ceiling(lim * 1.1 / 0.25) * 0.25;
        plot.Axes.Window = new PlotRect(-lim, -lim, 2 * lim, 2 * lim);
        Readable(plot);
        GiveMarkers(plot, markers);
        Announce(ConstellationContainer);
    }

    private static void Scatter(Plot plot, DataSet ds, string y, string x, string name, int colour, MarkerType shape,
                                double size, double opacity)
    {
        if (!CubeTraceSpecParser.TryParse(y, ds, out string cube, out var slice, out var transform, out _)) return;
        int index = TraceProperties.LineColorOrder[colour % TraceProperties.LineColorOrder.Length];
        var style = new TraceProperties
        {
            LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index,
            LineEnabled = false, MarkerEnabled = true, MarkerType = shape, MarkerSize = size, MarkerOpacity = opacity,
        };
        var t = new CircuitRF.Render.DataDisplay.Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real, secondaryAxis: false, style)
        {
            CubeName = cube, Slice = slice, Transform = transform, Expression = y, XSpec = x,
        };
        t.QuantityName = name;
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        if (t.Points.Count > 0) plot.Traces.Add(t);
    }

    // The two plots keep the Data Display's own text sizes (as the Match Designer's do): a bigger axis
    // font overran the tick numbers, because the plot's margins are laid out for its own sizes.
    private static void Fill(Plot plot, DataSet ds, (string cube, int colour)[] cubes, string title, string ylabel, string xlabel)
    {
        plot.CustomTitleOn = true;
        plot.CustomTitle = title;
        plot.CustomXLabelOn = true;
        plot.CustomXLabel = xlabel;
        var markers = TakeMarkers(plot);
        foreach (var (cube, colour) in cubes)
        {
            if (!CubeTraceSpecParser.TryParse(cube, ds, out string name, out var slice, out var transform, out _)) continue;
            int index = TraceProperties.LineColorOrder[colour % TraceProperties.LineColorOrder.Length];
            var style = new TraceProperties
            {
                LineColorIndex = index, MarkerColorIndex = index, FillColorIndex = index,
                LineEnabled = true, LineType = colour == 3 ? LineType.Dashed : LineType.Solid,
                LineWidth = colour is 2 or 3 ? 1.2 : 1.6,
            };
            var t = new CircuitRF.Render.DataDisplay.Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real, secondaryAxis: false, style)
            {
                CubeName = name, Slice = slice, Transform = transform, Expression = cube,
            };
            t.QuantityName = cube switch { "Gaussian" => "Gaussian noise (reference)", "CCDF" => "Signal", _ => cube };
            TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
            if (t.Points.Count > 0) plot.Traces.Add(t);
        }
        GiveMarkers(plot, markers);
        plot.CustomYLabelOn = true;
        plot.CustomYLabel = ylabel;
        plot.Autoscale(force: true);
        Readable(plot);                                  // the autoscale re-lays the margins out for the default font
    }

    /// <summary>Clears <paramref name="plot"/>'s traces and returns their markers by trace expression. Every preview
    /// rebuilds the traces from scratch, and a marker the user placed belongs to the QUANTITY, not to one rebuild of
    /// it: <see cref="GiveMarkers"/> puts each back on the new trace of the same expression, where it resolves to the
    /// nearest point of the new data.</summary>
    private static Dictionary<string, List<Marker>> TakeMarkers(Plot plot)
    {
        var kept = new Dictionary<string, List<Marker>>(StringComparer.Ordinal);
        foreach (var t in plot.Traces)
            if (t.Expression is { } e && t.Markers.Count > 0)
                (kept.TryGetValue(e, out var list) ? list : kept[e] = []).AddRange(t.Markers);
        plot.Traces.Clear();
        return kept;
    }

    private static void GiveMarkers(Plot plot, Dictionary<string, List<Marker>> kept)
    {
        foreach (var t in plot.Traces)
            if (t.Expression is { } e && kept.Remove(e, out var markers))
                t.Markers.AddRange(markers);
    }

    /// <summary>The traces of <paramref name="containers"/> were just replaced: their info boxes are rebuilt against
    /// the new traces (a box pointing at a discarded trace reads nothing), and the window repaints.</summary>
    private void Announce(params PlotContainerViewModel[] containers)
    {
        foreach (var c in containers) c.OnPlotChanged(this, EventArgs.Empty);
        PlotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes every SELECTED marker, as the Delete key does in a Data Display. Not
    /// <c>DataDisplayViewModel.DeleteSelected</c>: that also deletes selected PLOTS, and these four are fixed.</summary>
    [RelayCommand]
    public void DeleteSelectedMarkers()
    {
        foreach (var box in PlotHost.MarkerInfoBoxes.Where(b => b.IsSelected).ToList())
            box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
