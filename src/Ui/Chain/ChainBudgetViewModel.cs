using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Matching;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Chain;

/// <summary>One parameter of one stage: its name, its text, and the choices when it names one.</summary>
public sealed partial class ChainParamViewModel(ChainParam param, string value, Action<object?> changed) : ObservableObject
{
    public string Name => param.Name;
    /// <summary>The unit a bare number is read in, shown beside the field. A frequency's text carries its own unit
    /// ("2.35 GHz"), so none is shown for one.</summary>
    public string Unit => param.Unit is "dB" or "dBm" or "deg" or "W" ? param.Unit : "";

    /// <summary>A budget-only value left blank is not written into the stage, so a chain that does not use one saves as
    /// it always did.</summary>
    public bool OmitWhenBlank => param.BudgetOnly && string.IsNullOrWhiteSpace(Value);
    public string Tip => param.Help.Length > 0 ? $"{param.Name}: {param.Help}" : param.Name;
    public IReadOnlyList<string>? Options => param.Options;
    public bool HasOptions => param.Options is { Count: > 0 };
    public bool IsText => !HasOptions;
    /// <summary>The value names a file: the row offers a picker beside the box.</summary>
    public bool IsFile => param.IsFile;
    public string Watermark => param.Default.Length > 0 ? param.Default : param.BlankAllowed ? "blank" : "";

    [ObservableProperty] private string _value = value;

    /// <summary>The value typed here makes the budget refuse the chain: the box is drawn invalid, the views keep the
    /// last chain that evaluated, and leaving the box puts <see cref="LastGood"/> back.</summary>
    [ObservableProperty] private bool _invalid;

    /// <summary>The value this field had the last time the whole chain evaluated.</summary>
    public string LastGood { get; set; } = value;

    // A typed value is one undo step however many keystrokes it took; a pick from the list is a step of its own.
    partial void OnValueChanged(string value) => changed(IsText ? this : null);
}

/// <summary>One symbol of a stage's block in the block view: the System block whose artwork it is drawn with, and a
/// caption under it ("4:1", "2 × 2:1"). <paramref name="Artwork"/> is the block's own variant where the kind has
/// several (a filter's band form), null for the kind's default.</summary>
public sealed record ChainBlockGlyph(SymbolKind Kind, string Caption, IReadOnlyList<SymbolPrimitive>? Artwork = null)
{
    public bool HasCaption => Caption.Length > 0;
}

/// <summary>An edit that leaves an SnP stage with no file yet: kept, with its File box drawn invalid as a value being
/// typed is, and the window asked to pick one — rather than refused and undone, which would make the part unpickable.</summary>
public sealed record ChainFileWanted(ChainParamViewModel Param);

/// <summary>One stage row: its name, its part, the part's parameters, and the stage's own budget.</summary>
public sealed partial class ChainStageViewModel : ObservableObject
{
    private readonly Action<object?> _changed;

    /// <param name="changed">Told of every edit, with the key that coalesces it into the previous undo step (the same
    /// text field, typed on) or null for an edit that is a step of its own.</param>
    public ChainStageViewModel(ChainStage stage, Action<object?> changed)
    {
        _changed = changed;
        _name = stage.Name;
        NameLastGood = stage.Name;
        _part = ChainParts.Find(stage.Part) ?? ChainParts.All[0];
        Unlisted = stage.Parameters.Where(kv => _part.Parameters.All(p => p.Name != kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        Fill(stage);
    }

    /// <summary>Every part a stage can be, for the Part list.</summary>
    public static IReadOnlyList<ChainPart> Parts => ChainParts.All;

    [ObservableProperty] private string _name;
    [ObservableProperty] private ChainPart _part;

    /// <summary>The name typed makes the budget refuse the chain (see <see cref="ChainParamViewModel.Invalid"/>).</summary>
    [ObservableProperty] private bool _nameInvalid;

    /// <summary>The name the last time the whole chain evaluated.</summary>
    public string NameLastGood { get; set; } = "";
    [ObservableProperty] private string _summary = "";

    // ── the block view: an idealised block per stage ─────────────────────────

    /// <summary>The block drawn selected: the selected row's.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The symbols the block draws: one for most parts; a combiner's levels, each with its amplifier when
    /// active.</summary>
    [ObservableProperty] private IReadOnlyList<ChainBlockGlyph> _glyphs = [];

    /// <summary>"× N" under an array branch's block, empty elsewhere.</summary>
    [ObservableProperty] private string _timesN = "";

    /// <summary>What the block says beside its symbol: a mixer's LO, a phase shifter's phase, a combiner's variant.</summary>
    [ObservableProperty] private string _extra = "";

    /// <summary>The stage's own numbers, and the cascade at its output.</summary>
    [ObservableProperty] private string _keyLine = "";
    [ObservableProperty] private string _cascadeLine = "";

    /// <summary>Parameters the stage carries that its part does not list: kept, and written back, unchanged.</summary>
    private Dictionary<string, string> Unlisted { get; }

    public ObservableCollection<ChainParamViewModel> Parameters { get; } = [];

    partial void OnNameChanged(string value) => _changed((this, nameof(Name)));

    /// <summary>A new part keeps every parameter the two share by name and takes the new one's defaults for the rest.</summary>
    partial void OnPartChanged(ChainPart value)
    {
        Fill(ToStage());
        _changed(FileWanted());
    }

    /// <summary>The File box of a stage that names no file yet, as the edit's key; null otherwise.</summary>
    internal ChainFileWanted? FileWanted()
        => Parameters.FirstOrDefault(p => p.IsFile) is { } p && string.IsNullOrWhiteSpace(p.Value) ? new ChainFileWanted(p) : null;

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
        foreach (var p in Parameters.Where(p => !p.OmitWhenBlank)) s.Parameters[p.Name] = p.Value ?? "";
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
    partial void OnSelectedStageChanged(ChainStageViewModel? value)
    {
        foreach (var s in Stages) s.IsSelected = ReferenceEquals(s, value);
        OnPropertyChanged(nameof(SelectedStageIndex));
        if (!_restoring && !_loading && !_structural && _current is { } cur)
        {
            _current = cur with { Selected = SelectedStageIndex };
            _lastKey = null;
        }
    }

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

    // ── the levels and the canceller: Data Display plots over the budget's own data ──

    private CircuitRF.Ui.DataDisplay.ViewModels.PlotContainerViewModel? _levels, _levelsDb, _depthPhase, _depthBand;
    public CircuitRF.Render.DataDisplay.Plot? LevelsPlot => _levels?.PlotVM.Plot;
    public CircuitRF.Render.DataDisplay.Plot? LevelsDbPlot => _levelsDb?.PlotVM.Plot;
    public CircuitRF.Render.DataDisplay.Plot? DepthPhasePlot => _depthPhase?.PlotVM.Plot;
    public CircuitRF.Render.DataDisplay.Plot? DepthBandPlot => _depthBand?.PlotVM.Plot;

    /// <summary>True when the chain has a canceller: its tab shows the depth its errors leave.</summary>
    [ObservableProperty] private bool _hasCanceller;

    /// <summary>What the levels plots say about their simulated points: which bench results they read, or why none.</summary>
    [ObservableProperty] private string _levelsNote = "";

    /// <summary>
    /// Fills one plot, made on first use, with <paramref name="pc"/>'s traces over <paramref name="data"/>, which the
    /// Data Display reads through <see cref="CircuitRF.Render.DataDisplay.ChainBudgetDisplay.Source"/> as it reads a
    /// result file.
    /// </summary>
    private void Refill(ref CircuitRF.Ui.DataDisplay.ViewModels.PlotContainerViewModel? holder, string property,
                        CircuitRF.Render.DataDisplay.PlotContainerConfig pc, string sourceName, RfCore.Data.DataSet data)
    {
        if (holder is null)
        {
            holder = PlotHost.AddPlot(pc.PlotType, CircuitRF.Render.DataDisplay.FreqUnit.GHz, 0, 0, pc.Width, pc.Height);
            PlotHost.SelectOnly((CircuitRF.Ui.DataDisplay.ViewModels.PlotContainerViewModel?)null);
            OnPropertyChanged(property);
        }
        var plot = holder.PlotVM.Plot;
        plot.Traces.Clear();
        var source = new CircuitRF.Render.DataDisplay.ChainBudgetDisplay.Source(sourceName, data);
        foreach (var tc in pc.Traces)
            if (CircuitRF.Render.DataDisplay.PlotConfigLoader.LoadTrace(tc, pc.PlotType, pc.FreqUnit, source) is { } t)
                plot.Traces.Add(t);
        plot.CustomTitle = pc.CustomTitle;
        plot.CustomTitleOn = pc.CustomTitleOn;
        plot.Legend = pc.Legend;
        plot.CustomXLabel = pc.CustomXLabel; plot.CustomXLabelOn = pc.CustomXLabelOn;
        plot.CustomYLabel = pc.CustomYLabel; plot.CustomYLabelOn = pc.CustomYLabelOn;
        holder.OnPlotChanged(holder, EventArgs.Empty);
        holder.RequestPlotRedraw();
    }

    /// <summary>
    /// The levels at every node (<see cref="ChainLevels"/>), and, when the chain's Levels, Blockers and 1-Tone benches
    /// have been simulated beside it, their simulated points on the same axes, read from the results Simulate wrote.
    /// </summary>
    private void RefillLevels(ChainDesign d, ChainBudgetResult r)
    {
        var lv = ChainLevels.Evaluate(d, r);
        var data = ChainLevels.ToDataSet(lv);
        var notes = new List<string>();
        // A chain whose Levels bench Transfer writes per conversion has its Levels, 1-Tone and Blockers benches each on a
        // conversion's own node axis and beside its own budget: their displays show them.
        string chain = FilePath is { } fp ? Path.GetFileNameWithoutExtension(fp) : "chain";
        bool perConversion = lv.Count > 0 &&
                             ChainBenches.Build(d, r, chain).Any(b => b.Kind == ChainBench.Levels && b.Chain != chain);
        if (lv.Count > 0 && FilePath is { } path && !perConversion)
        {
            string results = Path.Combine(Path.GetDirectoryName(path)!, "results");
            var axis = data["Psig_dBm"].Axes[0];
            foreach (var (bench, cubes) in new (ChainBench, string[])[]
                     {
                         (ChainBench.Levels, ["Psig_sim", "IM3_sim"]), (ChainBench.OneTone, ["Noise_node_sim", "NF_node_sim"]),
                         (ChainBench.Blockers, [.. Enumerable.Range(1, d.Blockers.Count).Select(k => $"Blk{k}_sim")]),
                     })
            {
                if (cubes.Length == 0) continue;
                string file = Path.Combine(results, ChainBenches.CellName(chain, bench) + ".npy");
                if (!File.Exists(file)) { notes.Add($"{ChainBenches.CellName(chain, bench)} not simulated yet"); continue; }
                var sim = CircuitRF.Render.DataDisplay.PlotSourceFile.LoadWithNotes(file).Data;
                foreach (var c in cubes)
                    if (sim is not null && sim.CubesIn("measurements").TryGetValue(c, out var cube) && cube.RealValues.Length == lv.Count)
                        // A bench writes −300 where a quantity does not exist yet (the IM3 at the input): none, here.
                        data.Add(c, new RfCore.Data.DataCube([axis], [.. cube.RealValues.Select(v => v <= -299 ? double.NaN : v)]) { Unit = cube.Unit });
            }
        }
        LevelsNote = lv.Count == 0 ? "" : perConversion
            ? "Lines: the budget. This chain converts more than once, so its Levels, 1-Tone and Blockers benches are written " +
              "once per conversion, and each conversion's display puts its simulated points beside its own budget. Add a marker " +
              "to a trace to select that node's stage."
            : notes.Count == 0 && FilePath is not null
            ? "Points: the simulated levels from the chain's Levels, Blockers and 1-Tone benches. Lines: the budget. " +
              "Add a marker to a trace to select that node's stage."
            : "Lines: the budget. " + (FilePath is null ? "Save and transfer the chain, then simulate its benches, for the simulated points."
                                                         : string.Join("; ", notes) + ": simulate them for their points.");
        var cfg = CircuitRF.Render.DataDisplay.ChainBudgetDisplay.WindowLevels(d, lv);
        Refill(ref _levels, nameof(LevelsPlot), cfg[0], BudgetSource, data);
        Refill(ref _levelsDb, nameof(LevelsDbPlot), cfg[1], BudgetSource, data);
    }

    /// <summary>The canceller's depth in closed form: against the phase error for a few amplitude errors, and against
    /// the bandwidth for a few delay mismatches.</summary>
    private void RefillCanceller(ChainDesign d)
    {
        var st = d.Stages.FirstOrDefault(x => x.Part == ChainParts.Canceller);
        HasCanceller = st is not null;
        if (st is null) return;
        var c = CancellerSpec.Read(st);
        var (data, cfg) = CircuitRF.Render.DataDisplay.ChainBudgetDisplay.WindowCanceller(c);
        Refill(ref _depthPhase, nameof(DepthPhasePlot), cfg[0], BudgetSource, data);
        Refill(ref _depthBand, nameof(DepthBandPlot), cfg[1], BudgetSource, data);
    }

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

    /// <summary>
    /// The unit the Frequency field is typed in: Settings ▸ General ▸ Units' frequency unit, GHz under Automatic,
    /// read once when the window opens as the Match Designer reads it. The .cchain stores hertz, so the unit is
    /// only how the number is shown.
    /// </summary>
    public string FrequencyUnit { get; } = CircuitRF.Ui.Theming.PreferredUnits.Frequency ?? "GHz";

    private double FrequencyScale => FreqUnitHelper.Multiplier(FrequencyUnit);
    [ObservableProperty] private ChainPart _partToAdd = ChainParts.All[0];

    // The system inputs, as typed: a frequency in FrequencyUnit, a bandwidth in MHz, the rest in their own unit.
    [ObservableProperty] private string _frequency = "";
    [ObservableProperty] private string _bandwidthMHz = "";
    [ObservableProperty] private string _inputPowerDbm = "";
    [ObservableProperty] private string _temperatureK = "";
    [ObservableProperty] private string _requiredSnrDb = "";

    /// <summary>The blockers, as typed: "offset, level" pairs separated by ';' ("150 MHz, -40; 20 MHz, -50").</summary>
    [ObservableProperty] private string _blockers = "";

    /// <summary>Transfer also writes the modulated-signal demo bench.</summary>
    [ObservableProperty] private bool _demo;

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

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(Title));
        // The simulated levels live beside the file: a chain that has just been given one may have results to show.
        if (!_loading) Recompute();
    }
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    partial void OnRefusalsChanged(string value) => OnPropertyChanged(nameof(HasRefusals));
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
    partial void OnFrequencyChanged(string value) => Edited(nameof(Frequency));
    partial void OnBandwidthMHzChanged(string value) => Edited(nameof(BandwidthMHz));
    partial void OnInputPowerDbmChanged(string value) => Edited(nameof(InputPowerDbm));
    partial void OnTemperatureKChanged(string value) => Edited(nameof(TemperatureK));
    partial void OnRequiredSnrDbChanged(string value) => Edited(nameof(RequiredSnrDb));
    partial void OnBlockersChanged(string value) => Edited(nameof(Blockers));
    partial void OnDemoChanged(bool value) => Edited(null);

    private static string FormatBlockers(IEnumerable<ChainBlocker> b) =>
        string.Join("; ", b.Select(x => $"{N(x.OffsetHz / 1e6)} MHz, {N(x.LevelDbm)}"));

    /// <summary>The blockers typed, or null where an entry is not an offset and a level.</summary>
    private List<ChainBlocker>? ParseBlockers()
    {
        var list = new List<ChainBlocker>();
        foreach (var item in Blockers.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !ChainValue.TryParse(parts[0], out double off) || !ChainValue.TryParse(parts[1], out double lvl)) return null;
            // A bare offset is in MHz, as the field says.
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) off *= 1e6;
            list.Add(new ChainBlocker(off, lvl));
        }
        return list;
    }

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The workspace the window was opened from (its folder), where an unsaved chain's relative file references
    /// resolve. Set by the window.</summary>
    public string? HostWorkspaceRoot { get; set; }

    /// <summary>The base a document handed in with no file of its own came with.</summary>
    private string? _loadedBase;

    /// <summary>Where the chain's relative file references resolve: the workspace its <c>.cchain</c> belongs to (its own
    /// folder when none); unsaved, the base it was loaded with or the workspace the window was opened from.</summary>
    public string? ReferenceBase => FilePath is { } p ? ChainDesignIo.ReferenceBaseOf(p, HostWorkspaceRoot)
                                  : _loadedBase ?? HostWorkspaceRoot;

    /// <summary>A file picked for a stage, as the reference written into it: relative to <see cref="ReferenceBase"/> when
    /// under it, the full path otherwise.</summary>
    public string FileReference(string path) => ChainSnp.Reference(path, ReferenceBase);

    /// <summary>A stage now names no file and wants one picked: an SnP part chosen or inserted. Set by the window.</summary>
    public Action<ChainParamViewModel>? FileWanted { get; set; }

    public void Load(ChainDesign d)
    {
        _loadedBase = d.BaseDirectory;
        // Another document: the last good chain was the previous one's, and nothing of it may stand in for this one.
        _hasGood = false;
        SetErrorKey(null);
        _loading = true;
        Frequency = N(d.FrequencyHz / FrequencyScale);
        BandwidthMHz = N(d.BandwidthHz / 1e6);
        InputPowerDbm = N(d.InputPowerDbm);
        TemperatureK = N(d.TemperatureK);
        RequiredSnrDb = N(d.RequiredSnrDb);
        Blockers = FormatBlockers(d.Blockers);
        Demo = d.Demo;
        Stages.Clear();
        foreach (var s in d.Stages) Stages.Add(new ChainStageViewModel(s, Edited));
        _loading = false;
        Recompute();
        ResetHistory();
        IsDirty = false;
    }

    private static double Parse(string text, double scale, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * scale : fallback;

    public ChainDesign ToDesign() => new()
    {
        BaseDirectory = ReferenceBase,
        FrequencyHz = Parse(Frequency, FrequencyScale, double.NaN),
        BandwidthHz = Parse(BandwidthMHz, 1e6, double.NaN),
        InputPowerDbm = Parse(InputPowerDbm, 1, double.NaN),
        TemperatureK = Parse(TemperatureK, 1, double.NaN),
        RequiredSnrDb = Parse(RequiredSnrDb, 1, double.NaN),
        Stages = [.. Stages.Select(s => s.ToStage())],
        Blockers = ParseBlockers() ?? [new ChainBlocker(double.NaN, double.NaN)],
        Demo = Demo,
    };

    /// <summary>An edit that is an undo step of its own: a stage added, inserted, removed or moved.</summary>
    private void Changed() => Edited(null);

    // ── undo and redo ────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Every edit is undoable, and undoing back to the document as it was opened or last saved makes it clean again.
    // The history is SNAPSHOTS of the whole document as the window holds it — the system inputs AS TYPED (a half-typed
    // "2." must come back as "2.", not as the NaN it parses to), every stage's name, part and parameters, and which
    // stage was selected — so an undo restores exactly what was on screen and no edit needs an inverse of its own. A
    // chain is a few dozen strings; a snapshot per step costs nothing worth counting.
    //
    // Keystrokes coalesce: a run of edits to ONE text field (the same key, with nothing else between) is one step, so
    // undo takes a typed value back whole rather than a character at a time. A pick from a list, the Demo box and
    // every structural edit are a step each. Selecting another stage ends a run.

    private const int UndoDepth = 200;

    /// <summary>The document's content, compared to decide dirtiness; the selection rides beside it.</summary>
    private sealed record UndoContent(string Frequency, string Bandwidth, string InputPower, string Temperature,
                                      string RequiredSnr, string Blockers, bool Demo, string Stages);

    private sealed record UndoState(UndoContent Content, int Selected);

    private readonly List<UndoState> _undo = [];
    private readonly List<UndoState> _redo = [];
    private UndoState? _current;
    private UndoContent? _saved;
    private object? _lastKey;
    private bool _restoring;

    /// <summary>A stage is being added, removed or moved: the stage list may clear or move the selection on its own
    /// before the edit is reported, and the undo step must keep the selection the edit was made FROM.</summary>
    private bool _structural;

    /// <summary>Runs a structural edit with the recorded selection held still until it has been reported.</summary>
    private void Structural(Action edit)
    {
        _structural = true;
        try { edit(); }
        finally { _structural = false; }
    }

    private UndoState Capture() => new(new UndoContent(
        Frequency, BandwidthMHz, InputPowerDbm, TemperatureK, RequiredSnrDb, Blockers, Demo,
        System.Text.Json.JsonSerializer.Serialize(Stages.Select(st => st.ToStage()).Select(st => new { st.Name, st.Part, st.Parameters }))),
        SelectedStageIndex);

    /// <summary>A document just opened, made or loaded: nothing to undo, and this content is the clean state.</summary>
    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
        _current = Capture();
        _saved = _current.Content;
        NotifyHistory();
    }

    /// <summary>
    /// An edit has been made. <paramref name="key"/> names the text field it was typed into (a run of edits to one
    /// key is one undo step) or is null for an edit that is a step of its own.
    /// </summary>
    private void Edited(object? key)
    {
        if (_loading || _restoring) return;
        if (key is ChainFileWanted wanted)
        {
            Edited(wanted.Param);
            FileWanted?.Invoke(wanted.Param);
            return;
        }
        var now = Capture();
        if (_current is { } before && !now.Content.Equals(before.Content))
        {
            if (key is null || !Equals(key, _lastKey) || _undo.Count == 0)
            {
                _undo.Add(before);
                if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
            }
            _redo.Clear();
            _lastKey = key;
        }
        _current = now;

        // A typing run that has come back to where it began (a refused value put back) is no step at all.
        if (key is not null && Equals(key, _lastKey) && _undo.Count > 0 && _undo[^1].Content.Equals(now.Content))
        {
            _undo.RemoveAt(_undo.Count - 1);
            _lastKey = null;
        }

        IsDirty = !now.Content.Equals(_saved);
        bool ok = Recompute();
        if (ok) SetErrorKey(null);
        else if (key is null && _hasGood && _undo.Count > 0)
        {
            // A pick or a structural edit the budget refuses is not made: back to the chain before it.
            string why = Refusals;
            var back = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(back);
            Refusals = "Not applied: " + why;
            return;
        }
        else SetErrorKey(key);
        NotifyHistory();
    }

    private bool CanUndo() => _undo.Count > 0;
    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Step(_undo, _redo);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Step(_redo, _undo);

    private void Step(List<UndoState> from, List<UndoState> to)
    {
        if (from.Count == 0) return;
        to.Add(Capture());
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        Restore(state);
    }

    private void Restore(UndoState state)
    {
        _restoring = true;
        try
        {
            var c = state.Content;
            (Frequency, BandwidthMHz, InputPowerDbm, TemperatureK, RequiredSnrDb, Blockers, Demo) =
                (c.Frequency, c.Bandwidth, c.InputPower, c.Temperature, c.RequiredSnr, c.Blockers, c.Demo);
            Stages.Clear();
            foreach (var st in System.Text.Json.JsonSerializer.Deserialize<List<ChainStage>>(c.Stages) ?? [])
                Stages.Add(new ChainStageViewModel(st, Edited));
            SelectedStage = state.Selected >= 0 && state.Selected < Stages.Count ? Stages[state.Selected] : null;
        }
        finally { _restoring = false; }
        _current = state;
        _lastKey = null;
        IsDirty = !state.Content.Equals(_saved);
        Recompute();
        NotifyHistory();
    }

    /// <summary>What Undo and Redo would do, for their tooltips; and their enabled state.</summary>
    public string UndoTip => _undo.Count > 0 ? "Undo  (Ctrl+Z)" : "Nothing to undo";
    public string RedoTip => _redo.Count > 0 ? "Redo  (Ctrl+Shift+Z)" : "Nothing to redo";

    private void NotifyHistory()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
    }

    private static string D(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";
    private static string Dbm(double v) => double.IsFinite(v) ? D(v) + " dBm" : "none (linear)";

    /// <summary>The cascade, again, from what the rows say now.</summary>
    /// <summary>
    /// The cascade, again, from what the rows say now — and true when the chain evaluated. When it did not and an
    /// earlier chain did, NOTHING on screen changes but the refusal: the blocks, the schematic, the table and the plots
    /// keep that last good chain, so a value half-typed or mistyped never blanks them (owner, 2026-11-10). A chain
    /// that has never evaluated (a file opened with a bad value in it) shows its refusal as it always did.
    /// </summary>
    public bool Recompute()
    {
        var d = ToDesign();
        var r = ChainBudget.Evaluate(d);
        if (r.Totals is null && _hasGood)
        {
            Refusals = string.Join("\n", r.Refusals);
            return false;
        }
        Budget = r;
        foreach (var (row, s) in Stages.Zip(r.Stages))
            row.Summary = $"On its own: G {D(s.Stage.GainDb)} dB · NF {D(s.Stage.NfDb)} dB · IIP3 {Dbm(s.Stage.Iip3Dbm)} · IP1dB {Dbm(s.Stage.Ip1dbDbm)}";
        Diagram = ChainBenches.Diagram(d, r);
        Blocks(r.Design ?? d, r);
        RefillCascade(r);
        RefillLevels(d, r);
        RefillCanceller(d);
        PlotsUpdated?.Invoke(this, EventArgs.Empty);
        Refusals = string.Join("\n", r.Refusals);
        Notes = string.Join("\n", r.Notes);
        Summary = r.Totals is not { } t ? "" :
            $"Gain {D(t.GainDb)} dB    NF {D(t.NfDb)} dB (Te {t.TeK:0.#} K)    IIP3 {D(t.Iip3Dbm)} dBm    OIP3 {D(t.Oip3Dbm)} dBm    " +
            $"IP1dB {D(t.Ip1dbDbm)} dBm    OP1dB {D(t.Op1dbDbm)} dBm    Pout {D(t.PoutDbm)} dBm\n" +
            $"Thermal floor {D(t.ThermalFloorDbm)} dBm    Output noise {D(t.NoiseOutDbm)} dBm    MDS {D(t.MdsDbm)} dBm    " +
            $"Sensitivity {D(t.SensitivityDbm)} dBm    SNR {D(t.SnrDb)} dB    SFDR {D(t.SfdrDb)} dB" +
            (t.ArrayGainDb > 0 ? $"    Array gain {D(t.ArrayGainDb)} dB" : "") +
            (Math.Abs(t.OutputFrequencyHz - d.FrequencyHz) > 0 ? $"    Output at {t.OutputFrequencyHz / 1e6:0.###} MHz" : "");
        if (r.Totals is null) return false;
        MarkGood();
        return true;
    }

    // ── refusing bad input ────────────────────────────────────────────────────────────────────────────────────────
    //
    // An edit is accepted only if the whole chain still evaluates. A TEXT field cannot be refused keystroke by
    // keystroke — "-", "2." and "2.4 G" are all on the way to good values — so while one is being typed into, a refused
    // chain leaves the views on the last good one and draws the box invalid, with the budget's reason in the Chain
    // pane; when the box is left (focus moves, or Enter) a value still refused is put back to the last good one. A pick
    // from a list, the Demo box and a stage added, removed or moved are refused at once: the edit is undone and the
    // reason shown. So the chain the window shows is never one the budget refuses.

    private bool _hasGood;
    private (string Frequency, string Bandwidth, string InputPower, string Temperature, string RequiredSnr, string Blockers) _goodInputs;

    /// <summary>The field drawn invalid, if any: a system input's name, a parameter, or (stage, "Name").</summary>
    private object? _errorKey;

    public bool FrequencyInvalid => Equals(_errorKey, nameof(Frequency));
    public bool BandwidthInvalid => Equals(_errorKey, nameof(BandwidthMHz));
    public bool InputPowerInvalid => Equals(_errorKey, nameof(InputPowerDbm));
    public bool TemperatureInvalid => Equals(_errorKey, nameof(TemperatureK));
    public bool RequiredSnrInvalid => Equals(_errorKey, nameof(RequiredSnrDb));
    public bool BlockersInvalid => Equals(_errorKey, nameof(Blockers));

    /// <summary>The chain evaluated: every field's value now is its last good one.</summary>
    private void MarkGood()
    {
        _hasGood = true;
        _goodInputs = (Frequency, BandwidthMHz, InputPowerDbm, TemperatureK, RequiredSnrDb, Blockers);
        foreach (var st in Stages)
        {
            st.NameLastGood = st.Name;
            foreach (var p in st.Parameters) p.LastGood = p.Value;
        }
        SetErrorKey(null);
    }

    private void SetErrorKey(object? key)
    {
        if (Equals(_errorKey, key)) return;
        Flag(_errorKey, false);
        _errorKey = key;
        Flag(key, true);
        foreach (var n in new[] { nameof(FrequencyInvalid), nameof(BandwidthInvalid), nameof(InputPowerInvalid),
                                  nameof(TemperatureInvalid), nameof(RequiredSnrInvalid), nameof(BlockersInvalid) })
            OnPropertyChanged(n);

        static void Flag(object? key, bool on)
        {
            if (key is ChainParamViewModel p) p.Invalid = on;
            else if (key is ValueTuple<ChainStageViewModel, string> (var st, _)) st.NameInvalid = on;
        }
    }

    /// <summary>
    /// The field being typed into is left: a value the chain still refuses goes back to its last good one. Called
    /// when a box loses the focus or takes Enter, and before Save and Transfer, so neither ever writes a refused chain.
    /// </summary>
    public void CommitEdits()
    {
        switch (_errorKey)
        {
            case ChainParamViewModel p when Stages.Any(st => st.Parameters.Contains(p)): p.Value = p.LastGood; break;
            case ValueTuple<ChainStageViewModel, string> (var st, _) when Stages.Contains(st): st.Name = st.NameLastGood; break;
            case nameof(Frequency): Frequency = _goodInputs.Frequency; break;
            case nameof(BandwidthMHz): BandwidthMHz = _goodInputs.Bandwidth; break;
            case nameof(InputPowerDbm): InputPowerDbm = _goodInputs.InputPower; break;
            case nameof(TemperatureK): TemperatureK = _goodInputs.Temperature; break;
            case nameof(RequiredSnrDb): RequiredSnrDb = _goodInputs.RequiredSnr; break;
            case nameof(Blockers): Blockers = _goodInputs.Blockers; break;
        }
        SetErrorKey(null);
    }

    /// <summary>
    /// The block view, idealised: one block per stage with its System block's symbol, its own numbers and the cascade
    /// at its output. An array is drawn compactly — the branch's stages once, each marked "× N", into one combiner block
    /// — and a combiner as its variant: Direct one N:1 symbol, a tree its levels of 2:1 (a deep one as "× levels"), an
    /// amplifier after each when active.
    /// </summary>
    private void Blocks(ChainDesign d, ChainBudgetResult r)
    {
        int cmb = d.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        int n = cmb >= 0 ? ChainParts.ReadCombiner(d.Stages[cmb]).N : 1;
        for (int i = 0; i < Stages.Count && i < d.Stages.Count; i++)
        {
            var row = Stages[i];
            var st = d.Stages[i];
            row.TimesN = i < cmb ? $"× {n}" : "";
            row.Glyphs = Glyphs(st);
            row.Extra = st.Part switch
            {
                ChainParts.Mixer => $"LO {ChainValue.Or(st.Get("Flo"), 0) / 1e6:0.###} MHz",
                ChainParts.PhaseShifter => $"φ {st.Get("Phase")}°",
                ChainParts.Combiner => ChainParts.ReadCombiner(st).Describe()
                                       + (ChainValue.Or(st.Get("Steer"), 0) != 0 ? $", steer {st.Get("Steer")}°" : ""),
                ChainParts.Splitter => $"1:{st.Get("N")}",
                ChainParts.Snp => Path.GetFileName(ChainSnp.FileText(st)),
                ChainParts.Canceller => CancellerSpec.Read(st) is var cs
                    ? $"depth {cs.DepthDb:0.#} dB, residual {cs.ResidualDbm:0.#} dBm" : "",
                _ => "",
            };
            if (i < r.Stages.Count)
            {
                var s = r.Stages[i];
                row.KeyLine = $"G {D(s.Stage.GainDb)}  NF {D(s.Stage.NfDb)}\n" +
                              (double.IsFinite(s.Stage.Iip3Dbm) ? $"IIP3 {D(s.Stage.Iip3Dbm)}  P1 {D(s.Stage.Ip1dbDbm)}" : "linear");
                row.CascadeLine = $"Σ G {D(s.GainDb)}  NF {D(s.NfDb)}\nΣ IIP3 {D(s.Iip3Dbm)}  SNR {D(s.SnrDb)}";
            }
            else row.KeyLine = row.CascadeLine = "";
        }
    }

    private static IReadOnlyList<ChainBlockGlyph> Glyphs(ChainStage st)
    {
        if (st.Part == ChainParts.Combiner)
        {
            var c = ChainParts.ReadCombiner(st);
            var g = new List<ChainBlockGlyph>();
            void Level(string caption)
            {
                g.Add(new ChainBlockGlyph(SymbolKind.Combiner, caption));
                if (c.Active) g.Add(new ChainBlockGlyph(SymbolKind.Amp, ""));
            }
            if (!c.Tree) Level($"{c.N}:1");
            else if (c.Levels.Count <= 3) foreach (var l in c.Levels) Level($"{l.Parts} × 2:1");
            else Level($"2:1, × {c.Levels.Count} levels");
            return g;
        }
        // A filter draws its band form, as the placed Filter block does: the low-pass and high-pass glyphs, not the
        // band-pass default.
        if (st.Part == ChainParts.Filter)
        {
            var form = Enum.TryParse<NetworkForm>(st.Get("Form").Trim(), true, out var f) ? f : NetworkForm.Bandpass;
            return [new ChainBlockGlyph(SymbolKind.Filter, "", BuiltInSymbols.PrimitivesForFilter(form).Primitives)];
        }
        var kind = st.Part switch
        {
            ChainParts.Mixer => SymbolKind.Mixer, ChainParts.Atten => SymbolKind.Atten, ChainParts.Filter => SymbolKind.Filter,
            ChainParts.Switch => SymbolKind.Switch, ChainParts.Circulator => SymbolKind.Circulator,
            ChainParts.Coupler => SymbolKind.Coupler, ChainParts.PhaseShifter => SymbolKind.Tline,
            ChainParts.Limiter => SymbolKind.Diode, ChainParts.Splitter => SymbolKind.Combiner,
            ChainParts.Canceller => SymbolKind.Coupler, ChainParts.Snp => SymbolKind.Snp, _ => SymbolKind.Amp,
        };
        return [new ChainBlockGlyph(kind, st.Part == ChainParts.Splitter ? $"1:{st.Get("N")}" : "")];
    }

    // ── the stage list ───────────────────────────────────────────────────────

    [RelayCommand]
    private void AddStage() =>
        InsertStage(PartToAdd, SelectedStage is { } sel ? Stages.IndexOf(sel) + 1 : Stages.Count);

    /// <summary>
    /// A new stage of <paramref name="part"/>, named after the part and numbered past every name the chain already has,
    /// inserted before the stage now at <paramref name="insertBefore"/> (<see cref="Stages"/>' count puts it last), and
    /// selected: the toolbar's Add Stage, after the selected stage, and the Blocks view's Insert Block, where it was
    /// right-clicked.
    /// </summary>
    public ChainStageViewModel InsertStage(ChainPart part, int insertBefore)
    {
        string stem = part.Token switch
        {
            ChainParts.Amp => "AMP", ChainParts.Mixer => "MIX", ChainParts.Atten => "ATT", ChainParts.Filter => "FLT",
            ChainParts.Switch => "SW", ChainParts.Circulator => "ISO", ChainParts.Coupler => "CPL",
            ChainParts.PhaseShifter => "PS", ChainParts.Limiter => "LIM", ChainParts.Splitter => "SPL",
            ChainParts.Canceller => "CANC", ChainParts.Snp => "SNP", _ => "CMB",
        };
        int n = 1;
        while (Stages.Any(s => s.Name.Equals($"{stem}{n}", StringComparison.OrdinalIgnoreCase))) n++;
        var row = new ChainStageViewModel(new ChainStage { Name = $"{stem}{n}", Part = part.Token }, Edited);
        // The edit is reported BEFORE the selection moves, so the undo step keeps the selection the edit was made from.
        Structural(() => { Stages.Insert(Math.Clamp(insertBefore, 0, Stages.Count), row); Edited(row.FileWanted()); });
        SelectedStage = row;
        return row;
    }

    [RelayCommand]
    private void RemoveStage()
    {
        if (SelectedStage is { } sel) Remove(sel);
    }

    /// <summary>Removes <paramref name="stage"/>, the toolbar's Remove on the selected one and the Blocks view's Remove
    /// Block on the one right-clicked; the selection moves to the stage that took its place.</summary>
    public void Remove(ChainStageViewModel stage)
    {
        int at = Stages.IndexOf(stage);
        if (at < 0) return;
        Structural(() => { Stages.Remove(stage); Changed(); });
        SelectedStage = Stages.Count == 0 ? null : Stages[Math.Min(at, Stages.Count - 1)];
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
        Structural(() => { Stages.Move(at, to); Changed(); });
        SelectedStage = sel;
    }

    /// <summary>
    /// Moves <paramref name="stage"/> so that it lands before the stage now at <paramref name="insertBefore"/>
    /// (<see cref="Stages"/>' count puts it last): a block dragged in the Blocks view and dropped. It stays selected, and
    /// a drop where it already is changes nothing.
    /// </summary>
    public void MoveStage(ChainStageViewModel stage, int insertBefore)
    {
        int at = Stages.IndexOf(stage);
        if (at < 0) return;
        int to = Math.Clamp(insertBefore > at ? insertBefore - 1 : insertBefore, 0, Stages.Count - 1);
        if (to == at) return;
        Structural(() => { Stages.Move(at, to); Changed(); });
        SelectedStage = stage;
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
    public async Task<bool> Save()
    {
        CommitEdits();
        return FilePath is { } p ? Write(p) : await SaveAs();
    }

    [RelayCommand]
    private async Task<bool> SaveAs()
    {
        CommitEdits();
        if (PickSavePathAsync is null || await PickSavePathAsync(FilePath) is not { } path) return false;
        return Write(path);
    }

    private bool Write(string path)
    {
        try
        {
            ChainDesignIo.SaveToFile(path, ToDesign());
            FilePath = Path.GetFullPath(path);
            _saved = Capture().Content;
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
