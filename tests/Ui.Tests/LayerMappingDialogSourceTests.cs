using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

// ── Two bugs a user hit on the same dialog, both reported from the Gerber import path ────────────
//
// LayerMappingDialog is a real Window subclass and cannot be constructed headlessly (this project's
// test suite must not call any Avalonia runtime API), so these are source-text-scan tests — the same
// shape as AcknowledgmentsWindowTests and every prior dialog-content fix here.

public class LayerMappingDialogSourceTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    /// <summary>FOUR importers share one layer-mapping bridge, and it used to name GDSII in all four —
    /// so a user importing Gerber was told, in the dialog's title AND in its body, that they were
    /// mapping GDSII layers. The format is a parameter now, and no call site may hard-code it back.</summary>
    [Fact]
    public void EveryImporterNamesItsOwnFormat_InTheSharedLayerMappingDialog()
    {
        var source = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        var formats = Regex.Matches(source, @"ResolveImportLayerMappingAsync\(window, ""(\w+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["Board", "DXF", "GDSII", "Gerber"], formats.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Import GDSII — Layer Mapping", source, StringComparison.Ordinal);
    }

    /// <summary>The dialog's five columns declare MinWidths that add up to 570 px before column
    /// spacing, the table's own margins and the window's 40 — so it needs well over the 620 it used to
    /// open at, and the "Map to" column was simply off the right edge. With the horizontal scroll bar
    /// DISABLED there was no way to reach it at all.</summary>
    [Fact]
    public void TheDialogIsWideEnoughForItsOwnColumns_AndCanScrollToThemWhenItIsNot()
    {
        var xaml = ReadRepoFile("src/Ui/Views/Dialogs/LayerMappingDialog.axaml");

        int declaredMinimum = Regex.Matches(xaml, @"SharedSizeGroup=""M\w+""\s+MinWidth=""(\d+)""")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Take(5)                                   // the header row; the item template repeats it
            .Sum();
        Assert.Equal(570, declaredMinimum);

        int width = int.Parse(Regex.Match(xaml, @"\bWidth=""(\d+)""").Groups[1].Value);
        int minWidth = int.Parse(Regex.Match(xaml, @"\bMinWidth=""(\d+)""").Groups[1].Value);

        Assert.True(minWidth >= declaredMinimum + 90,
            $"MinWidth {minWidth} does not fit the columns' own {declaredMinimum} plus spacing and margins.");
        Assert.True(width >= minWidth, $"Width {width} is below MinWidth {minWidth}.");

        Assert.Contains(@"HorizontalScrollBarVisibility=""Auto""", xaml, StringComparison.Ordinal);
    }
}
