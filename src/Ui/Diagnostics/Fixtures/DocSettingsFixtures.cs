using System;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The circuitRF Settings dialog, one figure per tab.
///
/// <para><b>The real dialog, not a re-creation of it.</b> Each fixture constructs
/// <see cref="SettingsView"/>, lifts its content out (a Window cannot be hosted inside another
/// Window) and selects one tab. Everything a reader sees — the section headings, the control order,
/// the footer with Help at the leading edge — is therefore whatever the shipped XAML says it is, and
/// a control added to a tab appears in the figure the next time the docs are generated.</para>
///
/// <para><b>Populated on purpose.</b> The dialog fills its combo boxes and checkboxes from
/// <c>Loaded</c>, which never fires on a window that is never shown, so every fixture calls
/// <c>PopulateForCapture</c>. The values are first-launch defaults: <c>tools/DocGen</c> redirects the
/// per-user state directory to a throwaway one before anything reads a preference, so the figures
/// show what a new installation shows rather than whatever is set on the generating machine.</para>
/// </summary>
public static class DocSettingsFixtures
{
    /// <summary>The dialog's own declared size, less the synthetic title bar.</summary>
    public const int Width  = 720;
    public const int Height = 506;

    /// <summary>General: launch behaviour, copy/export, the export-time DRC gate, message timestamps.</summary>
    public static FigureScene General() => Tab(0);

    /// <summary>Security &amp; Permissions: what circuitRF may RUN, and what it may FETCH.</summary>
    public static FigureScene Security() => Tab(1);

    /// <summary>Color Theme: the role list, the RGBA editor and the light/dark variant toggle.</summary>
    public static FigureScene ColorTheme() => Tab(2);

    /// <summary>Wirebonds: the per-user creation defaults, and the built-in wire-clearance rule.</summary>
    public static FigureScene Wirebonds() => Tab(3);

    // ── Shared ────────────────────────────────────────────────────────────────

    private static FigureScene Tab(int index)
    {
        var dialog = new SettingsView(null);
        dialog.PopulateForCapture();

        var body = (Control)dialog.Content!;
        dialog.Content = null;

        var tabs = (body as Panel)?.Children.OfType<TabControl>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                   "SettingsView's content no longer starts with a TabControl, so a per-tab figure "
                 + "cannot select the tab it is a picture of.");

        if (index >= tabs.Items.Count)
            throw new InvalidOperationException(
                $"SettingsView has {tabs.Items.Count} tabs; this figure asks for tab {index}. A tab "
              + "was removed or reordered, and the docs page's figures no longer match its prose.");

        tabs.SelectedIndex = index;
        return new FigureScene(body);
    }
}
