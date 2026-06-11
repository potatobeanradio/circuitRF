namespace CircuitRF.Ui.Theming;

/// <summary>App-wide enum controlling which color variant clipboard renders use.</summary>
public enum CopyColorMode
{
    /// <summary>Use the app's current light/dark variant (follows the OS setting).</summary>
    FollowSystem,
    /// <summary>Always render in light mode regardless of the OS or app theme.</summary>
    ForceLight,
    /// <summary>Always render in dark mode regardless of the OS or app theme.</summary>
    ForceDark,
}

/// <summary>
/// App-wide policy governing how copy-to-clipboard renders schematic content.
/// ONE global setting — never a per-call parameter.
///
/// CONTRACT FOR FUTURE COPY PATHS:
/// Every copy command (symbol-glyph copy, splotRF-plot copy, …) MUST resolve
/// variant and background through this class:
///   var (variant, transparent) = ClipboardRenderPolicy.Resolve();
///   var theme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
/// See docs/design/color-themes.md §ClipboardRenderPolicy for the full contract.
/// </summary>
public static class ClipboardRenderPolicy
{
    /// <summary>
    /// Resolves the effective ColorVariant and TransparentBackground for a clipboard render.
    /// Reads the current AppPreferences on each call so changes take effect immediately.
    /// FollowSystem uses <see cref="ThemeService.CurrentVariant"/> (kept current by App).
    /// </summary>
    public static (ColorVariant Variant, bool TransparentBackground) Resolve()
    {
        var prefs   = AppPreferencesIo.Load();
        var variant = (prefs.CopyColorMode ?? CopyColorMode.FollowSystem) switch
        {
            CopyColorMode.ForceLight => ColorVariant.Light,
            CopyColorMode.ForceDark  => ColorVariant.Dark,
            _                        => ThemeService.CurrentVariant,
        };
        return (variant, prefs.CopyTransparentBackground ?? true);
    }
}
