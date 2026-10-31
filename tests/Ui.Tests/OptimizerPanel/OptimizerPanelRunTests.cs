using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Optimization;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>brief-tuneopt-10 R-to10-6/8/10: a run updates the goals and the best values per iteration,
/// pauses and resumes, stops keeping the best, refuses what cannot run, and shows a complex value's
/// parts as views of one decoded number.</summary>
public sealed class OptimizerPanelRunTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Run_UpdatesGoalsAndBest_PauseHolds_ResumeContinues()
    {
        // Random never converges and 37.5 is not on its grid, so the run ends at its iteration limit.
        var f = OptimizerPanelFixture.OneVariable("0", "100", "random", 6,
            OptimizerPanelFixture.Goal("G", "A", GoalType.Eq, "37.123456"));
        int pausedAt = 0;
        f.Panel.PostToUi = a =>
        {
            a();
            if (pausedAt == 0 && f.Panel.Iteration >= 2)
            {
                pausedAt = f.Panel.Iteration;
                Assert.True(f.Panel.Goals[0].HasResult);                         // the bar is live
                Assert.Equal(TunableValue.InUnit(f.Panel.BestValues!["A"], "")!.Value, f.Row("A").Best);   // and the best value
                f.Panel.PauseResumeCommand.Execute(null);
            }
        };

        f.Panel.RunCommand.Execute(null);
        var task = f.Panel.RunTask!;
        Assert.True(f.Panel.CurrentRun!.Held.WaitOne(Wait), "the run never held");
        Assert.Equal(pausedAt, f.Panel.Iteration);                               // paused: nothing moves
        Assert.True(f.Panel.IsPaused);
        Assert.True(f.Panel.LockInCommand.CanExecute(null));                     // keep it while paused

        f.Panel.PauseResumeCommand.Execute(null);
        await task.WaitAsync(Wait);

        Assert.Equal(OptimizerRunState.Finished, f.Panel.State);
        Assert.Equal(6, f.Panel.Result!.Iterations);                             // continued to the limit
        Assert.Equal(6, f.Panel.CostHistory.Count);
        Assert.StartsWith("The iteration limit (6) was reached · not met: G · best cost", f.Panel.StatusText);
    }

    [Fact]
    public async Task Stop_KeepsTheBestPoint()
    {
        var f = OptimizerPanelFixture.OneVariable("0", "100", "random", 50,
            OptimizerPanelFixture.Goal("G", "A", GoalType.Eq, "37.123456"));
        f.Panel.PostToUi = a =>
        {
            a();
            if (f.Panel.IsRunning && f.Panel.Iteration == 2) f.Panel.StopCommand.Execute(null);
        };

        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(Wait);

        var r = f.Panel.Result!;
        Assert.Equal("stopped", r.FinishReason);
        Assert.Equal(OptimizerRunState.Finished, f.Panel.State);
        Assert.Equal(r.BestValues["A"], f.Panel.BestValues!["A"]);
        Assert.Equal(TunableValue.InUnit(r.BestValues["A"], "")!.Value, f.Row("A").Best);
        Assert.True(f.Panel.PushCommand.CanExecute(null));
    }

    [Fact]
    public void Run_RefusesASetupWithNoGoals_InTheStatusLine_AndInMessages()
    {
        var f = OptimizerPanelFixture.OneVariable("0", "100", "simplex", 10);
        var posted = new List<string>();
        f.Panel.ReportProblem = posted.Add;
        f.Panel.RunCommand.Execute(null);

        Assert.Equal("Refused: Nothing to optimize: the design has no enabled goal.", f.Panel.StatusText);
        // Issue #33: the refusal reaches the Messages panel too, in the same words.
        Assert.Equal([f.Panel.StatusText], posted);
        Assert.Equal(OptimizerRunState.Idle, f.Panel.State);
        Assert.Null(f.Panel.RunTask);
    }

    private static OptimizerPanelFixture Complex(params TunableEntry[] entries)
    {
        const string cnl = """
            Z = 30+40j Ohm
            Port:P1 in 0 Num=1 Z=50 Ohm
            R:R1 in 0 R=50 Ohm
            """;
        var setup = new TuningSetup
        {
            Variables = [.. entries],
            Goals     = [OptimizerPanelFixture.Goal("Re", "real(Z)", GoalType.Eq, "60"),
                         OptimizerPanelFixture.Goal("Im", "imag(Z)", GoalType.Eq, "20")],
            Optimizer = new OptimizerSettings { Algorithm = "simplex", MaxIterations = 400 },
        };
        return new OptimizerPanelFixture(cnl, setup, ("Z", "30+40j", "Ohm"));
    }

    [Fact]
    public async Task AMagPhasePair_Runs_AndEveryPartRowShowsTheOneDecodedValue()
    {
        var f = Complex(
            OptimizerPanelFixture.Var("mag(Z)", "10 Ohm", "100 Ohm"),
            OptimizerPanelFixture.Var("phase(Z)", "0 deg", "90 deg"),
            OptimizerPanelFixture.Var("real(Z)", "0 Ohm", "100 Ohm", opt: false, tune: true));   // tunes only

        f.Panel.RunCommand.Execute(null);
        await f.Panel.RunTask!.WaitAsync(Wait);

        Assert.True(ComplexValue.TryParse(f.Panel.BestValues!["Z"], out var z, out _, out _));
        Assert.True((z - new Complex(60, 20)).Magnitude < 1e-2, z.ToString());
        Assert.Equal(z.Magnitude, f.Row("mag(Z)").Best, 9);
        Assert.Equal(z.Phase * 180 / Math.PI, f.Row("phase(Z)").Best, 9);
        Assert.Equal(z.Real, f.Row("real(Z)").Best, 9);                       // not optimized, still shown
    }

    [Fact]
    public void ThreeOptimizedPartsOfOneValue_AreRefused_WithTheOptimizersSentence()
    {
        var f = Complex(
            OptimizerPanelFixture.Var("real(Z)", "0 Ohm", "100 Ohm"),
            OptimizerPanelFixture.Var("mag(Z)", "10 Ohm", "100 Ohm"),
            OptimizerPanelFixture.Var("phase(Z)", "0 deg", "90 deg"));

        f.Panel.RunCommand.Execute(null);

        Assert.StartsWith("Refused: ", f.Panel.StatusText);
        Assert.Contains("are three parts of Z, which has two degrees of freedom", f.Panel.StatusText);
        Assert.Null(f.Panel.RunTask);
    }
}
