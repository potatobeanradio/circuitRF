using System.IO;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>Gates for brief-cell-first-and-ui-fixes.md §3 (R-cc-3) — the tree's New Schematic/Symbol/
/// Layout filename suggestion.</summary>
public class ViewFileNameSuggestionTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ViewFileNameSuggestionTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void NoExistingViews_SuggestsTheBareCellName()
    {
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            Assert.Equal("Amp", ViewFileNameSuggestion.Suggest(cellDir, "Amp", ViewType.Schematic));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void OneExistingView_NamedAfterTheCell_SuggestsTheNextBareNumeral()
    {
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            File.WriteAllText(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "Amp.csch"), "");

            Assert.Equal("Amp2", ViewFileNameSuggestion.Suggest(cellDir, "Amp", ViewType.Schematic));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void TwoExistingViews_SuggestsTheThirdBareNumeral()
    {
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            var dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            File.WriteAllText(Path.Combine(dir, "Amp.csch"), "");
            File.WriteAllText(Path.Combine(dir, "Amp2.csch"), "");

            Assert.Equal("Amp3", ViewFileNameSuggestion.Suggest(cellDir, "Amp", ViewType.Schematic));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void ScansEveryFileOfTheViewType_NotJustThePrimary()
    {
        // A non-primary duplicate (Amp2.csch) must still count — checking only ResolvePrimary's own
        // answer would suggest "Amp2" again here and collide.
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            var dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            File.WriteAllText(Path.Combine(dir, "Amp.csch"), "");
            File.WriteAllText(Path.Combine(dir, "Amp2.csch"), ""); // not the primary (Amp.csch is sole? no—2 files now)

            Assert.Equal("Amp3", ViewFileNameSuggestion.Suggest(cellDir, "Amp", ViewType.Schematic));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void CellNameEndingInDigits_ContinuesWithBareNumerals_NeverUnderscore()
    {
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp2");
            var dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

            Assert.Equal("Amp2", ViewFileNameSuggestion.Suggest(cellDir, "Amp2", ViewType.Schematic));

            File.WriteAllText(Path.Combine(dir, "Amp2.csch"), "");
            var next = ViewFileNameSuggestion.Suggest(cellDir, "Amp2", ViewType.Schematic);
            Assert.Equal("Amp22", next);
            Assert.DoesNotContain("_", next);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(ViewType.Symbol, ".csym")]
    [InlineData(ViewType.Layout, ".clay")]
    public void SameBehavior_ForSymbolAndLayout_ScopedToTheirOwnViewType(ViewType viewType, string ext)
    {
        var parent = MakeTempDir();
        try
        {
            var cellDir = CellFolder.CreateCellFolder(parent, "Amp");
            var dir = CellFolder.SubFolderPath(cellDir, viewType);
            File.WriteAllText(Path.Combine(dir, "Amp" + ext), "");

            // A schematic named Amp.csch existing has no bearing on the symbol/layout suggestion —
            // each view type scans only its own sub-folder.
            var schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            File.WriteAllText(Path.Combine(schematicDir, "Amp.csch"), "");

            Assert.Equal("Amp2", ViewFileNameSuggestion.Suggest(cellDir, "Amp", viewType));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
