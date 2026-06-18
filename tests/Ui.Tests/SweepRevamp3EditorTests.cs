using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for Stage 3 of the parametric-sweep revamp (brief-sweep-revamp-3-editor).
///
/// Critical regression guard: BuildAnalyses must write the dialog's Enabled to the base
/// and each row's Enabled to its own sweep — not the old isLast/!hasSweeps hack that
/// produced dead chains under Stage 2's collapse logic.
/// </summary>
public sealed class SweepRevamp3EditorTests
{
    // ── T1: all Enabled → base true + both sweeps true ────────────────────────

    [Fact]
    public void BuildAnalyses_TwoAxes_AllEnabled_BaseAndBothSweepsTrue()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name    = "DC1";
        vm.Enabled = true;

        var axis0 = MakeListAxis(model, "Vgs", "-3, -3.5");
        var axis1 = MakeListAxis(model, "Vds", "0, 5, 10");
        vm.SweepAxes.Add(axis0);
        vm.SweepAxes.Add(axis1);

        var chain = vm.BuildAnalyses();

        Assert.NotNull(chain);
        Assert.Equal(3, chain!.Count);

        // Base: dialog's Enabled
        Assert.True(chain[0].Enabled, "DC base must carry dialog Enabled=true");

        // Sweeps: each row's Enabled (default true)
        Assert.True(chain[1].Enabled, "sweep_Vgs row.Enabled default = true");
        Assert.True(chain[2].Enabled, "sweep_Vds row.Enabled default = true");
    }

    // ── T2: dialog Enabled=false → base false ────────────────────────────────

    [Fact]
    public void BuildAnalyses_DialogDisabled_BaseEnabledFalse()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name    = "DC1";
        vm.Enabled = false;   // dialog Enabled off

        var axis0 = MakeListAxis(model, "Vgs", "-3, -3.5");
        vm.SweepAxes.Add(axis0);

        var chain = vm.BuildAnalyses();

        Assert.NotNull(chain);
        Assert.Equal(2, chain!.Count);

        Assert.False(chain[0].Enabled, "DC base must carry dialog Enabled=false");
        Assert.True(chain[1].Enabled,  "sweep row default still true");
    }

    // ── T3: one row disabled → that sweep false, others true ─────────────────

    [Fact]
    public void BuildAnalyses_OneRowDisabled_ThatSweepFalseOtherTrue()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name    = "DC1";
        vm.Enabled = true;

        var axis0 = MakeListAxis(model, "Vgs", "-3, -3.5");
        var axis1 = MakeListAxis(model, "Vds", "0, 5, 10");
        SetField(axis0, "_enabled", false);   // disable the inner axis
        vm.SweepAxes.Add(axis0);
        vm.SweepAxes.Add(axis1);

        var chain = vm.BuildAnalyses();

        Assert.NotNull(chain);
        Assert.Equal(3, chain!.Count);

        Assert.True(chain[0].Enabled,  "DC base = dialog Enabled=true");
        Assert.False(chain[1].Enabled, "sweep_Vgs row.Enabled=false");
        Assert.True(chain[2].Enabled,  "sweep_Vds row.Enabled=true (default)");
    }

    // ── T4: no axes → single analysis, Enabled from dialog ───────────────────

    [Fact]
    public void BuildAnalyses_NoAxes_SingleAnalysis_EnabledFromDialog()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name    = "DC1";
        vm.Enabled = false;

        var chain = vm.BuildAnalyses();

        Assert.NotNull(chain);
        Assert.Single(chain!);
        Assert.False(chain[0].Enabled, "No sweeps: analysis carries dialog Enabled=false");
    }

    // ── T5: reorder — MoveSweepAxisDown swaps index 0 and 1 ──────────────────

    [Fact]
    public void MoveSweepAxisDownCommand_SwapsIndexZeroAndOne()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name = "DC1";

        var axisA = MakeListAxis(model, "VarA", "1, 2");
        var axisB = MakeListAxis(model, "VarB", "3, 4");
        var axisC = MakeListAxis(model, "VarC", "5, 6");
        vm.SweepAxes.Add(axisA);
        vm.SweepAxes.Add(axisB);
        vm.SweepAxes.Add(axisC);

        // Move axisA (index 0) down → should now be at index 1
        vm.MoveSweepAxisDownCommand.Execute(axisA);

        Assert.Equal(axisB, vm.SweepAxes[0]);
        Assert.Equal(axisA, vm.SweepAxes[1]);
        Assert.Equal(axisC, vm.SweepAxes[2]);
    }

    // ── T6: reorder — chain nesting reflects new order ────────────────────────

    [Fact]
    public void MoveSweepAxisDown_BuildAnalyses_ReflectsNewOrder()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name = "DC1";

        var axisA = MakeListAxis(model, "VarA", "1, 2");   // was innermost
        var axisB = MakeListAxis(model, "VarB", "3, 4");   // was outer
        vm.SweepAxes.Add(axisA);
        vm.SweepAxes.Add(axisB);

        // Move axisA down — VarB becomes innermost, VarA becomes outer
        vm.MoveSweepAxisDownCommand.Execute(axisA);

        var chain = vm.BuildAnalyses();
        Assert.NotNull(chain);
        Assert.Equal(3, chain!.Count);

        // chain[0] = DC1 (base)
        Assert.IsType<DcAnalysis>(chain[0]);
        Assert.Equal("DC1", chain[0].Name);

        // chain[1] = inner sweep = VarB (now first in SweepAxes)
        var sweepB = chain[1] as ParametricSweepAnalysis;
        Assert.NotNull(sweepB);
        Assert.Equal("VarB",  sweepB!.SweepVarName);
        Assert.Equal("DC1",   sweepB.InnerAnalysisName);

        // chain[2] = outer sweep = VarA
        var sweepA = chain[2] as ParametricSweepAnalysis;
        Assert.NotNull(sweepA);
        Assert.Equal("VarA",         sweepA!.SweepVarName);
        Assert.Equal("DC1_sweep_VarB", sweepA.InnerAnalysisName);
    }

    // ── T7: MoveSweepAxisUp does not go below index 0 ────────────────────────

    [Fact]
    public void MoveSweepAxisUpCommand_AtIndex0_DoesNothing()
    {
        var model = new SchematicEditModel();
        var vm    = new AnalysisEditorViewModel(model, AnalysisEditorViewModel.AnalysisKind.DC);
        vm.Name = "DC1";

        var axisA = MakeListAxis(model, "VarA", "1, 2");
        var axisB = MakeListAxis(model, "VarB", "3, 4");
        vm.SweepAxes.Add(axisA);
        vm.SweepAxes.Add(axisB);

        vm.MoveSweepAxisUpCommand.Execute(axisA);   // axisA already at index 0

        Assert.Equal(axisA, vm.SweepAxes[0]);
        Assert.Equal(axisB, vm.SweepAxes[1]);
    }

    // ── T8: edit-restore Enabled comes from base, not outermost sweep ─────────

    [Fact]
    public void EditRestore_EnabledFromBase_NotOutermostSweep()
    {
        var model = new SchematicEditModel();

        var dc = new DcAnalysis("DC1") { Enabled = false };   // base disabled
        var sw = new ParametricSweepAnalysis("DC1_sweep_Vgs", "Vgs",
            new double[] { -3, -3.5 }, "DC1") { Enabled = true };  // outer enabled

        model.Analyses.Add(dc);
        model.Analyses.Add(sw);

        // Open editor on the OUTER sweep — dialog's Enabled should come from the BASE (DC1)
        var vm = new AnalysisEditorViewModel(model, sw);

        Assert.False(vm.Enabled, "Dialog Enabled should reflect base analysis Enabled=false");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SweepAxisRowViewModel MakeListAxis(SchematicEditModel model, string varName, string listExpr)
    {
        var row = new SweepAxisRowViewModel(model);
        SetField(row, "_varName",  varName);
        SetField(row, "_mode",     SweepAxisMode.List);
        SetField(row, "_listExpr", listExpr);
        return row;
    }

    private static void SetField<T>(object obj, string fieldName, T value)
    {
        var fi = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        fi?.SetValue(obj, value);
    }
}
