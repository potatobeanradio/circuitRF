using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Headless tests for CellUsageScanner.CountReferencingCells.
/// Uses temp directories with real .ccell + .csch files so the scanner exercises
/// the full SchematicPersistence.LoadFromFile path.
/// </summary>
public class CellUsageScannerTests : IDisposable
{
    private readonly string _ws;

    public CellUsageScannerTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), $"crf_cus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string CreateCell(string name) => CellFolder.CreateCellFolder(_ws, name);

    private static void SaveSchematicWithCellRef(string cellDir, string cellRefRelative)
    {
        var schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        var cschPath = Path.Combine(schDir, Path.GetFileName(cellDir) + ".csch");

        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Resistor,
            CellRef      = cellRefRelative,
        });
        SchematicPersistence.SaveToFile(cschPath, model);
    }

    private static void SaveLayoutWithCellRef(string cellDir, string cellRefRelative)
    {
        var layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        var clayPath = Path.Combine(layDir, Path.GetFileName(cellDir) + ".clay");

        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = cellRefRelative, X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(clayPath, view);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CountsDistinctReferencingCells()
    {
        // cellA is the target; cellB and cellC each have one instance referencing it.
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        var cellC = CreateCell("cellC");

        // Relative path from cellB/schematic/ to cellA/ is ../../cellA
        SaveSchematicWithCellRef(cellB, "../../cellA");
        SaveSchematicWithCellRef(cellC, "../../cellA");

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(2, result);
    }

    [Fact]
    public void CountsTwoInstancesInOneCellAsOne()
    {
        // Two components in ONE schematic both reference cellA — counts as 1 referencing cell.
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");

        var schDir = CellFolder.SubFolderPath(cellB, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        var cschPath = Path.Combine(schDir, "cellB.csch");

        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Resistor, CellRef = "../../cellA" });
        model.Components.Add(new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.Resistor, CellRef = "../../cellA" });
        SchematicPersistence.SaveToFile(cschPath, model);

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(1, result);
    }

    [Fact]
    public void ZeroWhenUnused()
    {
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        // cellB has no schematic — nobody references cellA.

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ExcludesSelf()
    {
        // cellA's own schematic references itself — must NOT count as a user.
        var cellA = CreateCell("cellA");
        SaveSchematicWithCellRef(cellA, "..");   // relative from cellA/schematic/ to cellA/

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(0, result);
    }

    // ── RewriteCellReferences (Item 7) ────────────────────────────────────────

    [Fact]
    public void RewriteUpdatesLastSegmentMatch()
    {
        // cellA references oldCell; after rename oldCell→newCell the reference reads newCell.
        var oldCell = CreateCell("oldCell");
        var newCell = Path.Combine(_ws, "newCell");   // not created yet — we just check the rewrite
        var cellA   = CreateCell("cellA");
        SaveSchematicWithCellRef(cellA, "../../oldCell");

        CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out _);

        // Reload the schematic and verify the CellRef was updated.
        var schDir = CellFolder.SubFolderPath(cellA, ViewType.Schematic);
        var cschPath = Path.Combine(schDir, "cellA.csch");
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        Assert.Equal("../../newCell", model.Components[0].CellRef);
    }

    [Fact]
    public void RewriteDoesNotTouchUnrelatedRefs()
    {
        // cellA references "oldCell", cellB references "unrelated" — cellB's ref must be unchanged.
        CreateCell("oldCell");
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        SaveSchematicWithCellRef(cellA, "../../oldCell");
        SaveSchematicWithCellRef(cellB, "../../unrelated");

        CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out _);

        var schDirB  = CellFolder.SubFolderPath(cellB, ViewType.Schematic);
        var cschPathB = Path.Combine(schDirB, "cellB.csch");
        var (modelB, _, _) = SchematicPersistence.LoadFromFile(cschPathB);
        Assert.Equal("../../unrelated", modelB.Components[0].CellRef);
    }

    [Fact]
    public void RewriteReturnsUpdatedPaths()
    {
        CreateCell("oldCell");
        var cellA = CreateCell("cellA");
        SaveSchematicWithCellRef(cellA, "../../oldCell");

        var updated = CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out var failed);

        Assert.Empty(failed);
        Assert.Single(updated);
    }

    [Fact]
    public void RewriteEmptyWhenNoMatches()
    {
        CreateCell("oldCell");
        var cellA = CreateCell("cellA");
        SaveSchematicWithCellRef(cellA, "../../differentCell");

        var updated = CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out var failed);

        Assert.Empty(failed);
        Assert.Empty(updated);
    }

    // ── .clay instance references (Phase L3b — the gap L3a left open) ─────────────────────────────

    [Fact]
    public void CountReferencingCells_CountsALayoutInstanceReference()
    {
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        SaveLayoutWithCellRef(cellB, "../../cellA");

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(1, result);
    }

    [Fact]
    public void CountReferencingCells_MixedSchematicAndLayoutReferencesToSameCell_CountsAsTwoDistinctCells()
    {
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        var cellC = CreateCell("cellC");
        SaveSchematicWithCellRef(cellB, "../../cellA");
        SaveLayoutWithCellRef(cellC, "../../cellA");

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(2, result);
    }

    [Fact]
    public void CountReferencingCells_OneCellReferencingViaBothSchematicAndLayout_CountsAsOne()
    {
        var cellA = CreateCell("cellA");
        var cellB = CreateCell("cellB");
        SaveSchematicWithCellRef(cellB, "../../cellA");
        SaveLayoutWithCellRef(cellB, "../../cellA");

        int result = CellUsageScanner.CountReferencingCells(_ws, cellA);
        Assert.Equal(1, result);
    }

    [Fact]
    public void RewriteCellReferences_RewritesALayoutInstanceCellRef()
    {
        var oldCell = CreateCell("oldCell");
        var cellA   = CreateCell("cellA");
        SaveLayoutWithCellRef(cellA, "../../oldCell");

        var updated = CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out var failed);

        Assert.Empty(failed);
        Assert.Single(updated);

        var layDir  = CellFolder.SubFolderPath(cellA, ViewType.Layout);
        var clayPath = Path.Combine(layDir, "cellA.clay");
        var reloaded = LayoutPersistence.LoadFromFile(clayPath);
        Assert.Equal("../../newCell", reloaded.Instances[0].CellRef);
    }

    [Fact]
    public void RewriteCellReferences_TouchesBothSchematicAndLayoutWhenBothReferenceTheOldName()
    {
        CreateCell("oldCell");
        var cellA = CreateCell("cellA");
        SaveSchematicWithCellRef(cellA, "../../oldCell");
        SaveLayoutWithCellRef(cellA, "../../oldCell");

        var updated = CellUsageScanner.RewriteCellReferences(_ws, "oldCell", "newCell", out var failed);

        Assert.Empty(failed);
        Assert.Equal(2, updated.Count);   // one .csch + one .clay

        var schDir = CellFolder.SubFolderPath(cellA, ViewType.Schematic);
        var (schModel, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(schDir, "cellA.csch"));
        Assert.Equal("../../newCell", schModel.Components[0].CellRef);

        var layDir = CellFolder.SubFolderPath(cellA, ViewType.Layout);
        var layModel = LayoutPersistence.LoadFromFile(Path.Combine(layDir, "cellA.clay"));
        Assert.Equal("../../newCell", layModel.Instances[0].CellRef);
    }
}
