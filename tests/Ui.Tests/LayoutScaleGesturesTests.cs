using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1h gates 8/9: docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md R-L1h-4/R-L1h-5
// Mouse-driven bbox scale handles, driven through OnPointerPressed/Moved/Released exactly as the
// canvas would (mirrors LayoutHandleGesturesTests.cs's convention) — corner vs. side drag, Alt-anchors-
// centre, typed override mid-drag, Escape-pushes-nothing, and the three handle-mode rows.

public class LayoutScaleGesturesTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    /// <summary>Two rects whose combined bbox is exactly (0,0)-(3000,1000) — corners at (0,0)/(3000,0)/
    /// (3000,1000)/(0,1000); side midpoints at (1500,0)/(3000,500)/(1500,1000)/(0,500).</summary>
    private static (LayoutView Model, RectShape A, RectShape B) TwoRectSelection()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var b = new RectShape { Layer = Layer1, X1 = 2000, Y1 = 0, X2 = 3000, Y2 = 1000 };
        model.Shapes.Add(a); model.Shapes.Add(b);
        return (model, a, b);
    }

    // ── Gate 8: corner drag scales uniformly ──────────────────────────────────────────────────────

    [Fact]
    public void CornerDrag_ScalesUniformly_AnchoredAtOppositeCorner()
    {
        var (model, a, b) = TwoRectSelection();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        // Top-right corner (3000,1000); opposite anchor is (0,0). Dragging to exactly 2x the original
        // handle vector from the anchor produces an exact uniform factor of 2, regardless of direction.
        vm.OnPointerPressed(3000, 1000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(6000, 2000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(6000, 2000, KeyModifiers.None);

        Assert.True(vm.UndoRedo.CanUndo);
        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        var scaledB = Assert.IsType<RectShape>(model.Shapes[1]);
        Assert.Equal(0, scaledA.X1); Assert.Equal(0, scaledA.Y1);
        Assert.Equal(2000, scaledA.X2); Assert.Equal(2000, scaledA.Y2);
        Assert.Equal(4000, scaledB.X1); Assert.Equal(6000, scaledB.X2); // (2000,3000) shifted by anchor(0)*2
        Assert.NotSame(a, scaledA); // originals replaced, restorable via undo
        Assert.NotSame(b, scaledB);
    }

    // ── Gate 8: side drag scales only that axis ───────────────────────────────────────────────────

    [Fact]
    public void SideDrag_ScalesOnlyThatAxis()
    {
        var (model, _, _) = TwoRectSelection();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        // Right-side handle (3000,500); opposite anchor is the left side (0,500). Dragging to
        // (4500,500) is exactly 1.5x the original X distance from the anchor — Y must stay untouched.
        vm.OnPointerPressed(3000, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(4500, 500, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(4500, 500, KeyModifiers.None);

        Assert.True(vm.UndoRedo.CanUndo);
        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(0, scaledA.X1);
        Assert.Equal(1500, scaledA.X2);   // X scaled by 1.5
        Assert.Equal(0, scaledA.Y1);
        Assert.Equal(1000, scaledA.Y2);   // Y UNCHANGED — factor 1.0
    }

    // ── Gate 8: Alt anchors the selection centre instead of the opposite corner/side ──────────────

    [Fact]
    public void AltHeld_AnchorsAtSelectionCenter_NotTheOppositeCorner()
    {
        var (model, _, _) = TwoRectSelection(); // bbox (0,0)-(3000,1000), center (1500,500)
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        // Corner (3000,1000), Alt held -> anchor = center (1500,500), original handle vector from
        // anchor = (1500,500). Dragging to exactly 2x that vector from the anchor = (1500+3000,500+1000)
        // = (4500,1500) gives an exact factor of 2 about the CENTER, not the opposite corner.
        vm.OnPointerPressed(3000, 1000, KeyModifiers.Alt, 1, 40);
        vm.OnPointerMoved(4500, 1500, leftDown: true, KeyModifiers.Alt, 40);
        vm.OnPointerReleased(4500, 1500, KeyModifiers.Alt);

        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        // About center (1500,500) at factor 2: X1' = 1500 + (0-1500)*2 = -1500; X2' = 1500 + (1000-1500)*2 = 500.
        Assert.Equal(-1500, scaledA.X1);
        Assert.Equal(500, scaledA.X2);
    }

    // ── Gate 8: typed override mid-drag commits exactly ───────────────────────────────────────────

    [Fact]
    public void TypedOverrideMidDrag_CommitsAtExactlyThatFactor()
    {
        var (model, _, _) = TwoRectSelection();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.OnPointerPressed(3000, 1000, KeyModifiers.None, 1, 40); // top-right corner, anchor (0,0)
        vm.OnPointerMoved(3500, 1200, leftDown: true, KeyModifiers.None, 40); // an arbitrary partial drag

        vm.CommitTypedScale("3"); // exact factor, ignoring wherever the pointer currently sits

        Assert.True(vm.UndoRedo.CanUndo);
        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(3000, scaledA.X2); // 1000 * 3 exactly
        Assert.Equal(3000, scaledA.Y2);

        // The drag state is fully resolved — a subsequent release does nothing further.
        vm.OnPointerReleased(3500, 1200, KeyModifiers.None);
        Assert.Equal(2, model.Shapes.Count);
    }

    [Fact]
    public void TypedOverrideMidDrag_SizeText_CommitsAtThatResultingSize()
    {
        var (model, _, _) = TwoRectSelection(); // bbox width 3000 DBU = 3 um at 1000 dbu/um
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.OnPointerPressed(3000, 1000, KeyModifiers.None, 1, 40); // corner drag, anchor (0,0)
        vm.OnPointerMoved(3200, 1050, leftDown: true, KeyModifiers.None, 40);

        vm.CommitTypedScale("6um"); // resulting width should be exactly 6 um = 2x the original 3 um

        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(2000, scaledA.X2); // 1000 * 2 exactly
    }

    // ── Gate 8: Escape mid-drag pushes nothing ────────────────────────────────────────────────────

    [Fact]
    public void Escape_MidScaleDrag_PushesNoCommand_ModelUntouched()
    {
        var (model, a, b) = TwoRectSelection();
        var jsonBefore = LayoutPersistence.Serialize(model);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.OnPointerPressed(3000, 1000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(6000, 2000, leftDown: true, KeyModifiers.None, 40);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Same(a, model.Shapes[0]);
        Assert.Same(b, model.Shapes[1]);
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    // ── Gate 8 (the standing screen->world rule): drive a corner drag through actual screen-pixel
    // coordinates converted via LayoutViewport, exactly as LayoutCanvas would — not world coordinates
    // handed to the VM directly (see src/Ui/CLAUDE.md's L1-fix note on why this class of test exists). ──

    [Fact]
    public void CornerDrag_DrivenFromScreenPixelCoordinates_ScalesCorrectly()
    {
        var (model, _, _) = TwoRectSelection(); // bbox (0,0)-(3000,1000)
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        const double viewportW = 1200, viewportH = 800;
        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 3000, 1000), viewportW, viewportH);

        // Press exactly on the top-right corner handle (3000,1000), converted to screen pixels.
        double pressSx = vp.WorldToScreenX(3000), pressSy = vp.WorldToScreenY(1000);
        var (pressWx, pressWy) = (vp.ScreenToWorldX(pressSx), vp.ScreenToWorldY(pressSy));
        vm.OnPointerPressed(pressWx, pressWy, KeyModifiers.None, 1, 40);

        // Drag to the screen position of world point (6000,2000) — exactly 2x the handle vector from
        // the (0,0) anchor — via the SAME screen->world conversion the canvas uses on every move.
        double moveSx = vp.WorldToScreenX(6000), moveSy = vp.WorldToScreenY(2000);
        var (moveWx, moveWy) = (vp.ScreenToWorldX(moveSx), vp.ScreenToWorldY(moveSy));
        vm.OnPointerMoved(moveWx, moveWy, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(moveWx, moveWy, KeyModifiers.None);

        var scaledA = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(2000, scaledA.X2);
        Assert.Equal(2000, scaledA.Y2);
    }

    // ── Gate 9: handle modes ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneShapeSelected_DefaultsToL1dHandles_NotScaleHandles()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        Assert.False(vm.ShowScaleHandles);
    }

    [Fact]
    public void TwoOrMoreShapesSelected_AlwaysShowsScaleHandles()
    {
        var (model, _, _) = TwoRectSelection();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.ShowScaleHandles);
    }

    [Fact]
    public void SingleShape_ScaleModeToggle_TemporarilyReplacesL1dHandles_EscapeRestoresThem()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Assert.False(vm.ShowScaleHandles);

        vm.ToggleScaleModeCommand.Execute(null);
        Assert.True(vm.ScaleModeActive);
        Assert.True(vm.ShowScaleHandles);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.ScaleModeActive);
        Assert.False(vm.ShowScaleHandles);
        Assert.Single(vm.SelectedIndices); // Escape from Scale mode does NOT clear the selection
    }

    [Fact]
    public void SingleShape_ScaleMode_CanActuallyBeDraggedLikeAMultiSelection()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        vm.ToggleScaleModeCommand.Execute(null);

        // Corner handle at (1000,1000); anchor (0,0); drag to exactly 2x -> factor 2.
        vm.OnPointerPressed(1000, 1000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(2000, 2000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(2000, 2000, KeyModifiers.None);

        var scaled = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(2000, scaled.X2);
        Assert.Equal(2000, scaled.Y2);
    }
}
