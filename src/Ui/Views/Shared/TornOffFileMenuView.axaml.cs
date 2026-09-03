using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace CircuitRF.Ui.Views.Shared;

/// <summary>
/// brief-file-menu-restructure.md §4A: hidden while docked in the main shell, visible (and bound to
/// the resolved <see cref="ViewModels.WorkspaceViewModel"/>) while hosted in a torn-off
/// <see cref="ViewModels.Dock.CrfHostWindow"/> — Windows/Linux only. Re-evaluated on every attach,
/// since floating a document moves it into a fresh visual tree.
/// </summary>
public partial class TornOffFileMenuView : UserControl
{
    public TornOffFileMenuView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Deferred one frame: the owning Window may not be fully attached/assigned yet at the exact
        // moment this control's own visual-tree attachment fires (same precedent as
        // WorkspaceViewModel.TryWireHostWindowsUndo's own deferred scan).
        Dispatcher.UIThread.Post(RefreshForCurrentWindow, DispatcherPriority.Background);
    }

    private void RefreshForCurrentWindow()
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS: the app-global NativeMenu bar already covers this (its own key-window tracking,
            // via WorkspaceViewModel.TryWireWindowFocusTracking) -- an in-window Menu here would be a
            // second, redundant File menu appearing INSIDE a torn-off document window, which is wrong
            // on this platform (mirrors the main shell's own in-window Menu, which is likewise hidden
            // on macOS via IsVisible="{OnPlatform True, macOS=False}" in WorkspaceWindow.axaml).
            IsVisible = false;
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is not ViewModels.Dock.CrfHostWindow)
        {
            // Docked in the main shell (WorkspaceWindow), or in the built-in-symbol-preview
            // SymbolEditorWindow (not a real document window) — no File menu here.
            IsVisible = false;
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            IsVisible = false;
            return;
        }

        // The workspace THIS torn-off menu belongs to (MW1 R-mw1-14) — a float carries its owner's
        // stamp, so a File menu in a panel torn off window B never drives window A's workspace.
        var vm = WorkspaceLocator.For(this);

        if (vm is null)
        {
            IsVisible = false;
            return;
        }

        DataContext = vm;
        IsVisible = true;
    }
}
