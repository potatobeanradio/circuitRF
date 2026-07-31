using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Technology Editor — the header and the tab strip must survive a narrow window.
//
//  Owner-reported (2026-07-30): as the window narrows, the grey "· Technology" text overlapped the
//  "Default display unit for new layouts:" label, and the four tab headers spilled onto extra rows.
//
//  Two independent causes, both structural rather than cosmetic:
//
//   1. The identity block owned the only STAR column, so it shrank toward zero — but a horizontal
//      StackPanel keeps its full desired width regardless and simply paints over the next column.
//      Nothing clipped it. The fix is a Grid whose middle cell is itself starred (so the NAME trims)
//      plus ClipToBounds as the backstop.
//
//   2. TabControl's default ItemsPanel is a WrapPanel — confirmed directly in Avalonia.Controls
//      (`DefaultPanel = new FuncTemplate<Panel>(() => new WrapPanel())`). Wrapping onto a second row
//      is the built-in behaviour, not a sizing accident, so no amount of shrinking fixes it. Only
//      replacing the panel does.
//
//  These are source scans: an .axaml layout change has no headlessly assertable rendered output, the
//  same fallback this codebase already uses for every other AXAML-only fix.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class TechEditorNarrowWidthTests
{
    private static string Axaml() =>
        RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

    // ── The tab strip ─────────────────────────────────────────────────────────

    [Fact]
    public void TabStrip_ReplacesTheDefaultWrapPanel_SoHeadersCannotSpillOntoASecondRow()
    {
        var axaml = Axaml();

        // The headline fix. Without an explicit ItemsPanel the control inherits WrapPanel and wraps.
        var i = axaml.IndexOf("<TabControl.ItemsPanel>", StringComparison.Ordinal);
        Assert.True(i >= 0, "the TabControl must declare its own ItemsPanel");

        var block = axaml[i..axaml.IndexOf("</TabControl.ItemsPanel>", i, StringComparison.Ordinal)];
        Assert.Contains("StackPanel", block);
        Assert.Contains("Orientation=\"Horizontal\"", block);
        Assert.DoesNotContain("WrapPanel", block);
    }

    [Fact]
    public void TabHeaders_PinTheirOwnSize_RatherThanInheritingThePivotScaleDefault()
    {
        var axaml = Axaml();

        // One row is only useful if it actually FITS. The theme's own TabItem metrics are sized for a
        // Pivot-style header; pinning them here keeps four short labels comfortably on one line, and
        // keeps the result independent of whatever the theme's defaults happen to be after an upgrade.
        var i = axaml.IndexOf("Selector=\"TabItem\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "the Technology Editor must scope its own TabItem style");

        var style = axaml[i..axaml.IndexOf("</Style>", i, StringComparison.Ordinal)];
        Assert.Contains("\"FontSize\" Value=", style);
        Assert.Contains("\"Padding\" Value=", style);
    }

    // ── The header row ────────────────────────────────────────────────────────

    [Fact]
    public void TechnologyName_TrimsAndClips_SoItCanNeverPaintOverTheUnitLabel()
    {
        var axaml = Axaml();

        var start = axaml.IndexOf("<Grid ColumnDefinitions=\"*,Auto,Auto,Auto,Auto\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "the header row's outer Grid must still be present");

        // The identity block occupies the one star column and must degrade rather than overflow.
        var identity = axaml[start..axaml.IndexOf("<StackPanel Grid.Column=\"1\"", start, StringComparison.Ordinal)];
        Assert.Contains("ClipToBounds=\"True\"", identity);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", identity);

        // A horizontal StackPanel here is exactly what caused the overlap — it never shrinks.
        Assert.DoesNotContain("<StackPanel Grid.Column=\"0\"", identity);
    }

    [Fact]
    public void UnitLabel_IsShortened_ButStillSaysItAppliesToNewLayoutsOnly()
    {
        var axaml = Axaml();

        Assert.DoesNotContain("Default display unit for new layouts:", axaml);

        // R-tec-4: the label must never read as a live per-layout setting, so "for new layouts" is the
        // load-bearing half of the wording and survives the shortening. A bare "Display unit:" would
        // reintroduce exactly the ambiguity that phrasing exists to prevent.
        Assert.Contains("Unit for new layouts:", axaml);
        Assert.DoesNotContain("Text=\"Display unit:\"", axaml);
    }

    [Fact]
    public void TheFullExplanation_StaysReachable_FromBothTheLabelAndTheCombo()
    {
        var axaml = Axaml();

        // Shortening the visible text moves the detail into the tooltip, so the tooltip has to be on
        // the control the user actually points at — the combo — not only on the label beside it.
        const string tip = "Seeds a newly-created layout's own display-unit choice";
        var count = System.Text.RegularExpressions.Regex.Matches(axaml, System.Text.RegularExpressions.Regex.Escape(tip)).Count;
        Assert.True(count >= 2, $"the seeding explanation should be on both the label and the combo (found {count})");
    }

    // ── Numeric key fields ────────────────────────────────────────────────────

    [Fact]
    public void LayerAndDatatypeFields_AreDigitSized_AndAllFourAgree()
    {
        var axaml = Axaml();

        // Layer and Datatype are small integers (owner: three digits is more than enough, >999 rare).
        // The Interchange tab's GDSII layer/datatype pair is the SAME quantity and must not drift to a
        // different size — one number, four fields.
        foreach (var tag in new[] { "LayerNumber", "Datatype", "GdsiiLayer", "GdsiiDatatype" })
        {
            var i = axaml.IndexOf($"Tag=\"{tag}\"", StringComparison.Ordinal);
            Assert.True(i >= 0, $"the {tag} field must still exist");

            var element = axaml[axaml.LastIndexOf("<TextBox", i, StringComparison.Ordinal)..i];
            Assert.Contains("Width=\"34\"", element);
        }
    }

    [Fact]
    public void NoColumnMinWidth_ForcesTheNumericFieldsWiderThanTheyAreSet()
    {
        var axaml = Axaml();

        // A column MinWidth larger than the field is how these got over-wide in the first place: the
        // field and the column were two hand-maintained numbers that had to agree.
        foreach (var group in new[] { "LLayer", "LDatatype", "IGLayer", "IGDatatype" })
            foreach (var start in FindAll(axaml, $"SharedSizeGroup=\"{group}\""))
            {
                var element = axaml[start..axaml.IndexOf("/>", start, StringComparison.Ordinal)];
                var m = System.Text.RegularExpressions.Regex.Match(element, "MinWidth=\"(\\d+)\"");
                if (m.Success)
                    Assert.True(int.Parse(m.Groups[1].Value) <= 34,
                        $"{group}'s MinWidth would force its field wider than the 34 px it is set to");
            }
    }

    private static System.Collections.Generic.IEnumerable<int> FindAll(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            yield return i;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
    }

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}
