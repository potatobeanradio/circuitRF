// ================================================================
//  YieldPanelViewModel.Run.cs  —  Run / Pause / Stop, the live yield
//  readout, the trial table and what is done with a trial, the
//  contributions and the one-click yield display
//  (brief-yield-10 R-ya10-6…R-ya10-8, R-ya10-10; yield overview D14)
//
//  THE PANEL CALLS StatisticalRun AND NOTHING ELSE (R-ya10-10). The run
//  is created exactly as `circuitrf yield` creates it — the prepared
//  circuit, the schematic's setup, the result written beside the design
//  as <design>.yield.npy — on a background thread; its progress and its
//  frames are posted to the UI thread and nothing else crosses. The
//  trial table is read from the frames, so it is the same function of
//  the same cubes during the run and after it.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;

namespace CircuitRF.Ui.Yield;

/// <summary>Where a run's frames go: every open Data Display bound to the result, under its path (tuning D8).</summary>
public interface IYieldDisplay
{
    /// <summary>Shows a frame in place of the result file (UI thread). Only a source already loaded takes it.</summary>
    void Publish(DataSet data);

    /// <summary>The run wrote <paramref name="path"/>: the displays re-read it and drop what was published.</summary>
    void Written(string path);

    /// <summary>Drops whatever was published; the displays return to the file.</summary>
    void Drop();
}

/// <summary>Where a run is.</summary>
public enum YieldRunState { Idle, Running, Paused, Finished }

public sealed partial class YieldPanelViewModel : ITrialSelectionListener
{
    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The bench as Simulate would netlist it, prepared once; null when it cannot be.</summary>
    public Func<SchematicViewModel, PreparedCircuit?>? PrepareCircuit { get; set; }

    /// <summary>The file the design is — a run's result is written beside it (<see cref="StatisticalRun.ResultPathFor"/>);
    /// null writes nothing.</summary>
    public Func<SchematicViewModel, string?>? SourcePathFor { get; set; }

    /// <summary>The displays a run publishes to; null publishes nothing.</summary>
    public Func<string, IYieldDisplay?>? DisplayFor { get; set; }

    /// <summary>Hands work to the UI thread. Inline by default (tests).</summary>
    public Action<Action> PostToUi { get; set; } = a => a();

    /// <summary>Starts the run's thread.</summary>
    public Func<Action, Task> StartBackground { get; set; } = a => Task.Run(a);

    /// <summary>Send trial to Tuning: the trial's values into the Tuning sliders (YA-9 R-ya9-6).</summary>
    public Action<IReadOnlyDictionary<string, string>, string>? SendToTuningTarget { get; set; }

    /// <summary>Re-run trial: simulated afresh and shown as every bound plot's snapshot; returns a refusal's sentence.</summary>
    public Func<string, int, Task<string?>>? RerunTrial { get; set; }

    /// <summary>Copy values: puts text on the clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Save as corner…: asks for a name and adds the statistical corner.</summary>
    public Func<string, int, DataSet, Task>? SaveTrialAsCorner { get; set; }

    /// <summary>Open yield display: creates (or focuses) the display over the result.</summary>
    public Func<string, DataSet, Task>? OpenYieldDisplay { get; set; }

    /// <summary>Whether a yield display over the result already exists — the one-time offer is made only when not.</summary>
    public Func<string, DataSet?, bool>? HasYieldDisplay { get; set; }

    // ---- Run state -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPaused), nameof(IsFinished), nameof(IsRunActive),
                              nameof(PauseResumeTip))]
    private YieldRunState _state;

    public bool IsRunning   => State == YieldRunState.Running;
    public bool IsPaused    => State == YieldRunState.Paused;
    public bool IsFinished  => State == YieldRunState.Finished;
    public bool IsRunActive => State is YieldRunState.Running or YieldRunState.Paused;

    public string PauseResumeTip => IsPaused ? "Resume" : "Pause — finishes the trials in flight, then holds";

    partial void OnStateChanged(YieldRunState value) => NotifyRunCommands();

    private StatisticalRun?          _run;
    private CornerRun?               _cornerRun;
    private IYieldDisplay?           _display;
    private CancellationTokenSource? _cts;
    private Task?                    _task;
    private string?                  _resultPath;
    private DataSet?                 _lastData;
    private IReadOnlyList<GoalYield>? _goalYields;
    private Dictionary<string, double>? _shares;
    private bool                     _offeredDisplay;

    /// <summary>The background work of the current run; null when none.</summary>
    public Task? RunTask => _task;

    /// <summary>The result of the last Monte Carlo or yield run that finished; null before one.</summary>
    public StatisticalResult? Result { get; private set; }

    /// <summary>The run's DataSet — the newest frame while it runs, the file's content after.</summary>
    public DataSet? Data => _lastData;

    /// <summary>Where the last run wrote (or is writing) its result.</summary>
    public string? ResultPath => _resultPath;

    /// <summary>Frames the run handed the display (D14's live redraw — the display coalesces them).</summary>
    public long Frames { get; private set; }

    // ---- Readouts (R-ya10-6) ---------------------------------------------------

    [ObservableProperty] private bool _hasRunReadouts;

    /// <summary><c>88.5 %</c>.</summary>
    [ObservableProperty] private string _yieldText = "";

    /// <summary><c>86.0 – 90.7 %</c> at the run's confidence.</summary>
    [ObservableProperty] private string _intervalText = "";

    /// <summary>The yield and its interval as fractions, for the bar.</summary>
    [ObservableProperty] private double _yieldEstimate;
    [ObservableProperty] private double _yieldLower;
    [ObservableProperty] private double _yieldUpper;
    [ObservableProperty] private bool _hasYield;

    /// <summary><c>312 / 500</c>.</summary>
    [ObservableProperty] private string _trialsDoneText = "";

    /// <summary>How many trials did not evaluate; empty for none.</summary>
    [ObservableProperty] private string _didNotEvaluateText = "";

    /// <summary><c>1:08</c>.</summary>
    [ObservableProperty] private string _elapsedText = "";

    /// <summary><c>0:42 left</c> while running; empty otherwise.</summary>
    [ObservableProperty] private string _etaText = "";

    /// <summary>The target as a fraction — the bar's marker; null for none, or outside yield mode.</summary>
    public double? TargetFraction => Mode is YieldMode.Yield or YieldMode.Centering && Settings.Target is { } t ? t / 100 : null;

    public bool HasTarget => TargetFraction is not null;

    /// <summary>The one-time offer at the end of the first run on a schematic with no yield display (R-ya10-8).</summary>
    [ObservableProperty] private bool _offerYieldDisplay;

    /// <summary>Why the last result has no yield display — a corner run without Monte Carlo has no trials — or null.
    /// The button is disabled and says this rather than writing an empty display.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenDisplayTip))]
    private string? _displayUnavailable;

    /// <summary>The yield display button's tooltip: what it opens, or why there is nothing to open.</summary>
    public string OpenDisplayTip => DisplayUnavailable
        ?? "Open yield display — trials, histograms, sensitivity and the statistics table";

    partial void OnDisplayUnavailableChanged(string? value) => OpenDisplayCommand.NotifyCanExecuteChanged();

    private void ClearRunReadouts()
    {
        _run = null;
        _cornerRun = null;
        _display = null;
        _task = null;
        _resultPath = null;
        _lastData = null;
        _goalYields = null;
        _shares = null;
        _offeredDisplay = false;
        DisplayUnavailable = null;
        Result = null;
        CornerResult = null;
        HasRunReadouts = HasYield = OfferYieldDisplay = false;
        YieldText = IntervalText = TrialsDoneText = DidNotEvaluateText = ElapsedText = EtaText = "";
        _allTrials = [];
        Trials.Clear();
        SelectedTrial = null;
        CornerGrid.Clear();
        CornerGoals.Clear();
        ClearCenterReadouts();
        ClearDoeReadouts();
        State = YieldRunState.Idle;
        foreach (var g in Goals) g.ShowYield(null, TargetFraction);
        foreach (var v in Variables) v.ShowShare(null);
    }

    // ---- Run (R-ya10-6) ---------------------------------------------------------

    /// <summary>Starts a Monte Carlo, a yield run or a corner sweep for the mode. A setup that cannot run is refused
    /// with <see cref="StatisticalRun"/>'s own reason in the status line — the sentence <c>yield</c> prints.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (_tuned is null || IsRunActive) return;
        var circuit = PrepareCircuit?.Invoke(_tuned);
        if (circuit is null) { Problem("Refused: the schematic could not be netlisted."); return; }
        if (circuit.ReadError is { } readError) { Problem($"Refused: {readError}"); return; }
        string? source = SourcePathFor?.Invoke(_tuned);

        if (Mode == YieldMode.Centering)
        {
            RunCentering(circuit, source);
            return;
        }
        if (Mode == YieldMode.Doe)
        {
            RunDoe(circuit, source);
            return;
        }
        if (Mode == YieldMode.Corners || Settings.CornerNames is not { Count: 0 })
        {
            RunCorners(circuit, source);
            return;
        }

        var cts = new CancellationTokenSource();
        string? path = source is null ? null : StatisticalRun.ResultPathFor(source);
        StatisticalRun? run = null;
        run = StatisticalRun.Create(circuit, new StatisticalOptions
        {
            Mode         = Mode == YieldMode.MonteCarlo ? StatisticalMode.MonteCarlo : StatisticalMode.Yield,
            // The setup the bench netlists with — the schematic's own tuning block, extracted exactly as `yield`
            // reads it from the file — so the panel's result is the verb's, byte for byte (R-ya10-10).
            Setup        = null,
            Cancellation = cts.Token,
            ResultPath   = path,
            Progress     = new Inline<StatisticalProgress>(p => { var r = run; PostToUi(() => ApplyProgress(r, p)); }),
            Publish      = data => { var r = run; PostToUi(() => ApplyFrame(r, data)); },
        });
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        _run        = run;
        _cts        = cts;
        _resultPath = path;
        _display    = path is null ? null : DisplayFor?.Invoke(path);
        StatusText  = "";
        State       = YieldRunState.Running;
        HasRunReadouts = true;
        TrialsDoneText = $"0 / {run.Settings.EffectiveTrials}";
        foreach (var note in run.Notes) ReportMessages?.Invoke($"Yield: {note.Render()}", []);

        _task = StartBackground(() =>
        {
            StatisticalResult? result = null;
            string? crash = null;
            try { result = run.Run(); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() => Finish(run, result, crash));
        });
    }

    private bool CanRun() => _tuned is not null && !IsRunActive && PrepareCircuit is not null;

    private void ApplyProgress(StatisticalRun? run, StatisticalProgress p)
    {
        if (run is null || !ReferenceEquals(run, _run)) return;
        int cap = run.Settings.EffectiveTrials;
        TrialsDoneText = $"{p.Trials} / {cap}";
        DidNotEvaluateText = p.DidNotEvaluate > 0 ? $"{p.DidNotEvaluate} did not evaluate" : "";
        ElapsedText = Clock(p.Elapsed);
        EtaText = p.Trials > 0 && p.Trials < cap
            ? Clock(TimeSpan.FromTicks(p.Elapsed.Ticks / p.Trials * (cap - p.Trials))) + " left" : "";
        ShowYield(p.Yield, p.Goals);
    }

    private void ShowYield(YieldEstimate overall, IReadOnlyList<GoalYield> goals)
    {
        _goalYields = goals;
        HasYield = overall.Counted > 0 && !double.IsNaN(overall.Yield);
        if (HasYield)
        {
            YieldEstimate = overall.Yield;
            YieldLower    = overall.Lower;
            YieldUpper    = overall.Upper;
            YieldText     = YieldGoalRowViewModel.Percent(overall.Yield);
            IntervalText  = $"{YieldGoalRowViewModel.Percent(overall.Lower)} – {YieldGoalRowViewModel.Percent(overall.Upper)}";
        }
        foreach (var g in Goals) g.ShowYield(goals.FirstOrDefault(y => y.Goal == g.Name)?.Estimate, TargetFraction);
    }

    /// <summary>A frame from the run: the display redraws from it, and the trial table is read from it.</summary>
    private void ApplyFrame(StatisticalRun? run, DataSet data)
    {
        if (run is null || !ReferenceEquals(run, _run)) return;
        _lastData = data;
        Frames++;
        _display?.Publish(data);
        ShowTrialRows(data);
    }

    private void Finish(StatisticalRun run, StatisticalResult? result, string? crash)
    {
        if (!ReferenceEquals(run, _run)) return;            // abandoned meanwhile
        EtaText = "";
        if (result is null)
        {
            State = YieldRunState.Idle;
            Problem($"The run failed: {crash}");
            _display?.Drop();
            return;
        }
        Result = result;
        switch (result.Outcome)
        {
            case StatisticalOutcome.Cancelled:
                State = YieldRunState.Idle;
                StatusText = "Cancelled";
                _display?.Drop();
                return;
            case StatisticalOutcome.Refused:
                State = YieldRunState.Idle;
                Problem($"Refused: {result.Refusal?.Render() ?? result.FinishReason}");
                _display?.Drop();
                return;
        }

        State = YieldRunState.Finished;
        TrialsDoneText = $"{result.Trials} / {run.Settings.EffectiveTrials}";
        DidNotEvaluateText = result.DidNotEvaluate > 0 ? $"{result.DidNotEvaluate} did not evaluate" : "";
        ShowYield(result.Yield, result.Goals);
        if (result.Data is { } data) { _lastData = data; ShowTrialRows(data); }
        if (result.WrittenPath is { } written) _display?.Written(written);

        RefreshKit();
        var (summary, lines) = FinishSummary(result);
        StatusText   = summary;
        StatusDetail = string.Join(Environment.NewLine, lines);
        ReportMessages?.Invoke($"Yield: {summary}", lines);

        if (!_offeredDisplay && _resultPath is { } path && result.Data is not null)
        {
            _offeredDisplay = true;
            OfferYieldDisplay = HasYieldDisplay?.Invoke(path, result.Data) != true;
        }
        NotifyRunCommands();
    }

    /// <summary>The finish reason, the yield and the goals below target — and, for Messages, every note.</summary>
    internal static (string Summary, IReadOnlyList<string> Lines) FinishSummary(StatisticalResult r)
    {
        var parts = new List<string> { char.ToUpperInvariant(r.FinishReason[0]) + r.FinishReason[1..] };
        if (r.Mode == StatisticalMode.Yield && r.Yield.Counted > 0) parts.Add($"yield {YieldGoalRowViewModel.Percent(r.Yield.Yield)}");
        if (r.DidNotEvaluate > 0) parts.Add($"{r.DidNotEvaluate} did not evaluate");
        if (r.Outcome == StatisticalOutcome.BelowTarget) parts.Add("below target");
        var lines = new List<string>();
        foreach (var g in r.Goals)
            lines.Add($"{g.Goal}: {YieldGoalRowViewModel.Percent(g.Estimate.Yield)} " +
                      $"({YieldGoalRowViewModel.Percent(g.Estimate.Lower)} – {YieldGoalRowViewModel.Percent(g.Estimate.Upper)})");
        foreach (var n in r.Notes) lines.Add(n.Render());
        return (string.Join(" · ", parts), lines);
    }

    internal static string Clock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- Pause, Resume and Stop (D14) ---------------------------------------------

    private bool CanPause() => IsRunActive && (_run is not null || _centerRun is not null || _doeRun is not null);

    /// <summary>Pause finishes the trials in flight and holds; Resume continues as if never paused. One button.</summary>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void PauseResume()
    {
        // A yield run and a centering run pause alike: between batches, or between iterations.
        (Action Pause, Action Resume, WaitHandle Held, object Run)? target =
            _run is { } run ? (run.Pause, run.Resume, run.Held, run)
            : _centerRun is { } center ? (center.Pause, center.Resume, center.Held, center)
            : _doeRun is { } doe ? (doe.Pause, doe.Resume, doe.Held, doe)
            : null;
        if (target is not { } t) return;
        if (IsPaused)
        {
            t.Resume();
            State = YieldRunState.Running;
            StatusText = "";
            return;
        }
        t.Pause();
        State = YieldRunState.Paused;
        StatusText = "Pausing…";
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            WaitHandle.WaitAny([t.Held, ct.WaitHandle]);
            PostToUi(() => { if ((ReferenceEquals(t.Run, _run) || ReferenceEquals(t.Run, _centerRun) || ReferenceEquals(t.Run, _doeRun)) && IsPaused) StatusText = "Paused"; });
        });
    }

    /// <summary>Stop keeps every finished trial and reports yield over them; a corner sweep is cancelled.</summary>
    [RelayCommand(CanExecute = nameof(IsRunActive))]
    private void Stop()
    {
        if (_run is { } run)
        {
            run.Stop();
            State = YieldRunState.Running;
            StatusText = "Stopping…";
        }
        else if (_centerRun is { } center)
        {
            // Stop keeps the best point and still verifies it (YA-11).
            center.Stop();
            State = YieldRunState.Running;
            StatusText = "Stopping — verifying the best point…";
        }
        else if (_doeRun is { } doe)
        {
            // Stop keeps the runs done and analyses them (brief-yield-14).
            doe.Stop();
            State = YieldRunState.Running;
            StatusText = "Stopping — analysing the runs done…";
        }
        else if (_cornerRun is not null)
        {
            _cts?.Cancel();
            StatusText = "Stopping…";
        }
    }

    /// <summary>The focus moved to another schematic: the run is cancelled and whatever it published is dropped.</summary>
    private void Abandon()
    {
        _cts?.Cancel();
        _run?.Stop();
        _centerRun?.Stop();
        _doeRun?.Stop();
        _display?.Drop();
        _run = null;
        _cornerRun = null;
        _centerRun = null;
        _doeRun = null;
        State = YieldRunState.Idle;
    }

    // ---- The trial table (R-ya10-7) -------------------------------------------------

    private IReadOnlyList<YieldTrialRowViewModel> _allTrials = [];

    public ObservableCollection<YieldTrialRowViewModel> Trials { get; } = [];

    public bool HasTrials => Trials.Count > 0 || _allTrials.Count > 0;

    [ObservableProperty] private bool _failOnly;

    [ObservableProperty] private TrialSort _sort = TrialSort.Trial;

    partial void OnFailOnlyChanged(bool value) => FillTrials();

    partial void OnSortChanged(TrialSort value) => FillTrials();

    [RelayCommand] private void SortByTrial() => Sort = TrialSort.Trial;

    [RelayCommand] private void SortByMargin() => Sort = TrialSort.Margin;

    [ObservableProperty] private YieldTrialRowViewModel? _selectedTrial;

    private bool _selecting;

    partial void OnSelectedTrialChanged(YieldTrialRowViewModel? value)
    {
        NotifyTrialCommands();
        if (_selecting || _resultPath is not { } path) return;
        // YA-9's linked selection: every plot bound to the result highlights the trial.
        _selecting = true;
        if (value is null) TrialSelection.Shared.Clear(path);
        else TrialSelection.Shared.Select(path, [value.Trial]);
        _selecting = false;
    }

    /// <summary>A trial picked on a plot selects its row here.</summary>
    void ITrialSelectionListener.OnTrialSelectionChanged()
    {
        if (_selecting || _resultPath is not { } path) return;
        var picked = TrialSelection.Shared.For(path);
        int? trial = picked is { Count: 1 } one ? one.First() : null;
        if (SelectedTrial?.Trial == trial) return;
        _selecting = true;
        SelectedTrial = trial is { } t ? Trials.FirstOrDefault(r => r.Trial == t) : null;
        _selecting = false;
    }

    private bool _listening;

    private void ShowTrialRows(DataSet data)
    {
        if (!_listening) { TrialSelection.Shared.Subscribe(this); _listening = true; }
        _allTrials = YieldTrialRowViewModel.From(data);
        FillTrials();
        OnPropertyChanged(nameof(HasTrials));
        ContributionsCommand.NotifyCanExecuteChanged();
        OpenDisplayCommand.NotifyCanExecuteChanged();
    }

    private void FillTrials()
    {
        int? selected = SelectedTrial?.Trial;
        IEnumerable<YieldTrialRowViewModel> rows = _allTrials;
        if (FailOnly) rows = rows.Where(r => r.Pass == false);
        rows = Sort == TrialSort.Margin
            ? rows.OrderBy(r => double.IsFinite(r.Margin) ? r.Margin : double.NegativeInfinity).ThenBy(r => r.Trial)
            : rows.OrderBy(r => r.Trial);
        _selecting = true;
        Trials.Clear();
        foreach (var r in rows) Trials.Add(r);
        SelectedTrial = selected is { } s ? Trials.FirstOrDefault(r => r.Trial == s) : null;
        _selecting = false;
    }

    private bool HasSelectedTrial() => SelectedTrial is not null && _lastData is not null && _resultPath is not null;

    /// <summary>Send trial to Tuning: the trial's drawn values load into the Tuning sliders.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedTrial))]
    private void SendTrialToTuning()
    {
        if (SelectedTrial is not { } row || _lastData is not { } ds || SendToTuningTarget is null) return;
        var values = TrialPick.ValuesOf(ds, row.Trial);
        if (values.Count == 0) { StatusText = $"Trial {row.Trial} drew no designer values."; return; }
        SendToTuningTarget(values, $"Trial {row.Trial}");
        StatusText = $"Sent trial {row.Trial} to Tuning";
    }

    /// <summary>Re-run trial: simulated afresh in full, shown as the snapshot of every bound plot.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedTrial))]
    private async Task RerunSelectedTrial()
    {
        if (SelectedTrial is not { } row || _resultPath is not { } path || RerunTrial is null) return;
        StatusText = $"Re-running trial {row.Trial}…";
        string? refusal = await RerunTrial(path, row.Trial);
        StatusText = refusal ?? $"Trial {row.Trial} re-run";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTrial))]
    private void CopyTrialValues()
    {
        if (SelectedTrial is not { } row || _lastData is not { } ds) return;
        CopyText?.Invoke(TrialActions.ValuesText(ds, row.Trial));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedTrial))]
    private async Task SaveTrialAsCornerAsync()
    {
        if (SelectedTrial is not { } row || _resultPath is not { } path || _lastData is not { } ds || SaveTrialAsCorner is null) return;
        await SaveTrialAsCorner(path, row.Trial, ds);
    }

    private void NotifyTrialCommands()
    {
        SendTrialToTuningCommand.NotifyCanExecuteChanged();
        RerunSelectedTrialCommand.NotifyCanExecuteChanged();
        CopyTrialValuesCommand.NotifyCanExecuteChanged();
        SaveTrialAsCornerCommand.NotifyCanExecuteChanged();
    }

    // ---- Contributions (R-ya10-7) -----------------------------------------------------

    private bool CanComputeContributions() => _lastData is not null && !IsRunning && Goals.Count > 0;

    /// <summary>Computes the contributions ONCE (never unasked, YA-4 R-ya4-9) for the selected goal — the first when none
    /// is — and fills each variable's share bar.</summary>
    [RelayCommand(CanExecute = nameof(CanComputeContributions))]
    private void Contributions()
    {
        if (_lastData is not { } ds) return;
        string goal = SelectedGoal?.Name ?? Goals[0].Name;
        var report = ResultContributions.Of(ds, goal);
        if (report.Refusal is { } why) { StatusText = $"No contributions: {why.Render()}"; return; }
        // The bars and their tooltips show each share clamped to 0–100 % (owner decision D-a); the ranking is β·r's.
        _shares = report.Contributors.Where(c => c.Kind == "entry").ToDictionary(c => c.Name, c => c.ShownShare, StringComparer.Ordinal);
        foreach (var v in Variables) v.ShowShare(_shares.TryGetValue(v.Key, out double s) ? s : 0);
        StatusText = $"Contributions to {goal}: R² {report.RSquared.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    // ---- One-click yield display (R-ya10-8) --------------------------------------------

    private bool CanOpenDisplay() => _lastData is not null && _resultPath is not null && OpenYieldDisplay is not null && !IsRunning
                                     && DisplayUnavailable is null;

    [RelayCommand(CanExecute = nameof(CanOpenDisplay))]
    private async Task OpenDisplay()
    {
        OfferYieldDisplay = false;
        if (_resultPath is { } path && _lastData is { } ds && OpenYieldDisplay is not null) await OpenYieldDisplay(path, ds);
    }

    [RelayCommand] private void DismissDisplayOffer() => OfferYieldDisplay = false;

    private void NotifyRunCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ContributionsCommand.NotifyCanExecuteChanged();
        OpenDisplayCommand.NotifyCanExecuteChanged();
        EditCorrelationsCommand.NotifyCanExecuteChanged();
        EditGoalsCommand.NotifyCanExecuteChanged();
        NewSeedCommand.NotifyCanExecuteChanged();
        GenerateCornersCommand.NotifyCanExecuteChanged();
        NotifyTrialCommands();
        NotifyCenterCommands();
        NotifyDoeCommands();
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetFraction));
    }

    /// <summary>A progress sink that runs on the reporting thread — the run's own; the handler posts to the UI.</summary>
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
