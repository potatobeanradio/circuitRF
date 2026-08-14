using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace CircuitRF.Ui.Tests.Theming;

/// <summary>
/// <c>SettingsView</c> is a <c>Window</c> and cannot be constructed headlessly in this project (see
/// <c>DrcExportGateAndPanelWiringTests</c> / <c>HarmonicaThemeWiringTests</c> for the same fallback) —
/// this pins the Color Theme tab's open-time init by reading the real source.
///
/// The Light/Dark radio and the color listing must open on whatever variant circuitRF is CURRENTLY
/// RENDERING (<see cref="CircuitRF.Ui.Theming.ThemeService.CurrentVariant"/>), not a hardcoded
/// default — otherwise the editor shows colors the user isn't looking at.
/// </summary>
public class SettingsViewColorThemeInitTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string SettingsViewXaml() =>
        ReadRepoFile("src/Ui/Views/Dialogs/SettingsView.axaml");

    private static string SettingsViewCodeBehind() =>
        ReadRepoFile("src/Ui/Views/Dialogs/SettingsView.axaml.cs");

    [Fact]
    public void LightRadio_DoesNotHardcodeIsCheckedInXaml()
    {
        string xaml = SettingsViewXaml();
        Assert.DoesNotContain(
            "Name=\"LightRadio\" Content=\"Light\" IsChecked=\"True\"",
            xaml, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OnLoaded_SetsVariantRadiosFromThemeServiceCurrentVariant_BeforeLoadingTheEditor()
    {
        string src = SettingsViewCodeBehind();

        int setDark  = src.IndexOf(
            "DarkRadio.IsChecked  = ThemeService.CurrentVariant == ColorVariant.Dark;",
            System.StringComparison.Ordinal);
        int setLight = src.IndexOf(
            "LightRadio.IsChecked = ThemeService.CurrentVariant != ColorVariant.Dark;",
            System.StringComparison.Ordinal);
        int loadEditor = src.IndexOf(
            "LoadThemeIntoEditor(ThemeService.Active);",
            System.StringComparison.Ordinal);

        Assert.True(setDark >= 0, "OnLoaded must set DarkRadio.IsChecked from ThemeService.CurrentVariant.");
        Assert.True(setLight >= 0, "OnLoaded must set LightRadio.IsChecked from ThemeService.CurrentVariant.");
        Assert.True(loadEditor >= 0, "OnLoaded must call LoadThemeIntoEditor(ThemeService.Active).");

        // The radios must be set BEFORE the editor loads, since PopulateRoleList (called by
        // LoadThemeIntoEditor) reads SelectedVariant, which reads DarkRadio.IsChecked.
        Assert.True(setDark < loadEditor);
        Assert.True(setLight < loadEditor);
    }
}
