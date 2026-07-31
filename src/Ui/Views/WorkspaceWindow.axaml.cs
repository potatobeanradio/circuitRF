using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

    // The "Hide Dockers"/"Show Dockers" NativeMenuItem — a NativeMenuItem is an AvaloniaObject with
    // no DataContext, so its header cannot bind and is relabelled here instead.
    private NativeMenuItem? _dockersNativeItem;

    public WorkspaceWindow()
    {
        InitializeComponent();
        // Belt-and-suspenders: also set HostWindowFactory directly on the control so
        // both Dock dispatch paths (factory locator and DockControl) produce a CrfHostWindow,
        // which neutralizes the OS close box for TOOL tear-offs (whose close path crashes
        // Dock's teardown); document tear-offs still close normally.
        MainDockControl.HostWindowFactory = () => new CircuitRF.Ui.ViewModels.Dock.CrfHostWindow();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
        // R-dock-14: bring the floating tool panels forward with the workspace. `Window` exposes no
        // OnActivated to override, so this is the event.
        Activated += (_, _) =>
        {
            RaiseFloatingToolWindows();
            // Cheap (a handful of windows) and idempotent; RaiseFloatingToolWindows re-entering
            // Activated simply rebuilds again, which is harmless.
            _vm?.RebuildWindowMenuItems();
        };
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
            _vm.WindowMenuChanged       -= RebuildNativeWindowMenu;
            _vm.SaveScopeChanged        -= UpdateNativeSaveHeader;
            _vm.DockersCollapsedChanged -= UpdateNativeDockersHeader;
        }
        _vm = DataContext as WorkspaceViewModel;
        if (_vm is not null)
        {
            _vm.RecentWorkspacesChanged += RebuildNativeRecentMenu;
            // Declared for exactly this and previously left unsubscribed, which is why the macOS
            // Window menu never updated after the one build in OnOpened.
            _vm.WindowMenuChanged       += RebuildNativeWindowMenu;
            _vm.SaveScopeChanged        += UpdateNativeSaveHeader;
            _vm.DockersCollapsedChanged += UpdateNativeDockersHeader;
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
        UpdateNativeDockersHeader();

        // Locate the native Window item and hook NeedsUpdate now that AppKit has built the menu.
        EnsureWindowNativeItem();

        // Seed the Window menu now. SubmenuOpened alone is not enough: an empty ItemsSource makes
        // the parent a leaf with no submenu, so that event would never fire and the menu would stay
        // permanently empty (owner-reported). Refreshed again on Activated and on SubmenuOpened.
        _vm?.RebuildWindowMenuItems();
        RebuildNativeWindowMenu();

        // R-dock-12 baseline: the menu the OS shows when no window is key. It is NOT what makes a
        // floating window show the menu — observed directly, with this in place the owner still saw an
        // empty menu bar while a floating tool window was key. Avalonia does not fall back to the
        // application-scope menu for a key window that has none of its own; what works is attaching the
        // SAME NativeMenu instance to each floated window (WorkspaceViewModel.
        // AttachSharedNativeMenuIfMacOS, run for every float). One instance on several windows is not
        // the "duplicate menu" R-dock-12 warns against — nothing is copied, so nothing can drift.
        AttachNativeMenuAtApplicationScope();
    }

    /// <summary>
    /// R-dock-12. Deliberately NOT guarded by <c>OperatingSystem.IsMacOS()</c>: the attachment is a
    /// plain attached-property set that is inert on Windows/Linux (which have no native menu bar), and
    /// keeping it unconditional means the wiring is exercised — and testable — on every platform
    /// rather than existing only in a macOS-shaped branch nobody else ever runs.
    /// </summary>
    internal static void AttachNativeMenuAtApplicationScope(Window shell, Application? app)
    {
        var menu = NativeMenu.GetMenu(shell);
        if (menu is null || app is null) return;
        if (ReferenceEquals(NativeMenu.GetMenu(app), menu)) return;
        NativeMenu.SetMenu(app, menu);
    }

    private void AttachNativeMenuAtApplicationScope() =>
        AttachNativeMenuAtApplicationScope(this, Application.Current);

    // Re-entrancy guard for RaiseFloatingToolWindows: our own Activate() call re-raises Activated.
    private bool _raisingFloatingTools;

    /// <summary>
    /// R-dock-14: floating TOOL windows come to the front with the workspace window; torn-off
    /// DOCUMENT windows do not. R-dock-15: raising must not steal focus — the workspace stays active.
    ///
    /// <para><b>Why this is an Activated hook after all.</b> The brief preferred the owner
    /// relationship precisely to avoid one, and that was the right thing to try first — but it does
    /// not deliver the behaviour here, confirmed twice. <c>DockWindowOwnerMode.RootWindow</c> resolves
    /// to a NULL owner because our shell's root dock has no <c>IDockWindow</c> (it is hosted by a
    /// <c>DockControl</c>); <c>Default</c>, whose last-resort branch resolves the shell through
    /// <c>Factory.DockControls</c>, evidently did not resolve either — the owner reported floating
    /// panels still sitting behind other applications, and that branch also sets
    /// <c>copyOwnerChrome</c>, which would have retitled the panels with the workspace window's own
    /// title (it did not). The owner modes are kept — <c>None</c> on documents is what positively
    /// stops THEM being owned — but the raise is done explicitly.</para>
    ///
    /// <para>Activate-all-then-reactivate-the-initiator is Dock's own idiom for this
    /// (<c>WindowActivationHelper.ActivateAllWindows</c>, which it runs on every window drag); that
    /// helper is <c>internal</c>, so this is the same shape over just our tool windows.</para>
    /// </summary>
    private void RaiseFloatingToolWindows()
    {
        if (_raisingFloatingTools) return;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var tools = desktop.Windows
            .OfType<CircuitRF.Ui.ViewModels.Dock.CrfHostWindow>()
            .Where(w => w.PlatformImpl is not null && w.FloatsAnyTool())
            .ToList();

        if (tools.Count == 0) return;

        _raisingFloatingTools = true;
        try
        {
            foreach (var tool in tools)
                tool.Activate();

            // Focus comes straight back to the workspace (R-dock-15). Without this the raise would
            // hand the keyboard to whichever panel happened to be raised last.
            Activate();
        }
        catch (Exception ex)
        {
            _vm?.Messages.Warning($"Could not raise the floating panels: {ex.Message}");
        }
        finally
        {
            // Released one dispatcher pass later, NOT synchronously: the Activated events our own
            // Activate() calls produce arrive asynchronously from the platform, at a higher priority
            // than Background — so they land while the guard is still set and cannot start a raise
            // loop. Releasing here in the ordinary way would let exactly that happen.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _raisingFloatingTools = false,
                Avalonia.Threading.DispatcherPriority.Background);
        }
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
        if (_vm is null) return;

        var rootMenu = NativeMenu.GetMenu(this);
        if (rootMenu is null) return;

        foreach (var top in rootMenu.Items)
        {
            if (top is not NativeMenuItem fileItem || fileItem.Header != "File") continue;
            if (fileItem.Menu is null) break;
            foreach (var sub in fileItem.Menu.Items)
            {
                // brief-file-menu-restructure.md: matched by Command identity, not Header text — the
                // item's literal XAML header is now "Save" (was "Save All"), which used to be this
                // lookup's own search key and would otherwise never match again.
                if (sub is NativeMenuItem ni && ReferenceEquals(ni.Command, _vm.SaveAllDocumentsCommand))
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

    // ---- NativeMenu "Hide/Show Dockers" header (macOS native menu bar) ------

    private void EnsureDockersNativeItem()
    {
        if (_dockersNativeItem is not null || _vm is null) return;
        _dockersNativeItem = FindNativeItemByCommand(_vm.HideShowDockersCommand, "View");
    }

    private void UpdateNativeDockersHeader()
    {
        EnsureDockersNativeItem();
        if (_dockersNativeItem is null || _vm is null) return;
        _dockersNativeItem.Header = _vm.DockersMenuHeader;
    }

    /// <summary>
    /// Locates a NativeMenuItem by COMMAND IDENTITY inside a named top-level menu. Matching by header
    /// text is what broke the Save lookup once already (this file's own note above) — a header that
    /// changes at runtime, as this one does by design, cannot also be the search key.
    /// </summary>
    private NativeMenuItem? FindNativeItemByCommand(System.Windows.Input.ICommand command, string topLevelHeader)
    {
        if (NativeMenu.GetMenu(this) is not { } rootMenu) return null;

        foreach (var top in rootMenu.Items)
        {
            if (top is not NativeMenuItem topItem || topItem.Header != topLevelHeader) continue;
            if (topItem.Menu is null) return null;
            foreach (var sub in topItem.Menu.Items)
                if (sub is NativeMenuItem ni && ReferenceEquals(ni.Command, command))
                    return ni;
            return null;
        }
        return null;
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

    /// <summary>
    /// Rebuilds the in-window Window menu just before it opens.
    ///
    /// <para>On-open rather than tracked: entries depend on window lifetime (tear-off, re-dock,
    /// close — all driven by Dock) and on per-keystroke dirty state. Subscribing to all of that would
    /// be substantial bookkeeping to keep correct something only ever read at open time.</para>
    /// </summary>
    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e)
    {
        _vm?.RebuildWindowMenuItems();
        RebuildNativeWindowMenu();
    }

    /// <summary>
    /// Mirrors the Window menu into the macOS menu bar from the SAME
    /// <c>EnumerateWindowEntries()</c> the in-window menu uses — one ordering rule, two surfaces.
    /// <c>NativeMenuItem</c> is an <c>AvaloniaObject</c> with no DataContext, so entries are built
    /// in code and their actions attached directly, exactly as "Open Recent" already does.
    /// </summary>
    private void RebuildNativeWindowMenu()
    {
        if (_vm is null) return;
        EnsureWindowNativeItem();
        if (_windowNativeItem?.Menu is not { } target) return;

        target.Items.Clear();
        foreach (var entry in _vm.EnumerateWindowEntries())
        {
            if (entry.SeparatorBefore)
                target.Items.Add(new NativeMenuItemSeparator());

            var item = new NativeMenuItem(entry.Header);
            var window = entry.Target;
            item.Click += (_, _) =>
            {
                if (window.PlatformImpl is null) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            };
            target.Items.Add(item);
        }
    }

    // XAML-declared "Window" native menu item, located once by header walk.
    private NativeMenuItem? _windowNativeItem;

    /// <summary>
    /// Locates the XAML-declared "Window" native item and hooks its just-in-time refresh.
    ///
    /// <para><b>Why NeedsUpdate matters, and why the first attempt was broken on macOS:</b> the
    /// in-window <c>Menu</c> is hidden on macOS (<c>IsVisible="{OnPlatform True, macOS=False}"</c>),
    /// so its <c>SubmenuOpened</c> — which is what drove the rebuild — never fires there at all. The
    /// native menu was therefore built exactly once, in <c>OnOpened</c>, before any workspace or
    /// floating window existed, and then never again: the owner saw a single stale "circuitRF" entry.
    /// <c>NativeMenu.NeedsUpdate</c> is Avalonia's documented hook for "add, remove or modify menu
    /// items before a menu is shown" — the macOS counterpart of SubmenuOpened.</para>
    ///
    /// <para><c>NativeMenuItem</c> is an <c>AvaloniaObject</c>, not a <c>Control</c>, so <c>x:Name</c>
    /// generates no field; the item is found by walking the native menu tree, exactly as
    /// <see cref="EnsureOpenRecentNativeItem"/> does for "Open Recent".</para>
    /// </summary>
    private void EnsureWindowNativeItem()
    {
        if (_windowNativeItem is not null) return;

        var rootMenu = NativeMenu.GetMenu(this);
        if (rootMenu is null) return;

        foreach (var top in rootMenu.Items)
        {
            if (top is NativeMenuItem ni && ni.Header == "Window" && ni.Menu is not null)
            {
                _windowNativeItem = ni;
                // Rebuild the model right before the OS shows the menu. Refreshes the VM collection
                // too, so both surfaces come from the one enumeration.
                ni.Menu.NeedsUpdate += (_, _) => _vm?.RebuildWindowMenuItems();
                break;
            }
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
