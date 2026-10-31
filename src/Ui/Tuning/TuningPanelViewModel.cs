// ================================================================
//  TuningPanelViewModel.cs  —  the Tuning dock panel's state
//  (brief-tuneopt-4, overview §3)
//
//  Follows the focused schematic (the InstancesTool rule): cleared on
//  every focus change and set again only for a .csch. Owns at most one
//  TuneSession (TO-3). Moving a slider before Start only moves the
//  slider. Every change to the entries is a document edit in the tuned
//  schematic; the tuned VALUES are session state and dirty nothing
//  until Push.
//
//  A complex value is tuned through its parts (overview D18). The
//  panel holds ONE complex number per value, every part row is a view
//  of it, and that whole number — never a part — is what the session
//  evaluates and Push writes. A part's move stops at the edge of the
//  ranges of all that value's parts, and a range edit that would leave
//  no value inside all of them is refused.
//
//  Presets — lock in, recall, "Last tuned", compare — are the
//  .Presets.cs partial (TO-5).
//
//  Headless: the workspace supplies the session, the sub-cell sessions
//  and the canvas reveal through delegates, so every claim here is
//  tested without a shell.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tuning;

/// <summary>The header's status dot (R-to4-6).</summary>
public enum TuningStatus { Idle, Running, Lagging }

public sealed partial class TuningPanelViewModel : ObservableObject, ITuningSurface, ITunableAddHost
{
    /// <summary>A parametric sweep above this many points earns a warning in the analysis scope (R-to4-5).</summary>
    public const long SweepWarningPoints = 50;

    public const string NoSchematicText = "Focus a schematic to tune it.";

    // ---- What the workspace supplies ----------------------------------------

    /// <summary>The catalog of a schematic. Defaults to <see cref="TunableCatalog.Discover"/> through the
    /// session's own resolver — the one Simulate uses.</summary>
    public Func<SchematicViewModel, TunableCatalog> Discover { get; set; } = vm =>
        TunableCatalog.Discover(vm.EditModel, vm.CellResolver ?? DiskCellResolver.Instance, vm.WorkspaceRoot);

    /// <summary>Starts a session over the tuned schematic, narrowed to the named analyses (null = every
    /// enabled one). Null leaves Start unavailable.</summary>
    public Func<SchematicViewModel, IReadOnlyList<string>?, TuneSession?>? CreateSession { get; set; }

    /// <summary>The preferred-value ladders a <c>discrete=preferred</c> row steps along (the user's); null is the
    /// shipped ones.</summary>
    public Func<CircuitRF.Design.Matching.PreferredLadders?>? Ladders { get; set; }

    /// <summary>The session that edits a sub-cell's drawing, opening it as a tab WITHOUT focus when it
    /// has none (overview D2) — Push's only way into another document.</summary>
    public Func<SchematicEditModel, SchematicViewModel?>? SessionForDrawing { get; set; }

    /// <summary>The session already editing a drawing, or null — never opens anything. The canvas
    /// colouring asks this, because colouring a cell nobody is looking at is not a reason for a tab.</summary>
    public Func<SchematicEditModel, SchematicViewModel?>? ExistingSessionFor { get; set; }

    /// <summary>What each named analysis costs per evaluation — its leaf points and the parametric-sweep
    /// points among them (the ⚙ scope list). One call for all of them: the design is prepared once.</summary>
    public Func<SchematicViewModel, IReadOnlyList<string>, IReadOnlyDictionary<string, (long Points, long SweepPoints)>>? PointsOf { get; set; }

    /// <summary>Shows a row's component on the canvas (⋮ ▸ Reveal on canvas).</summary>
    public Action<SchematicViewModel, Tunable>? RevealOnCanvas { get; set; }

    /// <summary>Defers a catalog refresh off a model change; null runs it at once (tests).</summary>
    public Action<Action>? Defer { get; set; }

    // ---- State ----------------------------------------------------------------

    private SchematicViewModel? _tuned;
    private TunableCatalog?     _catalog;
    private bool                _refreshPending;
    private readonly List<SchematicEditModel> _watched = [];
    private readonly HashSet<SchematicViewModel> _coloured = [];

    /// <summary>The schematic being tuned: the focused tab's TOP frame, so pushing into a sub-cell keeps
    /// tuning the bench (overview D5).</summary>
    public SchematicViewModel? Tuned => _tuned;

    public bool HasSchematic => _tuned is not null;

    [ObservableProperty] private string _headerLabel = "";

    public ObservableCollection<TuningRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The row Up (−1) or Down (+1) moves to from <paramref name="row"/> — the nearest one with
    /// a slider, skipping a disabled row — or null at either end, where the key does nothing
    /// (R-dfr26-6-3).</summary>
    public TuningRowViewModel? NeighbourRow(TuningRowViewModel row, int direction)
    {
        int at = Rows.IndexOf(row);
        if (at < 0 || direction == 0) return null;
        for (int i = at + Math.Sign(direction); i >= 0 && i < Rows.Count; i += Math.Sign(direction))
            if (Rows[i].IsEnabled) return Rows[i];
        return null;
    }

    public TuningAddViewModel Add { get; }

    public TuningScopeViewModel ScopeSettings { get; }

    public TuningPanelViewModel()
    {
        Add           = new TuningAddViewModel(this);
        ScopeSettings = new TuningScopeViewModel(this);
        // Start needs a row: ticking Tune on the schematic already in the panel adds the first one
        // without any focus change, so the button must be told here or it stays disabled.
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRows));
            StartCommand.NotifyCanExecuteChanged();
        };
    }

    public event EventHandler? Changed;

    // ---- Following focus (R-to4-2) -----------------------------------------

    /// <summary>
    /// Called on every focus change: the focused tab's top-frame session for a <c>.csch</c>, null for
    /// anything else. A different schematic stops the running session first.
    /// </summary>
    public void SetActiveSchematic(SchematicViewModel? tuned, string? displayName)
    {
        if (ReferenceEquals(tuned, _tuned))
        {
            HeaderLabel = tuned is null ? "" : displayName ?? HeaderLabel;
            return;
        }

        if (IsRunning) Stop();
        ClearCanvas();
        Unwatch();

        _tuned   = tuned;
        _catalog = null;
        _catalogFailed = false;
        _values.Clear();
        _seenValue.Clear();
        _seenWhole.Clear();
        HeaderLabel = tuned is null ? "" : displayName ?? "";
        StatusText  = "";
        RunOnReleaseSuggested = false;
        Rows.Clear();
        _presetsBuiltFrom = null;
        Presets.Clear();
        CloseComparison();

        if (tuned is not null) RefreshNow();
        else { OnPropertyChanged(nameof(HasPresets)); RefreshCornerChoices(); }
        ScopeSettings.Reload();
        OnPropertyChanged(nameof(Tuned));
        OnPropertyChanged(nameof(HasSchematic));
        OnPropertyChanged(nameof(DigitsChoices));
        NotifyCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- The catalog and the rows ----------------------------------------

    /// <summary>The catalog of the tuned design, computed on first use after a change.</summary>
    public TunableCatalog? Catalog
    {
        get
        {
            EnsureCatalog();
            return _catalog;
        }
    }

    // Discovery extracts the whole design, so it runs only when something needs it — a tuned row, the
    // Add… list, the Inspector asking about a row. A design with nothing tuned pays nothing per edit.
    private bool _catalogFailed;

    private void EnsureCatalog()
    {
        if (_tuned is null || _catalog is not null || _catalogFailed) return;
        try { _catalog = Discover(_tuned); }
        catch { _catalogFailed = true; }
        Watch();
    }

    // Tuned values by key, kept across a row rebuild so an undo of a range edit does not snap a
    // slider back to the schematic.
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    // Tuned complex values by their WHOLE key, for the same reason. Absent while a value is the
    // schematic's.
    private readonly Dictionary<string, Complex> _complex = new(StringComparer.Ordinal);

    /// <summary>Re-reads the catalog and rebuilds the rows from the tuned schematic's entries.</summary>
    public void RefreshNow()
    {
        _refreshPending = false;
        if (_tuned is null) return;

        var entries = (_tuned.EditModel.Tuning?.Variables ?? []).Where(e => e.Tune).ToList();
        if (entries.Count > 0) EnsureCatalog();
        else Watch();
        var keep    = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var row in Rows.Where(r => !keep.Contains(r.Key)).ToList())
        {
            Rows.Remove(row);
            _values.Remove(row.Key);
        }
        var wholes = entries.Select(e => _catalog?.Find(e.Key)?.WholeKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var w in _complex.Keys.Where(w => !wholes.Contains(w)).ToList()) _complex.Remove(w);

        bool followed = false;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row   = Rows.FirstOrDefault(r => r.Key == entry.Key);
            if (row is null)
            {
                row = new TuningRowViewModel(this, entry.Key);
                Rows.Insert(Math.Min(i, Rows.Count), row);
            }
            else if (Rows.IndexOf(row) != i && i < Rows.Count)
            {
                Rows.Move(Rows.IndexOf(row), i);
            }
            var t = _catalog?.Find(entry.Key);
            if (t is not null && SchematicValueMoved(t)) followed = true;
            row.Bind(entry, t, _values.TryGetValue(entry.Key, out var v) ? v : null,
                     t?.WholeKey is { } wk && _complex.TryGetValue(wk, out var z) ? z : null);
        }

        RebuildPresets();
        RefreshCornerChoices();
        if (followed && IsRunning) Request(final: true);
        UpdateLag();
        UpdateCanvas();
        OnPropertyChanged(nameof(Digits));
        OnPropertyChanged(nameof(DigitsChoices));
        if (Add.IsOpen) Add.Refresh();
        LockInCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The schematic's own value of each key as the rows last read it. When it changes under a tuned
    // value — the Optimizer's or Yield's Push, Recall and Push, a typed edit, an undo — the schematic is the
    // newer word: the slider follows it rather than holding an older tuned value whose orange label would
    // hide the write and whose session would later write its stale result over it.
    private readonly Dictionary<string, double>  _seenValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Complex> _seenWhole = new(StringComparer.Ordinal);

    /// <summary>Drops the tuned value of <paramref name="t"/> when the schematic's value moved since the
    /// last read; true when one was dropped.</summary>
    private bool SchematicValueMoved(Tunable t)
    {
        bool dropped = false;
        if (t.WholeKey is { } whole)
        {
            if (_seenWhole.TryGetValue(whole, out var before) && before != t.Whole) dropped = _complex.Remove(whole);
            _seenWhole[whole] = t.Whole;
        }
        else
        {
            if (_seenValue.TryGetValue(t.Key, out var before) && before != t.Value) dropped = _values.Remove(t.Key);
            _seenValue[t.Key] = t.Value;
        }
        return dropped;
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

    // The tuned schematic and every sub-cell drawing it reaches: an edit in any of them can change
    // what is offered or what a row's schematic value is.
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

    // ---- Activation, the shared effect of the three ways (R-to4-3) -----------

    public IReadOnlyList<string> KeysFor(SchematicEditModel drawing, EditableComponent component, EditableParameter parameter)
        => Catalog?.KeysFor(drawing, component, parameter) ?? [];

    public bool IsTuned(string key)
        => _tuned?.EditModel.Tuning?.Variables.Any(v => v.Key == key && v.Tune) == true;

    bool ITunableAddHost.IsActive(string key) => IsTuned(key);

    void ITunableAddHost.Activate(IReadOnlyCollection<string> keys) => SetTuned(keys, on: true);

    public void SetTuned(string key, bool on) => SetTuned([key], on);

    /// <summary>Sets or clears the tune flag of every key in ONE undo step in the tuned schematic.
    /// A key the catalog does not offer is ignored.</summary>
    public void SetTuned(IReadOnlyCollection<string> keys, bool on)
    {
        if (_tuned is null || Catalog is not { } catalog) return;

        var setup   = _tuned.EditModel.Tuning;
        int changed = 0;
        string? refused = null;
        foreach (var key in keys)
        {
            bool tuned = setup?.Variables.Any(v => v.Key == key && v.Tune) == true;
            if (tuned == on) continue;
            var t = catalog.Find(key);
            if (on)
            {
                if (t is null) continue;
                var next = TuningSetupEdits.WithTune(setup, t, true);
                if (t.Part is not null && ComplexRegion.Conflict(next, key, t.WholeUnit) is { } why)
                {
                    refused = why;
                    continue;
                }
                setup = next;
            }
            else
            {
                setup = t is not null ? TuningSetupEdits.WithTune(setup, t, false) : TuningSetupEdits.WithoutTuning(setup, key);
                _values.Remove(key);
            }
            changed++;
        }
        if (refused is not null) StatusText = $"Not tuned: {refused}.";
        if (changed == 0) return;

        string what = changed == 1 ? keys.First() : $"{changed} values";
        ExecuteEdit(new SetTuningSetupCommand(_tuned.EditModel, setup,
            on ? $"Tune {what}" : $"Stop tuning {what}"));
        if (!on && IsRunning) Request(final: true);
    }

    /// <summary>
    /// Raised after an edit from this panel lands on a schematic's undo stack, with the session it landed
    /// on — the Analyses panel's <c>EditCommitted</c>, for the same reason: the panel is a tool, never the
    /// active document, so without it ⌘Z after a Push undid nothing until the schematic was clicked.
    /// </summary>
    public event Action<SchematicViewModel>? EditCommitted;

    private void ExecuteEdit(IUiCommand command)
    {
        if (_tuned is null) return;
        _tuned.Execute(command);
        EditCommitted?.Invoke(_tuned);
    }

    /// <summary>The tuned schematic's entry for <paramref name="key"/>, or null.</summary>
    internal TunableEntry? EntryOf(string key) => _tuned?.EditModel.Tuning?.Variables.FirstOrDefault(v => v.Key == key);

    /// <summary>A change to one row's entry — range, scale, step — as one undo step (R-to4-10).</summary>
    internal void EditEntry(TuningRowViewModel row, Action<TunableEntry> change, string description)
    {
        if (_tuned is null) return;
        var next = TuningSetupEdits.WithEntry(_tuned.EditModel.Tuning, row.Key, row.Tunable, change);
        if (row.Tunable is { Part: not null } t && ComplexRegion.Conflict(next, row.Key, t.WholeUnit) is { } why)
        {
            StatusText = $"Refused: {why}.";
            row.CancelRangeEdit();
            return;
        }
        if (row.Tunable?.Part is null) _values[row.Key] = row.Value;
        ExecuteEdit(new SetTuningSetupCommand(_tuned.EditModel, next, description));
    }

    internal void RevealRow(TuningRowViewModel row)
    {
        if (_tuned is not null && row.Tunable is { } t) RevealOnCanvas?.Invoke(_tuned, t);
    }

    // ---- Values and the session (R-to4-4, R-to4-5) ---------------------------

    private TuneSession? _session;

    /// <summary>The running session, or null.</summary>
    public TuneSession? Session => _session;

    public bool IsRunning => _session is not null;

    /// <summary>
    /// Every movable row's value as value text — what the session evaluates and Push writes. A complex
    /// value appears ONCE, whole, under its own key and in the form the schematic writes it
    /// (<c>ZL</c> → <c>30+52j Ohm</c>), and only once one of its parts has moved.
    /// </summary>
    public IReadOnlyDictionary<string, string> CurrentValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (row.Tunable is not { } t || row.IsDisabled) continue;
            if (t.WholeKey is null) values[row.Key] = row.ValueText;
            else if (_complex.TryGetValue(t.WholeKey, out var z))
                values[t.WholeKey] = ComplexValue.Format(z, t.WholeUnit, t.Form, TuningDigits.NumberFormat(Digits));
        }
        return values;
    }

    // ---- Digits (shared with the Optimizer panel) ---------------------------

    /// <summary>The significant digits a tuned value is spelled with (<see cref="TuningDigits"/>).</summary>
    public int Digits => TuningDigits.Of(_tuned?.EditModel.Tuning);

    /// <summary>The header's digits menu.</summary>
    public IReadOnlyList<TuningDigitsChoice> DigitsChoices
        => [.. TuningDigits.Choices.Select(d => new TuningDigitsChoice(d, d == Digits, SetDigitsCommand))];

    /// <summary>Spells tuned values with <paramref name="digits"/> figures — one undo step, written to
    /// the schematic so it is the same next time, and read by the Optimizer panel too.</summary>
    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void SetDigits(int digits)
    {
        if (_tuned is null || digits == Digits) return;
        var next = _tuned.EditModel.Tuning?.Clone() ?? new TuningSetup();
        next.Digits = digits == TuningDigits.Default ? null : digits;
        ExecuteEdit(new SetTuningSetupCommand(_tuned.EditModel, next, $"Show {TuningDigits.Label(digits)}"));
        if (IsRunning) Request(final: true);
    }

    internal void OnRowValueChanged(TuningRowViewModel row, bool final)
    {
        _values[row.Key] = row.Value;
        AfterValueChange(final);
    }

    /// <summary>
    /// A part row moved to <paramref name="target"/>: the whole value moves along that part's path —
    /// its partner in the same coordinate system held — and stops at the edge of the ranges of every
    /// part of the value (overview D18). Every part row of the value then shows the result.
    /// </summary>
    internal void OnPartMoved(TuningRowViewModel row, double target, bool final)
    {
        if (row.Tunable is not { Part: { } part, WholeKey: { } whole } t) return;
        var z     = _complex.TryGetValue(whole, out var held) ? held : t.Whole;
        var moved = ComplexRegion.Of(_tuned?.EditModel.Tuning, whole, t.WholeUnit).Move(z, part, target);

        _complex[whole] = moved;
        foreach (var r in Rows)
            if (r.Tunable?.WholeKey == whole) r.ShowWhole(moved);
        if (moved == z && !final) return;
        AfterValueChange(final);
    }

    private void AfterValueChange(bool final)
    {
        if (IsRunning) Request(final);
        UpdateCanvas();
        UpdateLag();
    }

    private void Request(bool final) => _session?.Request(CurrentValues(), final);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_tuned is null || CreateSession is null || IsRunning) return;

        TuneSession? session;
        try { session = CreateSession(_tuned, ScopeSettings.SelectedAnalyses()); }
        catch (Exception ex) { Problem(ex.Message); return; }
        if (session is null) return;

        session.RunOnRelease = RunOnRelease;
        if (CornerName is { } corner && session.EvaluateAt(corner) is { } refused)
        {
            Problem($"Refused: {refused}.");
            EvaluateAt = NominalChoice;
        }
        session.Changed               += OnSessionChanged;
        session.RunOnReleaseSuggested += OnRunOnReleaseSuggested;
        _session = session;
        StatusText = "";
        Request(final: true);
        AfterSessionChange();
    }

    private bool CanStart() => _tuned is not null && CreateSession is not null && !IsRunning && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        if (_session is not { } s) return;
        Detach(s);
        s.Stop();
        StatusText = "Stopped";
        AfterSessionChange();
    }

    /// <summary>
    /// Another panel is taking <paramref name="tuned"/>'s display (an optimizer run): a live session on it ends
    /// WITHOUT writing its result — the other run's result replaces it, and a session left running would later
    /// write its own older one over it. The sliders keep their values; Start resumes from them.
    /// </summary>
    public void EndSessionFor(SchematicViewModel tuned, string why)
    {
        if (!ReferenceEquals(tuned, _tuned) || _session is not { } s) return;
        Detach(s);
        s.Dispose();
        StatusText = $"Stopped — {why}";
        AfterSessionChange();
    }

    /// <summary>Every slider back to the schematic's value; the published result is dropped (R-to4-9).</summary>
    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void Revert()
    {
        _values.Clear();
        _complex.Clear();
        foreach (var row in Rows) row.ResetToSchematic();
        _session?.Revert();
        StatusText = "Reverted";
        AfterSessionChange();
    }

    /// <summary>
    /// Writes every tuned value into the document that owns it, one undo step per document (R-to4-8).
    /// The session continues, now at values the schematic holds.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSchematic))]
    private void Push()
    {
        if (_tuned is null || Catalog is not { } catalog) return;
        _session?.Push();

        var values = CurrentValues();
        var report = TuningPush.Push(catalog, values, _tuned, SessionForDrawing ?? (_ => null));
        StatusText = report.StatusLine;
        if (report.UndoSession(_tuned) is { } undo) EditCommitted?.Invoke(undo);

        // What was written is now the schematic's own value; what was skipped stays tuned.
        foreach (var key in report.WrittenKeys)
        {
            _values.Remove(key);
            _complex.Remove(key);
        }

        _catalog = null;
        RefreshNow();
        if (_session is { IsLagging: true }) Request(final: true);
        AfterSessionChange();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Snapshot() => _session?.Snapshot();

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void ClearSnapshot() => _session?.ClearSnapshot();

    // ---- Evaluate at (brief-yield-7 R-ya7-4) -----------------------------------

    /// <summary>The picker's first entry: the design as it stands.</summary>
    public const string NominalChoice = "Nominal";

    /// <summary>The toolbar's Evaluate at choices: <see cref="NominalChoice"/>, then each enabled corner of the
    /// tuned schematic.</summary>
    public IReadOnlyList<string> EvaluateAtChoices { get; private set; } = [NominalChoice];

    /// <summary>Whether there is any corner to choose — the picker shows only then.</summary>
    public bool HasCorners => EvaluateAtChoices.Count > 1;

    /// <summary>Where the live session evaluates: Nominal, or a corner — its bindings over the sliders' values (a
    /// statistical corner replayed around them). Push still writes the sliders' values alone.</summary>
    [ObservableProperty] private string _evaluateAt = NominalChoice;

    /// <summary>The chosen corner's name; null at the nominal.</summary>
    public string? CornerName => EvaluateAt == NominalChoice ? null : EvaluateAt;

    partial void OnEvaluateAtChanged(string value)
    {
        // A picker whose items were replaced may write back an empty selection.
        if (value is null) { EvaluateAt = NominalChoice; return; }
        if (_session is { } s && s.EvaluateAt(CornerName) is { } refused)
        {
            Problem($"Refused: {refused}.");
            EvaluateAt = NominalChoice;
            return;
        }
        OnPropertyChanged(nameof(CornerName));
        UpdateCanvas();
    }

    private void RefreshCornerChoices()
    {
        IReadOnlyList<string> next = [NominalChoice, .. (_tuned?.EditModel.Tuning?.Corners ?? []).Where(c => c.Enabled).Select(c => c.Name)];
        if (!next.SequenceEqual(EvaluateAtChoices))
        {
            EvaluateAtChoices = next;
            OnPropertyChanged(nameof(EvaluateAtChoices));
            OnPropertyChanged(nameof(HasCorners));
        }
        if (!EvaluateAtChoices.Contains(EvaluateAt)) EvaluateAt = NominalChoice;
    }

    // ---- Run on release and the badge (R-to4-6, D7) --------------------------

    [ObservableProperty] private bool _runOnRelease;

    /// <summary>The last evaluation was slow: the ⚙ button carries a badge until run-on-release is on.</summary>
    [ObservableProperty] private bool _runOnReleaseSuggested;

    partial void OnRunOnReleaseChanged(bool value)
    {
        if (_session is not null) _session.RunOnRelease = value;
        if (value) RunOnReleaseSuggested = false;
    }

    /// <summary>The badge's one click.</summary>
    [RelayCommand] private void AcceptRunOnRelease() => RunOnRelease = true;

    private void OnRunOnReleaseSuggested(object? sender, EventArgs e)
    {
        if (!RunOnRelease) RunOnReleaseSuggested = true;
    }

    // ---- Status (R-to4-6) ----------------------------------------------------

    [ObservableProperty] private TuningStatus _status;

    /// <summary>The last evaluation's duration, <c>0.4 s</c>; empty before one.</summary>
    [ObservableProperty] private string _lastEvalText = "";

    /// <summary><c>1.4 s behind</c> while lagging; empty otherwise.</summary>
    [ObservableProperty] private string _behindText = "";

    /// <summary>What the last action did — a Push's summary, a refusal.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Posts a refusal or a failure to the Messages panel (issue #33): a session that did not start must not
    /// be said only in a status line that the next action overwrites.</summary>
    public Action<string>? ReportProblem { get; set; }

    /// <summary>The status line AND the Messages panel, for a session's refusal or failure.</summary>
    private void Problem(string text)
    {
        StatusText = text;
        ReportProblem?.Invoke(text);
    }

    private void OnSessionChanged(object? sender, EventArgs e) => UpdateLag();

    /// <summary>
    /// How long the display must stay behind the sliders before the panel SAYS so. Every request is a
    /// lag until its result lands, so on a circuit that solves in a few milliseconds a drag flashed
    /// the dot amber, the time text and the rows several times a second while nothing was actually
    /// behind (owner report, 2026-10-25). A lag shorter than this is not one anybody can act on.
    /// </summary>
    internal static readonly TimeSpan LagShownAfter = TimeSpan.FromMilliseconds(200);

    /// <summary>The clock the lag is timed by; a test replaces it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime? _lagSince;

    /// <summary>Whether a lag that began at <paramref name="since"/> is shown at <paramref name="now"/>.</summary>
    internal static bool ShowsLag(DateTime? since, DateTime now) => since is { } s && now - s >= LagShownAfter;

    /// <summary>Re-reads the session's lag. The view calls it on a timer while lagging, so the age
    /// counts up — and so a lag that outlasts <see cref="LagShownAfter"/> turns amber without waiting
    /// for the next slider move.</summary>
    public void UpdateLag()
    {
        var s = _session;
        if (s is { IsLagging: true }) _lagSince ??= Clock();
        else                          _lagSince = null;
        bool lagging = s is not null && ShowsLag(_lagSince, Clock());

        Status = s is null ? TuningStatus.Idle : lagging ? TuningStatus.Lagging : TuningStatus.Running;
        LastEvalText = s?.LastDuration is { } d ? $"{d.TotalSeconds:0.0} s" : "";
        BehindText   = lagging && s!.DisplayedAge is { } age ? $"{age.TotalSeconds:0.0} s behind" : "";

        var shown   = s?.DisplayedValues;
        var current = CurrentValues();
        foreach (var row in Rows)
        {
            // A part row lags when its WHOLE value does: that is the value the session evaluates.
            string key = row.Tunable?.ValueKey ?? row.Key;
            row.IsLagging = lagging
                && current.GetValueOrDefault(key) != shown?.GetValueOrDefault(key);
        }
    }

    private void Detach(TuneSession s)
    {
        s.Changed               -= OnSessionChanged;
        s.RunOnReleaseSuggested -= OnRunOnReleaseSuggested;
        _session = null;
    }

    private void AfterSessionChange()
    {
        UpdateLag();
        UpdateCanvas();
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(IsRunning));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        SnapshotCommand.NotifyCanExecuteChanged();
        ClearSnapshotCommand.NotifyCanExecuteChanged();
        LockInCommand.NotifyCanExecuteChanged();
        SetDigitsCommand.NotifyCanExecuteChanged();
    }

    // ---- The canvas tells the truth (R-to4-7) ---------------------------------

    /// <summary>
    /// While a session runs, every parameter whose tuned value differs from its drawing's is drawn in
    /// the tuned colour with the tuned value — on the bench, and on a sub-cell's canvas when it is open.
    /// </summary>
    private void UpdateCanvas()
    {
        var byVm = new Dictionary<SchematicViewModel, Dictionary<string, IReadOnlyDictionary<int, string>>>();

        if (IsRunning && _tuned is not null && _catalog is { } catalog)
        {
            var current = CurrentValues();
            foreach (var row in Rows)
            {
                if (row.Tunable is not { } t || !row.DiffersFromSchematic) continue;
                var drawing = t.Cell is null ? _tuned.EditModel : catalog.Drawings.GetValueOrDefault(t.Cell);
                if (drawing is null) continue;
                var vm = ReferenceEquals(drawing, _tuned.EditModel) ? _tuned : ExistingSessionFor?.Invoke(drawing);
                string text = current.GetValueOrDefault(t.ValueKey) ?? row.ValueText;
                // At a corner the tuned label says where it is evaluated (brief-yield-7 R-ya7-4).
                if (CornerName is { } corner) text += " @ " + corner;
                if (vm is null || TunedLabel(drawing, t, text) is not { } label) continue;

                if (!byVm.TryGetValue(vm, out var labels)) byVm[vm] = labels = [];
                var rowsOf = labels.TryGetValue(label.ComponentId, out var existing)
                    ? new Dictionary<int, string>(existing) : new Dictionary<int, string>();
                rowsOf[label.Row] = label.Text;
                labels[label.ComponentId] = rowsOf;
            }
        }

        foreach (var vm in _coloured.Where(v => !byVm.ContainsKey(v)).ToList())
        {
            vm.TunedLabels = null;
            _coloured.Remove(vm);
        }
        foreach (var (vm, labels) in byVm)
        {
            vm.TunedLabels = labels;
            _coloured.Add(vm);
        }
    }

    private void ClearCanvas()
    {
        foreach (var vm in _coloured) vm.TunedLabels = null;
        _coloured.Clear();
    }

    /// <summary>Where a tunable's text is drawn — the label row <see cref="EditableComponent.LabelParameters"/>
    /// gives it — and what it should read. Null when the parameter is not drawn.</summary>
    internal static (string ComponentId, int Row, string Text)? TunedLabel(SchematicEditModel drawing, Tunable t, string valueText)
    {
        foreach (var c in drawing.Components)
        {
            bool owner = t.Kind == TunableKind.Variable
                ? c.Symbol == SymbolKind.Var && c.Parameters.Any(p => p.Name.Trim() == t.Owner)
                : c.InstanceName == t.Owner;
            if (!owner) continue;

            // A vendor part's tuned value is a PART: it is drawn where the part number is, the type label (row 0).
            if (t.VendorPart is { } vp)
                return c.ShowTypeLabel ? (c.Id, 0, vp.CandidateOfCell(valueText)?.Part ?? valueText) : null;

            string name = t.Parameter ?? t.Owner;
            int row = 2;
            foreach (var p in c.LabelParameters())
            {
                if (string.IsNullOrEmpty(p.Expression)) continue;
                if ((t.Kind == TunableKind.Variable ? p.Name.Trim() : p.Name) == name)
                    return (c.Id, row, string.IsNullOrEmpty(p.Name) ? valueText : $"{p.Name} = {valueText}");
                row++;
            }
            return null;
        }
        return null;
    }
}
