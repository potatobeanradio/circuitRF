// ================================================================
//  YieldPanelViewModel.Corners.cs  —  Corners mode (brief-yield-10
//  R-ya10-9, yield overview D10)
//
//  The corner list is the setup's corner lines; enabling one is one
//  undo step, and Generate… is YA-6's CornerGenerator in a small dialog
//  that shows the count before it writes. Run evaluates every enabled
//  corner through CornerRun — the object `yield corners` drives — or,
//  with "MC at each corner", a Monte Carlo at each; the result is a
//  corner × goal grid: the margin (or the yield) in each cell, failing
//  cells marked, each goal's worst corner in bold.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Yield;

public sealed partial class YieldPanelViewModel
{
    /// <summary>The kit corner axes the workspace offers — what Generate… crosses.</summary>
    public Func<SchematicViewModel, IReadOnlyList<WorkspaceCornerAxis>>? KitAxesFor { get; set; }

    public ObservableCollection<YieldCornerRowViewModel> Corners { get; } = [];

    public bool HasCorners => Corners.Count > 0;

    /// <summary>A Monte Carlo at each corner instead of one evaluation (YA-6 R-ya6-3) — session state, as <c>--mc</c>.</summary>
    [ObservableProperty] private bool _monteCarloAtEachCorner;

    /// <summary>The last corner sweep's result; null before one.</summary>
    public CornerResult? CornerResult { get; private set; }

    /// <summary>The grid's columns: the goals scored.</summary>
    public ObservableCollection<string> CornerGoals { get; } = [];

    /// <summary>The grid: one row per corner, the nominal first.</summary>
    public ObservableCollection<CornerGridRow> CornerGrid { get; } = [];

    public bool HasCornerGrid => CornerGrid.Count > 0;

    private void RefreshCorners()
    {
        var corners = Setup?.Corners ?? [];
        while (Corners.Count > corners.Count) Corners.RemoveAt(Corners.Count - 1);
        for (int i = 0; i < corners.Count; i++)
        {
            if (i < Corners.Count) Corners[i].Bind(i, corners[i]);
            else Corners.Add(new YieldCornerRowViewModel(this, i, corners[i]));
        }
    }

    internal void SetCornerEnabled(YieldCornerRowViewModel row, bool enabled)
    {
        if (Setup is not { } setup || row.Index >= setup.Corners.Count) return;
        var corners = setup.Corners.Select(c => c.Clone()).ToList();
        corners[row.Index].Enabled = enabled;
        Execute(TuningSetupEditsCorners(corners), enabled ? $"Enable corner {row.Name}" : $"Disable corner {row.Name}");
    }

    internal void RemoveCorner(YieldCornerRowViewModel row)
    {
        if (Setup is not { } setup || row.Index >= setup.Corners.Count) return;
        var corners = setup.Corners.Select(c => c.Clone()).ToList();
        corners.RemoveAt(row.Index);
        Execute(TuningSetupEditsCorners(corners), $"Remove corner {row.Name}");
    }

    private TuningSetup TuningSetupEditsCorners(IEnumerable<CornerDefinition> corners)
        => CircuitRF.Design.Optimization.TuningSetupEdits.WithCorners(Setup, corners);

    // ---- Generate… (YA-6's generator) --------------------------------------------------

    /// <summary>Raised to show the generator; the view shows it and the editor's Write adds the corners.</summary>
    public event EventHandler<CornerGeneratorViewModel>? GeneratorRequested;

    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void GenerateCorners() => OpenGenerator();

    /// <summary>The generator over the workspace's kit axes and the design's global variables, wired to append what it
    /// writes as one undo step. Raised to the view, and returned.</summary>
    public CornerGeneratorViewModel? OpenGenerator()
    {
        if (_tuned is null) return null;
        var axes = KitAxesFor?.Invoke(_tuned) ?? [];
        var editor = new CornerGeneratorViewModel(axes);
        editor.Written += generated => editor.Refusal = AddCorners(generated);
        GeneratorRequested?.Invoke(this, editor);
        return editor;
    }

    /// <summary>Appends <paramref name="generated"/>, a name the setup already uses taking a numeric suffix.</summary>
    internal string? AddCorners(IReadOnlyList<CornerDefinition> generated)
    {
        if (generated.Count == 0) return null;
        var corners = (Setup?.Corners ?? []).Select(c => c.Clone()).ToList();
        var names = corners.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var g in generated)
        {
            var c = g.Clone();
            string name = c.Name;
            for (int k = 2; !names.Add(c.Name); k++) c.Name = $"{name}_{k}";
            corners.Add(c);
        }
        return Execute(TuningSetupEditsCorners(corners), $"Generate {generated.Count} corner{(generated.Count == 1 ? "" : "s")}");
    }

    // ---- Run ---------------------------------------------------------------------------

    private void RunCorners(PreparedCircuit circuit, string? source)
    {
        var cts = new CancellationTokenSource();
        bool mc = MonteCarloAtEachCorner || Mode != YieldMode.Corners;
        var listed = Mode == YieldMode.Corners ? null : Settings.CornerNames;
        string? path = source is null ? null : mc ? StatisticalRun.ResultPathFor(source) : CornerRun.ResultPathFor(source);
        CornerRun? run = null;
        run = CornerRun.Create(circuit, new CornerOptions
        {
            Corners      = listed is null ? null : [.. listed],
            MonteCarlo   = mc,
            Mode         = Mode == YieldMode.MonteCarlo ? StatisticalMode.MonteCarlo : StatisticalMode.Yield,
            Cancellation = cts.Token,
            ResultPath   = path,
            Progress     = new Inline<CornerProgress>(p => { var r = run; PostToUi(() => ApplyCornerProgress(r, p)); }),
        });
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        _cornerRun  = run;
        _cts        = cts;
        _resultPath = path;
        _display    = path is null ? null : DisplayFor?.Invoke(path);
        StatusText  = "";
        State       = YieldRunState.Running;
        HasRunReadouts = true;
        TrialsDoneText = $"0 / {run.Corners.Count + 1}";

        _task = StartBackground(() =>
        {
            CornerResult? result = null;
            string? crash = null;
            try { result = run.Run(); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() => FinishCorners(run, result, crash));
        });
    }

    private void ApplyCornerProgress(CornerRun? run, CornerProgress p)
    {
        if (run is null || !ReferenceEquals(run, _cornerRun)) return;
        TrialsDoneText = $"{p.Done} / {p.Total}";
        ElapsedText = Clock(p.Elapsed);
        EtaText = p.Done > 0 && p.Done < p.Total ? Clock(TimeSpan.FromTicks(p.Elapsed.Ticks / p.Done * (p.Total - p.Done))) + " left" : "";
    }

    private void FinishCorners(CornerRun run, CornerResult? result, string? crash)
    {
        if (!ReferenceEquals(run, _cornerRun)) return;
        EtaText = "";
        if (result is null || result.Outcome is StatisticalOutcome.Cancelled or StatisticalOutcome.Refused)
        {
            State = YieldRunState.Idle;
            if (result?.Outcome == StatisticalOutcome.Cancelled) StatusText = "Cancelled";
            else Problem(result is null ? $"The run failed: {crash}" : $"Refused: {result.Refusal?.Render()}");
            _display?.Drop();
            return;
        }

        CornerResult = result;
        if (result.Data is { } data) _lastData = data;
        // Without a Monte Carlo there is one simulation per corner and no trial to plot: the grid is the result.
        bool trials = result.Data?.ContainsGroup(StatisticalDataSet.TrialsGroup) == true;
        DisplayUnavailable = trials ? null
            : "No trials to plot: without MC at each corner a corner run is one simulation per corner, and the corner " +
              "grid is its result. Tick MC at each corner, or choose Yield, for a yield display.";
        State = YieldRunState.Finished;
        if (result.WrittenPath is { } written) _display?.Written(written);
        FillCornerGrid(result);

        var unmet = result.Worst.Where(w => !w.Met).Select(w => $"{w.Goal} at {w.Corner}").ToList();
        StatusText = result.Yields.Count > 0
            ? $"{result.Yields.Count} corners · lowest yield at {result.WorstYield}"
            : unmet.Count == 0 ? $"{result.Corners.Count} corners · every goal met" : $"Not met: {string.Join(", ", unmet)}";
        ReportMessages?.Invoke($"Corners: {StatusText}", [.. result.Notes.Select(n => n.Render())]);
        if (trials && !_offeredDisplay && _resultPath is { } path)
        {
            _offeredDisplay = true;
            OfferYieldDisplay = HasYieldDisplay?.Invoke(path, result.Data) != true;
        }
        NotifyRunCommands();
    }

    private void FillCornerGrid(CornerResult r)
    {
        CornerGoals.Clear();
        CornerGrid.Clear();
        if (r.Yields.Count > 0)
        {
            // A Monte Carlo at each corner: the yield per goal at each.
            var goals = r.Yields.SelectMany(y => y.Result.Goals.Select(g => g.Goal)).Distinct().ToList();
            foreach (var g in goals) CornerGoals.Add(g);
            var lowest = goals.ToDictionary(g => g, g => r.Yields
                .Select(y => (y.Name, Y: y.Result.Goals.FirstOrDefault(x => x.Goal == g)?.Estimate.Yield ?? double.NaN))
                .Where(x => !double.IsNaN(x.Y)).OrderBy(x => x.Y).Select(x => x.Name).FirstOrDefault());
            foreach (var y in r.Yields)
                CornerGrid.Add(new CornerGridRow(y.Name, [.. goals.Select(g =>
                {
                    var e = y.Result.Goals.FirstOrDefault(x => x.Goal == g)?.Estimate;
                    bool below = e is { } est && y.Result.Settings?.Target is { } t && est.Yield < t / 100;
                    return new CornerGridCell(e is { Counted: > 0 } v ? YieldGoalRowViewModel.Percent(v.Yield) : "—", below, lowest[g] == y.Name);
                })]));
        }
        else
        {
            foreach (var g in r.Goals) CornerGoals.Add(g.Name);
            foreach (var c in r.Corners)
                CornerGrid.Add(new CornerGridRow(c.Name, [.. r.Goals.Select(g =>
                {
                    if (!c.Evaluated) return new CornerGridCell("—", true, false);
                    var s = c.Goals.FirstOrDefault(x => x.Name == g.Name);
                    bool worst = r.Worst.FirstOrDefault(w => w.Goal == g.Name)?.Corner == c.Name;
                    return s is null ? new CornerGridCell("", false, false)
                        : new CornerGridCell(double.IsFinite(s.Margin) ? s.Margin.ToString("G3", CultureInfo.InvariantCulture) : "—", !s.Met, worst);
                })]));
        }
        OnPropertyChanged(nameof(HasCornerGrid));
    }
}

/// <summary>One corner line in the list.</summary>
public sealed partial class YieldCornerRowViewModel : ObservableObject
{
    private readonly YieldPanelViewModel _panel;
    private bool _syncing;

    internal YieldCornerRowViewModel(YieldPanelViewModel panel, int index, CornerDefinition corner)
    {
        _panel = panel;
        Bind(index, corner);
    }

    public int Index { get; private set; }
    public CornerDefinition Corner { get; private set; } = new();
    public string Name => Corner.Name;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing) _panel.SetCornerEnabled(this, value);
    }

    /// <summary>The kit sections it selects, <c>ss, slow</c>.</summary>
    public string KitText => Corner.AxisSelections is { Count: > 0 } a ? string.Join(", ", a.Values) : "";

    public string TempText => Corner.Temp is { } t ? $"{t} °C" : "";

    public string ValuesText => string.Join(", ", Corner.Values.Select(kv => $"{kv.Key}={kv.Value}"));

    public bool IsStatistical => Corner.IsStatistical;

    /// <summary><c>trial 417</c> for a statistical corner.</summary>
    public string TrialText => Corner.Trial is { } t ? $"trial {t}" : "";

    internal void Bind(int index, CornerDefinition corner)
    {
        Index = index;
        Corner = corner;
        _syncing = true;
        IsEnabled = corner.Enabled;
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand] private void Remove() => _panel.RemoveCorner(this);
}

/// <summary>One row of the corner × goal grid.</summary>
public sealed record CornerGridRow(string Corner, IReadOnlyList<CornerGridCell> Cells);

/// <summary>A cell: the margin (or yield), whether the goal fails there, whether this is the goal's worst corner.</summary>
public sealed record CornerGridCell(string Text, bool Fails, bool IsWorst);

/// <summary>
/// Generate… (R-ya10-9): YA-6's <see cref="CornerGenerator.CrossProduct"/> over the kit options ticked, the
/// temperatures and the variable values typed — showing the count before anything is written.
/// </summary>
public sealed partial class CornerGeneratorViewModel : ObservableObject
{
    public CornerGeneratorViewModel(IReadOnlyList<WorkspaceCornerAxis> axes)
    {
        foreach (var a in axes) Axes.Add(new GeneratorAxisRow(this, a));
        Recompute();
    }

    public ObservableCollection<GeneratorAxisRow> Axes { get; } = [];

    public bool HasAxes => Axes.Count > 0;

    /// <summary><c>-40, 25, 85</c> (°C).</summary>
    [ObservableProperty] private string _temperatures = "";

    /// <summary><c>Vdd=3.0 V,3.6 V; Rbias=1k,2k</c>.</summary>
    [ObservableProperty] private string _values = "";

    /// <summary><c>12 corners</c>, or the generator's refusal.</summary>
    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private string? _refusal;

    private IReadOnlyList<CornerDefinition> _generated = [];

    public event Action<IReadOnlyList<CornerDefinition>>? Written;

    partial void OnTemperaturesChanged(string value) => Recompute();
    partial void OnValuesChanged(string value) => Recompute();

    internal void Recompute()
    {
        var axes = Axes.Select(a => new GeneratorAxis(a.Axis.Key, a.Axis.Label,
            [.. a.Options.Where(o => o.IsChecked).Select(o => o.Name)])).Where(a => a.Options.Count > 0).ToList();
        var temps = Split(Temperatures, ',');
        var values = new List<GeneratorValues>();
        foreach (var part in Split(Values, ';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) { CountText = $"'{part}' is not name=value,value"; _generated = []; WriteCommand.NotifyCanExecuteChanged(); return; }
            values.Add(new GeneratorValues(part[..eq].Trim(), Split(part[(eq + 1)..], ',')));
        }
        var gen = CornerGenerator.CrossProduct(axes, temps, values);
        _generated = gen.Corners;
        CountText = gen.Refusal is { } r ? r.Render() : $"{gen.Corners.Count} corner{(gen.Corners.Count == 1 ? "" : "s")}";
        WriteCommand.NotifyCanExecuteChanged();
    }

    private static List<string> Split(string text, char by)
        => [.. text.Split(by, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private bool CanWrite() => _generated.Count > 0;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void Write() => Written?.Invoke(_generated);

    /// <summary>The corners the current choices generate.</summary>
    public IReadOnlyList<CornerDefinition> Generated => _generated;
}

public sealed class GeneratorAxisRow
{
    internal GeneratorAxisRow(CornerGeneratorViewModel owner, WorkspaceCornerAxis axis)
    {
        Axis = axis;
        foreach (var o in axis.Options) Options.Add(new GeneratorOption(owner, o));
    }

    public WorkspaceCornerAxis Axis { get; }
    public string Label => Axis.Label;
    public ObservableCollection<GeneratorOption> Options { get; } = [];
}

public sealed partial class GeneratorOption(CornerGeneratorViewModel owner, string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.Recompute();
}
