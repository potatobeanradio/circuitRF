using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  brief-L3a-followups.md §1/R-fix-1 (gate 2) — the Instance cell-picker excludes ONLY the parent
//  (self-reference) cell; every other cell, including one that would form a deeper cycle, is listed;
//  a cell with no layout view is listed but disabled with its reason.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class InstanceCellChoicesTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public InstanceCellChoicesTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstChoicesTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private string CreateCellWithLayout(string name)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private string CreateCellWithoutLayout(string name) => CellFolder.CreateCellFolder(_workspaceDir, name);

    [Fact]
    public void ParentCell_IsExcluded_EverythingElseIsListed()
    {
        string parentDir = CreateCellWithLayout("Parent");
        CreateCellWithLayout("Other");

        var items = InstanceCellChoices.Collect(_workspaceDir, parentDir);

        Assert.DoesNotContain(items, i => i.AbsoluteCellDir == parentDir);
        Assert.Contains(items, i => i.DisplayName == "Other");
    }

    [Fact]
    public void NullParentCellDir_ExcludesNothing_MatchesScratchDocument()
    {
        string parentDir = CreateCellWithLayout("Parent");

        var items = InstanceCellChoices.Collect(_workspaceDir, null);

        Assert.Contains(items, i => i.AbsoluteCellDir == parentDir);
    }

    [Fact]
    public void ADeepChain_AInstantiatesB_EditingB_StillListsA()
    {
        // R-fix-1: "if A instantiates B, silently omitting A while editing B leaves the user hunting
        // for a cell that appears to have vanished." The picker filter itself knows nothing about
        // WHO references WHOM — it only ever excludes the literal parent — so A must be listed while
        // editing B regardless of the reference direction between them.
        string aDir = CreateCellWithLayout("A");
        string bDir = CreateCellWithLayout("B");

        var items = InstanceCellChoices.Collect(_workspaceDir, bDir); // editing B: parent = B

        Assert.Contains(items, i => i.AbsoluteCellDir == aDir);
        Assert.DoesNotContain(items, i => i.AbsoluteCellDir == bDir);
    }

    [Fact]
    public void ADeeperChain_AInstantiatesB_BInstantiatesC_EditingC_StillListsA()
    {
        string aDir = CreateCellWithLayout("A");
        CreateCellWithLayout("B");
        string cDir = CreateCellWithLayout("C");

        var items = InstanceCellChoices.Collect(_workspaceDir, cDir); // editing C: parent = C

        Assert.Contains(items, i => i.AbsoluteCellDir == aDir);
        Assert.DoesNotContain(items, i => i.AbsoluteCellDir == cDir);
    }

    [Fact]
    public void CellWithNoLayoutView_IsListed_ButDisabledWithReason()
    {
        string noLayoutDir = CreateCellWithoutLayout("Empty");

        var items = InstanceCellChoices.Collect(_workspaceDir, null);

        var found = Assert.Single(items, i => i.AbsoluteCellDir == noLayoutDir);
        Assert.False(found.IsEnabled);
        Assert.NotNull(found.DisabledReason);
        Assert.True(found.RowOpacity < 1.0);
    }

    [Fact]
    public void CellWithLayoutView_IsListed_Enabled()
    {
        string dir = CreateCellWithLayout("HasLayout");

        var items = InstanceCellChoices.Collect(_workspaceDir, null);

        var found = Assert.Single(items, i => i.AbsoluteCellDir == dir);
        Assert.True(found.IsEnabled);
        Assert.Null(found.DisabledReason);
        Assert.Equal(1.0, found.RowOpacity);
    }

    [Fact]
    public void NoWorkspaceRoot_ReturnsEmpty_NeverThrows()
    {
        var items = InstanceCellChoices.Collect("", null);
        Assert.Empty(items);

        var items2 = InstanceCellChoices.Collect(Path.Combine(_workspaceDir, "does-not-exist"), null);
        Assert.Empty(items2);
    }
}
