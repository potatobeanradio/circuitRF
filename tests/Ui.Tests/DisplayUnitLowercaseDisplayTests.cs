using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request: everywhere a DisplayUnit ComboBox appears (the Technology Editor's "Default
/// display unit for new layouts" combo and the Layout Editor's per-layout "Unit:" combo), the
/// combo's own text entries render lower-case ("nm"/"mm"/"mil"/…) instead of the raw enum
/// ToString() ("Nm"/"Mm"/"Mil"/…). Display-only — the persisted <see cref="LayoutUnit"/> value
/// itself is completely untouched (confirmed by construction: <see cref="ToLowerStringConverter"/>
/// is a one-way, view-only IValueConverter, never used on the SelectedItem binding itself, only on
/// the ItemTemplate that renders each combo entry's text).
/// </summary>
public class DisplayUnitLowercaseDisplayTests
{
    // ── The converter itself ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(LayoutUnit.Nm,   "nm")]
    [InlineData(LayoutUnit.Um,   "um")]
    [InlineData(LayoutUnit.Mm,   "mm")]
    [InlineData(LayoutUnit.Mil,  "mil")]
    [InlineData(LayoutUnit.Inch, "inch")]
    public void Convert_LowerCasesTheEnumToStringForm(LayoutUnit unit, string expected)
    {
        var result = ToLowerStringConverter.Instance.Convert(unit, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_Null_ReturnsNull()
    {
        var result = ToLowerStringConverter.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertBack_Throws_ViewOnlyNeverWritesBack()
    {
        Assert.Throws<System.NotSupportedException>(() =>
            ToLowerStringConverter.Instance.ConvertBack("nm", typeof(LayoutUnit), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PersistedValue_IsUnaffected_ConverterNeverAppliedToTheEnumItself()
    {
        // The converter only ever formats DISPLAY TEXT (see the source-scan tests below — it is
        // wired to the ItemTemplate's TextBlock, never to the SelectedItem/DefaultDisplayUnit
        // binding), so the round-tripped model value is exactly the enum, never a lower-cased
        // string standing in for it.
        var tech = new Technology { DefaultDisplayUnit = LayoutUnit.Mil };
        Assert.Equal(LayoutUnit.Mil, tech.DefaultDisplayUnit);
    }

    // ── Source-scan: both real ComboBoxes are wired to the converter, not just the converter's
    // own unit test — an AXAML-only display change like this cannot be pixel-verified headlessly
    // (matching this codebase's established `ReadRepoFile` pattern, e.g.
    // LayoutContextMenuStackingTests/AcknowledgmentsWindowTests). ──────────────────────────────

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void TechEditorView_DisplayUnitCombo_UsesTheLowerCaseConverter()
    {
        string axaml = ReadRepoFile("src/Ui/Views/Layout/TechEditorView.axaml");
        Assert.Contains("ToLowerStringConverter", axaml);
        Assert.Contains("ViewModel.DisplayUnitOptions", axaml);
        Assert.Contains("Converter={StaticResource ToLower}", axaml);
    }

    [Fact]
    public void LayoutEditorView_DisplayUnitCombo_UsesTheLowerCaseConverter()
    {
        string axaml = ReadRepoFile("src/Ui/Views/Layout/LayoutEditorView.axaml");
        Assert.Contains("ToLowerStringConverter", axaml);
        Assert.Contains("LayoutEditorViewModel.AllUnits", axaml);
        Assert.Contains("Converter={StaticResource ToLower}", axaml);
    }
}
