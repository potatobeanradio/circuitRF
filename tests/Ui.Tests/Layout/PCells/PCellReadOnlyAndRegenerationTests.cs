using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gates 13-14: read-only (R-pc-13) + regeneration (R-pc-9's invalidation trigger).</summary>
public class PCellReadOnlyAndRegenerationTests
{
    private static readonly Technology Pcb = StarterTechnologies.Pcb2Layer();

    private static LayoutView MakePCellView()
    {
        var parameters = new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 };
        var result = MlinPCell.Generate(parameters, Pcb, PCellLayerSelection.Default);
        var view = new LayoutView();
        view.Shapes.AddRange(result.Shapes);
        view.PCellOrigin = new PCellOrigin(MlinPCell.GeneratorId, parameters);
        return view;
    }

    [Fact]
    public void PCellBackedView_IsReadOnly()
    {
        var vm = new LayoutEditorViewModel(MakePCellView());
        Assert.True(vm.IsPCellReadOnly);
    }

    [Fact]
    public void OrdinaryView_IsNotReadOnly()
    {
        var vm = new LayoutEditorViewModel(new LayoutView());
        Assert.False(vm.IsPCellReadOnly);
    }

    [Fact]
    public void Execute_OnPCellBackedView_IsRefused_ModelUnchanged()
    {
        var view = MakePCellView();
        var vm = new LayoutEditorViewModel(view);
        int shapeCountBefore = view.Shapes.Count;

        var newShape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        vm.Execute(new AddShapeCommand(view, newShape));

        Assert.Equal(shapeCountBefore, view.Shapes.Count); // refused, not silently applied
        Assert.False(vm.UndoRedo.CanUndo); // nothing was pushed onto the undo stack either
    }

    [Fact]
    public void DetachFromPCell_ClearsOrigin_MakesViewEditableAgain()
    {
        var view = MakePCellView();
        var vm = new LayoutEditorViewModel(view);
        int shapeCountBefore = view.Shapes.Count;

        vm.DetachFromPCell();

        Assert.False(vm.IsPCellReadOnly);
        Assert.Null(view.PCellOrigin);
        Assert.Equal(shapeCountBefore, view.Shapes.Count); // geometry itself is untouched by detaching

        // Now ordinary edits succeed.
        var newShape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        vm.Execute(new AddShapeCommand(view, newShape));
        Assert.Equal(shapeCountBefore + 1, view.Shapes.Count);
    }

    [Fact]
    public void Regenerate_WithChangedW_ProducesDifferentGeometry()
    {
        var view = MakePCellView();
        var vm = new LayoutEditorViewModel(view) { Technology = Pcb };
        var originalPin2X = ((RectShape)view.Shapes[0]).X2;

        bool ok = vm.RegeneratePCell(new Dictionary<string, double> { ["L"] = 0.02 }); // double the length

        Assert.True(ok);
        Assert.NotNull(view.PCellOrigin);
        Assert.Equal(0.02, view.PCellOrigin!.Parameters["L"]);
        var newPin2X = ((RectShape)view.Shapes[0]).X2;
        Assert.NotEqual(originalPin2X, newPin2X);
        Assert.Equal(20_000_000, newPin2X); // 20mm at 1nm resolution
    }

    [Fact]
    public void Regenerate_PreservesUnchangedParameters()
    {
        var view = MakePCellView();
        var vm = new LayoutEditorViewModel(view) { Technology = Pcb };

        vm.RegeneratePCell(new Dictionary<string, double> { ["L"] = 0.02 }); // W not supplied — must be preserved

        Assert.Equal(0.0029, view.PCellOrigin!.Parameters["W"]);
    }

    [Fact]
    public void Regenerate_OnNonPCellView_Fails_ReportsWhy()
    {
        var vm = new LayoutEditorViewModel(new LayoutView());
        bool ok = vm.RegeneratePCell(new Dictionary<string, double> { ["L"] = 0.02 });
        Assert.False(ok);
    }
}
