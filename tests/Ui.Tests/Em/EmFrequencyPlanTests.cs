using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// An EM setup's frequency PLAN (GitHub discussion 26): a broadband sweep, a finer one around the centre and a single
/// point are solved as their sorted union, carried to the 3D solvers as that exact list, and saved in the <c>.cem</c>
/// without changing a byte of a single-sweep file.
/// </summary>
public sealed class EmFrequencyPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf-emplan-{Guid.NewGuid():N}");

    public EmFrequencyPlanTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static EmSetup Plan() => new()
    {
        Name = "plan", LayoutRef = "a.clay",
        Frequency = new FrequencySpec("4", "8", 5, SweepKind.Linear, "GHz", "GHz"),
        AdditionalFrequencies =
        [
            new FrequencySpec("5.6", "6", 3, SweepKind.Linear, "GHz", "GHz"),
            new FrequencySpec("5.8", "5.8", 1, SweepKind.Linear, "GHz", "GHz"),
        ],
    };

    [Fact]
    public void APlan_IsTheSortedUnion_RoundTripsThroughTheCem_AndReachesThe3DSolversAsItsExactList()
    {
        double[] expected = [4e9, 5e9, 5.6e9, 5.8e9, 6e9, 7e9, 8e9];
        var loaded = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(Plan()));
        Assert.Equal(expected, loaded.ExpandFrequencies().Select(f => Math.Round(f)));
        Assert.Equal(2, loaded.AdditionalFrequencies.Count);

        var f3 = loaded.ToEm3dFrequency(loaded.ExpandFrequencies());
        Assert.Equal(expected, Em3dRunService.FrequenciesHz(f3).Select(f => Math.Round(f)));
        Assert.Equal((4e9, 8e9, 7), (Math.Round(f3.StartHz), Math.Round(f3.StopHz), f3.Points));

        var single = new EmSetup { Name = "one", LayoutRef = "a.clay" };
        Assert.DoesNotContain("AdditionalFrequencies", EmSetupPersistence.Serialize(single));
        Assert.Null(single.ToEm3dFrequency(single.ExpandFrequencies()).ListHz);
    }

    [Fact]
    public void ThePanel_AddsASinglePoint_WritesThePlan_AndRemovesIt_WhileTheCircuitEditorKeepsAOnePointSweep()
    {
        string path = Path.Combine(_dir, "plan.cem");
        var setup = new EmSetup { Name = "plan", LayoutRef = "a.clay" };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup);

        vm.AddFrequencySegmentCommand.Execute(null);
        var added = Assert.Single(vm.Working.AdditionalFrequencies);
        Assert.Equal((setup.Frequency.StartExpr, 1), (added.StopExpr, added.NumPoints));
        Assert.True(vm.FrequencySegments[1].IsSinglePoint);
        Assert.All(vm.FrequencySegments, r => Assert.True(r.CanRemoveSelf));

        vm.FrequencySegments[1].RemoveCommand.Execute(null);
        Assert.Empty(vm.Working.AdditionalFrequencies);
        Assert.False(Assert.Single(vm.FrequencySegments).CanRemoveSelf);

        var circuit = new FrequencySpecViewModel(new SchematicEditModel(), new FrequencySpec("1", "2", 1, SweepKind.Linear, "GHz", "GHz"));
        Assert.False(circuit.IsSinglePoint);
        Assert.Equal("2", circuit.Build().StopExpr);
    }

    /// <summary>The S-parameter analysis offers the same plan: + Segment adds a single point, and it survives the netlist
    /// and sorts into the run's frequencies.</summary>
    [Fact]
    public void TheSParameterEditor_AddsASinglePoint_ThatSurvivesTheNetlistAndJoinsTheRun()
    {
        var model = new SchematicEditModel();
        var body = SpBodyViewModel.FromAnalysis(
            new SParameterAnalysis("SP1", new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")), model);

        body.AddSegmentCommand.Execute(null);
        Assert.True(body.Segments[1].IsSinglePoint);
        body.Segments[1].StartCoeff = "2.45";

        var tb = new TestBench("tb");
        tb.Analyses.Add(new SParameterAnalysis("SP1", body.BuildSweeps()));
        var read = Assert.IsType<SParameterAnalysis>(Assert.Single(new CnlReader().Read(CnlWriter.Write(tb)).TestBench.Analyses));

        Assert.Equal([1e9, 2e9, 2.45e9, 3e9], read.Expand());
        var reopened = SpBodyViewModel.FromAnalysis(read, model);
        Assert.Equal([false, true], reopened.Segments.Select(s => s.IsSinglePoint));
    }

    /// <summary>GitHub discussion 42: a field commits on every keystroke, so a commit that rebuilt the rows destroyed the
    /// text box being typed in and "11" took two clicks.</summary>
    [Fact]
    public void TypingInAFrequencyField_KeepsItsRow_AndUndoStillShowsTheEarlierValue()
    {
        string path = Path.Combine(_dir, "typed.cem");
        var setup = new EmSetup { Name = "typed", LayoutRef = "a.clay", Frequency = new FrequencySpec("9", "8", 5, SweepKind.Linear, "GHz", "GHz") };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup);
        var row = vm.FrequencySegments[0];

        row.StopCoeff = "1";
        row.StopCoeff = "11";

        Assert.Same(row, vm.FrequencySegments[0]);
        Assert.Equal("11", vm.Working.Frequency.StopExpr);

        vm.UndoRedo.Undo();
        Assert.Equal("1", vm.FrequencySegments[0].StopCoeff);
        Assert.Equal("1", vm.Working.Frequency.StopExpr);
    }
}
