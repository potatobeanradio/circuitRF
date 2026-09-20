using Avalonia;
using Avalonia.Styling;
using CircuitRF.Render;

namespace CircuitRF.Ui.Theming;

/// <summary>
/// Settings ▸ General ▸ <b>Theme</b> — whether circuitRF follows the operating system's light/dark
/// setting or is pinned to one of them.
///
/// <para><b>Serialized as an ORDINAL</b>, like <see cref="LaunchAction"/> and
/// <see cref="CopyColorMode"/>, and the Settings buttons that set it are positional. A member is
/// <b>APPENDED, never inserted or reordered</b>: doing either silently changes what every
/// already-saved <c>preferences.json</c> means.</para>
///
/// <para>This is the light/dark VARIANT, not the colour palette. The palette — which colour each
/// drawing role gets — is the separate Color Theme tab, and every palette carries both variants; the
/// two compose rather than competing.</para>
/// </summary>
public enum AppearanceMode
{
    /// <summary>Follow the operating system, which is what circuitRF has always done.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies <see cref="AppearanceMode"/> to the running application.
///
/// <para><b>The whole mechanism is <c>Application.RequestedThemeVariant</c></b>, and that is
/// deliberate: every window, dialog and docked panel resolves its brushes from the application's
/// variant, so setting it once recolours all of them live, including windows already open. Nothing
/// here walks a window list — a mechanism that did would miss whatever window was opened next.</para>
///
/// <para>The Skia canvases are covered by the same one assignment: Avalonia raises
/// <c>ActualThemeVariantChanged</c>, each app's handler pushes the new variant into
/// <see cref="ThemeService.CurrentVariant"/>, and that raises <c>ThemeChanged</c>, which every
/// canvas in the application already listens to.</para>
/// </summary>
public static class AppearanceService
{
    /// <summary>The saved preference, or <see cref="AppearanceMode.System"/> when there is none.</summary>
    public static AppearanceMode Current => AppPreferencesIo.Load().Appearance ?? AppearanceMode.System;

    public static ThemeVariant ToVariant(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => ThemeVariant.Light,
        AppearanceMode.Dark  => ThemeVariant.Dark,
        _                    => ThemeVariant.Default,
    };

    /// <summary>
    /// Applies the saved preference to <paramref name="app"/>. Called by each of the three
    /// applications before its first window is shown, so the window is never painted in one variant
    /// and then repainted in the other.
    /// </summary>
    public static void ApplySaved(Application app) => app.RequestedThemeVariant = ToVariant(Current);

    /// <summary>Records the choice and applies it immediately — the live half of the setting.</summary>
    public static void Set(AppearanceMode mode)
    {
        AppPreferencesIo.Update(p => p.Appearance = mode);
        if (Application.Current is { } app) app.RequestedThemeVariant = ToVariant(mode);
    }
}
