using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.Views.Palette;
using Dock.Avalonia.Controls;

namespace CircuitRF.Ui.Views;


public partial class WorkspaceWindow : Window
{
    private WorkspaceViewModel? _vm;
    // Set to true once the user confirms a close/quit prompt so the second Close() call
    // (re-triggered after saving) bypasses the prompt check.
    private bool _closingConfirmed;

    // The "Open Recent" NativeMenuItem declared in XAML. Looked up by walking the
    // NativeMenu tree the first time it's needed.  Using the XAML-declared instance
    // (rather than a dynamically-created one) ensures Avalonia's macOS backend has
    // already created the AppKit platform handle, so IsEnabled changes and submenu-item
    // mutations are properly synced to NSMenuItem/NSMenu at runtime.
    private NativeMenuItem? _openRecentNativeItem;

    // The "Save All" NativeMenuItem — header is updated to "Save" when a document is active.
    private NativeMenuItem? _saveNativeItem;

    public WorkspaceWindow()
    {
        InitializeComponent();
        // Belt-and-suspenders: also set HostWindowFactory directly on the control so
        // both Dock dispatch paths (factory locator and DockControl) produce a CrfHostWindow,
        // which neutralizes the OS close box for TOOL tear-offs (whose close path crashes
        // Dock's teardown); document tear-offs still close normally.
        MainDockControl.HostWindowFactory = () => new CircuitRF.Ui.ViewModels.Dock.CrfHostWindow();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    // While a placement is armed, R / Shift+R rotate the ghost regardless of which control has focus
    // (palette tile, canvas, …). Scoped to the schematic-placement context so it never steals R from
    // the Symbol Editor (rotate primitive), a text field, or other panels. Tunnel = fires before the
    // SchematicView tunnel and the canvas bubble, so it wins when armed and they don't double-rotate.
    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null || _vm.PlacementService.Pending is null) return;  // only when armed
        if (e.Key != Key.R) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return;  // leave ⌘/Ctrl+R alone
        if (!IsPlacementKeyContext(FocusManager?.GetFocusedElement())) return;

        _vm.PlacementService.Rotate(clockwise: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    // True only when focus is inside a schematic editor or the Library Palette (and not a text field),
    // i.e. the contexts where R-as-rotate-the-ghost is the intended meaning.
    private static bool IsPlacementKeyContext(IInputElement? focused)
    {
        if (focused is TextBox) return false;            // typing — don't steal R
        if (focused is not Visual v) return false;
        return v.FindAncestorOfType<SchematicView>()   is not null
            || v.FindAncestorOfType<PaletteToolView>() is not null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RecentWorkspacesChanged -= RebuildNativeRecentMenu;
            _vm.SaveScopeChanged        -= UpdateNativeSaveHeader;
        }
        _vm = DataContext as WorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.RecentWorkspacesChanged += RebuildNativeRecentMenu;
            _vm.SaveScopeChanged        += UpdateNativeSaveHeader;
            RebuildNativeRecentMenu();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (App.Current as App)?.NotifyWindowCountChanged();
        // AppKit has now built the native menu from the XAML — safe to find and populate.
        RebuildNativeRecentMenu();
        UpdateNativeSaveHeader();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => (App.Current as App)?.NotifyWindowCountChanged(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed) return;
        if (_vm is null || !_vm.HasAnyDirtyWork()) return;
        e.Cancel = true;
        try
        {
            if (await _vm.PromptSaveBeforeClose(this, "closing"))
            {
                _vm.OnCleanExit();
                _closingConfirmed = true;
                Close();
            }
            else
            {
                // User cancelled: release the app-quit latch so a subsequent Quit isn't silently swallowed.
                (App.Current as App)?.AbortQuit();
            }
        }
        catch (Exception ex)
        {
            _vm.Messages.Error($"Couldn't complete close/save: {ex.Message}");
            (App.Current as App)?.AbortQuit();
        }
    }

    // About menu item click (used for the in-window Help menu on Windows/Linux).
    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e)
    {
        await new Dialogs.AboutWindow().ShowDialog(this);
    }

    // ---- NativeMenu "Save All" / "Save" header (macOS native menu bar) ------

    private void EnsureSaveNativeItem()
    {
        if (_saveNativeItem is not null) return;

        var rootMenu = NativeMenu.GetMenu(this);
        if (rootMenu is null) return;

        foreach (var top in rootMenu.Items)
        {
            if (top is not NativeMenuItem fileItem || fileItem.Header != "File") continue;
            if (fileItem.Menu is null) break;
            foreach (var sub in fileItem.Menu.Items)
            {
                if (sub is NativeMenuItem ni && ni.Header == "Save All")
                {
                    _saveNativeItem = ni;
                    return;
                }
            }
            break;
        }
    }

    private void UpdateNativeSaveHeader()
    {
        EnsureSaveNativeItem();
        if (_saveNativeItem is null || _vm is null) return;
        _saveNativeItem.Header = _vm.SaveMenuHeader;
    }

    // ---- NativeMenu "Open Recent" (macOS native menu bar) -------------------

    // Walks the native menu tree once to locate the XAML-declared "Open Recent" item.
    private void EnsureOpenRecentNativeItem()
    {
        if (_openRecentNativeItem is not null) return;

        var rootMenu = NativeMenu.GetMenu(this);
        if (rootMenu is null) return;

        foreach (var top in rootMenu.Items)
        {
            if (top is not NativeMenuItem fileItem || fileItem.Header != "File") continue;
            if (fileItem.Menu is null) break;
            foreach (var sub in fileItem.Menu.Items)
            {
                if (sub is NativeMenuItem ni && ni.Header == "Open Recent")
                {
                    _openRecentNativeItem = ni;
                    return;
                }
            }
            break;
        }
    }

    private void RebuildNativeRecentMenu()
    {
        EnsureOpenRecentNativeItem();
        if (_openRecentNativeItem?.Menu is not { } menu) return;

        menu.Items.Clear();

        var recents = _vm?.RecentWorkspacesList;
        if (recents is null || recents.Count == 0)
        {
            // Standard macOS pattern: keep parent enabled, show disabled placeholder.
            // Avalonia does not reliably sync IsEnabled=false back to AppKit after a
            // menu has already been shown/enabled, so we avoid that toggle entirely.
            menu.Items.Add(new NativeMenuItem("(No Recent Workspaces)") { IsEnabled = false });
            return;
        }

        foreach (var path in recents)
        {
            var workspaceDir = Path.GetDirectoryName(path);
            var name = workspaceDir is not null ? Path.GetFileName(workspaceDir) : path;
            menu.Items.Add(new NativeMenuItem(name)
            {
                Command          = _vm!.OpenRecentWorkspaceCommand,
                CommandParameter = path,
            });
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Clear Recent")
        {
            Command = _vm!.ClearRecentWorkspacesCommand,
        });
    }
}
