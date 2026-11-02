using System.ComponentModel;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Optimization;

/// <summary>
/// A goal row's two Use ticks, shared by the Optimizer's and the Yield panel's goal rows (one control, GoalUseTicksView):
/// <b>Opt</b>, the optimizer aims for it, and <b>Sensitivity / Yield</b>, the yield counts it as a spec and the Sensitivity
/// panel ranks it (it ranks the yield goals). The two ticks ARE the goal's <see cref="GoalUse"/> — both is
/// <see cref="GoalUse.Both"/>, so nothing new is written to a file — and the last tick cannot be cleared.
/// </summary>
public interface IGoalUseTicks : INotifyPropertyChanged
{
    bool UseOpt { get; set; }
    bool UseSensYield { get; set; }
    string UseSummary { get; }
}

public static class GoalUseTicks
{
    public static bool Opt(GoalUse u) => u != GoalUse.Yield;
    public static bool SensYield(GoalUse u) => u != GoalUse.Opt;

    /// <summary>The use two ticks make, or null for neither (refused: a goal nothing uses would be a disabled one).</summary>
    public static GoalUse? From(bool opt, bool sensYield) => (opt, sensYield) switch
    {
        (true, true)  => GoalUse.Both,
        (true, false) => GoalUse.Opt,
        (false, true) => GoalUse.Yield,
        _             => null,
    };

    /// <summary>What the closed control reads.</summary>
    public static string Summary(GoalUse u) => u switch { GoalUse.Opt => "Opt", GoalUse.Yield => "Sens/Yield", _ => "All" };

    /// <summary>The undo entry's word: <c>Use goal G for …</c>.</summary>
    public static string Word(GoalUse u) => u switch
    {
        GoalUse.Opt => "optimizing", GoalUse.Yield => "sensitivity and yield", _ => "optimizing, sensitivity and yield",
    };

    public const string Tip = "Which panels use this goal: Opt, the optimizer aims for it; Sensitivity / Yield, the yield counts it "
                            + "as a spec and the Sensitivity panel ranks it.";
}
