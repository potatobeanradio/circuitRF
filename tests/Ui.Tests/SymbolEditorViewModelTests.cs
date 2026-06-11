using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

// ── Layer 2 gate: SymbolEditorViewModel headless interaction tests ─────────────

public class SymbolEditorViewModelTests
{
    // Build a minimal EditableSymbol with a handful of easy-to-hit primitives.
    private static (EditableSymbol, SymbolEditorViewModel, UndoRedoStack) MakeVm()
    {
        var sym = new EditableSymbol();
        // Prim 0: a horizontal line at y=0, x ∈ [-100, 100]
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                             -100, 0, 100, 0));
        // Prim 1: a filled circle at (0, 150), R=30
        sym.Primitives.Add(new CirclePrimitive { Cx = 0, Cy = 150, R = 30, Filled = true });
        // Prim 2: another line at y=300
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                             -80, 300, 80, 300));
        var vm    = new SymbolEditorViewModel(sym);
        return (sym, vm, vm.UndoRedo);
    }

    [Fact]
    public void InitialState_NoSelection_NoOverlay()
    {
        var (_, vm, _) = MakeVm();
        Assert.Empty(vm.Overlay.SelectedIndices);
        Assert.Null(vm.Overlay.RubberBand);
        Assert.Equal((0.0, 0.0), vm.Overlay.LiveDragOffset);
        Assert.NotNull(vm.RenderSymbol);
    }

    [Fact]
    public void ClickOnPrimitive_SelectsIt()
    {
        var (_, vm, _) = MakeVm();
        // Click near the circle at (0, 150)
        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        Assert.Contains(1, vm.Overlay.SelectedIndices);
    }

    [Fact]
    public void ClickOnEmpty_ClearsSelection()
    {
        var (_, vm, _) = MakeVm();
        vm.OnPointerPressed(0, 150, KeyModifiers.None);   // select circle
        vm.OnPointerReleased(0, 150);
        vm.OnPointerPressed(0, 500, KeyModifiers.None);   // click empty
        Assert.Empty(vm.Overlay.SelectedIndices);
    }

    [Fact]
    public void ShiftClick_TogglesSelection()
    {
        var (_, vm, _) = MakeVm();
        // Select line (index 0) first
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerReleased(0, 0);
        Assert.Contains(0, vm.Overlay.SelectedIndices);

        // Shift-click circle — should add it
        vm.OnPointerPressed(0, 150, KeyModifiers.Shift);
        Assert.Contains(0, vm.Overlay.SelectedIndices);
        Assert.Contains(1, vm.Overlay.SelectedIndices);

        // Shift-click circle again — should deselect it
        vm.OnPointerReleased(0, 150);
        vm.OnPointerPressed(0, 150, KeyModifiers.Shift);
        Assert.Contains(0, vm.Overlay.SelectedIndices);
        Assert.DoesNotContain(1, vm.Overlay.SelectedIndices);
    }

    [Fact]
    public void DragRelease_MoveCommandOnStack_AndUndoReverts()
    {
        var (sym, vm, stack) = MakeVm();
        // Select the circle (prim 1) at (0, 150)
        vm.OnPointerPressed(0, 150, KeyModifiers.None);

        // Move 5 units right (snaps to p=5), 10 down
        vm.OnPointerMoved(5, 160, leftDown: true);
        Assert.Equal((5.0, 10.0), vm.Overlay.LiveDragOffset);

        // Release → command committed
        vm.OnPointerReleased(5, 160);
        Assert.Equal((0.0, 0.0), vm.Overlay.LiveDragOffset);
        Assert.True(stack.CanUndo);

        // Circle should be at (5, 160)
        var circle = (CirclePrimitive)sym.Primitives[1];
        Assert.Equal( 5.0, circle.Cx, 1e-9);
        Assert.Equal(160.0, circle.Cy, 1e-9);

        // Undo reverts
        stack.Undo();
        Assert.Equal( 0.0, circle.Cx, 1e-9);
        Assert.Equal(150.0, circle.Cy, 1e-9);
    }

    [Fact]
    public void ZeroDrag_NoCommandOnStack()
    {
        var (_, vm, stack) = MakeVm();
        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        vm.OnPointerReleased(0, 150);   // zero delta
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void DeleteKey_RemovesPrimitives_AndUndoRestores()
    {
        var (sym, vm, stack) = MakeVm();
        int beforeCount = sym.Primitives.Count;

        // Select the line (prim 0) and delete
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerReleased(0, 0);
        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        Assert.Equal(beforeCount - 1, sym.Primitives.Count);
        Assert.True(stack.CanUndo);

        // Undo restores it
        stack.Undo();
        Assert.Equal(beforeCount, sym.Primitives.Count);
    }

    [Fact]
    public void RubberBand_SelectsContainedPrimitives()
    {
        var (_, vm, _) = MakeVm();
        // Drag a rubber-band that encloses the line at y=0 and the circle at y=150,
        // but not the third line at y=300.
        vm.OnPointerPressed(-200, -50, KeyModifiers.None);
        vm.OnPointerMoved(200, 200, leftDown: true);
        vm.OnPointerReleased(200, 200);

        Assert.Contains(0, vm.Overlay.SelectedIndices);  // line at y=0
        Assert.Contains(1, vm.Overlay.SelectedIndices);  // circle at y=150
        Assert.DoesNotContain(2, vm.Overlay.SelectedIndices); // line at y=300
    }

    [Fact]
    public void Escape_ClearsSelection()
    {
        var (_, vm, _) = MakeVm();
        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        vm.OnPointerReleased(0, 150);
        Assert.NotEmpty(vm.Overlay.SelectedIndices);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);
        Assert.Empty(vm.Overlay.SelectedIndices);
    }

    [Fact]
    public void MoveCommand_NotifyChanged_FiresBothDirections()
    {
        var (sym, vm, stack) = MakeVm();
        int notifyCount = 0;
        sym.Changed += (_, _) => notifyCount++;

        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        vm.OnPointerMoved(10, 160, leftDown: true);
        vm.OnPointerReleased(10, 160);
        int afterExecute = notifyCount;
        stack.Undo();
        int afterUndo = notifyCount;

        Assert.True(afterExecute >= 1, "Execute should fire NotifyChanged");
        Assert.True(afterUndo > afterExecute, "Undo should fire NotifyChanged");
    }

    [Fact]
    public void DeleteCommand_NotifyChanged_FiresBothDirections()
    {
        var (sym, vm, stack) = MakeVm();
        int notifyCount = 0;
        sym.Changed += (_, _) => notifyCount++;

        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        vm.OnPointerReleased(0, 150);
        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        int afterDelete = notifyCount;
        stack.Undo();
        int afterUndo = notifyCount;

        Assert.True(afterDelete >= 1);
        Assert.True(afterUndo > afterDelete);
    }

    [Fact]
    public void SnapToP_IsCorrect()
    {
        // Verify snap by checking the live offset during a drag of 3 units (should snap to 5).
        var (_, vm, _) = MakeVm();
        vm.OnPointerPressed(0, 150, KeyModifiers.None);
        vm.OnPointerMoved(3, 150, leftDown: true);
        Assert.Equal(5.0, vm.Overlay.LiveDragOffset.Dx, 1e-9);  // snapped up to 5
    }
}

// ── Layer 1 gate: PlaceSymbolPrimitiveCommand — append + undo-remove, both notify ──

public class PlaceSymbolPrimitiveCommandTests
{
    [Fact]
    public void Execute_AppendsToEnd_AndNotifies()
    {
        var sym = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, 0, 0, 10, 0));
        var prim = new CirclePrimitive { Cx = 5, Cy = 5, R = 10 };
        int notifyCount = 0;
        sym.Changed += (_, _) => notifyCount++;

        new PlaceSymbolPrimitiveCommand(sym, prim).Execute();

        Assert.Equal(2, sym.Primitives.Count);
        Assert.Same(prim, sym.Primitives[1]);  // appended to end — topmost Z
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public void Undo_RemovesPrimitive_AndNotifies()
    {
        var sym = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, 0, 0, 10, 0));
        var prim = new CirclePrimitive { Cx = 5, Cy = 5, R = 10 };
        int notifyCount = 0;
        sym.Changed += (_, _) => notifyCount++;

        var cmd = new PlaceSymbolPrimitiveCommand(sym, prim);
        cmd.Execute();
        cmd.Undo();

        Assert.Single(sym.Primitives);
        Assert.DoesNotContain(prim, sym.Primitives);
        Assert.Equal(2, notifyCount);  // Execute fires once, Undo fires once
    }

    [Fact]
    public void UndoRedoStack_PlaceIsUndoable_AndRedoable()
    {
        var sym   = new EditableSymbol();
        var stack = new UndoRedoStack();
        var prim  = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -50, 0, 50, 0);

        stack.Execute(new PlaceSymbolPrimitiveCommand(sym, prim));
        Assert.Single(sym.Primitives);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);

        stack.Undo();
        Assert.Empty(sym.Primitives);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Single(sym.Primitives);
    }

    [Fact]
    public void CurrentStyleProperties_HaveCorrectDefaults()
    {
        var sym = new EditableSymbol();
        var vm  = new SymbolEditorViewModel(sym);

        Assert.Equal(SymbolColorRole.SymbolLine,   vm.CurrentColorRole);
        Assert.Equal(SymbolStrokeTier.Normal,       vm.CurrentStrokeTier);
        Assert.Equal(12.0,                          vm.CurrentFontSize, 1e-9);
        Assert.Equal(SymbolFontStyle.Regular,       vm.CurrentFontStyle);
    }

    [Fact]
    public void ToolEnum_ContainsAllDrawingTools()
    {
        var tools = Enum.GetValues<SymbolEditorViewModel.Tool>();
        Assert.Contains(SymbolEditorViewModel.Tool.Select,      tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Line,        tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Polyline,    tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Rect,        tools);
        Assert.Contains(SymbolEditorViewModel.Tool.RoundedRect, tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Circle,      tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Ellipse,     tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Arc,         tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Triangle,    tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Polygon,     tools);
        Assert.Contains(SymbolEditorViewModel.Tool.QuadCurve,   tools);
        Assert.Contains(SymbolEditorViewModel.Tool.CubicCurve,  tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Sine,        tools);
        Assert.Contains(SymbolEditorViewModel.Tool.ExpTaper,   tools);
        Assert.Contains(SymbolEditorViewModel.Tool.Text,        tools);
    }
}

// ── Layer 2 gate: placement gesture tests ────────────────────────────────────

public class SymbolEditorDrawGestureTests
{
    private static SymbolEditorViewModel MakeVm(out EditableSymbol sym)
    {
        sym = new EditableSymbol();
        return new SymbolEditorViewModel(sym);
    }

    // ── Two-point drag ────────────────────────────────────────────────────────

    [Fact]
    public void LineTool_DragAndRelease_PlacesLine_Undoable()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Line;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(50, 30, leftDown: true);
        vm.OnPointerReleased(50, 30);

        Assert.Single(sym.Primitives);
        var line = Assert.IsType<LinePrimitive>(sym.Primitives[0]);
        Assert.Equal(0,  line.X1, 1e-9);
        Assert.Equal(0,  line.Y1, 1e-9);
        Assert.Equal(50, line.X2, 1e-9);
        Assert.Equal(30, line.Y2, 1e-9);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Empty(sym.Primitives);
    }

    [Fact]
    public void RectTool_DragAndRelease_PlacesRect()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(-50, -30, KeyModifiers.None);
        vm.OnPointerReleased(50, 30);

        var r = Assert.IsType<RectPrimitive>(sym.Primitives[0]);
        Assert.Equal(0,  r.Cx, 1e-9);
        Assert.Equal(0,  r.Cy, 1e-9);
        Assert.Equal(100, r.W, 1e-9);
        Assert.Equal(60,  r.H, 1e-9);
    }

    [Fact]
    public void CircleTool_DragAndRelease_PlacesCircle()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Circle;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerReleased(30, 40);  // dist = 50

        var c = Assert.IsType<CirclePrimitive>(sym.Primitives[0]);
        Assert.Equal(0,  c.Cx, 1e-9);
        Assert.Equal(0,  c.Cy, 1e-9);
        Assert.Equal(50, c.R, 1e-9);
    }

    [Fact]
    public void ZeroDrag_TwoPoint_NoCommandPlaced()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Line;

        vm.OnPointerPressed(10, 10, KeyModifiers.None);
        vm.OnPointerReleased(10, 10);  // same point — degenerate

        Assert.Empty(sym.Primitives);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void EscapeDuringTwoPointDraw_Cancels_NoPrimitive()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(40, 40, leftDown: true);
        Assert.NotNull(vm.Overlay.InProgressPrimitive);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Null(vm.Overlay.InProgressPrimitive);
        Assert.Empty(sym.Primitives);
    }

    [Fact]
    public void InProgressPrimitive_SetDuringDrag_ClearedAfterCommit()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Line;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(20, 20, leftDown: true);
        Assert.NotNull(vm.Overlay.InProgressPrimitive);

        vm.OnPointerReleased(20, 20);
        Assert.Null(vm.Overlay.InProgressPrimitive);
        Assert.Single(sym.Primitives);
    }

    [Fact]
    public void SineTool_DragAndRelease_PlacesSine()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Sine;

        vm.OnPointerPressed(-50, 0, KeyModifiers.None);
        vm.OnPointerReleased(50, 20);  // horizontal drag dominant

        var s = Assert.IsType<SinePrimitive>(sym.Primitives[0]);
        Assert.Equal(SineAxis.Horizontal, s.Axis);
        Assert.Equal(100, s.Length, 1e-9);
        Assert.Equal(1,   s.Cycles, 1e-9);
    }

    // ── Multi-point click ─────────────────────────────────────────────────────

    [Fact]
    public void PolylineTool_EnterFinishes_PlacesPolyline()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Polyline;

        vm.OnPointerPressed(0,  0,   KeyModifiers.None);
        vm.OnPointerReleased(0, 0);
        vm.OnPointerPressed(50, 0,   KeyModifiers.None);
        vm.OnPointerReleased(50, 0);
        vm.OnPointerPressed(50, 50,  KeyModifiers.None);
        vm.OnPointerReleased(50, 50);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var pl = Assert.IsType<PolylinePrimitive>(sym.Primitives[0]);
        Assert.Equal(3, pl.Points.Count);
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void PolylineTool_EscapeBeforeMinPoints_Cancels()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Polyline;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerReleased(0, 0);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);  // only 1 point — below min of 2

        Assert.Empty(sym.Primitives);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void TriangleTool_ThirdClick_AutoCompletes()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Triangle;

        vm.OnPointerPressed(0,   0,   KeyModifiers.None); vm.OnPointerReleased(0, 0);
        vm.OnPointerPressed(100, 0,   KeyModifiers.None); vm.OnPointerReleased(100, 0);
        vm.OnPointerPressed(50,  80,  KeyModifiers.None); vm.OnPointerReleased(50, 80);

        // Triangle auto-completes at 3 points — no Enter needed.
        var pg = Assert.IsType<PolygonPrimitive>(sym.Primitives[0]);
        Assert.Equal(3, pg.Points.Count);
    }

    [Fact]
    public void QuadCurveTool_ThirdClick_AutoCompletes()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.QuadCurve;

        vm.OnPointerPressed(0,   0,   KeyModifiers.None); vm.OnPointerReleased(0, 0);
        vm.OnPointerPressed(50, -30,  KeyModifiers.None); vm.OnPointerReleased(50, -30);
        vm.OnPointerPressed(100, 0,   KeyModifiers.None); vm.OnPointerReleased(100, 0);

        var qc = Assert.IsType<QuadCurvePrimitive>(sym.Primitives[0]);
        Assert.Equal(0,   qc.P0X,   1e-9);
        Assert.Equal(50,  qc.CtrlX, 1e-9);
        Assert.Equal(100, qc.P2X,   1e-9);
    }

    [Fact]
    public void CubicCurveTool_FourthClick_AutoCompletes()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.CubicCurve;

        vm.OnPointerPressed(0,   0,  KeyModifiers.None); vm.OnPointerReleased(0, 0);
        vm.OnPointerPressed(30, -40, KeyModifiers.None); vm.OnPointerReleased(30, -40);
        vm.OnPointerPressed(70, -40, KeyModifiers.None); vm.OnPointerReleased(70, -40);
        vm.OnPointerPressed(100, 0,  KeyModifiers.None); vm.OnPointerReleased(100, 0);

        var cc = Assert.IsType<CubicCurvePrimitive>(sym.Primitives[0]);
        Assert.Equal(0,   cc.P0X, 1e-9);
        Assert.Equal(30,  cc.C1X, 1e-9);
        Assert.Equal(70,  cc.C2X, 1e-9);
        Assert.Equal(100, cc.P3X, 1e-9);
    }

    [Fact]
    public void DoubleClick_FinishesPolyline()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Polyline;

        vm.OnPointerPressed(0,  0,  KeyModifiers.None, clickCount: 1); vm.OnPointerReleased(0, 0);
        vm.OnPointerPressed(50, 0,  KeyModifiers.None, clickCount: 1); vm.OnPointerReleased(50, 0);
        vm.OnPointerPressed(50, 50, KeyModifiers.None, clickCount: 2); // double-click finishes

        Assert.Single(sym.Primitives);
        var pl = Assert.IsType<PolylinePrimitive>(sym.Primitives[0]);
        Assert.Equal(2, pl.Points.Count);  // only the 2 single-clicked points
    }

    // ── Text tool ─────────────────────────────────────────────────────────────

    [Fact]
    public void TextTool_ClickThenEnter_PlacesTextPrimitive()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Text;

        vm.OnPointerPressed(10, 20, KeyModifiers.None);
        vm.OnPointerReleased(10, 20);
        vm.OnTextInput("Hello");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var tp = Assert.IsType<TextPrimitive>(sym.Primitives[0]);
        Assert.Equal("Hello", tp.Content);
        Assert.Equal(10, tp.AnchorX, 1e-9);
        Assert.Equal(20, tp.AnchorY, 1e-9);
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void TextTool_Backspace_RemovesLastChar()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Text;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnTextInput("Hi!");
        vm.OnKeyDown(Key.Back, KeyModifiers.None);  // removes '!'
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var tp = Assert.IsType<TextPrimitive>(sym.Primitives[0]);
        Assert.Equal("Hi", tp.Content);
    }

    [Fact]
    public void TextTool_EscapeBeforeEnter_Cancels()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Text;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnTextInput("discard me");
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(sym.Primitives);
    }

    [Fact]
    public void TextTool_InProgressPreview_ShowsCursorWhileTyping()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Text;

        vm.OnPointerPressed(5, 5, KeyModifiers.None);
        vm.OnTextInput("AB");

        // Preview should be active with the typed content + cursor marker.
        var preview = Assert.IsType<TextPrimitive>(vm.Overlay.InProgressPrimitive);
        Assert.Contains("AB", preview.Content);
    }

    // ── Style properties propagate to placed primitives ───────────────────────

    [Fact]
    public void CurrentStrokeTier_AppliestoNewPrimitive()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool      = SymbolEditorViewModel.Tool.Line;
        vm.CurrentStrokeTier = SymbolStrokeTier.Thin;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerReleased(50, 50);

        Assert.Equal(SymbolStrokeTier.Thin, ((LinePrimitive)sym.Primitives[0]).StrokeTier);
    }

    [Fact]
    public void CurrentFontSize_AppliestoTextPrimitive()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool    = SymbolEditorViewModel.Tool.Text;
        vm.CurrentFontSize = 18.0;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnTextInput("X");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        Assert.Equal(18.0, ((TextPrimitive)sym.Primitives[0]).FontSize, 1e-9);
    }

    // ── Snap ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoPointDraw_Snaps_ToP()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Line;

        // Press at non-grid coords — both endpoints should snap to nearest p=5.
        vm.OnPointerPressed(2, 3, KeyModifiers.None);   // snaps to (0, 5)
        vm.OnPointerReleased(48, 53);                   // snaps to (50, 55)

        var l = Assert.IsType<LinePrimitive>(sym.Primitives[0]);
        Assert.Equal(0,  l.X1, 1e-9);
        Assert.Equal(5,  l.Y1, 1e-9);
        Assert.Equal(50, l.X2, 1e-9);
        Assert.Equal(55, l.Y2, 1e-9);
    }

    // ── Tool switch cancels in-progress draw ──────────────────────────────────

    [Fact]
    public void SwitchingTool_CancelsInProgressDraw()
    {
        var vm = MakeVm(out var sym);
        vm.ActiveTool = SymbolEditorViewModel.Tool.Line;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(30, 30, leftDown: true);
        Assert.NotNull(vm.Overlay.InProgressPrimitive);

        vm.ActiveTool = SymbolEditorViewModel.Tool.Select;  // switch cancels

        Assert.Null(vm.Overlay.InProgressPrimitive);
        Assert.Empty(sym.Primitives);
    }
}
