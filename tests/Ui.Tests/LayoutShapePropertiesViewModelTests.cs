using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c gate 11: docs/sonnet-briefs/brief-L1c-selection-and-properties.md §7

public class LayoutShapePropertiesViewModelTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(LayoutView model)
    {
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, 40);
        vm.OnPointerReleased(wx, wy, mods);
    }

    [Fact]
    public void MultiSelectionNetEdit_AppliesToAll_AsOneUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 20_000, Y1 = 0, X2 = 21_000, Y2 = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 500, 500);
        Click(vm, 20_500, 500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        props.CommitNetText("RFin");
        Assert.Equal("RFin", ((RectShape)model.Shapes[0]).Net);
        Assert.Equal("RFin", ((RectShape)model.Shapes[1]).Net);

        vm.UndoRedo.Undo(); // ONE undo entry reverts both shapes together
        Assert.Null(((RectShape)model.Shapes[0]).Net);
        Assert.Null(((RectShape)model.Shapes[1]).Net);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void MultiSelection_DifferingCornerRadius_ShowsBlank()
    {
        var model = FreshModel();
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 20_000, Y1 = 0, X2 = 30_000, Y2 = 10_000, CornerRadius = 2000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Click(vm, 25_000, 5000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        Assert.True(props.ShowRoundedRect);
        Assert.Equal("", props.CornerRadiusText); // values differ -> blank
        Assert.NotNull(props.SelectedLayerItem);  // both on the same layer -> shown, not blank

        // Committing a shared radius applies to both as one undo entry.
        props.CommitCornerRadiusText("3000nm"); // 1 DBU = 1 nm at the default resolution
        Assert.Equal(3000, ((RoundedRectShape)model.Shapes[0]).CornerRadius);
        Assert.Equal(3000, ((RoundedRectShape)model.Shapes[1]).CornerRadius);
        vm.UndoRedo.Undo();
        Assert.Equal(1000, ((RoundedRectShape)model.Shapes[0]).CornerRadius);
        Assert.Equal(2000, ((RoundedRectShape)model.Shapes[1]).CornerRadius);
    }

    [Fact]
    public void DimensionField_AcceptsMillimeters_RevertsCleanlyOnGarbage()
    {
        var model = FreshModel();
        var rr = new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 };
        model.Shapes.Add(rr);
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Assert.True(props.ShowRoundedRect);

        props.CommitCornerRadiusText("2.9mm");
        long expected = LayoutUnits.ToDbu(2.9m, LayoutUnit.Mm, model.DbuPerMicron);
        Assert.Equal(expected, rr.CornerRadius);

        string canonical = props.CornerRadiusText;
        props.CommitCornerRadiusText("garbage");
        Assert.Equal(expected, rr.CornerRadius);      // unchanged
        Assert.Equal(canonical, props.CornerRadiusText); // reverted to canonical display
    }

    [Fact]
    public void FlattenTolerance_BlankMeansInherit()
    {
        var model = FreshModel();
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 1000, 0, 1000, 1000],
            FlattenTolDbu = 500,
        };
        model.Shapes.Add(curve);
        var (vm, props) = Setup(model);

        Click(vm, 500, 300); // inside the triangle's bbox
        Assert.True(props.ShowFlattenTol);
        Assert.NotEqual("", props.FlattenTolText);

        props.CommitFlattenTolText("");
        Assert.Null(curve.FlattenTolDbu);
    }

    [Fact]
    public void NoSelection_IsEmptyState()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var (_, props) = Setup(model);

        Assert.True(props.IsEmptyState);
    }
}
