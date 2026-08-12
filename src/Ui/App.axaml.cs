using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views;

namespace CircuitRF.Ui;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _isShuttingDown;
    private bool _launchHandled;

    // macOS: 1×1 transparent background window that keeps the menu bar alive
    // when no workspace window is open (standard macOS behaviour).
    private Window? _bgMenuWindow;

    // Settings window (macOS: opened from the app menu; Windows/Linux: from File menu).
    private Views.Dialogs.SettingsView? _appSettingsWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Dragging ANY floating dock window must not restack the others.
        //
        // Dock's HostWindow.TryBeginWindowDrag calls WindowActivationHelper.ActivateAllWindows the
        // moment a window drag begins, gated only on this flag (confirmed by decompiling
        // Dock.Avalonia 12.0.0.2, not assumed). That helper activates EVERY entry in
        // factory.HostWindows plus every DockControl's visual root — so grabbing a floating TOOL
        // panel also raised every torn-off DOCUMENT window, pulling documents that were deliberately
        // sitting behind the workspace out in front of it. Reported directly by the owner.
        //
        // Turning it off restores "dragging a window moves that window"; nothing else is affected.
        // R-dock-14 (floating TOOL panels rise with the workspace) is a SEPARATE, deliberate
        // mechanism — WorkspaceWindow.RaiseFloatingToolWindows on the shell's Activated — and is
        // untouched by this flag, which governs only Dock's own drag-begin restack.
        Dock.Settings.DockSettings.BringWindowsToFrontOnDrag = false;

        // Teach the kit importer to recognise a process's own technology files, so importing a kit
        // that carries them SAYS SO rather than listing them as unrecognised. The readers behind them
        // are UI-project code, which is why this is registered here rather than shipped as a built-in.
        Layout.TechImport.ProcessTechnologyRecognizers.RegisterOnce();

        // Register built-in .ccolor assets via AssetLoader so ThemeResolver can find them.
        ThemeResolver.SetBuiltInProvider(name =>
        {
            try
            {
                var uri = new Uri($"avares://CircuitRF.Ui/Assets/Color/{name}.ccolor");
                using var stream = AssetLoader.Open(uri);
                using var reader = new System.IO.StreamReader(stream);
                return ColorThemeIo.Load(reader.ReadToEnd());
            }
            catch { return null; }
        });

        // Apply saved theme preference before the first window is shown.
        var prefs = AppPreferencesIo.Load();
        if (prefs.ActiveThemeName is { } savedTheme)
            ThemeService.Active = ThemeResolver.Resolve(savedTheme);

        CircuitRF.Ui.Messages.MessageDisplay.Mode =
            prefs.MessageTimestamp ?? CircuitRF.Ui.Messages.MessageTimestampMode.Time;

        // Wire CrfWarningBrush to the active color theme so Project Tree warning nodes
        // use System.Warning from the theme rather than a literal color value.
        // Also keeps ThemeService.CurrentVariant in sync so ClipboardRenderPolicy.FollowSystem works.
        UpdateCrfWarningBrush();
        ThemeService.ThemeChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);
        ActualThemeVariantChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);

        // END EVERY WORKER WHEN THIS PROCESS DOES.
        //
        // A worker is a child process, and a child does not die with its parent on any platform this
        // runs on. Nothing was ending them at exit — ResetResolved was wired only to a workspace
        // switch — so quitting circuitRF left one running per kit the design had used.
        //
        // That is not the stray-process nuisance it sounds like. On macOS a kit's worker runs inside
        // a VM, macOS allows only a small number of those at once, and a leaked one goes on holding a
        // slot indefinitely: it sits waiting for a request that can no longer arrive, because closing
        // the pipe tells the guest nothing — a virtio console has no end-of-stream to deliver. The
        // NEXT run then cannot start its VM, and is killed by the system before it can say why. So
        // the report a user gets for this is a broken pipe with no worker output at all, describing
        // neither the leak nor the run that caused it. Measured, from a leak found still running 23
        // minutes after the app that started it had gone.
        //
        // Hooked on ProcessExit rather than added to Quit() because Quit is not the only way out —
        // three paths reach Environment.Exit directly, and a fourth added later would silently not
        // be covered.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ExternalDeviceRegistry.ResetResolved();

        // The same guarantee for PCell generators, and it is needed for the same reason: a leaked
        // interpreter is invisible to the user and cannot be cleaned up by them. Clearing the
        // registry is not sufficient on its own — the WorkspaceViewModel's own resolver is what
        // holds the processes and disposes them; this is the backstop for a path that never gets to
        // a workspace reset.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CircuitRF.Ui.Layout.PCells.PCellRegistry.ClearResolvers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            if (OperatingSystem.IsMacOS())
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += (_, e) =>
                {
                    e.Cancel = true;
                    Quit();
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                ApplyMacOsDockIcon();
                WireAppMenuItems();
                BuildBgMenuWindow(desktop);
            }

            var firstWindow = new WorkspaceWindow
            {
                DataContext = new WorkspaceViewModel(),
            };
            desktop.MainWindow = firstWindow;

            WireAboutMenuItem();

            // Startup file handling (Windows/Linux argv; macOS uses Apple Events).
            var startupPaths = OperatingSystem.IsMacOS()
                ? Array.Empty<string>()
                : (desktop.Args ?? Array.Empty<string>()).Where(File.Exists).ToArray();

            if (startupPaths.Length > 0)
            {
                // Startup file args take precedence over the launch action. Deferred to Background so
                // the window is realised first — OpenFiles reaches the workspace view model, and a
                // workspace switch rebuilds the dock layout the window is showing.
                firstWindow.Show();
                var startupVm = (WorkspaceViewModel)firstWindow.DataContext!;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => OpenFiles(startupVm, startupPaths),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            else if (OperatingSystem.IsMacOS())
            {
                firstWindow.Show();
                var launchVm = (WorkspaceViewModel)firstWindow.DataContext!;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => { if (!_launchHandled) { _launchHandled = true; ApplyLaunchSettings(launchVm); } },
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            else
            {
                firstWindow.Show();
                var launchVm = (WorkspaceViewModel)firstWindow.DataContext!;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => ApplyLaunchSettings(launchVm),
                    Avalonia.Threading.DispatcherPriority.Background);
            }

            // Apple Events (macOS Finder double-click).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) => OnActivated(e, firstWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void ApplyLaunchSettings(WorkspaceViewModel vm)
    {
        try
        {
            var prefs = AppPreferencesIo.Load();
            vm.ApplyLaunchPane(prefs.LaunchPane ?? LaunchPane.Palette);
            await vm.ExecuteLaunchActionAsync(prefs.LaunchAction ?? LaunchAction.Welcome);
            vm.ApplyShowDockersOnLaunchPreference(prefs.ShowDockersOnLaunch ?? true);
        }
        catch { /* non-critical — fall back to default startup state */ }
    }

    private void OnActivated(ActivatedEventArgs e, WorkspaceWindow firstWindow)
    {
        if (e.Kind != ActivationKind.File) return;
        if (e is not FileActivatedEventArgs fileArgs) return;
        // Startup file open suppresses the launch action (fires before Background priority runs).
        _launchHandled = true;
        // Show the first window if not yet visible.
        if (!firstWindow.IsVisible)
            firstWindow.Show();

        var paths = fileArgs.Files
            .Select(f => f.Path?.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

        if (paths.Length > 0 && firstWindow.DataContext is WorkspaceViewModel vm)
            OpenFiles(vm, paths);
    }

    // Called by Program.cs named-pipe server (Windows second-instance forwarding).
    internal static void HandleExternalFiles(string[] paths)
        => (Application.Current as App)?.HandleFilesInternal(paths);

    private void HandleFilesInternal(string[] paths)
    {
        var w = _desktop?.Windows.OfType<WorkspaceWindow>().FirstOrDefault()
                ?? new WorkspaceWindow { DataContext = new WorkspaceViewModel() };
        if (!w.IsVisible) w.Show();
        if (w.DataContext is WorkspaceViewModel vm) OpenFiles(vm, paths);
    }

    /// <summary>
    /// Opens files the operating system handed us — the double-click route on every platform
    /// (R-h8-10). One dispatcher for all three arrival paths (argv, Apple Event, the Windows
    /// second-instance pipe), so a type opened by one is opened by all of them.
    ///
    /// <para><b>This finishes the <c>.crfw</c> path as well as adding <c>.charm</c>, and that is
    /// worth stating.</b> Both entry points were stubs that showed a window and ignored the paths
    /// they were handed — so double-clicking a workspace launched circuitRF and opened nothing, which
    /// looked exactly like a broken file. The <c>.charm</c> work could not be built on top of a stub,
    /// so the stub is gone rather than worked around.</para>
    ///
    /// <para>Only ONE workspace opens even if several are named: a workspace switch replaces the
    /// contents of the window, so opening a second would silently discard the first.</para>
    /// </summary>
    private static void OpenFiles(WorkspaceViewModel vm, IReadOnlyList<string> paths)
    {
        bool workspaceOpened = false;

        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".crfw":
                case ".cws":
                    if (workspaceOpened) break;
                    workspaceOpened = true;
                    vm.OpenWorkspacePath(path);
                    break;

                case ".charm":
                    vm.OpenHarmonicaPath(path);
                    break;

                // R-wbe-7 — circuitRF's Info.plist declares it a VIEWER for .wBond, so it must
                // actually open one. Declaring the document type without wiring the dispatcher is
                // precisely the "launched circuitRF and opened nothing, which looked exactly like a
                // broken file" failure this method's own note above exists to have fixed; adding a
                // type here is not optional once a plist claims it.
                //
                // Lower-cased above, so this catches the .wBond spelling the format actually uses as
                // well as the .wbond one a filesystem may hand back.
                case ".wbond":
                    vm.OpenWBondPath(path);
                    break;
            }
        }
    }

    // ---- macOS Dock icon -------------------------------------------------------

    private static void ApplyMacOsDockIcon()
    {
        try
        {
            // circuitRFIcon.icns must be present in Assets/ for this to work.
            // Without the icon file the catch block silently continues.
            const string assetUri = "avares://CircuitRF.Ui/Assets/circuitRFIcon.icns";
            using var src = AssetLoader.Open(new Uri(assetUri));
            string tmp = Path.Combine(Path.GetTempPath(), $"circuitRF_{Guid.NewGuid():N}.icns");
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            try   { MacOsSetAppIcon(tmp); }
            finally { try { File.Delete(tmp); } catch { /* ignore */ } }
        }
        catch { /* Non-critical: falls back to default icon. */ }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint ObjcGetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint ObjcSel(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint ObjcSend(nint obj, nint sel);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint ObjcSendPtr(nint obj, nint sel, nint arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint ObjcSendStr(nint obj, nint sel,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    private static void MacOsSetAppIcon(string icnsPath)
    {
        nint nsPath = ObjcSendStr(ObjcGetClass("NSString"), ObjcSel("stringWithUTF8String:"), icnsPath);
        nint icon   = ObjcSendPtr(ObjcSend(ObjcGetClass("NSImage"), ObjcSel("alloc")),
                                   ObjcSel("initWithContentsOfFile:"), nsPath);
        ObjcSendPtr(ObjcSend(ObjcGetClass("NSApplication"), ObjcSel("sharedApplication")),
                    ObjcSel("setApplicationIconImage:"), icon);
    }

    // ---- macOS app menu wiring -------------------------------------------------

    private void WireAboutMenuItem()
    {
        var appMenu = NativeMenu.GetMenu(this);
        if (appMenu is null) return;

        var aboutItem = appMenu.Items.OfType<NativeMenuItem>()
                                     .FirstOrDefault(i => i.Header == "About circuitRF…");
        if (aboutItem is not null)
            aboutItem.Click += async (_, _) =>
            {
                // Look up the active window at click time — firstWindow may never have been shown
                // (on macOS, ShowFirstWindowIfNeeded creates a different window).
                var owner = _desktop?.Windows.OfType<WorkspaceWindow>().FirstOrDefault(w => w.IsVisible);
                var about = new Views.Dialogs.AboutWindow();
                if (owner is not null)
                    await about.ShowDialog(owner);
                else
                    about.Show();
            };

        // Quit is omitted from the XAML NativeMenu — AppKit appends it automatically.
        // The ShutdownRequested handler in OnFrameworkInitializationCompleted routes it to Quit().
    }

    private void WireAppMenuItems()
    {
        var appMenu = NativeMenu.GetMenu(this);
        if (appMenu is null) return;

        var settingsItem = appMenu.Items.OfType<NativeMenuItem>()
                                        .FirstOrDefault(i => i.Header == "Settings…");
        if (settingsItem is null) return;

        settingsItem.Click += (_, _) =>
        {
            if (_appSettingsWindow is { IsVisible: true })
            {
                _appSettingsWindow.Activate();
                return;
            }
            _appSettingsWindow = new Views.Dialogs.SettingsView();
            _appSettingsWindow.Closed += (_, _) => _appSettingsWindow = null;
            var owner = (_desktop as IClassicDesktopStyleApplicationLifetime)
                            ?.Windows.FirstOrDefault();
            if (owner is not null)
                _appSettingsWindow.Show(owner);
            else
                _appSettingsWindow.Show();
        };
    }

    // ---- macOS background menu window (no-window state) -----------------------

    private void BuildBgMenuWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var newItem = new NativeMenuItem { Header = "New Workspace", Gesture = new KeyGesture(Key.N, KeyModifiers.Meta) };
        newItem.Click += (_, _) =>
        {
            var w = new WorkspaceWindow { DataContext = new WorkspaceViewModel() };
            w.Show();
        };

        var fileMenu = new NativeMenu();
        fileMenu.Items.Add(newItem);

        var bgMenu = new NativeMenu();
        bgMenu.Items.Add(new NativeMenuItem { Header = "File", Menu = fileMenu });

        _bgMenuWindow = new Window
        {
            Width = 1, Height = 1, Opacity = 0,
            CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-2, -2),
        };
        NativeMenu.SetMenu(_bgMenuWindow, bgMenu);
    }

    // ---- CrfWarningBrush (System.Warning from the active color theme) --------

    private void UpdateCrfWarningBrush()
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark
            ? ColorVariant.Dark
            : ColorVariant.Light;

        // Keep ClipboardRenderPolicy.FollowSystem in sync with the OS variant.
        ThemeService.CurrentVariant = variant;

        var rgba  = ThemeService.Active.Resolve(ColorRole.SystemWarning, variant);
        var color = Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B);
        if (Resources.TryGetResource("CrfWarningBrush", null, out var res)
            && res is SolidColorBrush brush)
            brush.Color = color;
    }

    internal bool IsShuttingDown => _isShuttingDown;

    /// <summary>Called by a WorkspaceWindow when a close/quit prompt is cancelled, so a later Quit works.</summary>
    internal void AbortQuit() => _isShuttingDown = false;

    internal void Quit()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _bgMenuWindow?.Hide();

        if (_desktop is null) { Environment.Exit(0); return; }
        var windows = _desktop.Windows.OfType<WorkspaceWindow>().ToList();
        if (windows.Count == 0) { CloseAllFloatingWindows(); Environment.Exit(0); return; }
        foreach (var w in windows) w.Close();
    }

    /// <summary>
    /// Force-closes every floating (torn-off) tool/document window still open across every
    /// WorkspaceWindow, so File->Quit actually terminates the app rather than leaving them running.
    /// Tool floats have an inert OS close box (see CrfHostWindow) to avoid a Dock teardown crash;
    /// CloseForLayoutRebuild bypasses that guard safely since the app is exiting, not rebuilding a
    /// layout. Any dirty document content in a floated window was already offered a save/discard
    /// prompt by the owning WorkspaceWindow's own OnClosing (HasAnyDirtyWork(includeFloated: true)),
    /// so nothing here is unsaved by the time this runs.
    /// </summary>
    private void CloseAllFloatingWindows()
    {
        if (_desktop is null) return;
        foreach (var w in _desktop.Windows.OfType<Window>().ToList())
        {
            if (w is WorkspaceWindow) continue;
            if (ReferenceEquals(w, _bgMenuWindow)) continue;
            try
            {
                if (w is CircuitRF.Ui.ViewModels.Dock.CrfHostWindow crf) crf.CloseForLayoutRebuild();
                else w.Close();
            }
            catch { /* best-effort during shutdown */ }
        }
    }

    internal void NotifyWindowCountChanged()
    {
        if (_desktop is null) return;
        bool anyOpen = _desktop.Windows.OfType<WorkspaceWindow>().Any();

        // Once File->Quit is in flight, finish the job the instant the last WorkspaceWindow has
        // closed (every dirty-save prompt for it AND any floated content it owns has already run) —
        // cross-platform, not just macOS: without this, a torn-off tool/document window (whose OS
        // close box is deliberately inert, see CrfHostWindow) is never touched by anything else and
        // the app never actually terminates.
        if (_isShuttingDown)
        {
            if (!anyOpen)
            {
                CloseAllFloatingWindows();
                Environment.Exit(0);
            }
            return;
        }

        // Below: macOS-only "stay resident with just the Dock icon" convention.
        if (!OperatingSystem.IsMacOS() || _bgMenuWindow is null) return;
        if (!anyOpen) { if (!_bgMenuWindow.IsVisible) { _bgMenuWindow.Show(); _bgMenuWindow.Activate(); } }
        else { if (_bgMenuWindow.IsVisible) _bgMenuWindow.Hide(); }
    }
}
