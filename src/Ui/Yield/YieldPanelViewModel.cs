// ================================================================
//  YieldPanelViewModel.cs  —  the Yield dock panel's state
//  (brief-yield-10, yield overview D1, D4, D7, D8, D14 and §3)
//
//  Follows the focused schematic exactly as Tuning and the Optimizer
//  do. The tolerances, the goals' use=, the statistics line, the
//  correlations and the corners all live in the schematic's ONE tuning
//  block (D1), so every change to them is a document edit with one undo
//  step (SetTuningSetupCommand) — refused, before it lands, in check's
//  own words when it would add an error (StatisticsValidator.EditRefusal).
//  A run, its readouts and its trial table are session state.
//
//  This file is the lists and the edits; the run is the .Run.cs partial
//  and the corners the .Corners.cs one. Headless: the workspace supplies
//  everything the panel cannot reach through delegates, so every claim
//  is tested without a shell.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Yield;

/// <summary>What the panel does (R-ya10-2, R-ya12-1).</summary>
public enum YieldMode { MonteCarlo, Yield, Corners, Centering, Doe }

public sealed partial class YieldPanelViewModel : ObservableObject, ITunableAddHost, IToleranceSurface
{
    public const string NoSchematicText = "Focus a schematic to run a yield analysis on it.";

    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The catalog of a schematic — the same discovery Tuning and the Optimizer use.</summary>
    public Func<SchematicViewModel, TunableCatalog> Discover { get; set; } = vm =>
        TunableCatalog.Discover(vm.EditModel, vm.CellResolver ?? DiskCellResolver.Instance, vm.WorkspaceRoot);

    /// <summary>Defers a refresh off a model change; null runs it at once (tests).</summary>
    public Action<Action>? Defer { get; set; }

    /// <summary>Posts a summary and its detail lines to the Messages panel.</summary>
    public Action<string, IReadOnlyList<string>>? ReportMessages { get; set; }

    /// <summary>Edit goals…: focuses the Optimizer with the named goal selected (goals are authored there, D4).</summary>
    public Action<string?>? EditGoalInOptimizer { get; set; }

    /// <summary>What the design's kits contribute (R-ya10-3's Kit statistics row); null when nothing is known.</summary>
    public Func<SchematicViewModel, KitStatisticsReport?>? KitStatisticsFor { get; set; }

    /// <summary>Shows the corner picker (the Analyses panel's) — the Kit statistics row's link.</summary>
    public Action? OpenCornerPicker { get; set; }

    // ---- State ----------------------------------------------------------------

    private SchematicViewModel? _tuned;
    private TunableCatalog?     _catalog;
    private bool                _catalogFailed, _refreshPending;
    private SchematicEditModel? _watched;

    /// <summary>Keys whose tolerance this panel switched off, kept as rows until focus moves, so a row does not
    /// vanish from under the click that unticked it.</summary>
    private readonly HashSet<string> _unticked = new(StringComparer.Ordinal);

    public SchematicViewModel? Tuned => _tuned;

    public bool HasSchematic => _tuned is not null;

    [ObservableProperty] private string _headerLabel = "";

    /// <summary>What the last action did: a refusal in check's words, a run's verdict.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The full text behind the status line, for its tooltip.</summary>
    [ObservableProperty] private string? _statusDetail;

    partial void OnStatusTextChanged(string value) => StatusDetail = null;

    /// <summary>Posts a refusal or a failure to the Messages panel (issue #33): a run that did not happen, or ended
    /// without a result, must not be said only in a status line that the next action overwrites.</summary>
    public Action<string>? ReportProblem { get; set; }

    /// <summary>The status line AND the Messages panel, for a run's refusal or failure.</summary>
    private void Problem(string text)
    {
        StatusText = text;
        ReportProblem?.Invoke(text);
    }

    // ---- Mode (R-ya10-2) --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonteCarlo), nameof(IsYield), nameof(IsCorners), nameof(IsCentering), nameof(IsDoe), nameof(ShowTrials),
                              nameof(ShowYieldReadout), nameof(ShowVariableTolerances), nameof(ShowStatisticsSettings), nameof(RunTip))]
    private YieldMode _mode = YieldMode.Yield;

    public bool IsMonteCarlo { get => Mode == YieldMode.MonteCarlo; set { if (value) Mode = YieldMode.MonteCarlo; } }
    public bool IsYield      { get => Mode == YieldMode.Yield;      set { if (value) Mode = YieldMode.Yield; } }
    public bool IsCorners    { get => Mode == YieldMode.Corners;    set { if (value) Mode = YieldMode.Corners; } }

    /// <summary>The trial table and its readouts belong to a Monte Carlo or a yield run.</summary>
    public bool ShowTrials => Mode is not (YieldMode.Corners or YieldMode.Doe);

    /// <summary>A yield readout needs yield goals; a Monte Carlo reports spread only. Centering shows its verified yield.</summary>
    public bool ShowYieldReadout => Mode is YieldMode.Yield or YieldMode.Centering;

    public string RunTip => Mode switch
    {
        YieldMode.MonteCarlo => "Run a Monte Carlo",
        YieldMode.Yield      => "Run a yield analysis",
        YieldMode.Centering  => "Centre the design — move the designable nominals to maximize yield",
        YieldMode.Doe        => "Run the design of experiments — which factors matter, and how they interact",
        _                    => "Evaluate every enabled corner",
    };

    partial void OnModeChanged(YieldMode value)
    {
        NotifyRunCommands();
        if (value == YieldMode.Doe) RefreshDoePlan();
    }

    // ---- Lists -------------------------------------------------------------------

    public ObservableCollection<YieldVariableRowViewModel> Variables { get; } = [];

    public ObservableCollection<YieldGoalRowViewModel> Goals { get; } = [];

    public bool HasVariables => Variables.Count > 0;
    public bool HasGoals => Goals.Count > 0;

    [ObservableProperty] private YieldGoalRowViewModel? _selectedGoal;

    partial void OnSelectedGoalChanged(YieldGoalRowViewModel? value) => ContributionsCommand.NotifyCanExecuteChanged();

    public TuningAddViewModel Add { get; }

    public YieldPanelViewModel()
    {
        Add = new TuningAddViewModel(this);
        Variables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVariables));
        Goals.CollectionChanged     += (_, _) => OnPropertyChanged(nameof(HasGoals));
        Trials.CollectionChanged    += (_, _) => OnPropertyChanged(nameof(HasTrials));
        Corners.CollectionChanged   += (_, _) => OnPropertyChanged(nameof(HasCorners));
    }

    private TuningSetup? Setup => _tuned?.EditModel.Tuning;

    /// <summary>The statistics line as it stands — every default held as null.</summary>
    public StatisticsSettings Settings => Setup?.Statistics ?? new StatisticsSettings();

    /// <summary>Raised when what the Inspector's tolerance toggle and the canvas ask may have changed.</summary>
    public event EventHandler? Changed;

    // ---- Following focus ---------------------------------------------------------

    /// <summary>The focused tab's top-frame session for a <c>.csch</c>; null empties the panel. A different schematic
    /// abandons a run in flight.</summary>
    public void SetActiveSchematic(SchematicViewModel? tuned, string? displayName)
    {
        if (ReferenceEquals(tuned, _tuned))
        {
            HeaderLabel = tuned is null ? "" : displayName ?? HeaderLabel;
            return;
        }

        if (IsRunActive) Abandon();
        Unwatch();
        _tuned = tuned;
        _catalog = null;
        _catalogFailed = false;
        _unticked.Clear();
        HeaderLabel = tuned is null ? "" : displayName ?? "";
        StatusText = "";
        ClearRunReadouts();
        Variables.Clear();
        Goals.Clear();
        Corners.Clear();
        CenterRows.Clear();
        if (tuned is not null) RefreshNow();
        RefreshKit();
        OnPropertyChanged(nameof(Tuned));
        OnPropertyChanged(nameof(HasSchematic));
        OnPropertyChanged(nameof(DigitsChoices));
        SetDigitsCommand.NotifyCanExecuteChanged();
        NotifyRunCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TunableCatalog? Catalog
    {
        get
        {
            if (_tuned is not null && _catalog is null && !_catalogFailed)
            {
                try { _catalog = Discover(_tuned); }
                catch { _catalogFailed = true; }
            }
            return _catalog;
        }
    }

    /// <summary>Rebuilds the rows from the tuned schematic's tuning block.</summary>
    public void RefreshNow()
    {
        _refreshPending = false;
        if (_tuned is null) return;
        Watch();
        var setup = Setup;

        // Variables: every entry carrying a distribution (on or off), and any this panel just switched off.
        var entries = (setup?.Variables ?? [])
            .Where(e => e.Distribution != StatDistribution.None || _unticked.Contains(e.Key)).ToList();
        if (entries.Count > 0) _ = Catalog;
        var keep = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Variables.Where(r => !keep.Contains(r.Key)).ToList()) Variables.Remove(row);
        for (int i = 0; i < entries.Count; i++)
        {
            var row = Variables.FirstOrDefault(r => r.Key == entries[i].Key);
            if (row is null)
            {
                row = new YieldVariableRowViewModel(this, entries[i].Key);
                Variables.Insert(Math.Min(i, Variables.Count), row);
            }
            else if (Variables.IndexOf(row) != i) Variables.Move(Variables.IndexOf(row), i);
            row.Bind(entries[i], _catalog?.Find(entries[i].Key));
            row.ShowShare(_shares?.GetValueOrDefault(row.Key));
        }

        // Goals: every goal, in the setup's order.
        var goals = setup?.Goals ?? [];
        string? selected = SelectedGoal?.Name;
        while (Goals.Count > goals.Count) Goals.RemoveAt(Goals.Count - 1);
        for (int i = 0; i < goals.Count; i++)
        {
            if (i < Goals.Count) Goals[i].Bind(goals[i]);
            else Goals.Add(new YieldGoalRowViewModel(this, goals[i]));
            Goals[i].ShowYield(_goalYields?.FirstOrDefault(y => y.Goal == goals[i].Name)?.Estimate, TargetFraction);
        }
        SelectedGoal = Goals.FirstOrDefault(g => g.Name == selected);

        RefreshCorners();
        RefreshCenterRows();
        RefreshSettings();
        RefreshCenterSettings();
        RefreshDoeSettings();
        if (Add.IsOpen) Add.Refresh();
        NotifyRunCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        _catalog = null;
        _catalogFailed = false;
        if (Defer is null) { RefreshNow(); return; }
        if (_refreshPending) return;
        _refreshPending = true;
        Defer(() => { if (_refreshPending) RefreshNow(); });
    }

    private void Watch()
    {
        var want = _tuned?.EditModel;
        if (ReferenceEquals(want, _watched)) return;
        Unwatch();
        if (want is null) return;
        want.Changed += OnModelChanged;
        _watched = want;
    }

    private void Unwatch()
    {
        if (_watched is not null) _watched.Changed -= OnModelChanged;
        _watched = null;
    }

    /// <summary>Raised after an edit from this panel lands on a schematic's undo stack, so ⌘Z works with the panel
    /// focused (Tuning's and the Optimizer's <c>EditCommitted</c>).</summary>
    public event Action<SchematicViewModel>? EditCommitted;

    /// <summary>
    /// Replaces the tuned schematic's tuning block with <paramref name="next"/> as one undo step — unless that adds an
    /// error <c>check</c> would report, which is the edit's refusal, inline, in the same words (R-ya10-3). Returns the
    /// refusal's sentence, or null when the edit landed.
    /// </summary>
    internal string? Execute(TuningSetup next, string description)
    {
        if (_tuned is null) return null;
        if (Catalog is { } catalog && StatisticsValidator.EditRefusal(Setup, next, catalog) is { } refusal)
        {
            StatusText = $"Refused: {refusal.Render()}";
            RefreshNow();
            return StatusText;
        }
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, next, description));
        EditCommitted?.Invoke(_tuned);
        if (Defer is not null) RefreshNow();
        return null;
    }

    // ---- Tolerances (R-ya10-3) -----------------------------------------------------

    bool ITunableAddHost.IsActive(string key) => IsToleranced(key);

    bool ITunableAddHost.Offers(Tunable t) => !t.IsVendorPart;

    void ITunableAddHost.Activate(IReadOnlyCollection<string> keys) => SetToleranced(keys, on: true);

    public bool IsToleranced(string key) => Setup?.Variables.Any(v => v.Key == key && v.IsStatistical) == true;

    public void SetToleranced(string key, bool on) => SetToleranced([key], on);

    void IToleranceSurface.Reveal(string key) { }

    /// <summary>Sets or clears the stat flag of every key as ONE undo step. A key with no entry gets one with
    /// <c>tune</c> and <c>opt</c> off and the default tolerance.</summary>
    public void SetToleranced(IReadOnlyCollection<string> keys, bool on)
    {
        if (_tuned is null || Catalog is not { } catalog) return;
        var setup = Setup;
        int changed = 0;
        foreach (var key in keys)
        {
            bool now = setup?.Variables.Any(v => v.Key == key && v.IsStatistical) == true;
            if (now == on) continue;
            // A vendor part is chosen, never drawn: its static model states no tolerance (brief vendorlib-4 R-vl4-3d).
            if (catalog.Find(key) is not { IsVendorPart: false } t) continue;
            setup = TuningSetupEdits.WithStat(setup, t, on);
            if (on) _unticked.Remove(key); else _unticked.Add(key);
            changed++;
        }
        if (changed == 0) { RefreshNow(); return; }
        string what = changed == 1 ? keys.First() : $"{changed} values";
        Execute(setup!, on ? $"Add a tolerance to {what}" : $"Switch off the tolerance of {what}");
    }

    internal void SetStat(YieldVariableRowViewModel row, bool on) => SetToleranced([row.Key], on);

    /// <summary>⋮ ▸ Remove tolerance: the distribution and spread go, with any correlation naming it.</summary>
    internal void RemoveTolerance(YieldVariableRowViewModel row)
    {
        _unticked.Remove(row.Key);
        Execute(TuningSetupEdits.WithoutTolerance(Setup, row.Key, row.Tunable), $"Remove the tolerance of {row.Key}");
    }

    internal void SetDistribution(YieldVariableRowViewModel row, StatDistribution d)
    {
        if (Setup?.Variables.FirstOrDefault(v => v.Key == row.Key) is { } e && e.Distribution == d) return;
        Execute(TuningSetupEdits.WithDistribution(Setup, row.Key, row.Tunable, d),
                $"Draw {row.Key} from {DistributionChoice.Word(d)}");
    }

    /// <summary>The compact spread editor's text (<see cref="ToleranceText"/>), as one undo step or a refusal.</summary>
    internal string? EditSpread(YieldVariableRowViewModel row, string text)
    {
        var entry = Setup?.Variables.FirstOrDefault(v => v.Key == row.Key);
        var d = entry?.Distribution ?? StatDistribution.None;
        if (d == StatDistribution.None) return null;
        var spread = ToleranceText.Parse(text, d, entry?.Spread);
        if (spread is null)
        {
            StatusText = $"Refused: '{text}' is not a spread — write ± 2 %, σ 1 Ω, 45 … 55 Ω or 4 … 8 by 2.";
            RefreshNow();
            return StatusText;
        }
        if (ToleranceText.WrongShape(spread, d) is { } shape)
        {
            StatusText = $"Refused: {shape}.";
            RefreshNow();
            return StatusText;
        }
        if (ToleranceText.Format(d, spread) == ToleranceText.Format(d, entry?.Spread)) return null;
        return Execute(TuningSetupEdits.WithEntry(Setup, row.Key, row.Tunable, e => e.Spread = spread),
                       $"Set the spread of {row.Key}");
    }

    // ---- Goals (R-ya10-4) ----------------------------------------------------------

    internal void SetGoalUse(YieldGoalRowViewModel row, GoalUse use)
    {
        if (Setup?.Goals.FirstOrDefault(g => g.Name == row.Name) is not { } g || g.Use == use) return;
        Execute(TuningSetupEdits.WithGoalUse(Setup, row.Name, use), $"Use goal {row.Name} for {GoalUseChoice.Word(use)}");
    }

    /// <summary>Edit goals…: goals are authored in the Optimizer (D4), so it is focused with the selected goal.</summary>
    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void EditGoals() => EditGoalInOptimizer?.Invoke(SelectedGoal?.Name);

    // ---- Correlations (R-ya10-3) ---------------------------------------------------

    /// <summary>Raised to show the correlation grid; the view shows it and the editor's own Commit writes back.</summary>
    public event EventHandler<CorrelationsEditorViewModel>? CorrelationsRequested;

    private bool CanEditCorrelations() => Setup?.Variables.Count(v => v.IsStatistical) >= 2;

    [RelayCommand(CanExecute = nameof(CanEditCorrelations))]
    private void EditCorrelations() => OpenCorrelations();

    /// <summary>The grid over the stat entries, wired to write back as one undo step. Raised to the view, and returned.</summary>
    public CorrelationsEditorViewModel? OpenCorrelations()
    {
        if (_tuned is null || Setup is not { } setup) return null;
        var editor = new CorrelationsEditorViewModel(setup);
        editor.Committed += list => editor.Refusal = Execute(TuningSetupEdits.WithCorrelations(Setup, list), "Set correlations");
        CorrelationsRequested?.Invoke(this, editor);
        return editor;
    }

    // ---- Kit statistics (R-ya10-3) -------------------------------------------------

    [ObservableProperty] private bool _hasKitStatistics;
    [ObservableProperty] private string _kitStreamsText = "";
    [ObservableProperty] private string _kitSectionsText = "";
    [ObservableProperty] private string? _kitNote;

    public bool KitNeedsSection => KitNote is not null;

    partial void OnKitNoteChanged(string? value) => OnPropertyChanged(nameof(KitNeedsSection));

    /// <summary>Kit process draws (<c>statistics process=</c>); on unless switched off.</summary>
    public bool KitProcess
    {
        get => Settings.Process ?? true;
        set { if (value != KitProcess) EditSettings(s => s.Process = value ? null : false, value ? "Draw kit process statistics" : "Hold kit process statistics at nominal"); }
    }

    /// <summary>Kit mismatch draws (<c>statistics mismatch=</c>); on unless switched off.</summary>
    public bool KitMismatch
    {
        get => Settings.Mismatch ?? true;
        set { if (value != KitMismatch) EditSettings(s => s.Mismatch = value ? null : false, value ? "Draw kit mismatch statistics" : "Hold kit mismatch statistics at nominal"); }
    }

    /// <summary>Asks the workspace what the kits draw — on focus and after a run, never on every edit: it elaborates
    /// the design.</summary>
    private void RefreshKit()
    {
        var report = _tuned is null ? null : SafeKit(_tuned);
        HasKitStatistics = report is { } r && (r.Any || r.Notes.Count > 0);
        KitStreamsText  = report is null ? "" : $"{report.Process} process · {report.Mismatch} mismatch";
        KitSectionsText = report is null ? "" : string.Join(", ", report.Sections);
        KitNote         = report?.Notes.FirstOrDefault()?.Render();
        OnPropertyChanged(nameof(KitProcess));
        OnPropertyChanged(nameof(KitMismatch));
    }

    private KitStatisticsReport? SafeKit(SchematicViewModel tuned)
    {
        try { return KitStatisticsFor?.Invoke(tuned); }
        catch { return null; }
    }

    [RelayCommand] private void ShowCornerPicker() => OpenCornerPicker?.Invoke();

    // ---- Run settings (R-ya10-5) ----------------------------------------------------

    public IReadOnlyList<string> SamplingChoices { get; } = ["random", "lhs", "sobol"];

    public IReadOnlyList<string> NonConvergedChoices { get; } = ["Fail", "Warn"];

    [ObservableProperty] private bool _settingsExpanded;

    /// <summary><c>500 trials · seed 1 · random · target 95 %</c>.</summary>
    public string SettingsSummary
    {
        get
        {
            var s = Settings;
            var parts = new List<string>
            {
                $"{s.EffectiveTrials} trials", $"seed {s.EffectiveSeed}", SamplingChoices[(int)s.Sampling],
            };
            if (s.Target is { } t) parts.Add($"target {Number(t)} %");
            if (s.AutoStop) parts.Add("auto-stop");
            return string.Join(" · ", parts);
        }
    }

    public string TrialsText
    {
        get => Settings.Trials?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditInt(value, s => s.Trials, (s, v) => s.Trials = v, min: 1, "Set the trial count", "a trial count");
    }

    public string SeedText
    {
        get => Settings.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditInt(value, s => s.Seed, (s, v) => s.Seed = v, min: 0, "Set the seed", "a seed");
    }

    public int SamplingIndex
    {
        get => (int)Settings.Sampling;
        set { if (value >= 0 && value != SamplingIndex) EditSettings(s => s.Sampling = (StatSampling)value, $"Sample {SamplingChoices[value]}"); }
    }

    public string TargetText
    {
        get => Settings.Target is { } t ? Number(t) : "";
        set => EditPercent(value, (s, v) => s.Target = v, "Set the yield target", "a target");
    }

    public string ConfidenceText
    {
        get => Settings.Confidence is { } c ? Number(c) : "";
        set => EditPercent(value, (s, v) => s.Confidence = v, "Set the confidence", "a confidence");
    }

    public bool AutoStop
    {
        get => Settings.AutoStop;
        set { if (value != AutoStop) EditSettings(s => s.AutoStop = value, value ? "Stop automatically at the target" : "Run every trial"); }
    }

    public int NonConvergedIndex
    {
        get => (int)Settings.NonConverged;
        set { if (value >= 0 && value != NonConvergedIndex) EditSettings(s => s.NonConverged = (NonConvergedPolicy)value, value == 0 ? "Count a trial that did not evaluate as a fail" : "Exclude a trial that did not evaluate"); }
    }

    /// <summary><c>auto</c>, <c>scalars</c>, <c>all</c> or the first N trials.</summary>
    public string SaveText
    {
        get => Settings.Save ?? "";
        set
        {
            string v = value.Trim().ToLowerInvariant();
            if (v is "" or "auto") { EditSettings(s => s.Save = null, "Save automatically"); return; }
            if (v is "scalars" or "all" || (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0))
            { EditSettings(s => s.Save = v, $"Save {v}"); return; }
            StatusText = $"Refused: '{value}' is not auto, scalars, all or a trial count.";
            OnPropertyChanged(nameof(SaveText));
        }
    }

    public string ParallelText
    {
        get => Settings.Parallelism?.ToString(CultureInfo.InvariantCulture) ?? "";
        set => EditInt(value, s => s.Parallelism, (s, v) => s.Parallelism = v, min: 1, "Set parallel trials", "a count");
    }

    /// <summary>The dice: a new seed, as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void NewSeed()
    {
        int seed = Random.Shared.Next(2, 1_000_000);
        EditSettings(s => s.Seed = seed, "New seed");
    }

    [RelayCommand] private void ToggleSettings() => SettingsExpanded = !SettingsExpanded;

    private void EditInt(string text, Func<StatisticsSettings, int?> get, Action<StatisticsSettings, int?> set, int min,
                         string description, string what)
    {
        string t = text.Trim();
        int? v = null;
        if (t.Length > 0)
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min)
            {
                StatusText = $"Refused: '{text}' is not {what}.";
                RefreshSettings();
                return;
            }
            v = n;
        }
        if (get(Settings) == v) return;
        EditSettings(s => set(s, v), description);
    }

    private void EditPercent(string text, Action<StatisticsSettings, double?> set, string description, string what)
    {
        string t = text.Trim().TrimEnd('%').Trim();
        double? v = null;
        if (t.Length > 0)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !(n > 0 && n < 100))
            {
                StatusText = $"Refused: '{text}' is not {what} in percent, above 0 and below 100.";
                RefreshSettings();
                return;
            }
            v = n;
        }
        EditSettings(s => set(s, v), description);
    }

    /// <summary>The statistics line with <paramref name="change"/> applied, as one undo step — nothing when unchanged.</summary>
    internal void EditSettings(Action<StatisticsSettings> change, string description)
    {
        if (_tuned is null) return;
        var s = Settings.Clone();
        change(s);
        var next = TuningSetupEdits.WithStatistics(Setup, s);
        if (TuningDirectiveSame(Setup?.Statistics, next.Statistics)) { RefreshSettings(); return; }
        Execute(next, description);
        RefreshSettings();
    }

    private static bool TuningDirectiveSame(StatisticsSettings? a, StatisticsSettings? b)
        => (a is null || TuningSetupEdits.IsDefault(a)) && (b is null || TuningSetupEdits.IsDefault(b))
           || (a is not null && b is not null && Describe(a) == Describe(b));

    private static string Describe(StatisticsSettings s)
        => string.Join("|", s.Trials, s.Seed, s.Sampling, s.Target, s.Confidence, s.AutoStop, s.NonConverged, s.Save,
                       s.Process, s.Mismatch, s.SigmaScale, s.Parallelism, s.Scope, s.Corners);

    private void RefreshSettings()
    {
        foreach (var name in new[]
                 {
                     nameof(SettingsSummary), nameof(TrialsText), nameof(SeedText), nameof(SamplingIndex), nameof(TargetText),
                     nameof(ConfidenceText), nameof(AutoStop), nameof(NonConvergedIndex), nameof(SaveText),
                     nameof(ParallelText), nameof(KitProcess), nameof(KitMismatch), nameof(TargetFraction),
                 })
            OnPropertyChanged(name);
    }

    internal static string Number(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

/// <summary>One entry of a distribution drop-down.</summary>
public sealed record DistributionChoice(StatDistribution Value, string Label)
{
    public static IReadOnlyList<DistributionChoice> All { get; } =
    [
        new(StatDistribution.Gauss, "Gaussian"),
        new(StatDistribution.Unif, "Uniform"),
        new(StatDistribution.LogNorm, "Lognormal"),
        new(StatDistribution.Discrete, "Discrete"),
    ];

    public static string Word(StatDistribution d) => All.FirstOrDefault(c => c.Value == d)?.Label.ToLowerInvariant() ?? "nothing";

    public override string ToString() => Label;
}

/// <summary>One entry of a goal's Use drop-down (yield overview D4).</summary>
public sealed record GoalUseChoice(GoalUse Value, string Label)
{
    public static IReadOnlyList<GoalUseChoice> All { get; } =
        [new(GoalUse.Opt, "Opt"), new(GoalUse.Yield, "Yield"), new(GoalUse.Both, "Both")];

    public static string Word(GoalUse u) => u switch { GoalUse.Opt => "optimizing", GoalUse.Yield => "yield", _ => "both" };

    public override string ToString() => Label;
}
