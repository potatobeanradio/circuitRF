using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for analyses-list-grouping brief: PSA row labels/summary/IsSweep,
/// MoveAnalysisChainCommand correctness, and CanMove guards.
/// </summary>
public sealed class AnalysesListGroupingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchematicViewModel MakeSchVm(params Analysis[] analyses)
    {
        var model = new SchematicEditModel();
        foreach (var a in analyses) model.Analyses.Add(a);
        return new SchematicViewModel(model, messageSink: null);
    }

    private static AnalysesListViewModel BindListVm(SchematicViewModel schVm)
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        return vm;
    }

    private static ParametricSweepAnalysis MakePsa(string name, string varName, double[] values, string inner)
        => new ParametricSweepAnalysis(name, varName, values, inner);

    // ── AnalysisRowViewModel: PSA fields ──────────────────────────────────────

    [Fact]
    public void PSA_TypeLabel_IsSW()
    {
        var psa   = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1, 2 }, "DC1");
        var schVm = MakeSchVm(psa);
        var row   = new AnalysisRowViewModel(psa, schVm);

        Assert.Equal("SW", row.TypeLabel);
    }

    [Fact]
    public void PSA_Name_IsVarName_NotAnalysisName()
    {
        var psa   = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1, 2 }, "DC1");
        var schVm = MakeSchVm(psa);
        var row   = new AnalysisRowViewModel(psa, schVm);

        Assert.Equal("Vgs", row.Name);
        Assert.NotEqual("DC1_sweep_Vgs", row.Name);
    }

    [Fact]
    public void PSA_IsSweep_True()
    {
        var psa   = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var schVm = MakeSchVm(psa);
        var row   = new AnalysisRowViewModel(psa, schVm);

        Assert.True(row.IsSweep);
    }

    [Fact]
    public void DC_IsSweep_False()
    {
        var dc    = new DcAnalysis("DC1");
        var schVm = MakeSchVm(dc);
        var row   = new AnalysisRowViewModel(dc, schVm);

        Assert.False(row.IsSweep);
    }

    [Fact]
    public void PSA_Summary_41pts_0to120()
    {
        // 41 values: 0, 3, 6, ..., 120
        var values = Enumerable.Range(0, 41).Select(i => (double)i * 3).ToArray();
        var psa    = MakePsa("DC1_sweep_Vgs", "Vgs", values, "DC1");
        var schVm  = MakeSchVm(psa);
        var row    = new AnalysisRowViewModel(psa, schVm);

        Assert.StartsWith("41 pts:", row.Summary);
        Assert.Contains("0", row.Summary);
        Assert.Contains("120", row.Summary);
    }

    [Fact]
    public void PSA_Summary_EmptyValues_ShowsEmpty()
    {
        var psa   = MakePsa("DC1_sweep_Vgs", "Vgs", System.Array.Empty<double>(), "DC1");
        var schVm = MakeSchVm(psa);
        var row   = new AnalysisRowViewModel(psa, schVm);

        Assert.Equal("(empty)", row.Summary);
    }

    [Fact]
    public void PSA_Summary_SingleValue_ShowsOnePt()
    {
        var psa   = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 5.0 }, "DC1");
        var schVm = MakeSchVm(psa);
        var row   = new AnalysisRowViewModel(psa, schVm);

        Assert.StartsWith("1 pt:", row.Summary);
        Assert.Contains("5", row.Summary);
    }

    // ── MoveAnalysisChainCommand: basic move ──────────────────────────────────

    [Fact]
    public void MoveDown_Chain_MovesChainAndSweepsTogether()
    {
        // [DC1, DC1_sweep_Vgs, SP1] → MoveDown on DC1 → [SP1, DC1, DC1_sweep_Vgs]
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var model = new SchematicEditModel();
        model.Analyses.Add(dc1);
        model.Analyses.Add(sweep);
        model.Analyses.Add(sp1);

        var cmd = new MoveAnalysisChainCommand(model, dc1, moveUp: false);
        cmd.Execute();

        Assert.Equal("SP1",          model.Analyses[0].Name);
        Assert.Equal("DC1",          model.Analyses[1].Name);
        Assert.Equal("DC1_sweep_Vgs", model.Analyses[2].Name);
    }

    [Fact]
    public void MoveDown_Chain_UndoRestoresOrder()
    {
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var model = new SchematicEditModel();
        model.Analyses.Add(dc1);
        model.Analyses.Add(sweep);
        model.Analyses.Add(sp1);

        var cmd = new MoveAnalysisChainCommand(model, dc1, moveUp: false);
        cmd.Execute();
        cmd.Undo();

        Assert.Equal("DC1",           model.Analyses[0].Name);
        Assert.Equal("DC1_sweep_Vgs", model.Analyses[1].Name);
        Assert.Equal("SP1",           model.Analyses[2].Name);
    }

    [Fact]
    public void MoveUp_SP1_MovesBeforeChain()
    {
        // [DC1, DC1_sweep_Vgs, SP1] → MoveUp on SP1 → [SP1, DC1, DC1_sweep_Vgs]
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var model = new SchematicEditModel();
        model.Analyses.Add(dc1);
        model.Analyses.Add(sweep);
        model.Analyses.Add(sp1);

        var cmd = new MoveAnalysisChainCommand(model, sp1, moveUp: true);
        cmd.Execute();

        Assert.Equal("SP1",           model.Analyses[0].Name);
        Assert.Equal("DC1",           model.Analyses[1].Name);
        Assert.Equal("DC1_sweep_Vgs", model.Analyses[2].Name);
    }

    [Fact]
    public void MoveUp_OnSweepRow_MovesWholeChain()
    {
        // Selecting the sweep row of the chain should move the whole chain up
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var model = new SchematicEditModel();
        model.Analyses.Add(sp1);
        model.Analyses.Add(dc1);
        model.Analyses.Add(sweep);

        // Move the sweep row up (DC1 chain should move before SP1)
        var cmd = new MoveAnalysisChainCommand(model, sweep, moveUp: true);
        cmd.Execute();

        Assert.Equal("DC1",           model.Analyses[0].Name);
        Assert.Equal("DC1_sweep_Vgs", model.Analyses[1].Name);
        Assert.Equal("SP1",           model.Analyses[2].Name);
    }

    // ── CanMove guards (chain-aware) ──────────────────────────────────────────

    [Fact]
    public void CanMoveUp_FirstBlock_ReturnsFalse()
    {
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var schVm  = MakeSchVm(dc1, sweep, sp1);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[0]; // DC1 — first block
        Assert.False(listVm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_LastBlock_ReturnsFalse()
    {
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));

        var schVm  = MakeSchVm(dc1, sweep, sp1);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[2]; // SP1 — last block
        Assert.False(listVm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_SweepRow_UsesBlockEnd()
    {
        // Selecting the sweep row of the last chain → CanMoveDown = false
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");

        var schVm  = MakeSchVm(dc1, sweep);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[1]; // sweep row — but the whole chain is last
        Assert.False(listVm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveUp_LoneSweepRow_ReturnsFalse()
    {
        // Up on a sweep means "move inward within its chain" — a lone sweep has nowhere to go.
        var sp1   = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));
        var dc1   = new DcAnalysis("DC1");
        var sweep = MakePsa("DC1_sweep_Vgs", "Vgs", new double[] { 0, 1 }, "DC1");

        var schVm  = MakeSchVm(sp1, dc1, sweep);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[2]; // lone sweep in its chain → can't move inward
        Assert.False(listVm.MoveUpCommand.CanExecute(null));
    }
}
