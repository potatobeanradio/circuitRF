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

    // Set by RelaunchAsync and read at the very last moment before the process ends. A quit that is
    // CANCELLED at a save prompt clears it again (AbortQuit), so a user who backed out is not
    // relaunched the next time they genuinely quit.
    private bool _relaunchAfterExit;

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
        // This runs on the UI thread by definition, which is the only place that fact can be captured
        // without asking Avalonia for it — and asking has a side effect. See CrashReporter.MarkUiThread.
        Diagnostics.CrashReporter.MarkUiThread();

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

        // The built-in .ccolor provider is NOT registered here any more (R-rnd1-5):
        // ThemeResolver reads the shipped Default.ccolor out of CircuitRF.Render's own
        // manifest resources by default. Registering an AssetLoader-backed one on top would
        // be a second copy of the same file reached a second way, and it was the ABSENCE of
        // this call in every headless process that made a theme silently resolve to
        // ColorTheme.BuiltIn instead. The .ccolor is still an AvaloniaResource in this
        // project (linked from src/Render), so the Settings editor's avares:// URI is unchanged.

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

            // EVERY platform, deliberately. It was first added inside the macOS-only block below,
            // because macOS is where the gap showed — but a seam that only one platform installs is a
            // platform difference, which is the thing it was added to remove.
            Views.Dialogs.SettingsView.ActiveWorkspaceDirectory = ActiveWorkspaceDirectory;

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

            // Through the same factory as every other route (R-mw1-2) — shown and given its
            // preferences by the branches below, which is what the first window's startup duties are.
            var firstWindow = CreateWorkspaceWindow();
            desktop.MainWindow = firstWindow;

            WireAboutMenuItem();

            // The Messages panel's Relaunch button can now be offered — everything it needs (the
            // desktop lifetime, the window list, the quit path) exists from here on. Installed
            // before the update check is scheduled, which is what reads it.
            Updates.RelaunchRequest.Handler = RelaunchAsync;

            // Workspaces to reopen after a deliberate relaunch, consumed once. Null on an ordinary
            // launch, which is every launch but the one immediately after the user pressed the button.
            //
            // Read BEFORE the startup-file scan and allowed to lose to it: a relaunch and a
            // double-clicked file cannot both be the reason this process started, but if they
            // somehow are, what the user just double-clicked is the more recent intent.
            string[]? restored = Updates.RelaunchSession.Take();

            // Startup file handling (Windows/Linux argv; macOS uses Apple Events).
            var startupPaths = OperatingSystem.IsMacOS()
                ? Array.Empty<string>()
                : (desktop.Args ?? Array.Empty<string>()).Where(File.Exists).ToArray();

            // A relaunch supplies its own paths, on every platform — which is exactly why they
            // travel in a file rather than in argv. macOS ignores argv for files (the line above
            // says so), and an update applied during the relaunch hands over to a THIRD process that
            // would have to carry them again. See RelaunchSession for the full reasoning.
            //
            // It joins the startup-file branch rather than getting one of its own: that branch
            // already applies the window-shape preferences without running the launch ACTION, which
            // is precisely right here. Reopening what the user had AND then opening their start-up
            // workspace over the top of it is not what "carry on where I left off" means.
            if (startupPaths.Length == 0 && restored is { Length: > 0 })
                startupPaths = restored;

            // A relaunch from a window with no workspace open: no paths, but the launch action must
            // still not run, or the user gets their start-up workspace opened for them by a restart
            // they asked for to change nothing.
            bool suppressLaunchAction = restored is not null;

            // Claimed here rather than in each branch below. On macOS this flag is what tells a
            // later Apple Event that the launch is already accounted for, and a relaunch is the one
            // case where that platform reaches the startup-PATH branch at all — which does not set
            // it, because until now it could not be reached there.
            if (suppressLaunchAction) _launchHandled = true;

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
            else if (suppressLaunchAction)
            {
                // A relaunch that had nothing open. The window gets its SHAPE and nothing else —
                // same treatment a startup file gets, for the same reason.
                firstWindow.Show();
                var restoredVm = (WorkspaceViewModel)firstWindow.DataContext!;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => ApplyLayoutPreferences(restoredVm),
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

            // "Quit circuitRF", not "Quit" — see MacOsAppMenu. Posted rather than called: the native
            // menu bar is exported from the dispatcher once the first window is up, so there is no
            // application menu to rename yet at this point in Show(). WorkspaceWindow's own Activated
            // handler renames it again after every re-export.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => MacOsAppMenu.NameQuitItem("circuitRF"),
                Avalonia.Threading.DispatcherPriority.Background);

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
    /// <para><b>What was previously recorded is the range's other end.</b> An installation that
    /// jumped several releases at once — the machine was off, or simply not launched, while two went
    /// out — gets those versions' notes beneath the running one's, so the middle of the range is not
    /// silently lost.</para>
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

            // Read BEFORE MarkShown overwrites it: this is the version whose notes were last shown,
            // and everything released between it and the running version is a release this user
            // skipped and would otherwise never see the notes for.
            string? since = Updates.ReleaseNotesGate.LastShownVersion;

            Updates.ReleaseNotesResult result =
                await Updates.ReleaseNotesFetcher.FetchAsync(AppVersion.Display, since)
                                                 .ConfigureAwait(true);

            if (!preview) Updates.ReleaseNotesGate.MarkShown(AppVersion.Display);

            // Shown rather than ShowDialog: it belongs over the workspace window and follows it, but
            // a modal that arrives seconds after launch would seize a window the user is already
            // working in.
            var dialog = new Views.Dialogs.ReleaseNotesDialog(result);
            dialog.Show(owner);

            // AND BROUGHT TO THE FRONT, which Show(owner) alone does not do (owner request,
            // 2026-09-06). An owned window is kept above its OWNER; it is not raised above the
            // application's other windows. That was invisible while there was only ever one
            // workspace window at launch — and a relaunch is precisely the case where there may be
            // several, each of which called Activate() as it was created (NewWorkspaceWindow), at
            // Background priority, which runs BEFORE the ApplicationIdle this method is posted at.
            // So the notes for the version the user has just been moved to would open behind the
            // very windows the relaunch restored.
            dialog.Activate();
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

    // ---- Window creation (MW1 R-mw1-2) ----------------------------------------

    /// <summary>
    /// The ONE place a workspace window is created.
    ///
    /// <para><b>One place, because a fourth construction site added later is exactly how the layout
    /// preference gets silently skipped for one route</b> — that has already happened once, and the
    /// note at the startup-file branch above records it. Every route that wants a workspace window
    /// comes through here: the first window, a forwarded file with nowhere to go, the macOS
    /// background menu, and File ▸ New Window.</para>
    ///
    /// <para>The window is SHOWN and its layout preferences applied; the launch ACTION is
    /// deliberately not run (R-mw1-2). Opening the user's start-up workspace into a window they asked
    /// to be empty is not what "New Window" means, and the first window's own startup duties — the
    /// crash announcement, release notes, the update check — belong to it alone.</para>
    /// </summary>
    /// <param name="workspacePath">A <c>.cws</c> to open into the new window, or null for an empty one.</param>
    /// <summary>
    /// Constructs a workspace window and nothing else — the ONE <c>new WorkspaceWindow</c> in the
    /// application. Shown, given its preferences and pointed at a workspace by
    /// <see cref="NewWorkspaceWindow"/>, or by the first window's own startup branches.
    /// </summary>
    private static WorkspaceWindow CreateWorkspaceWindow()
        => new WorkspaceWindow { DataContext = new WorkspaceViewModel() };

    internal static WorkspaceWindow NewWorkspaceWindow(
        string? workspacePath = null, string? thenOpenDocument = null)
    {
        var window = CreateWorkspaceWindow();
        window.Show();

        var vm = (WorkspaceViewModel)window.DataContext!;

        // Deferred to Background for the same reason every other route defers it: ApplyWindowLayout
        // REBUILDS the dock, so the window has to be realised first, and a workspace opened into it
        // rebuilds the layout again.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                ApplyLayoutPreferences(vm);
                if (string.IsNullOrWhiteSpace(workspacePath)) return;
                // Straight to the workspace-then-documents path rather than through OpenFiles:
                // OpenFiles classifies by the extensions three operating systems are told circuitRF
                // owns, and RC-2's follow-up document is named by circuitRF itself — a cell's own
                // `.ccell` among them, which is deliberately not an OS-registered type.
                if (string.IsNullOrWhiteSpace(thenOpenDocument) || !File.Exists(thenOpenDocument))
                    OpenFiles(vm, [workspacePath!]);
                else
                    _ = OpenWorkspaceThenDocumentsAsync(vm, workspacePath!, [thenOpenDocument!]);
            },
            Avalonia.Threading.DispatcherPriority.Background);

        window.Activate();
        return window;
    }

    /// <summary>
    /// The workspace window already showing <paramref name="workspacePath"/>, or null when none is
    /// (R-mw1-9). Compared by fully-resolved absolute path, case-insensitively — the rule
    /// <c>TechnologyCache</c> already uses, and for the same reason.
    /// </summary>
    internal static WorkspaceWindow? WindowShowing(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return null;
        if ((Application.Current as App)?._desktop is not { } desktop) return null;

        string wanted;
        try { wanted = Path.GetFullPath(workspacePath); } catch { return null; }

        foreach (var w in desktop.Windows.OfType<WorkspaceWindow>())
        {
            if (w.DataContext is not WorkspaceViewModel vm) continue;
            if (vm.CurrentWorkspacePath is not { } open) continue;

            string resolved;
            try { resolved = Path.GetFullPath(open); } catch { continue; }
            if (string.Equals(resolved, wanted, StringComparison.OrdinalIgnoreCase)) return w;
        }
        return null;
    }

    // ── RC-5's channel (revision-control.md §5.3c; R-rc5-7a, R-rc5-7b) ────────────────────────────

    /// <summary>
    /// R-rc5-7a. <b>Does a window hold unsaved changes to this workspace?</b> Asked by a headless
    /// <c>serve</c> before a batch modifies anything.
    ///
    /// <para><b>A batch that edited files under a window holding unsaved changes to them would be
    /// erased by that window's next save</b> — the two-editors-one-file failure §7A.3 already calls
    /// worse than the divergence RC-2 exists to prevent, reached through the mechanism §1.2 is the
    /// motive for. A batch is headless and there is nobody to prompt, so the honest answer is a
    /// refusal and this is the question it turns on.</para>
    ///
    /// <para><b>The workspace root is a FOLDER; the window knows its <c>.cws</c></b>, so the match is
    /// on the containing directory rather than on the file — a caller that has one has no business
    /// having to guess the other's file name.</para>
    /// </summary>
    internal static bool WorkspaceHasUnsavedChanges(string workspaceRoot)
    {
        if (WindowForWorkspaceRoot(workspaceRoot) is not { DataContext: WorkspaceViewModel vm })
            return false;

        return vm.HasAnyDirtyWork(includeFloated: false);
    }

    /// <summary>
    /// R-rc5-7b. The documents a batch changed, so the window reloads them and discards their undo
    /// stacks.
    ///
    /// <para><b>It goes through the same path a restore uses</b> (R-rc5-12b) rather than growing a
    /// second reload — two reload implementations that drift is the shape of defect §5.8 is written
    /// against — and R-rc5-7a is what makes it cheap: with nothing unsaved anywhere, a reload cannot
    /// lose anything, which is why the refusal comes first.</para>
    /// </summary>
    internal static void WorkspaceDocumentsChangedUnderneath(
        string workspaceRoot, IReadOnlyList<string> relativePaths)
    {
        if (WindowForWorkspaceRoot(workspaceRoot) is not { DataContext: WorkspaceViewModel vm }) return;
        if (relativePaths.Count == 0) return;

        _ = vm.ReloadAfterExternalChange(relativePaths);
    }

    /// <summary>The window whose open workspace lives in <paramref name="workspaceRoot"/>.</summary>
    private static WorkspaceWindow? WindowForWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
        if ((Application.Current as App)?._desktop is not { } desktop) return null;

        string wanted;
        try { wanted = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return null; }

        foreach (var w in desktop.Windows.OfType<WorkspaceWindow>())
        {
            if (w.DataContext is not WorkspaceViewModel vm) continue;
            if (vm.CurrentWorkspacePath is not { } open) continue;

            string dir;
            try { dir = Path.GetFullPath(Path.GetDirectoryName(open)!).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }

            if (string.Equals(dir, wanted, StringComparison.OrdinalIgnoreCase)) return w;
        }
        return null;
    }

    /// <summary>
    /// The workspace window most recently brought to the front. The answer to "which window did the
    /// user mean" for anything that arrives without one — a file forwarded by the operating system
    /// that no open workspace contains (R-mw1-15).
    ///
    /// <para>Recorded rather than inferred from <c>desktop.Windows</c> order, which is creation order
    /// and says nothing about what the user was last looking at.</para>
    /// </summary>
    private WorkspaceWindow? _lastActiveWorkspace;

    /// <summary>Called by <see cref="WorkspaceWindow"/> whenever it becomes active.</summary>
    internal static void NoteWorkspaceActivated(WorkspaceWindow window)
    {
        if (Application.Current is App app) app._lastActiveWorkspace = window;
    }

    /// <summary>The workspace window most recently brought to the front, for
    /// <see cref="Views.WorkspaceLocator"/>'s last-resort fallback.</summary>
    internal static WorkspaceWindow? LastActiveWorkspace => (Application.Current as App)?._lastActiveWorkspace;

    /// <summary>
    /// Which window a set of forwarded documents should open into (R-mw1-15): the one whose workspace
    /// CONTAINS them, when exactly one does; otherwise the most recently active workspace window.
    ///
    /// <para>The containment test is the same ancestor walk-up everything else uses. It is the answer
    /// the user expects — double-clicking a cell of a project already open in one window should not
    /// land it in a different project's window — and it costs nothing extra.</para>
    /// </summary>
    private WorkspaceWindow? WindowForForwardedFiles(IReadOnlyList<string> paths)
    {
        if (_desktop is null) return null;
        var windows = _desktop.Windows.OfType<WorkspaceWindow>().ToList();
        if (windows.Count == 0) return null;
        if (windows.Count == 1) return windows[0];

        var containing = new List<WorkspaceWindow>();
        foreach (string path in paths)
        {
            string? owner = WorkspaceRootFinder.WorkspaceDirOf(
                Directory.Exists(path) ? path : Path.GetDirectoryName(path));
            if (owner is null) continue;

            foreach (var w in windows)
            {
                if (w.DataContext is not WorkspaceViewModel vm) continue;
                if (vm.CurrentWorkspacePath is not { } cws) continue;
                if (Path.GetDirectoryName(cws) is not { } root) continue;
                if (!string.Equals(WorkspaceRootFinder.Normalize(root), owner, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!containing.Contains(w)) containing.Add(w);
            }
        }

        if (containing.Count == 1) return containing[0];

        // Ambiguous (documents from two open workspaces at once) or foreign to all of them — the
        // window the user was last in front of is the only defensible answer.
        return _lastActiveWorkspace is { } last && windows.Contains(last) ? last : windows[0];
    }

    /// <summary>
    /// Opens a workspace in a window of its own, or raises the window already showing it (R-mw1-9).
    ///
    /// <para><b>A workspace may be open in at most one window.</b> Two <c>WorkspaceViewModel</c>s over
    /// one <c>.cws</c> means two independent edit-session registries over the same files: two undo
    /// stacks, two dirty flags, last-save-wins. Refusing that is both correct and cheaper than
    /// reconciling it — and "activate the window that has it" is what the user meant anyway.</para>
    /// </summary>
    /// <param name="workspacePath">The <c>.cws</c> to open.</param>
    /// <param name="thenOpenDocument">
    /// RC-2 R-rc2-6: a document inside that workspace to land on once it is open. The refusal on an
    /// edit through a read-only reference offers "open the workspace that owns this" as a one-click
    /// action, and it has to arrive at the CELL — an action that opened the folder and left the
    /// designer to find the cell again is the File ▸ Open it was meant to replace. Routed through
    /// <see cref="OpenFiles"/> so the workspace is fully open before the document is asked for,
    /// which is the same ordering a desktop double-click of a workspace plus a document needs.
    /// </param>
    internal static WorkspaceWindow OpenWorkspaceInNewWindow(
        string workspacePath, string? thenOpenDocument = null)
    {
        if (WindowShowing(workspacePath) is { } existing)
        {
            existing.Activate();
            if (thenOpenDocument is { Length: > 0 }
                && existing.DataContext is WorkspaceViewModel openVm)
                openVm.OpenDocumentByPath(thenOpenDocument);
            return existing;
        }
        return NewWorkspaceWindow(workspacePath, thenOpenDocument);
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
        var w = WindowForForwardedFiles(paths) ?? NewWorkspaceWindow();
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
    /// <para><b>A workspace has two halves and either one opens it</b> (RC-1 R-rc1-13). RC-1 moved
    /// the panel arrangement, open tabs, tree state and colour scheme out of the <c>.cws</c> and into
    /// a sibling <c>.cwsuser</c>; both are registered to circuitRF on all three platforms, and both
    /// resolve here to the folder that contains them. A <c>.cwsuser</c> arriving with no <c>.cws</c>
    /// beside it gets a sentence rather than an empty window.</para>
    ///
    /// <para><b>Several workspaces now open one WINDOW EACH</b> (MW1 R-mw1-16). That used to be
    /// destructive — a workspace switch replaced the window's contents, so the second would silently
    /// discard the first — and is not any more. The count is capped, because someone multi-selecting
    /// a folder should not get twelve windows; the rest are named in a message rather than dropped
    /// silently.</para>
    /// </summary>
    private static void OpenFiles(WorkspaceViewModel vm, IReadOnlyList<string> paths)
    {
        // Sorted into workspace-vs-documents BEFORE anything opens, rather than acted on in arrival
        // order. A workspace switch REPLACES the window's documents, so a document opened first and a
        // .cws opened second would leave the user looking at a window that discarded what they
        // double-clicked — the same "opened nothing" failure this dispatcher exists to have fixed,
        // reached by multi-selecting a .cws alongside a .csch.
        // Deduplicated by full path, because the two halves of one workspace can arrive together:
        // select-all in a workspace folder hands over `.cws` AND `.cwsuser`, and both resolve to the
        // same workspace. Without this they would open it twice — once in this window and once in a
        // window of its own.
        var workspaces = new List<string>();
        var seenWorkspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<string>();

        void AddWorkspace(string cwsPath)
        {
            string full;
            try   { full = Path.GetFullPath(cwsPath); }
            catch { full = cwsPath; }
            if (seenWorkspaces.Add(full)) workspaces.Add(full);
        }

        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;

            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                // The FIRST workspace opens into this window; any others open into windows of their
                // own (R-mw1-16), because that is no longer destructive.
                case ".crfw":
                case ".cws":
                    AddWorkspace(path);
                    break;

                // RC-1 R-rc1-13 — the per-user half of a workspace opens the WHOLE workspace.
                // `.cws` and `.cwsuser` are two halves of one document; a user who double-clicks
                // either one means the same thing by it, and a file that shows a circuitRF icon and
                // then does nothing reads as a broken file — the exact complaint this dispatcher
                // exists to have fixed. It resolves through the containing folder, exactly as its
                // sibling `.cws` does.
                case ".cwsuser":
                {
                    // R-rc1-8: a `.cwsuser` with no `.cws` beside it is NOT a workspace, and says so
                    // in a sentence. Someone who copied one file out of a folder must not get an
                    // empty window — that is the "opened nothing" failure again, arriving by a new
                    // route, and the sidecar is exactly the file someone would copy by mistake.
                    if (WorkspaceUserPersistence.ResolveWorkspace(path) is not { } sibling)
                    {
                        vm.Messages.Warning(
                            $"'{Path.GetFileName(path)}' only records how a workspace was arranged on " +
                            "screen — the workspace itself is the folder it sits in, and this folder " +
                            "has no '.cws' in it.");
                        break;
                    }

                    AddWorkspace(sibling);
                    break;
                }

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

        OpenExtraWorkspaceWindows(vm, workspaces.Skip(1).ToList());

        if (workspaces.Count == 0)
        {
            foreach (string doc in documents)
                vm.OpenDocumentByPath(doc);
            return;
        }

        _ = OpenWorkspaceThenDocumentsAsync(vm, workspaces[0], documents);
    }

    /// <summary>
    /// The maximum number of EXTRA workspace windows one launch or one forwarded open may create
    /// (R-mw1-16). Four is plenty; someone who multi-selected a folder of projects gets a message
    /// naming the rest rather than a screen full of windows.
    /// </summary>
    private const int MaxExtraWorkspaceWindows = 4;

    private static void OpenExtraWorkspaceWindows(WorkspaceViewModel vm, IReadOnlyList<string> workspaces)
    {
        if (workspaces.Count == 0) return;

        foreach (string cws in workspaces.Take(MaxExtraWorkspaceWindows))
            OpenWorkspaceInNewWindow(cws);

        if (workspaces.Count <= MaxExtraWorkspaceWindows) return;

        var skipped = workspaces.Skip(MaxExtraWorkspaceWindows)
                                .Select(p => Path.GetFileName(Path.GetDirectoryName(p)) ?? p);
        vm.Messages.Warning(
            $"Opened {MaxExtraWorkspaceWindows + 1} workspaces; the rest were left closed: " +
            string.Join(", ", skipped) + ".");
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

    /// <summary>
    /// The workspace folder of the window the user is actually looking at, or null when no workspace is
    /// open.
    ///
    /// <para><b>This exists because the macOS application menu's Settings… had no way to say.</b> That
    /// item constructs the dialog itself rather than going through
    /// <c>WorkspaceViewModel.ShowSettings</c>, and it passed null — so on macOS, where
    /// <c>circuitRF ▸ Settings…</c> and <c>⌘,</c> are the ONLY way most people open the dialog, Settings
    /// believed no workspace was open however many were. Nothing on the dialog noticed until RC-4 put a
    /// per-workspace row on it; the one earlier consumer, <c>ThemeResolver</c>, merely stopped offering
    /// workspace-local <c>.ccolor</c> themes, silently.</para>
    ///
    /// <para><b>Active first, then any visible workspace window.</b> With several workspaces open the
    /// question "which one" has one honest answer, and it is the one in front.</para>
    /// </summary>
    private string? ActiveWorkspaceDirectory()
    {
        if (_desktop is null) return null;

        var windows = _desktop.Windows.OfType<WorkspaceWindow>().Where(w => w.IsVisible).ToList();
        var chosen  = windows.FirstOrDefault(w => w.IsActive) ?? windows.FirstOrDefault();

        return chosen?.DataContext is WorkspaceViewModel { CurrentWorkspacePath: { } cws }
            ? Path.GetDirectoryName(cws)
            : null;
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
            _appSettingsWindow = new Views.Dialogs.SettingsView(ActiveWorkspaceDirectory());
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
            if (!IsKnownNativeMenuMismatch(e.Exception)) return;

            Console.Error.WriteLine(
                "circuitRF: swallowed a known Avalonia.Native NativeMenu exporter exception on the " +
                "dispatcher (brief-harmonicarf-r3a §2.4 — a floor, not the fix): " + e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Avalonia.Native's own <c>ArgumentException("The menu being updated does not match.")</c> —
    /// one native menu proxy asked to update itself from a DIFFERENT <c>NativeMenu</c> instance than
    /// the one it was built from. Narrow by construction: the exact message AND an
    /// <c>Avalonia.Native</c> frame, never a blanket "is this an ArgumentException".
    ///
    /// <para><b>It is matched in two places because the same throw arrives by two routes.</b>
    /// <see cref="WireNativeMenuDispatcherBackstop"/> catches it when a QUEUED
    /// <c>DoLayoutReset</c> throws — no call site exists to wrap. But when the throw is synchronous
    /// inside a native window callback (a window's <c>Activated</c>, which Avalonia raises from
    /// <c>DispatcherImpl.RunLoop</c> rather than from a dispatcher job) the dispatcher's unhandled
    /// hook never sees it and the process dies — which is exactly the crash an owner hit closing a
    /// workspace on macOS (2026-09-08). There the guard has to be at the call site, and the two
    /// share this predicate so the policy is stated once.</para>
    /// </summary>
    internal static bool IsKnownNativeMenuMismatch(Exception? e) =>
        e is ArgumentException ex
        && ex.Message.Contains("menu being updated does not match", StringComparison.OrdinalIgnoreCase)
        && ex.StackTrace is { } trace
        && trace.Contains("Avalonia.Native", StringComparison.Ordinal);

    // ---- macOS background menu window (no-window state) -----------------------

    private void BuildBgMenuWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // "New Window", because that is what the item DOES — `NewWorkspaceWindow(null)` shows an
        // empty workspace window, which is File ▸ New Window's action, not File ▸ New Workspace…'s
        // (that one runs a dialog and creates a `.cws` on disk). The header said "New Workspace" and
        // named an operation this menu cannot perform: with no window open there is no workspace
        // view model to run it on (owner, 2026-09-08). The gesture stays Meta+N even though the
        // shell spells New Window Meta+Shift+N — with no window open this is the only way in, and
        // Meta+N is what every macOS application opens a window with from the background state.
        var newItem = new NativeMenuItem { Header = "New Window", Gesture = new KeyGesture(Key.N, KeyModifiers.Meta) };
        newItem.Click += (_, _) => NewWorkspaceWindow();

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

    /// <summary>
    /// Called by a WorkspaceWindow when a close/quit prompt is cancelled, so a later Quit works.
    ///
    /// <para><b>A cancelled prompt also calls off a relaunch</b>, note and all. The user said no to
    /// closing a document; carrying on to restart the application would be answering a question they
    /// were not asked, and a note left on disk would reopen these workspaces on top of whatever they
    /// do next time they launch.</para>
    /// </summary>
    internal void AbortQuit()
    {
        _isShuttingDown = false;

        if (!_relaunchAfterExit) return;
        _relaunchAfterExit = false;
        Updates.RelaunchSession.Clear();
    }

    internal void Quit()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _bgMenuWindow?.Hide();

        if (_desktop is null) { ExitProcess(); return; }
        var windows = _desktop.Windows.OfType<WorkspaceWindow>().ToList();
        if (windows.Count == 0) { CloseAllFloatingWindows(); ExitProcess(); return; }

        _ = QuitAsync(windows);
    }

    /// <summary>
    /// Asks EVERY workspace window before closing ANY of them (MW1 R-mw1-18).
    ///
    /// <para><b>Two passes, not one.</b> Closing each window as its own prompt was answered meant
    /// that cancelling the second window's prompt left the first one already gone — the user asked to
    /// keep their work and lost a window doing it. With one window open the two passes are
    /// indistinguishable, which is why this was never visible before.</para>
    /// </summary>
    private async Task QuitAsync(IReadOnlyList<WorkspaceWindow> windows)
    {
        foreach (var w in windows)
        {
            bool clear;
            try { clear = await w.ConfirmCloseAsync(quitting: true); }
            catch { clear = false; }

            if (clear) continue;

            // Cancelled: nothing has been closed, and the quit is off. Every window is exactly as it
            // was, including the ones that already answered — they are simply marked clear to close,
            // which is harmless until they are actually asked to.
            AbortQuit();
            if (OperatingSystem.IsMacOS() && _bgMenuWindow is { IsVisible: true }) _bgMenuWindow.Hide();
            return;
        }

        foreach (var w in windows) w.Close();
    }

    /// <summary>
    /// Force-closes every floating (torn-off) tool/document window still open across every
    /// WorkspaceWindow, so File->Quit actually terminates the app rather than leaving them running.
    /// A tool float's OS close box does not close the window synchronously (see CrfHostWindow: it
    /// cancels the platform close and tears down on a later dispatcher pass, to stay out of Dock's
    /// crashing cascade), which is no use to a shutdown that is about to call Environment.Exit —
    /// CloseForLayoutRebuild is the same teardown, run now. Any dirty document content in a floated
    /// window was already offered a save/discard
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

    // ---- Relaunch (the Messages panel's "Relaunch circuitRF" link) -------------

    /// <summary>
    /// Shuts this session down and starts the version that has been installed in the background,
    /// reopening the workspaces that are open right now.
    ///
    /// <para><b>It is the ordinary Quit, with two things added at each end</b>, and that is the
    /// whole safety argument. <see cref="QuitAsync"/> already asks EVERY workspace window about its
    /// unsaved work before closing ANY of them, and already abandons the whole shutdown if one
    /// prompt is cancelled. So the answer to "a one-click relaunch invites data loss" — the reason
    /// docs/design/auto-update.md §10 refused this button — is that this click is not a shortcut
    /// past any of those questions. It asks all of them, and a user who cancels one is left exactly
    /// where they were, on the version they were already running, with the note that would have
    /// reopened their workspaces deleted.</para>
    ///
    /// <para><b>The successor is started BEFORE this process exits, not after</b>, which sounds
    /// backwards and is the only way it works: nothing runs after <c>Environment.Exit</c>, and
    /// leaving the job to a detached helper is a second program to get right. The successor is
    /// handed this process's id and waits for it — see <see cref="Updates.AppRelaunch.StartSuccessor"/>,
    /// which also explains why it MUST wait rather than merely ought to.</para>
    ///
    /// <para><b>The staged update is not applied here.</b> It is applied by the successor, in its own
    /// <c>Main</c>, by exactly the code that applies it on any other launch
    /// (<see cref="Updates.UpdateStartup"/>) — which is why this method knows nothing about swaps,
    /// pointers or bundles and cannot get any of them wrong.</para>
    /// </summary>
    internal async Task RelaunchAsync()
    {
        // Already on the way out — a second tap on the link, or a Quit in flight. The link stays
        // live rather than being disabled, so this is the guard that makes that safe.
        if (_isShuttingDown) return;

        // Written BEFORE the prompts, and deleted again if any of them is cancelled. The alternative
        // — write it once every window has agreed to close — reads the workspace paths out of view
        // models that have by then been through their own teardown.
        Updates.RelaunchSession.Write(OpenWorkspacePaths());

        _relaunchAfterExit = true;
        Quit();

        // Quit() runs the prompts on the dispatcher and returns immediately; there is nothing here to
        // await. Yielding keeps the signature honest for a caller that awaits it (the Messages view's
        // tap handler) without pretending the relaunch has finished.
        await Task.Yield();
    }

    /// <summary>
    /// The <c>.cws</c> of every open workspace window, in window order, skipping windows with no
    /// workspace open.
    ///
    /// <para>A window showing the Welcome screen or an unsaved scratch design contributes nothing —
    /// there is no path to reopen. Restoring it as an empty window would be guessing at intent, and
    /// the new session opens one empty window anyway.</para>
    /// </summary>
    private IReadOnlyList<string> OpenWorkspacePaths()
    {
        if (_desktop is null) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var w in _desktop.Windows.OfType<WorkspaceWindow>())
            if (w.DataContext is WorkspaceViewModel { CurrentWorkspacePath: { } cws } && File.Exists(cws))
                paths.Add(cws);

        return paths;
    }

    /// <summary>
    /// The ONE way this process ends, so the relaunch cannot be missed by one of the exit paths.
    ///
    /// <para>There were three bare <c>Environment.Exit(0)</c> calls before this, and a fourth added
    /// later would silently not have relaunched — the same shape of bug the <c>ProcessExit</c> hooks
    /// at the top of this file were written to avoid, recorded there in their own note.</para>
    /// </summary>
    private void ExitProcess()
    {
        if (_relaunchAfterExit)
        {
            _relaunchAfterExit = false;

            // The executable this session was launched from — on the versioned layout that is what
            // the stub started, and it is where UpdateStartup applies whatever is staged.
            string? exe = Environment.ProcessPath;

            if (exe is null || !Updates.AppRelaunch.StartSuccessor(exe))
                // Nothing to do but leave the note for the user's own next launch, which is the
                // behaviour they would have had before this feature existed plus their workspaces
                // coming back. Saying so is impossible — the windows are gone.
                { /* the RelaunchSession note stays; the next launch honours it */ }
        }

        Environment.Exit(0);
    }

    internal void NotifyWindowCountChanged()
    {
        if (_desktop is null) return;
        bool anyOpen = _desktop.Windows.OfType<WorkspaceWindow>().Any();

        // Once File->Quit is in flight, finish the job the instant the last WorkspaceWindow has
        // closed (every dirty-save prompt for it AND any floated content it owns has already run) —
        // cross-platform, not just macOS: without this, a torn-off tool/document window (whose OS
        // close box tears down on a LATER dispatcher pass, see CrfHostWindow) is never touched by
        // anything else and the app never actually terminates.
        if (_isShuttingDown)
        {
            if (!anyOpen)
            {
                CloseAllFloatingWindows();
                ExitProcess();
            }
            return;
        }

        // Below: macOS-only "stay resident with just the Dock icon" convention.
        if (!OperatingSystem.IsMacOS() || _bgMenuWindow is null) return;
        if (!anyOpen) { if (!_bgMenuWindow.IsVisible) { _bgMenuWindow.Show(); _bgMenuWindow.Activate(); } }
        else { if (_bgMenuWindow.IsVisible) _bgMenuWindow.Hide(); }
    }
}
