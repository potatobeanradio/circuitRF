using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §3/R-L5g-5: "double-clicking a PCell still enters its
/// hierarchy" even after the round-1 fix (<c>PCellPushInDisabledTests.cs</c>, which this file confirms
/// is STILL correct — <c>LayoutHierarchyResolver.CanPushInto</c> genuinely refuses at every push-in
/// entry point: canvas double-click, Ctrl/⌘+], and the App-menu "Push Into Cell" command, all of which
/// route through it). The round-1 fix did not take a SECOND way in: opening a generated cell's own
/// <c>.clay</c> directly (a file picker, a stale <c>.cws</c> restore entry, or — before this brief —
/// the Project Tree's Generated Cells group) was never push-in at all, so it never touched
/// <c>CanPushInto</c>. From the user's side this reads identically to a push-in: double-click something
/// PCell-related, land inside its generated geometry.
///
/// <c>GeneratedCellStore.IsUnderGeneratedCellsFolder</c> is the new, independent gate that closes this
/// second path — checked by <c>WorkspaceViewModel.OpenOrActivateLayout</c>, the ONE funnel every
/// "open this .clay as its own document" caller goes through (the file picker, and <c>.cws</c>
/// <c>OpenDocuments</c> restore). This is a pure, framework-free predicate, so it is tested directly
/// here rather than through the "simulate WorkspaceViewModel" pattern.
/// </summary>
public sealed class PCellDoubleClickSecondEntryPointTests : IDisposable
{
    private readonly string _root;

    public PCellDoubleClickSecondEntryPointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-2nd-entry-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void GeneratedCellClayPath_IsDetected_RegardlessOfWorkspaceRoot()
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string clayPath = Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay");

        Assert.True(GeneratedCellStore.IsUnderGeneratedCellsFolder(clayPath));
    }

    [Fact]
    public void OrdinaryCellClayPath_IsNotFlagged()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "MyAmp");
        string clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "MyAmp.clay");

        Assert.False(GeneratedCellStore.IsUnderGeneratedCellsFolder(clayPath));
    }

    [Fact]
    public void ACellNamed_LikeTheReservedFolder_AsALeafComponent_IsNotFalselyFlagged()
    {
        // The check is segment-exact, not substring — a hypothetical cell literally named
        // ".generated-cells-backup" (an unlikely but legal folder name) must not be swept in.
        string cellDir = CellFolder.CreateCellFolder(_root, ".generated-cells-backup");
        string clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "x.clay");

        Assert.False(GeneratedCellStore.IsUnderGeneratedCellsFolder(clayPath));
    }

    [Fact]
    public void CanPushInto_StillCorrectlyRefusesAtEveryPushInEntryPoint_RoundOneFixHolds()
    {
        // Direct confirmation (not an assumption) that the round-1 fix genuinely works — the bug this
        // brief investigates was never in CanPushInto itself.
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };

        Assert.False(LayoutHierarchyResolver.CanPushInto(inst, vm, out var reason));
        Assert.Equal(LayoutHierarchyResolver.PCellPushInRefusedReason, reason);
    }
}
