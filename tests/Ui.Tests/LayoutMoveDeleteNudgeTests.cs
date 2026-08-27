using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c gates 8, 9, 10: docs/sonnet-briefs/brief-L1c-selection-and-properties.md
// R-L1c-3 — move snaps the DELTA, never the resulting vertices (gate 8 is the test that catches
// the tempting wrong implementation: independently re-snapping each moved vertex).

public class LayoutMoveDeleteNudgeTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 8: move preserves off-grid geometry (R-L1c-3) ────────────────────

    [Fact]
    public void Move_OffGridPolygon_EveryVertexMovesByExactlyTheSameSnappedDelta()
    {
        var model = FreshModel(1000);
        // Deliberately off-grid (a 45° segment / imported geometry would produce vertices like these).
        long[] original = [7, 13, 1007, 13, 1007, 1013, 7, 1013];
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = (long[])original.Clone() };
        model.Shapes.Add(poly);

        var vm = SelectVm(model);
        Click(vm, 507, 513); // inside the polygon's bbox -> selects it

        // Drag by a raw delta that snaps to (2000, 3000): 2345 -> round(2345/1000)*1000 = 2000; etc.
        vm.OnPointerPressed(507, 513, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(507 + 2345, 513 + 2987, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(507 + 2345, 513 + 2987, KeyModifiers.None);

        long[] expected = [2007, 3013, 3007, 3013, 3007, 4013, 2007, 4013];
        Assert.Equal(expected, poly.Xy);

        // Undo restores the exact original off-grid coordinates.
        vm.UndoRedo.Undo();
        Assert.Equal(original, poly.Xy);
    }

    [Fact]
    public void Move_DragFromInsideAnyMultiSelectedShape_TranslatesTheWholeSelection()
    {
        var model = FreshModel(1000);
        var a = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var b = new RectShape { Layer = new LayerKey(1, 0), X1 = 20_000, Y1 = 0, X2 = 21_000, Y2 = 1000 };
        model.Shapes.Add(a);
        model.Shapes.Add(b);
        var vm = SelectVm(model);

        // Shift-select both.
        Click(vm, 500, 500);
        Click(vm, 20_500, 500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        // A plain press+drag starting INSIDE one already-selected member (A) must NOT collapse the
        // selection to just A — it must move the whole {A, B} group together.
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        Assert.Equal(2, vm.SelectedIndices.Count); // selection preserved, not collapsed to {A}
        vm.OnPointerMoved(500 + 5000, 500, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(500 + 5000, 500, KeyModifiers.None);

        Assert.Equal(5000, a.X1);
        Assert.Equal(25_000, b.X1); // moved by the same delta as A
    }

    /// <summary>R-dup-2: the grid-snap toggle (F9) is what applies a raw delta now. Alt was the old
    /// spelling and means duplicate from this round on — see the test below.</summary>
    [Fact]
    public void Move_WithGridSnapToggledOff_DeltaAppliedRaw()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);
        vm.ToggleSnapDbuEnabled();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 33, 500 + 77, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(500 + 33, 500 + 77, KeyModifiers.None);

        Assert.Equal(33, rect.X1);
        Assert.Equal(77, rect.Y1);
    }

    /// <summary>R-dup-1: the same drag under Alt leaves the original exactly where it was and adds a
    /// copy at the delta. Pinned here, beside the move it replaces, because the two gestures differ by
    /// one held key and the failure mode is silently getting the other one.</summary>
    [Fact]
    public void Move_WithAltHeld_LeavesTheOriginalAndAddsACopy()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 3000, 500, true, KeyModifiers.Alt, 40);
        vm.OnPointerReleased(500 + 3000, 500, KeyModifiers.Alt);

        Assert.Equal(2, vm.Model.Shapes.Count);
        Assert.Equal(0, rect.X1);                                   // the original never moved
        Assert.Equal(3000, ((RectShape)vm.Model.Shapes[1]).X1);     // the copy landed at the delta
    }

    // ── Gate 9: nudge — arrow keys move by one snap step; Shift by ten ───────

    [Fact]
    public void ArrowKeys_NudgeSelectionByOneSnapStep_ShiftByTen()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);
        Click(vm, 500, 500);

        vm.OnKeyDown(Key.Right, KeyModifiers.None);
        Assert.Equal(1000, rect.X1); Assert.Equal(0, rect.Y1);

        vm.OnKeyDown(Key.Up, KeyModifiers.None);
        Assert.Equal(1000, rect.X1); Assert.Equal(1000, rect.Y1);

        vm.OnKeyDown(Key.Right, KeyModifiers.Shift);
        Assert.Equal(11_000, rect.X1); Assert.Equal(1000, rect.Y1);

        vm.OnKeyDown(Key.Left, KeyModifiers.None);
        Assert.Equal(10_000, rect.X1);

        vm.OnKeyDown(Key.Down, KeyModifiers.None);
        Assert.Equal(0, rect.Y1);
    }

    [Fact]
    public void Nudge_EachKeyPress_IsOneUndoEntry()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = SelectVm(model);
        Click(vm, 500, 500);

        vm.OnKeyDown(Key.Right, KeyModifiers.None);
        vm.OnKeyDown(Key.Right, KeyModifiers.None);

        var rect = (RectShape)model.Shapes[0];
        Assert.Equal(2000, rect.X1);
        vm.UndoRedo.Undo();
        Assert.Equal(1000, rect.X1);
        vm.UndoRedo.Undo();
        Assert.Equal(0, rect.X1);
    }

    // ── Gate 10: delete + undo restores original z-order indices exactly ─────

    [Fact]
    public void DeleteMultiSelection_Undo_RestoresByteIdenticalSerialization()
    {
        var model = FreshModel(1000);
        for (int i = 0; i < 5; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey((i % 2) + 1, 0), X1 = i * 20_000, Y1 = 0, X2 = i * 20_000 + 10_000, Y2 = 10_000 });

        var before = LayoutPersistence.Serialize(model);

        var vm = SelectVm(model);
        Click(vm, 5000, 5000);                              // select index 0
        Click(vm, 3 * 20_000 + 5000, 5000, KeyModifiers.Control); // Ctrl-add index 3 (non-contiguous)
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        Assert.Equal(3, model.Shapes.Count);

        vm.UndoRedo.Undo();

        Assert.Equal(5, model.Shapes.Count);
        var after = LayoutPersistence.Serialize(model);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Delete_ClearsSelection()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = SelectVm(model);
        Click(vm, 500, 500);
        Assert.Single(vm.SelectedIndices);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        Assert.Empty(model.Shapes);
        Assert.Empty(vm.SelectedIndices);
    }
}
