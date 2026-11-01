// ================================================================
//  YieldPanelViewModel.Sensitivity.cs  —  the Sensitivity mode
//  (brief-sens-3 R-sens3-1…R-sens3-7)
//
//  THE PANEL CALLS SensitivityRun AND NOTHING ELSE. The run is created
//  exactly as `circuitrf yield sens` creates it — the prepared circuit,
//  the schematic's own setup (Setup = null), the result written beside
//  the design as <design>.sens.npy — on a background thread. The cost
//  shown before a run is the same constructor's Describe(), so what the
//  panel says it will spend is what Run spends.
//
//  The settings strip and the S-parameter blocks are the `sensitivity`
//  line, every change one undo step on the schematic's own stack; the
//  ranking is the result's own roll-ups, regrouped with no simulation
//  (R-sens2-6). Nothing here runs unasked (D7).
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
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using RfCore.Data;

namespace CircuitRF.Ui.Yield;

/// <summary>The level the ranked list groups the same result at (brief-sens-2 R-sens2-6).</summary>
public enum SensitivityLevel { Entry, Port, Instance }

public sealed partial class YieldPanelViewModel
{
    /// <summary>How many S-parameter block rows show before "Show all n" (R-sens3-4).</summary>
    public const int SnpBlocksShown = 5;

    // ---- What the workspace supplies ----------------------------------------

    /// <summary>Writes <c>&lt;design&gt;.sens.cdd</c> beside the result at the list's level, a Pareto per named response,
    /// and opens it (R-sens3-7).</summary>
    public Func<string, DataSet, SensitivityLevel, IReadOnlyList<string>, Task>? OpenSensitivityDisplay { get; set; }

    /// <summary>What the display draws: the goals ranked, or every scalar measure when no goal was.</summary>
    public IReadOnlyList<string> SensDisplayResponses
        => SensResult is not { } r ? [] : r.Goals.Count > 0 ? [.. r.Goals.Select(g => g.Name)] : [.. r.Measures.Select(m => m.Name)];

    /// <summary>Send to Tuning: the keys join the Tuning panel on the same schematic (its own door).</summary>
    public Action<IReadOnlyCollection<string>>? SendKeysToTuning { get; set; }

    /// <summary>Run Yield on these: the Yield panel, on the same schematic, in Monte Carlo mode; the argument is how
    /// many ranked factors carry no tolerance.</summary>
    public Action<int>? OpenYieldOnRanked { get; set; }

    /// <summary>Asks once before an edit that also removes lines (a title and the lines); null proceeds.</summary>
    public Func<string, IReadOnlyList<string>, Task<bool>>? Confirm { get; set; }

    // ---- Mode -----------------------------------------------------------------------

    public bool IsSensitivity { get => Mode == YieldMode.Sensitivity; set { if (value && OffersMode(YieldMode.Sensitivity)) Mode = YieldMode.Sensitivity; } }

    // ---- The sensitivity line (R-sens3-4) ------------------------------------------------

    public SensitivitySettings SensSettings => Setup?.Sensitivity ?? new SensitivitySettings();

    public IReadOnlyList<string> SensFactorChoices { get; } = ["All", "Toleranced", "Designable"];

    /// <summary><c>factors=all|stat|opt</c>.</summary>
    public int SensFactorIndex
    {
        get => (int)SensSettings.EffectiveFactors;
        set
        {
            if (value is < 0 or > 2 || value == SensFactorIndex) return;
            EditSens(s => s.Factors = value == 0 ? null : (SensitivityFactorSource)value,
                     $"Sensitivity of {SensFactorChoices[value].ToLowerInvariant()} values");
        }
    }

    public string SensStepText
    {
        get => SensSettings.Step is { } v ? Number(v) : "";
        set => EditSensNumber(value, SensitivitySettings.DefaultStep, (s, v) => s.Step = v, s => s.Step, "Set the sensitivity step", "a step in percent above zero");
    }

    public string SensSigmaText
    {
        get => SensSettings.Sigma is { } v ? Number(v) : "";
        set => EditSensNumber(value, SensitivitySettings.DefaultSigma, (s, v) => s.Sigma = v, s => s.Sigma, "Set the sensitivity σ", "a number of sigmas above zero");
    }

    /// <summary>The S-parameters master switch (<c>snp=on|off</c>, R-sens2-5a): off keeps the per-block choices.</summary>
    public bool SensSnp
    {
        get => SensSettings.EffectiveSnp;
        set { if (value != SensSnp) EditSens(s => s.Snp = value ? null : false, value ? "Step the S-parameter blocks" : "Leave every S-parameter block out"); }
    }

    public string SensSnpDbText
    {
        get => SensSettings.SnpDb is { } v ? Number(v) : "";
        set => EditSensNumber(value, SensitivitySettings.DefaultSnpDb, (s, v) => s.SnpDb = v, s => s.SnpDb, "Set the S-parameter dB step", "a step in dB above zero");
    }

    public string SensSnpDegText
    {
        get => SensSettings.SnpDeg is { } v ? Number(v) : "";
        set => EditSensNumber(value, SensitivitySettings.DefaultSnpDeg, (s, v) => s.SnpDeg = v, s => s.SnpDeg, "Set the S-parameter phase step", "a step in degrees above zero");
    }

    /// <summary>The sensitivity line with <paramref name="change"/> applied, as one undo step — nothing when unchanged. A
    /// value equal to its default is held as null and never written (R-sens1-5).</summary>
    internal void EditSens(Action<SensitivitySettings> change, string description)
    {
        if (_tuned is null) return;
        var before = SensSettings;
        var s = before.Clone();
        change(s);
        if (SensKey(s) == SensKey(before)) { RefreshSensSettings(); return; }
        Execute(TuningSetupEdits.WithSensitivity(Setup, s), description);
        RefreshSensSettings();
    }

    private void EditSensNumber(string text, double dflt, Action<SensitivitySettings, double?> set, Func<SensitivitySettings, double?> get,
                                string description, string what)
    {
        string t = text.Trim().TrimEnd('%', '°').Trim();
        if (t.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        double? v = null;
        if (t.Length > 0)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !(n > 0) || !double.IsFinite(n))
            {
                StatusText = $"Refused: '{text}' is not {what}.";
                RefreshSensSettings();
                return;
            }
            v = n == dflt ? null : n;
        }
        if (get(SensSettings) == v) { RefreshSensSettings(); return; }
        EditSens(s => set(s, v), description);
    }

    /// <summary>The line's identity, for "changed" and for "out of date".</summary>
    private static string SensKey(SensitivitySettings s)
        => string.Join("|", s.Factors, s.Step, s.Sigma, s.Ports, s.Snp, NormalizedExclude(s.SnpExclude), s.SnpDb, s.SnpDeg, s.Responses, s.Parallelism);

    private static string NormalizedExclude(string? csv)
        => string.Join(",", ExcludedNames(csv).Order(StringComparer.Ordinal));

    private static List<string> ExcludedNames(string? csv)
        => [.. (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private void RefreshSensSettings()
    {
        foreach (var name in new[]
                 {
                     nameof(SensFactorIndex), nameof(SensStepText), nameof(SensSigmaText), nameof(SensSnp),
                     nameof(SensSnpDbText), nameof(SensSnpDegText),
                 })
            OnPropertyChanged(name);
    }

    // ---- S-parameter blocks (R-sens3-4) -----------------------------------------------------

    /// <summary>One row per SnP instance in the design, there before any run.</summary>
    public ObservableCollection<SnpBlockRowViewModel> SnpBlocks { get; } = [];

    /// <summary>The rows shown: the first <see cref="SnpBlocksShown"/>, or all of them once asked.</summary>
    public ObservableCollection<SnpBlockRowViewModel> VisibleSnpBlocks { get; } = [];

    public bool HasSnpBlocks => SnpBlocks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllSnpText))]
    private bool _showAllSnpBlocks;

    partial void OnShowAllSnpBlocksChanged(bool value) => FillVisibleSnpBlocks();

    public bool HasMoreSnpBlocks => SnpBlocks.Count > SnpBlocksShown;

    public string ShowAllSnpText => ShowAllSnpBlocks ? "Show fewer" : $"Show all {SnpBlocks.Count}";

    [RelayCommand] private void ToggleShowAllSnp() => ShowAllSnpBlocks = !ShowAllSnpBlocks;

    /// <summary>Whether <paramref name="instance"/> is stepped — not named in <c>snpexclude</c>. The master switch is a
    /// separate setting: off, every box is disabled but keeps its tick.</summary>
    public bool IsSnpIncluded(string instance) => !ExcludedNames(SensSettings.SnpExclude).Contains(instance, StringComparer.Ordinal);

    /// <summary>Include ticked or unticked: <c>snpexclude</c> rewritten as one undo step.</summary>
    public void SetSnpIncluded(string instance, bool include)
    {
        if (include == IsSnpIncluded(instance)) return;
        var names = ExcludedNames(SensSettings.SnpExclude);
        if (include) names.RemoveAll(n => n == instance); else names.Add(instance);
        EditSens(s => s.SnpExclude = names.Count == 0 ? null : string.Join(",", names),
                 include ? $"Include {instance} in the sensitivity" : $"Leave {instance} out of the sensitivity");
    }

    private void RefreshSnpBlocks()
    {
        var infos = _tuned is null || Mode != YieldMode.Sensitivity ? [] : Catalog?.SnpInstances ?? [];
        while (SnpBlocks.Count > infos.Count) SnpBlocks.RemoveAt(SnpBlocks.Count - 1);
        for (int i = 0; i < infos.Count; i++)
        {
            if (i < SnpBlocks.Count && SnpBlocks[i].Instance == infos[i].Instance) SnpBlocks[i].Bind(infos[i]);
            else if (i < SnpBlocks.Count) SnpBlocks[i] = new SnpBlockRowViewModel(this, infos[i]);
            else SnpBlocks.Add(new SnpBlockRowViewModel(this, infos[i]));
        }
        foreach (var row in SnpBlocks) row.Sync();
        FillVisibleSnpBlocks();
        OnPropertyChanged(nameof(HasSnpBlocks));
        OnPropertyChanged(nameof(HasMoreSnpBlocks));
        OnPropertyChanged(nameof(ShowAllSnpText));
    }

    private void FillVisibleSnpBlocks()
    {
        var want = ShowAllSnpBlocks ? SnpBlocks.ToList() : SnpBlocks.Take(SnpBlocksShown).ToList();
        if (want.SequenceEqual(VisibleSnpBlocks)) return;
        VisibleSnpBlocks.Clear();
        foreach (var r in want) VisibleSnpBlocks.Add(r);
    }

    [ObservableProperty] private SnpBlockRowViewModel? _selectedSnpBlock;

    partial void OnSelectedSnpBlockChanged(SnpBlockRowViewModel? value)
    {
        if (value is not null) ShowTile(value.Instance, null, null);
    }

    // ---- The cost, before a run (R-sens3-4) ---------------------------------------------------

    /// <summary><c>7 factor(s) (2 at ±σ, 5 at ±1 %), 15 evaluations</c> — <see cref="SensitivityRun.Describe"/> — or the
    /// refusal the run would give.</summary>
    [ObservableProperty] private string _sensPlanText = "";

    /// <summary>The evaluations the run would make (2k + 1, less duplicates); 0 when it would be refused.</summary>
    [ObservableProperty] private int _sensPlannedEvaluations;

    /// <summary>Asks the run's own constructor what it would do — nothing is simulated.</summary>
    public void RefreshSensPlan()
    {
        if (_tuned is null || IsRunActive || PrepareCircuit?.Invoke(_tuned) is not { ReadError: null } circuit)
        {
            if (!IsRunActive) { SensPlanText = ""; SensPlannedEvaluations = 0; }
            return;
        }
        var run = SensitivityRun.Create(circuit);
        if (run.Refusal is { } refusal) { SensPlanText = refusal.Render(); SensPlannedEvaluations = 0; return; }
        SensPlannedEvaluations = run.PlannedEvaluations;
        SensPlanText = run.Describe();
    }

    /// <summary>What a model change or a mode change refreshes: the strip, the blocks, the cost, whether the result is
    /// still the design's, and the tile.</summary>
    private void RefreshSensitivity()
    {
        RefreshSensSettings();
        if (Mode != YieldMode.Sensitivity) return;
        // The cost and the blocks netlist the design: a panel behind another tab catches up when it is shown.
        if (_shown == false) { _sensDirty = true; return; }
        _sensDirty = false;
        RefreshSnpBlocks();
        RefreshSensPlan();
        foreach (var r in SensRows.Where(r => r.IsExcluded)) r.SyncIncluded();
        if (SensResult is not null)
        {
            bool stale = SensKey(SensSettings) != _sensRanWith;
            if (stale != SensStale)
            {
                SensStale = stale;
                StatusText = stale ? "Out of date — the sensitivity settings changed since this run; Run to update" : _sensSummary;
            }
        }
        if (SnpTile is { } tile) ShowTile(tile.Instance, tile.SelectedPort, tile.SelectedEntry);
    }

    private bool? _shown;
    private bool _sensDirty;

    /// <summary>Whether the view is on screen (null: not known — a headless caller). Hidden, the Sensitivity mode defers
    /// what netlists the design until it is shown again.</summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        if (shown && _sensDirty) RefreshSensitivity();
    }

    private void ClearSensitivityForNewSchematic()
    {
        SnpBlocks.Clear();
        VisibleSnpBlocks.Clear();
        ShowAllSnpBlocks = false;
        SelectedSnpBlock = null;
        SnpTile = null;
        SensPlanText = "";
        SensPlannedEvaluations = 0;
        OnPropertyChanged(nameof(HasSnpBlocks));
        OnPropertyChanged(nameof(HasMoreSnpBlocks));
    }

    // ---- The run --------------------------------------------------------------------

    private SensitivityRun? _sensRun;
    private string _sensRanWith = "";
    private string _sensSummary = "";

    /// <summary>The last run's result; null before one finishes.</summary>
    public SensitivityResult? SensResult { get; private set; }

    /// <summary>The factors of the run that produced <see cref="SensResult"/>.</summary>
    public IReadOnlyList<SensitivityFactor> SensFactors { get; private set; } = [];

    /// <summary>The settings changed since the run: the list is the last run's, said in the status line (D7 — never
    /// re-run unasked).</summary>
    [ObservableProperty] private bool _sensStale;

    private void ClearSensReadouts()
    {
        _sensRun = null;
        SensResult = null;
        SensFactors = [];
        SensStale = false;
        _sensSummary = "";
        SensResponses.Clear();
        SensRows.Clear();
        SelectedSensRow = null;
        foreach (var g in Goals) g.ShowSens(null);
        OnPropertyChanged(nameof(HasSensResult));
        OnPropertyChanged(nameof(HasSensRows));
    }

    private void RunSensitivity(PreparedCircuit circuit, string? source)
    {
        var cts = new CancellationTokenSource();
        string? path = source is null ? null : SensitivityRun.ResultPathFor(source);
        SensitivityRun? run = null;
        run = SensitivityRun.Create(circuit, new SensitivityOptions
        {
            // The setup the bench netlists with — the schematic's own tuning block, as `yield sens` reads it.
            Setup        = null,
            Cancellation = cts.Token,
            ResultPath   = path,
            Progress     = p => { var r = run; PostToUi(() => { if (ReferenceEquals(r, _sensRun)) { StatusText = $"{p.Done} of {p.Total} simulations"; ElapsedText = Clock(p.Elapsed); } }); },
        });
        if (run.Refusal is { } refusal)
        {
            Problem($"Refused: {refusal.Render()}");
            return;
        }

        ClearRunReadouts();
        _sensRun     = run;
        _cts         = cts;
        _resultPath  = path;
        _sensRanWith = SensKey(SensSettings);
        State        = YieldRunState.Running;
        HasRunReadouts = true;
        StatusText   = $"0 of {run.PlannedEvaluations} simulations";
        foreach (var note in run.Notes) ReportMessages?.Invoke($"Sensitivity: {note.Render()}", []);

        _task = StartBackground(() =>
        {
            SensitivityResult? result = null;
            string? crash = null;
            try { result = run.Run(); }
            catch (Exception ex) { crash = ex.Message; }
            PostToUi(() => FinishSensitivity(run, result, crash));
        });
    }

    private void FinishSensitivity(SensitivityRun run, SensitivityResult? result, string? crash)
    {
        if (!ReferenceEquals(run, _sensRun)) return;              // abandoned meanwhile
        if (result is null) { State = YieldRunState.Idle; Problem($"The run failed: {crash}"); return; }
        switch (result.Outcome)
        {
            case SensitivityOutcome.Cancelled:
                State = YieldRunState.Idle;
                StatusText = "Cancelled";
                return;
            case SensitivityOutcome.Refused or SensitivityOutcome.NominalFailed:
                State = YieldRunState.Idle;
                Problem($"Refused: {result.Refusal?.Render() ?? result.FinishReason}");
                return;
        }

        State = YieldRunState.Finished;
        SensResult = result;
        SensFactors = run.Factors;
        _lastData = result.Data;
        foreach (var r in result.Goals.Concat(result.Measures)) SensResponses.Add(r.Name);
        foreach (var g in Goals) g.ShowSens(result.Goals.FirstOrDefault(r => r.Name == g.Name));
        SelectedSensResponse = SelectedGoal is { } sg && SensResponses.Contains(sg.Name) ? sg.Name : SensResponses.FirstOrDefault();
        BuildSensRows();

        var lines = result.Notes.Select(n => n.Render()).ToList();
        _sensSummary = $"{run.Factors.Count} factor{(run.Factors.Count == 1 ? "" : "s")} · {result.Evaluations} simulations"
                     + (result.Excluded.Count > 0 ? $" · {result.Excluded.Count} S-parameter block{(result.Excluded.Count == 1 ? "" : "s")} left out" : "")
                     + (result.FinishReason.StartsWith("stopped", StringComparison.Ordinal) ? " · " + result.FinishReason : "");
        StatusText   = _sensSummary;
        StatusDetail = string.Join(Environment.NewLine, lines);
        SensStale    = false;
        ReportMessages?.Invoke($"Sensitivity: {_sensSummary}", lines);
        OnPropertyChanged(nameof(HasSensResult));
        NotifyRunCommands();
    }

    public bool HasSensResult => SensResult is not null;

    // ---- The ranked list (R-sens3-5) ---------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsByEntry), nameof(IsByPort), nameof(IsByInstance), nameof(ShareHeader))]
    private SensitivityLevel _sensLevel = SensitivityLevel.Entry;

    partial void OnSensLevelChanged(SensitivityLevel value) => BuildSensRows();

    public bool IsByEntry    { get => SensLevel == SensitivityLevel.Entry;    set { if (value) SensLevel = SensitivityLevel.Entry; } }
    public bool IsByPort     { get => SensLevel == SensitivityLevel.Port;     set { if (value) SensLevel = SensitivityLevel.Port; } }
    public bool IsByInstance { get => SensLevel == SensitivityLevel.Instance; set { if (value) SensLevel = SensitivityLevel.Instance; } }

    /// <summary>Port shares overlap: S31 counts toward port 3 and port 1.</summary>
    public string ShareHeader => SensLevel == SensitivityLevel.Port ? "share (overlapping)" : "share";

    /// <summary>The responses ranked: the goals, then each scalar measure.</summary>
    public ObservableCollection<string> SensResponses { get; } = [];

    [ObservableProperty] private string? _selectedSensResponse;

    partial void OnSelectedSensResponseChanged(string? value)
    {
        BuildSensRows();
        if (value is not null && Goals.FirstOrDefault(g => g.Name == value) is { } goal && !ReferenceEquals(SelectedGoal, goal)) SelectedGoal = goal;
    }

    /// <summary>Selecting a goal row picks its ranking (the goal picker, as today).</summary>
    private void FollowGoalInRanking(YieldGoalRowViewModel? goal)
    {
        if (Mode == YieldMode.Sensitivity && goal is not null && SensResponses.Contains(goal.Name)) SelectedSensResponse = goal.Name;
    }

    public ObservableCollection<SensitivityRowViewModel> SensRows { get; } = [];

    public bool HasSensRows => SensRows.Count > 0;

    [ObservableProperty] private SensitivityRowViewModel? _selectedSensRow;

    partial void OnSelectedSensRowChanged(SensitivityRowViewModel? value)
    {
        if (value is { IsExcluded: false } row && row.Instance.Length > 0 && SnpInfo(row.Instance) is not null)
            ShowTile(row.Instance, row.Port, row.Entry);
        else if (value is not null) SnpTile = null;
        NotifySensCommands();
    }

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    internal void ToggleExpanded(SensitivityRowViewModel row)
    {
        if (!_expanded.Remove(row.Id)) _expanded.Add(row.Id);
        BuildSensRows();
    }

    /// <summary>The response the list shows.</summary>
    public SensitivityResponse? SelectedResponse
        => SensResult is { } r ? r.Goals.Concat(r.Measures).FirstOrDefault(x => x.Name == SelectedSensResponse) : null;

    /// <summary>
    /// The rows for the selected response at <see cref="SensLevel"/> — the result's own roll-ups (R-sens2-6), regrouped
    /// with no simulation — then one greyed row per instance the run left out.
    /// </summary>
    private void BuildSensRows()
    {
        string? selectedId = SelectedSensRow?.Id;
        SensRows.Clear();
        if (SelectedResponse is { } resp && SensResult is { } result)
        {
            string unit = resp.IsGoal ? "" : resp.Unit;
            var cs = resp.Contributors;
            List<SensitivityContributor> EntryMembers(SensitivityRollup e)
                => [.. cs.Where(c => c.Factor.Instance == e.Instance && EntryLabel(c) == e.Label)];

            switch (SensLevel)
            {
                case SensitivityLevel.Entry:
                    foreach (var e in resp.Entries)
                        SensRows.Add(SensitivityRowViewModel.Of(this, "e:" + e.Instance + "|" + e.Label, e, EntryMembers(e), 0, unit));
                    break;

                case SensitivityLevel.Port:
                    var rows = new List<SensitivityRowViewModel>();
                    foreach (var p in resp.Ports)
                        rows.Add(SensitivityRowViewModel.Of(this, $"p:{p.Instance}|{p.Port}", p,
                            [.. cs.Where(c => c.Factor.Snp is { } s && s.Instance == p.Instance && (s.I == p.Port || s.J == p.Port))], 0, unit));
                    // What is not an SnP entry has no port: it stands as its own entry, so the list is complete.
                    foreach (var e in resp.Entries)
                        if (EntryMembers(e) is { Count: > 0 } m && m.All(c => c.Factor.Snp is null))
                            rows.Add(SensitivityRowViewModel.Of(this, "e:" + e.Instance + "|" + e.Label, e, m, 0, unit));
                    foreach (var r in rows.OrderByDescending(r => r.Share)) SensRows.Add(r);
                    break;

                default:
                    foreach (var inst in resp.Instances)
                    {
                        var entries = resp.Entries.Where(e => e.Instance == inst.Label).ToList();
                        var members = cs.Where(c => c.Factor.Instance == inst.Label).ToList();
                        string id = "i:" + inst.Label;
                        bool expandable = entries.Count > 1 || members.Count > 1;
                        var row = SensitivityRowViewModel.Of(this, id, inst, members, 0, unit);
                        row.SetExpander(expandable, _expanded.Contains(id));
                        SensRows.Add(row);
                        if (!expandable || !_expanded.Contains(id)) continue;
                        foreach (var e in entries)
                        {
                            var em = EntryMembers(e);
                            string eid = id + "|" + e.Label;
                            var erow = SensitivityRowViewModel.Of(this, eid, e, em, 1, unit);
                            erow.SetExpander(em.Count > 1, _expanded.Contains(eid));
                            SensRows.Add(erow);
                            if (em.Count < 2 || !_expanded.Contains(eid)) continue;
                            foreach (var c in em) SensRows.Add(SensitivityRowViewModel.OfKey(this, eid + "|" + c.Factor.Key, c, 2, unit));
                        }
                    }
                    break;
            }

            // An instance left out appears in every grouping, greyed, at the bottom — never read as one found insensitive.
            foreach (var x in result.Excluded) SensRows.Add(SensitivityRowViewModel.Excluded(this, x));
        }
        SelectedSensRow = selectedId is null ? null : SensRows.FirstOrDefault(r => r.Id == selectedId);
        OnPropertyChanged(nameof(HasSensRows));
        if (SnpTile is { } tile) ShowTile(tile.Instance, tile.SelectedPort, tile.SelectedEntry);
    }

    /// <summary>The entry roll-up's label of a contributor: <c>SNP3 · S31</c>, or a key's own label.</summary>
    internal static string EntryLabel(SensitivityContributor c)
        => c.Factor.Snp is { } s ? TunableKey.SnpEntryName(s.Instance, s.I, s.J, s.Ports) : c.Factor.Label;

    // ---- Row actions (R-sens3-5) -----------------------------------------------------

    /// <summary>Add tolerance…: the add-tolerance flow's own door, for the row's keys that step by a percent, dB or
    /// degrees.</summary>
    internal void AddTolerance(SensitivityRowViewModel row)
    {
        var keys = row.StepKeys;
        if (keys.Count == 0) return;
        SetToleranced(keys, on: true);
        StatusText = keys.Count == 1 ? $"{keys[0]} has a tolerance — the next run steps it by σ" : $"{keys.Count} values have a tolerance — the next run steps them by σ";
    }

    /// <summary>Send to Tuning: the Tuning panel adds the keys, on the same schematic.</summary>
    internal void SendToTuning(SensitivityRowViewModel row)
    {
        if (row.DesignKeys is not { Count: > 0 } keys || SendKeysToTuning is null) return;
        SendKeysToTuning(keys);
        StatusText = $"Sent {Plural(keys.Count, "value")} to Tuning";
    }

    /// <summary>Use in Optimizer: <c>opt=1</c> with the catalog's default range, as one undo step on the one setup.</summary>
    internal void UseInOptimizer(SensitivityRowViewModel row)
    {
        if (_tuned is null || Catalog is not { } catalog || row.DesignKeys is not { Count: > 0 } keys) return;
        var setup = Setup;
        int changed = 0;
        foreach (var key in keys)
        {
            if (setup?.Variables.Any(v => v.Key == key && v.Opt) == true || catalog.Find(key) is not { } t) continue;
            setup = TuningSetupEdits.WithOpt(setup, t, true);
            changed++;
        }
        if (changed == 0) return;
        if (Execute(setup!, changed == 1 ? $"Optimize {keys[0]}" : $"Optimize {changed} values") is null)
            StatusText = $"{Plural(changed, "value")} added to the Optimizer";
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>The ranked factors with no tolerance — what "Run Yield on these" says the Monte Carlo would hold still.</summary>
    public int UntolerancedRanked => SensFactors.Where(f => f.Basis != SensitivityBasis.Sigma).Select(f => f.Key).Distinct().Count();

    private bool CanRunYieldOnThese() => HasSensResult && OpenYieldOnRanked is not null && !IsRunActive;

    /// <summary>Run Yield on these: the Yield tab in Monte Carlo mode, on the same schematic.</summary>
    [RelayCommand(CanExecute = nameof(CanRunYieldOnThese))]
    private void RunYieldOnThese() => OpenYieldOnRanked?.Invoke(UntolerancedRanked);

    private void NotifySensCommands() => RunYieldOnTheseCommand.NotifyCanExecuteChanged();

    // ---- The SnP matrix tile (R-sens3-6) -----------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnpTile))]
    private SnpMatrixTileViewModel? _snpTile;

    public bool HasSnpTile => SnpTile is not null;

    internal SnpInstanceInfo? SnpInfo(string instance)
        => Catalog?.SnpInstances.FirstOrDefault(s => s.Instance == instance && s.Ports > 0);

    private void ShowTile(string instance, int? port, (int I, int J)? entry)
    {
        if (SnpInfo(instance) is not { } info) { SnpTile = null; return; }
        var shares = new Dictionary<(int, int), double>();
        bool hasShares = false;
        if (SelectedResponse is { } resp)
            foreach (var c in resp.Contributors)
                if (c.Factor.Snp is { } s && s.Instance == instance)
                {
                    hasShares = true;
                    shares[(s.I, s.J)] = shares.GetValueOrDefault((s.I, s.J)) + c.Share;
                    if (s.Tied && s.I != s.J) shares[(s.J, s.I)] = shares.GetValueOrDefault((s.J, s.I)) + c.Share;
                }
        if (SnpTile is { } tile && tile.Instance == instance) tile.Bind(info, shares, hasShares, port, entry);
        else SnpTile = new SnpMatrixTileViewModel(this, info, shares, hasShares, port, entry);
    }

    /// <summary>The component the tile's Tie writes to: a top-level SnP instance of the focused schematic.</summary>
    internal EditableComponent? SnpComponent(string instance)
        => instance.Contains(':') ? null
         : _tuned?.EditModel.Components.FirstOrDefault(c => c.Symbol == SymbolKind.Snp && c.InstanceName == instance);

    /// <summary>
    /// Tie reciprocal pairs (R-sens3-6): writes <c>Reciprocal=</c> on the instance — back to <c>auto</c> (the parameter
    /// removed) when the choice is the one the file makes on its own — as ONE undo step with the removal of every
    /// <c>tune</c> line that names an upper-triangle key tying would end, asked once first. Cancelling changes nothing.
    /// Returns false when nothing was changed.
    /// </summary>
    public async Task<bool> SetSnpTied(string instance, bool tie)
    {
        if (_tuned is not { } tuned || SnpInfo(instance) is not { Tied: { } tied } info || tied == tie) return false;
        if (SnpComponent(instance) is not { } comp) return false;

        // The lines that name a key tying ends: an upper-triangle entry (i < j) of this instance.
        var setup = Setup;
        var gone = tie
            ? (setup?.Variables ?? []).Where(v => v.Key.StartsWith(instance + ".", StringComparison.Ordinal)
                                                 && RfCore.SnpPerturbation.TryParseName(v.Key[(instance.Length + 1)..], out _, out int i, out int j)
                                                 && i < j).Select(v => v.Key).ToList()
            : [];
        if (gone.Count > 0 && Confirm is not null)
        {
            var lines = gone.Select(k => $"tune {k}").ToList();
            if (!await Confirm($"Tying {instance}'s reciprocal pairs removes {Plural(gone.Count, "tune line")} naming entries it ends", lines))
                return false;
        }

        bool autoTies = info.Reciprocity is { } rec && rec.Worst <= RfCore.SnpPerturbation.ReciprocalTolerance;
        var param = comp.Parameters.FirstOrDefault(p => p.Name.Equals(RfCore.SnpPerturbation.ReciprocalParameter, StringComparison.OrdinalIgnoreCase));
        IUiCommand? paramCmd;
        if (tie == autoTies)
            paramCmd = param is null ? null : new RemoveParameterCommand(tuned.EditModel, comp, param);
        else
        {
            string value = tie ? "tied" : "untied";
            paramCmd = param is null
                ? new AddParameterCommand(tuned.EditModel, comp,
                    new EditableParameter { Name = RfCore.SnpPerturbation.ReciprocalParameter, Expression = value, ShowOnSchematic = false })
                : new EditParameterCommand(tuned.EditModel, param, value);
        }
        if (paramCmd is null) return false;

        string description = tie ? $"Tie {instance}'s reciprocal pairs" : $"Untie {instance}'s reciprocal pairs";
        IUiCommand cmd = new DescribedCommand(paramCmd, description);
        if (gone.Count > 0)
        {
            var next = setup!.Clone();
            next.Variables.RemoveAll(v => gone.Contains(v.Key));
            next.Correlations.RemoveAll(c => gone.Contains(c.First) || gone.Contains(c.Second));
            cmd = new DescribedCommand(new CompositeCommand(paramCmd, new SetTuningSetupCommand(tuned.EditModel, next, description)), description);
        }
        tuned.Execute(cmd);
        EditCommitted?.Invoke(tuned);
        if (Defer is not null) RefreshNow();
        StatusText = gone.Count > 0 ? $"{description} — {Plural(gone.Count, "tune line")} removed" : description;
        return true;
    }

    /// <summary>A command under the name the undo list shows.</summary>
    private sealed class DescribedCommand(IUiCommand inner, string description) : IUiCommand
    {
        public string Description => description;
        public void Execute() => inner.Execute();
        public void Undo() => inner.Undo();
    }
}

/// <summary>One SnP instance in the S-parameter blocks section (R-sens3-4): Include, the instance, and what it holds.</summary>
public sealed partial class SnpBlockRowViewModel : ObservableObject
{
    private readonly YieldPanelViewModel _panel;
    private bool _syncing;

    internal SnpBlockRowViewModel(YieldPanelViewModel panel, SnpInstanceInfo info)
    {
        _panel = panel;
        Instance = info.Instance;
        Bind(info);
    }

    public string Instance { get; }

    /// <summary><c>4 ports · tied · 20 factors</c>, or why the file did not load.</summary>
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string? _tieReason;

    /// <summary>Include — not named in <c>snpexclude</c>.</summary>
    [ObservableProperty] private bool _isIncluded;

    /// <summary>Disabled (keeping its tick) while the S-parameters master is off.</summary>
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsIncludedChanged(bool value)
    {
        if (!_syncing) _panel.SetSnpIncluded(Instance, value);
    }

    internal void Bind(SnpInstanceInfo info)
    {
        Summary = info.Tied is { } tied
            ? $"{info.Ports} port{(info.Ports == 1 ? "" : "s")} · {(tied ? "tied" : "untied")} · {info.Entries} factor{(info.Entries == 1 ? "" : "s")}"
            : info.LoadError ?? "not read";
        TieReason = info.Tied is null ? info.LoadError : info.TieReason;
    }

    internal void Sync()
    {
        _syncing = true;
        IsIncluded = _panel.IsSnpIncluded(Instance);
        IsEnabled = _panel.SensSnp;
        _syncing = false;
    }
}
