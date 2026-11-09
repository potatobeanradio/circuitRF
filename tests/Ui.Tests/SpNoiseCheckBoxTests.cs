using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>The S-parameter body's Noise check box is <c>Noise=</c>: a new analysis gets it, an edit opens on the
/// analysis's own value and keeps its spelling and every other key, and the line written says it.</summary>
public sealed class SpNoiseCheckBoxTests
{
    private static SParameterAnalysis Build(AnalysisEditorViewModel vm)
        => Assert.IsType<SParameterAnalysis>(Assert.Single(vm.BuildAnalyses()!));

    [Fact]
    public void ANewAnalysis_IsNoiselessUntilTheBoxIsTicked()
    {
        var vm = new AnalysisEditorViewModel(new SchematicEditModel(), AnalysisEditorViewModel.AnalysisKind.SP) { Name = "SP1" };
        Assert.False(vm.SpBody.Noise);
        Assert.False(Build(vm).Noise);

        vm.SpBody.Noise = true;
        var sp = Build(vm);
        Assert.Equal("yes", sp.NoiseExpr);

        var tb = new TestBench("tb");
        tb.Analyses.Add(sp);
        var read = Assert.IsType<SParameterAnalysis>(Assert.Single(new CnlReader().Read(CnlWriter.Write(tb)).TestBench.Analyses));
        Assert.True(read.Noise);
    }

    [Fact]
    public void AnEdit_OpensOnTheAnalysisValue_AndKeepsItsOtherKeys()
    {
        var m = new SchematicEditModel();
        var existing = new SParameterAnalysis("SP1", new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"))
            { NoiseExpr = "on", NdfExpr = "yes", MarginThresholdExpr = "-20" };
        m.Analyses.Add(existing);

        var vm = new AnalysisEditorViewModel(m, existing);
        Assert.True(vm.SpBody.Noise);
        var kept = Build(vm);
        Assert.Equal(("on", "yes", "-20"), (kept.NoiseExpr, kept.NdfExpr, kept.MarginThresholdExpr));

        vm.SpBody.Noise = false;
        var off = Build(vm);
        Assert.False(off.Noise);
        Assert.Equal(("yes", "-20"), (off.NdfExpr, off.MarginThresholdExpr));
    }

    /// <summary>The per-part box is NoiseContrib=: off on a new analysis, written yes when ticked, and an edit opens on
    /// the analysis's own spelling and keeps it.</summary>
    [Fact]
    public void TheNoiseContribBox_IsNoiseContrib()
    {
        var vm = new AnalysisEditorViewModel(new SchematicEditModel(), AnalysisEditorViewModel.AnalysisKind.SP) { Name = "SP1" };
        Assert.False(vm.SpBody.NoiseContrib);
        vm.SpBody.Noise = true;
        vm.SpBody.NoiseContrib = true;
        Assert.Equal("yes", Build(vm).NoiseContribExpr);

        var m = new SchematicEditModel();
        var existing = new SParameterAnalysis("SP1", new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"))
            { NoiseExpr = "yes", NoiseContribExpr = "on" };
        m.Analyses.Add(existing);
        var edit = new AnalysisEditorViewModel(m, existing);
        Assert.True(edit.SpBody.NoiseContrib);
        Assert.Equal("on", Build(edit).NoiseContribExpr);
    }
}
