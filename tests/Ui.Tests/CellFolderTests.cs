using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Tests for CellFolder.CreateCellFolder and CellFolder.ResolvePrimary.
/// All five primacy branches are tested per view type independently.
/// </summary>
public class CellFolderTests
{
    // ── Temp directory helpers ────────────────────────────────────────────────

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CellFolderTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFileInSubFolder(string cellDir, ViewType type, string filename)
    {
        string sub = CellFolder.SubFolderPath(cellDir, type);
        File.WriteAllText(Path.Combine(sub, filename), "");
    }

    private static void SetNamedPrimary(string cellDir, ViewType type, string? filename)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = File.Exists(ccellPath)
            ? CellPersistence.LoadFromFile(ccellPath)
            : new CcellFile();

        switch (type)
        {
            case ViewType.Schematic: ccell.PrimarySchematic = filename; break;
            case ViewType.Symbol:    ccell.PrimarySymbol    = filename; break;
            case ViewType.Layout:    ccell.PrimaryLayout    = filename; break;
        }
        CellPersistence.SaveToFile(ccellPath, ccell);
    }

    // ── CreateCellFolder ──────────────────────────────────────────────────────

    [Fact]
    public void CreateCellFolder_CreatesExpectedStructure()
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "AmpStage");

            Assert.True(Directory.Exists(cellDir));
            Assert.True(Directory.Exists(Path.Combine(cellDir, "schematic")));
            Assert.True(Directory.Exists(Path.Combine(cellDir, "symbol")));
            Assert.True(Directory.Exists(Path.Combine(cellDir, "layout")));
            Assert.True(File.Exists(Path.Combine(cellDir, ".ccell")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CreateCellFolder_InitialCcell_IsEmptyValidCell()
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "MyCell");
            var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, ".ccell"));

            Assert.Empty(ccell.Parameters);
            Assert.Null(ccell.PrimarySchematic);
            Assert.Null(ccell.PrimarySymbol);
            Assert.Null(ccell.PrimaryLayout);
            Assert.False(ccell.IsTestBench);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CreateCellFolder_ReturnsCellDirPath()
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Resistor");
            Assert.Equal(Path.Combine(parent, "Resistor"), cellDir);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CreateCellFolder_InvalidName_Throws()
    {
        var parent = MakeTempDir();
        try
        {
            Assert.Throws<ArgumentException>(() => CellFolder.CreateCellFolder(parent, "CON"));
            Assert.Throws<ArgumentException>(() => CellFolder.CreateCellFolder(parent, "bad/name"));
            Assert.Throws<ArgumentException>(() => CellFolder.CreateCellFolder(parent, ""));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Branch 5: NoView (empty sub-folder) ───────────────────────────────────

    [Theory]
    [InlineData(ViewType.Schematic)]
    [InlineData(ViewType.Symbol)]
    [InlineData(ViewType.Layout)]
    public void ResolvePrimary_EmptySubFolder_ReturnsNoView(ViewType type)
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            // Sub-folders exist but are empty — created by CreateCellFolder.
            var result = CellFolder.ResolvePrimary(cellDir, type);

            Assert.Equal(PrimaryState.NoView, result.State);
            Assert.Null(result.ResolvedName);
            Assert.Null(result.MissingName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Branch 1: SoleFile ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ViewType.Schematic, "amp.csch")]
    [InlineData(ViewType.Symbol,    "amp.csym")]
    [InlineData(ViewType.Layout,    "amp.clay")]
    public void ResolvePrimary_SoleFile_ReturnsSoleFile(ViewType type, string filename)
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            WriteFileInSubFolder(cellDir, type, filename);

            var result = CellFolder.ResolvePrimary(cellDir, type);

            Assert.Equal(PrimaryState.SoleFile, result.State);
            Assert.Equal(filename, result.ResolvedName);
            Assert.Null(result.MissingName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void ResolvePrimary_SoleFile_IgnoresCcellField()
    {
        // Even when .ccell names a DIFFERENT (non-existent) file, sole-file wins.
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            WriteFileInSubFolder(cellDir, ViewType.Symbol, "actual.csym");
            SetNamedPrimary(cellDir, ViewType.Symbol, "phantom.csym");

            var result = CellFolder.ResolvePrimary(cellDir, ViewType.Symbol);

            Assert.Equal(PrimaryState.SoleFile, result.State);
            Assert.Equal("actual.csym", result.ResolvedName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Branch 2: NamedPresent ────────────────────────────────────────────────

    [Theory]
    [InlineData(ViewType.Schematic, "amp_v1.csch", "amp_v2.csch", "amp_v2.csch")]
    [InlineData(ViewType.Symbol,    "sym_a.csym",  "sym_b.csym",  "sym_b.csym")]
    [InlineData(ViewType.Layout,    "lay1.clay",   "lay2.clay",   "lay2.clay")]
    public void ResolvePrimary_NamedPresent_ReturnsNamedPresent(
        ViewType type, string file1, string file2, string named)
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            WriteFileInSubFolder(cellDir, type, file1);
            WriteFileInSubFolder(cellDir, type, file2);
            SetNamedPrimary(cellDir, type, named);

            var result = CellFolder.ResolvePrimary(cellDir, type);

            Assert.Equal(PrimaryState.NamedPresent, result.State);
            Assert.Equal(named, result.ResolvedName);
            Assert.Null(result.MissingName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Branch 3: MissingNamedPrimary (the contradiction) ────────────────────

    [Theory]
    [InlineData(ViewType.Schematic, "amp.csch")]
    [InlineData(ViewType.Symbol,    "sym.csym")]
    [InlineData(ViewType.Layout,    "lay.clay")]
    public void ResolvePrimary_NamedPrimaryMissing_ReturnsMissingNamedPrimary(
        ViewType type, string missingFile)
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            // Add two files so it's the "multiple files" path, but neither is the named one.
            string ext = CellFolder.ViewExtension(type);
            WriteFileInSubFolder(cellDir, type, "other1" + ext);
            WriteFileInSubFolder(cellDir, type, "other2" + ext);
            SetNamedPrimary(cellDir, type, missingFile);

            var result = CellFolder.ResolvePrimary(cellDir, type);

            Assert.Equal(PrimaryState.MissingNamedPrimary, result.State);
            Assert.Equal(missingFile, result.MissingName);
            Assert.Null(result.ResolvedName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void ResolvePrimary_MissingNamedPrimary_IsDistinctFromNoPrimary()
    {
        // Ensures the contradiction state is NOT collapsed into NoPrimary.
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            WriteFileInSubFolder(cellDir, ViewType.Symbol, "a.csym");
            WriteFileInSubFolder(cellDir, ViewType.Symbol, "b.csym");
            SetNamedPrimary(cellDir, ViewType.Symbol, "gone.csym");

            var result = CellFolder.ResolvePrimary(cellDir, ViewType.Symbol);
            Assert.Equal(PrimaryState.MissingNamedPrimary, result.State);
            Assert.NotEqual(PrimaryState.NoPrimary, result.State);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Branch 4: NoPrimary (multiple files, none named) ─────────────────────

    [Theory]
    [InlineData(ViewType.Schematic)]
    [InlineData(ViewType.Symbol)]
    [InlineData(ViewType.Layout)]
    public void ResolvePrimary_MultipleFilesNoneNamed_ReturnsNoPrimary(ViewType type)
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");
            string ext = CellFolder.ViewExtension(type);
            WriteFileInSubFolder(cellDir, type, "file1" + ext);
            WriteFileInSubFolder(cellDir, type, "file2" + ext);
            // Leave .ccell primary for this type as null.

            var result = CellFolder.ResolvePrimary(cellDir, type);

            Assert.Equal(PrimaryState.NoPrimary, result.State);
            Assert.Null(result.ResolvedName);
            Assert.Null(result.MissingName);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── View types are resolved independently ─────────────────────────────────

    [Fact]
    public void ResolvePrimary_EachViewTypeResolvedIndependently()
    {
        var parent = MakeTempDir();
        try
        {
            string cellDir = CellFolder.CreateCellFolder(parent, "Cell");

            // Schematic: sole file → SoleFile
            WriteFileInSubFolder(cellDir, ViewType.Schematic, "amp.csch");

            // Symbol: two files, one named → NamedPresent
            WriteFileInSubFolder(cellDir, ViewType.Symbol, "sym_a.csym");
            WriteFileInSubFolder(cellDir, ViewType.Symbol, "sym_b.csym");
            SetNamedPrimary(cellDir, ViewType.Symbol, "sym_b.csym");

            // Layout: empty → NoView

            var sch = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            var sym = CellFolder.ResolvePrimary(cellDir, ViewType.Symbol);
            var lay = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);

            Assert.Equal(PrimaryState.SoleFile,     sch.State);
            Assert.Equal("amp.csch",                sch.ResolvedName);
            Assert.Equal(PrimaryState.NamedPresent, sym.State);
            Assert.Equal("sym_b.csym",              sym.ResolvedName);
            Assert.Equal(PrimaryState.NoView,       lay.State);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    // ── Sub-folder name and extension correctness ─────────────────────────────

    [Theory]
    [InlineData(ViewType.Schematic, "schematic", ".csch")]
    [InlineData(ViewType.Symbol,    "symbol",    ".csym")]
    [InlineData(ViewType.Layout,    "layout",    ".clay")]
    public void SubFolderName_And_ViewExtension_AreCorrect(
        ViewType type, string expectedFolder, string expectedExt)
    {
        Assert.Equal(expectedFolder, CellFolder.SubFolderName(type));
        Assert.Equal(expectedExt,    CellFolder.ViewExtension(type));
    }
}
