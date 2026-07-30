using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  brief-L3a-followups.md §2/R-fix-2/R-fix-3 — a selection may now contain both shapes AND instances
//  at once (L3a's original "mutually exclusive" rule is gone). Gates covered here:
//  4/5 (marquee selects instances, live preview parity, arrays as one unit),
//  6 (move/nudge/delete/cut/copy/paste/duplicate apply to both kinds as ONE undo entry; booleans,
//     flatten, repair, and vertex handles are disabled with a reason naming the instance count).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutMixedSelectionTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutMixedSelectionTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfMixedSelTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private void CreateLeafCell()
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, "Leaf");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
    }

    /// <summary>Fixture: a document at Root/layout/root.clay (real cell folder, so instances of the
    /// sibling "Leaf" cell resolve for real) with two shapes and a lone instance already placed,
    /// far enough apart that marquee rects targeting one never accidentally touch another.
    ///   Shape 0: (0,0)-(2000,2000)
    ///   Shape 1 (far, negative control): (200000,200000)-(202000,202000)
    ///   Instance 2 (lone, resolved "Leaf"): X=10000,Y=10000 -> bbox (10000,10000)-(11000,11000)
    /// </summary>
    private (LayoutEditorViewModel Vm, LayoutView Model) MakeFixture()
    {
        CreateLeafCell();
        var rootDir = CellFolder.CreateCellFolder(_workspaceDir, "Root");
        string clayPath = Path.Combine(CellFolder.SubFolderPath(rootDir, ViewType.Layout), "root.clay");
        var model = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 2000, Y2 = 2000 });
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 200_000, Y1 = 200_000, X2 = 202_000, Y2 = 202_000 });
        model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 10_000, Y = 10_000, Mag = 1.0 });
        var vm = new LayoutEditorViewModel(model, clayPath) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        return (vm, model);
    }

    private static void ClickAt(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    private static void Marquee(LayoutEditorViewModel vm, double x1, double y1, double x2, double y2, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(x1, y1, mods, 1, 40);
        vm.OnPointerMoved(x2, y2, true, mods, 40);
        vm.OnPointerReleased(x2, y2, mods);
    }

    // ── Gate 4: marquee selects instances ───────────────────────────────────────────────────────

    [Fact]
    public void Marquee_OverLoneInstance_SelectsIt()
    {
        var (vm, _) = MakeFixture();

        Marquee(vm, 9000, 9000, 12_000, 12_000); // encloses the instance's bbox, nothing else

        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void Marquee_OverArray_TouchingOnlyOneInteriorCell_SelectsTheWholeArrayAsOneUnit()
    {
        var (vm, model) = MakeFixture();
        // Replace the lone instance with a 3x3 array; the marquee below only geometrically touches
        // the (row=2,col=2) cell, nowhere near the array's own (X,Y) origin cell (row=0,col=0) — if
        // the marquee used a per-cell bbox this would miss the array entirely; using the OVERALL
        // (array-expanded) bbox (R-L3a-4, "arrays are one object") it must still select it.
        var arrayInst = new LayoutInstance { CellRef = "../../Leaf", X = 10_000, Y = 10_000, Mag = 1.0, Rows = 3, Cols = 3, PitchX = 5000, PitchY = 5000 };
        model.Instances[0] = arrayInst;
        var (farCellX, farCellY) = LayoutInstanceTransform.ArrayCellOrigin(arrayInst, 2, 2); // (20000, 20000)

        // Right-to-left/top-to-bottom drag -> CROSSING mode (intersects, does not require full
        // enclosure) — the array's OVERALL bbox (10000,10000)-(21000,21000) is far bigger than this
        // small rect around just the far cell, so enclose mode would never select it; crossing only
        // needs to touch it, which is exactly the "any placement" gate this test is checking.
        Marquee(vm, farCellX + 1200, farCellY + 1200, farCellX - 200, farCellY - 200);

        Assert.Equal([0], vm.SelectedInstanceIndices);
    }

    [Fact]
    public void Marquee_OverMixedRegion_SelectsBothShapesAndInstances()
    {
        var (vm, _) = MakeFixture();

        // Encloses shape 0 (0,0)-(2000,2000) AND instance 2's bbox (10000,10000)-(11000,11000);
        // shape 1 (far away) stays outside.
        Marquee(vm, -1000, -1000, 12_000, 12_000);

        Assert.Equal([0], vm.SelectedIndices);
        Assert.Equal([0], vm.SelectedInstanceIndices);
    }

    // ── Gate 5: live preview parity for instances ───────────────────────────────────────────────

    [Fact]
    public void Marquee_LivePreview_InstanceHighlightsBeforeRelease_AndUnHighlightsUnderCtrl()
    {
        var (vm, _) = MakeFixture();

        vm.OnPointerPressed(9000, 9000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(12_000, 12_000, true, KeyModifiers.None, 40); // now encloses the instance
        Assert.Contains(0, vm.Overlay.SelectedInstanceIndices);          // highlighted BEFORE release
        Assert.Empty(vm.SelectedInstanceIndices);                        // not yet committed
        vm.OnPointerReleased(12_000, 12_000, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        // Ctrl-drag the SAME rect again over the now-selected instance: it must visibly un-highlight
        // live (toggle), exactly like L1i's shape behavior.
        vm.OnPointerPressed(9000, 9000, KeyModifiers.Control, 1, 40);
        vm.OnPointerMoved(12_000, 12_000, true, KeyModifiers.Control, 40);
        Assert.DoesNotContain(0, vm.Overlay.SelectedInstanceIndices);
        vm.OnPointerReleased(12_000, 12_000, KeyModifiers.Control);
        Assert.Empty(vm.SelectedInstanceIndices);
    }

    [Fact]
    public void Marquee_PreviewAtRelease_EqualsCommittedSelection_ForMixedRegion()
    {
        var (vm, _) = MakeFixture();

        vm.OnPointerPressed(-1000, -1000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(12_000, 12_000, true, KeyModifiers.None, 40);

        var previewShapes = vm.Overlay.SelectedIndices.ToList();
        var previewInstances = vm.Overlay.SelectedInstanceIndices.ToList();
        previewShapes.Sort(); previewInstances.Sort();

        vm.OnPointerReleased(12_000, 12_000, KeyModifiers.None);

        var committedShapes = vm.SelectedIndices.ToList();
        var committedInstances = vm.SelectedInstanceIndices.ToList();
        committedShapes.Sort(); committedInstances.Sort();

        Assert.Equal(previewShapes, committedShapes);
        Assert.Equal(previewInstances, committedInstances);
    }

    // ── Click modifiers keep/extend a mixed selection ───────────────────────────────────────────

    [Fact]
    public void ShiftClickInstance_WhileShapeSelected_AddsToSelection_KeepsBoth()
    {
        var (vm, _) = MakeFixture();
        ClickAt(vm, 1000, 1000); // select shape 0
        Assert.Equal([0], vm.SelectedIndices);

        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift); // shift-click the instance

        Assert.Equal([0], vm.SelectedIndices);      // shape selection SURVIVES
        Assert.Equal([0], vm.SelectedInstanceIndices);
    }

    [Fact]
    public void PlainClickShape_WhileMixedSelected_ReplacesWholeSelection()
    {
        var (vm, _) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);
        Assert.Equal([0], vm.SelectedIndices);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        ClickAt(vm, 201_000, 201_000); // plain click on the FAR shape (shape 1)

        Assert.Equal([1], vm.SelectedIndices);
        Assert.Empty(vm.SelectedInstanceIndices); // instances cleared — this was a REPLACE
    }

    [Fact]
    public void PlainClickOnShape_ThatIsPartOfMixedMultiSelection_PreservesWholeSelection()
    {
        var (vm, _) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);
        Assert.Equal([0], vm.SelectedIndices);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        // A plain click back on the shape that's already part of this 2-member mixed selection must
        // preserve the WHOLE thing (so a drag from here moves both) — not collapse to just the shape.
        ClickAt(vm, 1000, 1000);

        Assert.Equal([0], vm.SelectedIndices);
        Assert.Equal([0], vm.SelectedInstanceIndices);
    }

    // ── Gate 6: mixed-selection operations — one undo entry each ────────────────────────────────

    [Fact]
    public void MoveDrag_MixedSelection_MovesBothKinds_OneUndoEntry()
    {
        var (vm, model) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        vm.OnPointerPressed(1000, 1000, KeyModifiers.None, 1, 40); // press inside the selected shape — preserves the mixed selection
        vm.OnPointerMoved(4000, 4000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(4000, 4000, KeyModifiers.None);

        Assert.Equal(3000, ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(13_000, model.Instances[0].X);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(0, ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(10_000, model.Instances[0].X);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void Nudge_MixedSelection_MovesBothKinds_OneUndoEntry()
    {
        var (vm, model) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        vm.OnKeyDown(Key.Right, KeyModifiers.None);

        Assert.Equal(vm.Model.SnapDbu, ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(10_000 + vm.Model.SnapDbu, model.Instances[0].X);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(0, ((RectShape)model.Shapes[0]).X1);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void Delete_MixedSelection_RemovesBothKinds_OneUndoEntry()
    {
        var (vm, model) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        Assert.Equal(1, model.Shapes.Count);   // only the far shape remains
        Assert.Empty(model.Instances);
        Assert.Empty(vm.SelectedIndices);
        Assert.Empty(vm.SelectedInstanceIndices);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(2, model.Shapes.Count);
        Assert.Single(model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void Duplicate_MixedSelection_DuplicatesBothKinds_OneUndoEntry_SelectsNewSet()
    {
        var (vm, model) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        vm.Duplicate();

        Assert.Equal(3, model.Shapes.Count);     // original 2 + 1 duplicate
        Assert.Equal(2, model.Instances.Count);  // original 1 + 1 duplicate
        Assert.NotEmpty(vm.SelectedIndices);
        Assert.NotEmpty(vm.SelectedInstanceIndices);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(2, model.Shapes.Count);
        Assert.Single(model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void CutSelectionAfterCopy_MixedSelection_DeletesBothKinds_OneUndoEntry()
    {
        var (vm, model) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        var payload = vm.BuildCopyPayload(); // the caller writes to the system clipboard BEFORE cutting
        Assert.NotNull(payload);
        Assert.Single(payload!.Shapes);
        Assert.Single(payload.Instances);

        vm.CutSelectionAfterCopy();

        Assert.Equal(1, model.Shapes.Count);
        Assert.Empty(model.Instances);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(2, model.Shapes.Count);
        Assert.Single(model.Instances);
    }

    [Fact]
    public void PasteInPlace_MixedPayload_LandsBothKinds_OneUndoEntry()
    {
        var (vm, model) = MakeFixture();
        var pastedShape = new RectShape { Layer = LayerA, X1 = 50_000, Y1 = 50_000, X2 = 51_000, Y2 = 51_000 };
        var pastedInstance = new LayoutInstance { CellRef = "../../Leaf", X = 60_000, Y = 60_000, Mag = 1.0 };

        vm.PasteInPlace([pastedShape], [pastedInstance]);

        Assert.Equal(3, model.Shapes.Count);
        Assert.Equal(2, model.Instances.Count);
        Assert.NotEmpty(vm.SelectedIndices);
        Assert.NotEmpty(vm.SelectedInstanceIndices);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(2, model.Shapes.Count);
        Assert.Single(model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Gate 6, second half: shape-only ops disabled with a reason naming the instance count ──────

    private (LayoutEditorViewModel Vm, LayoutView Model) MakeMixedTwoShapeSelection()
    {
        var (vm, model) = MakeFixture();
        // A second same-layer shape overlapping shape 0, so BooleanOpAvailability would otherwise be
        // enabled. Clicked at (2200,2200) — inside shape 2's (500,500)-(2500,2500) but OUTSIDE shape
        // 0's (0,0)-(2000,2000), so the click unambiguously hits shape 2 (no overlap-cycling needed).
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 500, Y1 = 500, X2 = 2500, Y2 = 2500 });
        ClickAt(vm, 1000, 1000);                    // shape 0
        ClickAt(vm, 2200, 2200, KeyModifiers.Shift); // + shape 2 (same layer, overlapping)
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift); // + the instance
        Assert.Equal(2, vm.SelectedIndices.Count);
        Assert.Single(vm.SelectedInstanceIndices);
        return (vm, model);
    }

    [Fact]
    public void BooleanOpAvailability_MixedSelection_DisabledWithReason_NamingInstanceCount()
    {
        var (vm, _) = MakeMixedTwoShapeSelection();

        var avail = vm.BooleanOpAvailability;

        Assert.False(avail.CanExecute);
        Assert.Contains("1 instance", avail.DisabledReason);
        Assert.Contains("shapes only", avail.DisabledReason);
    }

    [Fact]
    public void OffsetAvailability_MixedSelection_Disabled()
    {
        var (vm, _) = MakeMixedTwoShapeSelection();
        Assert.False(vm.OffsetAvailability.CanExecute);
        Assert.Contains("instance", vm.OffsetAvailability.DisabledReason);
    }

    [Fact]
    public void FlattenAvailability_MixedSelection_Disabled_EvenWithCurvedGeometryPresent()
    {
        var (vm, model) = MakeFixture();
        model.Shapes.Add(new CircleShape { Layer = LayerA, Cx = 5000, Cy = 5000, R = 500 });
        ClickAt(vm, 5000, 5500); // the circle
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift); // + the instance

        Assert.False(vm.FlattenAvailability.CanExecute);
        Assert.Contains("instance", vm.FlattenAvailability.DisabledReason);
    }

    [Fact]
    public void ScaleAvailability_MixedSelection_Disabled()
    {
        var (vm, _) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        Assert.False(vm.ScaleAvailability.CanExecute);
        Assert.Contains("instance", vm.ScaleAvailability.DisabledReason);
    }

    [Fact]
    public void ShowScaleHandles_False_WhenInstanceMixedIntoAMultiShapeSelection()
    {
        var (vm, _) = MakeMixedTwoShapeSelection(); // 2 shapes + 1 instance — would show bbox handles if shape-only

        Assert.False(vm.ShowScaleHandles);
    }

    [Fact]
    public void CutCopyDeleteDuplicateAvailability_MixedSelection_Enabled()
    {
        var (vm, _) = MakeFixture();
        ClickAt(vm, 1000, 1000);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift);

        Assert.True(vm.CutCopyDeleteDuplicateAvailability.CanExecute);
    }

    [Fact]
    public void FindVertexAndEdgeForContextMenu_ReturnNull_WhenInstanceMixedIntoASingleShapeSelection()
    {
        var (vm, model) = MakeFixture();
        model.Shapes[0] = new PolygonShape { Layer = LayerA, Xy = [0, 0, 2000, 0, 1000, 2000] };
        ClickAt(vm, 500, 500);
        ClickAt(vm, 10_500, 10_500, KeyModifiers.Shift); // mix in the instance
        Assert.Single(vm.SelectedIndices);
        Assert.Single(vm.SelectedInstanceIndices);

        Assert.Null(vm.FindVertexForContextMenu(0, 0, 100));
        Assert.Null(vm.FindEdgeForContextMenu(1000, 0, 100));
    }
}
