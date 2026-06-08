using System.IO;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Theming;

public class ThemeResolverTests
{
    [Fact]
    public void Resolve_WithNoMatchesAndNoProvider_ReturnsFallback()
    {
        // No workspace dir, no files in user dir (or user dir doesn't exist in CI),
        // built-in provider not registered. Must fall back gracefully.
        var result = ThemeResolver.Resolve("__nonexistent_theme_xyz__");
        Assert.Equal(ColorTheme.BuiltIn.Name, result.Name);
    }

    [Fact]
    public void Resolve_WorkspaceDirFile_TakesPriority()
    {
        using var tmp = new TempDir();

        // Write a minimal theme to the workspace dir.
        var custom = BuildMinimalTheme("WorkspaceCustom");
        ColorThemeIo.SaveFile(Path.Combine(tmp.Path, "WorkspaceCustom.ccolor"), custom);

        var resolved = ThemeResolver.Resolve("WorkspaceCustom", tmp.Path);
        Assert.Equal("WorkspaceCustom", resolved.Name);
    }

    [Fact]
    public void Resolve_BuiltInProvider_UsedWhenNoFileMatch()
    {
        // Register a provider that recognises one theme name.
        ThemeResolver.SetBuiltInProvider(name =>
            name == "ProviderOnly" ? BuildMinimalTheme("ProviderOnly") : null);
        try
        {
            var resolved = ThemeResolver.Resolve("ProviderOnly");
            Assert.Equal("ProviderOnly", resolved.Name);
        }
        finally
        {
            // Restore no-op to avoid affecting other tests.
            ThemeResolver.SetBuiltInProvider(_ => null);
        }
    }

    [Fact]
    public void Resolve_BuiltInProvider_NotCalledWhenWorkspaceFileExists()
    {
        using var tmp  = new TempDir();
        var called = false;

        var custom = BuildMinimalTheme("Overlap");
        ColorThemeIo.SaveFile(Path.Combine(tmp.Path, "Overlap.ccolor"), custom);

        ThemeResolver.SetBuiltInProvider(name =>
        {
            if (name == "Overlap") called = true;
            return null;
        });
        try
        {
            ThemeResolver.Resolve("Overlap", tmp.Path);
            Assert.False(called, "Built-in provider should not be invoked when workspace file exists.");
        }
        finally
        {
            ThemeResolver.SetBuiltInProvider(_ => null);
        }
    }

    [Fact]
    public void DiscoverThemeNames_AlwaysIncludesDefault()
    {
        var names = ThemeResolver.DiscoverThemeNames();
        Assert.Contains("Default", names);
    }

    [Fact]
    public void DiscoverThemeNames_IncludesWorkspaceDirFiles()
    {
        using var tmp = new TempDir();
        ColorThemeIo.SaveFile(Path.Combine(tmp.Path, "MyTheme.ccolor"), BuildMinimalTheme("MyTheme"));

        var names = ThemeResolver.DiscoverThemeNames(tmp.Path);
        Assert.Contains("MyTheme", names);
    }

    [Fact]
    public void DiscoverThemeNames_IsSorted()
    {
        var names = ThemeResolver.DiscoverThemeNames();
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(sorted, names);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ColorTheme BuildMinimalTheme(string name)
    {
        var (light, dark) = ColorTheme.BuiltIn.GetRoleMaps();
        return new ColorTheme(name,
            new Dictionary<string, Rgba>(light),
            new Dictionary<string, Rgba>(dark));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
