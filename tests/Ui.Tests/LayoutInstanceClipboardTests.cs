using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a gate 11 — copy/paste an instance within and across layouts; CellRef resolves
//  correctly or is reported broken in the destination.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceClipboardTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceClipboardTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstClipTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private string CreateCell(string name)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_workspaceDir, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    /// <summary>A document nested several directory levels deep under the workspace root — for the
    /// cross-directory rebase test, where "how many ../ are needed" genuinely differs from the
    /// source document's own base dir.</summary>
    private LayoutEditorViewModel MakeVmAtNested(params string[] pathSegments)
    {
        string clayPath = Path.Combine([_workspaceDir, .. pathSegments, "layout", "main.clay"]);
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    // ── Within one document ───────────────────────────────────────────────────────────────────

    [Fact]
    public void CopyInstance_ThenPasteInSameDocument_CellRefUnchanged_ResolvesCorrectly()
    {
        CreateCell("Leaf");
        var vm = MakeVmAt("Doc");
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 1000, Y = 2000, Mag = 1.0 });

        var payload = vm.BuildCopyPayload(); // shape selection is empty; this test builds the payload directly below instead
        Assert.Null(payload); // nothing SELECTED yet — confirms Copy needs a real selection, not "any instance in the model"

        // Select the instance for real via the VM's own click path, then copy.
        vm.OnPointerPressed(1050, 2050, Avalonia.Input.KeyModifiers.None, hitTolDbu: 10);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        var copied = vm.BuildCopyPayload();
        Assert.NotNull(copied);
        Assert.Single(copied!.Instances);
        Assert.Equal("../../Leaf", copied.Instances[0].CellRef);

        // Rebase against the SAME document's own base dir — same-directory copy/paste rebases to the
        // identical relative path (a directory's relative path to itself and back is well-defined).
        var rebased = vm.RebaseFragmentInstances(copied);
        Assert.Equal("../../Leaf", rebased[0].CellRef);

        vm.PasteInstances(rebased);
        Assert.Equal(2, vm.Model.Instances.Count);
        var resolution = CellLayoutResolver.Resolve(vm.Model.Instances[1].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);
    }

    // ── Across two documents in different directories ────────────────────────────────────────────

    [Fact]
    public void CopyInstance_PasteIntoDifferentDocument_CellRefRebased_ResolvesCorrectly()
    {
        var leafDir = CreateCell("Leaf");
        var sourceVm = MakeVmAt("Source");
        sourceVm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        sourceVm.OnPointerPressed(50, 50, Avalonia.Input.KeyModifiers.None, hitTolDbu: 10);
        var payload = sourceVm.BuildCopyPayload()!;
        Assert.Equal(leafDir, payload.InstanceCellDirs[0]);

        // Destination lives several directory levels deeper — "../../Leaf" from THAT document's own
        // base would resolve to the WRONG place if reused verbatim; RebaseFragmentInstances must fix it.
        var destVm = MakeVmAtNested("Nested", "Deeper", "Dest");
        var rebased = destVm.RebaseFragmentInstances(payload);

        Assert.NotEqual("../../Leaf", rebased[0].CellRef); // the naive (unrebased) path would be wrong here
        var resolution = CellLayoutResolver.Resolve(rebased[0].CellRef, destVm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);
        Assert.Equal(leafDir, resolution.ResolvedCellDir);

        destVm.PasteInstancesInPlace(rebased);
        Assert.Single(destVm.Model.Instances);
        Assert.Equal(0, destVm.Model.Instances[0].X); // Paste in Place — original coordinates
    }

    [Fact]
    public void CopyAlreadyBrokenInstance_PasteElsewhere_StaysReportedBroken_NeverThrows()
    {
        // The instance references a cell that never existed — already broken at COPY time, so
        // Payload.InstanceCellDirs[0] is null (LayoutFragment.Payload's own doc comment: "or null
        // when it could not be resolved there"). RebaseFragmentInstances' documented fallback keeps
        // the original CellRef unchanged in that case — pasting elsewhere must still not throw, and
        // must resolve as broken (never silently vanish, R-L3a-1).
        var sourceVm = MakeVmAt("Source");
        sourceVm.Model.Instances.Add(new LayoutInstance { CellRef = "../../NeverExisted", X = 0, Y = 0, Mag = 1.0 });
        sourceVm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None, hitTolDbu: CellHierarchy.PlaceholderHalfExtentDbu);
        var payload = sourceVm.BuildCopyPayload()!;
        Assert.Null(payload.InstanceCellDirs[0]);

        var destVm = MakeVmAtNested("Elsewhere", "Nested");
        var rebased = destVm.RebaseFragmentInstances(payload);
        Assert.Equal("../../NeverExisted", rebased[0].CellRef); // fallback: kept unchanged, not silently rewritten wrong

        var exception = Record.Exception(() => destVm.PasteInstancesInPlace(rebased));
        Assert.Null(exception);
        Assert.Single(destVm.Model.Instances);

        var resolution = CellLayoutResolver.Resolve(destVm.Model.Instances[0].CellRef, destVm.InstanceBaseDir);
        Assert.NotEqual(CellLayoutState.Resolved, resolution.State); // broken — reported, not thrown, not vanished
    }

    [Fact]
    public void Duplicate_SelectedInstance_OffsetsByOneSnapStep_KeepsCellRef()
    {
        CreateCell("Leaf");
        var vm = MakeVmAt("Doc");
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(50, 50, Avalonia.Input.KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(50, 50, Avalonia.Input.KeyModifiers.None);

        vm.Duplicate();

        Assert.Equal(2, vm.Model.Instances.Count);
        var dup = vm.Model.Instances[1];
        Assert.Equal("../../Leaf", dup.CellRef);
        Assert.Equal(vm.Model.SnapDbu, dup.X);
        Assert.Equal(vm.Model.SnapDbu, dup.Y);
        Assert.Equal([1], vm.SelectedInstanceIndices); // the new instance becomes the selection
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void CutSelectedInstance_RemovesIt_OneUndoEntry()
    {
        CreateCell("Leaf");
        var vm = MakeVmAt("Doc");
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(50, 50, Avalonia.Input.KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(50, 50, Avalonia.Input.KeyModifiers.None);

        var payload = vm.BuildCopyPayload();
        Assert.NotNull(payload);
        vm.CutSelectionAfterCopy();

        Assert.Empty(vm.Model.Instances);
        vm.UndoRedo.Undo();
        Assert.Single(vm.Model.Instances);
    }
}
