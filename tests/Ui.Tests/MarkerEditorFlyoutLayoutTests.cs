// ================================================================
//  MarkerEditorFlyoutLayoutTests.cs — the narrow ComboBoxes in the marker flyout
//
//  Format, Impedance format and Norm Z share one row, and the two combo boxes are as wide as their
//  two-character contents ("mA" / "RI" / "dB") need. Two things in the Fluent ComboBox theme make a
//  combo that narrow misbehave, both measured from the live template tree rather than guessed:
//
//    * MinWidth=64 (ComboBoxThemeMinWidth) is a LOCAL value on the template's `Border#Background`.
//      A local value beats every style setter, so `ComboBox /template/ Border#Background` cannot
//      lower it; below 64 px the border measured 64 and, being Stretch-aligned in a smaller slot,
//      was arranged CENTRED at x = -15 — both side strokes outside the control. The box rendered
//      with a top and a bottom and no sides. The DynamicResource is the seam that does work.
//    * The template grid is `1*,32`, the second column being the chevron's at a fixed 32 px. Hiding
//      the glyph does not reclaim the column, so at 34 px the selected item was allotted 0 px and
//      the box read empty. The grid child holding it is the ContentControl named "ContentPresenter";
//      the `PART_ContentPresenter` name belongs to a presenter INSIDE it, which is not a child of
//      the grid — so the Grid.ColumnSpan setter aimed there had been setting it on nothing.
//
//  Both are one-line overrides that are easy to "tidy away" later, and neither fails loudly: the
//  symptom is a picture. Hence a gate.
// ================================================================

namespace CircuitRF.Ui.Tests;

public class MarkerEditorFlyoutLayoutTests
{
    private static string Xaml() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src/Ui/Views/DataDisplay/MarkerEditorView.axaml"));

    [Fact]
    public void TheFlyoutLowersTheComboBoxThemeMinWidthForItself()
    {
        Assert.Contains("x:Key=\"ComboBoxThemeMinWidth\"", Xaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheColumnSpanOverrideNamesTheGridsOwnChild()
    {
        string xaml = Xaml();
        Assert.Contains("ComboBox /template/ ContentControl#ContentPresenter", xaml, StringComparison.Ordinal);
        // ...and not the inner presenter, which is not a child of the grid and so spans nothing.
        Assert.DoesNotContain("ComboBox /template/ ContentPresenter#PART_ContentPresenter",
                              xaml, StringComparison.Ordinal);
    }

    /// <summary>Format, Impedance format and Norm Z are one row — and it is a StackPanel, which
    /// drops the spacing of a collapsed child; each of the three has its own visibility gate.</summary>
    [Fact]
    public void TheThreeFormatControlsShareOneRow()
    {
        string xaml = Xaml();
        int rowAt = xaml.IndexOf("Format, Impedance format and Norm Z share one row",
                                 StringComparison.Ordinal);
        Assert.True(rowAt >= 0, "the shared row is gone");

        string row = xaml[rowAt..];
        row = row[..row.IndexOf("<!-- Multi-marker", StringComparison.Ordinal)];
        Assert.Contains("Orientation=\"Horizontal\"", row, StringComparison.Ordinal);
        Assert.Contains("{Binding ShowFormatSelector}", row, StringComparison.Ordinal);
        Assert.Contains("{Binding ShowImpedanceFormatSelector}", row, StringComparison.Ordinal);
        Assert.Contains("{Binding ShowNormZ}", row, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
