using System;

namespace CircuitRF.Ui.Theming;

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
}
