using System.IO;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-cell-first-and-ui-fixes.md §1 (R-cc-1): "New Cell" also creates and opens the
/// cell's primary schematic. <see cref="WorkspaceViewModel"/> itself cannot be constructed
/// headlessly (its ctor touches the Dispatcher/Avalonia app host — see src/Ui/CLAUDE.md's own
/// standing note on this), so these tests exercise the exact PRIMITIVES its
/// <c>CreateAndOpenSchematicFileAsync</c> helper composes — <see cref="CellFolder.CreateCellFolder"/>,
/// <see cref="SchematicPersistence"/>, and <see cref="CellFolder.ResolvePrimary"/> — and assert the
/// resulting on-disk state is exactly what R-cc-1 promises: a real, resolvable primary schematic.
/// </summary>
public class NewCellCreatesSchematicTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NewCellSchematicTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CellNamedSchematic_WrittenIntoSchematicSubFolder_ResolvesAsSolePrimary()
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Amp");

            // Mirrors CreateAndOpenSchematicFileAsync's own write: <CellName>.csch into schematic/.
            string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            string filePath = Path.Combine(schematicDir, "Amp" + CellFolder.ViewExtension(ViewType.Schematic));
            SchematicPersistence.SaveToFile(filePath, new SchematicEditModel(), cellName: "Amp");

            var resolution = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);

            Assert.Equal(PrimaryState.SoleFile, resolution.State);
            Assert.Equal("Amp.csch", resolution.ResolvedName);
            Assert.True(File.Exists(filePath));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CellFolderCreation_SurvivesEvenWhenTheSchematicSubFolderIsMissingAfterward()
    {
        // Simulates the R-cc-1 "failure is partial, not fatal" gate: something prevents the
        // schematic step (here, the sub-folder itself is gone) from ever succeeding, yet the cell
        // folder + .ccell created just before it are completely unaffected.
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            Directory.Delete(CellFolder.SubFolderPath(cellDir, ViewType.Schematic));

            var resolution = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);

            // The cell itself is untouched by the schematic step's own failure.
            Assert.True(Directory.Exists(cellDir));
            Assert.True(File.Exists(Path.Combine(cellDir, CellFolder.CcellFileName)));
            Assert.Equal(PrimaryState.NoView, resolution.State);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CellNameEndingInDigits_StillProducesAResolvableSolePrimary()
    {
        // R-cc-1 doesn't itself pick a suffix (that's §3's job — see the separate suffix-suggestion
        // tests) but the created cell's own name may already end in digits; confirm nothing about
        // primacy resolution is sensitive to that.
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Amp2");
            string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            string filePath = Path.Combine(schematicDir, "Amp2" + CellFolder.ViewExtension(ViewType.Schematic));
            SchematicPersistence.SaveToFile(filePath, new SchematicEditModel(), cellName: "Amp2");

            var resolution = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            Assert.Equal(PrimaryState.SoleFile, resolution.State);
            Assert.Equal("Amp2.csch", resolution.ResolvedName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
