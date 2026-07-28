using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Owner follow-up after Phase L3a: "are instance array properties supposed to show up in the
//  Properties Inspector? I don't see them." — wires LayoutShapePropertiesViewModel's instance context
//  (CellRef+Re-target, X/Y, Rotation, Mirror, Magnification, Rows/Cols/PitchX/PitchY) into the SAME
//  panel the shape sections already use, gated by IsInstanceContext / IsSingleInstanceSelected.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstancePropertiesPanelTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(LayoutView model)
    {
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    private static void ClickInstance(LayoutEditorViewModel vm, LayoutInstance inst, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(inst.X, inst.Y, mods);
        vm.OnPointerReleased(inst.X, inst.Y, mods);
    }

    [Fact]
    public void SingleInstanceSelected_PopulatesInstanceContext_NotShapeContext()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../SomeCell", X = 1000, Y = 2000, Mag = 2.0, Rows = 3, Cols = 4, PitchX = 5000, PitchY = 6000 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);

        ClickInstance(vm, inst);

        Assert.False(props.IsEmptyState);
        Assert.True(props.IsInstanceContext);
        Assert.True(props.IsSingleInstanceSelected);
        Assert.False(props.ShowRectSize);
        Assert.False(props.ShowLabel);

        Assert.Equal("../SomeCell", props.InstanceCellRefText);
        Assert.Equal(LayoutUnits.Format(1000, model.DisplayUnit, model.DbuPerMicron), props.InstanceXText);
    }

    [Fact]
    public void SingleInstanceSelected_XYRotationMirrorArrayFields_MatchModel()
    {
        var model = FreshModel();
        var inst = new LayoutInstance
        {
            CellRef = "../Leaf", X = 10_000, Y = 20_000, Rot = LayoutRotation.R90, MirrorX = true,
            Mag = 1.5, Rows = 2, Cols = 3, PitchX = 30_000, PitchY = 40_000,
        };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);

        ClickInstance(vm, inst);

        Assert.Equal(LayoutUnits.Format(10_000, model.DisplayUnit, model.DbuPerMicron), props.InstanceXText);
        Assert.Equal(LayoutUnits.Format(20_000, model.DisplayUnit, model.DbuPerMicron), props.InstanceYText);
        Assert.Equal(LayoutRotation.R90, props.InstanceRotationValue);
        Assert.True(props.InstanceMirrorXValue);
        Assert.Equal("1.5", props.InstanceMagText);
        Assert.Equal("2", props.InstanceRowsText);
        Assert.Equal("3", props.InstanceColsText);
        Assert.Equal(LayoutUnits.Format(30_000, model.DisplayUnit, model.DbuPerMicron), props.InstancePitchXText);
        Assert.Equal(LayoutUnits.Format(40_000, model.DisplayUnit, model.DbuPerMicron), props.InstancePitchYText);
        Assert.True(props.HasInstanceArrayCount);
        Assert.Equal("2 × 3 = 6 placements", props.InstanceArrayCountText);
    }

    [Fact]
    public void PlainInstance_NoArray_ArrayCountTextIsBlank()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);

        ClickInstance(vm, inst);

        Assert.False(props.HasInstanceArrayCount);
        Assert.Equal("", props.InstanceArrayCountText);
    }

    [Fact]
    public void CommitInstanceXAndYText_TranslatesOnlyThatAxis_OneUndoEntryEach()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 1000, Y = 2000, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceXText("5 mm");
        Assert.Equal(5_000_000, model.Instances[0].X);
        Assert.Equal(2000, model.Instances[0].Y);

        props.CommitInstanceYText("7 mm");
        Assert.Equal(5_000_000, model.Instances[0].X);
        Assert.Equal(7_000_000, model.Instances[0].Y);

        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void CommitInstanceXText_InvalidText_SetsError_LeavesModelUnchanged()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 1000, Y = 2000, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceXText("garbage");

        Assert.True(props.HasInstanceXError);
        Assert.Equal(1000, model.Instances[0].X);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void CommitField_DispatchesToInstanceXY_ViaTagKey()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 1000, Y = 2000, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitField("InstanceX", "3 mm");
        Assert.Equal(3_000_000, model.Instances[0].X);

        props.CommitField("InstanceY", "4 mm");
        Assert.Equal(4_000_000, model.Instances[0].Y);
    }

    [Fact]
    public void RevertField_InstanceX_ClearsErrorAndRestoresCanonicalText()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 1000, Y = 2000, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceXText("garbage");
        Assert.True(props.HasInstanceXError);

        props.RevertField("InstanceX");
        Assert.False(props.HasInstanceXError);
        Assert.Equal(LayoutUnits.Format(1000, model.DisplayUnit, model.DbuPerMicron), props.InstanceXText);
    }

    [Fact]
    public void InstanceRotationValue_Change_CommitsRotation()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.InstanceRotationValue = LayoutRotation.R270;

        Assert.Equal(LayoutRotation.R270, model.Instances[0].Rot);
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void InstanceMirrorXValue_Change_CommitsMirror()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.InstanceMirrorXValue = true;

        Assert.True(model.Instances[0].MirrorX);
    }

    [Fact]
    public void CommitInstanceMagText_InvalidOrNonPositive_SetsError_LeavesModelUnchanged()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceMagText("not a number");
        Assert.True(props.HasInstanceMagError);
        Assert.Equal(1.0, model.Instances[0].Mag);

        props.CommitInstanceMagText("2.0");
        Assert.False(props.HasInstanceMagError);
        Assert.Equal(2.0, model.Instances[0].Mag);
    }

    [Fact]
    public void CommitInstanceRowsColsAndPitch_UpdatesArray_UpdatesArrayCountText()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceRowsText("4");
        props.CommitInstanceColsText("5");
        props.CommitInstancePitchXText("1 mm");
        props.CommitInstancePitchYText("2 mm");

        var after = model.Instances[0];
        Assert.Equal(4, after.Rows);
        Assert.Equal(5, after.Cols);
        Assert.Equal(1_000_000, after.PitchX);
        Assert.Equal(2_000_000, after.PitchY);
        Assert.Equal("4 × 5 = 20 placements", props.InstanceArrayCountText);
    }

    [Fact]
    public void CommitInstanceRowsText_NonPositive_SetsError_LeavesModelUnchanged()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0, Rows = 2 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceRowsText("0");

        Assert.True(props.HasInstanceRowsError);
        Assert.Equal(2, model.Instances[0].Rows);
    }

    [Fact]
    public void CommitInstanceCellRefText_RetargetsInstance_PreservesGeometry()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 1000, Y = 2000, Mag = 1.0 };
        model.Instances.Add(inst);
        var (vm, props) = Setup(model);
        ClickInstance(vm, inst);

        props.CommitInstanceCellRefText("../Other");

        Assert.Equal("../Other", model.Instances[0].CellRef);
        Assert.Equal(1000, model.Instances[0].X);
        Assert.Equal(2000, model.Instances[0].Y);
    }

    [Fact]
    public void MultiInstanceSelection_ShowsSummaryOnly_NoEditableInstanceFields()
    {
        var model = FreshModel();
        var a = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        var b = new LayoutInstance { CellRef = "../Leaf", X = 200_000, Y = 0, Mag = 1.0 };
        model.Instances.Add(a);
        model.Instances.Add(b);
        var (vm, props) = Setup(model);

        ClickInstance(vm, a);
        ClickInstance(vm, b, KeyModifiers.Control);
        Assert.Equal(2, vm.SelectedInstanceIndices.Count);

        Assert.True(props.IsInstanceContext);
        Assert.False(props.IsSingleInstanceSelected);
        Assert.Equal("", props.InstanceCellRefText);
        Assert.Equal("", props.InstanceXText);
        Assert.Null(props.InstanceRotationValue);
    }

    [Fact]
    public void SelectingAShapeAfterAnInstance_SwitchesBackToShapeContext()
    {
        var model = FreshModel();
        var inst = new LayoutInstance { CellRef = "../Leaf", X = 0, Y = 0, Mag = 1.0 };
        model.Instances.Add(inst);
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 500_000, Y1 = 500_000, X2 = 510_000, Y2 = 510_000 });
        var (vm, props) = Setup(model);

        ClickInstance(vm, inst);
        Assert.True(props.IsInstanceContext);

        vm.OnPointerPressed(505_000, 505_000, KeyModifiers.None);
        vm.OnPointerReleased(505_000, 505_000, KeyModifiers.None);

        Assert.False(props.IsInstanceContext);
        Assert.True(props.ShowRectSize);
    }

    [Fact]
    public void NoSelectionAtAll_EmptyMessage_MentionsShapeOrInstance()
    {
        var model = FreshModel();
        var (_, props) = Setup(model);

        Assert.True(props.IsEmptyState);
        Assert.False(props.IsInstanceContext);
        Assert.Equal("Select a shape or instance to inspect.", props.EmptyMessage);
    }
}
