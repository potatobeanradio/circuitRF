// ================================================================
//  YieldPanelViewModel.Centering.cs  —  the Centering mode
//  (brief-yield-12 R-ya12-1; yield overview D11, D14)
//
//  THE PANEL CALLS CenteringRun AND NOTHING ELSE. The run is created
//  exactly as `circuitrf yield center` creates it — the prepared
//  circuit, the schematic's own setup (Setup = null, so the panel's
//  centred nominals are the verb's for the same seed), the best
//  point's verification written beside the design as
//  <design>.yield.npy — on a background thread. What crosses back to
//  the UI thread is its progress and its result.
//
//  Lock in, Push and Send to Tuning act on the centred nominals
//  exactly as the Optimizer's act on its best point: a preset (TO-5),
//  one undo step per document (TuningPush), the Tuning sliders — to
//  the schematic's digits, the figures the rows show.
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
using CircuitRF.Engine.Optimization;
using CircuitRF.Engine.Statistics;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Yield;

public sealed partial class YieldPanelViewModel
{
    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The session that edits a drawing a centred value belongs to — Push's route into a sub-cell.</summary>
    public Func<SchematicEditModel, SchematicViewModel?>? SessionForDrawing { get; set; }

    /// <summary>The clock Lock in stamps a preset with.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    // ---- Mode -----------------------------------------------------------------------

    public bool IsCentering { get => Mode == YieldMode.Centering; set { if (value) Mode = YieldMode.Centering; } }

    /// <summary>The Monte Carlo / Yield / Corners lists; Centering shows its own.</summary>
    public bool ShowVariableTolerances => Mode is not (YieldMode.Centering or YieldMode.Doe);

    // ---- Digits (the Tuning and Optimizer panels' setting, on the same schematic) ----------

    /// <summary>The significant digits a centred value is shown and kept with (<see cref="TuningDigits"/>).</summary>
    public int Digits => TuningDigits.Of(Setup);

    public IReadOnlyList<TuningDigitsChoice> DigitsChoices
        => [.. TuningDigits.Choices.Select(d => new TuningDigitsChoice(d, d == Digits, SetDigitsCommand))];

    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void SetDigits(int digits)
    {
        if (_tuned is null || digits == Digits) return;
        var next = Setup?.Clone() ?? new TuningSetup();
        next.Digits = digits == TuningDigits.Default ? null : digits;
        Execute(next, $"Show {TuningDigits.Label(digits)}");
    }

    /// <summary>The centred nominals as Lock in, Push and Send to Tuning write them: to the panel's digits, the
    /// figures the rows show.</summary>
    private IReadOnlyDictionary<string, string>? KeptCentred()
        => _centred is { } values ? TuningDigits.Round(values, Digits) : null;

    // ---- The variable list: Opt and Stat together ------------------------------------

    public ObservableCollection<YieldCenterRowViewModel> CenterRows { get; } = [];

    public bool HasCenterRows => CenterRows.Count > 0;

    private void RefreshCenterRows()
    {
        var entries = (Setup?.Variables ?? []).Where(e => e.Opt || e.IsStatistical).ToList();
        if (entries.Count > 0) _ = Catalog;
        var keep = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in CenterRows.Where(r => !keep.Contains(r.Key)).ToList()) CenterRows.Remove(row);
        for (int i = 0; i < entries.Count; i++)
        {
            var row = CenterRows.FirstOrDefault(r => r.Key == entries[i].Key);
            if (row is null)
            {
                row = new YieldCenterRowViewModel(entries[i].Key);
                CenterRows.Insert(Math.Min(i, CenterRows.Count), row);
            }
            else if (CenterRows.IndexOf(row) != i) CenterRows.Move(CenterRows.IndexOf(row), i);
            row.Bind(entries[i], _catalog?.Find(entries[i].Key));
        }
        ShowCentred();
        OnPropertyChanged(nameof(HasCenterRows));
        OnPropertyChanged(nameof(Digits));
        OnPropertyChanged(nameof(DigitsChoices));
    }

    // ---- The center settings (collapsed to a summary, as R-ya10-5's) -----------------

    public CenteringSettings CenterSettings => Setup?.Centering ?? new CenteringSettings();

    /// <summary>The registry's algorithms that suit a noisy objective and are built (YA-11 R-ya11-4).</summary>
    public IReadOnlyList<string> CenterAlgorithmChoices { get; } =
        [.. OptimizerAlgorithms.ForNoisyObjective.Where(OptimizerFactory.IsBuilt)];

    public IReadOnlyList<string> SurrogateChoices { get; } = [.. AnalysisDirectiveSchema.SurrogateTokens];

    [ObservableProperty] private bool _centerSettingsExpanded;

    [RelayCommand] private void ToggleCenterSettings() => CenterSettingsExpanded = !CenterSettingsExpanded;

    /// <summary><c>cmaes · 200 trials · verify 1000 · quadratic</c>.</summary>
    public string CenterSettingsSummary
    {
        get
        {
            var c = CenterSettings;
            var parts = new List<string> { c.EffectiveAlgorithm, $"{c.EffectiveTrials} trials", $"verify {c.EffectiveVerify}" };
            if (c.MaxIterations is { } mi) parts.Add($"≤ {mi} iterations");
            if (c.MaxEvaluations is { } me) parts.Add($"≤ {me} simulations");
            if (c.TimeLimit is { } tl) parts.Add($"≤ {tl}");
            if (c.EffectiveSurrogate != CenteringSurrogate.None) parts.Add(SurrogateChoices[(int)c.EffectiveSurrogate]);
            return string.Join(" · ", parts);
        }
    }

    public int CenterAlgorithmIndex
    {
        get => Math.Max(0, CenterAlgorithmChoices.ToList().IndexOf(CenterSettings.EffectiveAlgorithm));
        set
        {
            if (value < 0 || value >= CenterAlgorithmChoices.Count || CenterAlgorithmChoices[value] == CenterSettings.EffectiveAlgorithm) return;
            string id = CenterAlgorithmChoices[value];
            EditCentering(c => c.Algorithm = id == CenteringSettings.DefaultAlgorithm ? null : id, $"Centre with {id}");
        }
    }

    public string CenterTrialsText
    {
        get => CenterSettings.Trials?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditCenterInt(value, c => c.Trials, (c, v) => c.Trials = v, "Set the common trials", "a trial count");
    }

    public string CenterVerifyText
    {
        get => CenterSettings.Verify?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditCenterInt(value, c => c.Verify, (c, v) => c.Verify = v, "Set the verification trials", "a trial count");
    }

    public string CenterMaxIterText
    {
        get => CenterSettings.MaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditCenterInt(value, c => c.MaxIterations, (c, v) => c.MaxIterations = v, "Set the iteration limit", "an iteration count");
    }

    public string CenterMaxEvalsText
    {
        get => CenterSettings.MaxEvaluations?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditCenterInt(value, c => c.MaxEvaluations, (c, v) => c.MaxEvaluations = v, "Set the simulation limit", "a simulation count");
    }

    public int SurrogateIndex
    {
        get => (int)CenterSettings.EffectiveSurrogate;
        set
        {
            if (value < 0 || value >= SurrogateChoices.Count || value == SurrogateIndex) return;
            EditCentering(c => c.Surrogate = value == 0 ? null : (CenteringSurrogate)value,
                          value == 0 ? "Centre on simulated trials" : "Centre on the quadratic surrogate");
        }
    }

    private void EditCenterInt(string text, Func<CenteringSettings, int?> get, Action<CenteringSettings, int?> set,
                               string description, string what)
    {
        string t = text.Trim();
        int? v = null;
        if (t.Length > 0)
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                StatusText = $"Refused: '{text}' is not {what}.";
                RefreshCenterSettings();
                return;
            }
            v = n;
        }
        if (get(CenterSettings) == v) return;
        EditCentering(c => set(c, v), description);
    }

    /// <summary>The center line with <paramref name="change"/> applied, as one undo step.</summary>
    internal void EditCentering(Action<CenteringSettings> change, string description)
    {
        if (_tuned is null) return;
        var c = CenterSettings.Clone();
        change(c);
        Execute(TuningSetupEdits.WithCentering(Setup, c), description);
        RefreshCenterSettings();
    }

    private void RefreshCenterSettings()
    {
        foreach (var name in new[]
                 {
                     nameof(CenterSettingsSummary), nameof(CenterAlgorithmIndex), nameof(CenterTrialsText), nameof(CenterVerifyText),
                     nameof(CenterMaxIterText), nameof(CenterMaxEvalsText), nameof(SurrogateIndex),
                 })
            OnPropertyChanged(name);
    }

    // ---- The run --------------------------------------------------------------------

    private CenteringRun? _centerRun;
    private IReadOnlyDictionary<string, string>? _centred;
    private IReadOnlyDictionary<string, string>? _centerStart;
    private IReadOnlyList<RailedVariable> _centerRailed = [];

    /// <summary>The last centering run's result; null before one finishes.</summary>
    public CenteringResult? CenterResult { get; private set; }

    /// <summary>The centred nominals — what Lock in stores, Push writes and Send to Tuning loads; null before a run.</summary>
    public IReadOnlyDictionary<string, string>? CentredValues => _centred;

    /// <summary>The best yield on the common trials per iteration — the Optimizer's cost chart, yield on its axis.</summary>
    [ObservableProperty] private IReadOnlyList<double> _yieldHistory = [];

    public bool HasYieldHistory => YieldHistory.Count > 0;

    partial void OnYieldHistoryChanged(IReadOnlyList<double> value) => OnPropertyChanged(nameof(HasYieldHistory));

    /// <summary><c>94 %</c> — the best yield the search has found so far (on the common or virtual trials).</summary>
    [ObservableProperty] private string _searchYieldText = "";

    /// <summary><c>R² 0.98</c> under the surrogate; empty otherwise.</summary>
    [ObservableProperty] private string _fitText = "";

    /// <summary><c>12.0 % [8.1 %, 17.1 %] → 84.3 % [79.0 %, 88.7 %]</c> — the simulated verification.</summary>
    [ObservableProperty] private string _verificationText = "";

    public bool HasVerification => VerificationText.Length > 0;

    partial void OnVerificationTextChanged(string value) => OnPropertyChanged(nameof(HasVerification));

    private void ClearCenterReadouts()
    {
        _centerRun = null;
        _centred = _centerStart = null;
        _centerRailed = [];
        CenterResult = null;
        YieldHistory = [];
        SearchYieldText = FitText = VerificationText = "";
        ShowCentred();
    }

    private void RunCentering(PreparedCircuit circuit, string? source)
    {
        var cts = new System.Threading.CancellationTokenSource();
        string? path = source is null ? null : StatisticalRun.ResultPathFor(source);
        CenteringRun? run = null;
        run = CenteringRun.Create(circuit, new CenteringOptions
        {
            // The setup the bench netlists with — the schematic's own tuning block, as `yield center` reads it.
            Setup        = null,
            Cancellation = cts.Token,
            ResultPath   = path,
            Progress     = p => { var r = run; PostToUi(() => ApplyCenterProgress(r, p)); },
        });
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        _centerRun  = run;
        _cts        = cts;
        _resultPath = path;
        _display    = path is null ? null : DisplayFor?.Invoke(path);
        StatusText  = "";
        State       = YieldRunState.Running;
        HasRunReadouts = true;
        foreach (var note in run.Notes) ReportMessages?.Invoke($"Centering: {note.Render()}", []);

        _task = StartBackground(() =>
        {
            CenteringResult? result = null;
            string? crash = null;
            try { result = run.Run(); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() => FinishCentering(run, result, crash));
        });
    }

    private void ApplyCenterProgress(CenteringRun? run, CenteringProgress p)
    {
        if (run is null || !ReferenceEquals(run, _centerRun)) return;
        ElapsedText = Clock(p.Elapsed);
        if (p.Stage == "verify")
        {
            TrialsDoneText = $"verifying · {p.Evaluations} simulations";
            return;
        }
        TrialsDoneText = $"iteration {p.Iteration} · {p.Evaluations} simulations";
        if (double.IsFinite(p.BestYield)) YieldHistory = [.. YieldHistory, p.BestYield];
        SearchYieldText = double.IsFinite(p.BestYield) ? YieldGoalRowViewModel.Percent(p.BestYield) : "";
        FitText = p.RSquared is { Count: > 0 } r2 ? $"R² {r2.Values.Min().ToString("0.00", CultureInfo.InvariantCulture)}" : "";
        _centred = p.BestValues;
        _centerRailed = p.Railed;
        ShowCentred();
    }

    private void FinishCentering(CenteringRun run, CenteringResult? result, string? crash)
    {
        if (!ReferenceEquals(run, _centerRun)) return;            // abandoned meanwhile
        if (result is null)
        {
            State = YieldRunState.Idle;
            Problem($"The run failed: {crash}");
            _display?.Drop();
            return;
        }
        CenterResult = result;
        switch (result.Outcome)
        {
            case CenteringOutcome.Cancelled:
                State = YieldRunState.Idle;
                StatusText = "Cancelled";
                _display?.Drop();
                return;
            case CenteringOutcome.Refused:
                State = YieldRunState.Idle;
                Problem($"Refused: {result.Refusal?.Render() ?? result.FinishReason}");
                _display?.Drop();
                return;
        }

        State = YieldRunState.Finished;
        TrialsDoneText = $"{result.Iterations} iterations · {result.Evaluations} simulations";
        _centred = result.Best is null ? null : result.BestValues;
        _centerStart = result.Start?.Values;
        _centerRailed = result.Railed;
        ShowCentred();

        if (result.Verification is { } v)
        {
            VerificationText = $"{Bracketed(v.Start)} → {Bracketed(v.Best)}";
            ShowYield(v.Best, result.Verified?.Goals ?? []);
        }
        if (result.Verified?.Data is { } data) { _lastData = data; ShowTrialRows(data); }
        if (result.WrittenPath is { } written) _display?.Written(written);

        var lines = new List<string>();
        if (result.Verification is { } vv) lines.Add(vv.Sentence);
        foreach (var n in result.Notes) lines.Add(n.Render());
        string summary = char.ToUpperInvariant(result.FinishReason[0]) + result.FinishReason[1..] +
                         (result.Verification is { } v2 ? $" · verified {YieldGoalRowViewModel.Percent(v2.Best.Yield)}" : "") +
                         (result.Outcome == CenteringOutcome.BelowTarget ? " · below target" : "");
        StatusText   = summary;
        StatusDetail = string.Join(Environment.NewLine, lines);
        ReportMessages?.Invoke($"Centering: {summary}", lines);
        NotifyRunCommands();
    }

    /// <summary><c>84.3 % [79.0 %, 88.7 %]</c>.</summary>
    internal static string Bracketed(YieldEstimate e)
        => $"{YieldGoalRowViewModel.Percent(e.Yield)} [{YieldGoalRowViewModel.Percent(e.Lower)}, {YieldGoalRowViewModel.Percent(e.Upper)}]";

    private void ShowCentred()
    {
        int digits = Digits;
        var best = KeptCentred();
        foreach (var row in CenterRows)
        {
            var start = _centerStart ?? (_centred is null ? null : StartValuesOf(row));
            row.Show(start is null ? null : TuningDigits.Round(start, digits), best,
                     _centerRailed.FirstOrDefault(r => r.Key == row.Key));
        }
    }

    /// <summary>Before the result names the start, a row's start is the schematic's own value.</summary>
    private static IReadOnlyDictionary<string, string>? StartValuesOf(YieldCenterRowViewModel row)
        => row.Tunable is { } t ? new Dictionary<string, string> { [t.ValueKey] = t.ValueText } : null;

    // ---- Keeping the result (R-ya12-1) ------------------------------------------------

    private bool CanKeepCentred() => IsFinished && Mode == YieldMode.Centering && _centred is { Count: > 0 } && _tuned is not null;

    /// <summary>Lock in (TO-5): the centred nominals as a new preset.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepCentred))]
    private void LockInCentred()
    {
        if (_tuned is null || KeptCentred() is not { } values) return;
        var (setup, preset) = TuningPresets.LockIn(Setup, values, UtcNow(), null);
        Execute(setup, "Lock in centred nominals");
        StatusText = $"Locked in {preset.Name}";
    }

    /// <summary>Push (TO-4 R-to4-8): the centred nominals into the documents that own them, one undo step per document.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepCentred))]
    private void PushCentred()
    {
        if (_tuned is null || KeptCentred() is not { } values || Catalog is not { } catalog) return;
        var report = TuningPush.Push(catalog, values, _tuned, SessionForDrawing ?? (_ => null));
        if (report.UndoSession(_tuned) is { } undo) EditCommitted?.Invoke(undo);
        _catalog = null;
        RefreshNow();
        StatusText = report.StatusLine;
    }

    /// <summary>Loads the centred nominals into the Tuning sliders.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepCentred))]
    private void SendCentredToTuning()
    {
        if (KeptCentred() is not { } values || SendToTuningTarget is null) return;
        SendToTuningTarget(values, "Centred");
        StatusText = $"Sent {values.Count} value{(values.Count == 1 ? "" : "s")} to Tuning";
    }

    private void NotifyCenterCommands()
    {
        LockInCentredCommand.NotifyCanExecuteChanged();
        PushCentredCommand.NotifyCanExecuteChanged();
        SendCentredToTuningCommand.NotifyCanExecuteChanged();
    }
}
