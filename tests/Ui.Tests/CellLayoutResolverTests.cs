using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a §1/§2 — CellLayoutResolver's three-state resolution, mirroring
//  CellSymbolResolverTests exactly (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md §1:
//  "CellSymbolResolver as the structural template").
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CellLayoutResolverTests : IDisposable
{
    private readonly string _workspaceDir;

    public CellLayoutResolverTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfLayoutResolverTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private string CreateCell(string name) => CellFolder.CreateCellFolder(_workspaceDir, name);

    private static void WriteMinimalClay(string cellDir, string fileName, Action<LayoutView>? populate = null)
    {
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string layoutPath = Path.Combine(layoutDir, fileName);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        populate?.Invoke(view);
        LayoutPersistence.SaveToFile(layoutPath, view);
    }

    private static void SetNamedPrimary(string cellDir, string primaryFileName)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = primaryFileName;
        CellPersistence.SaveToFile(ccellPath, ccell);
    }

    [Fact]
    public void Resolve_WhenCellFolderMissing_ReturnsNotFound()
    {
        var result = CellLayoutResolver.Resolve("GhostCell", _workspaceDir);
        Assert.Equal(CellLayoutState.NotFound, result.State);
        Assert.Null(result.View);
    }

    [Fact]
    public void Resolve_WhenCellExistsWithSolePrimary_ReturnsResolved()
    {
        var cellDir = CreateCell("ViaCell");
        WriteMinimalClay(cellDir, "main.clay", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }));

        var result = CellLayoutResolver.Resolve("ViaCell", _workspaceDir);

        Assert.Equal(CellLayoutState.Resolved, result.State);
        Assert.NotNull(result.View);
        Assert.Single(result.View!.Shapes);
        Assert.Equal(cellDir, result.ResolvedCellDir);
    }

    [Fact]
    public void Resolve_WhenCellHasNoLayoutView_ReturnsPrimaryMissing()
    {
        var cellDir = CreateCell("NoLayoutCell");
        var result = CellLayoutResolver.Resolve("NoLayoutCell", _workspaceDir);
        Assert.Equal(CellLayoutState.PrimaryMissing, result.State);
    }

    [Fact]
    public void Resolve_WhenCcellNamesMissingPrimary_ReturnsPrimaryMissing()
    {
        var cellDir = CreateCell("Ambiguous");
        WriteMinimalClay(cellDir, "a.clay");
        WriteMinimalClay(cellDir, "b.clay");
        SetNamedPrimary(cellDir, "nonexistent.clay");

        var result = CellLayoutResolver.Resolve("Ambiguous", _workspaceDir);
        Assert.Equal(CellLayoutState.PrimaryMissing, result.State);
    }

    [Fact]
    public void Resolve_NamedPrimaryAmongMultiple_ResolvesTheNamedOne()
    {
        var cellDir = CreateCell("Multi");
        WriteMinimalClay(cellDir, "a.clay", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }));
        WriteMinimalClay(cellDir, "b.clay", v =>
        {
            v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 });
            v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 });
        });
        SetNamedPrimary(cellDir, "b.clay");

        var result = CellLayoutResolver.Resolve("Multi", _workspaceDir);
        Assert.Equal(CellLayoutState.Resolved, result.State);
        Assert.Equal(2, result.View!.Shapes.Count);
    }

    [Fact]
    public void Resolve_CachesByMtime_ReturnsSameInstanceUntilFileChanges()
    {
        var cellDir = CreateCell("Cached");
        WriteMinimalClay(cellDir, "main.clay");

        var first = CellLayoutResolver.Resolve("Cached", _workspaceDir);
        var second = CellLayoutResolver.Resolve("Cached", _workspaceDir);
        Assert.Same(first.View, second.View);

        // Force a distinct mtime, then re-write with different content.
        Thread.Sleep(10);
        WriteMinimalClay(cellDir, "main.clay", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }));

        var third = CellLayoutResolver.Resolve("Cached", _workspaceDir);
        Assert.NotSame(first.View, third.View);
        Assert.Single(third.View!.Shapes);
    }

    [Fact]
    public void Invalidate_ForcesReloadOfThatCellOnly()
    {
        var cellA = CreateCell("A");
        var cellB = CreateCell("B");
        WriteMinimalClay(cellA, "main.clay");
        WriteMinimalClay(cellB, "main.clay");

        var a1 = CellLayoutResolver.Resolve("A", _workspaceDir);
        var b1 = CellLayoutResolver.Resolve("B", _workspaceDir);

        CellLayoutResolver.Invalidate(cellA);

        var a2 = CellLayoutResolver.Resolve("A", _workspaceDir);
        var b2 = CellLayoutResolver.Resolve("B", _workspaceDir);

        Assert.NotSame(a1.View, a2.View);
        Assert.Same(b1.View, b2.View);
    }

    [Fact]
    public void Invalidate_BumpsGeneration()
    {
        long before = CellLayoutResolver.Generation;
        CellLayoutResolver.Invalidate(_workspaceDir);
        Assert.True(CellLayoutResolver.Generation > before);
    }

    [Fact]
    public void InvalidateAll_BumpsGeneration()
    {
        long before = CellLayoutResolver.Generation;
        CellLayoutResolver.InvalidateAll();
        Assert.True(CellLayoutResolver.Generation > before);
    }
}
