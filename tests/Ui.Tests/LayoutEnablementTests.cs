using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1h gates 3/4: docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md §1.4/§1.5 (R-L1h-3)
// Headless audit: for each command and a representative selection, LayoutCommandAvailability.CanExecute
// matches the acceptance table exactly, and every disabled result carries a non-empty reason — "disable,
// don't hide; say why," never a silent no-op.

public class LayoutEnablementTests
{
    private static readonly LayerKey Layer1 = new(1, 0);
    private static readonly LayerKey Layer2 = new(2, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Union / Intersect / Difference / XOR: "≥2 selected shapes share a layer" ────────────────

    [Fact]
    public void BooleanOp_DisabledOnASingleShape_WithReason()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        var avail = vm.BooleanOpAvailability;
        Assert.False(avail.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(avail.DisabledReason));
    }

    [Fact]
    public void BooleanOp_EnabledOnTwoSameLayerShapes()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 500, Y1 = 500, X2 = 1500, Y2 = 1500 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.BooleanOpAvailability.CanExecute);
    }

    [Fact]
    public void BooleanOp_DisabledAcrossDifferentLayers_EvenWithTwoShapesSelected()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = Layer2, X1 = 500, Y1 = 500, X2 = 1500, Y2 = 1500 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        var avail = vm.BooleanOpAvailability;
        Assert.False(avail.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(avail.DisabledReason));
    }

    // ── Flatten to Polygon: "≥1 selected shape has curvature" ───────────────────────────────────

    [Fact]
    public void Flatten_DisabledOnRect_EnabledOnCircle()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new CircleShape { Layer = Layer1, Cx = 5000, Cy = 5000, R = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Click(vm, 500, 500);
        Assert.False(vm.FlattenAvailability.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(vm.FlattenAvailability.DisabledReason));

        Click(vm, 5000, 5000);
        Assert.True(vm.FlattenAvailability.CanExecute);
    }

    // ── Repair Self-Intersection: "≥1 selected shape is flagged self-intersecting" ───────────────

    [Fact]
    public void Repair_DisabledOnCleanShape_WithExactReason()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        var avail = vm.RepairAvailability;
        Assert.False(avail.CanExecute);
        Assert.Equal("No self-intersecting shapes in selection", avail.DisabledReason);
    }

    // ── Offset / Scale: "≥1 selected shape" ──────────────────────────────────────────────────────

    [Fact]
    public void Offset_And_Scale_DisabledWithNoSelection_EnabledWithOne()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.False(vm.OffsetAvailability.CanExecute);
        Assert.False(vm.ScaleAvailability.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(vm.OffsetAvailability.DisabledReason));
        Assert.False(string.IsNullOrWhiteSpace(vm.ScaleAvailability.DisabledReason));

        Click(vm, 500, 500);
        Assert.True(vm.OffsetAvailability.CanExecute);
        Assert.True(vm.ScaleAvailability.CanExecute);
    }

    // ── Cut / Copy / Delete / Duplicate: "≥1 selected shape" ─────────────────────────────────────

    [Fact]
    public void CutCopyDeleteDuplicate_Availability_RequiresSelection()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.False(vm.CutCopyDeleteDuplicateAvailability.CanExecute);
        Click(vm, 500, 500);
        Assert.True(vm.CutCopyDeleteDuplicateAvailability.CanExecute);
    }

    // ── Paste / Paste in Place: "clipboard holds a valid layout fragment" ────────────────────────

    [Fact]
    public void Paste_Availability_ReflectsClipboardState()
    {
        Assert.True(LayoutEditorViewModel.PasteAvailability(clipboardHasFragment: true).CanExecute);

        var disabled = LayoutEditorViewModel.PasteAvailability(clipboardHasFragment: false);
        Assert.False(disabled.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(disabled.DisabledReason));
    }

    // ── Insert/Remove Vertex: opened on a vertex, disabled below the minimum vertex count ────────

    [Fact]
    public void DeleteVertex_EnabledAboveMinimum_DisabledAtMinimum_WithExactReason()
    {
        var model = FreshModel();
        var square = new PolygonShape { Layer = Layer1, Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000] };
        var triangle = new PolygonShape { Layer = Layer1, Xy = [0, 0, 1000, 0, 500, 1000] };
        model.Shapes.Add(square);
        model.Shapes.Add(triangle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Click(vm, 500, 500);
        Assert.True(vm.DeleteVertexAvailability(0, 0).CanExecute); // 4 vertices, well above the minimum of 3

        Click(vm, 500, 300);
        var avail = vm.DeleteVertexAvailability(1, 0);
        Assert.False(avail.CanExecute);
        Assert.Equal("A closed shape needs at least 3 vertices", avail.DisabledReason);
    }

    // ── Gate 4: no silent greying anywhere in the audited command set ────────────────────────────

    [Fact]
    public void EveryDisabledCommandInTheAuditSet_HasANonEmptyReason()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        // Nothing selected -> every selection-gated command in the audit is disabled.

        LayoutCommandAvailability[] availabilities =
        [
            vm.BooleanOpAvailability,
            vm.OffsetAvailability,
            vm.ScaleAvailability,
            vm.FlattenAvailability,
            vm.RepairAvailability,
            vm.CutCopyDeleteDuplicateAvailability,
            LayoutEditorViewModel.PasteAvailability(false),
        ];

        foreach (var a in availabilities)
        {
            Assert.False(a.CanExecute);
            Assert.False(string.IsNullOrWhiteSpace(a.DisabledReason));
        }
    }
}
