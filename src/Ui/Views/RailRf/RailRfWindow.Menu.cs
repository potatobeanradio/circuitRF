// railRF's menu bar, the title's own context menu, and the plot's theme (owner, 2026-09-19).
//
// ── THREE REPORTS, ONE FILE, BECAUSE THEY ARE THE SAME OMISSION ───────────────────────────────
//
// This window had no menu, so Zoom to Fit existed only on the board panel's toolbar — which is
// hidden with that panel — and there was no way back to the circuitRF windows it was opened from.
// It drew its title from the view model and said nothing about WHERE the file was. And it never
// told its PlotControl which theme variant was in force, so in dark mode the axes, the grid, the
// labels, the markers and the marker info boxes were drawn in the LIGHT palette against a dark
// panel — the Match Designer's SyncPlotTheme, which this window was otherwise copied from, was the
// one line of that window it did not take.

using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// <inheritdoc cref="RailRfWindow"/>
/// </summary>
public partial class RailRfWindow : ICrfMenuWindow
{
    // ── What the menu bar's commands actually do ──────────────────────────────────────────────

    /// <summary>
    /// Gives every menu command its one line of window.
    /// </summary>
    /// <remarks>
    /// <b>Each hook calls the method the matching TOOLBAR BUTTON already calls</b>, and that is the
    /// whole rule: a menu item with a picker of its own would be a second Open, a second Export and a
    /// second Compare, drifting from the buttons beside them the first time any of the three is
    /// touched — silently, because both would keep producing a plausible dialog. The view model holds
    /// no control and cannot do any of this itself, which is why it holds hooks rather than code.
    ///
    /// <para><c>_ = XAsync()</c> is <see cref="InstallSaveHook"/>'s own spelling, for its own reason:
    /// these are <c>Task</c>-returning picker flows and the commands are synchronous. Each of them
    /// reports its own failure through the window's refusal strip rather than throwing.</para>
    /// </remarks>
    private void InstallMenuHooks(RailRfViewModel vm)
    {
        // View ▸ Zoom to Fit. The canvas is the window's, so the window is what supplies it — and it
        // is the board toolbar's own handler, not a second framing of the board.
        vm.ZoomToFitHook = () => { BoardCanvas.ZoomToFit(); BoardCanvas.Focus(); };

        // R-rail19-3a. A breakdown row names copper and the locator has to BRING IT ON SCREEN, not
        // merely light it: at fit zoom a 0.2 mm run on a 30 x 20 mm board is three pixels. The
        // camera move is the canvas's own ZoomToRegion — the same one the layout editor's
        // click-to-zoom uses, which already pads a hairline region up to a usable magnification
        // rather than clamping against MaxZoom.
        vm.ShowOnBoardHook = region => BoardCanvas.ZoomToRegion(region);

        vm.OpenDocumentHook = () => _ = OpenDocumentAsync();
        vm.ImportBoardHook  = () => _ = ImportBoardAsync();
        vm.ExportHook       = ext => _ = ExportAsync(ext);
        vm.CompareHook      = () => _ = CompareAsync();

        // Close goes through OnClosing, which is where the unsaved-work prompt lives — one window per
        // document means closing the window IS closing the document, so File ▸ Close must not be a
        // route around the question the close button already asks.
        vm.CloseHook = Close;
    }

    // ── The Window menu ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What this window is called in circuitRF's <b>own</b> Window menu.
    /// </summary>
    /// <remarks>
    /// <b>Implementing the interface is the whole registration.</b> The workspace's menu enumerates
    /// <see cref="ICrfMenuWindow"/> rather than naming concrete window types, precisely so a new
    /// standalone window is not silently missing from it — which is exactly what railRF was until
    /// now. The header is the document's own label, so an unsaved document carries its bullet here
    /// as well.
    /// </remarks>
    public string WindowMenuHeader =>
        Vm?.DocumentLabel is { Length: > 0 } label ? $"railRF — {label}" : "railRF";

    /// <summary>The XAML-declared "Window" native item, located once by a header walk.</summary>
    private NativeMenuItem? _windowNativeItem;

    /// <summary>
    /// Fills both Window-menu surfaces from one enumeration.
    /// </summary>
    /// <remarks>
    /// <b>Rebuilt on demand rather than tracked.</b> Window lifetime and dirty state both change
    /// constantly and the menu is only ever read at the moment it opens, so subscribing to all of
    /// that would be a lot of bookkeeping for a list nobody is looking at.
    /// </remarks>
    private void RebuildWindowMenu()
    {
        var entries = CrfWindowMenu.Enumerate();
        WindowMenuItem.ItemsSource = CrfWindowMenu.BuildItems(entries);
        if (_windowNativeItem?.Menu is { } native) CrfWindowMenu.Fill(native, entries);
    }

    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e) => RebuildWindowMenu();

    /// <summary>
    /// Finds the native "Window" item and hooks its just-in-time refresh.
    /// </summary>
    /// <remarks>
    /// <b><c>NeedsUpdate</c> is the macOS counterpart of <c>SubmenuOpened</c>, and on macOS it is
    /// the only hook that fires.</b> The in-window <c>Menu</c> is hidden there
    /// (<c>IsVisible="{OnPlatform True, macOS=False}"</c>), so its <c>SubmenuOpened</c> never runs
    /// at all and a native menu built once at construction would list whatever existed before this
    /// window did, for ever — which is the bug WorkspaceWindow's own note records.
    ///
    /// <para><c>NativeMenuItem</c> is an <c>AvaloniaObject</c>, not a <c>Control</c>, so
    /// <c>x:Name</c> generates no field and the item is found by walking the tree.</para>
    /// </remarks>
    private void EnsureWindowNativeItem()
    {
        if (_windowNativeItem is not null) return;
        if (NativeMenu.GetMenu(this) is not { } root) return;

        foreach (var top in root.Items)
        {
            if (top is not NativeMenuItem { Header: "Window", Menu: not null } item) continue;

            _windowNativeItem = item;
            item.Menu!.NeedsUpdate += (_, _) => RebuildWindowMenu();
            break;
        }
    }

    // ── The title's context menu ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the title's context menu: reveal this <c>.crail</c> in the platform's file manager.
    /// </summary>
    /// <remarks>
    /// <b>One <c>ContextMenu</c> instance, filled here.</b> A <c>ContextMenu</c> is a popup with its
    /// own visual root, so items built fresh on every right-click stack up — the stacking bug this
    /// application has fixed more than once, and the reason the board canvas below declares its menu
    /// the same way.
    ///
    /// <para><b>The label is the platform's</b>, through <see cref="FileReveal"/>: Finder on macOS,
    /// Explorer on Windows, File Manager elsewhere — one spelling of that decision, shared with the
    /// project tree and the document tabs rather than a fourth copy of it.</para>
    ///
    /// <para><b>A window with no file yet says so</b> rather than offering a dead item: a control
    /// that can be pressed and does nothing is indistinguishable from one that is broken, which is
    /// the line this window's Report button already draws. A file that has since been MOVED is still
    /// offered, because <see cref="FileReveal"/> opens the nearest folder that still exists — going
    /// to look is exactly why someone reaches for Reveal on a broken reference.</para>
    /// </remarks>
    private void OnTitleContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        string? path = Vm?.DocumentPath;
        var reveal = new MenuItem
        {
            Header    = FileReveal.Label,
            IsEnabled = !string.IsNullOrWhiteSpace(path)
                        && FileReveal.NearestExistingDirectory(path) is not null,
        };
        reveal.Click += (_, _) => FileReveal.Reveal(path);

        var copy = new MenuItem { Header = "Copy Path", IsEnabled = !string.IsNullOrWhiteSpace(path) };
        copy.Click += async (_, _) =>
        {
            // GetTopLevel's clipboard, and wrapped — the board canvas's own Copy Coordinate takes
            // the same shape: a clipboard that refuses is not worth an error banner on a menu item.
            try
            {
                if (path is not null && GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[railRF] Copy Path failed: {ex.Message}");
            }
        };

        var unsaved = new MenuItem
        {
            Header    = "Not saved yet — Save writes it somewhere",
            IsEnabled = false,
        };

        menu.ItemsSource = string.IsNullOrWhiteSpace(path)
            ? new Control[] { unsaved }
            : [reveal, copy];
    }

    // ── The plot's theme ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the results plot, its markers and their info boxes into the window's own light or dark
    /// palette.
    /// </summary>
    /// <remarks>
    /// <b>This is the Data Display's own palette and no railRF invention</b> — the owner's own
    /// instruction (2026-09-19): the plot in this window is a real <c>PlotControl</c> fed from a
    /// <c>DataSet</c>, so it should read as one, and a reader who knows the Data Display should
    /// recognise it. <see cref="RenderTheme"/> Light and Dark are exactly what a Data Display
    /// document uses, chosen the same way (<c>DisplayWindowViewModel.UpdateThemeFromSystem</c>).
    ///
    /// <para><b>Both halves, because they reach different things.</b>
    /// <c>DataDisplayViewModel.Theme</c> repaints every marker info box and carries the theme into
    /// anything the host renders; <c>PlotControl.PlotTheme</c> is what the control itself draws the
    /// axes, the grid, the labels and the markers with. Setting only the first leaves the plot in
    /// the light palette — which is the report — and setting only the second leaves the info boxes
    /// in it.</para>
    ///
    /// <para><b>The VARIANT, not the theme.</b> <c>ActualThemeVariantChanged</c> is light-vs-dark;
    /// <c>ThemeService.ThemeChanged</c> is a different colour theme being selected, and nothing here
    /// paints from a <c>.ccolor</c> role — the trace colours are the Data Display's own palette
    /// indices, which is the point.</para>
    /// </remarks>
    private void SyncPlotTheme()
    {
        if (Vm is not { } vm) return;

        vm.PlotHost.Theme = ActualThemeVariant == ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;

        ImpedancePlotControl.SetValue(PlotControl.PlotThemeProperty, vm.PlotHost.Theme);
    }
}
