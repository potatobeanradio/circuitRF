using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a — LayoutEditorViewModel instance editing: placement, selection, move, delete, array/
//  rotation/mirror/mag property edits, retarget, and R-L3a-2's edit-time cycle rejection.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceEditingTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceEditingTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstEditTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
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

    private sealed class RecordingMessageSink : IMessageSink
    {
        public readonly List<(MessageLevel Level, string Text)> Messages = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Messages.Add((level, text));
        public void Clear() => Messages.Clear();
    }

    private (LayoutEditorViewModel Vm, RecordingMessageSink Sink) MakeVm()
    {
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));
        var sink = new RecordingMessageSink();
        string clayPath = Path.Combine(_workspaceDir, "Root", "layout", "root.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath, sink);
        return (vm, sink);
    }

    // ── Placement (§6) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeginAndCommitInstancePlacement_AddsOneInstance_SelectsIt_OneUndoEntry()
    {
        var (vm, _) = MakeVm();
        vm.BeginInstancePlacement("../../Leaf");
        Assert.True(vm.IsInstancePlacementActive);
        Assert.Equal(LayoutEditorViewModel.Tool.Instance, vm.ActiveTool);

        vm.OnPointerPressed(5000, 6000, KeyModifiers.None);

        Assert.Single(vm.Model.Instances);
        Assert.Equal("../../Leaf", vm.Model.Instances[0].CellRef);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.True(vm.UndoRedo.CanUndo);

        // Stays armed for the next placement.
        Assert.True(vm.IsInstancePlacementActive);
    }

    [Fact]
    public void CommitInstancePlacement_Undo_RemovesInstance()
    {
        var (vm, _) = MakeVm();
        vm.BeginInstancePlacement("../../Leaf");
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        Assert.Single(vm.Model.Instances);

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Instances);

        vm.UndoRedo.Redo();
        Assert.Single(vm.Model.Instances);
    }

    [Fact]
    public void EscapeDuringPlacement_CancelsWithoutPushingCommand()
    {
        var (vm, _) = MakeVm();
        vm.BeginInstancePlacement("../../Leaf");
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsInstancePlacementActive);
        Assert.Empty(vm.Model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
    }

    // ── Selection / move / delete (§5, R-L3a-5) ──────────────────────────────────────────────────

    [Fact]
    public void ClickOnInstanceGeometry_SelectsInstance_ClearsAnyShapeSelection()
    {
        var (vm, _) = MakeVm();
        vm.Model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 100_000, Y1 = 100_000, X2 = 200_000, Y2 = 200_000 });
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });

        // Select the shape first.
        vm.OnPointerPressed(150_000, 150_000, KeyModifiers.None, hitTolDbu: 10);
        Assert.Single(vm.SelectedIndices);
        vm.OnPointerReleased(150_000, 150_000, KeyModifiers.None);

        // Now click the instance's own geometry (rect [0,1000]).
        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);

        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.Empty(vm.SelectedIndices); // mutual exclusivity
    }

    [Fact]
    public void DragSelectedInstance_MovesIt_OneUndoEntry()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });

        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10); // press on the rect -> selects + begins move
        Assert.Equal([0], vm.SelectedInstanceIndices);

        vm.OnPointerMoved(10_500, 20_500, leftDown: true, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(10_500, 20_500, KeyModifiers.None);

        Assert.Equal(10_000, vm.Model.Instances[0].X);
        Assert.Equal(20_000, vm.Model.Instances[0].Y);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(0, vm.Model.Instances[0].X);
        Assert.Equal(0, vm.Model.Instances[0].Y);
    }

    [Fact]
    public void DeleteKey_WithInstanceSelected_RemovesInstance_NotShapes()
    {
        var (vm, _) = MakeVm();
        vm.Model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 500_000, Y = 500_000, Mag = 1.0 });

        vm.OnPointerPressed(500_500, 500_500, KeyModifiers.None, hitTolDbu: 10);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        vm.OnPointerReleased(500_500, 500_500, KeyModifiers.None); // release the move-drag the click armed

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        Assert.Empty(vm.Model.Instances);
        Assert.Single(vm.Model.Shapes); // the unrelated shape is untouched
    }

    [Fact]
    public void NudgeArrowKey_WithInstanceSelected_MovesBySnapStep()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.OnKeyDown(Key.Right, KeyModifiers.None);

        Assert.Equal(vm.Model.SnapDbu, vm.Model.Instances[0].X);
    }

    // ── Array / rotation / mirror / mag properties (§6) ──────────────────────────────────────────

    [Fact]
    public void CommitSelectedInstanceArray_UpdatesFields_OneUndoEntry()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.CommitSelectedInstanceArray(rows: 5, cols: 7, pitchX: 2000, pitchY: 3000);

        var inst = vm.Model.Instances[0];
        Assert.Equal(5, inst.Rows);
        Assert.Equal(7, inst.Cols);
        Assert.Equal(2000, inst.PitchX);
        Assert.Equal(3000, inst.PitchY);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(1, vm.Model.Instances[0].Rows);
        Assert.Equal(1, vm.Model.Instances[0].Cols);
    }

    [Fact]
    public void SetSelectedInstanceRotationAndMirror_Commits()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.SetSelectedInstanceRotationDegrees(180.0);
        vm.SetSelectedInstanceMirrorX(true);

        Assert.Equal(180.0, vm.Model.Instances[0].RotationDegrees, 9);
        Assert.True(vm.Model.Instances[0].MirrorX);
    }

    [Fact]
    public void CommitSelectedInstanceMagText_ParsesAndCommits_RejectsInvalid()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.CommitSelectedInstanceMagText("2.5");
        Assert.Equal(2.5, vm.Model.Instances[0].Mag);

        vm.CommitSelectedInstanceMagText("not a number");
        Assert.Equal(2.5, vm.Model.Instances[0].Mag); // unchanged — invalid text is a no-op, never throws

        vm.CommitSelectedInstanceMagText("-1");
        Assert.Equal(2.5, vm.Model.Instances[0].Mag); // non-positive rejected too
    }

    [Fact]
    public void CommitSelectedInstancePosition_TranslatesOnlyTheGivenAxis_OneUndoEntryEach()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 1000, Y = 2000, Mag = 1.0 });
        vm.OnPointerPressed(1500, 2500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(1500, 2500, KeyModifiers.None);

        vm.CommitSelectedInstancePosition(newX: 5000, newY: null);
        Assert.Equal(5000, vm.Model.Instances[0].X);
        Assert.Equal(2000, vm.Model.Instances[0].Y); // untouched

        vm.CommitSelectedInstancePosition(newX: null, newY: 7000);
        Assert.Equal(5000, vm.Model.Instances[0].X); // untouched
        Assert.Equal(7000, vm.Model.Instances[0].Y);

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(2000, vm.Model.Instances[0].Y);
        vm.UndoRedo.Undo();
        Assert.Equal(1000, vm.Model.Instances[0].X);
    }

    [Fact]
    public void CommitSelectedInstancePosition_BothNull_IsANoOp()
    {
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 1000, Y = 2000, Mag = 1.0 });
        vm.OnPointerPressed(1500, 2500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(1500, 2500, KeyModifiers.None);

        vm.CommitSelectedInstancePosition(newX: null, newY: null);

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(1000, vm.Model.Instances[0].X);
        Assert.Equal(2000, vm.Model.Instances[0].Y);
    }

    // ── Retarget + edit-time cycle rejection (R-L3a-2) ───────────────────────────────────────────

    [Fact]
    public void RetargetSelectedInstance_ChangesCellRef_PreservesGeometry()
    {
        CreateCell("Other", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 50, Y2 = 50 }));
        var (vm, _) = MakeVm();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 1000, Y = 2000, Rot = LayoutRotation.R90, Mag = 1.0 });

        // R90, no mirror: local (0,0)-(1000,1000) -> (0,0)-(-1000,1000) -> world [0,1000]x[2000,3000]
        // (translate by X=1000,Y=2000) — see LayoutInstanceTransformTests' table for the same mapping.
        vm.OnPointerPressed(500, 2500, KeyModifiers.None, hitTolDbu: 10);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        vm.RetargetSelectedInstance("../Other");

        var inst = vm.Model.Instances[0];
        Assert.Equal("../Other", inst.CellRef);
        Assert.Equal(1000, inst.X);
        Assert.Equal(2000, inst.Y);
        Assert.Equal(LayoutRotation.R90, inst.Rot); // geometry (position/rotation) untouched
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void PlacingAnInstance_ThatWouldCreateACycle_IsRefused_WithMessage()
    {
        // Root references A; A's own layout will reference back to Root — placing that instance
        // (from A's own editor context) would close the cycle. We simulate this by opening Root's
        // OWN editor (not A's) and trying to place an instance whose target (A) already, when we set
        // it up, references back to Root — i.e. Root -> A already exists; adding A -> Root (from a
        // hypothetical A editor) would cycle. Exercised directly via CheckNotCyclic's public surface
        // (BeginInstancePlacement + commit), scoped to what this VM can drive: a direct self-reference,
        // the simplest and most literal "A -> B -> A" collapse (A -> A).
        var sink = new RecordingMessageSink();
        var rootCellDir = CellFolder.CreateCellFolder(_workspaceDir, "SelfRef");
        string layoutDir = CellFolder.SubFolderPath(rootCellDir, ViewType.Layout);
        string clayPath = Path.Combine(layoutDir, "main.clay");
        LayoutPersistence.SaveToFile(clayPath, new LayoutView { DbuPerMicron = 1000 });

        var vm = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(clayPath), clayPath, sink);
        // ".." from this document's own layout/ sub-folder resolves to the SelfRef cell folder
        // itself — a direct self-cycle (InstanceBaseDir's own doc comment: CellRef resolves against
        // the directory CONTAINING the .clay, i.e. the layout/ sub-folder, one level below the cell).
        vm.BeginInstancePlacement("..");
        vm.OnPointerPressed(0, 0, KeyModifiers.None);

        Assert.Empty(vm.Model.Instances);
        Assert.Contains(sink.Messages, m => m.Level == MessageLevel.Error && m.Text.Contains("cycle"));
    }
}
