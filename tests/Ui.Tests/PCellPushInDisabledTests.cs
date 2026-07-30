using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §3/R-L5f-6, gate 6: double-clicking a PCell instance
/// does NOT push in — its geometry is generated, read-only (pcell-contract.md R9); push-in is
/// disabled with a stated reason. Push-in for an ordinary (non-PCell) hierarchical instance is
/// untouched. <c>LayoutHierarchyResolver.CanPushInto</c> is the ONE gate both the toolbar button
/// (<c>LayoutEditorView.UpdateHierarchyButtonStates</c>) and double-click (<c>DoPushInto</c>) already
/// route through, so fixing it here fixes both entry points at once — no separate view-layer check
/// was needed.
/// </summary>
public sealed class PCellPushInDisabledTests : IDisposable
{
    private readonly string _root;

    public PCellPushInDisabledTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-pushin-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_root, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    [Fact]
    public void PCellInstance_PushInRefused_WithStatedReason()
    {
        var vm = MakeVmAt("Doc");
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(CircuitRF.Ui.Schematic.SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };

        bool can = LayoutHierarchyResolver.CanPushInto(inst, vm, out var reason);

        Assert.False(can);
        Assert.Equal(LayoutHierarchyResolver.PCellPushInRefusedReason, reason);
    }

    [Fact]
    public void OrdinaryHierarchicalInstance_PushInStillWorks()
    {
        var vm = MakeVmAt("Doc");
        var leafDir = CellFolder.CreateCellFolder(_root, "Leaf");
        var leafLayoutDir = CellFolder.SubFolderPath(leafDir, ViewType.Layout);
        LayoutPersistence.SaveToFile(Path.Combine(leafLayoutDir, "main.clay"),
            new LayoutView { DbuPerMicron = 1000 });

        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, leafDir), X = 0, Y = 0, Mag = 1.0 };

        bool can = LayoutHierarchyResolver.CanPushInto(inst, vm, out var reason);

        Assert.True(can);
        Assert.Null(reason);
    }
}
