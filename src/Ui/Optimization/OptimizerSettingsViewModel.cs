// ================================================================
//  OptimizerSettingsViewModel.cs  —  the ⚙ flyout (brief-tuneopt-10 R-to10-5)
//
//  Limits, the cost form (locked when the algorithm accepts only one),
//  which analyses an evaluation runs, parallelism, seed, and the chosen
//  algorithm's own options — generated from the TO-7 registry, never
//  listed here. The flyout edits a copy; closing it writes the whole
//  copy back as ONE undo step, and only when something changed.
// ================================================================

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Optimization;

/// <summary>One option the selected algorithm reads, from the registry.</summary>
public sealed partial class OptimizerOptionRow(OptimizerOptionInfo info, string value) : ObservableObject
{
    public OptimizerOptionInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Default => Info.Default;
    public string Summary => Info.Summary;

    /// <summary>Empty means the default.</summary>
    [ObservableProperty] private string _value = value;
}

public sealed partial class OptimizerSettingsViewModel(OptimizerPanelViewModel panel) : ObservableObject
{
    [ObservableProperty] private string _maxIterations = "";
    [ObservableProperty] private string _maxEvaluations = "";
    [ObservableProperty] private string _timeLimit = "";
    [ObservableProperty] private string _parallelism = "";
    [ObservableProperty] private string _seed = "";
    [ObservableProperty] private bool _isMinimax;
    [ObservableProperty] private bool _allAnalyses;

    /// <summary>The corners every goal must be met at (brief-yield-7 R-ya7-1): empty for none, <c>all</c>, or names
    /// separated by commas.</summary>
    [ObservableProperty] private string _corners = "";

    /// <summary>With corners, whether the nominal is evaluated too.</summary>
    [ObservableProperty] private bool _includeNominal = true;

    /// <summary>brief-sdl-7 D1 — the em= choices, in <see cref="EmModeIndex"/>'s order: unset (the default), then the three modes.</summary>
    public string[] EmModes { get; } =
    [
        "Automatic — correct when a goal is simulated by EM",
        "Correct the circuit models with EM",
        "Every evaluation by EM",
        "Circuit models only",
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmBudgetDefault))]
    private int _emModeIndex;

    /// <summary>brief-sdl-7 D4 — the solve budget; empty is the mode's default.</summary>
    [ObservableProperty] private string _emBudget = "";

    /// <summary>The budget box's placeholder: what an empty box means under the chosen mode.</summary>
    public string EmBudgetDefault => EmModeIndex == 2 ? "30" : EmModeIndex == 3 ? "—" : "12";

    private static readonly OptimizerEmMode?[] EmModeValues = [null, OptimizerEmMode.Correct, OptimizerEmMode.Direct, OptimizerEmMode.Off];

    /// <summary>The algorithm accepts one cost form only, which it sets (TO-7 R-to7-6).</summary>
    [ObservableProperty] private bool _costLocked;

    public bool IsLeastSquares
    {
        get => !IsMinimax;
        set => IsMinimax = !value;
    }

    partial void OnIsMinimaxChanged(bool value) => OnPropertyChanged(nameof(IsLeastSquares));

    public bool GoalAnalysesOnly
    {
        get => !AllAnalyses;
        set => AllAnalyses = !value;
    }

    partial void OnAllAnalysesChanged(bool value) => OnPropertyChanged(nameof(GoalAnalysesOnly));

    /// <summary>The selected algorithm's options, in the registry's order.</summary>
    public ObservableCollection<OptimizerOptionRow> Options { get; } = [];

    /// <summary>The common options every run reads (stall).</summary>
    public ObservableCollection<OptimizerOptionRow> CommonOptions { get; } = [];

    public bool HasOptions => Options.Count > 0;

    [ObservableProperty] private string _algorithmLabel = "";

    private OptimizerSettings _loaded = new();

    /// <summary>Reads the document's settings — every time the flyout opens.</summary>
    public void Reload()
    {
        _loaded = panel.Settings?.Clone() ?? new OptimizerSettings();
        var s   = _loaded;
        var alg = OptimizerAlgorithms.Find(s.Algorithm) ?? OptimizerAlgorithms.All[0];

        MaxIterations  = s.MaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaxEvaluations = s.MaxEvaluations?.ToString(CultureInfo.InvariantCulture) ?? "";
        TimeLimit      = s.TimeLimit ?? "";
        Parallelism    = s.Parallelism?.ToString(CultureInfo.InvariantCulture) ?? "";
        Seed           = s.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        AllAnalyses    = s.Scope == OptimizerScope.All;
        Corners        = s.Corners ?? "";
        IncludeNominal = s.Nominal != false;
        EmModeIndex    = Math.Max(0, Array.IndexOf(EmModeValues, s.Em));
        EmBudget       = s.EmBudget?.ToString(CultureInfo.InvariantCulture) ?? "";
        CostLocked     = alg.Costs.Count == 1;
        IsMinimax      = CostLocked ? alg.Costs[0] == OptimizerCost.Minimax : s.Cost == OptimizerCost.Minimax;
        AlgorithmLabel = alg.Label;

        Options.Clear();
        foreach (var o in alg.Options) Options.Add(new OptimizerOptionRow(o, s.Options?.GetValueOrDefault(o.Name) ?? ""));
        CommonOptions.Clear();
        foreach (var o in OptimizerAlgorithms.CommonOptions)
            CommonOptions.Add(new OptimizerOptionRow(o, s.Options?.GetValueOrDefault(o.Name) ?? ""));
        OnPropertyChanged(nameof(HasOptions));
    }

    /// <summary>The settings the fields describe. A box that is empty or not a whole number is
    /// "unset"; an option left empty takes its default and is not written.</summary>
    public OptimizerSettings Build()
    {
        var s = _loaded.Clone();
        s.MaxIterations  = Int(MaxIterations);
        s.MaxEvaluations = Int(MaxEvaluations);
        s.TimeLimit      = TimeLimit.Trim().Length == 0 ? null : TimeLimit.Trim();
        s.Parallelism    = Int(Parallelism);
        s.Seed           = Int(Seed, allowZero: true);
        s.Scope          = AllAnalyses ? OptimizerScope.All : OptimizerScope.GoalAnalyses;
        var corners      = Corners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        s.Corners        = corners.Length == 0 || corners is ["none" or "None"] ? null
                         : corners is ["all" or "All"] ? "all" : string.Join(',', corners);
        s.Nominal        = IncludeNominal ? null : false;
        s.Em             = EmModeValues[Math.Clamp(EmModeIndex, 0, EmModeValues.Length - 1)];
        s.EmBudget       = Int(EmBudget);
        // A one-form algorithm sets its form when it runs; the file keeps the user's own choice.
        if (!CostLocked) s.Cost = IsMinimax ? OptimizerCost.Minimax : OptimizerCost.LeastSquares;

        var options = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        // Options of OTHER algorithms the file carries survive a visit to this one's settings.
        var shown = Options.Concat(CommonOptions).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (s.Options is not null)
            foreach (var (k, v) in s.Options) if (!shown.Contains(k)) options[k] = v;
        foreach (var o in Options.Concat(CommonOptions))
            if (o.Value.Trim().Length > 0) options[o.Name] = o.Value.Trim();
        s.Options = options.Count == 0 ? null : options;
        return s;
    }

    /// <summary>The flyout closed: writes what changed as one undo step.</summary>
    public void Commit() => panel.EditSettings(Build(), "Optimizer settings");

    private static int? Int(string text, bool allowZero = false)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
           && (n > 0 || allowZero && n >= 0) ? n : null;
}
