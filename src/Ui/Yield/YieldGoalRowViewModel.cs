// ================================================================
//  YieldGoalRowViewModel.cs  —  one goal in the Yield panel's spec
//  list (brief-yield-10 R-ya10-4, yield overview D4)
//
//  A yield spec IS a goal. The row shows it as the Optimizer does and
//  adds its Use toggle (Opt / Yield / Both) and, once a run has data,
//  its own yield with the interval as a bar against the target.
//  Goals are authored in the Optimizer, never here.
// ================================================================

using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Statistics;
using CircuitRF.Ui.Optimization;

namespace CircuitRF.Ui.Yield;

public sealed partial class YieldGoalRowViewModel : ObservableObject, IGoalUseTicks
{
    private readonly YieldPanelViewModel _panel;

    internal YieldGoalRowViewModel(YieldPanelViewModel panel, OptimizationGoal goal)
    {
        _panel = panel;
        Bind(goal);
    }

    public OptimizationGoal Goal { get; private set; } = new();

    public string Name => Goal.Name;

    public string Summary => OptimizerGoalRowViewModel.Summarize(Goal);

    /// <summary>Disabled goals are listed, dimmed: enabling one is the Optimizer's.</summary>
    public bool IsEnabled => Goal.Enabled;

    /// <summary>The Use ticks (<see cref="IGoalUseTicks"/>) — the same state the Optimizer's goal row shows and sets.</summary>
    public bool UseOpt
    {
        get => GoalUseTicks.Opt(Goal.Use);
        set => SetUse(value, UseSensYield);
    }

    public bool UseSensYield
    {
        get => GoalUseTicks.SensYield(Goal.Use);
        set => SetUse(UseOpt, value);
    }

    public string UseSummary => GoalUseTicks.Summary(Goal.Use);

    private void SetUse(bool opt, bool sensYield)
    {
        if (GoalUseTicks.From(opt, sensYield) is { } use && use != Goal.Use) _panel.SetGoalUse(this, use);
        else RaiseUse();                                     // refused or unchanged: the boxes go back to what the goal says
    }

    private void RaiseUse()
    {
        OnPropertyChanged(nameof(UseOpt));
        OnPropertyChanged(nameof(UseSensYield));
        OnPropertyChanged(nameof(UseSummary));
    }

    /// <summary>The yield a run gave this goal, with its interval; empty before one.</summary>
    [ObservableProperty] private bool _hasYield;
    [ObservableProperty] private string _yieldText = "";
    [ObservableProperty] private string _intervalText = "";
    [ObservableProperty] private double _lower;
    [ObservableProperty] private double _upper;
    [ObservableProperty] private double _estimate;
    [ObservableProperty] private double? _target;
    [ObservableProperty] private bool _belowTarget;

    internal void Bind(OptimizationGoal goal)
    {
        Goal = goal;
        RaiseUse();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEnabled));
    }

    internal void ShowYield(YieldEstimate? e, double? target)
    {
        Target = target;
        if (e is not { Counted: > 0 } y || double.IsNaN(y.Yield))
        {
            HasYield = false;
            YieldText = IntervalText = "";
            return;
        }
        HasYield     = true;
        Estimate     = y.Yield;
        Lower        = y.Lower;
        Upper        = y.Upper;
        YieldText    = Percent(y.Yield);
        IntervalText = $"{Percent(y.Lower)} – {Percent(y.Upper)}";
        BelowTarget  = target is { } t && y.Upper < t;
    }

    // ---- Sensitivity (brief-sens-3 R-sens3-5) ------------------------------------

    /// <summary><c>m₀ 0.42 · σ 0.13</c> — the normalized margin at the nominal and the predicted spread; empty before a run.</summary>
    [ObservableProperty] private string _sensText = "";

    /// <summary><c>97.1 % estimated</c> — only when every contributor stepped by σ (SENS-1 D5).</summary>
    [ObservableProperty] private string _estimateText = "";

    [ObservableProperty] private bool _hasSens;

    internal void ShowSens(CircuitRF.Design.Statistics.SensitivityResponse? r)
    {
        HasSens = r is not null;
        SensText = r is null ? "" : $"m₀ {G(r.Nominal)} · σ {G(r.Spread)}";
        EstimateText = r?.Estimate is { } e ? $"{Percent(e)} estimated" : "";
    }

    private static string G(double v) => double.IsFinite(v) ? v.ToString("G3", CultureInfo.InvariantCulture) : "—";

    internal static string Percent(double fraction) => (100 * fraction).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
