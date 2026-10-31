// ================================================================
//  OptimizerPanelViewModel.cs  —  the Optimizer dock panel's state
//  (brief-tuneopt-10, overview D4/D5/D10–D18 and §3)
//
//  Follows the focused schematic exactly as the Tuning panel does. The
//  goals, the variable entries and the optimizer settings all live in
//  the tuned schematic's tuning block, so every change to them is a
//  document edit with one undo step (SetTuningSetupCommand). The run —
//  its progress, its best point, its result — is session state.
//
//  The run itself is the .Run.cs partial; this file is the lists and
//  the edits. Headless: the workspace supplies the prepared circuit,
//  the display and the sessions a Push writes into through delegates,
//  so every claim is tested without a shell.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Optimization;

/// <summary>One entry of the algorithm menu (the TO-7 registry).</summary>
public sealed partial class OptimizerAlgorithmItem(string id, string label, string useWhen) : ObservableObject
{
    public string Id      { get; } = id;
    public string Label   { get; } = label;
    public string UseWhen { get; } = useWhen;

    /// <summary>Why the design's variables rule it out — every optimized value a vendor part, and this a
    /// continuous-only method (brief vendorlib-4 R-vl4-5b); null when it can run. The entry is greyed and says why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(Tip))]
    private string? _disabledReason;

    public bool IsAvailable => DisabledReason is null;

    /// <summary>The menu entry's tooltip: when to use it, or why it cannot be used here.</summary>
    public string Tip => DisabledReason ?? UseWhen;

    public override string ToString() => Label;
}

public sealed partial class OptimizerPanelViewModel : ObservableObject, ITunableAddHost
{
    public const string NoSchematicText = "Focus a schematic to optimize it.";

    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The catalog of a schematic — the same discovery the Tuning panel uses.</summary>
    public Func<SchematicViewModel, TunableCatalog> Discover { get; set; } = vm =>
        TunableCatalog.Discover(vm.EditModel, vm.CellResolver ?? DiskCellResolver.Instance, vm.WorkspaceRoot);

    /// <summary>The session that edits a sub-cell's drawing, opening it as a tab without focus (D2).</summary>
    public Func<SchematicEditModel, SchematicViewModel?>? SessionForDrawing { get; set; }

    /// <summary>Defers a refresh off a model change; null runs it at once (tests).</summary>
    public Action<Action>? Defer { get; set; }

    /// <summary>Posts a summary and its detail lines to the Messages panel.</summary>
    public Action<string, IReadOnlyList<string>>? ReportMessages { get; set; }

    /// <summary>Send to Tuning: loads values into the Tuning panel's session (R-to10-8).</summary>
    public Action<IReadOnlyDictionary<string, string>>? SendToTuningTarget { get; set; }

    /// <summary>The clock a locked-in preset's time is taken from; tests replace it.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    // ---- State ----------------------------------------------------------------

    private SchematicViewModel? _tuned;
    private TunableCatalog?     _catalog;
    private bool                _catalogFailed, _refreshPending;
    private readonly List<SchematicEditModel> _watched = [];

    /// <summary>Keys whose opt flag this panel cleared, kept as rows until focus moves, so a row does
    /// not vanish from under the click that unticked it.</summary>
    private readonly HashSet<string> _unticked = new(StringComparer.Ordinal);

    public SchematicViewModel? Tuned => _tuned;

    public bool HasSchematic => _tuned is not null;

    [ObservableProperty] private string _headerLabel = "";

    /// <summary>What the last action did: a refusal, a Push's summary, a finished run's verdict.</summary>
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

    public ObservableCollection<OptimizerGoalRowViewModel> Goals { get; } = [];

    public ObservableCollection<OptimizerVariableRowViewModel> Variables { get; } = [];

    public bool HasGoals => Goals.Count > 0;
    public bool HasVariables => Variables.Count > 0;

    [ObservableProperty] private OptimizerGoalRowViewModel? _selectedGoal;

    partial void OnSelectedGoalChanged(OptimizerGoalRowViewModel? value) => NotifyGoalCommands();

    public TuningAddViewModel Add { get; }

    public OptimizerSettingsViewModel SettingsEditor { get; }

    /// <summary>The menu: the registry's algorithms this build implements, in the registry's order.</summary>
    public IReadOnlyList<OptimizerAlgorithmItem> Algorithms { get; } =
        [.. OptimizerAlgorithms.All.Where(a => OptimizationRun.Available.Contains(a.Id))
                                   .Select(a => new OptimizerAlgorithmItem(a.Id, a.Label, a.UseWhen))];

    [ObservableProperty] private OptimizerAlgorithmItem? _selectedAlgorithm;

    private bool _syncingAlgorithm;

    partial void OnSelectedAlgorithmChanged(OptimizerAlgorithmItem? value)
    {
        if (_syncingAlgorithm || value is null || _tuned is null) return;
        var s = Settings?.Clone() ?? new OptimizerSettings();
        if (s.Algorithm == value.Id) return;
        s.Algorithm = value.Id;
        EditSettings(s, $"Optimize with {value.Label}");
    }

    public OptimizerPanelViewModel()
    {
        Add            = new TuningAddViewModel(this);
        SettingsEditor = new OptimizerSettingsViewModel(this);
        Goals.CollectionChanged     += (_, _) => OnPropertyChanged(nameof(HasGoals));
        Variables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVariables));
    }

    /// <summary>The tuned schematic's optimizer settings; null when it has none.</summary>
    public OptimizerSettings? Settings => _tuned?.EditModel.Tuning?.Optimizer;

    private TuningSetup? Setup => _tuned?.EditModel.Tuning;

    // ---- Digits (the Tuning panel's setting, on the same schematic) ----------------

    /// <summary>The significant digits a best value is shown and kept with (<see cref="TuningDigits"/>).</summary>
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

    // ---- Following focus (overview §3) ------------------------------------------

    /// <summary>The focused tab's top-frame session for a <c>.csch</c>; null for anything else. A
    /// different schematic stops a running optimization first.</summary>
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
        _pushedFrom.Clear();
        HeaderLabel = tuned is null ? "" : displayName ?? "";
        StatusText = "";
        ClearRunReadouts();
        Goals.Clear();
        Variables.Clear();
        if (tuned is not null) RefreshNow();
        OnPropertyChanged(nameof(Tuned));
        OnPropertyChanged(nameof(HasSchematic));
        OnPropertyChanged(nameof(DigitsChoices));
        SetDigitsCommand.NotifyCanExecuteChanged();
        NotifyRunCommands();
    }

    // ---- The catalog and the lists -------------------------------------------

    public TunableCatalog? Catalog
    {
        get
        {
            if (_tuned is not null && _catalog is null && !_catalogFailed)
            {
                try { _catalog = Discover(_tuned); }
                catch { _catalogFailed = true; }
                Watch();
            }
            return _catalog;
        }
    }

    /// <summary>Rebuilds the goal and variable rows from the tuned schematic's tuning block.</summary>
    public void RefreshNow()
    {
        _refreshPending = false;
        if (_tuned is null) return;
        var setup = Setup;

        // Goals: one row per goal, in the setup's order.
        var goals = setup?.Goals ?? [];
        string? selected = SelectedGoal?.Name;
        while (Goals.Count > goals.Count) Goals.RemoveAt(Goals.Count - 1);
        for (int i = 0; i < goals.Count; i++)
        {
            if (i < Goals.Count) Goals[i].Bind(i, goals[i]);
            else Goals.Add(new OptimizerGoalRowViewModel(this, i, goals[i]));
            Goals[i].ShowReport(_lastGoals?.FirstOrDefault(r => r.Name == goals[i].Name));
        }
        SelectedGoal = Goals.FirstOrDefault(g => g.Name == selected) ?? (Goals.Count > 0 ? SelectedGoal : null);
        if (SelectedGoal is not null && !Goals.Contains(SelectedGoal)) SelectedGoal = null;

        // Variables: the shared entries this panel shows — opt-enabled, tune-enabled (a part that only
        // tunes still shows the value the run gave it, D18), and any it just unticked.
        var entries = (setup?.Variables ?? [])
            .Where(e => e.Opt || e.Tune || _unticked.Contains(e.Key)).ToList();
        if (entries.Count > 0) _ = Catalog;
        else Watch();
        var keep = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Variables.Where(r => !keep.Contains(r.Key)).ToList()) Variables.Remove(row);
        for (int i = 0; i < entries.Count; i++)
        {
            var row = Variables.FirstOrDefault(r => r.Key == entries[i].Key);
            if (row is null)
            {
                row = new OptimizerVariableRowViewModel(this, entries[i].Key);
                Variables.Insert(Math.Min(i, Variables.Count), row);
            }
            else if (Variables.IndexOf(row) != i)
            {
                Variables.Move(Variables.IndexOf(row), i);
            }
            row.Bind(entries[i], _catalog?.Find(entries[i].Key));
            row.ShowBest(_bestValues);
            row.ShowRailed(_lastRailed?.FirstOrDefault(r => r.Key == row.Key));
        }

        OnPropertyChanged(nameof(Digits));
        OnPropertyChanged(nameof(DigitsChoices));

        // Continuous-only methods are greyed, with the reason, when every optimized value is a vendor part (R-vl4-5b).
        var optimized = entries.Where(e => e.Opt).Select(e => _catalog?.Find(e.Key)).ToList();
        bool partsOnly = optimized.Count > 0 && optimized.All(t => t?.IsVendorPart == true);
        foreach (var a in Algorithms)
            a.DisabledReason = partsOnly && a.Id != OptimizerAlgorithms.Auto && a.Id != CircuitRF.Engine.Optimization.Discrete.AlgorithmId
                ? $"Every optimized value is a vendor part, whose levels are real parts with nothing between them; {a.Label} searches continuous values. Choose Discrete or Auto."
                : null;

        // The algorithm the document names.
        _syncingAlgorithm = true;
        string id = Settings?.Algorithm ?? OptimizerAlgorithms.Auto;
        SelectedAlgorithm = Algorithms.FirstOrDefault(a => a.Id == id) ?? Algorithms.FirstOrDefault();
        _syncingAlgorithm = false;

        if (Add.IsOpen) Add.Refresh();
        NotifyGoalCommands();
        NotifyRunCommands();
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
        var want = new List<SchematicEditModel>();
        if (_tuned is not null) want.Add(_tuned.EditModel);
        if (_catalog is not null)
            foreach (var d in _catalog.Drawings.Values)
                if (!want.Contains(d)) want.Add(d);
        foreach (var m in _watched.Except(want).ToList()) { m.Changed -= OnModelChanged; _watched.Remove(m); }
        foreach (var m in want.Except(_watched).ToList()) { m.Changed += OnModelChanged; _watched.Add(m); }
    }

    private void Unwatch()
    {
        foreach (var m in _watched) m.Changed -= OnModelChanged;
        _watched.Clear();
    }

    /// <summary>Replaces the tuned schematic's tuning block as one undo step.</summary>
    /// <summary>Raised after an edit from this panel lands on a schematic's undo stack, with the session
    /// it landed on — so ⌘Z works with the panel focused (the Tuning panel's <c>EditCommitted</c>).</summary>
    public event Action<SchematicViewModel>? EditCommitted;

    private void Execute(TuningSetup next, string description)
    {
        if (_tuned is null) return;
        _tuned.Execute(new SetTuningSetupCommand(_tuned.EditModel, next, description));
        EditCommitted?.Invoke(_tuned);
        if (Defer is not null) RefreshNow();
    }

    // ---- Variables (R-to10-2) -------------------------------------------------

    bool ITunableAddHost.IsActive(string key) => IsOptimized(key);

    /// <summary>Whether the entry for <paramref name="key"/> has its opt flag set — what the Inspector's optimize
    /// toggle shows (<see cref="IOptimizeSurface"/>).</summary>
    public bool IsOptimized(string key) => Setup?.Variables.Any(v => v.Key == key && v.Opt) == true;

    void ITunableAddHost.Activate(IReadOnlyCollection<string> keys) => SetOptimized(keys, on: true);

    /// <summary>Sets or clears the opt flag of every key, as ONE undo step. A part whose first
    /// activation would leave no value inside every range of its value is refused (D18).</summary>
    public void SetOptimized(IReadOnlyCollection<string> keys, bool on)
    {
        if (_tuned is null || Catalog is not { } catalog) return;
        var setup = Setup;
        int changed = 0;
        string? refused = null;
        foreach (var key in keys)
        {
            bool opt = setup?.Variables.Any(v => v.Key == key && v.Opt) == true;
            if (opt == on) continue;
            if (catalog.Find(key) is not { } t) continue;
            var next = TuningSetupEdits.WithOpt(setup, t, on);
            if (on && t.Part is not null && ComplexRegion.Conflict(next, key, t.WholeUnit) is { } why)
            {
                refused = why;
                continue;
            }
            setup = next;
            if (on) _unticked.Remove(key); else _unticked.Add(key);
            changed++;
        }
        if (refused is not null) StatusText = $"Not added: {refused}.";
        if (changed == 0) { RefreshNow(); return; }
        string what = changed == 1 ? keys.First() : $"{changed} values";
        Execute(setup!, on ? $"Optimize {what}" : $"Stop optimizing {what}");
    }

    internal void SetOptimized(OptimizerVariableRowViewModel row, bool on) => SetOptimized([row.Key], on);

    /// <summary>⋮ ▸ Remove: the row leaves this panel — its opt flag clears and it is not kept.</summary>
    internal void RemoveVariable(OptimizerVariableRowViewModel row)
    {
        _unticked.Remove(row.Key);
        if (Setup?.Variables.FirstOrDefault(v => v.Key == row.Key) is { Opt: true }) SetOptimized([row.Key], on: false);
        _unticked.Remove(row.Key);
        RefreshNow();
    }

    /// <summary>A range edit on a row — the same entry the Tuning panel edits — as one undo step.</summary>
    internal void EditRange(OptimizerVariableRowViewModel row, Action<TunableEntry> change, string description)
    {
        if (_tuned is null) return;
        var next = TuningSetupEdits.WithEntry(Setup, row.Key, row.Tunable, change);
        if (row.Tunable is { Part: not null } t && ComplexRegion.Conflict(next, row.Key, t.WholeUnit) is { } why)
        {
            StatusText = $"Refused: {why}.";
            RefreshNow();
            return;
        }
        Execute(next, description);
    }

    /// <summary>
    /// Widen (D17): doubles the span on the railed side — by ratio on a log range, by width on a linear
    /// one — of the entry whose range holds the value: the row's own, or the other part's it railed
    /// against (D18). One undo step. Widening only grows a range, so it is refused only when the
    /// value's ranges already conflict, or when a resistance, inductance or capacitance would cross
    /// zero (<see cref="TunableValue.FloorAtZero"/>).
    /// </summary>
    internal void Widen(OptimizerVariableRowViewModel row)
    {
        if (_tuned is null || row.Railed is not { } end || Catalog is not { } catalog) return;
        string key = row.RailedAgainst ?? row.Key;
        var entry  = Setup?.Variables.FirstOrDefault(v => v.Key == key);
        var t      = catalog.Find(key);
        if (entry is null || t is null) return;

        // A vendor part at the end of its candidates: the value bound on that side goes, so the candidates reach
        // further (brief vendorlib-4). With no bound there, the part is the library's own end and nothing widens.
        if (t.IsVendorPart)
        {
            EditRange(row, e => { if (end == RailEnd.Min) e.Min = null; else e.Max = null; },
                      $"Widen {key}'s candidates");
            return;
        }

        string u  = t.Unit;
        double lo = TunableValue.InUnit(entry.Min, u) ?? TunableValue.InUnit(t.DefaultMin, u) ?? 0;
        double hi = TunableValue.InUnit(entry.Max, u) ?? TunableValue.InUnit(t.DefaultMax, u) ?? 1;
        var scale = entry.Scale == TuneScale.Auto && t.Part == ComplexPart.Phase ? TuneScale.Lin : entry.Scale;
        bool log  = TunableValue.Effective(scale, lo, hi) == TuneScale.Log && lo > 0;
        double was = lo;
        (lo, hi) = WidenedRange(lo, hi, end, log);
        lo = TunableValue.FloorAtZero(t, was, lo);
        if (end == RailEnd.Min && lo == was)
        {
            StatusText = $"Refused: {key} cannot go below 0 — type a negative minimum to allow it.";
            return;
        }

        var next = TuningSetupEdits.WithEntry(Setup, key, t, e =>
        {
            e.Min = TuningRowViewModel.FormatValue(lo, u);
            e.Max = TuningRowViewModel.FormatValue(hi, u);
        });
        if (t.Part is not null && ComplexRegion.Conflict(next, key, t.WholeUnit) is { } why)
        {
            StatusText = $"Refused: {why}.";
            return;
        }
        Execute(next, $"Widen the range of {key}");
        StatusText = $"Widened {key} to {TuningRowViewModel.FormatValue(lo, u)} – {TuningRowViewModel.FormatValue(hi, u)}";
    }

    /// <summary>The range with its span doubled on <paramref name="end"/>'s side: by ratio when
    /// <paramref name="log"/>, by width otherwise.</summary>
    public static (double Lo, double Hi) WidenedRange(double lo, double hi, RailEnd end, bool log)
    {
        if (log)
        {
            double ratio = hi / lo;
            return end == RailEnd.Max ? (lo, hi * ratio) : (lo / ratio, hi);
        }
        double span = hi - lo;
        return end == RailEnd.Max ? (lo, hi + span) : (lo - span, hi);
    }

    // ---- Goals (R-to10-3) -----------------------------------------------------

    /// <summary>Raised to show the goal editor dialog; the view shows it and the editor's own
    /// <see cref="GoalEditorViewModel.Committed"/> writes the goal back.</summary>
    public event EventHandler<GoalEditorViewModel>? GoalEditorRequested;

    /// <summary>What the editor lists and validates against: the bench's templates, analyses and
    /// measurements. The workspace supplies it from the prepared circuit.</summary>
    public Func<SchematicViewModel, (IReadOnlyList<GoalTemplate> Templates, IReadOnlyList<Analysis> Analyses,
                                      IReadOnlyList<Measurement> Measurements)>? GoalContext { get; set; }

    /// <summary>Evaluates a goal over the current results for the editor's preview; null hides it.</summary>
    public Func<SchematicViewModel, Func<OptimizationGoal, System.Threading.CancellationToken,
                System.Threading.Tasks.Task<GoalPreview?>>?>? PreviewSource { get; set; }

    /// <summary>The editor for <paramref name="goal"/> at <paramref name="index"/> (null for a new goal),
    /// wired to write its result back as one undo step. Raised to the view, and returned.</summary>
    public GoalEditorViewModel? OpenGoalEditor(OptimizationGoal? goal, int? index)
    {
        if (_tuned is null) return null;
        var ctx = GoalContext?.Invoke(_tuned)
                  ?? ([], [.. _tuned.EditModel.Analyses], [.. _tuned.EditModel.Measurements]);
        var templates = ctx.Templates.Count > 0 ? ctx.Templates : GoalTemplates.For(new TestBench("tb"));
        var editor = new GoalEditorViewModel(templates, ctx.Analyses, ctx.Measurements, goal, PreviewSource?.Invoke(_tuned));
        editor.Committed += g => CommitGoal(g, index);
        GoalEditorRequested?.Invoke(this, editor);
        return editor;
    }

    /// <summary>"Add as goal…" from a trace (TO-9 R-to9-4): the editor, pre-filled. False when no view
    /// is listening, so the caller can fall back.</summary>
    public bool EditNewGoal(OptimizationGoal prefilled)
    {
        if (_tuned is null || GoalEditorRequested is null) return false;
        var g = prefilled.Clone();
        g.Name = UniqueGoalName(g.Name, Setup?.Goals ?? []);
        OpenGoalEditor(g, null);
        return true;
    }

    private void CommitGoal(OptimizationGoal goal, int? index)
    {
        if (_tuned is null) return;
        var next = Setup?.Clone() ?? new TuningSetup();
        if (index is { } i && i < next.Goals.Count)
        {
            if (next.Goals.Where((g, k) => k != i).Any(g => g.Name == goal.Name))
                goal.Name = UniqueGoalName(goal.Name, next.Goals.Where((g, k) => k != i));
            next.Goals[i] = goal;
            Execute(next, $"Edit goal {goal.Name}");
        }
        else
        {
            goal.Name = UniqueGoalName(goal.Name, next.Goals);
            next.Goals.Add(goal);
            Execute(next, $"Add goal {goal.Name}");
        }
        StatusText = "";
    }

    internal void SetGoalEnabled(OptimizerGoalRowViewModel row, bool enabled)
    {
        var next = Setup?.Clone();
        if (next is null || row.Index >= next.Goals.Count) return;
        next.Goals[row.Index].Enabled = enabled;
        Execute(next, enabled ? $"Enable goal {row.Name}" : $"Disable goal {row.Name}");
    }

    internal void SetGoalUse(OptimizerGoalRowViewModel row, GoalUse use)
    {
        if (Setup is null || row.Goal.Use == use) return;
        Execute(TuningSetupEdits.WithGoalUse(Setup, row.Name, use), $"Use goal {row.Name} for optimizing");
    }

    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void AddGoal() => OpenGoalEditor(null, null);

    [RelayCommand(CanExecute = nameof(HasSelectedGoal))]
    private void EditGoal()
    {
        if (SelectedGoal is { } row) OpenGoalEditor(row.Goal.Clone(), row.Index);
    }

    /// <summary>Double-click on a row.</summary>
    public void EditGoalRow(OptimizerGoalRowViewModel row)
    {
        SelectedGoal = row;
        OpenGoalEditor(row.Goal.Clone(), row.Index);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGoal))]
    private void DuplicateGoal()
    {
        if (SelectedGoal is not { } row || Setup?.Clone() is not { } next) return;
        var copy = row.Goal.Clone();
        copy.Name = UniqueGoalName(copy.Name, next.Goals);
        next.Goals.Insert(row.Index + 1, copy);
        Execute(next, $"Duplicate goal {row.Name}");
        SelectedGoal = Goals.FirstOrDefault(g => g.Name == copy.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGoal))]
    private void RemoveGoal()
    {
        if (SelectedGoal is not { } row || Setup?.Clone() is not { } next) return;
        next.Goals.RemoveAt(row.Index);
        SelectedGoal = null;
        Execute(next, $"Remove goal {row.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanMoveGoalUp))]
    private void MoveGoalUp() => MoveGoal(-1);

    [RelayCommand(CanExecute = nameof(CanMoveGoalDown))]
    private void MoveGoalDown() => MoveGoal(+1);

    private void MoveGoal(int delta)
    {
        if (SelectedGoal is not { } row || Setup?.Clone() is not { } next) return;
        int to = row.Index + delta;
        if (to < 0 || to >= next.Goals.Count) return;
        (next.Goals[row.Index], next.Goals[to]) = (next.Goals[to], next.Goals[row.Index]);
        string name = row.Name;
        Execute(next, $"Move goal {name}");
        SelectedGoal = Goals.FirstOrDefault(g => g.Name == name);
    }

    private bool HasSelectedGoal() => SelectedGoal is not null;
    private bool CanMoveGoalUp() => SelectedGoal is { Index: > 0 };
    private bool CanMoveGoalDown() => SelectedGoal is { } g && g.Index < Goals.Count - 1;

    private void NotifyGoalCommands()
    {
        AddGoalCommand.NotifyCanExecuteChanged();
        EditGoalCommand.NotifyCanExecuteChanged();
        DuplicateGoalCommand.NotifyCanExecuteChanged();
        RemoveGoalCommand.NotifyCanExecuteChanged();
        MoveGoalUpCommand.NotifyCanExecuteChanged();
        MoveGoalDownCommand.NotifyCanExecuteChanged();
    }

    internal static string UniqueGoalName(string name, IEnumerable<OptimizationGoal> goals)
    {
        var taken = goals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (int k = 2; ; k++)
            if (!taken.Contains($"{name}_{k}")) return $"{name}_{k}";
    }

    // ---- Settings (R-to10-5) --------------------------------------------------

    /// <summary>Writes <paramref name="settings"/> as one undo step — nothing when unchanged.</summary>
    internal void EditSettings(OptimizerSettings settings, string description)
    {
        if (_tuned is null) return;
        var next = Setup?.Clone() ?? new TuningSetup();
        if (SameSettings(next.Optimizer, settings)) return;
        next.Optimizer = settings;
        Execute(next, description);
    }

    private static bool SameSettings(OptimizerSettings? a, OptimizerSettings b)
    {
        a ??= new OptimizerSettings();
        static string Opts(OptimizerSettings s)
            => s.Options is null ? "" : string.Join(";", s.Options.Select(kv => kv.Key + "=" + kv.Value));
        return a.Algorithm == b.Algorithm && a.MaxIterations == b.MaxIterations && a.MaxEvaluations == b.MaxEvaluations
            && a.TimeLimit == b.TimeLimit && a.Cost == b.Cost && a.Scope == b.Scope && a.Seed == b.Seed
            && a.Parallelism == b.Parallelism && a.Corners == b.Corners && a.Nominal == b.Nominal && Opts(a) == Opts(b);
    }
}
