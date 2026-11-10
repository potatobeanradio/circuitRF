using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Chain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Chain;

/// <summary>One parameter of one stage: its name, its text, and the choices when it names one.</summary>
public sealed partial class ChainParamViewModel(ChainParam param, string value, Action changed) : ObservableObject
{
    public string Name => param.Name;
    /// <summary>The unit a bare number is read in, shown beside the field. A frequency's text carries its own unit
    /// ("2.35 GHz"), so none is shown for one.</summary>
    public string Unit => param.Unit is "dB" or "dBm" or "deg" ? param.Unit : "";
    public string Tip => param.Help.Length > 0 ? $"{param.Name}: {param.Help}" : param.Name;
    public IReadOnlyList<string>? Options => param.Options;
    public bool HasOptions => param.Options is { Count: > 0 };
    public bool IsText => !HasOptions;
    public string Watermark => param.Default.Length > 0 ? param.Default : param.BlankAllowed ? "blank" : "";

    [ObservableProperty] private string _value = value;

    partial void OnValueChanged(string value) => changed();
}

/// <summary>One stage row: its name, its part, the part's parameters, and the stage's own budget.</summary>
public sealed partial class ChainStageViewModel : ObservableObject
{
    private readonly Action _changed;

    public ChainStageViewModel(ChainStage stage, Action changed)
    {
        _changed = changed;
        _name = stage.Name;
        _part = ChainParts.Find(stage.Part) ?? ChainParts.All[0];
        Unlisted = stage.Parameters.Where(kv => _part.Parameters.All(p => p.Name != kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        Fill(stage);
    }

    /// <summary>Every part a stage can be, for the Part list.</summary>
    public static IReadOnlyList<ChainPart> Parts => ChainParts.All;

    [ObservableProperty] private string _name;
    [ObservableProperty] private ChainPart _part;
    [ObservableProperty] private string _summary = "";

    /// <summary>Parameters the stage carries that its part does not list: kept, and written back, unchanged.</summary>
    private Dictionary<string, string> Unlisted { get; }

    public ObservableCollection<ChainParamViewModel> Parameters { get; } = [];

    partial void OnNameChanged(string value) => _changed();

    /// <summary>A new part keeps every parameter the two share by name and takes the new one's defaults for the rest.</summary>
    partial void OnPartChanged(ChainPart value)
    {
        Fill(ToStage());
        _changed();
    }

    private void Fill(ChainStage stage)
    {
        Parameters.Clear();
        foreach (var p in Part.Parameters)
            Parameters.Add(new ChainParamViewModel(p, stage.Parameters.TryGetValue(p.Name, out var v) ? v : p.Default, _changed));
    }

    public ChainStage ToStage()
    {
        var s = new ChainStage { Name = Name.Trim(), Part = Part.Token };
        foreach (var (k, v) in Unlisted) s.Parameters[k] = v;
        foreach (var p in Parameters) s.Parameters[p.Name] = p.Value ?? "";
        return s;
    }
}

/// <summary>
/// The RF Chain Budget window: a <c>.cchain</c> as a table of stages and system inputs, its cascade recomputed on every
/// edit by <see cref="ChainBudget"/>, and its Transfer to Schematic. Nothing here computes a number.
/// </summary>
public sealed partial class ChainBudgetViewModel : ObservableObject
{
    public const string ToolName = "RF Chain Budget";

    private bool _loading;

    public ChainBudgetViewModel() : this(Example()) { }

    public ChainBudgetViewModel(ChainDesign design) => Load(design);

    /// <summary>What a new chain starts as: an LNA, a filter and a second amplifier.</summary>
    public static ChainDesign Example() => new()
    {
        Stages =
        [
            new ChainStage { Name = "LNA", Part = ChainParts.Amp, Parameters = { ["Gain"] = "16", ["NF"] = "1", ["IP3"] = "15" } },
            new ChainStage { Name = "BPF", Part = ChainParts.Filter, Parameters = { ["Response"] = "Butterworth", ["F1"] = "2.35 GHz", ["F2"] = "2.55 GHz", ["IL"] = "1.5" } },
            new ChainStage { Name = "AMP2", Part = ChainParts.Amp, Parameters = { ["Gain"] = "20", ["NF"] = "3", ["IP3"] = "25" } },
        ],
    };

    public ObservableCollection<ChainStageViewModel> Stages { get; } = [];

    [ObservableProperty] private ChainStageViewModel? _selectedStage;

    /// <summary>The diagram and the table share one selection: the selected row's parts are drawn selected, and a
    /// click on a part in the drawing selects its row.</summary>
    partial void OnSelectedStageChanged(ChainStageViewModel? value) => OnPropertyChanged(nameof(SelectedStageIndex));

    public int SelectedStageIndex
    {
        get => SelectedStage is { } s ? Stages.IndexOf(s) : -1;
        set
        {
            var next = value >= 0 && value < Stages.Count ? Stages[value] : null;
            if (!ReferenceEquals(next, SelectedStage)) SelectedStage = next;
        }
    }

    /// <summary>The chain as the benches draw it (<see cref="ChainBenches.Diagram"/>), annotated stage by stage.</summary>
    [ObservableProperty] private ChainDiagram? _diagram;

    /// <summary>The cascade, as a Data Display table over the budget's stage axis.</summary>
    public CircuitRF.Ui.DataDisplay.ViewModels.DataDisplayViewModel PlotHost { get; } =
        new(new CircuitRF.Ui.DataDisplay.ViewModels.DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    private CircuitRF.Ui.DataDisplay.ViewModels.PlotContainerViewModel? _cascade;

    public CircuitRF.Render.DataDisplay.Plot? CascadePlot => _cascade?.PlotVM.Plot;

    /// <summary>Raised after the cascade table is refilled in place: a PlotControl repaints only when told.</summary>
    public event EventHandler? PlotsUpdated;

    private void RefillCascade(ChainBudgetResult r)
    {
        var pc = CircuitRF.Render.DataDisplay.ChainBudgetDisplay.CascadeTable(BudgetSource);
        if (_cascade is null)
        {
            _cascade = PlotHost.AddPlot(CircuitRF.Render.DataDisplay.PlotType.Table, CircuitRF.Render.DataDisplay.FreqUnit.GHz, 0, 0, pc.Width, pc.Height);
            PlotHost.SelectOnly((CircuitRF.Ui.DataDisplay.ViewModels.PlotContainerViewModel?)null);
            OnPropertyChanged(nameof(CascadePlot));
        }
        var plot = _cascade.PlotVM.Plot;
        plot.Traces.Clear();
        var source = new CircuitRF.Render.DataDisplay.ChainBudgetDisplay.Source(BudgetSource, ChainBudget.ToDataSet(r));
        if (r.Totals is not null)
            foreach (var tc in pc.Traces)
                if (CircuitRF.Render.DataDisplay.PlotConfigLoader.LoadTrace(tc, pc.PlotType, pc.FreqUnit, source) is { } t)
                    plot.Traces.Add(t);
        plot.CustomTitle = pc.CustomTitle;
        plot.CustomTitleOn = pc.CustomTitleOn;
        _cascade.OnPlotChanged(_cascade, EventArgs.Empty);
        _cascade.RequestPlotRedraw();
        PlotsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private const string BudgetSource = "budget";
    [ObservableProperty] private ChainPart _partToAdd = ChainParts.All[0];

    // The system inputs, as typed: a frequency in GHz, a bandwidth in MHz, the rest in their own unit.
    [ObservableProperty] private string _frequencyGHz = "";
    [ObservableProperty] private string _bandwidthMHz = "";
    [ObservableProperty] private string _inputPowerDbm = "";
    [ObservableProperty] private string _temperatureK = "";
    [ObservableProperty] private string _requiredSnrDb = "";

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _refusals = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;

    public bool HasRefusals => Refusals.Length > 0;
    public bool HasNotes => Notes.Length > 0;
    public string Title => (IsDirty ? "• " : "") + ToolName + (FilePath is { } p ? " — " + Path.GetFileName(p) : "");

    /// <summary>The last budget computed: what Transfer writes from.</summary>
    public ChainBudgetResult? Budget { get; private set; }

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    partial void OnRefusalsChanged(string value) => OnPropertyChanged(nameof(HasRefusals));
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
    partial void OnFrequencyGHzChanged(string value) => Changed();
    partial void OnBandwidthMHzChanged(string value) => Changed();
    partial void OnInputPowerDbmChanged(string value) => Changed();
    partial void OnTemperatureKChanged(string value) => Changed();
    partial void OnRequiredSnrDbChanged(string value) => Changed();

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public void Load(ChainDesign d)
    {
        _loading = true;
        FrequencyGHz = N(d.FrequencyHz / 1e9);
        BandwidthMHz = N(d.BandwidthHz / 1e6);
        InputPowerDbm = N(d.InputPowerDbm);
        TemperatureK = N(d.TemperatureK);
        RequiredSnrDb = N(d.RequiredSnrDb);
        Stages.Clear();
        foreach (var s in d.Stages) Stages.Add(new ChainStageViewModel(s, Changed));
        _loading = false;
        Recompute();
        IsDirty = false;
    }

    private static double Parse(string text, double scale, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * scale : fallback;

    public ChainDesign ToDesign() => new()
    {
        FrequencyHz = Parse(FrequencyGHz, 1e9, double.NaN),
        BandwidthHz = Parse(BandwidthMHz, 1e6, double.NaN),
        InputPowerDbm = Parse(InputPowerDbm, 1, double.NaN),
        TemperatureK = Parse(TemperatureK, 1, double.NaN),
        RequiredSnrDb = Parse(RequiredSnrDb, 1, double.NaN),
        Stages = [.. Stages.Select(s => s.ToStage())],
    };

    private void Changed()
    {
        if (_loading) return;
        IsDirty = true;
        Recompute();
    }

    private static string D(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";
    private static string Dbm(double v) => double.IsFinite(v) ? D(v) + " dBm" : "none (linear)";

    /// <summary>The cascade, again, from what the rows say now.</summary>
    public void Recompute()
    {
        var d = ToDesign();
        var r = ChainBudget.Evaluate(d);
        Budget = r;
        foreach (var (row, s) in Stages.Zip(r.Stages))
            row.Summary = $"On its own: G {D(s.Stage.GainDb)} dB · NF {D(s.Stage.NfDb)} dB · IIP3 {Dbm(s.Stage.Iip3Dbm)} · IP1dB {Dbm(s.Stage.Ip1dbDbm)}";
        Diagram = ChainBenches.Diagram(d, r);
        RefillCascade(r);
        Refusals = string.Join("\n", r.Refusals);
        Notes = string.Join("\n", r.Notes);
        Summary = r.Totals is not { } t ? "" :
            $"Gain {D(t.GainDb)} dB    NF {D(t.NfDb)} dB (Te {t.TeK:0.#} K)    IIP3 {D(t.Iip3Dbm)} dBm    OIP3 {D(t.Oip3Dbm)} dBm    " +
            $"IP1dB {D(t.Ip1dbDbm)} dBm    OP1dB {D(t.Op1dbDbm)} dBm    Pout {D(t.PoutDbm)} dBm\n" +
            $"Thermal floor {D(t.ThermalFloorDbm)} dBm    Output noise {D(t.NoiseOutDbm)} dBm    MDS {D(t.MdsDbm)} dBm    " +
            $"Sensitivity {D(t.SensitivityDbm)} dBm    SNR {D(t.SnrDb)} dB    SFDR {D(t.SfdrDb)} dB" +
            (t.ArrayGainDb > 0 ? $"    Array gain {D(t.ArrayGainDb)} dB" : "") +
            (Math.Abs(t.OutputFrequencyHz - d.FrequencyHz) > 0 ? $"    Output at {t.OutputFrequencyHz / 1e6:0.###} MHz" : "");
    }

    // ── the stage list ───────────────────────────────────────────────────────

    [RelayCommand]
    private void AddStage()
    {
        var part = PartToAdd;
        string stem = part.Token switch
        {
            ChainParts.Amp => "AMP", ChainParts.Mixer => "MIX", ChainParts.Atten => "ATT", ChainParts.Filter => "FLT",
            ChainParts.Switch => "SW", ChainParts.Circulator => "ISO", ChainParts.Coupler => "CPL",
            ChainParts.PhaseShifter => "PS", ChainParts.Limiter => "LIM", ChainParts.Splitter => "SPL", _ => "CMB",
        };
        int n = 1;
        while (Stages.Any(s => s.Name.Equals($"{stem}{n}", StringComparison.OrdinalIgnoreCase))) n++;
        var row = new ChainStageViewModel(new ChainStage { Name = $"{stem}{n}", Part = part.Token }, Changed);
        int at = SelectedStage is { } sel ? Stages.IndexOf(sel) + 1 : Stages.Count;
        Stages.Insert(at, row);
        SelectedStage = row;
        Changed();
    }

    [RelayCommand]
    private void RemoveStage()
    {
        if (SelectedStage is not { } sel) return;
        int at = Stages.IndexOf(sel);
        Stages.Remove(sel);
        SelectedStage = Stages.Count == 0 ? null : Stages[Math.Min(at, Stages.Count - 1)];
        Changed();
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(+1);

    private void Move(int by)
    {
        if (SelectedStage is not { } sel) return;
        int at = Stages.IndexOf(sel), to = at + by;
        if (to < 0 || to >= Stages.Count) return;
        Stages.Move(at, to);
        SelectedStage = sel;
        Changed();
    }

    // ── files ────────────────────────────────────────────────────────────────

    /// <summary>Asks where to save; null when the user cancels. Set by the window.</summary>
    public Func<string?, Task<string?>>? PickSavePathAsync { get; set; }

    /// <summary>Asks for a <c>.cchain</c> to open; null when the user cancels. Set by the window.</summary>
    public Func<Task<string?>>? PickOpenPathAsync { get; set; }

    /// <summary>Where errors and notes go. Set by the window.</summary>
    public Action<string>? Report { get; set; }

    public void LoadFile(string path)
    {
        Load(ChainDesignIo.LoadFromFile(path));
        FilePath = Path.GetFullPath(path);
    }

    [RelayCommand]
    private void New()
    {
        Load(Example());
        FilePath = null;
    }

    [RelayCommand]
    private async Task Open()
    {
        if (PickOpenPathAsync is null || await PickOpenPathAsync() is not { } path) return;
        try { LoadFile(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { Report?.Invoke($"{Path.GetFileName(path)} did not open: {ex.Message}"); }
    }

    [RelayCommand]
    public async Task<bool> Save() => FilePath is { } p ? Write(p) : await SaveAs();

    [RelayCommand]
    private async Task<bool> SaveAs()
    {
        if (PickSavePathAsync is null || await PickSavePathAsync(FilePath) is not { } path) return false;
        return Write(path);
    }

    private bool Write(string path)
    {
        try
        {
            ChainDesignIo.SaveToFile(path, ToDesign());
            FilePath = Path.GetFullPath(path);
            IsDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report?.Invoke($"{Path.GetFileName(path)} was not saved: {ex.Message}");
            return false;
        }
    }
}
