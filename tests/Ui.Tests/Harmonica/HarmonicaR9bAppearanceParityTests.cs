// ================================================================
//  HarmonicaR9bAppearanceParityTests.cs — brief-harmonicarf-r9b
//
//  HarmonicaAppearanceSettingsView is a UserControl and cannot be constructed headlessly in
//  Ui.Tests (the same limitation HarmonicaSetTerminationDialogTests records) — so the gate here is a
//  source scan over the .axaml, comments stripped, asserting the layout-parity shape the owner asked
//  for: a role list with a colour swatch per row, the double-click-a-swatch gesture, RGBA sliders and
//  boxes, a hex field, and no theme-combo/Save-Theme/Pick-button leftovers. A second scan pins that
//  SettingsView.axaml — the file this one was copied from — still carries the same gesture and
//  binding, since a silent divergence there is exactly what "consistent for the user" eventually
//  loses to.
// ================================================================

using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR9bAppearanceParityTests
{
    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Removes <c>&lt;!-- … --&gt;</c> and <c>//</c>-to-end-of-line spans — the same
    /// simple, string-literal-blind stripper this repo's other Harmonica source-scan tests use,
    /// extended to XML comments for the .axaml file.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 3 < src.Length && src[i] == '<' && src[i + 1] == '!' && src[i + 2] == '-' && src[i + 3] == '-')
            {
                int end = src.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 2;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    private static string ReadHarmonicaAppearanceAxaml()
        => StripComments(ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaAppearanceSettingsView.axaml"));

    // ══ the parity shape the owner asked for ═══════════════════════════════════════════════════

    [Fact]
    public void HarmonicaAppearance_HasTheDoubleTapGesture()
        => Assert.Contains("DoubleTapped=\"OnRoleDoubleTapped\"", ReadHarmonicaAppearanceAxaml(), StringComparison.Ordinal);

    [Fact]
    public void HarmonicaAppearance_HasAllFourRgbaSliders()
    {
        string axaml = ReadHarmonicaAppearanceAxaml();
        Assert.Contains("Name=\"SliderR\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"SliderG\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"SliderB\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"SliderA\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppearance_HasAllFourRgbaBoxes()
    {
        string axaml = ReadHarmonicaAppearanceAxaml();
        Assert.Contains("Name=\"LabelR\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"LabelG\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"LabelB\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"LabelA\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppearance_HasTheHexBox()
        => Assert.Contains("Name=\"HexBox\"", ReadHarmonicaAppearanceAxaml(), StringComparison.Ordinal);

    [Fact]
    public void HarmonicaAppearance_RoleListItemTemplateBindsARectangleToSwatchColor()
    {
        string axaml = ReadHarmonicaAppearanceAxaml();
        int templateStart = axaml.IndexOf("ListBox.ItemTemplate", StringComparison.Ordinal);
        Assert.True(templateStart >= 0, "ListBox.ItemTemplate not found");
        int templateEnd = axaml.IndexOf("/DataTemplate", templateStart, StringComparison.Ordinal);
        Assert.True(templateEnd >= 0, "DataTemplate close not found");
        string template = axaml[templateStart..templateEnd];

        Assert.Contains("<Rectangle", template, StringComparison.Ordinal);
        Assert.Contains("Color=\"{Binding SwatchColor}\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppearance_PickButtonIsGone()
        => Assert.DoesNotContain("PickButton", ReadHarmonicaAppearanceAxaml(), StringComparison.Ordinal);

    [Fact]
    public void HarmonicaAppearance_CarriesNoThemeComboOrSaveThemeButton()
    {
        string axaml = ReadHarmonicaAppearanceAxaml();
        Assert.DoesNotContain("ThemeCombo", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveThemeButton", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppearance_KeepsTheCcolorInterchangeRow()
    {
        string axaml = ReadHarmonicaAppearanceAxaml();
        Assert.Contains("Name=\"ImportButton\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"ExportButton\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"ResetAllButton\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppearance_CodeBehindDeletedThePrivateRoleRowRecord()
    {
        string codeBehind = StripComments(
            ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaAppearanceSettingsView.axaml.cs"));
        Assert.DoesNotContain("record RoleRow", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RoleRowModel", codeBehind, StringComparison.Ordinal);
    }

    // ══ SettingsView.axaml — the file this one was copied from — must still match ════════════════

    [Fact]
    public void SettingsView_StillHasTheDoubleTapGesture()
    {
        string axaml = StripComments(ReadSource("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"));
        Assert.Contains("DoubleTapped=\"OnRoleDoubleTapped\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Color=\"{Binding SwatchColor}\"", axaml, StringComparison.Ordinal);
    }
}
