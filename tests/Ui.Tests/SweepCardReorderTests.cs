using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-sweep-card-reorder:
/// - ReorderSweepInChainCommand swaps adjacent sweeps + relinks InnerAnalysisName
/// - Edge no-ops at chain boundaries
/// - CanMoveUp/Down guards for sweep vs base selection
/// </summary>
public sealed class SweepCardReorderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ParametricSweepAnalysis MakeSweep(string name, string var, string inner)
        => new ParametricSweepAnalysis(name, var, [1.0, 2.0], inner);

    private static SchematicEditModel MakeModel(params Analysis[] analyses)
    {
        var model = new SchematicEditModel();
        foreach (var a in analyses) model.Analyses.Add(a);
        return model;
    }

    private static SchematicViewModel MakeSchVm(params Analysis[] analyses)
        => new SchematicViewModel(MakeModel(analyses), messageSink: null);

    private static AnalysesListViewModel BindListVm(SchematicViewModel schVm)
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        return vm;
    }

    // ── ReorderSweepInChainCommand ────────────────────────────────────────────

    [Fact]
    public void Reorder_MoveInner_SwapsAndRelinks()
    {
        // Chain: [DC1, DC1_sweep_Vds(inner=DC1), DC1_sweep_Vgs(inner=DC1_sweep_Vds)]
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var model    = MakeModel(dc1, sweepVds, sweepVgs);

        // Move Vgs inward (toward the base) → Vgs becomes innermost, Vds becomes outer
        var cmd = new ReorderSweepInChainCommand(model, sweepVgs, moveInner: true);
        cmd.Execute();

        var list = model.Analyses;
        Assert.Equal(3, list.Count);
        Assert.Same(dc1, list[0]);

        var newInner = Assert.IsType<ParametricSweepAnalysis>(list[1]);
        var newOuter = Assert.IsType<ParametricSweepAnalysis>(list[2]);

        Assert.Equal("DC1_sweep_Vgs", newInner.Name);
        Assert.Equal("DC1",            newInner.InnerAnalysisName);

        Assert.Equal("DC1_sweep_Vds", newOuter.Name);
        Assert.Equal("DC1_sweep_Vgs", newOuter.InnerAnalysisName);
    }

    [Fact]
    public void Reorder_MoveInner_Undo_RestoresOriginalInstances()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var model    = MakeModel(dc1, sweepVds, sweepVgs);

        var cmd = new ReorderSweepInChainCommand(model, sweepVgs, moveInner: true);
        cmd.Execute();
        cmd.Undo();

        var list = model.Analyses;
        Assert.Same(dc1,      list[0]);
        Assert.Same(sweepVds, list[1]);
        Assert.Same(sweepVgs, list[2]);
        Assert.Equal("DC1",          ((ParametricSweepAnalysis)list[1]).InnerAnalysisName);
        Assert.Equal("DC1_sweep_Vds", ((ParametricSweepAnalysis)list[2]).InnerAnalysisName);
    }

    [Fact]
    public void Reorder_MoveOuter_SwapsAndRelinks()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var model    = MakeModel(dc1, sweepVds, sweepVgs);

        // Move Vds outward → Vgs becomes innermost, Vds becomes outer
        var cmd = new ReorderSweepInChainCommand(model, sweepVds, moveInner: false);
        cmd.Execute();

        var newInner = Assert.IsType<ParametricSweepAnalysis>(model.Analyses[1]);
        var newOuter = Assert.IsType<ParametricSweepAnalysis>(model.Analyses[2]);

        Assert.Equal("DC1_sweep_Vgs", newInner.Name);
        Assert.Equal("DC1",            newInner.InnerAnalysisName);
        Assert.Equal("DC1_sweep_Vds", newOuter.Name);
        Assert.Equal("DC1_sweep_Vgs", newOuter.InnerAnalysisName);
    }

    [Fact]
    public void Reorder_MoveInner_AtInnermostSlot_NoOp()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var model    = MakeModel(dc1, sweepVds, sweepVgs);

        // Vds is already the innermost — moving inward is a no-op
        var cmd = new ReorderSweepInChainCommand(model, sweepVds, moveInner: true);
        cmd.Execute();

        Assert.Same(sweepVds, model.Analyses[1]);
        Assert.Same(sweepVgs, model.Analyses[2]);
    }

    [Fact]
    public void Reorder_MoveOuter_AtOutermostSlot_NoOp()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var model    = MakeModel(dc1, sweepVds, sweepVgs);

        // Vgs is already the outermost — moving outward is a no-op
        var cmd = new ReorderSweepInChainCommand(model, sweepVgs, moveInner: false);
        cmd.Execute();

        Assert.Same(sweepVds, model.Analyses[1]);
        Assert.Same(sweepVgs, model.Analyses[2]);
    }

    // ── CanMoveUp / CanMoveDown guards ───────────────────────────────────────

    [Fact]
    public void CanMoveUp_InnermostSweep_ReturnsFalse()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var schVm    = MakeSchVm(dc1, sweepVds, sweepVgs);
        var listVm   = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[1]; // DC1_sweep_Vds (innermost)
        Assert.False(listVm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveUp_OutermostSweep_ReturnsTrue()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var schVm    = MakeSchVm(dc1, sweepVds, sweepVgs);
        var listVm   = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[2]; // DC1_sweep_Vgs (outermost)
        Assert.True(listVm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_OutermostSweep_ReturnsFalse()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var schVm    = MakeSchVm(dc1, sweepVds, sweepVgs);
        var listVm   = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[2]; // DC1_sweep_Vgs (outermost)
        Assert.False(listVm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_InnermostSweep_ReturnsTrue()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sweepVgs = MakeSweep("DC1_sweep_Vgs", "Vgs", "DC1_sweep_Vds");
        var schVm    = MakeSchVm(dc1, sweepVds, sweepVgs);
        var listVm   = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[1]; // DC1_sweep_Vds (innermost)
        Assert.True(listVm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveUp_BaseAtTop_ReturnsFalse()
    {
        var dc1   = new DcAnalysis("DC1");
        var schVm = MakeSchVm(dc1);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[0];
        Assert.False(listVm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_BaseAtBottom_ReturnsFalse()
    {
        var dc1   = new DcAnalysis("DC1");
        var schVm = MakeSchVm(dc1);
        var listVm = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[0];
        Assert.False(listVm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void CanMoveDown_BaseWithSweepsNotAtBottom_ReturnsTrue()
    {
        var dc1      = new DcAnalysis("DC1");
        var sweepVds = MakeSweep("DC1_sweep_Vds", "Vds", "DC1");
        var sp1      = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", 101));
        var schVm    = MakeSchVm(dc1, sweepVds, sp1);
        var listVm   = BindListVm(schVm);

        listVm.SelectedRow = listVm.Rows[0]; // DC1 (base with one sweep, SP1 below)
        Assert.True(listVm.MoveDownCommand.CanExecute(null));
    }
}
