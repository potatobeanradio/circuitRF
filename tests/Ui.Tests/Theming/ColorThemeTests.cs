using System.IO;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests.Theming;

public class ColorThemeTests
{
    // ── 1. Round-trip ─────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_BuiltIn_AllRolesMatch()
    {
        var json   = ColorThemeIo.Save(ColorTheme.BuiltIn);
        var loaded = ColorThemeIo.Load(json);

        Assert.Equal("Default", loaded.Name);
        foreach (var role in ColorRole.All)
        {
            Assert.Equal(
                ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light),
                loaded.Resolve(role, ColorVariant.Light));
            Assert.Equal(
                ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark),
                loaded.Resolve(role, ColorVariant.Dark));
        }
    }

    // ── 2. Partial .ccolor — missing roles fall back to built-in defaults ─────

    [Fact]
    public void PartialCcolor_MissingRolesResolveToBuiltIn()
    {
        const string json = """
            {
              "format_version": 1,
              "name": "Partial",
              "light": {
                "Schematic.Wire": { "r": 255, "g": 0, "b": 0, "a": 255 }
              },
              "dark": {}
            }
            """;

        var theme = ColorThemeIo.Load(json);

        // Wire is overridden in light
        Assert.Equal(new Rgba(255, 0, 0), theme.Resolve(ColorRole.SchematicWire, ColorVariant.Light));

        // Background absent → built-in light default
        Assert.Equal(
            ColorTheme.BuiltIn.Resolve(ColorRole.SchematicBackground, ColorVariant.Light),
            theme.Resolve(ColorRole.SchematicBackground, ColorVariant.Light));

        // Wire absent in dark → built-in dark default
        Assert.Equal(
            ColorTheme.BuiltIn.Resolve(ColorRole.SchematicWire, ColorVariant.Dark),
            theme.Resolve(ColorRole.SchematicWire, ColorVariant.Dark));
    }

    // ── 3. format_version mismatch → ColorThemeFormatException ───────────────

    [Fact]
    public void FormatVersionMismatch_ThrowsColorThemeFormatException()
    {
        const string json = """
            {
              "format_version": 99,
              "name": "Future",
              "light": {},
              "dark": {}
            }
            """;

        Assert.Throws<ColorThemeFormatException>(() => ColorThemeIo.Load(json));
    }

    [Fact]
    public void FormatVersionMismatch_ErrorMessageMentionsVersions()
    {
        const string json = """
            { "format_version": 42, "name": "X", "light": {}, "dark": {} }
            """;

        var ex = Assert.Throws<ColorThemeFormatException>(() => ColorThemeIo.Load(json));
        Assert.Contains("42", ex.Message);
        Assert.Contains($"{ColorThemeIo.CurrentFormatVersion}", ex.Message);
    }

    // ── 4. Default preset file matches built-in table ─────────────────────────

    [Fact]
    public void DefaultPresetFile_MatchesBuiltIn()
    {
        // Default.ccolor is copied to the test output directory by the test .csproj.
        var path = Path.Combine(AppContext.BaseDirectory, "Default.ccolor");
        Assert.True(File.Exists(path), $"Default.ccolor not found at {path}");

        var loaded = ColorThemeIo.LoadFile(path);

        Assert.Equal("Default", loaded.Name);
        foreach (var role in ColorRole.All)
        {
            Assert.Equal(
                ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light),
                loaded.Resolve(role, ColorVariant.Light));
            Assert.Equal(
                ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark),
                loaded.Resolve(role, ColorVariant.Dark));
        }
    }

    // ── 5. No Id is persisted ─────────────────────────────────────────────────

    [Fact]
    public void SavedJson_ContainsNoIdField()
    {
        var json = ColorThemeIo.Save(ColorTheme.BuiltIn);
        Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
