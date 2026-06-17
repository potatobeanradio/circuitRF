// ================================================================
//  InlineEditorFixesTests.cs
//  Gate tests for brief-inline-editor-fixes
//
//  T1  — ParseUnit_NoSpaceOhm_Remaps
//  T2  — ParseUnit_NoSpaceNH_Remaps
//  T3  — ParseUnit_Spaced_Unchanged
//  T4  — ParseUnit_BarePrefix_NotSplit
//  T5  — ParseUnit_PlainNumber_NoUnit
//  T6  — VarNameEdit_Commit_RenamesAndSetsValue
//  T7  — ParamSelection_ValueOnly_WhenUnitPresent
//  T8  — ParamSelection_SelectAll_WhenNoUnit_And_VarNameMode
// ================================================================

using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class InlineEditorFixesTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableParameter MakeParam(string expr, string unit, string name = "R")
        => new() { Name = name, Expression = expr, Unit = unit };

    private static SchematicHitTest.HitResult ComponentParamHit(string compId, int subIndex)
        => new(SchematicHitTest.HitKind.ComponentParam, compId, subIndex);

    // ── T1 — no-space Ω remap ─────────────────────────────────────────────────

    [Fact]
    public void ParseUnit_NoSpaceOhm_Remaps()
    {
        var p = MakeParam("1", "Ω");
        var (expr, unit) = SchematicViewModel.ParseExpressionUnit("1Ω", p);
        Assert.Equal("1", expr);
        Assert.Equal("Ω", unit);
    }

    // ── T2 — no-space nH remap ───────────────────────────────────────────────

    [Fact]
    public void ParseUnit_NoSpaceNH_Remaps()
    {
        var p = MakeParam("2.5", "nH");
        var (expr, unit) = SchematicViewModel.ParseExpressionUnit("2.5nH", p);
        Assert.Equal("2.5", expr);
        Assert.Equal("nH", unit);
    }

    // ── T3 — already-spaced unit unchanged ───────────────────────────────────

    [Fact]
    public void ParseUnit_Spaced_Unchanged()
    {
        var p = MakeParam("1", "Ω");
        var (expr, unit) = SchematicViewModel.ParseExpressionUnit("1 Ω", p);
        Assert.Equal("1", expr);
        Assert.Equal("Ω", unit);
    }

    // ── T4 — bare SI prefix "n" is not split as a unit ───────────────────────

    [Fact]
    public void ParseUnit_BarePrefix_NotSplit()
    {
        var p = MakeParam("100", "nH");
        var (expr, unit) = SchematicViewModel.ParseExpressionUnit("100n", p);
        // "n" is a bare SI prefix — must NOT be treated as the unit
        Assert.Equal("100n", expr);
        Assert.Equal("", unit);
    }

    // ── T5 — plain number gets no unit ───────────────────────────────────────

    [Fact]
    public void ParseUnit_PlainNumber_NoUnit()
    {
        var p = MakeParam("50", "");
        var (expr, unit) = SchematicViewModel.ParseExpressionUnit("50", p);
        Assert.Equal("50", expr);
        Assert.Equal("", unit);
    }

    // ── T6 — VAR param name rename + value commit + undo ─────────────────────

    [Fact]
    public void VarNameEdit_Commit_RenamesAndSetsValue()
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "VAR1",
            Symbol       = SymbolKind.Var,
            X = 0, Y = 0,
        };
        var param = new EditableParameter { Name = "a", Expression = "1", Unit = "" };
        comp.Parameters.Add(param);
        edit.Components.Add(comp);

        var vm  = new SchematicViewModel(edit);
        var hit = ComponentParamHit(comp.Id, 0);
        vm.BeginInlineEditForHit(hit, 0, 0);

        Assert.True(vm.InlineEditIncludesName);
        Assert.Equal(-1, vm.InlineEditSelLength);   // select-all for VAR

        // Simulate user typing "freq = 2.4 GHz"
        vm.InlineEditValue = "freq = 2.4 GHz";
        vm.CommitInlineEdit();

        Assert.Equal("freq", param.Name);
        Assert.Equal("2.4",  param.Expression);
        Assert.Equal("GHz",  param.Unit);

        // One undo restores all three fields
        vm.UndoRedo.Undo();
        Assert.Equal("a", param.Name);
        Assert.Equal("1", param.Expression);
        Assert.Equal("",  param.Unit);
    }

    // ── T7 — unit-bearing param → value-only selection ───────────────────────

    [Fact]
    public void ParamSelection_ValueOnly_WhenUnitPresent()
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X = 0, Y = 0,
        };
        // Resistor with unit "Ω": expression "47", unit "Ω"
        comp.Parameters.Add(new EditableParameter { Name = "R", Expression = "47", Unit = "Ω", ShowOnSchematic = true });
        edit.Components.Add(comp);

        var vm  = new SchematicViewModel(edit);
        var hit = ComponentParamHit(comp.Id, 0);
        vm.BeginInlineEditForHit(hit, 0, 0);

        Assert.False(vm.InlineEditIncludesName);
        Assert.Equal(0, vm.InlineEditSelStart);
        // selLength == expression length (value only, not the trailing unit)
        Assert.Equal("47".Length, vm.InlineEditSelLength);
    }

    // ── T8 — no-unit param and VAR name-mode both select-all ─────────────────

    [Theory]
    [InlineData(SymbolKind.Resistor, false)]   // unit-less param → select all
    [InlineData(SymbolKind.Var,      true)]    // VAR name-mode → select all
    public void ParamSelection_SelectAll_WhenNoUnit_And_VarNameMode(SymbolKind kind, bool expectsName)
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = kind,
            X = 0, Y = 0,
        };
        comp.Parameters.Add(new EditableParameter { Name = "a", Expression = "1", Unit = "" });
        edit.Components.Add(comp);

        var vm  = new SchematicViewModel(edit);
        var hit = ComponentParamHit(comp.Id, 0);
        vm.BeginInlineEditForHit(hit, 0, 0);

        Assert.Equal(expectsName, vm.InlineEditIncludesName);
        Assert.Equal(-1, vm.InlineEditSelLength);   // select-all in both cases
    }
}
