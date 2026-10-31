// ================================================================
//  OptimizerPanelViewModel.Run.cs  —  Run / Pause / Stop, the per-
//  iteration readouts, the Data Display following the best point, and
//  keeping the result (brief-tuneopt-10 R-to10-6…R-to10-9, D16)
//
//  The run is TO-6's OptimizationRun on a background thread; its
//  progress is posted to the UI thread and nothing else crosses. Each
//  time the best point improves its DataSet is published under the
//  schematic's own results path (the TO-3 path) — the display's frame
//  coalescing does the skipping. On finish the best point is evaluated
//  ONCE more with every enabled analysis, published whole and written
//  to the results file with the tuned-values provenance.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;

namespace CircuitRF.Ui.Optimization;

/// <summary>Where the Optimizer's results go: the Data Display, and on finish the results file.</summary>
public interface IOptimizerDisplay
{
    /// <summary>Shows the best point's DataSet in place of the results file (UI thread).
    /// <paramref name="partial"/>: it holds only the goals' analyses, and the rest are stale.</summary>
    void Publish(DataSet data, bool partial);

    /// <summary>Writes the finished run's full DataSet to the results file, stating the values.</summary>
    void Commit(DataSet data, IReadOnlyDictionary<string, string> values);

    /// <summary>Drops whatever was published; the display returns to the file.</summary>
    void Drop();
}

/// <summary>Where a run is.</summary>
public enum OptimizerRunState { Idle, Running, Paused, Finished }

public sealed partial class OptimizerPanelViewModel
{
    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The bench as Simulate would netlist it, prepared once; null when it cannot be.</summary>
    public Func<SchematicViewModel, PreparedCircuit?>? PrepareCircuit { get; set; }

    /// <summary>The display a run publishes to; null publishes nothing.</summary>
    public Func<SchematicViewModel, IOptimizerDisplay?>? DisplayFor { get; set; }

    /// <summary>Hands work to the UI thread. Inline by default (tests).</summary>
    public Action<Action> PostToUi { get; set; } = a => a();

    /// <summary>Starts the run's thread.</summary>
    public Func<Action, Task> StartBackground { get; set; } = a => Task.Run(a);

    /// <summary>The preferred-value ladders <c>discrete=preferred</c> snaps to (the user's).</summary>
    public Func<PreferredLadders?>? Ladders { get; set; }

    /// <summary>Called as a run (or a snap) takes this schematic's display: the Tuning panel ends a live
    /// session on the same schematic WITHOUT writing it, since the run's result replaces what it shows and
    /// a session left running would later write its own older result over the run's.</summary>
    public Action<SchematicViewModel>? TakingDisplay { get; set; }

    // ---- Run state -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPaused), nameof(IsFinished), nameof(IsRunActive),
                              nameof(PauseResumeTip))]
    private OptimizerRunState _state;

    public bool IsRunning   => State == OptimizerRunState.Running;
    public bool IsPaused    => State == OptimizerRunState.Paused;
    public bool IsFinished  => State == OptimizerRunState.Finished;
    public bool IsRunActive => State is OptimizerRunState.Running or OptimizerRunState.Paused;

    public string PauseResumeTip => IsPaused ? "Resume" : "Pause — finishes the evaluations in flight, then holds";

    partial void OnStateChanged(OptimizerRunState value) => NotifyRunCommands();

    private OptimizationRun?        _run;
    private PreparedCircuit?        _circuit;
    private IOptimizerDisplay?      _display;
    private CancellationTokenSource? _cts;
    private Task?                   _task;
    private bool                    _partial;
    private bool                    _snapAfterStop;
    private double                  _publishedCost = double.PositiveInfinity;

    private IReadOnlyDictionary<string, string>? _bestValues;
    private double?                       _bestCost;
    private IReadOnlyList<GoalReport>?    _lastGoals;
    private IReadOnlyList<RailedVariable>? _lastRailed;

    /// <summary>The background work of the current run (or snap, or sensitivity); null when none.</summary>
    public Task? RunTask => _task;

    /// <summary>The run in hand — live, paused, or finished and kept for snap and sensitivity.</summary>
    internal OptimizationRun? CurrentRun => _run;

    /// <summary>The result of the last run that finished; null before one.</summary>
    public OptimizationResult? Result { get; private set; }

    /// <summary>The best point's values — what lock-in stores and Push writes; null before any.</summary>
    public IReadOnlyDictionary<string, string>? BestValues => _bestValues;

    /// <summary>Best-point DataSets handed to the display (R-to10-7) — the display coalesces them.</summary>
    public long BestPublishes { get; private set; }

    /// <summary>Full re-runs of the best point with every enabled analysis (R-to10-7): one per finish.</summary>
    public int FinalEvaluations => _finalEvaluations;
    private int _finalEvaluations;

    // ---- Readouts (R-to10-6) --------------------------------------------------

    /// <summary><c>Iter 37 · 412 evals · 1:08</c>, the stage, and failed / infeasible counts when any.</summary>
    [ObservableProperty] private string _runStatusText = "";

    /// <summary><c>0.0123</c> — the best cost so far; empty before a run.</summary>
    [ObservableProperty] private string _bestCostText = "";

    /// <summary><c>Goals met 3/4</c>.</summary>
    [ObservableProperty] private string _goalsMetText = "";

    /// <summary>The best cost after each iteration — the sparkline (log scale). Replaced whole.</summary>
    [ObservableProperty] private IReadOnlyList<double> _costHistory = [];

    [ObservableProperty] private int _iteration;

    public bool HasRunReadouts => CostHistory.Count > 0 || BestCostText.Length > 0;

    partial void OnCostHistoryChanged(IReadOnlyList<double> value) => OnPropertyChanged(nameof(HasRunReadouts));
    partial void OnBestCostTextChanged(string value) => OnPropertyChanged(nameof(HasRunReadouts));

    private void ClearRunReadouts()
    {
        _run = null;
        _circuit = null;
        _display = null;
        _task = null;
        Result = null;
        _bestValues = null;
        _bestCost = null;
        _lastGoals = null;
        _lastRailed = null;
        _publishedCost = double.PositiveInfinity;
        RunStatusText = BestCostText = GoalsMetText = "";
        CostHistory = [];
        Iteration = 0;
        State = OptimizerRunState.Idle;
        foreach (var g in Goals) g.ShowReport(null);
        foreach (var v in Variables)
        {
            v.ShowBest(null);
            v.ShowRailed(null);
            v.SensitivityText = "";
        }
    }

    // ---- Run (R-to10-6) --------------------------------------------------------

    /// <summary>
    /// Validates and starts a run. A setup that cannot run — nothing to optimize, no enabled goal,
    /// three parts of one complex value, an unknown algorithm — is refused with the reason in the
    /// status line, never a dialog; a goal that cannot be scored at the start point is refused the
    /// same way when the first evaluation finds it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (_tuned is null || IsRunActive) return;
        var circuit = PrepareCircuit?.Invoke(_tuned);
        if (circuit is null) { Problem("Refused: the schematic could not be netlisted."); return; }
        if (circuit.ReadError is { } readError) { Problem($"Refused: {readError}"); return; }
        circuit = WithStart(circuit);

        var setup = Setup?.Clone() ?? new TuningSetup();
        var cts   = new CancellationTokenSource();
        OptimizationRun? run = null;
        var options = new OptimizationOptions
        {
            Setup        = setup,
            Cancellation = cts.Token,
            Ladders      = Ladders?.Invoke(),
            Progress     = p => { var r = run; PostToUi(() => ApplyProgress(r, p)); },
        };
        run = OptimizationRun.Create(circuit, options);
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        TakingDisplay?.Invoke(_tuned);
        _run     = run;
        _cts     = cts;
        _circuit = circuit;
        _display = DisplayFor?.Invoke(_tuned);
        _partial = setup.Optimizer?.Scope != OptimizerScope.All;
        _snapAfterStop = false;
        StatusText = "";
        State = OptimizerRunState.Running;
        _perPoint = run.EvaluationsPerPoint;
        RunStatusText = _perPoint > 1 ? $"Starting… {_perPoint} evaluations per point ({string.Join(", ", run.CornerNames)})" : "Starting…";

        _task = StartBackground(() => Background(run, circuit, cts.Token, () => run.Run()));
    }

    // Cost honesty across corners (brief-yield-7 R-ya7-5): what one point costs, beside the evaluation count.
    private int _perPoint = 1;
    private string PerPointText => _perPoint > 1 ? $" ({_perPoint} per point)" : "";

    private bool CanRun() => _tuned is not null && !IsRunActive && PrepareCircuit is not null;

    /// <summary>The run's thread: the run (or snap), then ONE full evaluation of its best point.</summary>
    private void Background(OptimizationRun run, PreparedCircuit circuit, CancellationToken ct, Func<OptimizationResult> work)
    {
        OptimizationResult? result = null;
        string? crash = null;
        try { result = work(); }
        catch (Exception ex) { crash = ex.Message; }

        DataSet? full = null;
        if (result is { Outcome: OptimizationOutcome.GoalsMet or OptimizationOutcome.GoalsUnmet, BestValues.Count: > 0 }
            && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _finalEvaluations);
            try
            {
                var rr = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = result.BestValues });
                if (rr.Status == RunStatus.Success) full = rr.GroupedResults;
            }
            catch { /* the result stands without its plots */ }
        }
        PostToUi(() => Finish(run, result, full, crash));
    }

    private void ApplyProgress(OptimizationRun? run, OptimizationProgress p)
    {
        if (run is null || !ReferenceEquals(run, _run)) return;
        Iteration = p.Iteration;
        var parts = new List<string>
        {
            $"Iter {p.Iteration}",
            $"{p.Evaluations} eval{(p.Evaluations == 1 ? "" : "s")}" + PerPointText,
            Clock(p.Elapsed),
        };
        if (p.Stage is { } stage) parts.Add(stage);
        if (p.Failures > 0) parts.Add($"{p.Failures} failed");
        if (p.Infeasible > 0) parts.Add($"{p.Infeasible} infeasible");
        RunStatusText = string.Join(" · ", parts);

        if (double.IsFinite(p.BestCost)) CostHistory = [.. CostHistory, p.BestCost];
        ShowBest(p.BestValues, double.IsFinite(p.BestCost) ? p.BestCost : null, p.Goals, p.Railed);

        // The display follows the best point, and only when it improves (R-to10-7).
        if (p.BestData is { } data && p.BestCost < _publishedCost && _display is { } display)
        {
            _publishedCost = p.BestCost;
            BestPublishes++;
            display.Publish(data, _partial);
        }
    }

    private void ShowBest(IReadOnlyDictionary<string, string>? values, double? cost,
                          IReadOnlyList<GoalReport> goals, IReadOnlyList<RailedVariable> railed)
    {
        if (values is { Count: > 0 }) _bestValues = values;
        _bestCost   = cost ?? _bestCost;
        _lastGoals  = goals;
        _lastRailed = railed;
        BestCostText = _bestCost is { } c ? c.ToString("G4", CultureInfo.InvariantCulture) : "";
        int enabled = Goals.Count(g => g.Goal.Enabled);
        GoalsMetText = goals.Count > 0 ? $"Goals met {goals.Count(g => g.Met)}/{enabled}" : "";
        foreach (var g in Goals) g.ShowReport(goals.FirstOrDefault(r => r.Name == g.Name));
        foreach (var v in Variables)
        {
            v.ShowBest(_bestValues);
            v.ShowRailed(railed.FirstOrDefault(r => r.Key == v.Key));
        }
        NotifyRunCommands();
    }

    private void Finish(OptimizationRun run, OptimizationResult? result, DataSet? full, string? crash)
    {
        if (!ReferenceEquals(run, _run)) return;            // abandoned meanwhile

        if (result is null)
        {
            State = OptimizerRunState.Idle;
            Problem($"The run failed: {crash}");
            _display?.Drop();
            return;
        }
        Result = result;

        switch (result.Outcome)
        {
            case OptimizationOutcome.Refused:
                State = OptimizerRunState.Idle;
                Problem($"Refused: {result.Refusal?.Render() ?? result.FinishReason}");
                _display?.Drop();
                return;
            case OptimizationOutcome.Cancelled:
                State = OptimizerRunState.Idle;
                StatusText = "Cancelled";
                _display?.Drop();
                return;
        }

        ShowBest(result.BestValues, result.BestCost, result.Goals, result.Railed);
        RunStatusText = $"Iter {result.Iterations} · {result.Evaluations} evals" + PerPointText
                        + (result.Failures > 0 ? $" · {result.Failures} failed" : "")
                        + (result.Infeasible > 0 ? $" · {result.Infeasible} infeasible" : "");
        State = OptimizerRunState.Finished;

        // Every plot current: the best point, every enabled analysis, written with its provenance.
        if (full is not null && _display is { } display)
        {
            display.Publish(full, partial: false);
            display.Commit(full, result.BestValues);
        }
        else if (result.Outcome == OptimizationOutcome.NoConvergence) _display?.Drop();

        var (summary, lines) = FinishSummary(result);
        StatusText   = summary;
        StatusDetail = string.Join(Environment.NewLine, lines);
        ReportMessages?.Invoke($"Optimizer: {summary}", lines);

        if (_snapAfterStop)
        {
            _snapAfterStop = false;
            SnapAndPolishCore();
        }
    }

    /// <summary>R-to10-9: the reason, every goal met or which are not, the best cost — and, for
    /// Messages, one line per goal and every note.</summary>
    internal static (string Summary, IReadOnlyList<string> Lines) FinishSummary(OptimizationResult r)
    {
        var parts = new List<string> { Capitalize(r.FinishReason) };
        if (r.Outcome == OptimizationOutcome.NoConvergence)
            parts.Add(r.Refusal?.Render() ?? "no evaluation converged");
        else
        {
            var unmet = r.Goals.Where(g => !g.Met).Select(g => g.Name).ToList();
            parts.Add(unmet.Count == 0 ? "all goals met" : $"not met: {string.Join(", ", unmet)}");
            if (r.BestCost is { } c) parts.Add($"best cost {c.ToString("G4", CultureInfo.InvariantCulture)}");
        }
        // What the ideal stand-ins hid (brief vendorlib-4 R-vl4-3b): said where it is seen, not only in Messages.
        if (r.Snap is { Parts: > 0 } snap)
            parts.Add($"ideal parts {snap.CostBefore.ToString("G4", CultureInfo.InvariantCulture)} → real parts " +
                      snap.CostSnapped.ToString("G4", CultureInfo.InvariantCulture));
        if (r.Infeasible > 0) parts.Add($"{r.Infeasible} infeasible");
        if (r.Failures > 0) parts.Add($"{r.Failures} failed");

        var lines = new List<string>();
        foreach (var g in r.Goals)
        {
            string at = g.WorstAt is { } x ? $" at {g.Axis} = {AxisText(x, g.Axis)}" : "";
            lines.Add(g.Met
                ? $"{g.Name}: met (worst value {g.WorstValue.ToString("G4", CultureInfo.InvariantCulture)}{at})"
                : $"{g.Name}: not met — worst value {g.WorstValue.ToString("G4", CultureInfo.InvariantCulture)}{at}, " +
                  $"short by {g.WorstViolation.ToString("G4", CultureInfo.InvariantCulture)}");
        }
        foreach (var (k, v) in r.BestValues) lines.Add($"{k} = {v}");
        foreach (var n in r.Notes) lines.Add(n.Render());
        return (string.Join(" · ", parts), lines);
    }

    /// <summary>A point on a goal's range axis as a designer reads it: <c>0.95 GHz</c> on a frequency axis
    /// (WorstAt is base SI), the plain number on any other.</summary>
    internal static string AxisText(double x, string? axis)
    {
        if (axis is not ("freq" or "frequency") || x == 0 || !double.IsFinite(x))
            return x.ToString("G4", CultureInfo.InvariantCulture);
        (double scale, string unit) = Math.Abs(x) switch
        {
            >= 1e9 => (1e9, "GHz"),
            >= 1e6 => (1e6, "MHz"),
            >= 1e3 => (1e3, "kHz"),
            _      => (1.0, "Hz"),
        };
        return (x / scale).ToString("G4", CultureInfo.InvariantCulture) + " " + unit;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Clock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- Pause, Resume and Stop (D16) --------------------------------------------

    /// <summary>Pause finishes the evaluations in flight, then holds the algorithm's whole state;
    /// Resume continues exactly as if never paused. One button.</summary>
    [RelayCommand(CanExecute = nameof(IsRunActive))]
    private void PauseResume()
    {
        if (_run is not { } run) return;
        if (IsPaused)
        {
            run.Resume();
            State = OptimizerRunState.Running;
            StatusText = "";
            return;
        }
        run.Pause();
        State = OptimizerRunState.Paused;
        StatusText = "Pausing…";
        _ = Task.Run(() =>
        {
            WaitHandle.WaitAny([run.Held, _cts?.Token.WaitHandle ?? new ManualResetEvent(false)]);
            PostToUi(() => { if (ReferenceEquals(run, _run) && IsPaused) StatusText = "Paused"; });
        });
    }

    /// <summary>Ends the run after the batch in flight and keeps the best point.</summary>
    [RelayCommand(CanExecute = nameof(IsRunActive))]
    private void Stop()
    {
        if (_run is not { } run) return;
        run.Stop();
        State = OptimizerRunState.Running;
        StatusText = "Stopping…";
    }

    /// <summary>The focus moved to another schematic or the bench closed: the run is cancelled and
    /// whatever it published is dropped.</summary>
    private void Abandon()
    {
        _cts?.Cancel();
        _run?.Stop();
        _display?.Drop();
        _run = null;
        State = OptimizerRunState.Idle;
    }

    // ---- Keeping the result (R-to10-8) --------------------------------------------

    private bool CanKeep() => (IsPaused || IsFinished) && _bestValues is { Count: > 0 };

    /// <summary>Whether the best values may be WRITTEN somewhere (Lock in, Push, Send to Tuning). Not while
    /// paused with a standard-series row between its rungs — a gradient algorithm searches continuously —
    /// until Stop (or Snap and polish) puts it on them (designer report, round 20).</summary>
    private bool CanKeepValues() => CanKeep() && !(IsPaused && _run?.BestOffSeries == true);

    /// <summary>The best values as Lock in, Push and Send to Tuning write them: to the panel's digits,
    /// the figures the rows show.</summary>
    private IReadOnlyDictionary<string, string>? KeptValues()
        => _bestValues is { } values ? TuningDigits.Round(values, Digits) : null;

    /// <summary>Lock in (TO-5): the best values as a new preset, with the cost they scored.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepValues))]
    private void LockIn()
    {
        if (_tuned is null || KeptValues() is not { } values) return;
        var (setup, preset) = TuningPresets.LockIn(Setup, values, UtcNow(), _bestCost);
        Execute(setup, "Lock in optimizer result");
        StatusText = $"Locked in {preset.Name}";
    }

    /// <summary>Push (TO-4 R-to4-8): the best values into the documents that own them, one undo step
    /// per document.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepValues))]
    private void Push()
    {
        if (_tuned is null || KeptValues() is not { } values || Catalog is not { } catalog) return;
        var drawn = AsDrawn(catalog, values.Keys);
        var report = TuningPush.Push(catalog, values, _tuned, SessionForDrawing ?? (_ => null));
        if (report.UndoSession(_tuned) is { } undo) EditCommitted?.Invoke(undo);
        // Reset's way back: the value each key had BEFORE the first Push of this panel, so a second Push
        // does not make the first one's result the "as drawn".
        foreach (var key in report.WrittenKeys)
            if (drawn.TryGetValue(key, out var before)) _pushedFrom.TryAdd(key, before);
        _catalog = null;
        RefreshNow();
        StatusText = report.StatusLine;
        ResetCommand.NotifyCanExecuteChanged();
    }

    // Key → the value text the schematic held before this panel first pushed it (Reset puts it back).
    private readonly Dictionary<string, string> _pushedFrom = new(StringComparer.Ordinal);

    /// <summary>The schematic's own values of <paramref name="keys"/>, spelled as Push spells them.</summary>
    private static Dictionary<string, string> AsDrawn(TunableCatalog catalog, IEnumerable<string> keys)
    {
        var drawn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (catalog.FindValue(key) is not { } t) continue;
            drawn[key] = t.IsVendorPart ? t.ValueText
                : t.Part is null
                ? TunableValue.Format(t.Value, t.Unit)
                : ComplexValue.Format(t.Whole, t.WholeUnit, t.Form, "G15");
        }
        return drawn;
    }

    private bool CanReset() => _tuned is not null && !IsRunActive
                               && (HasRunReadouts || _pushedFrom.Count > 0 || HasStartFrom || Result is not null);

    /// <summary>
    /// Back to the start: the values this panel pushed return to what the schematic held before (one undo
    /// step), a handed-over start point is cleared, and the run's readouts go — so the next run, with this
    /// algorithm or another, starts from the design as drawn rather than from the last run's answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        if (_tuned is null || IsRunActive) return;
        string status = "Reset";
        if (_pushedFrom.Count > 0 && Catalog is { } catalog)
        {
            var report = TuningPush.Push(catalog, new Dictionary<string, string>(_pushedFrom), _tuned, SessionForDrawing ?? (_ => null));
            if (report.UndoSession(_tuned) is { } undo) EditCommitted?.Invoke(undo);
            if (report.Written > 0)
                status = $"Reset: {report.Written} value{(report.Written == 1 ? "" : "s")} back as drawn";
            _catalog = null;
        }
        _pushedFrom.Clear();
        if (HasStartFrom) ClearStartFrom();
        ClearRunReadouts();
        RefreshNow();
        StatusText = status;
        NotifyRunCommands();
    }

    /// <summary>Loads the best values into the Tuning session so the user hand-tunes from there.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepValues))]
    private void SendToTuning()
    {
        if (KeptValues() is not { } values || SendToTuningTarget is null) return;
        SendToTuningTarget(values);
        StatusText = $"Sent {values.Count} value{(values.Count == 1 ? "" : "s")} to Tuning";
    }

    private bool CanSnap() => CanKeep() && _run?.Variables?.DiscreteCoordinates.Any() == true;

    /// <summary>
    /// Snap and polish (TO-8 R-to8-4) — offered when any variable is integer, stepped or preferred.
    /// While paused the run is stopped first and the snap follows its finish.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSnap))]
    private void SnapAndPolish()
    {
        if (IsPaused)
        {
            _snapAfterStop = true;
            Stop();
            return;
        }
        SnapAndPolishCore();
    }

    private void SnapAndPolishCore()
    {
        if (_run is not { } run || _circuit is not { } circuit || _cts is null) return;
        if (_tuned is not null) TakingDisplay?.Invoke(_tuned);
        State = OptimizerRunState.Running;
        StatusText = "Snapping…";
        var ct = _cts.Token;
        _task = StartBackground(() => Background(run, circuit, ct, () => run.SnapAndPolish(ct)));
    }

    /// <summary>The sensitivity at the best point (TO-8 R-to8-6): each variable's share, on its row.</summary>
    [RelayCommand(CanExecute = nameof(CanKeep))]
    private void Sensitivity()
    {
        if (_run is not { } run || _cts is null) return;
        var ct = _cts.Token;
        StatusText = "Sensitivity…";
        _task = StartBackground(() =>
        {
            SensitivityReport? rep = null;
            try { rep = run.Sensitivity(ct: ct); } catch { }
            PostToUi(() =>
            {
                if (!ReferenceEquals(run, _run)) return;
                if (rep is null) { StatusText = "No sensitivity: the best point has no successful evaluation"; return; }
                foreach (var row in Variables)
                    row.SensitivityText = rep.Variables.FirstOrDefault(v => v.Key == row.Key) is { } s
                        ? OptimizerVariableRowViewModel.Share(s.Share) : "";
                StatusText = $"Sensitivity: {rep.Evaluations} evaluation{(rep.Evaluations == 1 ? "" : "s")}";
            });
        });
    }

    private void NotifyRunCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        LockInCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        SendToTuningCommand.NotifyCanExecuteChanged();
        SnapAndPolishCommand.NotifyCanExecuteChanged();
        SensitivityCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }
}
