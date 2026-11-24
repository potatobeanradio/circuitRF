using CircuitRF.Core.Design;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// The Designer's mask, sent to the Optimizer: |S21| in dB at least −Ap over each passband and at most
/// −As over each stopband, over the bands the Designer checks, clipped to the analysis's sweep.
/// </summary>
public sealed class FilterMaskGoalsTests
{
    private static readonly FilterMaskSpec Bandpass =
        new(NetworkForm.Bandpass, 3, 20, 1800e6, 2000e6, 2700e6, 2800e6);

    private static EditableComponent Port(string num)
    {
        var c = new EditableComponent { InstanceName = "T" + num, Symbol = SymbolKind.Term };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num });
        return c;
    }

    private static SParameterAnalysis Sweep(string start, string stop) =>
        new("SP1", new FrequencySpec(start, stop, 201, SweepKind.Linear, "MHz", "MHz"));

    [Fact]
    public void ABandpassMaskIsOnePassGoalAndTwoStopGoals_ClippedToTheSweep()
    {
        var made = FilterMaskGoals.For(Bandpass, [Sweep("1000", "4000")], [Port("1"), Port("2")]);
        Assert.Null(made.Refusal);
        Assert.Equal((1, 2), (made.InPort, made.OutPort));
        Assert.Collection(made.Goals,
            g =>
            {
                Assert.Equal("mask_stop_lower", g.Name);
                Assert.Equal(GoalType.Le, g.Type);
                Assert.Equal("-20", g.Limit);
                Assert.Equal(("1 GHz", "1.8 GHz"), (g.Range!.Lo, g.Range.Hi));
            },
            g =>
            {
                Assert.Equal("mask_pass", g.Name);
                Assert.Equal(GoalType.Ge, g.Type);
                Assert.Equal("-3", g.Limit);
                Assert.Equal(("2 GHz", "2.7 GHz"), (g.Range!.Lo, g.Range.Hi));
            },
            g =>
            {
                Assert.Equal("mask_stop_upper", g.Name);
                Assert.Equal(("2.8 GHz", "4 GHz"), (g.Range!.Lo, g.Range.Hi));
            });
        Assert.All(made.Goals, g =>
        {
            Assert.Equal("dB(SP1.S(2, 1))", g.Expression);
            Assert.Equal("SP1", g.Analysis);
            Assert.Null(GoalTemplates.Validate(g.Expression));
            Assert.True(TunableValue.TryParse(g.Range!.Lo, out _, out _, out _));
        });
    }

    [Fact]
    public void ABandTheSweepMissesHasNoGoal_AndIsNamed()
    {
        var made = FilterMaskGoals.For(Bandpass, [Sweep("1900", "2750")], [Port("1"), Port("2")]);
        Assert.Equal(["mask_pass"], made.Goals.Select(g => g.Name));
        Assert.Equal(2, made.Notes.Count(n => n.Contains("outside SP1's sweep")));
    }

    [Fact]
    public void NoSParameterAnalysisIsARefusal()
    {
        var made = FilterMaskGoals.For(Bandpass, [], [Port("1"), Port("2")]);
        Assert.Empty(made.Goals);
        Assert.Contains("no S-parameter analysis", made.Refusal);
    }

    [Fact]
    public void PortsOtherThanOneAndTwo_TheLowestTwoAreRead()
    {
        var made = FilterMaskGoals.For(Bandpass, [Sweep("1000", "4000")], [Port("5"), Port("3")]);
        Assert.Equal((3, 5), (made.InPort, made.OutPort));
        Assert.All(made.Goals, g => Assert.Equal("dB(SP1.S(5, 3))", g.Expression));
    }

    [Fact]
    public void TheGoalsCoverTheBandsTheDesignerChecks()
    {
        foreach (var mask in new[]
                 {
                     new FilterMaskSpec(NetworkForm.Lowpass, 0.5, 30, 1000e6, 1300e6, 0, 0),
                     new FilterMaskSpec(NetworkForm.Highpass, 0.5, 30, 700e6, 1000e6, 0, 0),
                     new FilterMaskSpec(NetworkForm.Bandstop, 1, 25, 900e6, 950e6, 1050e6, 1100e6),
                     Bandpass,
                 })
        {
            var goals = FilterMaskGoals.Build(mask, "SP1", 1, 2);
            var (pass, stop) = FilterMask.CheckFrequencies(mask, 10);
            foreach (var (hz, isPass) in pass.Select(f => (f, true)).Concat(stop.Select(f => (f, false))))
                Assert.Contains(goals, g => (g.Type == GoalType.Ge) == isPass
                    && TunableValue.TryParse(g.Range!.Lo, out _, out _, out double lo) && lo <= hz * (1 + 1e-9)
                    && TunableValue.TryParse(g.Range!.Hi, out _, out _, out double hi) && hi >= hz * (1 - 1e-9));
        }
    }

    [Fact]
    public async Task TheDesignersButtonSendsItsMaskToThePlacementSchematic()
    {
        var vm = new LcFilterDesignerViewModel { Response = FilterResponse.Chebyshev, Form = NetworkForm.Bandpass };
        vm.UnitA = vm.UnitB = vm.UnitC = vm.UnitD = "MHz";
        vm.FaText = "1800"; vm.FbText = "2000"; vm.FcText = "2700"; vm.FdText = "2800";
        vm.ApText = "3"; vm.RippleText = "0.5"; vm.AsText = "20";
        await vm.DesignAsync(0);
        Assert.Equal(new FilterMaskSpec(NetworkForm.Bandpass, 3, 20, 1800e6, 2000e6, 2700e6, 2800e6), vm.CurrentMask);

        // No schematic to send it to: said, and nothing is sent.
        FilterMaskSpec? sent = null;
        vm.MaskGoalSink = (_, m) => { sent = m; return "sent"; };
        Assert.Contains("no schematic", vm.SendMaskToOptimizer());
        Assert.Null(sent);

        vm.SchematicProvider = () => new CircuitRF.Ui.ViewModels.SchematicViewModel(new SchematicEditModel());
        Assert.Equal("sent", vm.SendMaskToOptimizer());
        Assert.Equal(vm.CurrentMask, sent);
    }
}
