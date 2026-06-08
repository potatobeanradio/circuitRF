using System;
using System.Collections.Generic;
using System.IO;

namespace CircuitRF.Ui.Theming;

/// <summary>
/// Resolves a theme name to a <see cref="ColorTheme"/> using the ordered search chain:
///   1. workspace directory   (project-local overrides)
///   2. user themes directory (per-user custom themes)
///   3. built-in provider     (bundled .ccolor assets via Avalonia AssetLoader)
///   4. <see cref="ColorTheme.BuiltIn"/> fallback (always succeeds)
/// </summary>
public static class ThemeResolver
{
    private static Func<string, ColorTheme?>? _builtInProvider;

    public static string UserThemesDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "circuitRF", "themes");

    /// <summary>
    /// Registers the provider for bundled assets. Call once in App.axaml.cs using AssetLoader.
    /// </summary>
    public static void SetBuiltInProvider(Func<string, ColorTheme?> provider)
        => _builtInProvider = provider;

    /// <summary>
    /// Resolves <paramref name="name"/> to a theme using the four-step chain.
    /// Never throws — always returns a valid <see cref="ColorTheme"/>.
    /// </summary>
    public static ColorTheme Resolve(string name, string? workspaceDirPath = null)
    {
        // 1. workspace dir
        if (workspaceDirPath is not null)
        {
            var wsPath = Path.Combine(workspaceDirPath, name + ".ccolor");
            if (File.Exists(wsPath))
            {
                try { return ColorThemeIo.LoadFile(wsPath); } catch { }
            }
        }

        // 2. user themes dir
        var userPath = Path.Combine(UserThemesDir, name + ".ccolor");
        if (File.Exists(userPath))
        {
            try { return ColorThemeIo.LoadFile(userPath); } catch { }
        }

        // 3. built-in provider (bundled Avalonia assets)
        if (_builtInProvider is not null)
        {
            try
            {
                var built = _builtInProvider(name);
                if (built is not null) return built;
            }
            catch { }
        }

        // 4. BuiltIn fallback
        return ColorTheme.BuiltIn;
    }

    /// <summary>
    /// Returns the union of theme names found in the workspace dir, user themes dir,
    /// and the built-in "Default" preset. Always sorted; always includes "Default".
    /// </summary>
    public static IReadOnlyList<string> DiscoverThemeNames(string? workspaceDirPath = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (workspaceDirPath is not null && Directory.Exists(workspaceDirPath))
            foreach (var f in Directory.EnumerateFiles(workspaceDirPath, "*.ccolor"))
                names.Add(Path.GetFileNameWithoutExtension(f));

        if (Directory.Exists(UserThemesDir))
            foreach (var f in Directory.EnumerateFiles(UserThemesDir, "*.ccolor"))
                names.Add(Path.GetFileNameWithoutExtension(f));

        names.Add("Default");

        var list = new List<string>(names);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Saves <paramref name="theme"/> to the user themes directory.</summary>
    public static void SaveUserTheme(ColorTheme theme)
    {
        Directory.CreateDirectory(UserThemesDir);
        ColorThemeIo.SaveFile(Path.Combine(UserThemesDir, theme.Name + ".ccolor"), theme);
    }
}
