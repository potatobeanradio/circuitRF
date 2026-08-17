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
    /// Every theme the application ships as a bundled asset, in <c>Assets/Color/</c>.
    ///
    /// <para><b>A list, because an embedded asset directory cannot be enumerated.</b> Avalonia's
    /// <c>AssetLoader</c> opens a resource by URI; there is no directory to walk, which is why
    /// <see cref="SetBuiltInProvider"/> takes a NAME and returns a theme. So the built-ins are named
    /// here and resolved there — and a name that is in this list but has no asset behind it simply
    /// falls through the resolution chain to <see cref="ColorTheme.BuiltIn"/>, which is a working
    /// theme rather than a failure.</para>
    ///
    /// <para>The six <c>wBond-…</c> entries state only the six <c>wBond.*</c> roles (owner,
    /// 2026-08-16 — alternatives to try for the wire, its start dot and its vertices). Everything
    /// else they leave unsaid, and <see cref="ColorTheme.Resolve"/>'s per-role fallback to
    /// <see cref="ColorTheme.BuiltIn"/> supplies it — so selecting one recolours the wirebond editor
    /// and leaves the schematic, the layout and harmonicaRF exactly as they were. That is the point:
    /// they are wBond palettes to judge side by side, not six whole application themes.</para>
    ///
    /// <para><b>The wire-to-accent relationship is transcribed from a pairing the owner named as
    /// working: <c>Schematic.Wire</c> and <c>Schematic.WireJunctionDot</c></b> (2026-08-16). Measured,
    /// that pair is <b>~72° apart on the colour wheel</b> with the dot roughly TWICE as saturated as
    /// the wire and a little lighter — adjacent, not complementary, and not the same hue either. The
    /// first set shipped paired each wire with its COMPLEMENT (180°) and the verdict was that the
    /// accent sat "too far different than the wBond.Wire colour". <c>wBond-Orchid</c> is that
    /// schematic pair itself, to within a rounding step; the other five apply its geometry to
    /// different wire hues.</para>
    ///
    /// <para><c>wBond.Selected</c> is deliberately outside that rule — a near-black on the light
    /// ground, a near-white on the dark one. It is a STATE, not an accent: it has to be unmistakable
    /// against the wire, the vertex and the canvas at once, and a third hue would compete with the
    /// two the theme is actually built from.</para>
    ///
    /// <para><b>No spaces in a built-in's name</b>, and that is a constraint rather than a style
    /// choice: a built-in is fetched as <c>avares://…/Assets/Color/&lt;name&gt;.ccolor</c>, and a
    /// space in a URI is percent-escaped by <see cref="Uri"/> before the asset loader ever sees it —
    /// so a theme called "wBond Copper" would be looked up as "wBond%20Copper" and silently resolve
    /// to <see cref="ColorTheme.BuiltIn"/> instead. A user's OWN theme is loaded from a file path and
    /// may be called anything.</para>
    /// </summary>
    public static IReadOnlyList<string> BuiltInThemeNames { get; } =
    [
        "Default",
        "wBond-Copper",
        "wBond-Gold",
        "wBond-Olive",
        "wBond-Orchid",
        "wBond-Steel",
        "wBond-Teal",
    ];

    /// <summary>
    /// Returns the union of theme names found in the workspace dir, the user themes dir, and the
    /// bundled <see cref="BuiltInThemeNames"/>. Always sorted; always includes "Default".
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

        foreach (string builtIn in BuiltInThemeNames) names.Add(builtIn);

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
