using System;

namespace CircuitRF.Render;

/// <summary>
/// Application-wide active color theme.
/// Setting <see cref="Active"/> fires <see cref="ThemeChanged"/> so all SchematicCanvas
/// instances re-render immediately — the live-preview contract for SettingsView.
/// </summary>
public static class ThemeService
{
    private static ColorTheme _active = ColorTheme.BuiltIn;

    public static ColorTheme Active
    {
        get => _active;
        set
        {
            _active = value;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// The color variant circuitRF is currently RENDERING in (Light or Dark) — the OS variant when
    /// the appearance preference follows the system, and the chosen one when it does not.
    /// Set by App on startup and on <c>OnActualThemeVariantChanged</c> so that
    /// <see cref="ClipboardRenderPolicy"/> can resolve FollowSystem without an Avalonia dependency.
    ///
    /// <para><b>Changing it raises <see cref="ThemeChanged"/></b>, because a variant change repaints
    /// exactly what a palette change repaints: every Skia canvas in the application resolves its
    /// colours per variant and each one already listens to that event. Avalonia repaints its own
    /// templated controls when the variant changes, but it has no way to know that a
    /// <c>DrawingContext.Custom</c> operation depended on the variant too — so without this a theme
    /// switch left every schematic, symbol, layout and wirebond canvas painted in the old colours
    /// until something else happened to invalidate it. Assigning the SAME variant raises nothing,
    /// which is what keeps App's own ThemeChanged handler (it re-assigns this) from recursing.</para>
    /// </summary>
    public static ColorVariant CurrentVariant
    {
        get => _currentVariant;
        set
        {
            if (_currentVariant == value) return;
            _currentVariant = value;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static ColorVariant _currentVariant = ColorVariant.Light;
}
