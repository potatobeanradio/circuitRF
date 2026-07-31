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
                // Load workspace files on startup (Windows/Linux).
                // For now: just show the window; workspace loading is a stub in 6b.
                // Launch action skipped — startup file args take precedence.
                firstWindow.Show();
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
        // Workspace file loading from Apple Events: stub for 6b (6c wires it).
        _ = fileArgs; // suppress unused warning
    }

    // Called by Program.cs named-pipe server (Windows second-instance forwarding).
    internal static void HandleExternalFiles(string[] paths)
        => (Application.Current as App)?.HandleFilesInternal(paths);

    private void HandleFilesInternal(string[] paths)
    {
        // Show an existing or new workspace window and load the files (stub in 6b).
        var w = _desktop?.Windows.OfType<WorkspaceWindow>().FirstOrDefault()
                ?? new WorkspaceWindow { DataContext = new WorkspaceViewModel() };
        if (!w.IsVisible) w.Show();
        _ = paths; // stub: workspace loading wired in 6c
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
        if (windows.Count == 0) { Environment.Exit(0); return; }
        foreach (var w in windows) w.Close();
    }

    internal void NotifyWindowCountChanged()
    {
        if (!OperatingSystem.IsMacOS() || _desktop is null || _bgMenuWindow is null) return;
        bool anyOpen = _desktop.Windows.OfType<WorkspaceWindow>().Any();
        if (_isShuttingDown) { if (!anyOpen) Environment.Exit(0); return; }
        if (!anyOpen) { if (!_bgMenuWindow.IsVisible) { _bgMenuWindow.Show(); _bgMenuWindow.Activate(); } }
        else { if (_bgMenuWindow.IsVisible) _bgMenuWindow.Hide(); }
    }
}
