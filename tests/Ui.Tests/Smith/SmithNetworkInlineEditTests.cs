using System.Linq;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The network pane's inline value editor: double-clicking a label on the drawing edits what the label
/// says. <b>One test per claim</b>; the canvas's part (the hit-test and the anchor) is the schematic
/// editor's and the Designer's, so what is asserted here is the view model's resolve and commit.
/// </summary>
public sealed class SmithNetworkInlineEditTests
{
    private static SmithChartViewModel LineVm()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm = 50.0;
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 12.0, -8.5));
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.Tline, Placement = SmithPlacement.Series, Name = "TL1",
            Values = { Z0Ohm = 50.0, ElectricalLengthDeg = 90.0, ReferenceFrequencyHz = 2.0e9 },
        });
        return new SmithChartViewModel(d);
    }

    /// <summary>A double-click on the line's parameter label named <paramref name="param"/>.</summary>
    private static SchematicHitTest.HitResult ParamHit(SmithChartViewModel vm, string param)
    {
        string id  = vm.Network.ElementIndexByComponentId.Single(kv => kv.Value == 0).Key;
        var comp   = vm.Network.Edit.FindComponent(id)!;
        int sub    = comp.Parameters.FindIndex(p => p.Name == param);
        return new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentParam, id, sub);
    }

    private static int DrainUndo(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        return n;
    }

    /// <summary>
    /// <b>A line's F is editable, and is one undo entry.</b> It had no door at all: the strip's rows
    /// are the draggable parameters and a reference frequency is not one.
    /// </summary>
    [Fact]
    public void LineReferenceFrequency_IsEditableFromItsLabel()
    {
        var vm = LineVm();

        var target = vm.ResolveInlineEdit(ParamHit(vm, "F"));
        Assert.NotNull(target);
        Assert.Equal(SmithInlineEditField.ReferenceFrequency, target!.Field);
        Assert.Equal("2 GHz", target.SeedText);

        Assert.True(vm.CommitInlineEdit(target, "3.5"));      // a bare number reads in the label's unit
        var line = vm.Design.Elements.Single();
        Assert.Equal(3.5e9, line.Values.ReferenceFrequencyHz, 6);
        Assert.Equal(90.0, line.Values.ElectricalLengthDeg);
        Assert.Equal(1, DrainUndo(vm));
    }

    /// <summary>
    /// <b>A slider-backed value typed past its slider's range widens the range and lands unclamped</b>
    /// — it goes through the row, so the slider's own coercion cannot clamp it and write it back.
    /// </summary>
    [Fact]
    public void LineZ0_TypedPastTheSliderRange_LandsUnclamped()
    {
        var vm = LineVm();

        var target = vm.ResolveInlineEdit(ParamHit(vm, "Z"))!;
        Assert.Equal(SmithParameter.Z0, target.Parameter);

        Assert.True(vm.CommitInlineEdit(target, "800 Ω"));
        var line = vm.Design.Elements.Single();
        Assert.Equal(800.0, line.Values.Z0Ohm, 9);
        Assert.True(line.SliderRange[SmithParameter.Z0].Max >= 800.0);
    }

    /// <summary>
    /// <b>Re-committing the seed and committing unreadable text write nothing</b>; the second says why.
    /// The generator is not an element and opens nothing.
    /// </summary>
    [Fact]
    public void UnchangedOrUnreadable_WritesNothing()
    {
        var vm = LineVm();

        var target = vm.ResolveInlineEdit(ParamHit(vm, "E"))!;
        Assert.False(vm.CommitInlineEdit(target, target.SeedText));
        Assert.False(vm.CommitInlineEdit(target, "ninety"));
        Assert.Contains("ninety", vm.StripNotice);
        Assert.False(vm.UndoRedo.CanUndo);

        string genId  = vm.Network.Edit.Components
                          .Single(c => c.InstanceName == SmithNetworkModel.GeneratorName).Id;
        var generator = new SchematicHitTest.HitResult(SchematicHitTest.HitKind.Component, genId);
        Assert.Null(vm.ResolveInlineEdit(generator));
    }
}
