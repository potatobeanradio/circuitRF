// ================================================================
//  YieldPanelViewModel.Doe.cs  —  the DOE mode (brief-yield-14 R-ya14-9)
//
//  THE PANEL CALLS DoeRun AND NOTHING ELSE. The design is created
//  exactly as `circuitrf yield doe` creates it — the prepared circuit,
//  the schematic's own setup (Setup = null), the result written beside
//  the design as <design>.doe.npy — on a background thread. The plan
//  (factors and run count) is the same constructor asked before
//  anything runs, so what the panel shows is what Run will do.
//
//  Model optimum → confirmation → Send to Tuning / Optimizer: the
//  run's own ModelOptimum, and the Tuning panel's and the Optimizer's
//  own doors for a point.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Yield;

/// <summary>One factor of the plan: letter, entry and its low / centre / high levels.</summary>
public sealed record DoeFactorRow(string Letter, string Key, string Low, string Centre, string High);

/// <summary>One effect of the selected response, largest first.</summary>
public sealed record DoeEffectRow(string Term, string Effect, bool Active, string Aliases);

/// <summary>One goal at the model optimum: predicted, and simulated by the confirmation.</summary>
public sealed record DoeOptimumRow(string Goal, string Predicted, string Simulated, bool? Met);

public sealed partial class YieldPanelViewModel
{
    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The Optimizer's door for a start point — Send to Optimizer.</summary>
    public Action<IReadOnlyDictionary<string, string>, string>? SendToOptimizerTarget { get; set; }

    // ---- Mode -----------------------------------------------------------------------

    public bool IsDoe { get => Mode == YieldMode.Doe; set { if (value) Mode = YieldMode.Doe; } }

    /// <summary>The statistics line's settings — every mode's but DOE's, which has its own line.</summary>
    public bool ShowStatisticsSettings => Mode != YieldMode.Doe;

    // ---- The doe line (collapsed to a summary, as the others) -------------------------

    public DoeSettings DoeSettings => Setup?.Doe ?? new DoeSettings();

    public IReadOnlyList<string> DoeDesignChoices { get; } = ["full factorial", "fractional", "Plackett–Burman", "composite (ccf)"];
    public IReadOnlyList<string> DoeFactorChoices { get; } = ["opt ranges", "tolerances"];
    public IReadOnlyList<string> DoeResolutionChoices { get; } = ["IV", "V"];
    public IReadOnlyList<string> DoeResponseChoices { get; } = [.. AnalysisDirectiveSchema.DoeResponseTokens];

    [ObservableProperty] private bool _doeSettingsExpanded;

    [RelayCommand] private void ToggleDoeSettings() => DoeSettingsExpanded = !DoeSettingsExpanded;

    /// <summary><c>frac · res V · opt ranges · 1 centre</c>.</summary>
    public string DoeSettingsSummary
    {
        get
        {
            var d = DoeSettings;
            var parts = new List<string> { AnalysisDirectiveSchema.DoeDesignTokens[(int)d.EffectiveDesign] };
            if (d.EffectiveDesign == DoeDesignKind.Frac) parts.Add("res " + DoeRun.Roman(d.EffectiveResolution));
            parts.Add(d.EffectiveFactors == DoeFactorSource.Stat ? $"tolerances ± {G(d.SigmaK ?? 1)} σ" : "opt ranges");
            parts.Add($"{d.EffectiveCentre} centre");
            if (d.EffectiveResponses == DoeResponseSet.All) parts.Add("all goals");
            return string.Join(" · ", parts);
        }
    }

    public int DoeDesignIndex
    {
        get => (int)DoeSettings.EffectiveDesign;
        set
        {
            if (value < 0 || value > 3 || value == DoeDesignIndex) return;
            EditDoe(d => d.Design = value == 0 ? null : (DoeDesignKind)value, $"DOE design {AnalysisDirectiveSchema.DoeDesignTokens[value]}");
        }
    }

    public int DoeResolutionIndex
    {
        get => DoeSettings.EffectiveResolution == 5 ? 1 : 0;
        set
        {
            if (value is < 0 or > 1 || value == DoeResolutionIndex) return;
            EditDoe(d => d.Resolution = value == 1 ? 5 : null, $"DOE resolution {DoeResolutionChoices[value]}");
        }
    }

    public bool IsDoeFrac => DoeSettings.EffectiveDesign == DoeDesignKind.Frac;

    /// <summary>The factor toggle (R-ya14-9): the opt rows, or the stat rows.</summary>
    public int DoeFactorIndex
    {
        get => (int)DoeSettings.EffectiveFactors;
        set
        {
            if (value is < 0 or > 1 || value == DoeFactorIndex) return;
            EditDoe(d => { d.Factors = value == 0 ? null : DoeFactorSource.Stat; d.Levels = null; },
                    value == 0 ? "DOE over the opt ranges" : "DOE over the tolerances");
        }
    }

    public bool IsDoeStat => DoeSettings.EffectiveFactors == DoeFactorSource.Stat;

    public string DoeSigmaText
    {
        get => DoeSettings.Levels is { } l && DoeSettings.SigmaOf(l) is { } k ? G(k) : "";
        set
        {
            string t = value.Trim();
            string? levels = null;
            if (t.Length > 0)
            {
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) || !(k > 0) || !double.IsFinite(k))
                {
                    StatusText = $"Refused: '{value}' is not a number of sigmas above zero.";
                    RefreshDoeSettings();
                    return;
                }
                levels = k == DoeSettings.DefaultSigma ? null : "sigma:" + k.ToString("R", CultureInfo.InvariantCulture);
            }
            if (levels == DoeSettings.Levels) return;
            EditDoe(d => d.Levels = levels, "Set the DOE levels");
        }
    }

    public string DoeCentreText
    {
        get => DoeSettings.Centre?.ToString(CultureInfo.InvariantCulture) ?? "";
        set
        {
            string t = value.Trim();
            int? c = null;
            if (t.Length > 0)
            {
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
                {
                    StatusText = $"Refused: '{value}' is not a count of centre points.";
                    RefreshDoeSettings();
                    return;
                }
                c = n == DoeSettings.DefaultCentre ? null : n;
            }
            if (c == DoeSettings.Centre) return;
            EditDoe(d => d.Centre = c, "Set the DOE centre points");
        }
    }

    public int DoeResponseIndex
    {
        get => (int)DoeSettings.EffectiveResponses;
        set
        {
            if (value is < 0 or > 1 || value == DoeResponseIndex) return;
            EditDoe(d => d.Responses = value == 0 ? null : DoeResponseSet.All, value == 0 ? "DOE on the goals the factors are for" : "DOE on every goal");
        }
    }

    /// <summary>The doe line with <paramref name="change"/> applied, as one undo step.</summary>
    internal void EditDoe(Action<DoeSettings> change, string description)
    {
        if (_tuned is null) return;
        var d = DoeSettings.Clone();
        change(d);
        Execute(TuningSetupEdits.WithDoe(Setup, d), description);
        RefreshDoeSettings();
    }

    private void RefreshDoeSettings()
    {
        foreach (var name in new[]
                 {
                     nameof(DoeSettingsSummary), nameof(DoeDesignIndex), nameof(DoeResolutionIndex), nameof(IsDoeFrac),
                     nameof(DoeFactorIndex), nameof(IsDoeStat), nameof(DoeSigmaText), nameof(DoeCentreText), nameof(DoeResponseIndex),
                 })
            OnPropertyChanged(name);
        if (Mode == YieldMode.Doe) RefreshDoePlan();
    }

    // ---- The plan: factors and run count, before running ------------------------------

    public ObservableCollection<DoeFactorRow> DoeFactors { get; } = [];

    /// <summary><c>2-level full factorial: 3 factor(s), 9 runs (1 centre)</c>, or the refusal.</summary>
    [ObservableProperty] private string _doePlanText = "";

    /// <summary>Asks the run's own constructor what it would do — nothing is simulated.</summary>
    public void RefreshDoePlan()
    {
        DoeFactors.Clear();
        DoePlanText = "";
        if (_tuned is null || PrepareCircuit?.Invoke(_tuned) is not { ReadError: null } circuit) return;
        var run = DoeRun.Create(circuit);
        if (run.Refusal is { } refusal) { DoePlanText = refusal.Render(); return; }
        foreach (var f in run.Factors) DoeFactors.Add(new DoeFactorRow(f.Letter, f.Key, f.Low, f.Centre, f.High));
        DoePlanText = $"{run.Describe()} — {run.DistinctRuns} simulation(s)";
    }

    // ---- The run --------------------------------------------------------------------

    private DoeRun? _doeRun;

    /// <summary>The last design's result; null before one finishes.</summary>
    public DoeResult? DoeResult { get; private set; }

    /// <summary>The responses analysed, for the effects table's picker.</summary>
    public ObservableCollection<string> DoeResponses { get; } = [];

    [ObservableProperty] private string? _selectedDoeResponse;

    partial void OnSelectedDoeResponseChanged(string? value) => ShowDoeEffects();

    public ObservableCollection<DoeEffectRow> DoeEffects { get; } = [];

    public bool HasDoeEffects => DoeEffects.Count > 0;

    /// <summary><c>Lenth margin 0.42 · R² 0.998</c> for the selected response.</summary>
    [ObservableProperty] private string _doeFitText = "";

    private void ClearDoeReadouts()
    {
        _doeRun = null;
        DoeResult = null;
        DoeResponses.Clear();
        DoeEffects.Clear();
        DoeFitText = "";
        ClearDoeOptimum();
        OnPropertyChanged(nameof(HasDoeEffects));
    }

    private void RunDoe(PreparedCircuit circuit, string? source)
    {
        var cts = new System.Threading.CancellationTokenSource();
        string? path = source is null ? null : DoeRun.ResultPathFor(source);
        DoeRun? run = null;
        run = DoeRun.Create(circuit, new DoeOptions
        {
            // The setup the bench netlists with — the schematic's own tuning block, as `yield doe` reads it.
            Setup        = null,
            Cancellation = cts.Token,
            ResultPath   = path,
            Progress     = p => { var r = run; PostToUi(() => { if (ReferenceEquals(r, _doeRun)) { TrialsDoneText = $"run {p.Done} / {p.Total}"; ElapsedText = Clock(p.Elapsed); } }); },
        });
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        _doeRun     = run;
        _cts        = cts;
        _resultPath = path;
        StatusText  = "";
        State       = YieldRunState.Running;
        HasRunReadouts = true;
        TrialsDoneText = $"0 / {run.RunCount}";
        foreach (var note in run.Notes) ReportMessages?.Invoke($"DOE: {note.Render()}", []);

        _task = StartBackground(() =>
        {
            DoeResult? result = null;
            string? crash = null;
            try { result = run.Run(); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() => FinishDoe(run, result, crash));
        });
    }

    private void FinishDoe(DoeRun run, DoeResult? result, string? crash)
    {
        if (!ReferenceEquals(run, _doeRun)) return;               // abandoned meanwhile
        if (result is null) { State = YieldRunState.Idle; Problem($"The run failed: {crash}"); return; }
        DoeResult = result;
        if (result.Outcome is DoeOutcome.Cancelled or DoeOutcome.Refused)
        {
            State = YieldRunState.Idle;
            if (result.Outcome == DoeOutcome.Cancelled) StatusText = "Cancelled";
            else Problem($"Refused: {result.Refusal?.Render() ?? result.FinishReason}");
            return;
        }

        State = YieldRunState.Finished;
        TrialsDoneText = $"{result.Records.Count(r => r.Evaluated)} / {result.Records.Count} runs · {result.Evaluations} simulations";
        _lastData = result.Data;
        foreach (var r in result.Responses.Where(r => r.Fit is not null)) DoeResponses.Add(r.Name);
        SelectedDoeResponse = DoeResponses.FirstOrDefault();

        int active = result.Responses.Sum(r => r.Fit?.Effects.Count(e => e.Active) ?? 0);
        var lines = result.Notes.Select(n => n.Render()).ToList();
        string summary = result.Outcome == DoeOutcome.NoneEvaluated
            ? result.Refusal?.Render() ?? "No run evaluated"
            : $"{result.Records.Count} runs · {result.Responses.Count} response(s) · {active} active effect(s)";
        StatusText   = summary;
        StatusDetail = string.Join(Environment.NewLine, lines);
        ReportMessages?.Invoke($"DOE: {summary}", lines);
        if (result.WrittenPath is not null && !_offeredDisplay && HasYieldDisplay?.Invoke(result.WrittenPath, result.Data) == false)
        {
            OfferYieldDisplay = true;
            _offeredDisplay = true;
        }
        NotifyRunCommands();
    }

    private void ShowDoeEffects()
    {
        DoeEffects.Clear();
        DoeFitText = "";
        if (DoeResult?.Responses.FirstOrDefault(r => r.Name == SelectedDoeResponse)?.Fit is { } fit)
        {
            foreach (var e in fit.Effects.OrderByDescending(e => Math.Abs(e.Effect)))
                DoeEffects.Add(new DoeEffectRow(e.Term, G(e.Effect), e.Active,
                    e.Aliases.Count == 0 ? "" : "= " + string.Join(", ", e.Aliases.Take(4)) + (e.Aliases.Count > 4 ? $", … {e.Aliases.Count - 4} more" : "")));
            DoeFitText = $"Lenth margin {G(fit.Margin)} · R² {fit.RSquared.ToString("0.000", CultureInfo.InvariantCulture)}" +
                         (fit.Curvature is { } c ? $" · curvature {G(c)}{(fit.CurvatureActive ? " (fit a composite)" : "")}" : "");
        }
        OnPropertyChanged(nameof(HasDoeEffects));
    }

    private static string G(double v) => double.IsFinite(v) ? v.ToString("G4", CultureInfo.InvariantCulture) : "—";

    // ---- Model optimum → confirmation → Send (R-ya14-7) --------------------------------

    private DoeOptimum? _doeOptimum;

    public ObservableCollection<DoeOptimumRow> DoeOptimumGoals { get; } = [];

    /// <summary><c>a = 1.3, b = 0.6</c> — the confirmed point.</summary>
    [ObservableProperty] private string _doeOptimumText = "";

    public bool HasDoeOptimum => _doeOptimum is { Refusal: null };

    /// <summary>The model optimum's values — what Send to Tuning and Send to Optimizer hand over; null before one.</summary>
    public IReadOnlyDictionary<string, string>? DoeOptimumValues => _doeOptimum is { Refusal: null } o ? o.Values : null;

    [ObservableProperty] private bool _isSearchingOptimum;

    private void ClearDoeOptimum()
    {
        _doeOptimum = null;
        DoeOptimumGoals.Clear();
        DoeOptimumText = "";
        OnPropertyChanged(nameof(HasDoeOptimum));
        NotifyDoeCommands();
    }

    private bool CanFindOptimum() => IsFinished && Mode == YieldMode.Doe && _doeRun is not null && DoeResult is { Outcome: DoeOutcome.Finished }
                                     && !IsSearchingOptimum;

    /// <summary>Searches the fitted model, then confirms its best point by one simulation — on the run's thread.</summary>
    [RelayCommand(CanExecute = nameof(CanFindOptimum))]
    private void FindModelOptimum()
    {
        if (_doeRun is not { } run || DoeResult is not { } result) return;
        ClearDoeOptimum();
        IsSearchingOptimum = true;
        StatusText = "Searching the model…";
        _task = StartBackground(() =>
        {
            DoeOptimum? o = null;
            string? crash = null;
            try { o = run.ModelOptimum(result); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() =>
            {
                IsSearchingOptimum = false;
                if (!ReferenceEquals(run, _doeRun)) return;
                if (o is null) { Problem($"The model optimum failed: {crash}"); return; }
                if (o.Refusal is { } why) { Problem($"Refused: {why.Render()}"); return; }
                _doeOptimum = o;
                foreach (var g in o.Goals)
                    DoeOptimumGoals.Add(new DoeOptimumRow(g.Goal,
                        g.PredictedValue is { } pv ? G(pv) : $"margin {G(g.PredictedMargin)}",
                        g.SimulatedValue is { } sv ? G(sv) : g.SimulatedMargin is { } sm ? $"margin {G(sm)}" : "—", g.Met));
                DoeOptimumText = string.Join(", ", o.Values.Select(kv => $"{kv.Key} = {kv.Value}"));
                StatusText = o.Confirmation == PointStatus.Evaluated
                    ? "Model optimum confirmed by simulation"
                    : $"Model optimum found; {o.ConfirmationReason?.Render()}";
                OnPropertyChanged(nameof(HasDoeOptimum));
                NotifyDoeCommands();
            });
        });
    }

    partial void OnIsSearchingOptimumChanged(bool value) => NotifyDoeCommands();

    private bool CanSendOptimum() => HasDoeOptimum && Mode == YieldMode.Doe;

    /// <summary>Loads the model optimum into the Tuning sliders.</summary>
    [RelayCommand(CanExecute = nameof(CanSendOptimum))]
    private void SendOptimumToTuning()
    {
        if (DoeOptimumValues is not { } values || SendToTuningTarget is null) return;
        SendToTuningTarget(values, "DOE optimum");
        StatusText = $"Sent {values.Count} value{(values.Count == 1 ? "" : "s")} to Tuning";
    }

    /// <summary>Sets the model optimum as the Optimizer's start point.</summary>
    [RelayCommand(CanExecute = nameof(CanSendOptimum))]
    private void SendOptimumToOptimizer()
    {
        if (DoeOptimumValues is not { } values || SendToOptimizerTarget is null) return;
        SendToOptimizerTarget(values, "DOE optimum");
        StatusText = "The Optimizer starts from the DOE optimum";
    }

    private void NotifyDoeCommands()
    {
        FindModelOptimumCommand.NotifyCanExecuteChanged();
        SendOptimumToTuningCommand.NotifyCanExecuteChanged();
        SendOptimumToOptimizerCommand.NotifyCanExecuteChanged();
    }
}
