using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Layer 2 gate — three-state resolver test.
//
//  Scenario              Resolution
//  ───────────────────── ─────────────
//  Cell + primary .csym  → Resolved (with primitives accessible)
//  Cell folder absent    → NotFound
//  .ccell names missing  → PrimaryMissing
//  No .csym at all       → PrimaryMissing (NoView branch)
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CellSymbolResolverTests : IDisposable
{
    // One temp dir shared across tests in this class; wiped on Dispose.
    private readonly string _workspaceDir;

    public CellSymbolResolverTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfResolverTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateCell(string name)
    {
        return CellFolder.CreateCellFolder(_workspaceDir, name);
    }

    private static void WriteMinimalCsym(string cellDir, string fileName)
    {
        string symDir  = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        string symPath = Path.Combine(symDir, fileName);
        var sym = new EditableSymbol { UserEditable = true };
        SymbolPersistence.SaveToFile(symPath, sym.ToSymbol());
    }

    private static void SetNamedPrimary(string cellDir, string primarySymFileName)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = primarySymFileName;
        CellPersistence.SaveToFile(ccellPath, ccell);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_WhenCellFolderMissing_ReturnsNotFound()
    {
        // CellRef points at a folder that was never created.
        var result = CellSymbolResolver.Resolve("GhostCell", _workspaceDir);

        Assert.Equal(CellSymbolState.NotFound, result.State);
        Assert.Null(result.Symbol);
    }

    [Fact]
    public void Resolve_WhenCellExistsWithPrimary_ReturnsResolved()
    {
        var cellDir = CreateCell("MyCell");
        WriteMinimalCsym(cellDir, "main.csym");

        var result = CellSymbolResolver.Resolve("MyCell", _workspaceDir);

        Assert.Equal(CellSymbolState.Resolved, result.State);
        Assert.NotNull(result.Symbol);
    }

    [Fact]
    public void Resolve_WhenCellExistsNoSymbolFiles_ReturnsPrimaryMissing()
    {
        CreateCell("EmptyCell");
        // Don't write any .csym — NoView branch.

        var result = CellSymbolResolver.Resolve("EmptyCell", _workspaceDir);

        Assert.Equal(CellSymbolState.PrimaryMissing, result.State);
    }

    [Fact]
    public void Resolve_WhenCcellNamesAbsentPrimary_ReturnsPrimaryMissing()
    {
        var cellDir = CreateCell("BrokenCell");
        WriteMinimalCsym(cellDir, "viewA.csym");
        WriteMinimalCsym(cellDir, "viewB.csym");
        // Name a primary that does not exist → MissingNamedPrimary branch.
        SetNamedPrimary(cellDir, "doesNotExist.csym");

        var result = CellSymbolResolver.Resolve("BrokenCell", _workspaceDir);

        Assert.Equal(CellSymbolState.PrimaryMissing, result.State);
    }

    [Fact]
    public void Resolve_DeleteFolderAfterCaching_ReturnsNotFound()
    {
        var cellDir = CreateCell("TransientCell");
        WriteMinimalCsym(cellDir, "main.csym");

        // First call — warms the cache.
        var first = CellSymbolResolver.Resolve("TransientCell", _workspaceDir);
        Assert.Equal(CellSymbolState.Resolved, first.State);

        // Blow away the folder, invalidate the cache.
        Directory.Delete(cellDir, recursive: true);
        CellSymbolResolver.Invalidate(cellDir);

        var second = CellSymbolResolver.Resolve("TransientCell", _workspaceDir);
        Assert.Equal(CellSymbolState.NotFound, second.State);
    }

    [Fact]
    public void Resolve_MultipleFilesWithNamedPresent_ReturnsResolved()
    {
        var cellDir = CreateCell("MultiSym");
        WriteMinimalCsym(cellDir, "v1.csym");
        WriteMinimalCsym(cellDir, "v2.csym");
        // Explicitly name v2 as primary.
        SetNamedPrimary(cellDir, "v2.csym");

        var result = CellSymbolResolver.Resolve("MultiSym", _workspaceDir);

        Assert.Equal(CellSymbolState.Resolved, result.State);
        Assert.NotNull(result.Symbol);
    }

    [Fact]
    public void Resolve_WithRelativeDotDotPath_Resolves()
    {
        // Sub-directory inside workspace that refers to the cell with "../CellX".
        string subDir = Path.Combine(_workspaceDir, "sub");
        Directory.CreateDirectory(subDir);
        var cellDir = CreateCell("CellX");
        WriteMinimalCsym(cellDir, "main.csym");

        var result = CellSymbolResolver.Resolve("../CellX", subDir);

        Assert.Equal(CellSymbolState.Resolved, result.State);
    }

    [Fact]
    public void Invalidate_ClearsOnlyTargetCell()
    {
        var cellA = CreateCell("CellA");
        var cellB = CreateCell("CellB");
        WriteMinimalCsym(cellA, "main.csym");
        WriteMinimalCsym(cellB, "main.csym");

        // Warm both.
        var ra = CellSymbolResolver.Resolve("CellA", _workspaceDir);
        var rb = CellSymbolResolver.Resolve("CellB", _workspaceDir);
        Assert.Equal(CellSymbolState.Resolved, ra.State);
        Assert.Equal(CellSymbolState.Resolved, rb.State);

        // Invalidate only A.
        CellSymbolResolver.Invalidate(cellA);

        // Both should still resolve — cache miss for A, cache hit for B (both go to disk for A).
        var ra2 = CellSymbolResolver.Resolve("CellA", _workspaceDir);
        var rb2 = CellSymbolResolver.Resolve("CellB", _workspaceDir);
        Assert.Equal(CellSymbolState.Resolved, ra2.State);
        Assert.Equal(CellSymbolState.Resolved, rb2.State);
    }
}
