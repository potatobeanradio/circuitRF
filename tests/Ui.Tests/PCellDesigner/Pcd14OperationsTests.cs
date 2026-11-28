// Owner: PCD-14 (brief-pcd-14 R-pcd14-2 to -5). Headless, through the view model, on PCD-13's engine.

using CircuitRF.Design.PCellDesigner.Operations;
using CircuitRF.Ui.PCellDesigner;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd14OperationsTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd14-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>C1 with a 100 µm MSHAPE SH1 sitting inside TL3, 3 mm back from its far end.</summary>
    private PCellDesignerViewModel WithPad() => Pcd6Fixture.Open(_scratch, edit: m =>
    {
        var pad = new EditableComponent
        {
            Symbol = SymbolKind.MShape, InstanceName = "SH1",
            Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1, Dx = "-3", Unit = "mm" },
        };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
            pad.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
        m.Components.Add(pad);
    }).Vm;

    /// <summary>R-pcd14-4: Make tool turns the selected MSHAPE into a cutter, one undo step.</summary>
    [Fact]
    public void MakeTool_TurnsTheSelectedShapeIntoACutter()
    {
        var vm = WithPad();
        Assert.False(vm.MakeTool());   // nothing selected
        vm.Selection.Add("SH1");
        Assert.True(vm.MakeTool(), vm.Status);
        var shape = vm.Schematic.Components.Single(c => c.InstanceName == "SH1");
        Assert.True(PCellOperations.IsTool(shape));
        Assert.Equal("\"open\"", shape.Parameters.Single(p => p.Name == DrawnMetal.Model).Expression);
        vm.Undo();
        Assert.False(PCellOperations.IsTool(vm.Schematic.Components.Single(c => c.InstanceName == "SH1")));
    }

    /// <summary>R-pcd14-2 and -5: Subtract (the first selected minus the rest) is added to the history and planned; the
    /// response line says the cell has no circuit model, naming the operation.</summary>
    [Fact]
    public async Task Subtract_IsAddedAndPlanned_AndTheResponseSaysTheCellIsEmOnly()
    {
        var vm = WithPad();
        vm.Selection.Add("SH1");
        Assert.True(vm.MakeTool(), vm.Status);
        vm.Selection.Clear();
        Assert.False(vm.AddOperation(PCellOperationKind.Subtract));   // one operand is not enough
        vm.Selection.Add("TL3");
        vm.Selection.Add("SH1");
        Assert.True(vm.AddOperation(PCellOperationKind.Subtract), vm.Status);

        var op = Assert.Single(PCellOperations.Of(vm.Schematic));
        Assert.Equal((PCellOperationKind.Subtract, "TL3", "SH1"), (op.Kind, op.A.Single(), op.B!.Single()));
        Assert.Single(vm.Operations);

        await vm.DesignAsync(0);
        Assert.False(vm.Operations[0].Refused, vm.Operations[0].Refusal);
        Assert.Null(vm.Response);
        Assert.Contains($"no circuit model: {op.Id} (Subtract)", vm.Status);
        Assert.Contains("Run EM", vm.Status);
    }

    /// <summary>Keep model on a row accepts the circuit model as an approximation: the step no longer makes the cell
    /// EM-only, and the response is drawn again. One undo step.</summary>
    [Fact]
    public async Task KeepModel_OnARow_GivesTheCellItsCircuitModelBack()
    {
        var vm = WithPad();
        vm.Selection.Add("SH1");
        Assert.True(vm.MakeTool(), vm.Status);
        vm.Selection.Clear();
        vm.Selection.Add("TL3");
        vm.Selection.Add("SH1");
        Assert.True(vm.AddOperation(PCellOperationKind.Subtract), vm.Status);
        Assert.NotEmpty(PCellOperations.WithoutCircuitModel(vm.Schematic, vm.Technology));

        vm.Operations[0].KeepModel = true;   // the check box
        Assert.True(PCellOperations.Of(vm.Schematic)[0].KeepModel);
        Assert.Empty(PCellOperations.WithoutCircuitModel(vm.Schematic, vm.Technology));
        await vm.DesignAsync(0);
        Assert.NotNull(vm.Response);
        vm.Undo();
        Assert.False(PCellOperations.Of(vm.Schematic)[0].KeepModel);
    }

    /// <summary>R-pcd14-3: the history edited in place: an edited amount, a move, a suppress and a delete, each one undo
    /// step.</summary>
    [Fact]
    public async Task TheHistory_EditsMovesSuppressesAndDeletes()
    {
        var vm = WithPad();
        vm.Selection.Add("TL1");
        vm.GrowAmount = "20 um";
        Assert.True(vm.AddOperation(PCellOperationKind.Grow), vm.Status);
        vm.Selection.Clear();
        vm.Selection.Add("TL2");
        (vm.RepeatColumns, vm.RepeatPitchX) = ("3", "2 mm");
        Assert.True(vm.AddOperation(PCellOperationKind.Repeat), vm.Status);
        await vm.DesignAsync(0);
        Assert.Equal([PCellOperationKind.Grow, PCellOperationKind.Repeat], PCellOperations.Of(vm.Schematic).Select(o => o.Kind));

        var grow = vm.Operations[0];
        grow.Amount = "30 um";
        Assert.True(vm.EditOperation(grow.Id, grow.Edited()), vm.Status);
        Assert.Equal("30 um", PCellOperations.Of(vm.Schematic)[0].Amount);

        Assert.True(vm.MoveOperation(vm.Operations[1].Id, 0), vm.Status);
        Assert.Equal(PCellOperationKind.Repeat, PCellOperations.Of(vm.Schematic)[0].Kind);

        vm.Operations[1].Suppressed = true;   // the check box
        Assert.True(PCellOperations.Of(vm.Schematic)[1].Suppressed);

        Assert.True(vm.DeleteOperation(vm.Operations[0].Id), vm.Status);
        Assert.Equal([PCellOperationKind.Grow], PCellOperations.Of(vm.Schematic).Select(o => o.Kind));

        vm.Undo();
        Assert.Equal(2, PCellOperations.Of(vm.Schematic).Count);
    }
}
