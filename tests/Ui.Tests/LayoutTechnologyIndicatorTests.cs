using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Layout Editor technology indicator (owner, 2026-07-30).
//
//  1. The layer COUNT is gone. "PCB 2-Layer RO4350B (20mil, 1oz) · 8 layers" is self-contradictory
//     to anyone in the industry: "2-layer" is the board's physical METAL count (top and bottom
//     copper); 8 is the number of drawing layers in the .ctech. Same word, different thing.
//  2. The indicator is a two-item menu (Edit / Change Technology…), not a straight-to-Change button.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutTechnologyIndicatorTests
{
    private static LayoutView NewView() =>
        new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }

    [Fact]
    public void WithATechnology_TheReadoutIsJustItsName_NoLayerCount()
    {
        var tech = ShippedTechnologies.Load(
            ShippedTechnologies.All.First(e => e.Id.Contains("20mil", StringComparison.OrdinalIgnoreCase)));

        var vm = new LayoutEditorViewModel(NewView());
        vm.ApplyTechResolution(new TechResolution(tech, "/tmp/x.ctech", TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal(tech.Name, vm.TechSummaryText);

        // The contradiction the owner reported: a "2-Layer" board reading "· 8 layers".
        Assert.DoesNotContain("layers", vm.TechSummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCountIsStillAvailable_JustNotInTheGlanceReadout()
    {
        var tech = ShippedTechnologies.Load(
            ShippedTechnologies.All.First(e => e.Id.Contains("20mil", StringComparison.OrdinalIgnoreCase)));

        var vm = new LayoutEditorViewModel(NewView());
        vm.ApplyTechResolution(new TechResolution(tech, "/tmp/x.ctech", TechResolutionSource.WorkspaceDefault, []));

        // Removed from the readout, not from the model — a caller that wants it can still ask.
        Assert.Contains("layers", vm.LayerCountText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoTechnology_TheReadoutStillSaysWhatItFallsBackTo()
    {
        var vm = new LayoutEditorViewModel(NewView());

        // THAT is worth knowing at a glance: geometry draws in generated placeholder colours.
        Assert.Contains("No technology", vm.TechSummaryText);
        Assert.Contains("fallback", vm.TechSummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheIndicator_OffersEditAndChange_AsAFlyout_NotAComboBox()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        Assert.Contains("OnEditTechnologyClick", axaml);
        Assert.Contains("Change Technology…", axaml);

        // A MenuFlyout on the existing borderless button keeps the metadata bar's footprint
        // unchanged; a ComboBox would be far taller (owner's explicit constraint).
        Assert.Contains("<MenuFlyout", axaml);
    }

    // ── Unit and Snap combos are the same size, left-aligned (owner, 2026-07-30) ──

    [Fact]
    public void UnitAndSnapCombos_ShareOneSizingStyle_SoTheyCannotDrift()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        // One style, applied to both — rather than two hand-maintained sets of numbers, which is how
        // they came to differ in the first place (Snap had MinWidth=90, Unit had none).
        Assert.Contains("Selector=\"ComboBox.metaCombo\"", axaml);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(axaml, "Classes=\"metaCombo\"").Count);
    }

    [Fact]
    public void NeitherCombo_SetsItsOwnFontSizeOrPadding_SoThoseCannotDiverge()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        // A local value beats a style setter in Avalonia, so a stray FontSize/Padding on either combo
        // would silently un-match them. Width is EXEMPT on purpose: Snap holds values like "25.4 mil"
        // and is allowed to be wider than Unit (owner's call), so each sets its own.
        foreach (var start in FindAll(axaml, "<ComboBox Classes=\"metaCombo\""))
        {
            var element = axaml[start..(axaml.IndexOf('>', start) + 1)];
            Assert.DoesNotContain("FontSize=", element);
            Assert.DoesNotContain("Padding=", element);
        }
    }

    [Fact]
    public void TheSharedStyle_SetsOneSmallFontSize_ForBothCombos()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        var i = axaml.IndexOf("Selector=\"ComboBox.metaCombo\"", StringComparison.Ordinal);
        var style = axaml[i..axaml.IndexOf("</Style>", i, StringComparison.Ordinal)];

        var m = System.Text.RegularExpressions.Regex.Match(style, "FontSize\" Value=\"(\\d+)\"");
        Assert.True(m.Success, "the shared style must set a font size");
        Assert.True(int.Parse(m.Groups[1].Value) <= 11,
            "the combos are meant to be small — no larger than the surrounding 11pt labels");
    }

    [Fact]
    public void TheSharedStyle_LeftAlignsContent_TheUnitCombosOriginalComplaint()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        var i = axaml.IndexOf("Selector=\"ComboBox.metaCombo\"", StringComparison.Ordinal);
        var style = axaml[i..axaml.IndexOf("</Style>", i, StringComparison.Ordinal)];

        // A non-editable ComboBox centres its selected content by default — which is exactly why the
        // Unit combo's text sat centred while the editable Snap combo's sat left.
        Assert.Contains("HorizontalContentAlignment", style);
        Assert.Contains("Left", style);
    }

    [Fact]
    public void TheSharedStyle_PinsOneHeight_ForBothCombos_TheReportedMismatch()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        var i = axaml.IndexOf("Selector=\"ComboBox.metaCombo\"", StringComparison.Ordinal);
        var style = axaml[i..axaml.IndexOf("</Style>", i, StringComparison.Ordinal)];

        // The owner-reported bug: the two combos occupied different vertical space. They CANNOT match
        // by sharing Padding alone — the editable one (Snap) is templated around an inner TextBox with
        // its own padding and min-height, while the non-editable one (Unit) is a plain ContentPresenter.
        // Pinning Height explicitly is what actually makes the two templates agree.
        Assert.Contains("\"Height\" Value=", style);
    }

    [Fact]
    public void NeitherCombo_PinsItsOwnWidth_SoTheTemplateCannotClipItsContent()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        // Width is a CAP; the ComboBox template reserves space for the drop-down chevron, so a tight
        // Width clips the content presenter on both sides (the reported "cut off on the left and right").
        // Widths are expressed as MinWidth — a floor, never a cap.
        foreach (var start in FindAll(axaml, "<ComboBox Classes=\"metaCombo\""))
        {
            var element = axaml[start..axaml.IndexOf('>', start)];
            Assert.DoesNotContain(" Width=", element);
            Assert.Contains("MinWidth=", element);
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
}
