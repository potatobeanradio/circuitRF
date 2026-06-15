using System.IO;
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
}
