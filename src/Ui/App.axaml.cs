using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        // A ComboBox in a hidden panel must not act on a click that reaches it anyway — see the class
        // for why that locks up the whole machine rather than merely misbehaving.
        Controls.HiddenComboBoxInputGuard.Install();

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

        // Apply the theme before the first window is shown — the saved preference, or the SHIPPED
        // DEFAULT when there is none. See ThemeResolver.DefaultThemeName for why that default is a
        // wBond palette and why it changes nothing outside the wirebond editor.
        var prefs = AppPreferencesIo.Load();
        ThemeService.Active = ThemeResolver.Resolve(prefs.ActiveThemeName ?? ThemeResolver.DefaultThemeName);

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
                WireNativeMenuDispatcherBackstop();
            }

            // AFTER the backstop, deliberately: subscription order is invocation order, so
            // subscribing last is what lets the reporter read e.Handled and stay quiet about the one
            // exception the backstop above deliberately swallows.
            Diagnostics.CrashReporter.WireDispatcherLogging();

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
                // Startup file args take precedence over the launch ACTION — but not over the window
                // SHAPE. This branch used to skip ApplyLaunchSettings entirely, which threw away the
                // Window Layout and Show-Dockers preferences along with the launch action, so a file
                // opened by double-click got the default dock layout instead of the user's own. It
                // was easy to miss while only .cws/.charm/.wBond opened that way; every document type
                // does now.
                //
                // Ordered, not merely both-run: ApplyWindowLayout REBUILDS the dock, so it has to
                // happen before a document is opened into it. Deferred to Background so the window is
                // realised first — OpenFiles reaches the workspace view model, and a workspace switch
                // rebuilds the dock layout the window is showing.
                firstWindow.Show();
                var startupVm = (WorkspaceViewModel)firstWindow.DataContext!;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => { ApplyLayoutPreferences(startupVm); OpenFiles(startupVm, startupPaths); },
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

            Diagnostics.MenuBarProbe.StartIfRequested();   // opt-in; inert without CRF_MENU_DIAG

            // "The last session crashed" is announced LAST, at ApplicationIdle. Everything above can
            // still open a workspace, and a workspace open CLEARS the Messages region — announcing
            // any earlier would post the notice and then wipe it. Idle is below every priority those
            // paths post at, so it runs once they have settled. (The report itself is on disk either
            // way; Help ▸ Crash Reports… finds it whatever happens to the message.)
            var crashVm = (WorkspaceViewModel)firstWindow.DataContext!;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                crashVm.AnnouncePendingCrashReports,
                Avalonia.Threading.DispatcherPriority.ApplicationIdle);

            // Automatic updates, at the same idle priority and for the same reason: the window is
            // provably up, so the launch counter can be cleared and the retained previous version
            // released. The check itself is scheduled from here and does not run for at least a
            // minute, so it never competes with startup and never appears in a cold-start
            // measurement. With automatic updates off, no timer is even created.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => Updates.UpdateStartup.AfterFirstWindow(crashVm.Messages),
                Avalonia.Threading.DispatcherPriority.ApplicationIdle);

            // Release Notes, at the same idle priority and behind the same "the window is provably
            // up" condition — this one needs an owner to centre on. It is circuitRF's alone:
            // harmonicaRF and wBond share the preferences file but have no workspace window to open
            // it over, and a launch of either that recorded a version as seen would consume the one
            // showing circuitRF is entitled to.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ShowReleaseNotesIfDue(firstWindow),
                Avalonia.Threading.DispatcherPriority.ApplicationIdle);

            // Apple Events (macOS Finder double-click).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) => OnActivated(e, firstWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Opens the Release Notes dialog when this launch is the first one of a newly installed version.
    ///
    /// <para><b>The version is recorded as seen at the moment the dialog opens</b>, whatever it ends
    /// up showing — see <c>ReleaseNotesGate.MarkShown</c>. It is deliberately not recorded before the
    /// fetch: a launch the user quits during those few seconds has been shown nothing, and should be
    /// offered the notes again next time.</para>
    ///
    /// <para><c>async void</c> because it is a dispatcher callback, and every path inside it is
    /// wrapped: release notes are the least important thing happening at startup and must never be
    /// the reason anything else fails.</para>
    /// </summary>
    private static async void ShowReleaseNotesIfDue(Window owner)
    {
        try
        {
            // Checked before anything else, and ahead of the preview switch below: an installation
            // whose administrator forbade contacting the update host does not contact it for this
            // either, and no developer convenience may be the way around that.
            if (!Updates.ReleaseNotesGate.NetworkPermitted) return;

            Updates.ReleaseNotesDecision decision = Updates.ReleaseNotesGate.Resolve();

            // Diagnostic force-show, for looking at the dialog without reinstalling: it bypasses the
            // gate ONLY, so what appears is the real fetch of the real running version's notes.
            // Nothing is recorded on this path — a preview must not consume a real showing.
            bool preview = Environment.GetEnvironmentVariable("CRF_RELEASE_NOTES") == "1";

            if (!preview)
            {
                if (decision == Updates.ReleaseNotesDecision.None) return;

                if (decision == Updates.ReleaseNotesDecision.RecordSilently)
                {
                    Updates.ReleaseNotesGate.MarkShown(AppVersion.Display);
                    return;
                }
            }

            Updates.ReleaseNotesResult result =
                await Updates.ReleaseNotesFetcher.FetchAsync(AppVersion.Display).ConfigureAwait(true);

            if (!preview) Updates.ReleaseNotesGate.MarkShown(AppVersion.Display);

            // Shown rather than ShowDialog: it belongs over the workspace window and follows it, but
            // a modal that arrives seconds after launch would seize a window the user is already
            // working in.
            var dialog = new Views.Dialogs.ReleaseNotesDialog(result);
            dialog.Show(owner);
        }
        catch (Exception) { /* never the reason a launch is worse than it would have been */ }
    }

    private static async void ApplyLaunchSettings(WorkspaceViewModel vm)
    {
        try
        {
            var prefs = AppPreferencesIo.Load();
            vm.ApplyWindowLayout(prefs.WindowLayout ?? WindowLayout.ProjectTreeAndLibrary);
            await vm.ExecuteLaunchActionAsync(prefs.LaunchAction ?? LaunchAction.Welcome);
            vm.ApplyShowDockersOnLaunchPreference(prefs.ShowDockersOnLaunch ?? true);
        }
        catch { /* non-critical — fall back to default startup state */ }
    }

    /// <summary>
    /// The half of <see cref="ApplyLaunchSettings"/> that describes the SHAPE of the window rather
    /// than what it should open. Used by the startup paths that already know what to open — a file
    /// handed to us by the desktop — where the launch action must not run but the user's Window
    /// Layout and Show-Dockers preferences still should.
    ///
    /// <para>Synchronous on purpose. <see cref="WorkspaceViewModel.ApplyWindowLayout"/> rebuilds the
    /// dock, so it must complete before any document is opened into it, and an awaited version would
    /// make that ordering something a caller has to remember.</para>
    /// </summary>
    private static void ApplyLayoutPreferences(WorkspaceViewModel vm)
    {
        try
        {
            var prefs = AppPreferencesIo.Load();
            vm.ApplyWindowLayout(prefs.WindowLayout ?? WindowLayout.ProjectTreeAndLibrary);
            vm.ApplyShowDockersOnLaunchPreference(prefs.ShowDockersOnLaunch ?? true);
        }
        catch { /* non-critical — fall back to default startup state */ }
    }

    private void OnActivated(ActivatedEventArgs e, WorkspaceWindow firstWindow)
    {
        if (e.Kind != ActivationKind.File) return;
        if (e is not FileActivatedEventArgs fileArgs) return;
        // Startup file open suppresses the launch action (fires before Background priority runs).
        // `isLaunch` distinguishes THAT case from a later Apple Event delivered to an app that is
        // already up: only the launch case owes the window its shape, and re-applying the layout on a
        // later open would rebuild the dock out from under the documents already in it.
        bool isLaunch = !_launchHandled;
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
        {
            if (isLaunch) ApplyLayoutPreferences(vm);
            OpenFiles(vm, paths);
        }
    }

    // Called by Program.cs named-pipe server (Windows second-instance forwarding).
    internal static void HandleExternalFiles(string[] paths)
        => (Application.Current as App)?.HandleFilesInternal(paths);

    private void HandleFilesInternal(string[] paths)
    {
        var w = _desktop?.Windows.OfType<WorkspaceWindow>().FirstOrDefault()
                ?? new WorkspaceWindow { DataContext = new WorkspaceViewModel() };
        if (!w.IsVisible) w.Show();
        // The user double-clicked a file in a file manager, so the window they want is THIS one — a
        // forwarded open that leaves circuitRF behind the file manager looks like nothing happened.
        w.Activate();
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
        // Sorted into workspace-vs-documents BEFORE anything opens, rather than acted on in arrival
        // order. A workspace switch REPLACES the window's documents, so a document opened first and a
        // .cws opened second would leave the user looking at a window that discarded what they
        // double-clicked — the same "opened nothing" failure this dispatcher exists to have fixed,
        // reached by multi-selecting a .cws alongside a .csch.
        string? workspacePath = null;
        var documents = new List<string>();

        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                // Only ONE workspace opens even if several are named: a workspace switch replaces the
                // contents of the window, so opening a second would silently discard the first.
                case ".crfw":
                case ".cws":
                    workspacePath ??= path;
                    break;

                // The document types. Every one of these is claimed by the plist, the .wxs and the
                // Linux mime file, and three parity tests hold this list shut against all three — a
                // type declared to the operating system without a case here launches circuitRF and
                // opens nothing, which reads to the user as a broken file.
                //
                // Lower-cased above, so `.wbond` catches the `.wBond` spelling the format actually
                // uses as well as the one a filesystem may hand back.
                case ".csch":
                case ".clay":
                case ".csym":
                case ".cdd":
                case ".ctech":
                case ".cem":
                case ".charm":
                case ".wbond":
                    documents.Add(path);
                    break;
            }
        }

        if (workspacePath is null)
        {
            foreach (string doc in documents)
                vm.OpenDocumentByPath(doc);
            return;
        }

        _ = OpenWorkspaceThenDocumentsAsync(vm, workspacePath, documents);
    }

    /// <summary>
    /// Opens a workspace and only THEN the documents named alongside it. Awaited rather than posted:
    /// the switch is asynchronous (it can prompt to save first, and the user can cancel), and a
    /// document opened into the outgoing workspace is one the switch throws away.
    /// </summary>
    private static async Task OpenWorkspaceThenDocumentsAsync(
        WorkspaceViewModel vm, string cwsPath, IReadOnlyList<string> documents)
    {
        try   { await vm.OpenWorkspacePathAsync(cwsPath); }
        catch { /* the workspace open reports its own failure through Messages */ }

        foreach (string doc in documents)
            vm.OpenDocumentByPath(doc);
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

    /// <summary>
    /// brief-harmonicarf-r3a §2.4 — a FLOOR, not the fix; the fix is
    /// <c>HarmonicaMenuView.RecomputeAttachment</c> never handing a window a second <c>NativeMenu</c>
    /// instance in the first place (see <c>src/Ui/RESOLVED.md</c>). Even so, a queued
    /// <c>AvaloniaNativeMenuExporter.DoLayoutReset</c> that throws runs on the dispatcher, where no
    /// call-site <c>try</c>/<c>catch</c> can reach it — it takes the whole process down. This matches
    /// ONLY that specific <c>ArgumentException("The menu being updated does not match.")</c> coming out
    /// of Avalonia.Native's own menu interop, never a blanket handler — swallowing every dispatcher
    /// exception would hide real bugs and is refused.
    /// </summary>
    private static void WireNativeMenuDispatcherBackstop()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (e.Exception is not ArgumentException ex) return;
            if (!ex.Message.Contains("menu being updated does not match", StringComparison.OrdinalIgnoreCase)) return;
            if (ex.StackTrace is not { } trace || !trace.Contains("Avalonia.Native", StringComparison.Ordinal)) return;

            Console.Error.WriteLine(
                "circuitRF: swallowed a known Avalonia.Native NativeMenu exporter exception on the " +
                "dispatcher (brief-harmonicarf-r3a §2.4 — a floor, not the fix): " + ex);
            e.Handled = true;
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
