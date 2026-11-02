using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Tests.Tuning;
using CircuitRF.Ui.Tuning;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>
/// The first field trial of Tuning and the Optimizer together: the slider's mode is one choice, an E series
/// is a choice in both panels, the Tuning panel lets go of a value the schematic changed under it, a run
/// takes the display from a live Tuning session, and Reset puts back what a Push wrote.
/// </summary>
public sealed class TuningOptimizerHandoffTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public void SliderMode_IsOneChoice_LogClearsTheStep_SeriesStepsRungs()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", on: true);
        var row = f.Row("R1.R");

        row.StepText = "1";
        row.CommitStepText();
        row = f.Row("R1.R");
        Assert.True(row.IsModeStep);

        row.SetLogCommand.Execute(null);
        row = f.Row("R1.R");
        Assert.Equal((true, false, (double?)null), (row.IsModeLog, row.IsModeStep, row.Step));

        row.SetSeriesCommand.Execute("E12");
        row = f.Row("R1.R");
        Assert.Equal((TuneDiscrete.E12, "E12", false), (row.Discrete, row.ScaleLabel, row.IsModeLog));
        row.Nudge(+1, TuningNudge.Step);
        Assert.Equal(56, row.Value);                                 // 50 Ω → the next E12 rung
        row.Nudge(-1, TuningNudge.Step);
        row.Nudge(-1, TuningNudge.Step);
        Assert.Equal(39, row.Value);                                 // 56 → 47 → 39
    }

    [Fact]
    public void TunedValue_FollowsTheSchematic_WhenAnotherPanelPushes()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned("R1.R", on: true);
        var row = f.Row("R1.R");
        row.ValueBoxText = "60";
        row.CommitValueText();
        Assert.Equal(60, f.Row("R1.R").Value);

        // What the Optimizer's (or Yield's) Push does: writes the schematic, not the slider.
        var catalog = TunableCatalog.Discover(f.Top.EditModel, Optimization.TuningFixture.Resolver(f.DutModel), null);
        TuningPush.Push(catalog, new Dictionary<string, string> { ["R1.R"] = "75 Ohm" }, f.Top, _ => null);

        Assert.Equal(75, f.Row("R1.R").Value);
        Assert.False(f.Row("R1.R").DiffersFromSchematic);
    }

    [Fact]
    public async Task Run_TakesTheDisplay_Push_ThenResetPutsTheDrawnValueBack()
    {
        var f = OptimizerPanelFixture.OneVariable("0", "100", "lm", 50,
            OptimizerPanelFixture.Goal("G", "A", GoalType.Eq, "37"));
        var took = new List<object>();
        f.Panel.TakingDisplay = vm => took.Add(vm);

        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(Wait);
        Assert.Equal([f.Top], took);

        f.Panel.PushCommand.Execute(null);
        Assert.Equal(37, double.Parse(VarA(f)), 6);
        Assert.True(f.Panel.ResetCommand.CanExecute(null));

        f.Panel.ResetCommand.Execute(null);
        Assert.Equal("10", VarA(f));
        Assert.Equal("Reset: 1 value back as drawn", f.Panel.StatusText);
        Assert.Null(f.Panel.Result);
        Assert.Equal(10, f.Row("A").Best);
    }

    [Fact]
    public async Task Discrete_RunsOverAnESeries()
    {
        const string cnl = """
            A = 10
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=50 Ohm
            """;
        var entry = OptimizerPanelFixture.Var("A", "1", "100");
        entry.Discrete = TuneDiscrete.E12;
        var setup = new TuningSetup
        {
            Variables = [entry],
            Goals     = [OptimizerPanelFixture.Goal("G", "A", GoalType.Eq, "4.6")],
            Optimizer = new OptimizerSettings { Algorithm = "discrete", MaxIterations = 50 },
        };
        var f = new OptimizerPanelFixture(cnl, setup, ("A", "10", ""));
        Assert.Equal("E12", f.Row("A").DiscreteText);

        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(Wait);

        Assert.Equal(4.7, TunableValue.InUnit(f.Panel.BestValues!["A"], "")!.Value, 9);
    }

    [Fact]
    public async Task AYieldOnlyGoal_IsNamedInTheRefusal_AndItsOptTickUsesItForOptimizing()
    {
        var goal = OptimizerPanelFixture.Goal("Match", "A", GoalType.Eq, "37");
        goal.Use = GoalUse.Yield;                                    // set in the Yield panel; still Enabled
        var f = OptimizerPanelFixture.OneVariable("0", "100", "lm", 50, goal);
        var row = f.Panel.Goals.Single();
        Assert.True((row.IsEnabled, row.IsYieldOnly) == (true, true));

        f.Panel.RunCommand.Execute(null);
        Assert.Contains("every enabled goal is a yield spec only", f.Panel.StatusText);
        Assert.Contains("Match", f.Panel.StatusText);

        Assert.True((row.UseOpt, row.UseSensYield, row.UseSummary) == (false, true, "Sens/Yield"));   // the Yield panel's state
        row.UseSensYield = false;                                     // the last tick: refused, nothing written
        Assert.Equal(GoalUse.Yield, f.Top.EditModel.Tuning!.Goals.Single().Use);
        Assert.True(row.UseSensYield);
        row.UseOpt = true;
        Assert.False(f.Panel.Goals.Single().IsYieldOnly);
        Assert.Equal("All", f.Panel.Goals.Single().UseSummary);
        Assert.Equal(GoalUse.Both, f.Top.EditModel.Tuning!.Goals.Single().Use);
        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(Wait);
        Assert.Equal(OptimizerRunState.Finished, f.Panel.State);
    }

    [Fact]
    public void ESeriesSpelling_RoundTrips()
    {
        var setup = new TuningSetup { Variables = [new TunableEntry { Key = "L1.L", Min = "1 nH", Max = "50 nH", Discrete = TuneDiscrete.E24 }] };
        string line = CircuitRF.Core.Netlist.TuningDirectiveText.Write(setup).Single(l => l.StartsWith("tune "));
        Assert.Contains("discrete=e24", line);
        Assert.Equal([1.0, 1.1, 1.2], TunableValue.DiscreteLevels(TuneDiscrete.E24, "nH", 1, 1.25));
    }

    [Fact]
    public void WorstPoint_OnAFrequencyAxis_ReadsInHertzPrefixes()
        => Assert.Equal("950 MHz", OptimizerPanelViewModel.AxisText(9.5e8, "freq"));

    private static string VarA(OptimizerPanelFixture f)
        => f.Top.EditModel.Components.SelectMany(c => c.Parameters).Single(p => p.Name == "A").Expression;
}
