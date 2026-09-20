using System;
using System.IO;
using Avalonia.Styling;
using CircuitRF.Render;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Theming;

/// <summary>
/// Settings ▸ General ▸ <b>Theme</b> — System / Light / Dark, applied live.
/// </summary>
public class AppearanceModeTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Src(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, "src", .. parts]));

    /// <summary>
    /// The ordinals are the file format — <c>appearance_mode</c> is serialized as a number and the
    /// Settings buttons are positional. Inserting or reordering a member silently changes what every
    /// already-saved <c>preferences.json</c> means.
    /// </summary>
    [Fact]
    public void TheOrdinalsAreTheFileFormat()
    {
        Assert.Equal(0, (int)AppearanceMode.System);
        Assert.Equal(1, (int)AppearanceMode.Light);
        Assert.Equal(2, (int)AppearanceMode.Dark);

        // System is Default, not Light: Default is what makes Avalonia follow the OS, and pinning it
        // to Light would make "System" mean "Light" on a machine set to dark.
        Assert.Equal(ThemeVariant.Default, AppearanceService.ToVariant(AppearanceMode.System));
        Assert.Equal(ThemeVariant.Light,   AppearanceService.ToVariant(AppearanceMode.Light));
        Assert.Equal(ThemeVariant.Dark,    AppearanceService.ToVariant(AppearanceMode.Dark));
    }

    /// <summary>
    /// The Skia canvases repaint. Avalonia recolours its own templated controls when the variant
    /// changes, but a <c>DrawingContext.Custom</c> operation is opaque to it — every canvas in the
    /// application instead listens to <see cref="ThemeService.ThemeChanged"/>, so the variant has to
    /// raise it or a theme switch leaves every schematic, symbol and layout in the old colours.
    /// Re-assigning the same variant raises nothing, which is what keeps App's own handler for that
    /// event (it re-assigns this) from recursing.
    /// </summary>
    [Fact]
    public void ChangingTheVariantRaisesThemeChanged_AndReassigningItDoesNot()
    {
        var entry = ThemeService.CurrentVariant;
        int raised = 0;
        void Count(object? s, EventArgs e) => raised++;

        ThemeService.ThemeChanged += Count;
        try
        {
            ThemeService.CurrentVariant = ColorVariant.Light;
            raised = 0;

            ThemeService.CurrentVariant = ColorVariant.Dark;
            Assert.Equal(1, raised);

            ThemeService.CurrentVariant = ColorVariant.Dark;
            Assert.Equal(1, raised);
        }
        finally
        {
            ThemeService.ThemeChanged -= Count;
            ThemeService.CurrentVariant = entry;
        }
    }

    /// <summary>
    /// All three applications apply the preference BEFORE their first window exists — a window shown
    /// in one variant and repainted in the other is a visible flash on every launch. Source-scanned
    /// because Ui.Tests stands up no Avalonia application.
    /// </summary>
    [Theory]
    [InlineData("App.axaml.cs")]
    [InlineData("WBondApp.axaml.cs")]
    [InlineData("HarmonicaApp.axaml.cs")]
    public void EveryApplicationAppliesTheSavedAppearanceBeforeItsFirstWindow(string file)
    {
        string src = Src("Ui", file);

        int apply = src.IndexOf("AppearanceService.ApplySaved(this)", StringComparison.Ordinal);
        Assert.True(apply >= 0, $"{file} never applies the saved Theme preference, so the setting is "
                              + "silently ignored in that binary.");

        int firstWindow = src.IndexOf("ApplicationLifetime is IClassicDesktopStyleApplicationLifetime",
                                      StringComparison.Ordinal);
        Assert.True(firstWindow > apply,
            $"{file} applies the appearance after it starts building windows — the first window is "
          + "then shown in the wrong variant and repainted, which is a flash on every launch.");
    }
}
