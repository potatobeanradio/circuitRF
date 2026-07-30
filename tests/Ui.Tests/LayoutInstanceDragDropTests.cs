using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  brief-L3a-followups.md §4/R-fix-5/R-fix-6 — dragging a cell from the project tree onto a layout.
//  LayoutCanvas.OnCellDragOver/OnCellDrop themselves cannot be driven headlessly (any Control
//  subclass needs a live Avalonia runtime, per this project's established testing constraint) — these
//  tests drive the exact VM-level methods the canvas calls: UpdateDragInstanceGhost,
//  WouldDragCellBeSelfReference, CommitDragInstancePlacement, CancelDragInstancePlacement.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceDragDropTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceDragDropTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstDragDropTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private sealed class RecordingMessageSink : IMessageSink
    {
        public readonly List<(MessageLevel Level, string Text)> Messages = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Messages.Add((level, text));
        public void Clear() => Messages.Clear();
    }

    private string CreateCell(string name, Action<LayoutView>? populate = null)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        populate?.Invoke(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    /// <summary>A cell whose own layout already instances <paramref name="instanceCellRef"/> — used to
    /// build a genuine two-cell A&lt;-&gt;B cycle setup, mirroring CellHierarchyTests' own
    /// CreateEmptyCellWithInstance helper.</summary>
    private string CreateCellWithInstance(string name, string instanceCellRef)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = instanceCellRef, X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private (LayoutEditorViewModel Vm, RecordingMessageSink Sink) OpenVmOnCell(string cellDir)
    {
        string clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay");
        var sink = new RecordingMessageSink();
        var vm = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(clayPath), clayPath, sink);
        return (vm, sink);
    }

    // ── WouldDragCellBeSelfReference (R-fix-1/R-fix-6's "exclude/refuse the parent cell only") ────

    [Fact]
    public void WouldDragCellBeSelfReference_TrueForTheParentCellItself()
    {
        var rootDir = CreateCell("Root");
        var (vm, _) = OpenVmOnCell(rootDir);

        Assert.True(vm.WouldDragCellBeSelfReference(rootDir));
    }

    [Fact]
    public void WouldDragCellBeSelfReference_FalseForAnyOtherCell_EvenADeeperCycleFormingOne()
    {
        var rootDir = CreateCell("Root");
        var aDir = CreateCellWithInstance("A", "../../Root"); // A already instantiates Root
        var (vm, _) = OpenVmOnCell(rootDir);

        // A is not the literal parent (Root) — DragOver must accept it, per R-fix-1's own principle,
        // even though placing it WOULD actually close a cycle (checked separately, at drop time).
        Assert.False(vm.WouldDragCellBeSelfReference(aDir));
    }

    [Fact]
    public void WouldDragCellBeSelfReference_UnrelatedCell_False()
    {
        var rootDir = CreateCell("Root");
        var otherDir = CreateCell("Other");
        var (vm, _) = OpenVmOnCell(rootDir);

        Assert.False(vm.WouldDragCellBeSelfReference(otherDir));
    }

    // ── CommitDragInstancePlacement (R-fix-6: same command path, same cycle guard) ─────────────────

    [Fact]
    public void CommitDragInstancePlacement_NormalCell_AddsOneInstance_SelectsIt_OneUndoEntry()
    {
        var rootDir = CreateCell("Root");
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var (vm, _) = OpenVmOnCell(rootDir);

        // "../../Leaf": Root's InstanceBaseDir is Root/layout/ — up one reaches Root/, up two reaches
        // the workspace root, where Leaf (a sibling of Root) actually lives.
        bool placed = vm.CommitDragInstancePlacement("../../Leaf", 5000, 6000);

        Assert.True(placed);
        Assert.Single(vm.Model.Instances);
        Assert.Equal("../../Leaf", vm.Model.Instances[0].CellRef);
        Assert.Equal(5000, vm.Model.Instances[0].X);
        Assert.Equal(6000, vm.Model.Instances[0].Y);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Instances);
    }

    [Fact]
    public void CommitDragInstancePlacement_DeeperCycle_RefusedWithMessage_NoInstanceAdded()
    {
        // Root <- A -> Root: A already instances Root. Dropping A onto Root's own canvas would close
        // Root -> A -> Root — a genuine two-cell cycle, distinct from the trivial self-reference case,
        // and NOT caught by DragOver's own self-only check (WouldDragCellBeSelfReference is false for
        // A) — this is exactly the "accepted by DragOver, refused on drop" case R-fix-1/R-fix-6 name.
        var rootDir = CreateCell("Root");
        CreateCellWithInstance("A", "../../Root");
        var (vm, sink) = OpenVmOnCell(rootDir);

        bool placed = vm.CommitDragInstancePlacement("../../A", 0, 0);

        Assert.False(placed);
        Assert.Empty(vm.Model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Contains(sink.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("cycle"));
    }

    // ── Gate 9: drop and the Instance tool produce an IDENTICAL LayoutInstance for the same cell/point ──

    [Fact]
    public void CommitDragInstancePlacement_And_InstanceTool_ProduceIdenticalLayoutInstance()
    {
        var rootDir = CreateCell("Root");
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));

        var (dragVm, _) = OpenVmOnCell(rootDir);
        dragVm.CommitDragInstancePlacement("../../Leaf", 7000, 8000);
        var viaDrag = dragVm.Model.Instances[0];

        // The Instance tool's ghost only tracks the cursor via OnPointerMoved (R-lbl-2-style gesture
        // vocabulary) — OnPointerPressed alone commits WHEREVER the ghost currently sits, so a real
        // "click at (7000,8000)" needs the pointer to have moved there first, exactly like a live drag.
        var (toolVm, _) = OpenVmOnCell(rootDir);
        toolVm.BeginInstancePlacement("../../Leaf");
        toolVm.OnPointerMoved(7000, 8000, leftDown: false, KeyModifiers.None);
        toolVm.OnPointerPressed(7000, 8000, KeyModifiers.None);
        var viaTool = toolVm.Model.Instances[0];

        Assert.Equal(viaTool.CellRef, viaDrag.CellRef);
        Assert.Equal(viaTool.X, viaDrag.X);
        Assert.Equal(viaTool.Y, viaDrag.Y);
        Assert.Equal(viaTool.Rot, viaDrag.Rot);
        Assert.Equal(viaTool.MirrorX, viaDrag.MirrorX);
        Assert.Equal(viaTool.Mag, viaDrag.Mag);
        Assert.Equal(viaTool.Rows, viaDrag.Rows);
        Assert.Equal(viaTool.Cols, viaDrag.Cols);
        Assert.Equal(viaTool.PitchX, viaDrag.PitchX);
        Assert.Equal(viaTool.PitchY, viaDrag.PitchY);
    }

    // ── Ghost lifecycle ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateDragInstanceGhost_SetsPendingPlacementOverlay_DoesNotChangeActiveToolOrSelection()
    {
        var rootDir = CreateCell("Root");
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var (vm, _) = OpenVmOnCell(rootDir);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;

        vm.UpdateDragInstanceGhost("../../Leaf", 1000, 2000);

        Assert.NotNull(vm.Overlay.PendingInstancePlacement);
        Assert.Equal("../../Leaf", vm.Overlay.PendingInstancePlacement!.Value.Instance.CellRef);
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool); // never armed the Instance tool
        Assert.False(vm.IsInstancePlacementActive);                    // a SEPARATE state machine
        Assert.Empty(vm.Model.Instances);                               // nothing committed yet
    }

    [Fact]
    public void CancelDragInstancePlacement_ClearsTheGhost()
    {
        var rootDir = CreateCell("Root");
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var (vm, _) = OpenVmOnCell(rootDir);
        vm.UpdateDragInstanceGhost("../../Leaf", 1000, 2000);
        Assert.NotNull(vm.Overlay.PendingInstancePlacement);

        vm.CancelDragInstancePlacement();

        Assert.Null(vm.Overlay.PendingInstancePlacement);
    }

    [Fact]
    public void CommitDragInstancePlacement_ClearsTheGhost()
    {
        var rootDir = CreateCell("Root");
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var (vm, _) = OpenVmOnCell(rootDir);
        vm.UpdateDragInstanceGhost("../../Leaf", 1000, 2000);

        vm.CommitDragInstancePlacement("../../Leaf", 1000, 2000);

        Assert.Null(vm.Overlay.PendingInstancePlacement);
    }
}
