using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views;

public partial class WorkspaceWindow : Window
{
    private WorkspaceViewModel? _vm;

    // The "Open Recent" NativeMenuItem declared in XAML. Looked up by walking the
    // NativeMenu tree the first time it's needed.  Using the XAML-declared instance
    // (rather than a dynamically-created one) ensures Avalonia's macOS backend has
    // already created the AppKit platform handle, so IsEnabled changes and submenu-item
    // mutations are properly synced to NSMenuItem/NSMenu at runtime.
    private NativeMenuItem? _openRecentNativeItem;

    public WorkspaceWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.RecentWorkspacesChanged -= RebuildNativeRecentMenu;
        _vm = DataContext as WorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.RecentWorkspacesChanged += RebuildNativeRecentMenu;
            RebuildNativeRecentMenu();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (App.Current as App)?.NotifyWindowCountChanged();
        // AppKit has now built the native menu from the XAML — safe to find and populate.
        RebuildNativeRecentMenu();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => (App.Current as App)?.NotifyWindowCountChanged(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // About menu item click (used for the in-window Help menu on Windows/Linux).
    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e)
    {
        await new Dialogs.AboutWindow().ShowDialog(this);
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
