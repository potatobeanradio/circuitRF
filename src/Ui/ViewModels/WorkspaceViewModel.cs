using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Main ViewModel for the Workspace window. Owns the Dock layout, undo/redo stack,
/// message sink, and all menu/toolbar commands. The GUI never simulates the design layer
/// directly — it always builds/edits the design layer, then asks the engine to elaborate
/// and run (6e). For 6b this is the frame: layout + commands wired but stubbed.
/// </summary>
public partial class WorkspaceViewModel : ViewModelBase, ITreeActions, IHierarchyHost, ILayoutHierarchyHost, ICellResolver
{
    // ---- Dock layout ---------------------------------------------------------

    private readonly CircuitRfDockFactory _factory;

    // ---- App-level placement service -----------------------------------------

    /// <summary>
    /// App-level armed-placement state shared by all schematic canvases and the Library Palette.
    /// The palette ARMS via Toggle(); each SchematicCanvas READS the Pending state.
    /// </summary>
    public PlacementService PlacementService { get; } = new();

    [RelayCommand]
    private void DisarmPlacement() => PlacementService.Disarm();

    // ---- Open-document tracking (dedup by absolute path) --------------------

    // Maps absolute path → the open dockable.  Checked before opening a new tab.
    // Not persisted; rebuilt from the Dock state on workspace open.
    private readonly Dictionary<string, IDockable> _openDocsByPath
        = new(StringComparer.OrdinalIgnoreCase);

    // ---- Session registry (hier1) -------------------------------------------

    // One SchematicViewModel per abs-normalized .csch path — the single source of truth.
    // Every tab and pushed-in frame (hier2+) for a path shares the same VM+EditModel+UndoRedo.
    private readonly SchematicSessionRegistry _registry = new();

    // L3b — the layout-side mirror of _registry: one LayoutEditorViewModel per abs-normalized .clay
    // path, shared by every tab and pushed-in frame for that path.
    private readonly LayoutSessionRegistry _layoutRegistry = new();

    // Scratch documents have no path so they cannot go in _openDocsByPath.
    // Tracked here for enumeration by save/rebuild operations (steps 2+).
    // NOTE (step 1): entries are not removed when a scratch tab is closed — that
    // cleanup and the close-prompt are added in step 2/3.
    private readonly List<SchematicDocument>    _scratchDocs         = [];
    private readonly List<SymbolEditorDocument> _scratchSymbols      = [];
    private readonly List<DataDisplayDocument>  _scratchDataDisplays = [];
    private readonly List<LayoutDocument>       _scratchLayouts      = [];

    // ---- Technology cache (L0c) -----------------------------------------------

    // Owned for the lifetime of a workspace; replaced (not just cleared) on every
    // NewWorkspace/SwitchToWorkspace/ResetToBlankShell so a fresh subscription always matches
    // the current instance — see ResetTechCache.
    private TechnologyCache _techCache = new();

    [ObservableProperty] private IRootDock? _layout;

    // Exposed so WorkspaceWindow.axaml can bind DockControl.Factory — required for float/tear-off.
    public IFactory DockFactory => _factory;

    // ---- Infrastructure ------------------------------------------------------

    public IMessageSink Messages => _factory.MessagesTool
        ?? throw new InvalidOperationException("DockFactory must expose MessagesTool.");

    // ---- Autosave / recovery -------------------------------------------------

    private readonly RecoveryManager _recovery;
    private Avalonia.Threading.DispatcherTimer? _autosaveTimer;

    // ---- Debounced .cws autosave --------------------------------------------

    // Debounces .cws writes triggered by config changes (filter, ordering, etc.).
    // Flushed synchronously on clean exit; never triggered by pan/zoom.
    private Avalonia.Threading.DispatcherTimer? _cwsSaveTimer;

    // Track the currently-subscribed FilterState so we can unsubscribe when
    // CreateDefaultLayout replaces the ProjectTreeTool instance.
    private System.ComponentModel.PropertyChangedEventHandler? _filterStateHandler;
    private ProjectTreeFilterState? _subscribedFilterState;

    // Track the currently-subscribed ProjectTreeTool for SelectedItem changes (inspector).
    private System.ComponentModel.PropertyChangedEventHandler? _treeSelectionHandler;
    private ProjectTreeTool? _subscribedTreeTool;
    private CellParameterEditModel? _treeInspectorCellModel;

    // Track the currently-subscribed DisplayWindowViewModel for ActiveInspector changes.
    private CircuitRF.Ui.DataDisplay.ViewModels.DisplayWindowViewModel? _subscribedDisplayWindow;
    private System.ComponentModel.PropertyChangedEventHandler? _displayInspectorHandler;

    // ---- Per-document undo routing ------------------------------------------

    // The active editable document; null when no undoable document is active.
    private IUndoableDocument? _activeUndoTarget;

    // Last schematic document made active — kept so the Analyses panel + Run button survive focusing a
    // data display / symbol / cell tab. Cleared when this doc is closed or the workspace changes.
    private SchematicDocument? _lastActiveSchematicDoc;

    // Windows that already have undo/redo KeyBindings injected (Dock float support).
    private readonly HashSet<Window> _wiredHostWindows = [];

    // ---- Per-window active document (brief-file-menu-restructure.md R-menu-4) ----------------

    // "The active document" for every File-menu enablement predicate and every Save/Save-As/Export
    // command means THAT WINDOW's own document: the main shell's DocumentDock.ActiveDockable while
    // the shell has focus, or a torn-off window's own hosted document while IT has focus. Established
    // once here so both menu surfaces (and every command) read the SAME resolution rather than each
    // resolving _factory.DocumentDock?.ActiveDockable directly — which would silently keep targeting
    // whatever the shell happens to show even while a torn-off window is the one in front. Scoped
    // deliberately to File-menu commands only; tree/Properties/Analyses routing is untouched.
    private IDockable? _focusedWindowDocument;

    // True while a floating TOOL window (Properties, Analyses, Project Tree, Palette, Messages) has
    // focus. Deliberately SEPARATE from _focusedWindowDocument rather than clearing it: R-dock-13 —
    // a tool panel is not a document context, so "the active document" must keep meaning the last
    // active DOCUMENT (Save stays enabled and targets it). What this flag governs is only the
    // Close item, which reads "Close Workspace" for a tool window because a tool panel belongs to the
    // workspace, not to a document of its own.
    private bool _focusedWindowIsToolOnly;

    // Windows already wired for focus tracking (parallel to, but independent of, _wiredHostWindows).
    private readonly HashSet<Window> _focusTrackedWindows = [];

    // The document each focus-tracked CrfHostWindow was last found to host, so Closed can tell
    // whether the closing window was the one that owns the current override.
    private readonly Dictionary<Window, IDockable?> _focusTrackedWindowDocs = new();

    private IDockable? ResolveActiveDocumentForCommands()
        => _focusedWindowDocument ?? _factory.DocumentDock?.ActiveDockable;

    public string UndoDescription => _activeUndoTarget?.UndoRedo.UndoDescription ?? "Undo";
    public string RedoDescription => _activeUndoTarget?.UndoRedo.RedoDescription ?? "Redo";

    // ---- Window title --------------------------------------------------------

    [ObservableProperty] private string _windowTitle = "circuitRF";

    // ---- Last-run DataSets (held for Phase 7) --------------------------------
    // Populated by RunAnalysis after a successful engine run; visualised in Phase 7.
    private IReadOnlyList<DataSet> _lastRunDataSets = [];
    [ObservableProperty] private string? _currentWorkspacePath;

    private string? CurrentWorkspaceRoot
        => CurrentWorkspacePath is null ? null : Path.GetDirectoryName(CurrentWorkspacePath);

    // Last-used parent directory for the New Workspace dialog (in-memory, not persisted).
    // Seeds the Location field so repeated New Workspace dialogs start at the same folder.
    private string _lastWorkspaceParentDir =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ---- Recent Workspaces (persisted in AppPreferences) --------------------

    private readonly List<string> _recentWorkspaces = [];

    // ---- Recently-Placed MRU (persisted in AppPreferences) ------------------

    private readonly List<SymbolKind> _recentlyPlaced;
    private const int MruPlacedCap = 12;

    // Observable collection of menu items for the in-window "Open Recent" submenu.
    // Rebuilt by RebuildRecentMenuItems() after every push/clear.
    public ObservableCollection<Control> RecentMenuItems { get; } = new();

    // True when the recent list is non-empty; drives IsEnabled on the "Open Recent" MenuItem.
    public bool HasRecentWorkspaces => _recentWorkspaces.Count > 0;

    // Exposed for the NativeMenu code-behind rebuild.
    public IReadOnlyList<string> RecentWorkspacesList => _recentWorkspaces;

    // Fired (on UI thread) after any push or clear so the NativeMenu code-behind can rebuild.
    public event Action? RecentWorkspacesChanged;

    // ---- Save scope (drives "Save All" vs "Save" menu label) ----------------

    /// <summary>Whether ⌘S/Ctrl+S should save all documents or only the active one.</summary>
    public enum SaveScope { AllDocs, SingleDoc }

    [ObservableProperty] private SaveScope _activeSaveScope = SaveScope.AllDocs;

    /// <summary>Menu label for the primary save action; bound by both the in-window menu and
    /// updated on the NativeMenu via <see cref="SaveScopeChanged"/>.</summary>
    public string SaveMenuHeader => ActiveSaveScope == SaveScope.SingleDoc ? "Save" : "Save All";

    /// <summary>Fired after <see cref="ActiveSaveScope"/> changes so the NativeMenu code-behind
    /// can update the macOS menu bar label (same pattern as RecentWorkspacesChanged).</summary>
    public event Action? SaveScopeChanged;

    partial void OnActiveSaveScopeChanged(SaveScope value)
    {
        OnPropertyChanged(nameof(SaveMenuHeader));
        SaveScopeChanged?.Invoke();
    }

    partial void OnCurrentWorkspacePathChanged(string? value)
    {
        // Workspace name comes from the containing FOLDER (the file is literally ".cws", no stem).
        var dir  = value is not null ? Path.GetDirectoryName(value) : null;
        var name = dir is not null   ? Path.GetFileName(dir)         : null;
        WindowTitle = !string.IsNullOrEmpty(name) ? $"{name} — circuitRF" : "circuitRF";

        NewCellInWorkspaceCommand.NotifyCanExecuteChanged();
        ExportDataCommand.NotifyCanExecuteChanged();
        CloseWorkspaceCommand.NotifyCanExecuteChanged();
        CloseWorkspaceOrWindowCommand.NotifyCanExecuteChanged();
        ImportGdsiiLibraryCommand.NotifyCanExecuteChanged();
        ImportDxfLibraryCommand.NotifyCanExecuteChanged();

        if (_factory.ProjectTreeTool is { } tree)
        {
            if (dir is not null)
                tree.SetWorkspace(dir);
            else
                tree.ClearWorkspace();
        }

        _factory.AnalysesTool?.SetWorkspaceDir(dir);

        // brief-foreign-documents.md §4: IsForeign/SourceWorkspaceName are computed live from
        // CurrentWorkspaceRootDirProvider, which has no PropertyChanged of its own — refresh every
        // open (docked or floated) layout's marking explicitly whenever the open workspace changes.
        foreach (var doc in _scratchLayouts.Concat(_openDocsByPath.Values.OfType<LayoutDocument>()))
            doc.RefreshForeignMarking();
    }

    // ---- Constructor ---------------------------------------------------------

    public WorkspaceViewModel()
    {
        _factory = new CircuitRfDockFactory();

        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;
        _factory.PaletteTool?.SetPlacementService(PlacementService);

        // Load persisted preferences and seed the recent lists BEFORE wiring the tree.
        // SetActions(this) triggers ProjectTreeTool.RefreshRecent(), which snapshots
        // GetRecentWorkspaces() — so _recentWorkspaces must already be populated, or the
        // no-workspace tree shows "No recent workspaces." on a fresh launch even though the
        // File ▸ Open Recent menu (rebuilt below) is correct. (Close-workspace re-runs
        // SetActions later when the list is populated, which is why this only bit cold start.)
        var prefs         = AppPreferencesIo.Load();
        _recentWorkspaces.AddRange(prefs.RecentWorkspaces ?? []);
        _recentlyPlaced   = ParseMruPlaced(prefs.RecentlyPlaced);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        RebuildRecentMenuItems();

        // Wire tree-item actions before any workspace is loaded so actions are available
        // the moment SetWorkspace builds the first VM tree. RefreshRecent() now sees the
        // populated recent list seeded above.
        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        // Notify PropertiesTool when the active document tab changes (active schematic tracking).
        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnDocumentDockPropertyChanged;
        WireAnalysesRun();

        // Wire close-tab prompt: before a dockable is removed, show Save/Don't Save/Cancel
        // for dirty/scratch documents. FactoryBase.DockableClosed fires from base.CloseDockable
        // and cleans up _scratchDocs/_openDocsByPath.
        _factory.CloseDockableConfirm = ConfirmCloseDockable;
        _factory.DockableClosed += (_, args) => { if (args.Dockable is not null) OnDockableClosed(args.Dockable); };

        // A newly floated window needs its per-window wiring — focus tracking, undo key bindings, and
        // the macOS menu attach. The only other trigger is OnDocumentDockPropertyChanged, which a TOOL
        // tear-off never fires (it does not touch the DocumentDock), so a torn-off tool window used to
        // get no Activated handler at all and therefore no macOS menu bar while it was key. Deferred
        // one frame so the host window is fully shown before the scan looks for it.
        _factory.WindowAdded += (_, _) =>
        {
            // A tear-off is immediately followed by a window DRAG, which runs Dock's
            // WindowActivationHelper.ActivateAllWindows -> Window.SortWindowsByZOrder over
            // factory.HostWindows. One closed window still in that collection throws
            // ArgumentException there and takes the app down, so sweep before the drag can start.
            _factory.PurgeClosedHostWindows();
            Dispatcher.UIThread.Post(TryWireHostWindowsUndo,     DispatcherPriority.Background);
            Dispatcher.UIThread.Post(TryWireWindowFocusTracking, DispatcherPriority.Background);
        };

        // Autosave: periodic dirty-scratch serialization to the per-session recovery dir.
        _recovery = new RecoveryManager();
        StartAutosaveTimer();

        // Defer recovery-offer until the window is fully shown (Background priority).
        Avalonia.Threading.Dispatcher.UIThread.Post(
            CheckForRecovery, Avalonia.Threading.DispatcherPriority.Background);

        ResetTechCache();

        // L3b — CellLayoutResolver is a static, process-lifetime class (unlike the per-workspace
        // _techCache), so this subscribes exactly once, ever; never re-subscribed per workspace reset.
        CellLayoutResolver.LiveViewChanged += OnCellLayoutLiveViewChanged;

        Messages.Info("circuitRF ready.");
    }

    // ---- Technology resolution (L0c) ------------------------------------------

    /// <summary>
    /// Replaces the technology cache with a fresh instance and (re)subscribes the live-refresh
    /// handler. Called once from the constructor and again from every workspace-lifetime reset
    /// (NewWorkspace / SwitchToWorkspace / ResetToBlankShell) so stale cached entries from the
    /// previous workspace can never leak into the new one.
    /// </summary>
    private void ResetTechCache()
    {
        _techCache = new TechnologyCache();
        _techCache.TechnologyChanged += OnTechnologyChanged;

        // Drop any not-yet-flushed live-tech update targeting the OLD cache — every workspace-reset
        // path closes open documents first, but this guards the (rare) case of a reset landing in
        // the same dispatcher tick as a pending flush, which would otherwise install a stale edit
        // into the brand-new cache under a coincidentally-matching absolute path.
        _pendingTechLive.Clear();
        _techLiveFlushScheduled = false;
    }

    /// <summary>
    /// brief-foreign-documents.md R-fgn-4: a session-scoped override for a materialized layout with no
    /// ancestor workspace at all (a genuinely parent-less loose file) — keyed by absolute .clay path.
    /// <see cref="ResolveTechFor"/> checks this FIRST, before doing any resolution walk, so the prompt
    /// (below) is asked once per document per session and never overwritten by a later re-resolve.
    /// Never written to disk (R-fgn-4's own guardrail) and never consulted for a scratch document
    /// (there is no path to key by).
    /// </summary>
    private readonly record struct OrphanTechChoice(string? Path, Technology? StarterTech);
    private readonly Dictionary<string, OrphanTechChoice> _sessionTechOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Documents currently showing (or about to show) the R-fgn-4 prompt — guards against a
    /// rapid-fire re-resolve (e.g. <see cref="RefreshAllOpenLayoutTech"/>) popping the same dialog
    /// more than once concurrently for the same path.</summary>
    private readonly HashSet<string> _pendingOrphanTechPrompts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective Technology for a layout and posts every diagnostic to Messages at
    /// Warning level (TechnologyResolver itself never posts — see its header). <paramref name="techRef"/>
    /// is the layout's own LayoutView.TechRef; <paramref name="clayPath"/> is the absolute .clay path,
    /// or null for a not-yet-saved scratch layout (workspace-default resolution still applies).
    ///
    /// brief-foreign-documents.md R-fgn-3: resolves against the DOCUMENT'S OWN parent workspace — the
    /// nearest ancestor <c>.cws</c> walking up from <paramref name="clayPath"/>'s own directory — never
    /// against whichever workspace happens to be currently open. This is what keeps a foreign layout's
    /// layers from being silently reinterpreted by a different technology sharing the same numeric
    /// keys (the exact L1g collision arriving through a new door). Re-run on every call, never cached
    /// as "this document's workspace" — a Save-As that moves the file elsewhere is picked up for free.
    /// A scratch document (<paramref name="clayPath"/> is null) has no path of its own yet, so it falls
    /// back to the CURRENTLY open workspace, matching the pre-existing behavior for a brand-new layout.
    /// </summary>
    private TechResolution ResolveTechFor(string? techRef, string? clayPath)
    {
        string? normalizedClayPath = clayPath is null ? null : Path.GetFullPath(clayPath);

        // R-fgn-4: a session choice for a genuinely parent-less loose file always wins, and is never
        // re-derived from the (still-absent) ancestor workspace.
        if (normalizedClayPath is not null &&
            _sessionTechOverrides.TryGetValue(normalizedClayPath, out var choice))
        {
            if (choice.Path is not null)
            {
                var loaded = TechnologyResolver.LoadDirect(choice.Path, TechResolutionSource.WorkspaceDefault, _techCache);
                foreach (var d in loaded.Diagnostics) Messages.Warning(d);
                return loaded;
            }
            return new TechResolution(choice.StarterTech, null, TechResolutionSource.None, []);
        }

        string? ownCwsPath = normalizedClayPath is not null
            ? WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(normalizedClayPath))
            : CurrentWorkspacePath;

        string? workspaceDir = ownCwsPath is null ? null : Path.GetDirectoryName(ownCwsPath);

        string? defaultTechRef = null;
        if (ownCwsPath is not null)
        {
            try { defaultTechRef = WorkspacePersistence.LoadFromFile(ownCwsPath).DefaultTechRef; }
            catch { /* corrupt .cws — treated as "no default", matches TryLoadCws elsewhere */ }
        }

        string? clayDir = normalizedClayPath is null ? null : Path.GetDirectoryName(normalizedClayPath);
        var resolution = TechnologyResolver.Resolve(techRef, clayDir, workspaceDir, defaultTechRef, _techCache);

        foreach (var diagnostic in resolution.Diagnostics)
            Messages.Warning(diagnostic);

        // R-fgn-4: a materialized document with NO ancestor workspace at all (never a scratch one —
        // those legitimately fall back to the current workspace above) and no explicit TechRef of its
        // own is the "loose file, nothing to resolve against" case — prompt once per session rather
        // than silently falling back to FallbackPalette (§2.1's own explicit rule).
        if (normalizedClayPath is not null && ownCwsPath is null && techRef is null &&
            resolution.Source == TechResolutionSource.None)
        {
            TryPromptForOrphanTechnology(normalizedClayPath);
        }

        return resolution;
    }

    /// <summary>Fire-and-forget: shows the R-fgn-4 prompt at most once per document per session
    /// (guarded by <see cref="_sessionTechOverrides"/>/<see cref="_pendingOrphanTechPrompts"/>), and
    /// applies the chosen technology to every open <see cref="LayoutDocument"/>/scratch layout whose
    /// <see cref="LayoutEditorViewModel.CurrentLayoutPath"/> matches once answered.</summary>
    private void TryPromptForOrphanTechnology(string normalizedClayPath)
    {
        if (_sessionTechOverrides.ContainsKey(normalizedClayPath)) return;
        if (!_pendingOrphanTechPrompts.Add(normalizedClayPath)) return;

        _ = RunOrphanTechnologyPromptAsync(normalizedClayPath);
    }

    private async Task RunOrphanTechnologyPromptAsync(string normalizedClayPath)
    {
        try
        {
            var window = ResolveOwner(null);
            if (window is null) return;

            var choice = await Views.Dialogs.OrphanTechnologyDialog.ShowAsync(
                window, CurrentWorkspacePath, Path.GetFileName(normalizedClayPath));
            if (choice is null) return; // dismissed — ask again next time, per §2.1's own no-silent-fallback rule

            _sessionTechOverrides[normalizedClayPath] = choice.Value.Path is not null
                ? new OrphanTechChoice(choice.Value.Path, null)
                : new OrphanTechChoice(null, choice.Value.StarterTech);

            foreach (var doc in _scratchLayouts.Concat(_openDocsByPath.Values.OfType<LayoutDocument>()))
            {
                if (doc.FilePath is null) continue;
                if (!string.Equals(Path.GetFullPath(doc.FilePath), normalizedClayPath, StringComparison.OrdinalIgnoreCase)) continue;
                doc.ViewModel.ApplyTechResolution(ResolveTechFor(doc.ViewModel.Model.TechRef, doc.FilePath));
            }
        }
        finally
        {
            _pendingOrphanTechPrompts.Remove(normalizedClayPath);
        }
    }

    /// <summary>
    /// The live-refresh seam: fires when the cache invalidates a .ctech path (Reload Technology,
    /// or Set as Workspace Default invalidating the newly-chosen default). Re-resolves and pushes
    /// the technology into every open LayoutDocument whose resolution used that path. In L0c the
    /// visible effect is limited to the metadata bar; L1/L2 hook the renderer to this same event.
    /// </summary>
    private void OnTechnologyChanged(string changedPath)
    {
        foreach (var doc in _scratchLayouts.Concat(_openDocsByPath.Values.OfType<LayoutDocument>()))
        {
            if (!string.Equals(doc.ViewModel.ResolvedTechPath, changedPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var resolution = ResolveTechFor(doc.ViewModel.Model.TechRef, doc.FilePath);
            doc.ViewModel.ApplyTechResolution(resolution);
        }
    }

    /// <summary>Re-resolves every open layout document, regardless of which path it previously
    /// resolved against. Used by SetAsWorkspaceDefault, where the default itself changed —
    /// the OnTechnologyChanged path-match alone would miss documents that move from the old
    /// default to the new one.</summary>
    private void RefreshAllOpenLayoutTech()
    {
        foreach (var doc in _scratchLayouts.Concat(_openDocsByPath.Values.OfType<LayoutDocument>()))
        {
            var resolution = ResolveTechFor(doc.ViewModel.Model.TechRef, doc.FilePath);
            doc.ViewModel.ApplyTechResolution(resolution);
        }
    }

    // ---- Helpers -------------------------------------------------------------

    // $parent[Window] bindings resolve to null on macOS for both NativeMenuItem and
    // Window.KeyBindings. MainWindow is also null (App.axaml.cs only calls Show(), never
    // assigns desktop.MainWindow). Find the window whose DataContext is this instance so
    // each VM correctly locates its own host window across multi-window scenarios.
    private Window? ResolveOwner(Window? parameter) =>
        parameter
        ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
           ?.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, this));

    /// <summary>
    /// Switches the left ToolDock's active tab to the specified launch pane.
    /// Called once after the window is shown; a no-op if the dock isn't ready.
    /// </summary>
    public void ApplyLaunchPane(LaunchPane pane)
    {
        IDockable? target = pane == LaunchPane.Palette
            ? (IDockable?)_factory.PaletteTool
            : _factory.ProjectTreeTool;
        if (target is not null)
            _factory.SetActiveDockable(target);
    }

    /// <summary>
    /// Executes the stored launch action. Called once after window show when no files
    /// were passed as startup arguments. The launch action OWNS the initial document;
    /// the Welcome stub (created by CreateLayout) is removed for every action except Welcome.
    /// For NewWorkspace/OpenWorkspace, RemoveWelcomeStub is only called on success so a
    /// cancelled dialog leaves Welcome showing rather than an empty dock.
    /// </summary>
    public async Task ExecuteLaunchActionAsync(LaunchAction action)
    {
        switch (action)
        {
            case LaunchAction.Welcome:
                // Leave the Welcome stub showing; add nothing.
                break;

            case LaunchAction.NewSchematic:
                _factory.RemoveWelcomeStub();
                NewScratchSchematic();
                break;

            case LaunchAction.NewWorkspace:
                await NewWorkspace(null);
                // NewWorkspace calls CreateDefaultLayout (new Welcome stub) on success;
                // remove it only when the workspace was actually created (not cancelled).
                if (CurrentWorkspacePath is not null)
                    _factory.RemoveWelcomeStub();
                break;

            case LaunchAction.OpenWorkspace:
                await OpenWorkspace(null);
                // RemoveWelcomeStub is a no-op if RestoreOpenDocuments already removed it.
                // Called only on success so a cancelled picker leaves Welcome showing.
                if (CurrentWorkspacePath is not null)
                    _factory.RemoveWelcomeStub();
                break;

            case LaunchAction.NewSymbol:
                _factory.RemoveWelcomeStub();
                NewScratchSymbol();
                break;

            case LaunchAction.NewDataDisplay:
                _factory.RemoveWelcomeStub();
                NewDataDisplay();
                break;
        }
    }

    // ---- File commands -------------------------------------------------------

    [RelayCommand]
    private async Task NewWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (HasAnyDirtyWork(includeFloated: false) && !await PromptSaveBeforeClose(window, "creating a new workspace", includeFloated: false))
            return;

        var result = await new NewWorkspaceDialog(_lastWorkspaceParentDir).ShowDialog<NewWorkspaceResult?>(window);
        if (result is null) return;

        var workspaceDir = Path.Combine(result.ParentDir, result.Name);

        // Race guard: re-check that the target folder still doesn't exist at create time.
        if (Directory.Exists(workspaceDir))
        {
            Messages.Error($"A folder named '{result.Name}' already exists at that location.");
            return;
        }

        var cwsPath = Path.Combine(workspaceDir, ".cws");

        try
        {
            Directory.CreateDirectory(workspaceDir);

            // Technology choice (R-misc-8/11/12): the chosen SHIPPED technology's own bytes are
            // written verbatim into tech/<id>.ctech + a .cws default ref — a real, independently-
            // editable file, never a reference back to the embedded copy (R-misc-8's "a workspace
            // must stay self-contained"). None writes neither — a perfectly valid workspace that
            // resolves to the fallback palette (pcell-contract.md §5's own supported no-technology
            // state).
            var cws = new CwsFile();
            if (result.TechnologyId is { Length: > 0 } techId)
            {
                var entry = ShippedTechnologies.All.First(e => e.Id == techId);
                var techDir = Path.Combine(workspaceDir, "tech");
                Directory.CreateDirectory(techDir);
                var techPath = Path.Combine(techDir, entry.Id + ".ctech");
                File.WriteAllText(techPath, ShippedTechnologies.LoadRawJson(entry));
                cws.DefaultTechRef = Path.GetRelativePath(workspaceDir, techPath);
            }
            WorkspacePersistence.SaveToFileAtomic(cwsPath, cws);

            // Update tracked location to the chosen parent (seeds the next New Workspace dialog).
            _lastWorkspaceParentDir = result.ParentDir;

            SetActiveUndoTarget(null);
            _lastActiveSchematicDoc = null;
            // Same as the switch path: the workspace being left keeps its own session record.
            PersistOutgoingWorkspaceSession();
            // A torn-off document belonging to the OLD workspace closes with it; a foreign one
            // survives. Must run while CurrentWorkspacePath still names the workspace being left.
            CloseFloatedDocumentsOwnedByWorkspace(CurrentWorkspacePath);
            _openDocsByPath.Clear();
            _scratchDocs.Clear();
            _scratchSymbols.Clear();
            _scratchLayouts.Clear();
            _scratchDataDisplays.Clear();
            _registry.Clear();
            _layoutRegistry.Clear();
            ResetTechCache();
            CurrentWorkspacePath = cwsPath;

            var newLayout = _factory.CreateDefaultLayout();
            _factory.InitLayout(newLayout);
            Layout = newLayout;
            _factory.PaletteTool?.SetPlacementService(PlacementService);
            _factory.PaletteTool?.SetMru(_recentlyPlaced);

            // CreateDefaultLayout replaced all tool instances and the DocumentDock — re-wire them.
            _factory.ProjectTreeTool?.SetActions(this);
            SubscribeToFilterState();
            SubscribeToTreeSelection();
            _factory.ProjectTreeTool?.SetWorkspace(workspaceDir);

            // Re-subscribe to the new DocumentDock (instance replaced by CreateDefaultLayout).
            if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged newNpc)
                newNpc.PropertyChanged += OnDocumentDockPropertyChanged;
            WireAnalysesRun();

            PushRecent(cwsPath);
            Messages.Clear();
            Messages.Success($"New workspace '{result.Name}' created.");

            // R-dock-9: hiding the dockers is a view preference, so it survives a workspace switch.
            ReapplyCollapsedStateIfNeeded();
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create workspace: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (HasAnyDirtyWork(includeFloated: false) && !await PromptSaveBeforeClose(window, "opening a workspace", includeFloated: false))
            return;

        IStorageFolder? startLocation = null;
        try { startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title        = "Open Workspace",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        if (folders.Count == 0) return;
        var workspaceDir = folders[0].Path.LocalPath;
        var cwsPath      = Path.Combine(workspaceDir, ".cws");

        if (!File.Exists(cwsPath))
        {
            Messages.Error("That folder is not a circuitRF workspace (no .cws found).");
            return;
        }

        SwitchToWorkspace(cwsPath);
    }

    [RelayCommand]
    private async Task SaveWorkspace(Window? owner)
    {
        if (CurrentWorkspacePath is not null)
        {
            WriteWorkspaceFile(CurrentWorkspacePath);
            return;
        }
        await SaveWorkspaceAs(owner);
    }

    [RelayCommand]
    private async Task SaveWorkspaceAs(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;
        var result = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Workspace As",
            SuggestedFileName = "untitled",
            DefaultExtension = "cws",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("circuitRF Workspace") { Patterns = new[] { "*.cws" } },
            },
        });

        if (result is null) return;
        CurrentWorkspacePath = result.Path.LocalPath;
        WriteWorkspaceFile(CurrentWorkspacePath);
    }

    // silent = true suppresses the "Saved: …" message (used on debounce tick + clean exit).
    private void WriteWorkspaceFile(string path, bool silent = false)
    {
        try
        {
            // Load existing .cws to preserve KnownFiles + LibraryRefs (authoritative on disk).
            CwsFile ws;
            try { ws = WorkspacePersistence.LoadFromFile(path); }
            catch { ws = new CwsFile(); }

            ws.ColorSchemeName = ThemeService.Active.Name is "Default" ? null : ThemeService.Active.Name;

            // Dock arrangement — OUR schema, never the docking library's serialized graph (R-dock-3).
            // R-dock-9: while the dockers are collapsed this writes the UNDERLYING arrangement, so a
            // workspace saved collapsed reopens EXPANDED with its real panel layout intact.
            // Never fatal: a capture problem must not stop the .cws being written.
            try
            {
                if (DockLayoutToPersist() is { } dockLayout)
                    ws.DockLayout = Docking.DockLayoutSerialization.Write(dockLayout);
            }
            catch (Exception ex)
            {
                Messages.Warning($"Window layout was not saved: {ex.Message}");
            }

            if (_factory.ProjectTreeTool?.FilterState is { } fs)
            {
                ws.TreeViewState = new CwsTreeViewState
                {
                    Cells               = fs.Cells,
                    Libraries           = fs.Libraries,
                    TestBenches         = fs.TestBenches,
                    DataDisplays        = fs.DataDisplays,
                    ColorThemes         = fs.ColorThemes,
                    KnownFiles          = fs.KnownFiles,
                    WorkspaceFileSystem = fs.WorkspaceFileSystem,
                };
            }

            // Persist every open MATERIALIZED, workspace-bound document — DOCKED or torn off alike.
            // brief-foreign-documents.md R-fgn-1: tearing a document off is presentation only, so it
            // keeps full privileges, ".cws session membership" included — the previous version of this
            // method scanned only `_factory.DocumentDock?.VisibleDockables`, which silently excludes
            // anything floated; a torn-off workspace-bound document was dropped from .cws and never
            // reopened next time, exactly the kind of "tear-off changed something other than
            // presentation" bug R-fgn-1 asks to find and fix. `_openDocsByPath` already tracks every
            // materialized document regardless of dock state (confirmed by ResetToBlankShell's own
            // re-population of survivors), so it is the correct source here — scratch docs (no path)
            // and the welcome stub were never reachable through either collection and still aren't.
            {
                var wsDir    = Path.GetDirectoryName(path)!;
                var docsList = new List<CwsOpenDocument>();
                int order    = 0;
                foreach (var dockable in _openDocsByPath.Values)
                {
                    string? docPath = null;
                    string? kind    = null;

                    if (dockable is SchematicDocument sd && sd.FilePath is not null)
                    {
                        docPath = sd.FilePath;
                        kind    = "schematic";
                    }
                    else if (dockable is SymbolEditorDocument syed &&
                             syed.ViewModel.CurrentSymbolPath is not null)
                    {
                        docPath = syed.ViewModel.CurrentSymbolPath;
                        kind    = "symbol";
                    }
                    else if (dockable is CellParameterEditorDocument cpd)
                    {
                        // Derive cell folder path from the .ccell path stored in the edit model.
                        docPath = Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath);
                        kind    = "cell";
                    }
                    else if (dockable is DataDisplayDocument dd && dd.FilePath is not null)
                    {
                        docPath = dd.FilePath;
                        kind    = "datadisplay";
                    }
                    else if (dockable is LayoutDocument lad && lad.FilePath is not null)
                    {
                        docPath = lad.FilePath;
                        kind    = "layout";
                    }
                    else if (dockable is TechDocument techDocKind)
                    {
                        docPath = techDocKind.FilePath;
                        kind    = "tech";
                    }

                    // R-fgn-6: a foreign document is never recorded in the current workspace's .cws —
                    // even one that's currently DOCKED (opened via File ▸ Open from outside the
                    // workspace, never torn off). Determined purely from its own path, same as every
                    // other foreign check in this file.
                    if (docPath is null || kind is null || WorkspaceRootFinder.IsOutside(docPath, wsDir)) continue;

                    string stored;
                    try   { stored = Path.GetRelativePath(wsDir, docPath); }
                    catch { stored = docPath; }
                    docsList.Add(new CwsOpenDocument { Path = stored, Kind = kind, TabOrder = order++ });
                }
                ws.OpenDocuments = docsList.Count > 0 ? docsList : null;

                // Persist active document path so the same tab is focused on restore. R-fgn-6: skip
                // entirely when the active dockable is foreign (docked but outside the workspace) — it
                // has no place in this workspace's own restore state.
                var active = _factory.DocumentDock?.ActiveDockable;
                string? activeAbsPath = active switch
                {
                    SchematicDocument asd                  => asd.FilePath,
                    SymbolEditorDocument asyed              => asyed.ViewModel.CurrentSymbolPath,
                    CellParameterEditorDocument acpd        => Path.GetDirectoryName(acpd.ViewModel.EditModel.CcellPath),
                    DataDisplayDocument add                 => add.FilePath,
                    LayoutDocument alad                     => alad.FilePath,
                    TechDocument atech                      => atech.FilePath,
                    _                                       => null,
                };

                string? activePath = null;
                if (activeAbsPath is not null && !WorkspaceRootFinder.IsOutside(activeAbsPath, wsDir))
                {
                    try   { activePath = Path.GetRelativePath(wsDir, activeAbsPath); }
                    catch { activePath = activeAbsPath; }
                }
                ws.ActiveDocumentPath = activePath;
            }

            WorkspacePersistence.SaveToFileAtomic(path, ws);
            if (!silent)
                Messages.Success("Saved", path);
        }
        catch (Exception ex)
        {
            Messages.Error($"Workspace save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribes to the current ProjectTreeTool's FilterState, unsubscribing from any
    /// previously-subscribed instance first.  Call after SetActions / CreateDefaultLayout.
    /// </summary>
    private void SubscribeToFilterState()
    {
        var newFs = _factory.ProjectTreeTool?.FilterState;
        if (ReferenceEquals(newFs, _subscribedFilterState)) return;

        if (_subscribedFilterState is not null && _filterStateHandler is not null)
            _subscribedFilterState.PropertyChanged -= _filterStateHandler;

        _subscribedFilterState = newFs;
        if (newFs is not null)
        {
            _filterStateHandler ??= (_, _) => ScheduleCwsSave();
            newFs.PropertyChanged += _filterStateHandler;
        }
    }

    /// <summary>
    /// Schedules a debounced .cws write.  Resets the timer on each call so rapid config
    /// changes coalesce into one write.  No-op when no workspace is open.
    /// </summary>
    private void ScheduleCwsSave()
    {
        if (CurrentWorkspacePath is null) return;

        if (_cwsSaveTimer is null)
        {
            _cwsSaveTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
            };
            _cwsSaveTimer.Tick += (_, _) =>
            {
                _cwsSaveTimer.Stop();
                if (CurrentWorkspacePath is not null)
                    WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
            };
        }

        _cwsSaveTimer.Stop();
        _cwsSaveTimer.Start();
    }

    /// <summary>
    /// Flushes the OUTGOING workspace's session to its own <c>.cws</c> — which open documents there
    /// were, and how the docks were arranged — immediately before that workspace is torn down.
    ///
    /// <para><b>Owner report this fixes:</b> a <c>.ctech</c> tab was not restored after opening
    /// another workspace and coming back. The cause was general, not technology-specific:
    /// <see cref="WriteWorkspaceFile"/> is the ONE place both the open-document list and the dock
    /// layout are captured, and every one of its callers was an explicit save (Save Workspace, Save
    /// All, a per-document save), the tree-filter debounce, or clean exit. <b>No path that LEAVES a
    /// workspace called it</b>, so the session was only ever recorded by accident — whenever some
    /// unrelated action happened to trigger a save while those tabs were open.</para>
    ///
    /// <para>That accident is why the report named <c>.ctech</c>: a schematic is typically edited and
    /// saved, and <c>SaveAllDocuments</c> writes <c>.cws</c> as a side effect, incidentally recording
    /// the session. A technology opened, read, and left clean triggers none of those, so nothing ever
    /// recorded it as open. Every document type was affected; <c>.ctech</c> is simply the one whose
    /// normal usage never hits the accidental save.</para>
    ///
    /// <para><b>Call this BEFORE the teardown begins</b> — specifically before
    /// <see cref="CloseFloatedDocumentsOwnedByWorkspace"/> (which removes torn-off documents from
    /// <c>_openDocsByPath</c>, so a later write would drop them from the record) and before
    /// <c>CurrentWorkspacePath</c> is reassigned. Silent: leaving a workspace is not a save the user
    /// asked for, so it must not announce itself. Guarded on the file still existing, so a workspace
    /// deleted out from under us fails quietly rather than posting an error on the way out.</para>
    /// </summary>
    private void PersistOutgoingWorkspaceSession()
    {
        if (CurrentWorkspacePath is not { } leaving) return;
        if (!File.Exists(leaving)) return;
        WriteWorkspaceFile(leaving, silent: true);
    }

    // ── Generated-cell lifecycle (brief-L5-followups-2.md §4, R-L5g-6/7/8) ─────────────────────────
    // R-L5g-6 establishes the property this whole section rests on: every LayoutView.PCellSnapshots
    // entry carries everything GeneratedCellStore.GetOrCreate needs to rebuild ONE generated cell
    // folder byte-identically, so the folder itself is a pure, deletable, rebuildable-from-the-layout
    // cache — never authoritative. That is what makes the delete-on-close/delete-again-on-open policy
    // below safe rather than data-destroying (§4.1's own warning: it would NOT have been safe before
    // R-L5g-6, since a palette-dropped or layout-authored PCell's only parameter record used to live
    // solely inside the generated cell's own .clay).

    /// <summary>R-L5g-7: delete the whole <c>.generated-cells</c> folder for the workspace at
    /// <paramref name="cwsPath"/> — leaves a clean workspace on disk (close) and guarantees a clean
    /// start even after a crash (open, called again before <see cref="RegenerateAllGeneratedCells"/>).
    /// Best-effort: a locked/unremovable folder is reported, never left half-deleted in a way that
    /// blocks the close/open itself. Thin wrapper over the framework-free
    /// <see cref="GeneratedCellsLifecycle"/> (directly unit-tested there) — see that class's own doc
    /// comment for the full policy story.</summary>
    private void DeleteGeneratedCellsFolder(string cwsPath)
    {
        try { GeneratedCellsLifecycle.DeleteGeneratedCellsFolder(Path.GetDirectoryName(cwsPath)!); }
        catch (Exception ex) { Messages.Warning($"Could not clear the generated-cell cache: {ex.Message}"); }
    }

    /// <summary>R-L5g-8: thin wrapper over <see cref="GeneratedCellsLifecycle.RegenerateAll"/>, supplying
    /// a small memoized <c>.ctech</c> loader as the technology resolver.</summary>
    private void RegenerateAllGeneratedCells(string cwsPath)
    {
        var techCache = new Dictionary<string, Technology?>(StringComparer.OrdinalIgnoreCase);
        Technology? ResolveTech(string? techIdentity)
        {
            if (string.IsNullOrEmpty(techIdentity)) return null;
            if (techCache.TryGetValue(techIdentity, out var cached)) return cached;
            Technology? tech = null;
            try { if (File.Exists(techIdentity)) tech = TechPersistence.LoadFromFile(techIdentity); }
            catch { /* best-effort — a missing/renamed .ctech regenerates on the fallback palette */ }
            techCache[techIdentity] = tech;
            return tech;
        }

        GeneratedCellsLifecycle.RegenerateAll(Path.GetDirectoryName(cwsPath)!, ResolveTech);
    }

    /// <summary>
    /// Replaces the current session with the workspace at <paramref name="cwsPath"/>.
    /// Caller must have already prompted for and handled any dirty documents.
    /// Clears open docs, installs a fresh Dock layout, re-wires tools, restores theme,
    /// tree state, and the persisted open-document list.
    /// </summary>
    private void SwitchToWorkspace(string cwsPath)
    {
        var workspaceDir = Path.GetDirectoryName(cwsPath)!;
        _lastWorkspaceParentDir = Path.GetDirectoryName(workspaceDir) ?? _lastWorkspaceParentDir;

        SetActiveUndoTarget(null);
        _lastActiveSchematicDoc = null;
        // Record the outgoing workspace's session (open tabs + dock arrangement) BEFORE anything is
        // torn down — nothing else on this path ever wrote it, so those tabs were simply forgotten.
        PersistOutgoingWorkspaceSession();
        // A torn-off document belonging to the OLD workspace closes with it; a foreign one survives.
        // Must run while CurrentWorkspacePath still names the workspace being left — hence before the
        // reassignment below. Without it the OS window outlives its workspace and reopening that
        // workspace shows the same file in two windows.
        CloseFloatedDocumentsOwnedByWorkspace(CurrentWorkspacePath);
        _openDocsByPath.Clear();
        _scratchDocs.Clear();
        _scratchSymbols.Clear();
        _scratchLayouts.Clear();
        _scratchDataDisplays.Clear();
        _registry.Clear();
        _layoutRegistry.Clear();
        ResetTechCache();
        CurrentWorkspacePath = cwsPath;

        var newLayout = _factory.CreateDefaultLayout();
        _factory.InitLayout(newLayout);
        Layout = newLayout;
        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);

        // CreateDefaultLayout replaced all tool instances and the DocumentDock — re-wire them.
        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();
        _factory.ProjectTreeTool?.SetWorkspace(workspaceDir);

        // Re-subscribe to the new DocumentDock (instance replaced by CreateDefaultLayout).
        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged newNpc)
            newNpc.PropertyChanged += OnDocumentDockPropertyChanged;
        WireAnalysesRun();

        var cws = TryLoadCws(cwsPath);
        if (cws.ColorSchemeName is { } schemeName)
        {
            try { ThemeService.Active = ThemeResolver.Resolve(schemeName, workspaceDir); }
            catch { }
        }
        ApplyTreeViewState(cws.TreeViewState);

        // R-L5g-7/8: clean start even after a crash, then warm the cache back up before any layout
        // actually opens below — see this file's "Generated-cell lifecycle" section for the full story.
        DeleteGeneratedCellsFolder(cwsPath);
        RegenerateAllGeneratedCells(cwsPath);

        // The dock arrangement is PARSED here but applied after the documents are open, so the
        // rebuilt shell re-hosts the populated DocumentDock rather than an empty one. Its document
        // order feeds RestoreOpenDocuments — R-dock-2: the layout supplies ARRANGEMENT, while
        // cws.OpenDocuments stays authoritative for MEMBERSHIP.
        var dockLayoutRead = ReadDockLayout(cws);

        RestoreOpenDocuments(cws, workspaceDir, dockLayoutRead.Layout);

        PushRecent(cwsPath);
        Messages.Clear();
        Messages.Info("Opened", cwsPath);

        ApplyRestoredDockLayout(dockLayoutRead);

        // R-res-11 — migrate any results/<key>/run.npy directories left from the earlier layout to
        // the flat results/<key>.npy one, reporting what moved. Cheap no-op on an already-flat workspace.
        RunResultsWriter.MigrateOldLayout(Path.Combine(workspaceDir, "results"), Messages);
    }

    /// <summary>
    /// Re-opens the documents listed in <paramref name="cws"/> into the current DocumentDock.
    /// Removes the welcome stub first (so the restored tabs are the only content).
    /// No-op when <see cref="CwsFile.OpenDocuments"/> is null or empty.
    /// </summary>
    private void RestoreOpenDocuments(CwsFile cws, string workspaceDir, Docking.CwsDockLayout? dockLayout = null)
    {
        if (cws.OpenDocuments is not { Count: > 0 } docs) return;

        _factory.RemoveWelcomeStub();

        // R-dock-2 — the layout records ARRANGEMENT, the open list records MEMBERSHIP, and when the
        // two disagree the open list wins. Opening in the reconciled order is what actually produces
        // the saved tab order (note that OpenDocuments[].TabOrder is written from a dictionary walk,
        // not from the real tab strip, so it is a membership record with an order-shaped field —
        // the layout block is the first thing in .cws that records true tab order).
        var byKey  = docs.ToDictionary(d => d.Path, d => d, StringComparer.OrdinalIgnoreCase);
        var opened = docs.OrderBy(d => d.TabOrder).Select(d => d.Path).ToList();
        var order  = dockLayout is null
            ? opened
            : Docking.DockLayoutSerialization.ReconcileDocumentOrder(dockLayout.DocumentOrder, opened);

        foreach (var key in order)
        {
            if (!byKey.TryGetValue(key, out var entry)) continue;

            var absPath = Path.IsPathRooted(entry.Path)
                ? entry.Path
                : Path.GetFullPath(Path.Combine(workspaceDir, entry.Path));

            switch (entry.Kind)
            {
                case "schematic" when File.Exists(absPath):
                    OpenOrActivateSchematic(absPath);
                    break;
                case "symbol" when File.Exists(absPath):
                    OpenOrActivateSymbol(absPath);
                    break;
                case "cell" when Directory.Exists(absPath):
                    OpenOrActivateCellPlaceholder(absPath, Path.GetFileName(absPath));
                    break;
                case "datadisplay" when File.Exists(absPath):
                    OpenOrActivateDataDisplay(absPath);
                    break;
                case "layout" when File.Exists(absPath):
                    OpenOrActivateLayout(absPath);
                    break;
                case "tech" when File.Exists(absPath):
                    OpenOrActivateTech(absPath);
                    break;
            }
        }

        // Restore the previously active tab. The layout's own record wins when it names a document
        // that really is open; otherwise .cws's ActiveDocumentPath (R-dock-2 again — arrangement
        // from the layout, membership from the open list).
        var activePath = dockLayout?.ActiveDocument is { } fromLayout && byKey.ContainsKey(fromLayout)
            ? fromLayout
            : cws.ActiveDocumentPath;

        if (activePath is not null)
        {
            var absActive = Path.IsPathRooted(activePath)
                ? activePath
                : Path.GetFullPath(Path.Combine(workspaceDir, activePath));
            if (_openDocsByPath.TryGetValue(absActive, out var activeDoc))
                _factory.SetActiveDockable(activeDoc);
        }
    }

    /// <summary>
    /// Loads the .cws, returning a default CwsFile and logging a user-visible warning on corruption.
    /// The "no .cws → not a workspace" Open gate (file-existence check) is separate — this is only
    /// called when the file is known to exist but may be corrupt.
    /// </summary>
    private CwsFile TryLoadCws(string cwsPath)
    {
        try { return WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex)
        {
            Messages.Warning(
                $"Workspace config (.cws) could not be read; starting from defaults. ({ex.Message})");
            return new CwsFile();
        }
    }

    /// <summary>
    /// Applies persisted tree view-state to the current ProjectTreeTool's FilterState.
    /// Temporarily unsubscribes the debounce handler to avoid a spurious .cws write on open.
    /// </summary>
    private void ApplyTreeViewState(CwsTreeViewState? tvs)
    {
        if (tvs is null || _factory.ProjectTreeTool?.FilterState is not { } fs) return;

        // Suspend the debounce subscription while applying restored state.
        if (_filterStateHandler is not null) fs.PropertyChanged -= _filterStateHandler;

        fs.Cells               = tvs.Cells;
        fs.Libraries           = tvs.Libraries;
        fs.TestBenches         = tvs.TestBenches;
        fs.DataDisplays        = tvs.DataDisplays;
        fs.ColorThemes         = tvs.ColorThemes;
        fs.KnownFiles          = tvs.KnownFiles;
        fs.WorkspaceFileSystem = tvs.WorkspaceFileSystem;

        if (_filterStateHandler is not null) fs.PropertyChanged += _filterStateHandler;
    }

    // Library management is not implemented yet. Keep the menu item present but DISABLED (greyed) —
    // driving the disabled state through CanExecute greys both the in-window MenuItem and the macOS
    // NativeMenuItem reliably. When library support lands, make this return true (or remove it).
    private bool CanAddLibrary => false;

    [RelayCommand(CanExecute = nameof(CanAddLibrary))]
    private async Task AddLibrary(Window? owner) => await Task.CompletedTask;

    [RelayCommand]
    private async Task ImportData(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Data",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Loadpull / Data Files")
                    { Patterns = ["*.spl", "*.lpcwave", "*.npy", "*.s1p", "*.s2p", "*.s3p", "*.s4p", "*.snp"] },
                new FilePickerFileType("Loadpull (SPL)")  { Patterns = ["*.spl"] },
                new FilePickerFileType("Loadpull (LPCW)") { Patterns = ["*.lpcwave"] },
                new FilePickerFileType("NumPy Array")     { Patterns = ["*.npy"] },
                new FilePickerFileType("Touchstone")      { Patterns = ["*.s1p", "*.s2p", "*.s3p", "*.s4p", "*.snp"] },
                new FilePickerFileType("All Files")       { Patterns = ["*.*"] },
            ],
        });

        if (result.Count == 0) return;

        foreach (var item in result)
            AddKnownFile(item.Path.LocalPath);

        var displays = _openDocsByPath.Values.OfType<DataDisplayDocument>()
            .Concat(_scratchDataDisplays);
        foreach (var dd in displays)
            dd.ViewModel.Window.DataSourceLibrary.RefreshAvailableDataSources();
    }

    // ---- Recent Workspaces commands -----------------------------------------

    /// <summary>Open a workspace from the Recent list by its .cws path.</summary>
    [RelayCommand]
    private async Task OpenRecentWorkspace(string? cwsPath)
    {
        if (cwsPath is null) return;

        if (HasAnyDirtyWork(includeFloated: false))
        {
            var window = ResolveOwner(null);
            if (window is not null && !await PromptSaveBeforeClose(window, "opening a workspace", includeFloated: false))
                return;
        }

        if (!File.Exists(cwsPath))
        {
            _recentWorkspaces.RemoveAll(p =>
                string.Equals(p, cwsPath, StringComparison.OrdinalIgnoreCase));
            SaveRecent();
            RebuildRecentMenuItems();
            var missingName = Path.GetFileName(Path.GetDirectoryName(cwsPath)) ?? cwsPath;
            Messages.Error($"Workspace '{missingName}' was not found and has been removed from Recent.");
            return;
        }

        SwitchToWorkspace(cwsPath);
    }

    /// <summary>
    /// brief-foreign-documents.md §4 item 2: the edge band's "open it" affordance on a foreign
    /// document — switches to that document's OWN source workspace. Mirrors
    /// <see cref="OpenRecentWorkspace"/>'s shape exactly (switch-scoped dirty check, so a surviving
    /// torn-off document is never prompted for here either).
    /// </summary>
    [RelayCommand]
    private async Task OpenSourceWorkspace(string? cwsPath)
    {
        if (cwsPath is null) return;

        if (HasAnyDirtyWork(includeFloated: false))
        {
            var window = ResolveOwner(null);
            if (window is not null && !await PromptSaveBeforeClose(window, "opening a workspace", includeFloated: false))
                return;
        }

        if (!File.Exists(cwsPath))
        {
            Messages.Error($"Workspace '{Path.GetFileName(Path.GetDirectoryName(cwsPath))}' was not found.");
            return;
        }

        SwitchToWorkspace(cwsPath);
    }

    /// <summary>Close the current workspace and return to the no-workspace shell (Item 2).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private async Task CloseWorkspace()
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(null);
        if (window is null) return;

        if (HasAnyDirtyWork(includeFloated: false) && !await PromptSaveBeforeClose(window, "closing the workspace", includeFloated: false))
            return;

        // R-L5g-7: leaves a clean workspace on disk — CurrentWorkspacePath is still valid here, before
        // ResetToBlankShell clears it.
        DeleteGeneratedCellsFolder(CurrentWorkspacePath);
        ResetToBlankShell();
    }
    private bool CanCloseWorkspace() => CurrentWorkspacePath is not null;

    /// <summary>
    /// File → "Close Workspace" / "Close Window" (brief-file-menu-restructure.md R-menu-3/§4A.1/
    /// R-menu-4). While the shell has focus this is the whole-workspace teardown above
    /// (<see cref="CloseWorkspace"/>, unchanged). While a torn-off document window has focus
    /// (<see cref="_focusedWindowDocument"/> is non-null) this instead closes ONLY that window's own
    /// document, through the SAME <c>CircuitRfDockFactory.CloseDockable</c>/<see cref="ConfirmCloseDockable"/>
    /// path a docked tab's own close already uses — never a second prompt path. One command, one menu
    /// item, read by both the main shell's menu and a tear-off window's own menu; never the tree's own
    /// context-menu "Close Workspace" item, which always means the whole workspace and is intentionally
    /// left bound to <see cref="CloseWorkspaceCommand"/> directly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseWorkspaceOrWindow))]
    private async Task CloseWorkspaceOrWindow()
    {
        if (ClosesASingleDocumentWindow)
        {
            _factory.CloseDockable(_focusedWindowDocument!);
            return;
        }
        await CloseWorkspace();
    }

    private bool CanCloseWorkspaceOrWindow() => ClosesASingleDocumentWindow || CanCloseWorkspace();

    /// <summary>
    /// True only when this command means "close the one torn-off DOCUMENT in front of me".
    ///
    /// <para>A floating TOOL window is excluded on the owner's own call: a tool panel is associated
    /// with the workspace, not with a document, so its File menu reads <b>Close Workspace</b>. Note
    /// this deliberately does NOT clear <see cref="_focusedWindowDocument"/> — R-dock-13 keeps "the
    /// active document" meaning the last active DOCUMENT, so Save and Save-As stay enabled and act on
    /// it while a tool panel has focus.</para>
    /// </summary>
    private bool ClosesASingleDocumentWindow =>
        !_focusedWindowIsToolOnly && _focusedWindowDocument is not null;

    /// <summary>The File menu's trailing item label — "Close Window" while a torn-off DOCUMENT window
    /// has focus, "Close Workspace" otherwise (including while a floating tool panel has focus).
    /// Refreshed alongside every other R-menu-4 enablement signal in
    /// <see cref="RaiseFileMenuEnablementChanged"/>.</summary>
    public string CloseWorkspaceOrWindowHeader
        => ClosesASingleDocumentWindow ? "Close Window" : "Close Workspace";

    /// <summary>
    /// Reverts to the no-workspace state (blank Dock layout, no open documents).
    /// Called by CloseWorkspace; extractable here so startup's blank-shell launch path
    /// can share the same reset logic.
    /// </summary>
    /// <summary>
    /// True when <paramref name="dockable"/> is currently a child of the main shell's own
    /// <see cref="Layout"/> tree — false for a torn-off (floated) document, whose own
    /// <see cref="Dock.Model.Core.IRootDock"/> is a separate object with its own <c>Window</c>.
    /// brief-foreign-documents.md R-fgn-2: this is the ONE place "docked vs. floated" is decided, so
    /// every workspace-switch/teardown path reads the same answer.
    /// </summary>
    private bool IsDockableDocked(IDockable dockable) => ReferenceEquals(_factory.FindRoot(dockable), Layout);

    /// <summary>
    /// Reverts to the no-workspace state (blank Dock layout) for whatever was DOCKED in the main shell.
    /// Called by CloseWorkspace; extractable here so startup's blank-shell launch path can share the
    /// same reset logic.
    ///
    /// brief-foreign-documents.md R-fgn-2: "a workspace switch replaces the contents of the WINDOW it
    /// happens in; other windows are not affected." A docked document closes exactly as it always has;
    /// a TORN-OFF document survives, unaffected, becoming foreign to whichever workspace opens next (or
    /// to no workspace at all) — R-fgn-1's "tear-off is presentation only" cuts both ways: it does not
    /// grant a document special status, but neither does a switch performed in the main window have any
    /// business reaching into a separate one. This supersedes the R-menu-6 finding recorded in
    /// brief-file-menu-restructure.md (that finding was investigate-only for THAT brief; changing the
    /// behavior is squarely this brief's own job).
    /// </summary>
    private void ResetToBlankShell()
    {
        // Closing a workspace records its session too, so reopening it restores the same tabs.
        PersistOutgoingWorkspaceSession();

        SetActiveUndoTarget(null);
        _lastActiveSchematicDoc = null;

        // Split every tracked MATERIALIZED document by docked-vs-floated; the docked ones close, and
        // so do the floated ones that BELONG to this workspace (see
        // CloseFloatedDocumentsOwnedByWorkspace — a foreign torn-off document still survives).
        var stillOpen = new List<IDockable>();
        foreach (var dockable in _openDocsByPath.Values.ToList())
        {
            if (IsDockableDocked(dockable) || FloatedDocumentClosesWithWorkspace(dockable))
            {
                _factory.ForceCloseDockable(dockable);
                // Mirrors RemoveCellAsync/RenameCellAsync's own established pattern: retire the shared
                // session (if any) for a schematic/layout ONLY when it is no longer referenced by any
                // open document — never a blanket registry Clear(), which would also tear down a
                // surviving floated document's own push-in/undo session.
                if (dockable is SchematicDocument closedSd && closedSd.FilePath is { } schPath)
                    RetireSessionIfUnreferenced(schPath);
                else if (dockable is LayoutDocument closedLd && closedLd.FilePath is { } layPath)
                    RetireLayoutSessionIfUnreferenced(layPath);
            }
            else
            {
                stillOpen.Add(dockable); // torn off — survives, now foreign to whatever opens next
            }
        }

        // Same split for scratch (no on-disk path) documents — a DOCKED scratch tab disappears for
        // free once `Layout` is reassigned below (the whole DocumentDock tree it lived in is
        // discarded), but a TORN-OFF scratch document is a separate physical window untouched by that
        // reassignment and must be explicitly preserved in our own tracking, or later code (Save All,
        // the quit prompt, HasAnyDirtyWork) would treat it as closed when its window is still open.
        var stillOpenScratchDocs         = _scratchDocs.Where(d         => !IsDockableDocked(d)).ToList();
        var stillOpenScratchSymbols      = _scratchSymbols.Where(d      => !IsDockableDocked(d)).ToList();
        var stillOpenScratchLayouts      = _scratchLayouts.Where(d      => !IsDockableDocked(d)).ToList();
        var stillOpenScratchDataDisplays = _scratchDataDisplays.Where(d => !IsDockableDocked(d)).ToList();

        _openDocsByPath.Clear();
        foreach (var dockable in stillOpen)
        {
            var path = dockable switch
            {
                SchematicDocument sd            => sd.FilePath,
                SymbolEditorDocument syed        => syed.ViewModel.CurrentSymbolPath,
                LayoutDocument lad               => lad.FilePath,
                DataDisplayDocument dd           => dd.FilePath,
                TechDocument td                  => td.FilePath,
                CellParameterEditorDocument cpd  => Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath),
                _ => null,
            };
            if (path is not null) _openDocsByPath[path] = dockable;
        }

        _scratchDocs.Clear();         _scratchDocs.AddRange(stillOpenScratchDocs);
        _scratchSymbols.Clear();      _scratchSymbols.AddRange(stillOpenScratchSymbols);
        _scratchLayouts.Clear();      _scratchLayouts.AddRange(stillOpenScratchLayouts);
        _scratchDataDisplays.Clear(); _scratchDataDisplays.AddRange(stillOpenScratchDataDisplays);

        // Session registries: NOT a blanket Clear() any more (see the per-dockable retire calls above)
        // — a surviving floated schematic/layout's own push-in session must stay registered. Any
        // session genuinely no longer referenced by anything (closed docked tab, or a popped-out frame
        // that was never re-opened) was already retired above / by its own existing pop-out path.
        ResetTechCache();

        CurrentWorkspacePath = null;   // fires OnCurrentWorkspacePathChanged → tree.ClearWorkspace()

        var newLayout = _factory.CreateDefaultLayout();
        _factory.InitLayout(newLayout);
        Layout = newLayout;
        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);

        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged newNpc)
            newNpc.PropertyChanged += OnDocumentDockPropertyChanged;
        WireAnalysesRun();

        Messages.Clear();
        Messages.Info("Workspace closed.");

        // R-dock-9: the collapsed toggle is a view preference and survives closing a workspace.
        ReapplyCollapsedStateIfNeeded();
    }

    /// <summary>Empty the Recent Workspaces list and save.</summary>
    [RelayCommand]
    private void ClearRecentWorkspaces()
    {
        _recentWorkspaces.Clear();
        SaveRecent();
        RebuildRecentMenuItems();
    }

    // Pushes cwsPath to the front of the recent list (MRU), deduplicates
    // (case-insensitive), caps at 10, persists, and rebuilds menus.
    private void PushRecent(string cwsPath)
    {
        _recentWorkspaces.RemoveAll(p =>
            string.Equals(p, cwsPath, StringComparison.OrdinalIgnoreCase));
        _recentWorkspaces.Insert(0, cwsPath);
        if (_recentWorkspaces.Count > 10)
            _recentWorkspaces.RemoveRange(10, _recentWorkspaces.Count - 10);
        SaveRecent();
        RebuildRecentMenuItems();
    }

    private void SaveRecent()
    {
        var list = _recentWorkspaces.Count > 0 ? new List<string>(_recentWorkspaces) : null;
        AppPreferencesIo.Update(p => p.RecentWorkspaces = list);
    }

    // ── Recently-Placed MRU ──────────────────────────────────────────────────

    private static List<SymbolKind> ParseMruPlaced(List<string>? stored)
    {
        if (stored is null) return [];
        var result = new List<SymbolKind>(stored.Count);
        foreach (var s in stored)
            if (Enum.TryParse<SymbolKind>(s, ignoreCase: true, out var k)) result.Add(k);
        return result;
    }

    private void OnComponentPlaced(SymbolKind kind) => PushMruPlaced(kind);

    private void PushMruPlaced(SymbolKind kind)
    {
        _recentlyPlaced.Remove(kind);
        _recentlyPlaced.Insert(0, kind);
        while (_recentlyPlaced.Count > MruPlacedCap)
            _recentlyPlaced.RemoveAt(_recentlyPlaced.Count - 1);

        _factory.PaletteTool?.SetMru(_recentlyPlaced);

        var list = _recentlyPlaced.Count > 0 ? _recentlyPlaced.Select(k => k.ToString()).ToList() : null;
        AppPreferencesIo.Update(p => p.RecentlyPlaced = list);
    }

    // Rebuilds the in-window menu ObservableCollection and fires RecentWorkspacesChanged
    // so the NativeMenu code-behind can sync.
    private void RebuildRecentMenuItems()
    {
        RecentMenuItems.Clear();

        foreach (var path in _recentWorkspaces)
        {
            var workspaceDir = Path.GetDirectoryName(path);
            var name = workspaceDir is not null ? Path.GetFileName(workspaceDir) : path;
            RecentMenuItems.Add(new MenuItem
            {
                Header = name,
                Command = OpenRecentWorkspaceCommand,
                CommandParameter = path,
            });
        }

        if (_recentWorkspaces.Count > 0)
        {
            RecentMenuItems.Add(new Separator());
            RecentMenuItems.Add(new MenuItem
            {
                Header = "Clear Recent",
                Command = ClearRecentWorkspacesCommand,
            });
        }

        OnPropertyChanged(nameof(HasRecentWorkspaces));
        RecentWorkspacesChanged?.Invoke();
    }

    // ---- Edit commands (route to the active document's stack) ---------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _activeUndoTarget?.UndoRedo.Undo();
    private bool CanUndo() => _activeUndoTarget?.UndoRedo.CanUndo ?? false;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _activeUndoTarget?.UndoRedo.Redo();
    private bool CanRedo() => _activeUndoTarget?.UndoRedo.CanRedo ?? false;

    private void SetActiveUndoTarget(IUndoableDocument? target)
    {
        if (_subscribedUndoStack is { } old)
            old.PropertyChanged -= OnActiveStackPropertyChanged;
        _subscribedUndoStack = null;

        if (_activeUndoDoc is { } oldDoc)
            oldDoc.ActiveViewModelChanged -= OnActiveDocFrameChanged;
        _activeUndoDoc = null;

        _activeUndoTarget = target;

        // A schematic tab can retarget its undo stack via Push In / Pop Out WITHOUT the active
        // dockable changing — follow those frame changes so Undo/Redo stay routed and enabled.
        if (target is SchematicDocument sd)
        {
            _activeUndoDoc = sd;
            sd.ActiveViewModelChanged += OnActiveDocFrameChanged;
        }

        HookActiveStack();
    }

    // The exact stack OnActiveStackPropertyChanged is subscribed to. Tracked separately because a
    // SchematicDocument's UndoRedo changes on Push In / Pop Out — we must unsubscribe the stack we
    // actually hooked, not whatever UndoRedo returns after a retarget.
    private UndoRedoStack? _subscribedUndoStack;

    // The active schematic doc whose ActiveViewModelChanged we're following (hierarchy retarget).
    private SchematicDocument? _activeUndoDoc;

    // (Re)subscribe to the active target's CURRENT stack and refresh Undo/Redo command + labels.
    private void HookActiveStack()
    {
        if (_subscribedUndoStack is { } old)
            old.PropertyChanged -= OnActiveStackPropertyChanged;
        _subscribedUndoStack = null;

        if (_activeUndoTarget?.UndoRedo is { } stack)
        {
            _subscribedUndoStack = stack;
            stack.PropertyChanged += OnActiveStackPropertyChanged;
        }

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    // Push In / Pop Out on the active schematic swaps its UndoRedo stack; re-hook to the new one.
    private void OnActiveDocFrameChanged(object? sender, EventArgs e) => HookActiveStack();

    private void OnActiveStackPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.CanUndo))
        {
            UndoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UndoDescription));
        }
        if (e.PropertyName is nameof(UndoRedoStack.CanRedo))
        {
            RedoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(RedoDescription));
        }
    }

    // Cut / Copy / Paste — route to the active document's clipboard implementation.
    // The canvas key handler (Ctrl/Cmd+C/X/V) is the primary path; these menu commands
    // provide the Edit-menu path so both work identically.
    [RelayCommand] private async Task Cut()   => await InvokeClipboardAsync(cut: true,  paste: false);
    [RelayCommand] private async Task Copy()  => await InvokeClipboardAsync(cut: false, paste: false);
    [RelayCommand] private async Task Paste() => await InvokeClipboardAsync(cut: false, paste: true);
    // No window-level "Select All": each editor owns a focus-gated Ctrl/Cmd+A handler (schematic canvas,
    // symbol-editor tunnel, data-display key bindings) so it never hijacks Ctrl+A in a docked panel's text box.

    private async Task InvokeClipboardAsync(bool cut, bool paste)
    {
        var clipboard = GetClipboard();
        if (clipboard is null) return;
        var active = _factory.DocumentDock?.ActiveDockable;
        if (active is SymbolEditorDocument symDoc)
        {
            if (paste) await symDoc.ViewModel.ClipboardPasteAsync(clipboard);
            else       await symDoc.ViewModel.ClipboardCopyAsync(clipboard, cut);
        }
        else if (active is SchematicDocument schDoc)
        {
            if (paste) await schDoc.ViewModel.ClipboardPasteAsync(clipboard);
            else       await schDoc.ViewModel.ClipboardCopyAsync(clipboard, cut);
        }
        else if (active is DataDisplayDocument ddDoc)
        {
            var win = ddDoc.ViewModel.Window;
            if (paste)    await win.InvokePasteAsync();
            else if (cut) await win.InvokeCutAsync();
            else          await win.InvokeCopyAsync();
        }
    }

    private IClipboard? GetClipboard()
    {
        var window = ResolveOwner(null);
        return window is not null ? TopLevel.GetTopLevel(window)?.Clipboard : null;
    }

    // ---- View commands -------------------------------------------------------

    [RelayCommand]
    private void ResetLayout()
    {
        // Preserve documents: re-host the existing DocumentDock and tool instances
        // into a fresh proportional skeleton.  Documents, active tab, and per-document
        // selection are kept; only panel positions/proportions are restored.
        var newLayout = _factory.CreateLayoutPreservingContent();
        _factory.InitLayout(newLayout);
        Layout = newLayout;
        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        // Resetting the layout to the default means showing the panels — leaving the collapsed
        // toggle armed here would produce a "reset" that still hides everything.
        DockersCollapsed   = false;
        _preCollapseLayout = null;

        Messages.Info("Layout reset to default.");
    }

    [RelayCommand] private void ZoomToFit()        { Messages.Info("Zoom to Fit: not yet implemented (6c)."); }
    // HideShowDockers lives in WorkspaceViewModel.Docking.cs — it is a real full-canvas toggle now.
    [RelayCommand] private void FitWindowsToFrame() { Messages.Info("Fit Windows to Frame: not yet implemented."); }

    [RelayCommand]
    private void ToggleMessagesRegion()
    {
        // Expand/show the Messages region (StatusMessages toolbar button).
        // Dock provides float/show; for now we just ensure Messages is active.
        if (_factory.MessagesTool is { } mt)
        {
            _factory.SetActiveDockable(mt);
            // SetFocusedDockable requires the parent IDock container; skip for 6b.
        }
    }

    // ---- Simulate commands ---------------------------------------------------

    /// <summary>
    /// Extracts the active schematic, writes netlist.cnl, then runs the engine chain
    /// (CnlReader → Elaborator → analysis engine → DataSet) on a background thread.
    /// Reports progress and results via Messages; holds DataSets for Phase 7.
    /// </summary>
    [RelayCommand]
    private async Task RunAnalysis()
    {
        var doc = (_factory.DocumentDock?.ActiveDockable as SchematicDocument) ?? _lastActiveSchematicDoc;
        if (doc is null) { Messages.Warning("Run: no schematic is active."); return; }
        await RunSchematicDocAsync(doc);
    }

    private async Task RunSchematicDocAsync(SchematicDocument activeDoc)
    {
        var testBenchName = activeDoc.Id;

        // Step 1: extract + write netlist.cnl (synchronous — fast).
        string netlistPath;
        string baseDir;
        try
        {
            IReadOnlyList<string> conflicts;
            (netlistPath, conflicts) = WriteNetlist(activeDoc.ViewModel.EditModel, testBenchName);
            baseDir = Path.GetDirectoryName(netlistPath)!;
            foreach (var conflict in conflicts)
                Messages.Warning($"Extraction: {conflict}");
            Messages.Success("Wrote netlist", netlistPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Netlist write failed: {ex.Message}");
            return;
        }

        // Step 2: run the engine on a background thread so the UI stays responsive.
        Messages.Info($"Running '{testBenchName}'…");
        string? workspaceRoot = CurrentWorkspacePath is not null
            ? Path.GetDirectoryName(CurrentWorkspacePath)
            : null;
        RunResult result;
        try
        {
            result = await Task.Run(() => SchematicRunService.RunNetlist(netlistPath, baseDirectory: workspaceRoot));
        }
        catch (Exception ex)
        {
            // Defensive — RunNetlist never throws, but guard anyway.
            Messages.Error($"Run failed unexpectedly: {ex.Message}");
            return;
        }

        // Step 3: surface the result.
        // Post engine/elaboration warnings first (present on Success and EngineError alike).
        foreach (var w in result.Warnings)
            Messages.Warning(w);

        switch (result.Status)
        {
            case RunStatus.NoAnalysis:
                Messages.Info(result.StatusMessage);
                break;
            case RunStatus.EngineError:
                Messages.Error(result.StatusMessage);
                break;
            case RunStatus.Success:
                Messages.Success(result.StatusMessage);
                var schematicKey = RunResultsWriter.SchematicKey(activeDoc.FilePath, activeDoc.Id);
                var written = RunResultsWriter.WriteRun(
                    baseDir,
                    schematicKey,
                    result.GroupedResults,
                    Messages,
                    activeDoc.ViewModel.EditModel.ResultsFileName);
                await RefreshOpenDataDisplaysAsync(written);
                if (written.Count > 0)
                    await AutoOpenOrCreateDataDisplayAsync(baseDir, schematicKey, written[0]);
                break;
        }

        // Hold DataSets for Phase 7 visualisation.
        _lastRunDataSets = result.DataSets;
    }

    [RelayCommand]
    private void StopAnalysis()
    {
        // Engine instances created by RunAnalysis are synchronous and do not expose
        // CancellationToken.  Stop is informational for v1 — the run will complete.
        Messages.Info("Stop: engine runs to completion (no cancellation support in v1).");
    }

    private void WireAnalysesRun()
    {
        if (_factory.AnalysesTool?.ListVm is { } listVm)
        {
            listVm.RunRequested -= OnAnalysesRunRequested;
            listVm.RunRequested += OnAnalysesRunRequested;
        }
    }

    private void OnAnalysesRunRequested()
    {
        var doc = (_factory.DocumentDock?.ActiveDockable as SchematicDocument) ?? _lastActiveSchematicDoc;
        if (doc is null) { Messages.Warning("Run: no schematic available."); return; }
        _ = RunSchematicDocAsync(doc);
    }

    /// <summary>
    /// Opens the "Setup Analyses…" modal dialog — the same <see cref="AnalysesListViewModel"/>
    /// the dock panel uses, so mutations in either host affect the same schematic.
    /// </summary>
    [RelayCommand]
    private async Task SetupAnalyses(Window? owner)
    {
        var listVm = _factory.AnalysesTool?.ListVm;
        if (listVm is null) return;
        var window = ResolveOwner(owner);
        if (window is null) return;
        var dialog = new Views.Dialogs.SetupAnalysesDialog(listVm);
        await dialog.ShowDialog(window);
    }

    /// <summary>
    /// Extracts the active schematic and writes netlist.cnl (no analysis is run), then opens it
    /// in the OS default editor. Enabled only when a schematic document is active. Not undoable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateNetlist))]
    private void GenerateNetlist()
    {
        if (_factory.DocumentDock?.ActiveDockable is not SchematicDocument activeDoc)
            return; // CanExecute guards this; defensive.

        var testBenchName = activeDoc.Id;

        string netlistPath;
        try
        {
            IReadOnlyList<string> conflicts;
            // ActiveViewModel = the cell currently being viewed (base schematic, or a pushed-in
            // sub-cell). WYSIWYG: generate a netlist for what the user is looking at.
            (netlistPath, conflicts) = WriteNetlist(activeDoc.ActiveViewModel.EditModel, testBenchName);
            foreach (var conflict in conflicts)
                Messages.Warning($"Extraction: {conflict}");
            Messages.Success("Wrote netlist", netlistPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Netlist write failed: {ex.Message}");
            return;
        }

        // Open in the OS default editor (no analysis).
        // TryOpenExternal returns false when the OS has no registered handler for .cnl.
        try
        {
            if (!TryOpenExternal(netlistPath))
                Messages.Warning("Couldn't open externally: no .cnl handler configured", netlistPath);
        }
        catch (Exception ex) { Messages.Warning($"Couldn't open externally: {ex.Message}", netlistPath); }
    }

    private bool CanGenerateNetlist()
        => _factory.DocumentDock?.ActiveDockable is SchematicDocument;

    // ── Netlist write (Phase 6e Step 4) ──────────────────────────────────────

    /// <summary>
    /// Extracts <paramref name="model"/> and writes one netlist.cnl (overwritten each
    /// run) to the workspace root when a workspace is open, or to the RecoveryManager
    /// scratch-session dir when no workspace is open. Atomic write (temp + rename).
    /// </summary>
    /// <returns>The absolute path written and any non-fatal extraction conflicts.</returns>
    private (string Path, IReadOnlyList<string> Conflicts) WriteNetlist(
        SchematicEditModel model, string testBenchName)
    {
        // Resolve destination: workspace root or scratch-session dir.
        string destDir;
        if (CurrentWorkspacePath is not null)
            destDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        else
        {
            destDir = _recovery.SessionDir;
            Directory.CreateDirectory(destDir); // session dir is created lazily
        }

        var targetPath = Path.Combine(destDir, "netlist.cnl");
        var tmpPath    = targetPath + ".tmp";

        var result = NetExtractor.Extract(model, testBenchName, cells: this);
        var header = $"netlist.cnl — generated from TestBench \"{testBenchName}\"" +
                     $" at {DateTime.UtcNow:O}";
        var text = CnlWriter.Write(result.TestBench, result.Library, header);

        File.WriteAllText(tmpPath, text, System.Text.Encoding.UTF8);
        File.Move(tmpPath, targetPath, overwrite: true);

        return (targetPath, result.Conflicts);
    }

    // ── ICellResolver implementation ──────────────────────────────────────────

    /// <summary>
    /// ICellResolver — resolves a cell instance to its primary schematic (WYSIWYG: the shared
    /// in-memory session if the cell is open anywhere, else the primary .csch from disk) plus the
    /// cell's declared parameter interface. Returns null when unresolvable (scratch parent with no
    /// directory, missing cell, or no primary schematic) — the extractor skips the instance.
    /// </summary>
    public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
    {
        var primaryPath = HierarchyResolver.ResolvePrimaryPath(cellInstance, containingModel);
        if (primaryPath is null) return null;

        // Memory-else-disk. GetOrCreateSession returns the shared session VM (registry) or loads
        // the .csch from disk and wires it up — SchematicDirectory is set exactly as Open/Push-In
        // do, so nested cell instance resolution works and unsaved edits are visible.
        var schematic = GetOrCreateSession(primaryPath).EditModel;

        // primaryPath = …/<cell>/schematic/<file>.csch → cell dir is two levels up.
        var cellDir  = Path.GetDirectoryName(Path.GetDirectoryName(primaryPath))!;
        var cellName = Path.GetFileName(cellDir);

        IReadOnlyList<ParameterDeclaration> parameters = [];
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (File.Exists(ccellPath))
        {
            try
            {
                parameters = CellPersistence.LoadFromFile(ccellPath).Parameters
                    .Select(p => new ParameterDeclaration(
                        p.Name,
                        p.DefaultExpression,
                        string.IsNullOrEmpty(p.Unit) ? null : p.Unit,
                        hidden: !p.ShowOnSchematic))
                    .ToList();
            }
            catch { /* malformed .ccell → no declared params; instance overrides still apply */ }
        }

        return new CellResolution(cellName, schematic, parameters);
    }

    // ---- Help ----------------------------------------------------------------

    [RelayCommand]
    private async Task ShowAbout(Window? owner)
    {
        if (owner is null) return;
        await new Views.Dialogs.AboutWindow().ShowDialog(owner);
    }

    [RelayCommand]
    private async Task ShowSettings(Window? owner)
    {
        if (owner is null) return;
        var workspaceDir = CurrentWorkspacePath is not null
            ? Path.GetDirectoryName(CurrentWorkspacePath)
            : null;
        var w = new Views.Dialogs.SettingsView(workspaceDir);
        w.Show(owner);
        await Task.CompletedTask;
    }

    // ---- New Tab command (Ctrl+T) --------------------------------------------

    [RelayCommand]
    private void NewTab()
    {
        var doc = new StubDocument($"Tab {System.Guid.NewGuid().ToString("N")[..4]}");
        _factory.OpenDocument(doc);
    }

    // ---- New Schematic (⇧⌘N / Ctrl+Shift+N) — scratch, no workspace needed --

    /// <summary>
    /// Creates an in-memory scratch schematic tab immediately, with no workspace or
    /// save prompt required. The tab is dirty from creation and tracked in _scratchDocs.
    /// Always enabled (no workspace requirement). Save/materialize are steps 2+.
    /// NOTE: closing a scratch tab in step 1 loses it — the close-prompt comes in step 3.
    /// </summary>
    [RelayCommand]
    private void NewScratchSchematic()
    {
        var title = NextScratchSchematicTitle();
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model, Messages);
        vm.SetPlacementService(PlacementService);
        vm.ComponentPlaced         += OnComponentPlaced;
        vm.CellSymbolAutoGenerated += OnCellSymbolAutoGenerated;
        vm.WorkspaceRootProvider    = () => CurrentWorkspaceRoot;
        // filePath = null → scratch; IsScratch = true, IsDirty = false (starts clean), Title = "<title>"
        var doc   = new SchematicDocument(title, vm) { Messages = Messages, Hierarchy = this };

        _scratchDocs.Add(doc);
        _factory.OpenDocument(doc);
    }

    /// <summary>
    /// Returns the lowest free "Untitled-Schematic-N" title across all current scratch
    /// and path-keyed open schematic documents.
    /// </summary>
    private string NextScratchSchematicTitle()
    {
        const string prefix = "Untitled-Schematic-";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchDocs)
            used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is SchematicDocument sd)
                used.Add(sd.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    // ---- New Symbol (scratch) ------------------------------------------------

    /// <summary>
    /// Creates an in-memory scratch symbol tab immediately, with no workspace or
    /// cell required. Save/materialize happen on first ⌘S.
    /// brief-file-menu-restructure.md §1.1: this is the SOLE creation path for "New Symbol" — the
    /// menu command below just exposes it; on-launch (ExecuteLaunchActionAsync) calls the same method.
    /// </summary>
    [RelayCommand]
    private void NewScratchSymbol()
    {
        var title    = NextScratchSymbolTitle();
        var editable = new EditableSymbol { UserEditable = true };
        var vm       = new SymbolEditorViewModel(editable);
        vm.SymbolSaved += OnSymbolSaved;
        vm.SaveError   += OnSymbolSaveError;
        var doc = new SymbolEditorDocument(title, vm);  // filePath = null → scratch
        _scratchSymbols.Add(doc);
        _factory.OpenDocument(doc);
        HookSymbolCellDirty(doc);
    }

    /// <summary>
    /// Returns the lowest free "Untitled-Symbol-N" title across all current scratch
    /// and path-keyed open symbol editor documents.
    /// </summary>
    private string NextScratchSymbolTitle()
    {
        const string prefix = "Untitled-Symbol-";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchSymbols)
            used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is SymbolEditorDocument sd)
                used.Add(sd.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    // ---- New Layout (File menu) — scratch, no workspace needed --------------

    /// <summary>
    /// Creates an in-memory scratch layout tab immediately, with no workspace or cell required.
    /// Save/materialize happen on first save. TechRef stays null (§1's "null means workspace
    /// default" convention) — DisplayUnit/SnapDbu are seeded from the resolved workspace default
    /// technology; with no technology, L0b's hardcoded defaults apply.
    /// </summary>
    [RelayCommand]
    private void NewLayout()
    {
        var title      = NextScratchLayoutTitle();
        var resolution = ResolveTechFor(techRef: null, clayPath: null);
        var tech       = resolution.Tech;

        var model = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = tech?.DefaultDisplayUnit ?? LayoutUnit.Um,
            SnapDbu      = tech?.DefaultSnapDbu ?? 1000,
            AngleMode    = AngleMode.AnyAngle,
            TechRef      = null,
        };
        var vm = new LayoutEditorViewModel(model, messageSink: Messages);
        vm.ApplyTechResolution(resolution);
        vm.SaveError += OnLayoutSaveError;
        vm.RequestAddLayerToTechnology += OnLayoutRequestAddLayerToTechnology;
        WireRetargetSeam(vm);
        var doc = new LayoutDocument(title, vm) { Hierarchy = this };  // filePath = null → scratch
        _scratchLayouts.Add(doc);
        _factory.OpenDocument(doc);
        HookLayoutCellDirty(doc);
    }

    /// <summary>
    /// Returns the lowest free "Untitled-Layout-N" title across all current scratch
    /// and path-keyed open layout documents.
    /// </summary>
    private string NextScratchLayoutTitle()
    {
        const string prefix = "Untitled-Layout-";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchLayouts)
            used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is LayoutDocument ld)
                used.Add(ld.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Opens a .csch file and loads it into a docked Schematic tab — mirrors
    /// <see cref="OpenLayoutFile"/> exactly. brief-file-menu-restructure.md §1.2: File → Open →
    /// "Open Schematic…" didn't exist as a menu command before this brief (schematics were previously
    /// reachable only via the project tree or New Schematic); reuses the existing
    /// <see cref="OpenOrActivateSchematic"/> path, no new opening logic.</summary>
    [RelayCommand]
    private async Task OpenSchematicFile(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Schematic",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Schematic") { Patterns = ["*.csch"] },
                new FilePickerFileType("All Files")           { Patterns = ["*.*"] },
            ],
        });

        if (result.Count == 0) return;

        OpenOrActivateSchematic(result[0].Path.LocalPath);
    }

    /// <summary>Opens a .clay file and loads it into a docked Layout Editor tab.</summary>
    [RelayCommand]
    private async Task OpenLayoutFile(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Layout",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Layout") { Patterns = ["*.clay"] },
                new FilePickerFileType("All Files")        { Patterns = ["*.*"] },
            ],
        });

        if (result.Count == 0) return;

        OpenOrActivateLayout(result[0].Path.LocalPath);
    }

    // ── Import GDSII Library (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §8) ──────────────
    // GdsiiImport does the actual read/reconcile/CellFolder-creation work; this method is only file
    // picking (UI firewall), workspace/technology context, and the layer-mapping dialog bridge.

    [RelayCommand(CanExecute = nameof(CanImportGdsiiLibrary))]
    private Task ImportGdsiiLibrary(Window? owner) => ImportGdsiiLibraryAsync(owner);
    private bool CanImportGdsiiLibrary() => CurrentWorkspacePath is not null;

    private async Task ImportGdsiiLibraryAsync(Window? owner)
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(owner);
        if (window is null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import GDSII Library",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GDSII Stream") { Patterns = ["*.gds", "*.gdsii", "*.sf"] },
                new FilePickerFileType("All Files")    { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var techRes = ResolveTechFor(null, null); // the workspace's own default technology

        CircuitRF.Ui.Layout.Interchange.GdsiiImport.ImportResult result;
        try
        {
            result = await Task.Run(() =>
            {
                using var stream = File.OpenRead(files[0].Path.LocalPath);
                return CircuitRF.Ui.Layout.Interchange.GdsiiImport.Import(
                    stream, workspaceDir, techRes.Tech, LayoutUnits.DefaultDbuPerMicron,
                    preferSourceResolution: false,
                    resolveLayerMapping: rows =>
                    {
                        var settled = Dispatcher.UIThread
                            .InvokeAsync(() => ResolveGdsiiLayerMappingAsync(window, techRes.Tech, rows))
                            .GetAwaiter().GetResult();
                        return settled is null ? null : LayoutLayerMapping.BuildChoices(settled);
                    });
            });
        }
        catch (Exception ex)
        {
            Messages.Error($"Import GDSII: {ex.Message}");
            return;
        }

        if (result.Cancelled)
        {
            Messages.Info("Import GDSII cancelled — nothing was created.");
            return;
        }

        foreach (var msg in result.Messages) Messages.Info(msg);
        _factory.ProjectTreeTool?.Refresh();

        // item 7/R-fix-6: cells WERE always created correctly under the workspace, and the tree WAS
        // always refreshed (both confirmed by direct code reading + the existing gate-10 test) — the
        // actual gap was legibility: a bare "Imported N cell(s)" message, with nothing opened, reads as
        // "nothing appears" even though the import fully succeeded. Name what happened and where, and
        // open the top-level cell automatically when it's unambiguous.
        var workspaceName = Path.GetFileName(Path.GetDirectoryName(CurrentWorkspacePath));
        var sourceFileName = Path.GetFileName(files[0].Path.LocalPath);
        var cellNames = result.CreatedCellDirs.Select(Path.GetFileName).ToList();
        Messages.Success(
            $"Imported {cellNames.Count} cell(s) from \"{sourceFileName}\" into \"{workspaceName}\": " +
            $"{FormatTruncatedNameList(cellNames)}. {DescribeTopLevelCells(result.TopLevelCellDirs)}");

        if (result.TopLevelCellDirs.Count == 1)
            OpenPrimaryLayoutIfResolvable(result.TopLevelCellDirs[0]);
    }

    /// <summary>"a, b, c, … (N more)" — first 3 verbatim, the rest counted rather than listed, per
    /// R-fix-6's own example. Shared by any import-completion message that needs to name what was
    /// created without flooding the log for a large library. Internal (not private) so it is directly
    /// unit-testable — <see cref="WorkspaceViewModel"/> itself cannot be constructed headlessly.</summary>
    internal static string FormatTruncatedNameList(IReadOnlyList<string?> names)
    {
        const int shown = 3;
        var quoted = names.Take(shown).Select(n => $"\"{n}\"");
        var text = string.Join(", ", quoted);
        return names.Count > shown ? $"{text}, … ({names.Count - shown} more)" : text;
    }

    /// <summary>Names which cell(s) will actually open with the design visible on screen — the thing
    /// the user actually wants after an import, per R-fix-6. Ordinarily exactly one; a pathological
    /// all-mutually-referenced library (no structure is ever "outermost") says so explicitly rather
    /// than guessing one to open.</summary>
    internal static string DescribeTopLevelCells(IReadOnlyList<string> topLevelCellDirs) => topLevelCellDirs.Count switch
    {
        0 => "No distinct top-level cell — every structure is referenced by another.",
        1 => $"Top-level cell: \"{Path.GetFileName(topLevelCellDirs[0])}\".",
        _ => $"Top-level cells: {string.Join(", ", topLevelCellDirs.Select(d => $"\"{Path.GetFileName(d)}\""))}.",
    };

    /// <summary>Opens <paramref name="cellDir"/>'s primary layout, if it resolves — silently does
    /// nothing otherwise (an import always has a resolvable primary for every cell it just wrote, so
    /// this is a defensive no-op path, not a case expected to fire).</summary>
    private void OpenPrimaryLayoutIfResolvable(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null)
            return;
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        OpenOrActivateLayout(Path.Combine(layoutDir, primary.ResolvedName));
    }

    /// <summary>Shows the shared L1g layer-mapping dialog (never a second reconciliation UI) for the
    /// GDSII import path. Returns null (abort the whole import) when <paramref name="destTech"/> is
    /// itself null and rows would otherwise have nowhere sensible to map to — but rows are still
    /// accepted as-is (Keep-as-unknown) since there is truly no destination to reconcile against.</summary>
    private async Task<IReadOnlyList<LayerMappingRow>?> ResolveGdsiiLayerMappingAsync(
        Window owner, Technology? destTech, IReadOnlyList<LayerMappingRow> rows)
    {
        if (destTech is null) return rows;
        var dialog = new LayerMappingDialog("Import GDSII — Layer Mapping", "GDSII", destTech, rows);
        var result = await dialog.ShowDialog<LayerMappingDialogResult?>(owner);
        return result?.Rows;
    }

    // ── Import DXF Library (docs/sonnet-briefs/brief-L4b-dxf-interchange.md §2) ───────────────────
    // DxfImport does the actual read/reconcile/CellFolder-creation work; this method is only file
    // picking (UI firewall), workspace/technology context, the units prompt, and the layer-mapping
    // dialog bridge — mirrors ImportGdsiiLibraryAsync exactly, with one extra prompt (R-L4b-4).

    [RelayCommand(CanExecute = nameof(CanImportDxfLibrary))]
    private Task ImportDxfLibrary(Window? owner) => ImportDxfLibraryAsync(owner);
    private bool CanImportDxfLibrary() => CurrentWorkspacePath is not null;

    private async Task ImportDxfLibraryAsync(Window? owner)
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(owner);
        if (window is null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import DXF Library",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("DXF Drawing")  { Patterns = ["*.dxf"] },
                new FilePickerFileType("All Files")    { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var techRes = ResolveTechFor(null, null); // the workspace's own default technology

        CircuitRF.Ui.Layout.Interchange.DxfImport.ImportResult result;
        try
        {
            result = await Task.Run(() =>
            {
                using var stream = File.OpenRead(files[0].Path.LocalPath);
                return CircuitRF.Ui.Layout.Interchange.DxfImport.Import(
                    stream, workspaceDir, techRes.Tech, LayoutUnits.DefaultDbuPerMicron,
                    resolveUnits: rawInsUnits => Dispatcher.UIThread
                        .InvokeAsync(() => ResolveDxfUnitsAsync(window))
                        .GetAwaiter().GetResult(),
                    resolveLayerMapping: rows =>
                    {
                        var settled = Dispatcher.UIThread
                            .InvokeAsync(() => ResolveGdsiiLayerMappingAsync(window, techRes.Tech, rows))
                            .GetAwaiter().GetResult();
                        return settled is null ? null : LayoutLayerMapping.BuildChoices(settled);
                    });
            });
        }
        catch (Exception ex)
        {
            Messages.Error($"Import DXF: {ex.Message}");
            return;
        }

        if (result.Cancelled)
        {
            foreach (var msg in result.Messages) Messages.Info(msg);
            Messages.Info("Import DXF cancelled — nothing was created.");
            return;
        }

        foreach (var msg in result.Messages) Messages.Info(msg);
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success($"Imported {result.CreatedCellDirs.Count} cell(s) from DXF.");
    }

    /// <summary>R-L4b-4's own prompt — shown only when the file's $INSUNITS cannot be trusted as-is.
    /// Returns the chosen $INSUNITS value, or null to abort the whole import.</summary>
    private async Task<int?> ResolveDxfUnitsAsync(Window owner)
    {
        var dialog = new CircuitRF.Ui.Views.Dialogs.DxfUnitsPromptDialog();
        return await dialog.ShowDialog<int?>(owner);
    }

    private void OpenOrActivateLayout(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        // R-L5g-9/10: a generated cell is a regeneration cache, never independently-openable content —
        // this closes the "second entry point" R-L5g-5's own investigation named: opening a generated
        // cell's .clay directly (file picker, a stale .cws OpenDocuments entry from before this fix)
        // used to bypass push-in's PCellOrigin gate entirely, since it isn't push-in at all. There is
        // nothing in it a user can usefully edit anyway (LayoutEditorViewModel.IsPCellReadOnly already
        // refuses every mutation) — refuse opening it as a document at all, with a reason, per R13a.
        if (GeneratedCellStore.IsUnderGeneratedCellsFolder(absolutePath))
        {
            Messages.Warning(
                "This is a generated PCell cell (internal cache), not user content — " +
                "edit its parameters through the placed instance's own Properties Inspector instead.");
            return;
        }

        try
        {
            // L3b: funnel through the session registry so a cell simultaneously open as its own tab
            // and pushed into elsewhere shares one session — GetOrCreateLayoutSession does the
            // load-and-wire that used to happen inline here.
            var vm  = GetOrCreateLayoutSession(absolutePath);
            var doc = new LayoutDocument(Path.GetFileName(absolutePath), vm, absolutePath) { Hierarchy = this };
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookLayoutCellDirty(doc);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open layout: {ex.Message}");
        }
    }

    // A layout save failed (e.g. read-only / unwritable location) — surface it instead of crashing.
    private void OnLayoutSaveError(string message) => Messages.Error(message);

    // L1f — a paste's "Add to the technology" choice installs a live (unsaved) technology override,
    // exactly mirroring OnTechLiveChanged's SetLive call for the .ctech editor itself.
    private void OnLayoutRequestAddLayerToTechnology(string path, Technology tech) => _techCache.SetLive(path, tech);

    // L1g — Change Technology needs to enumerate the workspace's tech/ folder and resolve
    // "(Workspace default)" without LayoutEditorViewModel depending on WorkspaceViewModel directly.
    // Wired once per document at every `new LayoutEditorViewModel(...)` call site, alongside the
    // RequestAddLayerToTechnology seam above.
    private void WireRetargetSeam(LayoutEditorViewModel vm)
    {
        vm.FallbackWorkspaceTechDir = CurrentWorkspacePath is null
            ? null : Path.Combine(Path.GetDirectoryName(CurrentWorkspacePath)!, "tech");
        // brief-foreign-documents.md R-fgn-3: read vm.CurrentLayoutPath LIVE (never captured), so a
        // Save-As that moves this document into a different workspace is picked up automatically —
        // ResolveTechFor itself does the ancestor-.cws walk from whatever path it's handed.
        vm.ResolveWorkspaceDefaultTech = () => ResolveTechFor(techRef: null, clayPath: vm.CurrentLayoutPath);
        vm.ResolveTechAt = (techRef, clayDir) => ResolveTechFor(techRef, Path.Combine(clayDir, "x.clay"));
        // §4 marking: read CurrentWorkspacePath LIVE too, so switching workspaces updates IsForeign/
        // SourceWorkspaceName on every already-open document without re-wiring anything.
        vm.CurrentWorkspaceRootDirProvider = () => CurrentWorkspacePath is null
            ? null : Path.GetDirectoryName(CurrentWorkspacePath);
    }

    // ---- Technology (.ctech) editor (L0d) --------------------------------------

    /// <summary>
    /// File-menu "New Technology…" entry point. Delegates to the same core logic as the
    /// tree-header/context-menu command (<see cref="NewTechnologyAsync"/>).
    /// </summary>
    [RelayCommand]
    private async Task NewTechnology(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;
        await NewTechnologyCoreAsync(window);
    }

    /// <inheritdoc/>
    public async Task NewTechnologyAsync(ProjectTreeNodeViewModel node)
    {
        var window = ResolveOwner(null);
        if (window is null) return;
        await NewTechnologyCoreAsync(window);
    }

    private async Task NewTechnologyCoreAsync(Window window)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Info("Open or create a workspace first.");
            return;
        }

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var techDir      = Path.Combine(workspaceDir, "tech");
        var suggested    = NextFreeTechName(techDir);

        var result = await new NewTechnologyDialog(techDir, suggested).ShowDialog<NewTechnologyResult?>(window);
        if (result is null) return;

        try
        {
            Directory.CreateDirectory(techDir);

            var tech = result.Starter switch
            {
                NewTechnologyStarter.Mmic  => StarterTechnologies.MmicGaAs(),
                NewTechnologyStarter.Empty => StarterTechnologies.Empty(),
                _                          => StarterTechnologies.Pcb2Layer(),
            };
            tech.Name = result.Name;

            var techPath = Path.Combine(techDir, $"{result.Name}.ctech");
            TechPersistence.SaveToFile(techPath, tech);

            if (result.SetAsDefault)
            {
                CwsFile cws;
                try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
                catch { cws = new CwsFile(); }
                cws.DefaultTechRef = Path.GetRelativePath(workspaceDir, techPath);
                WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);
                _techCache.Invalidate(techPath);
                RefreshAllOpenLayoutTech();
            }

            _factory.ProjectTreeTool?.Refresh();
            OpenOrActivateTech(techPath);
            Messages.Success("Created technology", techPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create technology: {ex.Message}");
        }
    }

    private static string NextFreeTechName(string techDir)
    {
        for (int n = 1; n <= 9999; n++)
        {
            var candidate = $"Untitled-Technology-{n}";
            if (!File.Exists(Path.Combine(techDir, $"{candidate}.ctech")))
                return candidate;
        }
        return "Untitled-Technology";
    }

    /// <summary>
    /// Opens or activates a .ctech editor tab. A .ctech that fails to load (corrupt JSON, a newer
    /// <c>FormatVersion</c>) surfaces the error and does NOT open a blank document — silently
    /// offering an empty editor over a file that couldn't be parsed invites saving over it.
    /// </summary>
    /// <summary>
    /// Opens (or focuses) a <c>.ctech</c> as an ordinary editor document. Public so a surface that
    /// is not the project tree can offer it — the Layout Editor's own Technology ▾ ▸ Edit, which
    /// reaches this through the desktop-windows scan since its DataContext is a LayoutDocument.
    /// </summary>
    public void OpenTechnologyDocument(string absolutePath) => OpenOrActivateTech(absolutePath);

    private void OpenOrActivateTech(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        try
        {
            var tech = TechPersistence.LoadFromFile(absolutePath);
            var vm   = new TechEditorViewModel(absolutePath, tech);
            vm.TechSaved += OnTechSaved;
            vm.SaveError += OnTechSaveError;
            vm.TechLiveChanged += OnTechLiveChanged;
            var doc = new TechDocument(Path.GetFileName(absolutePath), vm, absolutePath);
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookTechFileDirty(doc);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open technology: {ex.Message}");
        }
    }

    /// <summary>Reflects a .ctech editor's dirty state onto its tree node's dirty dot — mirrors
    /// <see cref="HookLayoutCellDirty"/>, except a technology has no owning cell: the node
    /// updated is the .ctech file node itself.</summary>
    private void HookTechFileDirty(TechDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TechEditorViewModel.IsDirty))
                _factory.ProjectTreeTool?.SetTechFileDirty(doc.FilePath, doc.ViewModel.IsDirty);
        };
    }

    // The single call that fires L0c's live-refresh seam: every open layout resolved against
    // this path re-resolves via TechnologyCache.TechnologyChanged → OnTechnologyChanged. This also
    // clears any live override for the path (Invalidate now drops both — see TechnologyCache) so
    // disk and the cache agree once saved; no separate ClearLive call is needed here.
    private void OnTechSaved(string path)
    {
        _techCache.Invalidate(path);
        Messages.Success("Saved", path);
    }

    // A technology save failed (e.g. read-only / unwritable location) — surface it instead of crashing.
    private void OnTechSaveError(string message) => Messages.Error(message);

    // ---- Live technology edits (brief-L1-fix-path-seams-and-live-tech.md §2) -----------------

    // Coalesce, don't throttle: a multi-selection apply in the .ctech editor can fire several
    // TechLiveChanged events in one user gesture (each commits on its own focus-loss/Enter). Only
    // the LATEST clone per path survives in this dictionary; one dispatcher post per burst applies
    // them all, so the canvas repaints once per burst rather than once per commit.
    private readonly Dictionary<string, Technology> _pendingTechLive = new(StringComparer.OrdinalIgnoreCase);
    private bool _techLiveFlushScheduled;

    private void OnTechLiveChanged(string path, Technology clone)
    {
        _pendingTechLive[path] = clone;
        if (_techLiveFlushScheduled) return;
        _techLiveFlushScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(FlushPendingTechLive, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingTechLive()
    {
        _techLiveFlushScheduled = false;
        var snapshot = _pendingTechLive.ToList();
        _pendingTechLive.Clear();
        foreach (var (path, tech) in snapshot)
            _techCache.SetLive(path, tech);
    }

    // ---- Data Display commands ----------------------------------------------

    /// <summary>
    /// Creates a scratch Data Display tab immediately.
    /// Always enabled; no workspace required (scratch-first, same as New Schematic/Symbol).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportData))]
    private async Task ExportData()
    {
        var resultsRoot = GetResultsRoot();
        var vm = new CircuitRF.Ui.DataDisplay.ViewModels.DataExporterViewModel(resultsRoot, null);
        await DataExporterDialog.ShowAsync(null, vm);
    }

    private bool CanExportData() => GetResultsRoot() is not null;

    // ---- Layout export commands (item 8: File → Export → GDSII/DXF/Gerber) ------------------------
    // GDSII/DXF export logic lives entirely in the active LayoutEditorView's own code-behind (file
    // picking, the fidelity/options dialogs — see LayoutEditorView.axaml.cs's own header comment on
    // OnExportGdsiiAsync/OnExportDxfAsync). These commands only decide whether a layout document is
    // active and, if so, ask it to run ITS OWN export via LayoutDocument.RequestExportGdsii/
    // RequestExportDxf — never a second export code path (item 5/R-fix-4's own "route every entry
    // point through the same accessor").

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportGdsii() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportGdsii();

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportDxf() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportDxf();

    /// <summary>R-menu-4: reads the PER-WINDOW active document (<see cref="ResolveActiveDocumentForCommands"/>),
    /// not the shell's own <c>DocumentDock.ActiveDockable</c> directly — so this stays correct while a
    /// torn-off layout window has focus.</summary>
    private bool IsLayoutDocumentActive() => ResolveActiveDocumentForCommands() is LayoutDocument;

    /// <summary>Gates "Save Schematic As…" — disabled (greyed out) unless a schematic document is the
    /// active document, mirroring <see cref="IsLayoutDocumentActive"/> exactly (incl. its R-menu-4
    /// per-window resolution).</summary>
    private bool IsSchematicDocumentActive() => ResolveActiveDocumentForCommands() is SchematicDocument;

    /// <summary>Gates "Save Symbol As…" — disabled (greyed out) unless a symbol document is the
    /// active document, mirroring <see cref="IsLayoutDocumentActive"/> exactly (incl. its R-menu-4
    /// per-window resolution).</summary>
    private bool IsSymbolDocumentActive() => ResolveActiveDocumentForCommands() is SymbolEditorDocument;

    /// <summary>Gerber export (docs/sonnet-briefs/brief-L4c-gerber-export.md) — mirrors ExportGdsii/
    /// ExportDxf exactly: this command only decides whether a layout document is active and asks it to
    /// run its OWN export via LayoutDocument.RequestExportGerber (file picking, the fidelity dialog, and
    /// the actual GerberExport.Analyze/Write calls all live in LayoutEditorView's own code-behind).</summary>
    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportGerber() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportGerber();

    [RelayCommand]
    private void NewDataDisplay()
    {
        var title = NextDataDisplayTitle();
        var vm    = new DataDisplayDocumentViewModel();
        var doc   = new DataDisplayDocument(title, vm);
        _scratchDataDisplays.Add(doc);
        vm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        vm.Window.GetResultsRootAction = GetResultsRoot;
        WireDataDisplayLibraryEvents(vm);
        _factory.OpenDocument(doc);
        // Seed the toolbar combo with the most-recent run.npy.
        vm.Window.RefreshAvailableDataSources();
        _ = vm.Window.DataSourceLibrary.SelectDataSourceAsync(
            vm.Window.DataSourceLibrary.MostRecentRunRef());
    }

    /// <summary>
    /// Opens a .cdd file as a new Data Display document tab.
    /// Deduplicates against already-open path-keyed documents.
    /// Injected into every DisplayWindowViewModel so "Open Display" creates a new tab
    /// instead of replacing the current document's content.
    /// </summary>
    private async Task OpenDataDisplayFromFileAsync(string path, Stream stream)
        => await OpenOrActivateDataDisplayCoreAsync(Path.GetFullPath(path), stream);

    /// <summary>
    /// Opens (or activates) a .cdd by absolute path. Used by the restore path and
    /// tree double-click — fire-and-forget; errors surface via Messages.
    /// </summary>
    private void OpenOrActivateDataDisplay(string absPath)
    {
        var abs = Path.GetFullPath(absPath);
        _ = RunAsync();
        async Task RunAsync()
        {
            try   { await OpenOrActivateDataDisplayCoreAsync(abs, stream: null); }
            catch (InvalidDataException ex) { Messages.Error($"Cannot open Data Display: {ex.Message}"); }
            catch (Exception ex)            { Messages.Error($"Failed to open Data Display: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Core open-or-activate: dedup, create, inject, open, load (stream or path), materialize.
    /// Null stream reads the file from disk. Does NOT catch — callers handle errors.
    /// </summary>
    private async Task OpenOrActivateDataDisplayCoreAsync(string absPath, Stream? stream)
    {
        if (_openDocsByPath.TryGetValue(absPath, out var existing))
        {
            _factory.SetActiveDockable(existing);
            return;
        }

        string title = Path.GetFileNameWithoutExtension(absPath);
        var newVm  = new DataDisplayDocumentViewModel();
        var newDoc = new DataDisplayDocument(title, newVm, filePath: absPath);
        _openDocsByPath[absPath] = newDoc;
        newVm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        newVm.Window.GetResultsRootAction = GetResultsRoot;
        WireDataDisplayLibraryEvents(newVm);
        _factory.OpenDocument(newDoc);

        // format_version check throws InvalidDataException on mismatch.
        await newVm.Window.LoadAllAsync(absPath, stream);
        newDoc.Materialize(absPath);
    }

    /// <summary>
    /// Returns the lowest free "Untitled-Display-N" title across all current scratch
    /// and path-keyed open data display documents.
    /// </summary>
    private string NextDataDisplayTitle()
    {
        const string prefix = "Untitled-Display-";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchDataDisplays)
            used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is DataDisplayDocument dd)
                used.Add(dd.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    // Results root MUST mirror WriteNetlist's destination: workspace root when a workspace is open,
    // otherwise the scratch recovery-session dir — so a scratch sim's results (written under
    // <SessionDir>/results/) are discoverable in the Data Display without saving the .csch/.cdd.
    private string? GetResultsRoot()
        => RunResultsWriter.ResolveResultsRoot(CurrentWorkspacePath, _recovery.SessionDir);

    private IReadOnlyList<string> GetKnownTouchstoneFiles()
    {
        if (CurrentWorkspacePath is not { } cwsPath) return Array.Empty<string>();
        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return Array.Empty<string>(); }
        // R-stb-10/11: a stored ref may be workspace-relative (with `/` separators) or absolute;
        // this contract is "absolute paths", so resolve before handing them to the data-source
        // library. Any .sNp for N >= 2 qualifies — nothing here is specific to .s2p (R-stb-9).
        string root = System.IO.Path.GetDirectoryName(cwsPath) ?? "";
        return cws.KnownFiles
            .Where(p =>
            {
                var ext = System.IO.Path.GetExtension(p);
                return ext.StartsWith(".s", StringComparison.OrdinalIgnoreCase)
                    && (ext.EndsWith("p", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".snp", StringComparison.OrdinalIgnoreCase));
            })
            .Select(p => WorkspaceRefs.Resolve(p, root))
            .ToList();
    }

    private IReadOnlyList<string> GetKnownLoadpullFiles()
    {
        if (CurrentWorkspacePath is not { } cwsPath) return Array.Empty<string>();
        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return Array.Empty<string>(); }
        string lpRoot = System.IO.Path.GetDirectoryName(cwsPath) ?? "";
        return cws.KnownFiles
            .Where(p =>
            {
                var ext = System.IO.Path.GetExtension(p);
                return string.Equals(ext, ".spl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".lpcwave", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p => WorkspaceRefs.Resolve(p, lpRoot))
            .ToList();
    }

    // ---- Data Display library event wiring ---------------------------------

    private void WireDataDisplayLibraryEvents(DataDisplayDocumentViewModel docVm)
    {
        var lib = docVm.Window.DataSourceLibrary;
        lib.ResultsRootProvider     = GetResultsRoot;
        lib.KnownTouchstoneProvider = GetKnownTouchstoneFiles;
        lib.KnownLoadpullProvider   = GetKnownLoadpullFiles;
    }

    // ---- Symbol Editor commands ---------------------------------------------

    /// <summary>Opens the Symbol Editor docked on a built-in Resistor symbol (read-only).</summary>
    [RelayCommand]
    private void OpenSymbolEditorDocked()
    {
        var editable = EditableSymbol.FromSymbol(BuiltInSymbols.Primitives(SymbolKind.Resistor));
        editable.UserEditable = false;  // built-ins are read-only
        var vm  = new SymbolEditorViewModel(editable);
        var doc = new SymbolEditorDocument("Symbol Editor [Resistor]", vm);
        _factory.OpenDocument(doc);
    }

    /// <summary>Opens the Symbol Editor as a standalone tear-off window on a built-in Inductor symbol (read-only).</summary>
    [RelayCommand]
    private void OpenSymbolEditorWindow()
    {
        var editable = EditableSymbol.FromSymbol(BuiltInSymbols.Primitives(SymbolKind.Inductor));
        editable.UserEditable = false;  // built-ins are read-only
        var vm     = new SymbolEditorViewModel(editable);
        var doc    = new SymbolEditorDocument("Symbol Editor [Inductor]", vm);
        var window = new CircuitRF.Ui.Views.SymbolEditorWindow(doc);
        window.Show();
    }

    /// <summary>Opens a .csym file and loads it into a docked Symbol Editor tab.</summary>
    [RelayCommand]
    private async Task OpenSymbolFile(Window? owner)
    {
        // NativeMenu passes a null owner on macOS ($parent[Window] is null there) — resolve the host
        // window the same way the other File commands do, or the picker never opens.
        var window = ResolveOwner(owner);
        if (window is null) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Symbol",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Symbol") { Patterns = ["*.csym"] },
                new FilePickerFileType("All Files")        { Patterns = ["*.*"] },
            ],
        });

        if (result.Count == 0) return;

        // Delegate to the shared open path so File→Open Symbol gets the same wiring as the project tree:
        // dedup (don't reopen the same file twice), SymbolSaved/SaveError routing (so save errors surface),
        // and the cell-dirty hook. This works for an ORPHAN .csym (one not inside a cell) too — it opens
        // materialized at its own path, so editing and saving writes straight back to that file on disk.
        OpenOrActivateSymbol(result[0].Path.LocalPath);
    }

    [RelayCommand]
    private async Task OpenDataDisplayFile(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var result = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Open Data Display",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Data Display") { Patterns = ["*.cdd"] },
                new FilePickerFileType("All Files")              { Patterns = ["*.*"] },
            ],
        });

        if (result.Count == 0) return;

        try
        {
            await using var s = await result[0].OpenReadAsync();
            await OpenDataDisplayFromFileAsync(result[0].Path.LocalPath, s);
        }
        catch (InvalidDataException ex) { Messages.Error($"Cannot open Data Display: {ex.Message}"); }
        catch (Exception ex)            { Messages.Error($"Failed to open Data Display: {ex.Message}"); }
    }

    // ---- ITreeActions — double-click, context-menu actions ------------------

    // ── Open / activate (dedup by absolute path) ──────────────────────────────

    /// <inheritdoc/>
    public void OpenCellSchematic(ProjectTreeNodeViewModel cellNode) => OpenCellPrimary(cellNode, ViewType.Schematic);
    public void OpenCellSymbol(ProjectTreeNodeViewModel cellNode)    => OpenCellPrimary(cellNode, ViewType.Symbol);
    public void OpenCellLayout(ProjectTreeNodeViewModel cellNode)    => OpenCellPrimary(cellNode, ViewType.Layout);

    private void OpenCellPrimary(ProjectTreeNodeViewModel cellNode, ViewType viewType)
    {
        var cellDir = cellNode.AbsolutePath;
        var pr      = CellFolder.ResolvePrimary(cellDir, viewType);
        if (pr.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || pr.ResolvedName is null)
        {
            var what = viewType switch
            {
                ViewType.Schematic => "schematic",
                ViewType.Layout    => "layout",
                _                  => "symbol",
            };
            Messages.Info($"Cell '{Path.GetFileName(cellDir)}' has no primary {what}.");
            return;
        }
        var path = Path.Combine(CellFolder.SubFolderPath(cellDir, viewType), pr.ResolvedName);
        if (viewType == ViewType.Schematic)    OpenOrActivateSchematic(path);
        else if (viewType == ViewType.Layout)  OpenOrActivateLayout(path);
        else                                    OpenOrActivateSymbol(path);
    }

    public void OpenNode(ProjectTreeNodeViewModel node)
    {
        switch (node.Kind)
        {
            case NodeKind.ViewFile:
                var ext = Path.GetExtension(node.AbsolutePath).ToLowerInvariant();
                if (ext == ".csym")  { OpenOrActivateSymbol(node.AbsolutePath);    return; }
                if (ext == ".csch")  { OpenOrActivateSchematic(node.AbsolutePath); return; }
                if (ext == ".clay")  { OpenOrActivateLayout(node.AbsolutePath);    return; }
                // other view-file types → deferred no-op
                return;

            case NodeKind.Cell:
                OpenOrActivateCellPlaceholder(node.AbsolutePath, node.Name);
                return;

            case NodeKind.DataDisplayFile:
                OpenOrActivateDataDisplay(node.AbsolutePath);
                return;

            case NodeKind.TechFile:
                OpenOrActivateTech(node.AbsolutePath);
                return;

            default:
                // Folder nodes, colour themes, other files → no-op (no viewer yet)
                return;
        }
    }

    private void OpenOrActivateSymbol(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        try
        {
            var symbol   = SymbolPersistence.LoadFromFile(absolutePath);
            var editable = EditableSymbol.FromSymbol(symbol);
            editable.UserEditable     = true;
            editable.ExternalPortCount = TryCellPortCount(absolutePath);
            var vm  = new SymbolEditorViewModel(editable) { CurrentSymbolPath = absolutePath };
            vm.SymbolSaved += OnSymbolSaved;
            vm.SaveError   += OnSymbolSaveError;
            var doc = new SymbolEditorDocument(Path.GetFileName(absolutePath), vm, absolutePath);
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookSymbolCellDirty(doc);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open symbol: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the NumPorts declared in the .ccell when the .csym lives under a cell folder,
    /// otherwise null (orphan symbol — no external port authority).
    /// The .ccell is the authority for port count; the symbol's own PortCount is ignored.
    /// </summary>
    private static int? TryCellPortCount(string csymPath)
    {
        var symbolDir = Path.GetDirectoryName(csymPath);
        if (symbolDir is null) return null;
        var cellDir   = Path.GetDirectoryName(symbolDir);
        if (cellDir is null) return null;
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return null;
        try   { return CellPersistence.LoadFromFile(ccellPath).NumPorts; }
        catch { return null; }
    }

    private void OpenOrActivateSchematic(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        try
        {
            var vm    = GetOrCreateSession(absolutePath);
            var title = Path.GetFileName(absolutePath);
            var doc   = new SchematicDocument(title, vm, absolutePath) { Messages = Messages, Hierarchy = this };
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open schematic: {ex.Message}");
        }
    }

    private void OpenOrActivateCellPlaceholder(string absolutePath, string cellName)
    {
        if (ActivateIfOpen(absolutePath)) return;

        var ccellPath = Path.Combine(absolutePath, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath))
        {
            Messages.Error($"No .ccell found in '{cellName}'. Create a cell folder first.");
            return;
        }

        try
        {
            var file      = CellPersistence.LoadFromFile(ccellPath);
            var editModel = new CellParameterEditModel(ccellPath, file);
            editModel.PrimarySymbolChanged += OnCellPrimarySymbolChanged;
            editModel.PortCountChanged     += OnCellPortCountChanged;
            var vm        = new CellParameterEditorViewModel(cellName, editModel);
            var doc       = new CellParameterEditorDocument(cellName, vm);
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            Messages.Info("Opened", ccellPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open cell parameter editor for '{cellName}': {ex.Message}");
        }
    }

    private bool ActivateIfOpen(string absolutePath)
    {
        if (!_openDocsByPath.TryGetValue(absolutePath, out var existing)) return false;
        _factory.SetActiveDockable(existing);
        return true;
    }

    // ── Make Primary ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void MakePrimary(ProjectTreeNodeViewModel node)
    {
        // node is a ViewFile; its parent is the view sub-folder, grandparent is the cell folder.
        var viewSubDir = Path.GetDirectoryName(node.AbsolutePath)!;
        var cellDir    = Path.GetDirectoryName(viewSubDir)!;
        var ccellPath  = Path.Combine(cellDir, CellFolder.CcellFileName);

        if (!File.Exists(ccellPath))
        {
            Messages.Error($"No .ccell found in '{Path.GetFileName(cellDir)}'.");
            return;
        }

        try
        {
            var ccell    = CellPersistence.LoadFromFile(ccellPath);
            var filename = Path.GetFileName(node.AbsolutePath);

            var subFolderName = Path.GetFileName(viewSubDir)!.ToLowerInvariant();
            if (subFolderName == CellFolder.SchematicSubFolder)
                ccell.PrimarySchematic = filename;
            else if (subFolderName == CellFolder.SymbolSubFolder)
                ccell.PrimarySymbol = filename;
            else if (subFolderName == CellFolder.LayoutSubFolder)
                ccell.PrimaryLayout = filename;
            else
            {
                Messages.Error($"Cannot determine view type for: {node.AbsolutePath}");
                return;
            }

            CellPersistence.SaveToFile(ccellPath, ccell);
            _factory.ProjectTreeTool?.Refresh();

            // Invalidate the symbol resolver and rebuild open schematics when the
            // primary symbol changes — cell-ref components must re-resolve.
            if (subFolderName == CellFolder.SymbolSubFolder)
            {
                CellSymbolResolver.Invalidate(cellDir);
                RebuildOpenSchematics();
            }

            Messages.Success("Made primary", ccellPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Make Primary failed: {ex.Message}");
        }
    }

    // ── Live-update helpers (cell-ref resolver + schematic rebuild) ──────────

    /// <summary>
    /// Fired by <see cref="CellParameterEditModel.PrimarySymbolChanged"/> when the user (or undo)
    /// changes the primary symbol via the cell editor. Mirrors MakePrimary symbol invalidation.
    /// </summary>
    private void OnCellPrimarySymbolChanged(string cellDir)
    {
        CellSymbolResolver.Invalidate(cellDir);
        RebuildOpenSchematics();
        _factory.ProjectTreeTool?.Refresh();
    }

    /// <summary>
    /// Fired by <see cref="CellParameterEditModel.PortCountChanged"/> when the user (or undo)
    /// changes the port count via the cell editor. Invalidates the cell-symbol resolver so
    /// cell-ref components in open schematics re-resolve with the new port count.
    /// </summary>
    private void OnCellPortCountChanged(string cellDir)
    {
        CellSymbolResolver.Invalidate(cellDir);
        RebuildOpenSchematics();
    }

    /// <summary>
    /// Subscribes to the current ProjectTreeTool's PropertyChanged to watch SelectedItem.
    /// Mirrors <see cref="SubscribeToFilterState"/> — unsubscribes from the old tool instance
    /// before subscribing to the new one.  Call after SetActions / CreateDefaultLayout.
    /// </summary>
    private void SubscribeToTreeSelection()
    {
        var newTool = _factory.ProjectTreeTool;
        if (ReferenceEquals(newTool, _subscribedTreeTool)) return;

        if (_subscribedTreeTool is not null && _treeSelectionHandler is not null)
            _subscribedTreeTool.PropertyChanged -= _treeSelectionHandler;

        _subscribedTreeTool = newTool;
        if (newTool is not null)
        {
            _treeSelectionHandler ??= (_, e) =>
            {
                if (e.PropertyName is nameof(ProjectTreeTool.SelectedItem))
                    OnProjectTreeSelectionChanged();
            };
            newTool.PropertyChanged += _treeSelectionHandler;
        }
    }

    /// <summary>
    /// Called when ProjectTreeTool.SelectedItem changes. When a cell node is selected,
    /// loads its .ccell and pushes a CellParameterEditorViewModel into the Properties inspector.
    /// </summary>
    private void OnProjectTreeSelectionChanged()
    {
        var selected = _factory.ProjectTreeTool?.SelectedItem;

        // Clean up the previous ephemeral inspector model.
        if (_treeInspectorCellModel is not null)
        {
            _treeInspectorCellModel.PrimarySymbolChanged -= OnCellPrimarySymbolChanged;
            _treeInspectorCellModel.PortCountChanged     -= OnCellPortCountChanged;
            _treeInspectorCellModel = null;
        }

        if (selected?.Kind != NodeKind.Cell)
        {
            // Don't clobber the inspector when a cell document tab or data display is active.
            var activeDockable = _factory.DocumentDock?.ActiveDockable;
            if (activeDockable is not CellParameterEditorDocument && activeDockable is not DataDisplayDocument)
                _factory.PropertiesTool?.SetActiveCell(null);
            return;
        }

        var cellDir   = selected.AbsolutePath;
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;

        try
        {
            var file      = CellPersistence.LoadFromFile(ccellPath);
            var editModel = new CellParameterEditModel(ccellPath, file);
            editModel.PrimarySymbolChanged += OnCellPrimarySymbolChanged;
            editModel.PortCountChanged     += OnCellPortCountChanged;
            _treeInspectorCellModel = editModel;

            var vm = new CellParameterEditorViewModel(selected.Name, editModel);
            _factory.PropertiesTool?.SetActiveCell(vm);
        }
        catch { /* don't surface inspector errors for tree clicks */ }
    }

    /// <summary>
    /// Invalidates the cell-symbol resolver cache for the cell that owns <paramref name="savedSymPath"/>
    /// and triggers a render-model rebuild on all open schematics.
    /// Call after any .csym save or Make-Primary change that affects a symbol view.
    /// </summary>
    // A symbol save failed (e.g. read-only / unwritable location) — surface it instead of crashing.
    private void OnSymbolSaveError(string message) => Messages.Error(message);

    private void OnSymbolSaved(string savedSymPath)
    {
        // Derive cell folder: .../CellName/symbol/foo.csym → .../CellName
        var symDir  = Path.GetDirectoryName(savedSymPath);
        var cellDir = symDir is not null ? Path.GetDirectoryName(symDir) : null;
        if (cellDir is not null)
            CellSymbolResolver.Invalidate(cellDir);
        else
            CellSymbolResolver.InvalidateAll();
        RebuildOpenSchematics();
    }

    /// <summary>
    /// Called when an auto-generated symbol has been saved for <paramref name="cellAbsDir"/>.
    /// Invalidates the resolver cache for that cell, refreshes the project tree, and triggers
    /// a render-model rebuild on all open schematics so the new symbol appears immediately.
    /// </summary>
    private void OnCellSymbolAutoGenerated(string cellAbsDir)
    {
        CellSymbolResolver.Invalidate(cellAbsDir);
        _factory.ProjectTreeTool?.Refresh();
        RebuildOpenSchematics();
    }

    /// <summary>
    /// Calls TriggerRebuild() on every open SchematicDocument so cell-ref components
    /// re-resolve and re-render after a symbol save or Make-Primary change.
    /// </summary>
    private void RebuildOpenSchematics()
    {
        foreach (var doc in _openDocsByPath.Values)
            if (doc is SchematicDocument schDoc)
                schDoc.ViewModel.TriggerRebuild();

        foreach (var doc in _scratchDocs)
            doc.ViewModel.TriggerRebuild();
    }

    // ---- Session registry helpers (hier1) -----------------------------------

    /// <summary>
    /// Builds a fully-wired <see cref="SchematicViewModel"/> from an already-loaded edit model.
    /// Identical wiring for every creation site (open, new-schematic, recovery restore).
    /// </summary>
    private SchematicViewModel BuildSessionVm(SchematicEditModel editModel)
    {
        var vm = new SchematicViewModel(editModel, Messages);
        vm.SetPlacementService(PlacementService);
        vm.ComponentPlaced         += OnComponentPlaced;
        vm.CellSymbolAutoGenerated += OnCellSymbolAutoGenerated;
        vm.WorkspaceRootProvider    = () => CurrentWorkspaceRoot;
        return vm;
    }

    /// <summary>
    /// Registers a VM in the session registry and subscribes to its UndoRedo so
    /// dirty state and cell-tree indicator stay in sync.
    /// </summary>
    private SchematicViewModel RegisterSession(string absNormalizedPath, SchematicViewModel vm)
    {
        _registry.Register(absNormalizedPath, vm, UpdateCellDirtyForSession);
        return vm;
    }

    /// <summary>
    /// Returns the shared <see cref="SchematicViewModel"/> for <paramref name="absCschPath"/>,
    /// creating and registering it from disk if not already present.
    /// </summary>
    internal SchematicViewModel GetOrCreateSession(string absCschPath)
    {
        var key = Path.GetFullPath(absCschPath);
        if (_registry.TryGet(key, out var existing))
            return existing!;
        var (editModel, _, _) = SchematicPersistence.LoadFromFile(key);
        ReportUnknownComponents(editModel, key);
        return RegisterSession(key, BuildSessionVm(editModel));
    }

    /// <summary>
    /// R-hk-19a: report every component whose type this build doesn't recognize (e.g. a `.csch`
    /// naming the hard-removed library FET, §7A) by NAME — instance name + the original unrecognized
    /// type string — rather than silently rendering it as an unexplained placeholder. Called once,
    /// right after a fresh load (never on a re-open of an already-registered session, which would
    /// just repeat the same warning).
    /// </summary>
    private void ReportUnknownComponents(SchematicEditModel model, string path)
    {
        foreach (var c in model.Components)
        {
            if (c.Symbol != SymbolKind.Unknown) continue;
            Messages.Warning(
                $"'{c.InstanceName}' has unknown component type \"{c.UnknownSymbolRawName}\" " +
                $"— it is not recognized by this version of circuitRF and is shown as a placeholder.",
                path);
        }
    }

    /// <summary>
    /// Removes a session from the registry when it is clean AND has no referencing
    /// <see cref="SchematicDocument"/>.  Dirty sessions are never retired.
    /// </summary>
    internal void RetireSessionIfUnreferenced(string absCschPath)
    {
        var key = Path.GetFullPath(absCschPath);
        _registry.RetireIfUnreferenced(key, IsSessionReferenced);
    }

    private bool IsSessionReferenced(string normalizedKey)
        => _openDocsByPath.Values
               .OfType<SchematicDocument>()
               .Any(d => d.FilePath is { } fp &&
                         string.Equals(Path.GetFullPath(fp), normalizedKey,
                                       StringComparison.OrdinalIgnoreCase));

    // ---- Layout session registry helpers (L3b) — mirrors the schematic block above exactly ------

    /// <summary>Builds a fully-wired <see cref="LayoutEditorViewModel"/> from an already-loaded
    /// model. Identical wiring for every creation site (open, push-in) — mirrors
    /// <see cref="BuildSessionVm"/> and folds in the tech-resolution/live-tech/retarget wiring
    /// <see cref="OpenOrActivateLayout"/> used to do inline before L3b.</summary>
    private LayoutEditorViewModel BuildLayoutSessionVm(LayoutView model, string absClayPath)
    {
        var vm = new LayoutEditorViewModel(model, absClayPath, messageSink: Messages);
        vm.ApplyTechResolution(ResolveTechFor(model.TechRef, absClayPath));
        vm.SaveError += OnLayoutSaveError;
        vm.RequestAddLayerToTechnology += OnLayoutRequestAddLayerToTechnology;
        WireRetargetSeam(vm);
        return vm;
    }

    /// <summary>Registers a VM in the layout session registry and subscribes to its dirty state so
    /// the cell-tree indicator stays in sync.</summary>
    private LayoutEditorViewModel RegisterLayoutSession(string absNormalizedPath, LayoutEditorViewModel vm)
    {
        _layoutRegistry.Register(absNormalizedPath, vm, UpdateCellDirtyForLayoutSession);
        return vm;
    }

    /// <summary>
    /// Returns the shared <see cref="LayoutEditorViewModel"/> for <paramref name="absClayPath"/>,
    /// creating and registering it from disk if not already present — the SAME funnel both "open as
    /// tab" and "push in" go through, so a cell simultaneously open as its own tab and pushed into
    /// elsewhere shares one session (R-L3b-1's in-session-edit path depends on this).
    ///
    /// brief-L5-followups-3.md §2 (R-L5h-4): also the ONE place a fresh load strips any already-
    /// persisted ratsnest shapes (<see cref="SchematicToLayoutGenerator.RatsnestLayer"/>) an older
    /// <c>.clay</c> still carries from before R-L5h-3 stopped emitting them as geometry — reached by
    /// every layout load (open-as-tab, push-in, and any other caller of this method) rather than only
    /// ones re-run through the schematic→layout generator, so already-polluted designs are cleaned
    /// the first time they are opened, not only if the owner happens to regenerate them.
    /// </summary>
    internal LayoutEditorViewModel GetOrCreateLayoutSession(string absClayPath)
    {
        var key = Path.GetFullPath(absClayPath);
        if (_layoutRegistry.TryGet(key, out var existing))
            return existing!;
        var model = LayoutPersistence.LoadFromFile(key);

        int removedRatsnest = SchematicToLayoutGenerator.RemoveRatsnestShapes(model);

        var vm = RegisterLayoutSession(key, BuildLayoutSessionVm(model, key));
        if (removedRatsnest > 0)
        {
            vm.IsDirty = true;
            Messages.Warning(
                $"Removed {removedRatsnest} obsolete ratsnest guide line(s) from '{Path.GetFileName(key)}' " +
                "— connectivity guides are no longer generated as layout geometry.");
        }
        return vm;
    }

    /// <summary>
    /// Removes a layout session from the registry when it is clean AND has no referencing
    /// <see cref="LayoutDocument"/> — and, on actual retirement, clears its live-resolution override
    /// too (R-L3b-1: a session gone means <see cref="CellLayoutResolver"/> falls back to the on-disk
    /// value for anything still resolving it). Dirty sessions are never retired.
    /// </summary>
    internal void RetireLayoutSessionIfUnreferenced(string absClayPath)
    {
        var key = Path.GetFullPath(absClayPath);
        _layoutRegistry.RetireIfUnreferenced(key, IsLayoutSessionReferenced);
        if (!_layoutRegistry.TryGet(key, out _))
            CellLayoutResolver.ClearLive(key);
    }

    private bool IsLayoutSessionReferenced(string normalizedKey)
        => _openDocsByPath.Values
               .OfType<LayoutDocument>()
               .Any(d => d.FilePath is { } fp &&
                         string.Equals(Path.GetFullPath(fp), normalizedKey,
                                       StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// R-L3b-1's live-refresh seam: a push-in session's edit (or a save from any surface) fires this,
    /// which (1) evicts the renderer's compiled geometry for whatever LayoutView is now live at that
    /// path (mutated in place across edits — same reference, so the ConditionalWeakTable cache would
    /// otherwise never self-heal) and (2) nudges every OPEN layout frame's own model to repaint —
    /// cheap and always safe to over-broadcast, since a repaint that finds nothing changed costs
    /// nothing structural (InstancesOnly routes to MarkInstancesDirty, not a full shape rebuild).
    /// </summary>
    private void OnCellLayoutLiveViewChanged(string clayPath)
    {
        if (_layoutRegistry.TryGet(clayPath, out var liveVm) && liveVm is not null)
        {
            LayoutRenderer.InvalidateCompiledGeometry(liveVm.Model);
            // brief-snap-distance-and-geometry-snap.md R-snp-12's own seam — a pushed-in sub-cell's
            // intrinsic feature index (cached, cell-local, keyed the same way) must invalidate on the
            // identical live-refresh trigger the compiled-geometry cache already does.
            LayoutSnapFeatureIndex.Invalidate(liveVm.Model);
        }

        foreach (var doc in _openDocsByPath.Values.OfType<LayoutDocument>().Concat(_scratchLayouts))
            foreach (var (session, _) in doc.NavFrames)
                session.Model.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    /// <summary>
    /// Called after a layout session's .clay is written: clears its dirty flag, refreshes the
    /// cell-tree dirty indicator, and busts <see cref="CellLayoutResolver"/>'s resolution (R-L3b-1's
    /// on-disk-change path) so every OTHER reference to this cell — a parent showing it, or another
    /// tab open on the same file — re-resolves against the just-saved content.
    /// </summary>
    private void NotifyLayoutSessionSaved(string absClayPath)
    {
        var key = Path.GetFullPath(absClayPath);
        _layoutRegistry.MarkSaved(key);
        UpdateCellDirtyForLayoutSession(key);

        if (Path.GetDirectoryName(key) is { } layoutDir &&
            Path.GetDirectoryName(layoutDir) is { } cellDir)
            CellLayoutResolver.Invalidate(cellDir);
    }

    /// <summary>
    /// True when there are dirty layout sessions with no open tab (orphaned by a "Don't Save" tab
    /// close or by a pop-out). Used to extend <see cref="HasAnyDirtyWork"/> — mirrors
    /// <see cref="HasOrphanedDirtySession"/> exactly.
    /// </summary>
    private bool HasOrphanedDirtyLayoutSession()
        => _layoutRegistry.HasOrphanedDirtySession(IsLayoutSessionReferenced);

    /// <summary>Updates the owning cell's dirty indicator when a layout session changes dirty
    /// state.</summary>
    private void UpdateCellDirtyForLayoutSession(string absNormalizedClayPath)
    {
        if (CellDirOfView(absNormalizedClayPath) is { } cellDir)
            RefreshCellDirty(cellDir);
    }

    // ── ILayoutHierarchyHost implementation (L3b) — mirrors IHierarchyHost above exactly ─────────

    /// <inheritdoc/>
    public bool CanPushInto(LayoutInstance? instance, LayoutEditorViewModel? parentVm, out string? reason)
        => LayoutHierarchyResolver.CanPushInto(instance, parentVm, out reason);

    /// <inheritdoc/>
    public void PushIntoCell(LayoutDocument doc, LayoutInstance instance)
    {
        var path = LayoutHierarchyResolver.ResolvePrimaryPath(instance, doc.ActiveViewModel);
        if (path is null) return;
        var session = GetOrCreateLayoutSession(path);
        var label   = string.IsNullOrEmpty(instance.CellRef)
            ? "(cell)" : Path.GetFileName(instance.CellRef.TrimEnd('/', '\\'));
        doc.PushIn(session, label);
    }

    /// <inheritdoc/>
    public void PopOutOf(LayoutDocument doc)
    {
        var popped = doc.PopOut();
        if (popped is null) return;
        if (_layoutRegistry.TryGetPath(popped, out var poppedPath) && poppedPath is not null)
            RetireLayoutSessionIfUnreferenced(poppedPath);
    }

    /// <inheritdoc/>
    public void PopToLevel(LayoutDocument doc, int frameIndex)
    {
        var popped = doc.PopTo(frameIndex);
        foreach (var vm in popped)
        {
            if (_layoutRegistry.TryGetPath(vm, out var path) && path is not null)
                RetireLayoutSessionIfUnreferenced(path);
        }
    }

    /// <inheritdoc/>
    public void OpenCellInNewTab(LayoutDocument fromDoc, LayoutInstance instance)
    {
        var path = LayoutHierarchyResolver.ResolvePrimaryPath(instance, fromDoc.ActiveViewModel);
        if (path is null) return;
        OpenOrActivateLayout(path);
    }

    /// <inheritdoc/>
    public async Task SaveLayoutDocumentAsync(LayoutDocument doc)
    {
        var window = ResolveOwner(null);
        if (window is null) return;

        if (!doc.IsDirty)
        {
            Messages.Info("Nothing to save.");
            return;
        }

        await SaveSingleLayoutDocument(doc, window);

        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
    }

    /// <summary>The currently active <see cref="LayoutDocument"/>, or null.</summary>
    public LayoutDocument? ActiveLayoutDocument
        => _factory.DocumentDock?.ActiveDockable as LayoutDocument;

    private static LayoutInstance? GetSingleSelectedCellInstance(LayoutEditorViewModel? vm)
    {
        if (vm is null) return null;
        var inst = vm.SingleSelectedInstance;
        return inst?.CellRef is not null ? inst : null;
    }

    // ---- Hierarchy navigation (hier3) ------------------------------------------

    // Subscription tracking for CanExecuteChanged.
    private SchematicDocument?   _hierarchySubscribedDoc;
    private SchematicViewModel?  _hierarchySubscribedVm;

    /// <summary>The currently active <see cref="SchematicDocument"/>, or null.</summary>
    public SchematicDocument? ActiveSchematicDocument
        => _factory.DocumentDock?.ActiveDockable as SchematicDocument;

    // ── IHierarchyHost implementation ─────────────────────────────────────────

    /// <inheritdoc/>
    public bool CanPushInto(EditableComponent? comp, SchematicEditModel? parentModel, out string? reason)
        => HierarchyResolver.CanPushInto(comp, parentModel, out reason);

    /// <inheritdoc/>
    public void PushIntoCell(SchematicDocument doc, EditableComponent comp)
    {
        var path = HierarchyResolver.ResolvePrimaryPath(comp, doc.ActiveViewModel.EditModel);
        if (path is null) return;
        var session = GetOrCreateSession(path);
        doc.PushIn(session, comp.InstanceName);
        NotifyHierarchyCanExecuteChanged();
    }

    /// <inheritdoc/>
    public void PopOutOf(SchematicDocument doc)
    {
        var popped = doc.PopOut();
        if (popped is null) return;
        if (_registry.TryGetPath(popped, out var poppedPath) && poppedPath is not null)
            RetireSessionIfUnreferenced(poppedPath);
        NotifyHierarchyCanExecuteChanged();
    }

    /// <inheritdoc/>
    public void PopToLevel(SchematicDocument doc, int frameIndex)
    {
        var popped = doc.PopTo(frameIndex);
        foreach (var vm in popped)
        {
            if (_registry.TryGetPath(vm, out var path) && path is not null)
                RetireSessionIfUnreferenced(path);
        }
        NotifyHierarchyCanExecuteChanged();
    }

    /// <inheritdoc/>
    public void OpenCellInNewTab(SchematicDocument fromDoc, EditableComponent comp)
    {
        var path = HierarchyResolver.ResolvePrimaryPath(comp, fromDoc.ActiveViewModel.EditModel);
        if (path is null) return;
        OpenOrActivateSchematic(path);
    }

    /// <inheritdoc/>
    public async Task SaveSchematicDocumentAsync(SchematicDocument doc)
    {
        var window = ResolveOwner(null);
        if (window is null) return;

        if (!doc.IsDirty)
        {
            Messages.Info("Nothing to save.");
            return;
        }

        await SaveSingleDocument(doc, window);

        // ⌘S single-doc parity: refresh the .cws open-doc snapshot silently.
        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
    }

    // ── RelayCommands for app-menu / keyboard (CanExecute managed here) ───────

    // These three commands are shared by BOTH editors (the menu items say "Push Into Cell"/"Pop
    // Out"/"Open Cell in New Tab", not "Schematic: …") — dispatch on whichever document type is
    // ACTIVE. L3b's own keyboard path (Ctrl+]/Ctrl+[) is handled directly by each editor's view
    // instead (see LayoutEditorView's OnViewKeyDownTunnel), which is also where viewport
    // capture/restore happens for layout — these menu-driven commands intentionally skip that (see
    // ILayoutHierarchyHost.PushIntoCell's own doc comment): the canvas's own initial-fit-on-VM-switch
    // still frames the new content, just without restoring the exact prior pan/zoom.

    [RelayCommand(CanExecute = nameof(CanHierarchyPushIn))]
    private void HierarchyPushIn()
    {
        if (ActiveSchematicDocument is { } schDoc)
        {
            var comp = GetSingleSelectedCellComp(schDoc.ActiveViewModel);
            if (comp is not null) PushIntoCell(schDoc, comp);
            return;
        }
        if (ActiveLayoutDocument is { } layDoc)
        {
            var inst = GetSingleSelectedCellInstance(layDoc.ActiveViewModel);
            if (inst is not null) PushIntoCell(layDoc, inst);
        }
    }
    private bool CanHierarchyPushIn()
    {
        if (ActiveSchematicDocument is { } schDoc)
            return CanPushInto(GetSingleSelectedCellComp(schDoc.ActiveViewModel),
                               schDoc.ActiveViewModel.EditModel, out _);
        if (ActiveLayoutDocument is { } layDoc)
            return CanPushInto(GetSingleSelectedCellInstance(layDoc.ActiveViewModel),
                               layDoc.ActiveViewModel, out _);
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanHierarchyPopOut))]
    private void HierarchyPopOut()
    {
        if (ActiveSchematicDocument is { } schDoc) { PopOutOf(schDoc); return; }
        if (ActiveLayoutDocument is { } layDoc) PopOutOf(layDoc);
    }
    private bool CanHierarchyPopOut()
        => (ActiveSchematicDocument?.CanPopOut ?? false) || (ActiveLayoutDocument?.CanPopOut ?? false);

    [RelayCommand(CanExecute = nameof(CanHierarchyPushIn))]
    private void HierarchyOpenInNewTab()
    {
        if (ActiveSchematicDocument is { } schDoc)
        {
            var comp = GetSingleSelectedCellComp(schDoc.ActiveViewModel);
            if (comp is not null) OpenCellInNewTab(schDoc, comp);
            return;
        }
        if (ActiveLayoutDocument is { } layDoc)
        {
            var inst = GetSingleSelectedCellInstance(layDoc.ActiveViewModel);
            if (inst is not null) OpenCellInNewTab(layDoc, inst);
        }
    }

    private static EditableComponent? GetSingleSelectedCellComp(SchematicViewModel? vm)
    {
        if (vm is null) return null;
        var ids = vm.Selection.Ids;
        if (ids.Count != 1) return null;
        var comp = vm.EditModel.FindComponent(ids.First());
        return comp?.CellRef is not null ? comp : null;
    }

    private void NotifyHierarchyCanExecuteChanged()
    {
        HierarchyPushInCommand.NotifyCanExecuteChanged();
        HierarchyPopOutCommand.NotifyCanExecuteChanged();
        HierarchyOpenInNewTabCommand.NotifyCanExecuteChanged();
    }

    // Rewires the doc + VM subscriptions used to raise CanExecuteChanged.
    // Called from OnDocumentDockPropertyChanged (active doc changed) and OnHierarchyNavChanged.
    private void RewireHierarchySubscriptions()
    {
        var doc = ActiveSchematicDocument;

        if (!ReferenceEquals(doc, _hierarchySubscribedDoc))
        {
            if (_hierarchySubscribedDoc is not null)
                _hierarchySubscribedDoc.ActiveViewModelChanged -= OnHierarchyNavChanged;
            _hierarchySubscribedDoc = doc;
            if (doc is not null)
                doc.ActiveViewModelChanged += OnHierarchyNavChanged;
        }

        var vm = doc?.ActiveViewModel;
        if (!ReferenceEquals(vm, _hierarchySubscribedVm))
        {
            if (_hierarchySubscribedVm is not null)
                _hierarchySubscribedVm.Selection.Changed -= OnHierarchySelectionChanged;
            _hierarchySubscribedVm = vm;
            if (vm is not null)
                vm.Selection.Changed += OnHierarchySelectionChanged;
        }

        NotifyHierarchyCanExecuteChanged();
    }

    private void OnHierarchyNavChanged(object? sender, EventArgs e)
    {
        // After push/pop the active VM changes — rewire selection subscription.
        var vm = ActiveSchematicDocument?.ActiveViewModel;
        if (!ReferenceEquals(vm, _hierarchySubscribedVm))
        {
            if (_hierarchySubscribedVm is not null)
                _hierarchySubscribedVm.Selection.Changed -= OnHierarchySelectionChanged;
            _hierarchySubscribedVm = vm;
            if (vm is not null)
                vm.Selection.Changed += OnHierarchySelectionChanged;
        }
        NotifyHierarchyCanExecuteChanged();
    }

    private void OnHierarchySelectionChanged(object? sender, EventArgs e)
        => NotifyHierarchyCanExecuteChanged();

    /// <summary>
    /// Called after a session's .csch is written: clears its dirty flag and refreshes the
    /// cell-tree dirty indicator.
    /// </summary>
    private void NotifySessionSaved(string absCschPath)
    {
        var key = Path.GetFullPath(absCschPath);
        _registry.MarkSaved(key);
        UpdateCellDirtyForSession(key);
    }

    /// <summary>
    /// True when there are dirty sessions with no open tab (orphaned by a "Don't Save" tab
    /// close or by a hier2+ push-in frame).  Used to extend <see cref="HasAnyDirtyWork"/>.
    /// </summary>
    private bool HasOrphanedDirtySession()
        => _registry.HasOrphanedDirtySession(IsSessionReferenced);

    /// <summary>
    /// Updates the owning cell's dirty indicator when a schematic session changes dirty state.
    /// Delegates to <see cref="RefreshCellDirty"/> so the indicator aggregates over BOTH the
    /// cell's .csch sessions and its open .csym editors.
    /// </summary>
    private void UpdateCellDirtyForSession(string absNormalizedCschPath)
    {
        if (CellDirOfView(absNormalizedCschPath) is { } cellDir)
            RefreshCellDirty(cellDir);
    }

    /// <summary>
    /// Updates the owning cell's dirty indicator when an open symbol editor changes dirty state.
    /// No-op for symbols with no cell path yet (scratch / loose / built-in).
    /// </summary>
    private void RefreshCellDirtyForSymbol(SymbolEditorDocument doc)
    {
        if (doc.ViewModel.CurrentSymbolPath is not { } sp) return;
        if (CellDirOfView(sp) is { } cellDir)
            RefreshCellDirty(cellDir);
    }

    /// <summary>
    /// Recomputes the project-tree dirty indicator for <paramref name="cellDir"/> from ALL open
    /// dirty work inside that cell — any dirty .csch session OR any dirty open .csym editor — and
    /// marks the cell clean when nothing within it is dirty.  Safe no-op when the directory is
    /// not a cell node in the current tree (see <c>ProjectTreeTool.SetCellDirty</c>).
    /// </summary>
    private bool IsCellDirty(string cellDir) =>
        _registry.AllDirtyPaths.Any(p => IsViewInCell(p, cellDir))
        || _layoutRegistry.AllDirtyPaths.Any(p => IsViewInCell(p, cellDir))
        || _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d =>
               d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir))
        || _openDocsByPath.Values.OfType<LayoutDocument>().Any(d =>
               d.IsDirty && d.FilePath is { } lp && IsViewInCell(lp, cellDir));

    private void RefreshCellDirty(string cellDir)
        => _factory.ProjectTreeTool?.SetCellDirty(cellDir, IsCellDirty(cellDir));

    /// <summary>
    /// Subscribes a symbol editor document's dirty state to its owning cell's tree indicator so
    /// editing or saving a .csym flips the cell node dirty/clean.  Harmless for symbols that never
    /// acquire a cell path — <see cref="RefreshCellDirtyForSymbol"/> skips those.
    /// </summary>
    private void HookSymbolCellDirty(SymbolEditorDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SymbolEditorViewModel.IsDirty))
                RefreshCellDirtyForSymbol(doc);
        };
    }

    /// <summary>
    /// Updates the owning cell's dirty indicator when an open layout editor changes dirty state.
    /// No-op for layouts with no cell path yet (scratch / loose).
    /// </summary>
    private void RefreshCellDirtyForLayout(LayoutDocument doc)
    {
        if (doc.FilePath is not { } lp) return;
        if (CellDirOfView(lp) is { } cellDir)
            RefreshCellDirty(cellDir);
    }

    /// <summary>
    /// Subscribes a layout editor document's dirty state to its owning cell's tree indicator so
    /// editing or saving a .clay flips the cell node dirty/clean.  Mirrors <see cref="HookSymbolCellDirty"/>.
    /// </summary>
    private void HookLayoutCellDirty(LayoutDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayoutEditorViewModel.IsDirty))
                RefreshCellDirtyForLayout(doc);
        };
        doc.CanvasInteracted += () => OnLayoutCanvasInteracted(doc);
    }

    /// <summary>brief-layout-testing-fixes.md item 3/R-fix-3 — the user just clicked/focused back into
    /// this layout document's own canvas. Makes it the Dock-active document (the brief's own literal
    /// framing) AND unconditionally re-asserts the Properties/undo/save-scope routing directly — the
    /// latter is not merely redundant: a click on an ALREADY-active tab's canvas does not change
    /// <c>DocumentDock.ActiveDockable</c> at all (it was already this document), so
    /// <see cref="OnDocumentDockPropertyChanged"/> would never re-fire on that path alone, and the
    /// Properties panel would stay showing whatever the project tree (a DIFFERENT dock region) last
    /// forced it to (e.g. <see cref="PropertiesTool.SetActiveCell"/>(null) unconditionally clears the
    /// layout context too) even though this document was never actually deactivated.</summary>
    private void OnLayoutCanvasInteracted(LayoutDocument doc)
    {
        _factory.SetActiveDockable(doc);
        ActivateLayoutDocumentForProperties(doc);
        SetActiveUndoTarget(doc);
        ActiveSaveScope = SaveScope.SingleDoc;
    }

    private void ActivateLayoutDocumentForProperties(LayoutDocument doc) =>
        _factory.PropertiesTool?.SetActiveLayout(doc.ActiveViewModel);

    // Cell dir for a view file at .../cell/<viewfolder>/file.ext → .../cell (two levels up); else null.
    private static string? CellDirOfView(string viewFilePath)
    {
        var sub = Path.GetDirectoryName(viewFilePath);
        return sub is not null ? Path.GetDirectoryName(sub) : null;
    }

    // True when a view file's owning cell dir equals cellDir (case-insensitive).
    private static bool IsViewInCell(string viewFilePath, string cellDir)
        => CellDirOfView(viewFilePath) is { } c
           && string.Equals(c, cellDir, StringComparison.OrdinalIgnoreCase);

    // ── ITreeActions: dirty detection + per-node save ─────────────────────────

    /// <inheritdoc/>
    public bool IsNodeDirty(ProjectTreeNodeViewModel node)
    {
        switch (node.Kind)
        {
            case NodeKind.Cell:
                return IsCellDirty(node.AbsolutePath);
            case NodeKind.ViewFile:
            {
                var key = Path.GetFullPath(node.AbsolutePath);
                var ext = Path.GetExtension(key).ToLowerInvariant();
                if (ext == ".csch")
                    return _registry.AllDirtyPaths.Any(p =>
                        string.Equals(Path.GetFullPath(p), key, StringComparison.OrdinalIgnoreCase));
                if (ext == ".csym")
                    return _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d =>
                        d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp
                        && string.Equals(Path.GetFullPath(sp), key, StringComparison.OrdinalIgnoreCase));
                if (ext == ".clay")
                    return _openDocsByPath.Values.OfType<LayoutDocument>().Any(d =>
                        d.IsDirty && d.FilePath is { } lp
                        && string.Equals(Path.GetFullPath(lp), key, StringComparison.OrdinalIgnoreCase));
                return false;
            }
            case NodeKind.DataDisplayFile:
            {
                var ddKey = Path.GetFullPath(node.AbsolutePath);
                return _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d =>
                    d.FilePath is { } fp
                    && string.Equals(Path.GetFullPath(fp), ddKey, StringComparison.OrdinalIgnoreCase)
                    && d.ViewModel.Window.HasUnsavedChanges());
            }
            case NodeKind.TechFile:
            {
                var techKey = Path.GetFullPath(node.AbsolutePath);
                return _openDocsByPath.Values.OfType<TechDocument>().Any(d =>
                    d.IsDirty && string.Equals(Path.GetFullPath(d.FilePath), techKey, StringComparison.OrdinalIgnoreCase));
            }
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public async Task SaveNodeAsync(ProjectTreeNodeViewModel node)
    {
        var owner = ResolveOwner(null);
        if (owner is null) return;

        switch (node.Kind)
        {
            case NodeKind.Cell:
                await SaveCellViewsAsync(node.AbsolutePath, owner);
                break;
            case NodeKind.ViewFile:
            {
                var ext = Path.GetExtension(node.AbsolutePath).ToLowerInvariant();
                if (ext == ".csch")      SaveSchematicByPath(node.AbsolutePath);
                else if (ext == ".csym") await SaveSymbolByPathAsync(node.AbsolutePath, owner);
                else if (ext == ".clay") await SaveLayoutByPathAsync(node.AbsolutePath, owner);
                break;
            }
            case NodeKind.DataDisplayFile:
                await SaveDataDisplayByPathAsync(node.AbsolutePath, owner);
                break;
            case NodeKind.TechFile:
                SaveTechByPath(node.AbsolutePath);
                break;
        }

        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
    }

    private void SaveTechByPath(string absPath)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<TechDocument>().FirstOrDefault(d =>
            string.Equals(Path.GetFullPath(d.FilePath), key, StringComparison.OrdinalIgnoreCase));
        if (doc is { IsDirty: true }) doc.ViewModel.SaveCommand.Execute(null);
    }

    private void SaveSchematicByPath(string absPath)
    {
        var key = Path.GetFullPath(absPath);
        if (!_registry.TryGet(key, out var vm) || vm is null || !vm.UndoRedo.IsModified) return;
        try
        {
            SchematicPersistence.SaveToFile(key, vm.EditModel, Path.GetFileNameWithoutExtension(key));
            NotifySessionSaved(key);
            Messages.Success("Saved", key);
        }
        catch (Exception ex) { Messages.Error($"Failed to save '{key}': {ex.Message}"); }
    }

    private async Task SaveSymbolByPathAsync(string absPath, Window owner)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<SymbolEditorDocument>().FirstOrDefault(d =>
            d.ViewModel.CurrentSymbolPath is { } sp
            && string.Equals(Path.GetFullPath(sp), key, StringComparison.OrdinalIgnoreCase));
        if (doc is { IsDirty: true }) await SaveMaterializedSymbolDoc(doc, owner);
    }

    private async Task SaveDataDisplayByPathAsync(string absPath, Window owner)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<DataDisplayDocument>().FirstOrDefault(d =>
            d.FilePath is { } fp
            && string.Equals(Path.GetFullPath(fp), key, StringComparison.OrdinalIgnoreCase));
        if (doc is not null && doc.ViewModel.Window.HasUnsavedChanges())
            await SaveDataDisplayDoc(doc, owner);
    }

    private async Task SaveLayoutByPathAsync(string absPath, Window owner)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<LayoutDocument>().FirstOrDefault(d =>
            d.FilePath is { } lp
            && string.Equals(Path.GetFullPath(lp), key, StringComparison.OrdinalIgnoreCase));
        if (doc is { IsDirty: true }) await SaveMaterializedLayoutDoc(doc, owner);
    }

    private async Task SaveCellViewsAsync(string cellDir, Window owner)
    {
        foreach (var p in _registry.AllDirtyPaths.Where(p => IsViewInCell(p, cellDir)).ToList())
            SaveSchematicByPath(p);
        foreach (var doc in _openDocsByPath.Values.OfType<SymbolEditorDocument>()
                     .Where(d => d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir))
                     .ToList())
            await SaveMaterializedSymbolDoc(doc, owner);
        foreach (var doc in _openDocsByPath.Values.OfType<LayoutDocument>()
                     .Where(d => d.IsDirty && d.FilePath is { } lp && IsViewInCell(lp, cellDir))
                     .ToList())
            await SaveMaterializedLayoutDoc(doc, owner);
        RefreshCellDirty(cellDir);
    }

    // ── ITreeActions: recent-workspace access (Item 1) ───────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<(string Name, string Path)> GetRecentWorkspaces()
    {
        var sep    = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var result = new List<(string, string)>(_recentWorkspaces.Count);
        foreach (var p in _recentWorkspaces)
        {
            var dir = Path.GetDirectoryName(p.TrimEnd(sep));
            if (dir is null || !Directory.Exists(dir)) continue;
            result.Add((Path.GetFileName(dir) ?? p, p));
        }
        return result;
    }

    /// <inheritdoc/>
    public void OpenWorkspacePath(string cwsPath)
        => _ = OpenRecentWorkspaceCommand.ExecuteAsync(cwsPath);

    /// <inheritdoc/>
    void ITreeActions.ClearRecentWorkspaces()
    {
        _recentWorkspaces.Clear();
        SaveRecent();
        RebuildRecentMenuItems();
    }

    // ── ITreeActions: selection change hook (Item 5) ──────────────────────────

    /// <inheritdoc/>
    public void OnTreeSelectionChanged(ProjectTreeNodeViewModel? node)
    {
        if (node is not null
            && (node.Kind == NodeKind.OtherFile
                || (node.Kind == NodeKind.KnownFile && File.Exists(node.AbsolutePath))))
        {
            _factory.PropertiesTool?.SetActiveFileInfo(new FileInfoInspectorViewModel(node.AbsolutePath));
            return;
        }
        // For all other node kinds, leave the current document-driven context intact.
    }

    // ── Reveal in file manager ────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Reveal(ProjectTreeNodeViewModel node)
    {
        var path = node.AbsolutePath;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // open -R reveals the specific file in Finder
                Process.Start(new ProcessStartInfo("open", new[] { "-R", path })
                    { UseShellExecute = false });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // /select, highlights the file in Explorer; works for both files and folders
                Process.Start(new ProcessStartInfo("explorer", $"/select,\"{path}\"")
                    { UseShellExecute = false });
            }
            else
            {
                // Linux: open the containing folder (xdg-open doesn't highlight)
                var dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
                Process.Start(new ProcessStartInfo("xdg-open", dir)
                    { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            Messages.Error($"Reveal failed: {ex.Message}");
        }
    }

    // ── Known File actions ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void AddKnownFile(string path)
    {
        if (CurrentWorkspacePath is null) return;
        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
        catch { cws = new CwsFile(); }

        // R-stb-10/11/13: store a workspace-RELATIVE path (with `/` separators) when the target is
        // inside the workspace, an absolute one only when it is outside. The file itself is NEVER
        // copied — a Known File is a reference, and a referenced measurement is an INPUT that must
        // not be swept up when the user clears results/ in Finder.
        string stored = WorkspaceRefs.ToStoredRef(path, Path.GetDirectoryName(CurrentWorkspacePath));

        if (!cws.KnownFiles.Contains(stored, StringComparer.OrdinalIgnoreCase))
        {
            cws.KnownFiles.Add(stored);
            WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);
        }
        _factory.ProjectTreeTool?.Refresh();
    }

    /// <inheritdoc/>
    public void OpenExternal(ProjectTreeNodeViewModel node)
    {
        try { OpenPathExternal(node.AbsolutePath); }
        catch (Exception ex) { Messages.Error($"Open failed: {ex.Message}"); }
    }

    /// <summary>Opens <paramref name="path"/> with the OS default application.</summary>
    private static void OpenPathExternal(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", new[] { path }) { UseShellExecute = false });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
    }

    /// <summary>
    /// Opens <paramref name="path"/> with the OS default application and returns whether a
    /// registered handler was found. Redirects stderr on macOS/Linux to suppress OS-level
    /// error messages to the terminal when no app is associated with the file type.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the OS located a handler; <c>false</c> if no application is registered
    /// for the file extension (e.g. macOS kLSApplicationNotFoundErr, Linux xdg-open exit 4).
    /// </returns>
    private static bool TryOpenExternal(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Redirect stderr so the "No application knows how to open…" OS message doesn't
            // appear in the terminal; we surface it ourselves via the message panel instead.
            var psi = new ProcessStartInfo("open", new[] { path })
            {
                UseShellExecute        = false,
                RedirectStandardError  = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            bool exited = proc.WaitForExit(3000); // open returns in < 100 ms normally
            return exited && proc.ExitCode == 0;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // UseShellExecute = true shows the "Open With" dialog or throws Win32Exception
            // when there is truly no handler — the caller's catch handles that case.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }

        // Linux / other: xdg-open exits with 4 when no handler is registered.
        var lpsi = new ProcessStartInfo("xdg-open", path)
        {
            UseShellExecute       = false,
            RedirectStandardError = true,
        };
        using var lproc = Process.Start(lpsi);
        if (lproc is null) return false;
        bool lexited = lproc.WaitForExit(3000);
        return lexited && lproc.ExitCode == 0;
    }

    /// <inheritdoc/>
    public void CopyToWorkspace(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        var sourcePath   = node.AbsolutePath;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        if (Directory.Exists(sourcePath))
        {
            Messages.Info("Copy to Workspace is not supported for directories in v1.");
            return;
        }

        var dest = ResolveNonConflictingDestination(workspaceDir, Path.GetFileName(sourcePath));

        try { File.Copy(sourcePath, dest); }
        catch (Exception ex) { Messages.Error($"Copy failed: {ex.Message}"); return; }

        // Update the Known File reference in .cws to point to the new in-workspace path.
        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
        catch { cws = new CwsFile(); }

        int idx = cws.KnownFiles.FindIndex(
            p => string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            cws.KnownFiles[idx] = dest;
        else
            cws.KnownFiles.Add(dest);

        WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Copied", dest);
    }

    /// <inheritdoc/>
    public void RemoveKnownFile(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        var path = node.AbsolutePath;
        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
        catch { cws = new CwsFile(); }

        cws.KnownFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);
        _factory.ProjectTreeTool?.Refresh();
        Messages.Info($"Reference removed (file not deleted):\n  {path}");
    }

    // ── Technology (.ctech) node actions (L0c) ────────────────────────────────

    /// <inheritdoc/>
    public void SetAsWorkspaceDefault(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        string relPath;
        try   { relPath = Path.GetRelativePath(workspaceDir, node.AbsolutePath); }
        catch { relPath = node.AbsolutePath; }

        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
        catch { cws = new CwsFile(); }

        cws.DefaultTechRef = relPath;
        WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath, cws);

        // The new default's cached entry (if any) may be stale relative to what's on disk now;
        // Invalidate forces a fresh load, then every open layout re-resolves against it.
        _techCache.Invalidate(node.AbsolutePath);
        RefreshAllOpenLayoutTech();
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Set as workspace default technology", node.AbsolutePath);
    }

    /// <inheritdoc/>
    public async Task ReloadTechnologyAsync(ProjectTreeNodeViewModel node)
    {
        if (_techCache.HasLiveOverride(node.AbsolutePath))
        {
            var window = ResolveOwner(null);
            if (window is null) return;

            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Discard unsaved changes to '{node.Name}'?",
                saveLabel: "Discard", dontSaveLabel: null, cancelLabel: "Cancel",
                title: "Discard Changes");
            await dlg.ShowDialog(window);
            if (dlg.Result != SaveChangesResult.Save) return; // Cancel — leave the override intact
        }

        _techCache.Invalidate(node.AbsolutePath);
        Messages.Info("Technology reloaded", node.AbsolutePath);
    }

    /// <inheritdoc/>
    public bool IsWorkspaceDefaultTech(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return false;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        string? defaultTechRef;
        try   { defaultTechRef = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath).DefaultTechRef; }
        catch { return false; }
        if (defaultTechRef is null) return false;

        var defaultAbsPath = Path.GetFullPath(Path.Combine(workspaceDir, defaultTechRef));
        return string.Equals(defaultAbsPath, node.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task RemoveCellAsync(ProjectTreeNodeViewModel cellNode)
    {
        if (CurrentWorkspacePath is null) return;
        var workspaceRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;

        var window = ResolveOwner(null);
        if (window is null) return;

        int usedIn = CellUsageScanner.CountReferencingCells(workspaceRoot, cellNode.AbsolutePath);

        var msg = $"Remove cell '{cellNode.Name}'?\n\nThis moves the entire cell folder to the Trash/Recycle Bin. There is no in-app undo.";
        if (usedIn == 1)
            msg += "\n\n⚠ This cell is used in 1 other cell. Removing it will break that reference.";
        else if (usedIn > 1)
            msg += $"\n\n⚠ This cell is used in {usedIn} cells. Removing it will break those references.";

        var dlg = new Views.Dialogs.SaveChangesDialog(
            msg,
            saveLabel:     "Remove Cell",
            dontSaveLabel: null,
            cancelLabel:   "Cancel",
            title:         "Remove Cell");
        await dlg.ShowDialog(window);
        if (dlg.Result != SaveChangesResult.Save) return;

        var cellPath = cellNode.AbsolutePath;

        // Close any open tabs/sessions under the cell dir.
        var keysToClose = _openDocsByPath
            .Where(kvp => IsPathOrUnder(kvp.Key, cellPath))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        foreach (var (key, dockable) in keysToClose)
        {
            _factory.ForceCloseDockable(dockable);
            if (key.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
                RetireSessionIfUnreferenced(key);
            else if (key.EndsWith(".clay", StringComparison.OrdinalIgnoreCase))
                RetireLayoutSessionIfUnreferenced(key);
        }

        if (!SystemTrash.TryMoveToTrash(cellPath, out var err))
        {
            Messages.Error($"Remove cell failed: {err}");
            return;
        }

        Messages.Info($"Removed cell (moved to Trash): {cellPath}");
        _factory.ProjectTreeTool?.Refresh();
    }

    /// <inheritdoc/>
    public void RemoveDataDisplay(ProjectTreeNodeViewModel node)
    {
        var name = Path.GetFileNameWithoutExtension(node.AbsolutePath);
        var msg  = $"Remove Data Display '{name}'?\n\nThis moves the file to the Trash/Recycle Bin. There is no in-app undo.";
        _ = RemoveNodeToTrashAsync(node, msg, "Remove Data Display");
    }

    /// <inheritdoc/>
    public void RemoveFile(ProjectTreeNodeViewModel node)
    {
        var name = node.Name;
        var msg  = $"Remove '{name}'?\n\nThis moves it to the Trash/Recycle Bin. There is no in-app undo.";
        _ = RemoveNodeToTrashAsync(node, msg, "Remove");
    }

    private async Task RemoveNodeToTrashAsync(ProjectTreeNodeViewModel node, string dialogMessage, string dialogTitle)
    {
        var window = ResolveOwner(null);
        if (window is null) return;

        var dlg = new Views.Dialogs.SaveChangesDialog(
            dialogMessage,
            saveLabel:     "Remove",
            dontSaveLabel: null,
            cancelLabel:   "Cancel",
            title:         dialogTitle);
        await dlg.ShowDialog(window);
        if (dlg.Result != SaveChangesResult.Save) return;

        var path = node.AbsolutePath;

        // Close any open tabs that reference this path (file) or a path under it (directory).
        // ForceCloseDockable bypasses the dirty-save prompt — the file is going away.
        var keysToClose = _openDocsByPath
            .Where(kvp => IsPathOrUnder(kvp.Key, path))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        foreach (var (key, dockable) in keysToClose)
        {
            _factory.ForceCloseDockable(dockable);
            // OnDockableClosed fires via DockableClosed event and cleans up _openDocsByPath.
            // For .csch tabs, RetireSessionIfUnreferenced is also called there.
        }

        if (!SystemTrash.TryMoveToTrash(path, out var err))
        {
            Messages.Error($"Remove failed: {err}");
            return;
        }

        Messages.Info($"Removed (moved to Trash): {path}");
        _factory.ProjectTreeTool?.Refresh();
    }

    // True when candidate is exactly path, or is a file under the directory path.
    private static bool IsPathOrUnder(string candidate, string dirOrFile)
    {
        if (string.Equals(candidate, dirOrFile, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Directory.Exists(dirOrFile)) return false;

        var rel = Path.GetRelativePath(dirOrFile, candidate);
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }

    private static string ResolveNonConflictingDestination(string dir, string fileName)
    {
        string dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext  = Path.GetExtension(fileName);
        for (int n = 1; n <= 99; n++)
        {
            dest = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(dest)) return dest;
        }
        return Path.Combine(dir, $"{stem} (99){ext}");
    }

    // ── Creation actions ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task NewCellAsync(ProjectTreeNodeViewModel parentNode)
    {
        var parentDir  = parentNode.AbsolutePath;
        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dialog = new InputNameDialog("New Cell", "Cell name:");
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        string newCellDir = Path.Combine(parentDir, name);
        if (Directory.Exists(newCellDir))
        {
            Messages.Error($"A cell named '{name}' already exists.");
            return;
        }

        try
        {
            CellFolder.CreateCellFolder(parentDir, name);
            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Created", Path.Combine(newCellDir, CellFolder.CcellFileName));
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create cell: {ex.Message}");
            return; // the cell was never created — nothing further to do.
        }

        // R-cc-1: the cell already exists at this point regardless of what happens next — a failure
        // here is reported by CreateAndOpenSchematicFileAsync itself and never rolls the cell back.
        await CreateAndOpenSchematicFileAsync(newCellDir, name, name);
    }

    // ── New Cell in workspace root (File menu + tree-header button) ──────────────

    [RelayCommand(CanExecute = nameof(CanNewCellInWorkspace))]
    private Task NewCellInWorkspace() => NewCellInWorkspaceAsync();
    private bool CanNewCellInWorkspace() => CurrentWorkspacePath is not null;

    /// <inheritdoc/>
    public async Task NewCellInWorkspaceAsync()
    {
        if (CurrentWorkspacePath is null) return;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var mainWindow   = ResolveOwner(null);
        if (mainWindow is null) return;

        var dialog = new InputNameDialog("New Cell", "Cell name:");
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        string newCellDir = Path.Combine(workspaceDir, name);
        if (Directory.Exists(newCellDir))
        {
            Messages.Error($"A cell named '{name}' already exists.");
            return;
        }

        try
        {
            CellFolder.CreateCellFolder(workspaceDir, name);
            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Created", Path.Combine(newCellDir, CellFolder.CcellFileName));
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create cell: {ex.Message}");
            return; // the cell was never created — nothing further to do.
        }

        // R-cc-1: same as NewCellAsync — the cell already exists regardless of what follows.
        await CreateAndOpenSchematicFileAsync(newCellDir, name, name);
    }

    /// <inheritdoc/>
    public async Task NewSymbolAsync(ProjectTreeNodeViewModel cellNode)
    {
        var cellDir   = cellNode.AbsolutePath;
        var symbolDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);

        if (!Directory.Exists(symbolDir))
        {
            Messages.Error($"Symbol sub-folder not found in '{cellNode.Name}'.");
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var suggested = ViewFileNameSuggestion.Suggest(cellDir, cellNode.Name, ViewType.Symbol);
        var dialog = new InputNameDialog("New Symbol", "Symbol file name (without extension):", suggested);
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid symbol name: {reason}");
            return;
        }

        var ext      = CellFolder.ViewExtension(ViewType.Symbol);
        var filePath = Path.Combine(symbolDir, name + ext);
        if (File.Exists(filePath))
        {
            Messages.Error($"A file named '{name}{ext}' already exists.");
            return;
        }

        try
        {
            // Write an empty .csym so the file exists on disk (Refresh picks it up).
            var emptySymbol = new Symbol(
                System.Array.AsReadOnly(System.Array.Empty<SymbolPrimitive>()),
                System.Array.AsReadOnly(System.Array.Empty<SymbolPin>()),
                portCount: 0);
            SymbolPersistence.SaveToFile(filePath, emptySymbol);

            _factory.ProjectTreeTool?.Refresh();

            // Open it in the Symbol Editor with a fresh editable symbol.
            var editable = new EditableSymbol { UserEditable = true };
            var vm  = new SymbolEditorViewModel(editable) { CurrentSymbolPath = filePath };
            vm.SymbolSaved += OnSymbolSaved;
            vm.SaveError   += OnSymbolSaveError;
            var doc = new SymbolEditorDocument(name + ext, vm, filePath);
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;
            HookSymbolCellDirty(doc);

            Messages.Success("Created", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create symbol: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task NewSchematicAsync(ProjectTreeNodeViewModel cellNode)
    {
        var cellDir    = cellNode.AbsolutePath;
        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var suggested = ViewFileNameSuggestion.Suggest(cellDir, cellNode.Name, ViewType.Schematic);
        var dialog = new InputNameDialog("New Schematic", "Schematic file name (without extension):", suggested);
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid schematic name: {reason}");
            return;
        }

        await CreateAndOpenSchematicFileAsync(cellDir, cellNode.Name, name);
    }

    /// <summary>
    /// The single schematic-creation path (brief-cell-first-and-ui-fixes.md R-cc-1): writes an empty
    /// <c>.csch</c> for <paramref name="fileNameWithoutExt"/> under the given cell's schematic
    /// sub-folder and opens it in a new tab, materialized (a real file path, not a scratch document).
    /// Used by both the tree's "New Schematic" (after its own name prompt) and New Cell's automatic
    /// primary schematic (R-cc-1) — one path, never two, so they cannot silently diverge. Reports its
    /// own failures via <see cref="Messages"/> and returns <c>false</c> rather than throwing, so a
    /// caller that already created something else (e.g. the cell folder itself) is never forced to
    /// roll that back just because this step failed.
    /// </summary>
    private async Task<bool> CreateAndOpenSchematicFileAsync(string cellDir, string cellName, string fileNameWithoutExt)
    {
        var schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        if (!Directory.Exists(schematicDir))
        {
            Messages.Error($"Schematic sub-folder not found in '{cellName}'.");
            return false;
        }

        var ext      = CellFolder.ViewExtension(ViewType.Schematic);
        var filePath = Path.Combine(schematicDir, fileNameWithoutExt + ext);
        if (File.Exists(filePath))
        {
            Messages.Error($"A file named '{fileNameWithoutExt}{ext}' already exists.");
            return false;
        }

        try
        {
            // Write an empty .csch, then open it for authoring.
            var emptyModel = new SchematicEditModel();
            SchematicPersistence.SaveToFile(filePath, emptyModel, cellName: cellName);

            _factory.ProjectTreeTool?.Refresh();

            // Open in a schematic content tab (materialized — has a real file path).
            // Use BuildSessionVm so wiring matches GetOrCreateSession exactly.
            var vm  = BuildSessionVm(emptyModel);
            RegisterSession(filePath, vm);
            var doc = new SchematicDocument(fileNameWithoutExt + ext, vm, filePath) { Messages = Messages, Hierarchy = this };
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;

            Messages.Success("Created", filePath);
            return true;
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create schematic: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task NewLayoutAsync(ProjectTreeNodeViewModel cellNode)
    {
        var cellDir   = cellNode.AbsolutePath;
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        if (!Directory.Exists(layoutDir))
        {
            Messages.Error($"Layout sub-folder not found in '{cellNode.Name}'.");
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var suggested = ViewFileNameSuggestion.Suggest(cellDir, cellNode.Name, ViewType.Layout);
        var dialog = new InputNameDialog("New Layout", "Layout file name (without extension):", suggested);
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid layout name: {reason}");
            return;
        }

        var ext      = CellFolder.ViewExtension(ViewType.Layout);
        var filePath = Path.Combine(layoutDir, name + ext);
        if (File.Exists(filePath))
        {
            Messages.Error($"A file named '{name}{ext}' already exists.");
            return;
        }

        try
        {
            var resolution = ResolveTechFor(techRef: null, clayPath: filePath);
            var tech = resolution.Tech;
            var model = new LayoutView
            {
                DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
                DisplayUnit  = tech?.DefaultDisplayUnit ?? LayoutUnit.Um,
                SnapDbu      = tech?.DefaultSnapDbu ?? 1000,
                AngleMode    = AngleMode.AnyAngle,
                TechRef      = null,
            };
            LayoutPersistence.SaveToFile(filePath, model);

            _factory.ProjectTreeTool?.Refresh();

            // Open in a Layout Editor tab (materialized — has a real file path). Register into the
            // L3b session registry immediately so this cell is push-in-able from elsewhere right away.
            var vm = RegisterLayoutSession(filePath, BuildLayoutSessionVm(model, filePath));
            var doc = new LayoutDocument(name + ext, vm, filePath) { Hierarchy = this };
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;
            HookLayoutCellDirty(doc);

            Messages.Success("Created", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create layout: {ex.Message}");
        }
    }

    // ── Duplicate Cell (Item 6) ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task DuplicateCellAsync(ProjectTreeNodeViewModel cellNode)
    {
        if (CurrentWorkspacePath is null) return;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var oldDir       = cellNode.AbsolutePath;
        var parentDir    = Path.GetDirectoryName(oldDir) ?? workspaceDir;

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dlg     = new InputNameDialog("Duplicate Cell", "New cell name:");
        var newName = await dlg.ShowDialog<string?>(mainWindow);
        if (newName is null) return;

        var reason = NameValidator.Validate(newName);
        if (reason is not null) { Messages.Error($"Invalid cell name: {reason}"); return; }

        var newDir = Path.Combine(parentDir, newName);
        if (Directory.Exists(newDir))
        {
            Messages.Error($"A cell or folder named '{newName}' already exists.");
            return;
        }

        try
        {
            CopyDirectoryRecursive(oldDir, newDir);

            // Rename primary schematic and symbol if present.
            foreach (var viewType in new[] { ViewType.Schematic, ViewType.Symbol })
            {
                var res = CellFolder.ResolvePrimary(newDir, viewType);
                if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    continue;

                var subDir     = CellFolder.SubFolderPath(newDir, viewType);
                var ext        = CellFolder.ViewExtension(viewType);
                var targetName = newName + ext;
                var targetPath = Path.Combine(subDir, targetName);

                if (res.ResolvedName is null) continue;
                var sourcePath = Path.Combine(subDir, res.ResolvedName);

                // Skip rename if a different non-primary file already has the target name.
                if (File.Exists(targetPath)
                    && !string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                    File.Move(sourcePath, targetPath);

                // Update .ccell to point to the renamed primary.
                UpdateCcellPrimary(newDir, viewType, targetName);
            }

            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Duplicated", newDir);
        }
        catch (Exception ex)
        {
            Messages.Error($"Duplicate failed: {ex.Message}");
        }
    }

    // ── Rename Cell (Item 7) ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task RenameCellAsync(ProjectTreeNodeViewModel cellNode)
    {
        if (CurrentWorkspacePath is null) return;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var oldDir       = cellNode.AbsolutePath;
        var oldName      = cellNode.Name;
        var parentDir    = Path.GetDirectoryName(oldDir) ?? workspaceDir;

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dlg    = new Views.Dialogs.RenameCellDialog(oldName);
        var result = await dlg.ShowDialog<(string? Name, bool RenamePrimaries)>(mainWindow);
        if (result.Name is null) return;
        var newName          = result.Name;
        var renamePrimaries  = result.RenamePrimaries;

        var reason = NameValidator.Validate(newName);
        if (reason is not null) { Messages.Error($"Invalid cell name: {reason}"); return; }

        if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            Messages.Info("Name unchanged."); return;
        }

        var newDir = Path.Combine(parentDir, newName);
        if (Directory.Exists(newDir) || File.Exists(newDir))
        {
            Messages.Error($"A cell or folder named '{newName}' already exists.");
            return;
        }

        // Require save + close any open docs under this cell before renaming.
        var cellDocs = _openDocsByPath
            .Where(kvp => IsPathOrUnder(kvp.Key, oldDir))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
        if (cellDocs.Count > 0)
        {
            if (HasAnyDirtyWork() && !await PromptSaveBeforeClose(mainWindow, $"renaming '{oldName}'"))
                return;
            foreach (var (key, dockable) in cellDocs)
            {
                _factory.ForceCloseDockable(dockable);
                if (key.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
                    RetireSessionIfUnreferenced(key);
                else if (key.EndsWith(".clay", StringComparison.OrdinalIgnoreCase))
                    RetireLayoutSessionIfUnreferenced(key);
            }
        }

        try { Directory.Move(oldDir, newDir); }
        catch (Exception ex) { Messages.Error($"Rename failed: {ex.Message}"); return; }

        // Rewrite all schematics that reference the old cell name.
        var rewritten = CellUsageScanner.RewriteCellReferences(workspaceDir, oldName, newName, out var failed);
        foreach (var f in failed)
            Messages.Warning($"Reference rewrite failed: {f}");
        if (rewritten.Count > 0)
            Messages.Info($"Updated {rewritten.Count} schematic reference(s) to '{newName}'.");

        // Optionally rename primary schematic + symbol.
        if (renamePrimaries)
        {
            foreach (var viewType in new[] { ViewType.Schematic, ViewType.Symbol })
            {
                var res = CellFolder.ResolvePrimary(newDir, viewType);
                if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    continue;

                var subDir     = CellFolder.SubFolderPath(newDir, viewType);
                var ext        = CellFolder.ViewExtension(viewType);
                var targetName = newName + ext;
                var targetPath = Path.Combine(subDir, targetName);

                if (res.ResolvedName is null) continue;
                var sourcePath = Path.Combine(subDir, res.ResolvedName);

                if (File.Exists(targetPath)
                    && !string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    Messages.Warning(
                        $"Skipped renaming {CellFolder.SubFolderName(viewType)} primary: '{targetName}' already exists as a non-primary file.");
                    continue;
                }

                if (!string.Equals(res.ResolvedName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Move(sourcePath, targetPath); }
                    catch (Exception ex)
                    {
                        Messages.Warning($"Could not rename {CellFolder.SubFolderName(viewType)} primary: {ex.Message}");
                        continue;
                    }
                }

                UpdateCcellPrimary(newDir, viewType, targetName);
            }
        }

        _factory.ProjectTreeTool?.Refresh();
        Messages.Success($"Renamed '{oldName}' → '{newName}'", newDir);
    }

    // Reads, updates, and re-saves a .ccell file's primary field for one view type.
    private static void UpdateCcellPrimary(string cellDir, ViewType viewType, string newPrimaryFileName)
    {
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;
        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            switch (viewType)
            {
                case ViewType.Schematic: ccell.PrimarySchematic = newPrimaryFileName; break;
                case ViewType.Symbol:    ccell.PrimarySymbol    = newPrimaryFileName; break;
            }
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch { /* non-fatal: .ccell update is best-effort for alpha */ }
    }

    // Copies a directory tree recursively (all files and sub-directories).
    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // ---- Active-document tracking (Properties region) ───────────────────────

    private async Task RefreshOpenDataDisplaysAsync(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0) return;
        var changed = new HashSet<string>(changedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var displays = _openDocsByPath.Values.OfType<DataDisplayDocument>()
            .Concat(_scratchDataDisplays);
        foreach (var dd in displays)
        {
            var lib = dd.ViewModel.Window.DataSourceLibrary;
            lib.RefreshAvailableDataSources();
            await lib.ReloadChangedAsync(changedPaths);
            // If the selected datasource file was among the changed paths, re-select to re-resolve traces.
            if (lib.SelectedDataSourceAbs is { } selAbs &&
                changed.Contains(Path.GetFullPath(selAbs)))
            {
                await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);
            }
        }
    }

    /// <summary>
    /// R-res-8/9/10 — after a successful run, opens (and focuses) the schematic's own
    /// <c>results/&lt;schematicKey&gt;.cdd</c> with no prompt when it already exists; otherwise creates
    /// it, pre-populates a non-empty default plot bound to the just-written results file, saves it, and
    /// opens it — also unprompted. This is a deliberate behavior change from the original "starts empty"
    /// Data Display decision: the whole point of the command is that a run "just works."
    /// </summary>
    private async Task AutoOpenOrCreateDataDisplayAsync(string baseDir, string schematicKey, string npyPath)
    {
        var resultsDir = Path.GetDirectoryName(npyPath) ?? Path.Combine(baseDir, "results");
        var cddPath    = Path.GetFullPath(Path.Combine(resultsDir, schematicKey + ".cdd"));

        if (File.Exists(cddPath))
        {
            OpenOrActivateDataDisplay(cddPath);
            return;
        }
        if (_openDocsByPath.TryGetValue(cddPath, out var existingDoc))
        {
            _factory.SetActiveDockable(existingDoc);
            return;
        }

        var newVm  = new DataDisplayDocumentViewModel();
        var newDoc = new DataDisplayDocument(Path.GetFileNameWithoutExtension(cddPath), newVm);
        _openDocsByPath[cddPath] = newDoc;   // register early so a re-entrant open dedups against it
        newVm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        newVm.Window.GetResultsRootAction = GetResultsRoot;
        WireDataDisplayLibraryEvents(newVm);
        _factory.OpenDocument(newDoc);

        var lib = newVm.Window.DataSourceLibrary;

        // Populate AvailableDataSources BEFORE selecting, and select by the same relative logical id
        // ("<name>.npy") those entries carry — never the absolute path. SelectedDataSourceItem's getter
        // matches AvailableDataSources by LogicalId == SelectedDataSourceRef; selecting by absolute path
        // (a) never matches an AvailableDataSources entry (all of which use the flat relative id) and
        // (b) would find nothing anyway in an unrefreshed (still-empty) combo — either alone was enough
        // to leave the toolbar combo blank even though SelectedEntry/traces resolve and render correctly
        // via SourcePath, which never went through the combo at all.
        lib.RefreshAvailableDataSources();
        await lib.SelectDataSourceAsync(Path.GetFileName(npyPath));

        bool populated = false;
        if (lib.SelectedEntry is { } entry)
        {
            var plotType = CircuitRF.Ui.DataDisplay.ViewModels.PlotInspectorViewModel.HasPlottableData(entry, allowScalars: false)
                ? PlotType.Rect
                : PlotType.Table;

            // A brand-new DisplayWindowViewModel's initial tab already seeds one empty Smith plot
            // (DataDisplayViewModel's own "starts empty; user authors it" constructor default) —
            // reuse THAT container instead of calling AddPlot, which would add a SECOND one. Only
            // one plot must ever exist after auto-create.
            var container = newVm.Window.DataDisplay?.Plots.FirstOrDefault();
            if (container is not null)
            {
                if (container.Inspector.PlotType != plotType)
                {
                    container.Inspector.PlotType = plotType;
                    bool square = plotType is PlotType.Smith or PlotType.Polar;
                    container.Width  = square ? 420 : 520;
                    container.Height = square ? 420 : 360;
                }
                if (container.Inspector.AddTraceCommand.CanExecute(null))
                {
                    container.Inspector.AddTraceCommand.Execute(null);
                    populated = container.Inspector.Traces.Count > 0;
                }
            }
        }
        if (!populated)
            Messages.Warning(
                $"Auto-created Data Display for '{schematicKey}' has no default plot — no plottable data was found in the run's results.",
                cddPath);

        try
        {
            await newVm.Window.SaveAllAsync(cddPath, 0, 0, 0, 0);
            newDoc.Materialize(cddPath);
            // The .cdd now exists on disk as a loose file at the workspace root — refresh the tree
            // so it appears there immediately, matching every other file-creating command's convention.
            _factory.ProjectTreeTool?.Refresh();
        }
        catch (Exception ex)
        {
            Messages.Warning($"Auto-created Data Display could not be saved: {ex.Message}", cddPath);
        }
    }

    /// <summary>
    /// Subscribes to the given data display document's <see cref="DisplayWindowViewModel.ActiveInspector"/>
    /// and routes changes to the Properties dock. Unsubscribes from any previously-subscribed window first.
    /// Null clears the data display context.
    /// </summary>
    private void RouteDataDisplayProperties(DataDisplayDocument? dd)
    {
        if (_subscribedDisplayWindow is not null && _displayInspectorHandler is not null)
            _subscribedDisplayWindow.PropertyChanged -= _displayInspectorHandler;
        _subscribedDisplayWindow = null;

        // R-stb-12: the Datasets list marks a source that lives OUTSIDE the workspace, so a user
        // about to share a workspace can see which sources will not travel with it.
        if (_factory.PropertiesTool?.DatasetsVm is { } dsVm)
            dsVm.WorkspaceRootProvider = () =>
                CurrentWorkspacePath is null ? null : System.IO.Path.GetDirectoryName(CurrentWorkspacePath);

        if (dd is null)
        {
            _factory.PropertiesTool?.SetActiveDataDisplay(null);
            return;
        }

        var window = dd.ViewModel.Window;
        _subscribedDisplayWindow = window;
        _displayInspectorHandler ??= (_, e) =>
        {
            if (e.PropertyName is nameof(CircuitRF.Ui.DataDisplay.ViewModels.DisplayWindowViewModel.ActiveInspector))
                _factory.PropertiesTool?.SetActiveDataDisplay(_subscribedDisplayWindow?.ActiveInspector, _subscribedDisplayWindow);
        };
        window.PropertyChanged += _displayInspectorHandler;
        _factory.PropertiesTool?.SetActiveDataDisplay(window.ActiveInspector, window);
    }

    // Points the Analyses panel (and the Run target) at a schematic document.
    // schName is the SchematicKey (the same value RunResultsWriter uses to name the results file) —
    // never the raw file name, which would carry the ".csch" extension into the Analyses panel's
    // "Results file:" placeholder (e.g. "HBTest.csch.npy" instead of "HBTest.npy").
    private void PointAnalysesAt(SchematicDocument sd)
    {
        _lastActiveSchematicDoc = sd;
        string schName = RunResultsWriter.SchematicKey(sd.FilePath, sd.Id);
        _factory.AnalysesTool?.SetActiveSchematic(sd.ViewModel, schName);
    }

    /// <summary>
    /// Cross-pane link: returns the open schematic whose base filename (sans extension) matches the
    /// focused Data Display's, so focusing a <c>.cdd</c> tab can show the like-named <c>.csch</c>'s
    /// analyses; null when none matches (the caller then retains the last schematic). Match is on the
    /// filename only (directory ignored), case-insensitive. Pure/static so it is unit-testable without
    /// the Avalonia runtime.
    /// </summary>
    internal static SchematicDocument? MatchSchematicForDataDisplay(
        DataDisplayDocument dataDisplay, IEnumerable<SchematicDocument> openSchematics)
    {
        static string? BaseName(string? filePath, string? fallbackId)
            => filePath is { } fp
                ? System.IO.Path.GetFileNameWithoutExtension(fp)
                : (string.IsNullOrEmpty(fallbackId) ? null : fallbackId);

        string? cddBase = BaseName(dataDisplay.FilePath, dataDisplay.Id);
        if (string.IsNullOrEmpty(cddBase)) return null;

        foreach (var sd in openSchematics)
            if (string.Equals(BaseName(sd.FilePath, sd.Id), cddBase, StringComparison.OrdinalIgnoreCase))
                return sd;
        return null;
    }

    private void OnDocumentDockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "ActiveDockable") return;

        var activeDockable = _factory.DocumentDock?.ActiveDockable;

        // The activated editor view should grab keyboard focus so shortcuts (Select All, nudges, …)
        // work without a preliminary click on the canvas. The view focuses its canvas on the event, or
        // — if it binds after this fires (first open) — by consuming the pending flag on DataContext change.
        (activeDockable as IActivatableDocument)?.RequestActivationFocus();

        // Properties panel — route to data display, schematic, symbol-editor, or cell inspector.
        if (activeDockable is DataDisplayDocument ddDoc)
        {
            RouteDataDisplayProperties(ddDoc);
        }
        else if (activeDockable is SymbolEditorDocument symDoc)
        {
            RouteDataDisplayProperties(null);
            _factory.PropertiesTool?.SetActiveSymbolEditor(symDoc.ViewModel);
            // Ports indicator may be stale if the owning cell's .ccell NumPorts changed in the cell
            // editor while this tab was inactive — re-read it on activation.
            if (symDoc.ViewModel.CurrentSymbolPath is { } sp)
                symDoc.ViewModel.SetExternalPortCount(TryCellPortCount(sp));
        }
        else if (activeDockable is CellParameterEditorDocument cpd)
        {
            RouteDataDisplayProperties(null);
            _factory.PropertiesTool?.SetActiveCell(cpd.ViewModel);
        }
        else if (activeDockable is LayoutDocument layDocForProps)
        {
            RouteDataDisplayProperties(null);
            ActivateLayoutDocumentForProperties(layDocForProps);
        }
        else
        {
            RouteDataDisplayProperties(null);
            var activeVm = activeDockable is SchematicDocument schDoc ? schDoc.ViewModel : null;
            _factory.PropertiesTool?.SetActiveSchematic(activeVm);
        }

        // Analyses panel — retain the last schematic so focusing a data display / symbol / cell tab
        // does NOT blank it. A schematic document updates it directly; a Data Display whose base
        // filename matches an OPEN schematic redirects it to that schematic, so its analyses show
        // beside the plots (otherwise the last schematic is retained, as before).
        if (activeDockable is SchematicDocument sd)
        {
            PointAnalysesAt(sd);
        }
        else if (activeDockable is DataDisplayDocument ddForAnalyses
                 && MatchSchematicForDataDisplay(
                        ddForAnalyses,
                        _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)) is { } linked)
        {
            PointAnalysesAt(linked);
        }

        // Undo routing — follows any IUndoableDocument for main-window tabs.
        SetActiveUndoTarget(activeDockable as IUndoableDocument);

        // Save-scope: "Save" when a document tab is active, "Save All" otherwise.
        ActiveSaveScope = activeDockable is IUndoableDocument
            ? SaveScope.SingleDoc
            : SaveScope.AllDocs;

        // Hierarchy commands depend on the active schematic document + its selection.
        RewireHierarchySubscriptions();

        // Generate Netlist is enabled only when a schematic document is active.
        GenerateNetlistCommand.NotifyCanExecuteChanged();

        // Export GDSII/DXF (item 8) are enabled only when a layout document is active.
        ExportGdsiiCommand.NotifyCanExecuteChanged();
        ExportDxfCommand.NotifyCanExecuteChanged();

        // Save Schematic As… / Save Layout As… are each enabled only when their own document type
        // is the active dockable.
        SaveLooseSchematicCommand.NotifyCanExecuteChanged();
        SaveLooseLayoutCommand.NotifyCanExecuteChanged();
        SaveLooseSymbolCommand.NotifyCanExecuteChanged();
        SaveAllDocumentsCommand.NotifyCanExecuteChanged();

        // Design menu (L5): each is enabled only when its own document type is the active dockable —
        // same rule, same fan-out, as the Save-As commands just above.
        UpdateLayoutFromSchematicCommand.NotifyCanExecuteChanged();
        UpdateSchematicFromLayoutCommand.NotifyCanExecuteChanged();

        // A dockable may have just been floated into a Dock-generated HostWindow.
        // Defer one frame (Background) so the HostWindow is fully shown before we scan.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            TryWireHostWindowsUndo,
            Avalonia.Threading.DispatcherPriority.Background);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            TryWireWindowFocusTracking,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // ---- Per-window active document — focus tracking (R-menu-4) ------------

    /// <summary>
    /// Scans all application windows and wires <c>Activated</c> tracking for R-menu-4: the main
    /// shell clears <see cref="_focusedWindowDocument"/> (deferring back to
    /// <c>DocumentDock.ActiveDockable</c>); a torn-off document window (a <c>CrfHostWindow</c>) sets
    /// it to its own hosted document while it has focus. A tool-only float (Properties, Analyses,
    /// Project Tree, Palette, Messages) is left alone — it has no document of its own to contribute,
    /// so its activation must not change which document File-menu commands target.
    /// </summary>
    private void TryWireWindowFocusTracking()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var shellWindow = desktop.Windows.OfType<Views.WorkspaceWindow>().FirstOrDefault();

        foreach (var window in desktop.Windows)
        {
            if (window is Views.SymbolEditorWindow) continue; // built-in-symbol preview, not a real document window
            if (_focusTrackedWindows.Contains(window)) continue;
            _focusTrackedWindows.Add(window);

            if (window is Views.WorkspaceWindow)
            {
                window.Activated += (_, _) =>
                {
                    _focusedWindowDocument   = null;
                    _focusedWindowIsToolOnly = false;
                    RaiseFileMenuEnablementChanged();
                };
                // The shell is typically already the active window at app-startup wiring time; this
                // mirrors the immediate IsActive check below so the shell case is handled uniformly
                // too, even though in practice a torn-off window is what actually races the scan.
                if (window.IsActive)
                {
                    _focusedWindowDocument   = null;
                    _focusedWindowIsToolOnly = false;
                    RaiseFileMenuEnablementChanged();
                }
                continue;
            }

            // A Dock-created CrfHostWindow (tool or document tear-off) — resolved fresh on every
            // activation since a floated window's own hosted document cannot change once created,
            // but re-resolving is cheap and avoids relying on that assumption.
            void ApplyFocusedDocument()
            {
                var doc = FindAnyDocumentInWindow(window);
                _focusTrackedWindowDocs[window] = doc;

                // EVERY floated window needs the macOS menu attached, not only one hosting a document.
                // On macOS the menu bar's contents follow the key window, and a window with no menu of
                // its own shows only the bare app menu — the owner-reported symptom for a floating
                // TOOL panel. Attaching the SAME NativeMenu instance (never a second, hand-built copy)
                // is what already fixes this for torn-off document windows; the only bug was the
                // `doc is not null` gate around it.
                if (shellWindow is not null)
                    AttachSharedNativeMenuIfMacOS(shellWindow, window);

                if (doc is not null)
                {
                    _focusedWindowDocument   = doc;
                    _focusedWindowIsToolOnly = false;
                    RaiseFileMenuEnablementChanged();
                }
                else if (WindowFloatsATool(window))
                {
                    // R-dock-13: a tool panel is NOT a document context, so _focusedWindowDocument is
                    // left alone and Save keeps targeting the last active document. Only the Close
                    // item changes: a tool panel belongs to the workspace, so it reads
                    // "Close Workspace".
                    _focusedWindowIsToolOnly = true;
                    RaiseFileMenuEnablementChanged();
                }
            }
            window.Activated += (_, _) => ApplyFocusedDocument();
            window.Closed += (_, _) =>
            {
                _focusTrackedWindows.Remove(window);
                var hadDoc = _focusTrackedWindowDocs.Remove(window, out var closedDoc);

                if (hadDoc && closedDoc is not null && ReferenceEquals(_focusedWindowDocument, closedDoc))
                {
                    _focusedWindowDocument   = null;
                    _focusedWindowIsToolOnly = false;
                    RaiseFileMenuEnablementChanged();
                }
                else if (_focusedWindowIsToolOnly)
                {
                    // A tool float closing (or being torn down by a layout rebuild) must not leave the
                    // Close item stuck on the workspace variant for whatever gets focus next.
                    _focusedWindowIsToolOnly = false;
                    RaiseFileMenuEnablementChanged();
                }
            };

            // Bug fix: a torn-off window is typically created AND already key/active before this
            // scan (itself deferred one frame) ever runs — its own real Activated event fires and is
            // missed entirely, so _focusedWindowDocument stayed stale ("Close Workspace" instead of
            // "Close Window") until some LATER, unrelated Activated event happened to fire. Checking
            // IsActive immediately upon wiring closes that gap without waiting for a future event.
            if (window.IsActive)
                ApplyFocusedDocument();
        }
    }

    /// <summary>
    /// macOS bug fix: <c>NativeMenu.Menu</c> is a PER-WINDOW attached property, not an application-
    /// global one — a torn-off document window has none of its own attached, so while it is key the OS
    /// shows only its bare default app menu ("circuitRF") instead of the File/Edit/View/Simulate/Help
    /// bar, exactly the reported symptom. Fix: attach the SAME <see cref="Avalonia.Controls.NativeMenu"/>
    /// instance already declared in <c>WorkspaceWindow.axaml</c> to the torn-off window too, rather than
    /// building a second, hand-rolled copy. This is safe because <c>NativeMenu</c>/<c>NativeMenuItem</c>
    /// derive from <c>AvaloniaObject</c>, not <c>StyledElement</c> (confirmed by reading Avalonia's own
    /// source) — they carry no DataContext and are not part of any window's visual/logical tree, so
    /// their compiled <c>{Binding ...}</c> expressions resolve against the ORIGINAL file's root object
    /// (<paramref name="shellWindow"/>, whose own DataContext is the <see cref="WorkspaceViewModel"/>)
    /// regardless of which window the menu is later attached to. <c>NativeMenu.SetMenu</c> is a plain
    /// attached-property setter with no reparenting/exclusivity guard — each window that has it set
    /// gets its own independent platform exporter (per Avalonia's own <c>NativeMenu.GetInfo</c>), so
    /// attaching the same instance to a second window does not detach it from the first.
    /// </summary>
    private static void AttachSharedNativeMenuIfMacOS(Window shellWindow, Window tornOffWindow)
    {
        if (!OperatingSystem.IsMacOS()) return;

        var menu = Avalonia.Controls.NativeMenu.GetMenu(shellWindow);
        if (menu is not null)
            Avalonia.Controls.NativeMenu.SetMenu(tornOffWindow, menu);
    }

    /// <summary>Finds the first non-<see cref="ITool"/> dockable reachable from a window's
    /// DataContext — mirrors <see cref="FindUndoDocInWindow"/>'s tree-walk shape exactly, generalized
    /// from "the floated document that supports undo" to "the floated document, of any kind."</summary>
    /// <summary>
    /// True when a floated window's own layout contains a tool panel — i.e. it is a torn-off
    /// Properties/Analyses/Project Tree/Palette/Messages window rather than a document one. Uses the
    /// SAME predicate the factory uses to decide owner mode and teardown, so "is this a tool window"
    /// has one answer across the app.
    /// </summary>
    internal static bool WindowFloatsATool(Window window) =>
        window.DataContext is IDockable d && Dock.CircuitRfDockFactory.ContainsTool(d);

    internal static IDockable? FindAnyDocumentInWindow(Window window)
    {
        if (window.DataContext is IDock dock) return FindAnyDocumentInDock(dock);
        if (window.DataContext is IDockable direct and not ITool and not IDock) return direct;
        return null;
    }

    internal static IDockable? FindAnyDocumentInDock(IDock dock)
    {
        if (dock.ActiveDockable is IDock nestedActive)
        {
            var result = FindAnyDocumentInDock(nestedActive);
            if (result is not null) return result;
        }
        else if (dock.ActiveDockable is IDockable active and not ITool)
        {
            return active;
        }

        if (dock.VisibleDockables is null) return null;
        foreach (var dockable in dock.VisibleDockables)
        {
            if (dockable is IDock childDock)
            {
                var result = FindAnyDocumentInDock(childDock);
                if (result is not null) return result;
            }
            else if (dockable is not ITool and not null)
            {
                return dockable;
            }
        }
        return null;
    }

    /// <summary>Refreshes every File-menu enablement predicate + <see cref="ActiveSaveScope"/> after
    /// <see cref="_focusedWindowDocument"/> changes — the one place this fan-out happens, mirroring
    /// <see cref="OnDocumentDockPropertyChanged"/>'s own equivalent fan-out for the shell's own
    /// ActiveDockable changes.</summary>
    private void RaiseFileMenuEnablementChanged()
    {
        var doc = ResolveActiveDocumentForCommands();
        ActiveSaveScope = doc is IUndoableDocument ? SaveScope.SingleDoc : SaveScope.AllDocs;

        ExportGdsiiCommand.NotifyCanExecuteChanged();
        ExportDxfCommand.NotifyCanExecuteChanged();
        SaveLooseSchematicCommand.NotifyCanExecuteChanged();
        SaveLooseLayoutCommand.NotifyCanExecuteChanged();
        SaveLooseSymbolCommand.NotifyCanExecuteChanged();
        SaveAllDocumentsCommand.NotifyCanExecuteChanged();
        CloseWorkspaceOrWindowCommand.NotifyCanExecuteChanged();
        UpdateLayoutFromSchematicCommand.NotifyCanExecuteChanged();
        UpdateSchematicFromLayoutCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CloseWorkspaceOrWindowHeader));
    }

    // ---- Dock float — per-window undo wiring --------------------------------

    // Scans all application windows for Dock-created host windows that are not yet
    // wired with undo/redo key bindings and injects them.  Called deferred after every
    // ActiveDockable change so it catches newly-floated documents.
    private void TryWireHostWindowsUndo()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop) return;

        foreach (var window in desktop.Windows)
        {
            // Skip our own known window types — they have their own undo handling.
            if (window is Views.WorkspaceWindow or Views.SymbolEditorWindow) continue;
            if (_wiredHostWindows.Contains(window)) continue;

            var undoDoc = FindUndoDocInWindow(window);
            if (undoDoc is null) continue;

            WireWindowUndo(window, undoDoc);
        }
    }

    // Finds the first IUndoableDocument reachable from a window's DataContext.
    // Dock's HostWindow sets DataContext to the IDockWindow (an IDock) that contains
    // the layout with the floated dockable.
    private static IUndoableDocument? FindUndoDocInWindow(Window window)
    {
        if (window.DataContext is IUndoableDocument direct) return direct;
        if (window.DataContext is IDock dock) return FindUndoDocInDock(dock);
        return null;
    }

    private static IUndoableDocument? FindUndoDocInDock(IDock dock)
    {
        if (dock is IUndoableDocument ud) return ud;
        if (dock.ActiveDockable is IUndoableDocument active) return active;
        if (dock.ActiveDockable is IDock nestedActive)
        {
            var result = FindUndoDocInDock(nestedActive);
            if (result is not null) return result;
        }
        if (dock.VisibleDockables is null) return null;
        foreach (var dockable in dock.VisibleDockables)
        {
            if (dockable is IUndoableDocument ud2) return ud2;
            if (dockable is IDock childDock)
            {
                var result = FindUndoDocInDock(childDock);
                if (result is not null) return result;
            }
        }
        return null;
    }

    // Injects Ctrl+Z / Cmd+Z / Ctrl+Shift+Z / Cmd+Shift+Z / Ctrl+Y key bindings
    // onto a Dock-created host window, pointing at the given document's own stack.
    // Mirrors the pattern used in SetActiveUndoTarget (PropertyChanged subscribe).
    private void WireWindowUndo(Window window, IUndoableDocument undoDoc)
    {
        _wiredHostWindows.Add(window);

        var stack   = undoDoc.UndoRedo;
        var undoCmd = new RelayCommand(stack.Undo, () => stack.CanUndo);
        var redoCmd = new RelayCommand(stack.Redo, () => stack.CanRedo);

        void OnStackChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) undoCmd.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) redoCmd.NotifyCanExecuteChanged();
        }
        stack.PropertyChanged += OnStackChanged;

        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control),                       Command = undoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Meta),                          Command = undoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift),  Command = redoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Meta    | KeyModifiers.Shift),  Command = redoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Y, KeyModifiers.Control),                       Command = redoCmd });

        window.Closed += (_, _) =>
        {
            stack.PropertyChanged -= OnStackChanged;
            _wiredHostWindows.Remove(window);
        };
    }

    // ---- Tab-close prompt (CircuitRfDockFactory hook) -----------------------

    // Shown before any dockable is removed. Returns true = proceed, false = cancel.
    private async Task<bool> ConfirmCloseDockable(IDockable dockable)
    {
        var window = ResolveOwner(null);
        if (window is null) return true;

        // Schematic document.
        if (dockable is SchematicDocument doc && doc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{doc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            return dlg.Result switch
            {
                SaveChangesResult.Cancel   => false,
                SaveChangesResult.DontSave => true,
                SaveChangesResult.Save     => await SaveSingleDocument(doc, window),
                _                          => false,
            };
        }

        // Symbol editor document.
        if (dockable is SymbolEditorDocument symDoc && symDoc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{symDoc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            switch (dlg.Result)
            {
                case SaveChangesResult.Cancel:
                    return false;
                case SaveChangesResult.DontSave:
                    return true;
                case SaveChangesResult.Save:
                    await SaveSingleSymbolDocument(symDoc, window);
                    // Cancel in the save-target dialog counts as "save cancelled" → cancel close.
                    return !symDoc.IsDirty;
                default:
                    return false;
            }
        }

        // Layout editor document.
        if (dockable is LayoutDocument layDoc && layDoc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{layDoc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            switch (dlg.Result)
            {
                case SaveChangesResult.Cancel:
                    return false;
                case SaveChangesResult.DontSave:
                    return true;
                case SaveChangesResult.Save:
                    await SaveSingleLayoutDocument(layDoc, window);
                    // Cancel in the save-target dialog counts as "save cancelled" → cancel close.
                    return !layDoc.IsDirty;
                default:
                    return false;
            }
        }

        // Technology editor document — always materialized, never scratch, so Save is a direct
        // write (no offer-target dialog like Layout/Symbol).
        if (dockable is TechDocument techDoc && techDoc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{techDoc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            switch (dlg.Result)
            {
                case SaveChangesResult.Cancel:
                    return false; // override stays in force — nothing changes
                case SaveChangesResult.DontSave:
                    _techCache.ClearLive(techDoc.FilePath); // open layouts revert to the on-disk technology
                    return true;
                case SaveChangesResult.Save:
                    techDoc.ViewModel.SaveCommand.Execute(null); // clears the override itself, via OnTechSaved -> Invalidate
                    return !techDoc.IsDirty;
                default:
                    return false;
            }
        }

        // Data display document.
        if (dockable is DataDisplayDocument ddDoc && ddDoc.ViewModel.Window.HasUnsavedChanges())
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{ddDoc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);
            return dlg.Result switch
            {
                SaveChangesResult.Cancel   => false,
                SaveChangesResult.DontSave => true,
                SaveChangesResult.Save     => await SaveDataDisplayDoc(ddDoc, window),
                _                          => false,
            };
        }

        return true; // clean doc — no prompt needed
    }

    // Fires after confirm and before base.CloseDockable removes the dockable from the layout.
    // Clean up tracking so _scratchDocs, _scratchSymbols, and _openDocsByPath stay consistent.
    private void OnDockableClosed(IDockable dockable)
    {
        // Remove from scratch-docs lists for any document type.
        if (dockable is SchematicDocument scratchCandidate)
            _scratchDocs.Remove(scratchCandidate);
        if (dockable is SymbolEditorDocument scratchSymbol)
            _scratchSymbols.Remove(scratchSymbol);
        if (dockable is DataDisplayDocument scratchDisplay)
            _scratchDataDisplays.Remove(scratchDisplay);
        if (dockable is LayoutDocument scratchLayout)
            _scratchLayouts.Remove(scratchLayout);

        // A .ctech editor being disposed for ANY reason (not just the confirmed-dirty-close path
        // above — e.g. a force-close, or a bug in some other path that skips the confirm hook) must
        // never leave a live override dangling in the cache. No-op when nothing was installed (a
        // clean or already-saved document has none), so this is safe to call unconditionally.
        if (dockable is TechDocument closedTechDoc)
            _techCache.ClearLive(closedTechDoc.FilePath);

        // Unsubscribe from cell edit model events to prevent memory leaks.
        if (dockable is CellParameterEditorDocument cellDoc)
        {
            cellDoc.ViewModel.EditModel.PrimarySymbolChanged -= OnCellPrimarySymbolChanged;
            cellDoc.ViewModel.EditModel.PortCountChanged     -= OnCellPortCountChanged;
        }

        // Remove any _openDocsByPath entry whose value is this dockable (reference equality),
        // regardless of document type — fixes reopen after close for symbol, schematic, and cell docs.
        var keysToRemove = _openDocsByPath
            .Where(kvp => ReferenceEquals(kvp.Value, dockable))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in keysToRemove)
            _openDocsByPath.Remove(key);

        // After removing the doc from _openDocsByPath, retire its session if clean + unreferenced.
        if (dockable is SchematicDocument closedSchDoc && closedSchDoc.FilePath is { } closedPath)
            RetireSessionIfUnreferenced(closedPath);

        // If the retained schematic is closed, blank the Analyses panel.
        if (ReferenceEquals(dockable, _lastActiveSchematicDoc))
        {
            _lastActiveSchematicDoc = null;
            _factory.AnalysesTool?.SetActiveSchematic(null);
        }
    }

    // ---- Save All documents (⌘S / Ctrl+S) ----------------------------------

    /// <summary>
    /// Gates the "Save"/"Save All" menu item per §3/R13a: enabled only when there is something for
    /// <see cref="SaveAllDocuments"/> to actually act on — the R-menu-4 per-window active document's
    /// own dirty flag when one of the five saveable document types is active, or "any dirty work
    /// anywhere" (<see cref="HasAnyDirtyWork"/>) for the AllDocs/no-document-active case. The menu
    /// item's own tooltip states the reason ("Nothing to save.") per R13a, mirroring the existing
    /// static-reason convention already used by the GDSII/DXF export items. <c>RelayCommand.CanExecute</c>
    /// is evaluated fresh at invocation time regardless of when <c>NotifyCanExecuteChanged</c> was last
    /// called, so Ctrl+S can never be wrongly blocked by a stale visual state — only the menu item's
    /// enabled/disabled APPEARANCE can lag until the next refresh point (tab switch, window focus
    /// change, or a completed save), which is the deliberate, narrower scope of this gate.
    /// </summary>
    private bool CanSaveAllDocuments() => ResolveActiveDocumentForCommands() switch
    {
        DataDisplayDocument dd   => dd.ViewModel.Window.HasUnsavedChanges(),
        SchematicDocument sd     => sd.IsDirty,
        SymbolEditorDocument syd => syd.IsDirty,
        LayoutDocument ld        => ld.IsDirty,
        TechDocument td          => td.IsDirty,
        _                        => HasAnyDirtyWork(),
    };

    /// <summary>
    /// Routes ⌘S/Ctrl+S.  When a document tab is active (SingleDoc scope) saves only that
    /// document.  When a tool panel is active (AllDocs scope) saves all dirty documents and
    /// updates the .cws.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveAllDocuments))]
    private async Task SaveAllDocuments(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        try
        {
            // Active Data Display — save (or save-as) the focused .cdd, consistent with
            // how Ctrl+S saves an active schematic or symbol. R-menu-4: resolved per-window, so a
            // torn-off data display window's own Save works even while the shell shows something else.
            if (ResolveActiveDocumentForCommands() is DataDisplayDocument activeDisplay)
            {
                if (!activeDisplay.ViewModel.Window.HasUnsavedChanges())
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveDataDisplayDoc(activeDisplay, window);
                return;
            }

            // SingleDoc scope: save only the active document.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                ResolveActiveDocumentForCommands() is SchematicDocument singleDoc)
            {
                if (!singleDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveSingleDocument(singleDoc, window);
                return;
            }

            // SingleDoc scope for an active symbol editor — scratch → offer dialog; materialized → PerformSave.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                ResolveActiveDocumentForCommands() is SymbolEditorDocument singleSymDoc)
            {
                if (!singleSymDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveSingleSymbolDocument(singleSymDoc, window);
                return;
            }

            // SingleDoc scope for an active layout editor — scratch → offer dialog; materialized → PerformSave.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                ResolveActiveDocumentForCommands() is LayoutDocument singleLayDoc)
            {
                if (!singleLayDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveSingleLayoutDocument(singleLayDoc, window);
                return;
            }

            // SingleDoc scope for an active technology editor — always materialized, direct write.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                ResolveActiveDocumentForCommands() is TechDocument singleTechDoc)
            {
                if (!singleTechDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                singleTechDoc.ViewModel.SaveCommand.Execute(null);
                return;
            }

            // AllDocs scope: save every dirty document.
            var dirtyScratch = _scratchDocs.Where(d => d.IsDirty).ToList();
            var dirtyMaterialized = _openDocsByPath.Values
                .OfType<SchematicDocument>()
                .Where(d => d.IsDirty && !d.IsScratch)
                .ToList();
            var dirtyScratchSymbols = _scratchSymbols.Where(d => d.IsDirty).ToList();
            var dirtyMaterializedSymbols = _openDocsByPath.Values
                .OfType<SymbolEditorDocument>()
                .Where(d => d.IsDirty && !d.IsScratch)
                .ToList();
            var dirtyScratchLayouts = _scratchLayouts.Where(d => d.IsDirty).ToList();
            var dirtyMaterializedLayouts = _openDocsByPath.Values
                .OfType<LayoutDocument>()
                .Where(d => d.IsDirty && !d.IsScratch)
                .ToList();
            var dirtyTechDocs = _openDocsByPath.Values
                .OfType<TechDocument>()
                .Where(d => d.IsDirty)
                .ToList();

            bool anyDirty = dirtyScratch.Count > 0 || dirtyMaterialized.Count > 0
                         || dirtyScratchSymbols.Count > 0 || dirtyMaterializedSymbols.Count > 0
                         || dirtyScratchLayouts.Count > 0 || dirtyMaterializedLayouts.Count > 0
                         || dirtyTechDocs.Count > 0;
            if (!anyDirty)
            {
                Messages.Info("Nothing to save.");
                return;
            }

            // Scratch schematic docs → plan dialog → execute
            if (dirtyScratch.Count > 0)
            {
                var builder     = new SavePlanBuilder(
                    CurrentWorkspacePath, _lastWorkspaceParentDir, dirtyScratch.AsReadOnly());
                var initialPlan = builder.Build();

                var confirmedPlan = await new Views.Dialogs.SavePlanDialog(initialPlan, builder)
                    .ShowDialog<SavePlan?>(window);
                if (confirmedPlan is null) return;  // user cancelled

                ExecuteSavePlan(confirmedPlan);
            }

            // Already-materialized dirty schematic docs — write directly.
            foreach (var doc in dirtyMaterialized)
            {
                if (doc.FilePath is null) continue;
                try
                {
                    SchematicPersistence.SaveToFile(doc.FilePath, doc.ViewModel.EditModel, doc.Id);
                    doc.Materialize(doc.FilePath);  // clears dirty (FilePath unchanged)
                    NotifySessionSaved(doc.FilePath);
                    Messages.Success("Saved", doc.FilePath);
                }
                catch (Exception ex)
                {
                    Messages.Error($"Failed to save '{doc.Id}': {ex.Message}");
                }
            }

            // Dirty sessions with no open tab (orphaned by a prior "Don't Save" close or hier2+ pop-out).
            foreach (var sessionPath in _registry.GetOrphanedDirtyPaths(IsSessionReferenced))
            {
                if (!_registry.TryGet(sessionPath, out var sessionVm)) continue;
                try
                {
                    var cellName = Path.GetFileNameWithoutExtension(sessionPath);
                    SchematicPersistence.SaveToFile(sessionPath, sessionVm!.EditModel, cellName);
                    NotifySessionSaved(sessionPath);
                    Messages.Success("Saved", sessionPath);
                }
                catch (Exception ex)
                {
                    Messages.Error($"Failed to save session '{sessionPath}': {ex.Message}");
                }
            }

            // Scratch symbol docs — per-doc offer dialog.
            foreach (var symDoc in dirtyScratchSymbols)
                await SaveScratchSymbol(symDoc, window);

            // Already-materialized dirty symbol docs — write directly via VM.
            foreach (var symDoc in dirtyMaterializedSymbols)
                await SaveMaterializedSymbolDoc(symDoc, window);

            // Scratch layout docs — per-doc offer dialog.
            foreach (var layDoc in dirtyScratchLayouts)
                await SaveScratchLayout(layDoc, window);

            // Already-materialized dirty layout docs — write directly via VM.
            foreach (var layDoc in dirtyMaterializedLayouts)
                await SaveMaterializedLayoutDoc(layDoc, window);

            // Dirty layout sessions with no open tab (orphaned by a prior "Don't Save" close or a pop-out).
            foreach (var sessionPath in _layoutRegistry.GetOrphanedDirtyPaths(IsLayoutSessionReferenced))
            {
                if (!_layoutRegistry.TryGet(sessionPath, out var sessionVm)) continue;
                try
                {
                    LayoutPersistence.SaveToFile(sessionPath, sessionVm!.Model);
                    NotifyLayoutSessionSaved(sessionPath);
                    Messages.Success("Saved", sessionPath);
                }
                catch (Exception ex)
                {
                    Messages.Error($"Failed to save layout session '{sessionPath}': {ex.Message}");
                }
            }

            // Dirty technology docs — always materialized, direct write via VM.
            foreach (var techDoc in dirtyTechDocs)
                techDoc.ViewModel.SaveCommand.Execute(null);
        }
        finally
        {
            // Save All always refreshes the .cws (open-doc snapshot + tree state) when a workspace
            // is open — even when nothing was dirty, and even when no documents are open (null list).
            // For single-doc saves the .cws is still written (persists open-doc list) but silently:
            // the user only asked to save one file, so the .cws message would be noise.
            if (CurrentWorkspacePath is not null)
                WriteWorkspaceFile(CurrentWorkspacePath, silent: ActiveSaveScope != SaveScope.AllDocs);

            // Refresh Save's own visual enabled/disabled state immediately after a save completes,
            // per §3's "disabled with a reason" rule — see CanSaveAllDocuments's own doc comment for
            // why staleness here is cosmetic only, never a functional Ctrl+S block.
            SaveAllDocumentsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Executes a confirmed <see cref="SavePlan"/>: delegates file I/O to
    /// <see cref="SavePlanExecutor"/>, then updates Dock state, workspace path,
    /// recent list, and tree — and reports every file written.
    /// </summary>
    private void ExecuteSavePlan(SavePlan plan)
    {
        // Derive the current workspace dir (may be null if this plan creates one).
        var existingWsDir = CurrentWorkspacePath is not null
            ? Path.GetDirectoryName(CurrentWorkspacePath)
            : null;

        IReadOnlyList<string> written;
        try
        {
            written = SavePlanExecutor.ExecuteFileOps(plan, existingWsDir);
        }
        catch (Exception ex)
        {
            Messages.Error($"Save failed: {ex.Message}");
            return;
        }

        // ── Post-IO: update VM state for a newly-created workspace ────────────
        if (plan.WorkspaceStep is { } wsStep)
        {
            var newWsDir = Path.Combine(wsStep.ParentDir, wsStep.Name);
            var cwsPath  = Path.Combine(newWsDir, ".cws");
            _lastWorkspaceParentDir = wsStep.ParentDir;
            CurrentWorkspacePath    = cwsPath;
            _factory.ProjectTreeTool?.SetActions(this);
            SubscribeToFilterState();
            SubscribeToTreeSelection();
            _factory.ProjectTreeTool?.SetWorkspace(newWsDir);
            PushRecent(cwsPath);
        }

        // ── Move docs: scratch → materialized tracking ────────────────────────
        foreach (var saveStep in plan.SaveSteps)
        {
            _scratchDocs.Remove(saveStep.Document);
            _recovery.ClearDoc(saveStep.Document);
            if (saveStep.Document.FilePath is { } fp)
            {
                _openDocsByPath[fp] = saveStep.Document;
                // Register the now-materialized VM in the session registry and clear dirty.
                RegisterSession(fp, saveStep.Document.ViewModel);
                NotifySessionSaved(fp);
            }
        }

        // ── Refresh tree + report ─────────────────────────────────────────────
        _factory.ProjectTreeTool?.Refresh();

        foreach (var p in written)
            Messages.Success("Saved", p);
    }

    // ---- Close / quit prompt helpers -----------------------------------------

    /// <summary>
    /// True when any open document has unsaved content. brief-foreign-documents.md R-fgn-2/R-fgn-5:
    /// <paramref name="includeFloated"/> is false for a WORKSPACE SWITCH/CLOSE check, and now means
    /// "skip only the torn-off documents that will SURVIVE that switch" — i.e. the foreign ones.
    ///
    /// <para>A torn-off document whose file lives inside the workspace being left is closed by the
    /// switch (see <see cref="CloseFloatedDocumentsOwnedByWorkspace"/>), so it must be counted here:
    /// anything the switch will CLOSE has to be something the switch first OFFERS TO SAVE, or unsaved
    /// work vanishes silently. A FOREIGN torn-off document survives untouched and so still has nothing
    /// to warn about at a switch.</para>
    ///
    /// <para>It stays true (the default) for the QUIT prompt and every other caller, since quitting the
    /// app really would discard a dirty foreign document's unsaved work if nobody was asked. An
    /// orphaned dirty session (no open document references it at all — nothing survives to keep it
    /// alive) always counts, regardless of <paramref name="includeFloated"/>.</para>
    /// </summary>
    public bool HasAnyDirtyWork(bool includeFloated = true)
    {
        // A floated document that the workspace switch is about to CLOSE counts as if it were docked —
        // see the note above. Only a foreign float (which survives) is skipped.
        bool Keep(IDockable d) => includeFloated || IsDockableDocked(d) || FloatedDocumentClosesWithWorkspace(d);

        return _scratchDocs.Any(d => d.IsDirty && Keep(d))
            || _openDocsByPath.Values.OfType<SchematicDocument>().Any(d => d.IsDirty && Keep(d))
            || _scratchSymbols.Any(d => d.IsDirty && Keep(d))
            || _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d => d.IsDirty && Keep(d))
            || _scratchLayouts.Any(d => d.IsDirty && Keep(d))
            || _openDocsByPath.Values.OfType<LayoutDocument>().Any(d => d.IsDirty && Keep(d))
            || _openDocsByPath.Values.OfType<TechDocument>().Any(d => d.IsDirty && Keep(d))
            || _scratchDataDisplays.Any(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d))
            || _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d))
            || HasOrphanedDirtySession()
            || HasOrphanedDirtyLayoutSession();
    }

    /// <summary>
    /// Shows Save / Don't Save / Cancel for dirty work before a close/quit/open action.
    /// Returns true when it's safe to proceed (saved or discarded), false when cancelled.
    /// <paramref name="includeFloated"/> mirrors <see cref="HasAnyDirtyWork"/> exactly — pass false
    /// from a workspace switch/close caller so a SURVIVING (foreign) torn-off document is neither
    /// counted nor swept into "Save All" here (R-fgn-2), while one that the switch will actually close
    /// is; leave it true (the default) for quit.
    /// </summary>
    public async Task<bool> PromptSaveBeforeClose(Window owner, string context = "closing", bool includeFloated = true)
    {
        // A floated document that the workspace switch is about to CLOSE counts as if it were docked —
        // see the note above. Only a foreign float (which survives) is skipped.
        bool Keep(IDockable d) => includeFloated || IsDockableDocked(d) || FloatedDocumentClosesWithWorkspace(d);

        var dirtyScratch = _scratchDocs.Where(d => d.IsDirty && Keep(d)).ToList();
        var dirtyMat     = _openDocsByPath.Values
            .OfType<SchematicDocument>()
            .Where(d => d.IsDirty && !d.IsScratch && Keep(d))
            .ToList();
        var dirtyScratchSymbols = _scratchSymbols.Where(d => d.IsDirty && Keep(d)).ToList();
        var dirtyMatSymbols     = _openDocsByPath.Values
            .OfType<SymbolEditorDocument>()
            .Where(d => d.IsDirty && !d.IsScratch && Keep(d))
            .ToList();
        var dirtyScratchDisplays = _scratchDataDisplays
            .Where(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d)).ToList();
        var dirtyMatDisplays = _openDocsByPath.Values
            .OfType<DataDisplayDocument>()
            .Where(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d)).ToList();
        var dirtyScratchLayouts = _scratchLayouts.Where(d => d.IsDirty && Keep(d)).ToList();
        var dirtyMatLayouts     = _openDocsByPath.Values
            .OfType<LayoutDocument>()
            .Where(d => d.IsDirty && !d.IsScratch && Keep(d))
            .ToList();
        var dirtyTechDocs = _openDocsByPath.Values
            .OfType<TechDocument>()
            .Where(d => d.IsDirty && Keep(d))
            .ToList();
        var dirtyOrphanedSessions       = _registry.GetOrphanedDirtyPaths(IsSessionReferenced);
        var dirtyOrphanedLayoutSessions = _layoutRegistry.GetOrphanedDirtyPaths(IsLayoutSessionReferenced);

        int total = dirtyScratch.Count + dirtyMat.Count
                  + dirtyScratchSymbols.Count + dirtyMatSymbols.Count
                  + dirtyScratchDisplays.Count + dirtyMatDisplays.Count
                  + dirtyScratchLayouts.Count + dirtyMatLayouts.Count
                  + dirtyTechDocs.Count
                  + dirtyOrphanedSessions.Count
                  + dirtyOrphanedLayoutSessions.Count;
        if (total == 0) return true;

        // Build a concise message naming the single doc or giving the count.
        string? firstId =
              dirtyScratch.Count               > 0 ? dirtyScratch[0].Id
            : dirtyScratchSymbols.Count        > 0 ? dirtyScratchSymbols[0].Id
            : dirtyScratchLayouts.Count        > 0 ? dirtyScratchLayouts[0].Id
            : dirtyMat.Count                   > 0 ? dirtyMat[0].Id
            : dirtyMatSymbols.Count            > 0 ? dirtyMatSymbols[0].Id
            : dirtyMatLayouts.Count            > 0 ? dirtyMatLayouts[0].Id
            : dirtyTechDocs.Count              > 0 ? dirtyTechDocs[0].Id
            : dirtyMatDisplays.Count           > 0 ? dirtyMatDisplays[0].Id
            : dirtyScratchDisplays.Count       > 0 ? dirtyScratchDisplays[0].Id
            : dirtyOrphanedSessions.Count       > 0 ? Path.GetFileNameWithoutExtension(dirtyOrphanedSessions[0])
            : dirtyOrphanedLayoutSessions.Count > 0 ? Path.GetFileNameWithoutExtension(dirtyOrphanedLayoutSessions[0])
            : null;
        string msg = (total == 1 && firstId is not null)
            ? $"Save '{firstId}' before {context}?"
            : $"You have {total} unsaved document(s). Save before {context}?";

        var dlg = new Views.Dialogs.SaveChangesDialog(msg, saveLabel: "Save All", cancelLabel: "Cancel", title: "Unsaved Changes");
        await dlg.ShowDialog(owner);

        switch (dlg.Result)
        {
            case SaveChangesResult.Cancel:
                return false;

            case SaveChangesResult.DontSave:
                // Discarding a dirty .ctech must revert open layouts to the on-disk technology —
                // clear its live override rather than leaving unsaved edits visible after "discard".
                foreach (var techDoc in dirtyTechDocs)
                    _techCache.ClearLive(techDoc.FilePath);
                return true; // discard everything else — caller proceeds

            case SaveChangesResult.Save:
                // Scratch schematics → plan dialog.
                if (dirtyScratch.Count > 0)
                {
                    var builder  = new SavePlanBuilder(CurrentWorkspacePath, _lastWorkspaceParentDir,
                                                       dirtyScratch.AsReadOnly());
                    var plan     = builder.Build();
                    var confirmed = await new Views.Dialogs.SavePlanDialog(plan, builder)
                        .ShowDialog<SavePlan?>(owner);
                    if (confirmed is null) return false; // plan cancelled → abort
                    ExecuteSavePlan(confirmed);
                }
                // Materialized dirty schematics → direct write.
                foreach (var doc in dirtyMat)
                {
                    if (doc.FilePath is null) continue;
                    try
                    {
                        SchematicPersistence.SaveToFile(doc.FilePath, doc.ViewModel.EditModel, doc.Id);
                        doc.Materialize(doc.FilePath);
                        NotifySessionSaved(doc.FilePath);
                        Messages.Success("Saved", doc.FilePath);
                    }
                    catch (Exception ex)
                    {
                        Messages.Error($"Failed to save '{doc.Id}': {ex.Message}");
                    }
                }
                // Orphaned dirty sessions (no open tab) → write directly.
                foreach (var sessionPath in dirtyOrphanedSessions)
                {
                    if (!_registry.TryGet(sessionPath, out var sessionVm)) continue;
                    try
                    {
                        var cellName = Path.GetFileNameWithoutExtension(sessionPath);
                        SchematicPersistence.SaveToFile(sessionPath, sessionVm!.EditModel, cellName);
                        NotifySessionSaved(sessionPath);
                        Messages.Success("Saved", sessionPath);
                    }
                    catch (Exception ex)
                    {
                        Messages.Error($"Failed to save session '{sessionPath}': {ex.Message}");
                    }
                }
                // Scratch symbols → per-doc offer dialog (same as AllDocs scope).
                foreach (var symDoc in dirtyScratchSymbols)
                    await SaveScratchSymbol(symDoc, owner);
                // Materialized dirty symbols → write directly via VM.
                foreach (var symDoc in dirtyMatSymbols)
                    await SaveMaterializedSymbolDoc(symDoc, owner);
                // Scratch layouts → per-doc offer dialog (same as AllDocs scope).
                foreach (var layDoc in dirtyScratchLayouts)
                    await SaveScratchLayout(layDoc, owner);
                // Materialized dirty layouts → write directly via VM.
                foreach (var layDoc in dirtyMatLayouts)
                    await SaveMaterializedLayoutDoc(layDoc, owner);
                // Orphaned dirty layout sessions (no open tab) → write directly.
                foreach (var sessionPath in dirtyOrphanedLayoutSessions)
                {
                    if (!_layoutRegistry.TryGet(sessionPath, out var sessionVm)) continue;
                    try
                    {
                        LayoutPersistence.SaveToFile(sessionPath, sessionVm!.Model);
                        NotifyLayoutSessionSaved(sessionPath);
                        Messages.Success("Saved", sessionPath);
                    }
                    catch (Exception ex)
                    {
                        Messages.Error($"Failed to save layout session '{sessionPath}': {ex.Message}");
                    }
                }
                // Dirty technology docs — always materialized, direct write via VM.
                foreach (var techDoc in dirtyTechDocs)
                    techDoc.ViewModel.SaveCommand.Execute(null);
                // Dirty data displays → save in place (materialized) or via picker (scratch).
                foreach (var dd in dirtyMatDisplays)
                    await SaveDataDisplayDoc(dd, owner);
                foreach (var dd in dirtyScratchDisplays)
                    await SaveDataDisplayDoc(dd, owner);
                return true;

            default: return false;
        }
    }

    /// <summary>
    /// Saves a single document (scratch → plan dialog, materialized → direct write).
    /// Returns true when the doc was saved, false when cancelled or failed.
    /// </summary>
    private async Task<bool> SaveSingleDocument(SchematicDocument doc, Window owner)
    {
        if (doc.IsScratch)
        {
            var builder  = new SavePlanBuilder(CurrentWorkspacePath, _lastWorkspaceParentDir,
                                               new[] { doc }.AsReadOnly());
            var plan     = builder.Build();
            var confirmed = await new Views.Dialogs.SavePlanDialog(plan, builder)
                .ShowDialog<SavePlan?>(owner);
            if (confirmed is null) return false;
            ExecuteSavePlan(confirmed);
            return true;
        }

        if (doc.FilePath is null) return false;
        try
        {
            SchematicPersistence.SaveToFile(doc.FilePath, doc.ViewModel.EditModel, doc.Id);
            doc.Materialize(doc.FilePath);
            NotifySessionSaved(doc.FilePath);
            Messages.Success("Saved", doc.FilePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to save '{doc.Id}': {ex.Message}");
            return false;
        }

        // Persist every dirty pushed-in sub-cell session in this doc's nav stack to its own .csch.
        // (Hierarchy edits live in the active frame's shared session, NOT doc.ViewModel.EditModel.)
        foreach (var (session, _) in doc.NavFrames)
        {
            if (ReferenceEquals(session, doc.ViewModel)) continue;   // base handled above
            if (!session.UndoRedo.IsModified) continue;              // clean frame — skip
            if (!_registry.TryGetPath(session, out var subPath) || subPath is null) continue;
            try
            {
                var subCellName = Path.GetFileNameWithoutExtension(subPath);
                SchematicPersistence.SaveToFile(subPath, session.EditModel, subCellName);
                NotifySessionSaved(subPath);
                Messages.Success("Saved", subPath);
            }
            catch (Exception ex)
            {
                Messages.Error($"Failed to save '{subPath}': {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>Called by WorkspaceWindow.OnClosing on a confirmed clean exit.</summary>
    public void OnCleanExit()
    {
        _autosaveTimer?.Stop();
        _cwsSaveTimer?.Stop();
        // Flush pending .cws config write synchronously before the process exits.
        if (CurrentWorkspacePath is not null)
        {
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
            // R-L5g-7: quitting is a close too — leave a clean workspace on disk.
            DeleteGeneratedCellsFolder(CurrentWorkspacePath);
        }
        _recovery.ClearSession();
    }

    // ── Autosave / recovery internals ─────────────────────────────────────────

    private void StartAutosaveTimer()
    {
        _autosaveTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _autosaveTimer.Tick += (_, _) => AutoSaveAll();
        _autosaveTimer.Start();
    }

    private void AutoSaveAll()
    {
        foreach (var doc in _scratchDocs.Where(d => d.IsDirty))
            _recovery.AutoSave(doc);
    }

    // Checks for prior-session recovery files and offers restore. Runs once at launch
    // (deferred to Background priority so the window is shown before the dialog appears).
    private async void CheckForRecovery()
    {
        var priorSessions = RecoveryManager.FindPriorSessions(_recovery.SessionDir);
        if (priorSessions.Count == 0) return;

        // Collect all recoverable docs across prior sessions.
        var allDocs = new List<(string Name, SchematicEditModel Model)>();
        foreach (var dir in priorSessions)
            allDocs.AddRange(RecoveryManager.LoadSession(dir));

        // Delete all prior session dirs whether or not we restore (v1: offer once, discard on decline).
        // We do this AFTER loading so we always have the content to restore.

        if (allDocs.Count == 0)
        {
            // Nothing recoverable — clean up stale empty dirs.
            foreach (var dir in priorSessions)
                RecoveryManager.DeletePriorSession(dir);
            return;
        }

        var window = ResolveOwner(null);
        if (window is null)
        {
            foreach (var dir in priorSessions) RecoveryManager.DeletePriorSession(dir);
            return;
        }

        int n = allDocs.Count;
        var dlg = new Views.Dialogs.SaveChangesDialog(
            $"circuitRF recovered {n} unsaved document{(n == 1 ? "" : "s")} " +
            "from a previous session. Restore?",
            saveLabel:     "Restore",
            dontSaveLabel: "Discard",
            cancelLabel:   "Later",
            title:         "Restore Documents");
        await dlg.ShowDialog(window);

        if (dlg.Result == SaveChangesResult.Save)
        {
            foreach (var (name, model) in allDocs)
            {
                var vm  = new SchematicViewModel(model, Messages);
                vm.SetPlacementService(PlacementService);
                vm.ComponentPlaced      += OnComponentPlaced;
                vm.WorkspaceRootProvider = () => CurrentWorkspaceRoot;
                var doc = new SchematicDocument(name, vm) { Messages = Messages, Hierarchy = this };
                _scratchDocs.Add(doc);
                _factory.OpenDocument(doc);
            }
            Messages.Success($"Restored {n} document{(n == 1 ? "" : "s")} from previous session.");
        }

        // Delete prior sessions regardless of result (offer once — v1).
        foreach (var dir in priorSessions)
            RecoveryManager.DeletePriorSession(dir);
    }

    // ---- Save Loose (tier 2 / tier 3) ----------------------------------------

    /// <summary>
    /// "Save Schematic As…" — saves the active (or first dirty) scratch doc as a
    /// loose .csch file without creating a cell structure:
    ///   Tier 2: workspace open → file picker → write .csch + register as Known File in .cws.
    ///   Tier 3: no workspace  → offer once to create workspace (plan dialog); on decline,
    ///                           write a plain .csch (no workspace, no Known-File registration).
    /// Disabled (greyed out) unless a schematic document is the active dockable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSchematicDocumentActive))]
    private async Task SaveLooseSchematic(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        // Prefer the active document (R-menu-4: per-window, not the shell's own ActiveDockable) if
        // it's a scratch doc; else first dirty scratch doc. Also handle already-materialized docs
        // (Save As to a new path).
        var doc = ResolveActiveDocumentForCommands() as SchematicDocument;
        if (doc is null || !doc.IsScratch)
        {
            var scratch = _scratchDocs.FirstOrDefault(d => d.IsDirty);
            if (scratch is not null)
                doc = scratch;
        }

        if (doc is null)
        {
            Messages.Info("No schematic to save.");
            return;
        }

        if (CurrentWorkspacePath is not null)
            await SaveLooseToWorkspace(doc, window);
        else
            await SaveLooseNoWorkspace(doc, window);
    }

    // Tier 2: save to a user-picked location + register as Known File in the open workspace.
    private async Task SaveLooseToWorkspace(SchematicDocument doc, Window owner)
    {
        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Schematic",
            SuggestedFileName = doc.Id,
            DefaultExtension  = "csch",
            FileTypeChoices   =
            [
                new FilePickerFileType("circuitRF Schematic") { Patterns = ["*.csch"] },
            ],
        });
        if (result is null) return;

        var filePath = result.Path.LocalPath;
        try
        {
            SchematicPersistence.SaveToFile(filePath, doc.ViewModel.EditModel, doc.Id);

            // Register as Known File in the workspace .cws (atomic write).
            CwsFile cws;
            try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath!); }
            catch { cws = new CwsFile(); } // corrupt .cws → start fresh (§6)

            if (!cws.KnownFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                cws.KnownFiles.Add(filePath);
            WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath!, cws);

            if (doc.IsScratch)
            {
                // Scratch → materialized transition.
                _scratchDocs.Remove(doc);
                _recovery.ClearDoc(doc);
                doc.Materialize(filePath);
            }
            else
            {
                // Materialized → Save As (new path).
                string? oldPath = doc.FilePath;
                if (oldPath is not null && oldPath != filePath)
                    _openDocsByPath.Remove(oldPath);
                doc.OnSavedAs(filePath, Path.GetFileNameWithoutExtension(filePath));
            }
            _openDocsByPath[filePath] = doc;
            RegisterSession(filePath, doc.ViewModel);
            NotifySessionSaved(filePath);

            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Saved", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Save failed: {ex.Message}");
        }
    }

    // No-workspace flow: offer workspace creation once; on decline, save as plain file.
    private async Task SaveLooseNoWorkspace(SchematicDocument doc, Window owner)
    {
        var offerDialog = new Views.Dialogs.SaveChangesDialog(
            "No workspace is open. Save to a workspace for better organization?",
            saveLabel:     "Create Workspace…",
            dontSaveLabel: "Save as File",
            cancelLabel:   "Cancel",
            title:         "Save Schematic");
        await offerDialog.ShowDialog(owner);

        switch (offerDialog.Result)
        {
            case SaveChangesResult.Save: // "Create Workspace…" → full plan dialog
                var dirtyScratch = _scratchDocs.Where(d => d.IsDirty).ToList();
                var builder      = new SavePlanBuilder(null, _lastWorkspaceParentDir,
                                                       dirtyScratch.AsReadOnly());
                var initialPlan  = builder.Build();
                var confirmed    = await new Views.Dialogs.SavePlanDialog(initialPlan, builder)
                    .ShowDialog<SavePlan?>(owner);
                if (confirmed is null) return; // plan dialog cancelled
                ExecuteSavePlan(confirmed);
                break;

            case SaveChangesResult.DontSave: // "Save as File" → tier 3 plain file
                await SaveLoosePlainFile(doc, owner);
                break;

            default: // Cancel
                return;
        }
    }

    // Tier 3: write the .csch to a user-picked location with no workspace association.
    private async Task SaveLoosePlainFile(SchematicDocument doc, Window owner)
    {
        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Schematic",
            SuggestedFileName = doc.Id,
            DefaultExtension  = "csch",
            FileTypeChoices   =
            [
                new FilePickerFileType("circuitRF Schematic") { Patterns = ["*.csch"] },
            ],
        });
        if (result is null) return;

        var filePath = result.Path.LocalPath;
        try
        {
            SchematicPersistence.SaveToFile(filePath, doc.ViewModel.EditModel, doc.Id);

            if (doc.IsScratch)
            {
                // Materialize (plain — no workspace registration, no Known-File entry).
                _scratchDocs.Remove(doc);
                _recovery.ClearDoc(doc);
                doc.Materialize(filePath);
            }
            else
            {
                // Materialized → Save As (new path, no workspace entry).
                string? oldPath = doc.FilePath;
                if (oldPath is not null && oldPath != filePath)
                    _openDocsByPath.Remove(oldPath);
                doc.OnSavedAs(filePath, Path.GetFileNameWithoutExtension(filePath));
            }
            _openDocsByPath[filePath] = doc;
            RegisterSession(filePath, doc.ViewModel);
            NotifySessionSaved(filePath);

            Messages.Success("Saved", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Save Layout As…" — saves the active layout document to a new, user-picked .clay path,
    /// mirroring <see cref="SaveLooseSchematic"/>'s own name/placement/intent (a loose file, no cell
    /// structure created) but reusing LAYOUT's own existing save primitives rather than
    /// <see cref="SavePlanBuilder"/>'s cell-creation wizard, which is schematic-specific and has no
    /// layout analogue. Scratch → <see cref="SaveScratchLayoutAsFile"/> (already exists, already used
    /// by the generic save/close flow); already-materialized → the VM's own <c>SaveLayoutAsCommand</c>
    /// (always re-prompts for a path) followed by <see cref="LayoutDocument.OnSavedAs"/>, mirroring
    /// <see cref="SaveLoosePlainFile"/>'s materialized branch exactly. Disabled (greyed out) unless a
    /// layout document is the active dockable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private async Task SaveLooseLayout(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc)
        {
            Messages.Info("No layout to save.");
            return;
        }

        if (doc.IsScratch)
        {
            await SaveScratchLayoutAsFile(doc, window);
            return;
        }

        var pathBefore = doc.ViewModel.CurrentLayoutPath;
        await doc.ViewModel.SaveLayoutAsCommand.ExecuteAsync(window);
        var pathAfter = doc.ViewModel.CurrentLayoutPath;
        if (pathAfter is null || pathAfter == pathBefore) return; // user cancelled the picker

        if (pathBefore is not null && pathBefore != pathAfter)
            _openDocsByPath.Remove(pathBefore);
        _openDocsByPath[pathAfter] = doc;
        doc.OnSavedAs(pathAfter, Path.GetFileNameWithoutExtension(pathAfter));
        RegisterLayoutSession(pathAfter, doc.ViewModel);

        // brief-foreign-documents.md §3 "Save As adopts": re-resolve technology against the NEW path
        // immediately — ResolveTechFor's ancestor-.cws walk now finds whatever workspace pathAfter
        // actually lives in (the current one, for the common "bring this into my project" gesture), but
        // Technology/ResolvedTechPath are snapshotted by ApplyTechResolution and won't refresh on their
        // own just because CurrentLayoutPath changed.
        doc.ViewModel.ApplyTechResolution(ResolveTechFor(doc.ViewModel.Model.TechRef, pathAfter));
        doc.RefreshForeignMarking(); // §4: IsForeign/SourceWorkspaceName may have changed with the path

        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Saved", pathAfter);
    }

    // ---- Save scratch symbol (Layer 4) ---------------------------------------

    /// <summary>
    /// Routes ⌘S for a single SymbolEditorDocument.
    /// Scratch → two-option offer dialog ("Save to Cell…" / "Save as File").
    /// Materialized → VM's existing SaveSymbolCommand (writes to CurrentSymbolPath).
    /// </summary>
    private async Task SaveSingleSymbolDocument(SymbolEditorDocument doc, Window window)
    {
        if (doc.IsScratch)
            await SaveScratchSymbol(doc, window);
        else
            await SaveMaterializedSymbolDoc(doc, window);
    }

    /// <summary>Saves an already-materialized symbol via its VM command and logs one "Saved" message.</summary>
    private async Task SaveMaterializedSymbolDoc(SymbolEditorDocument doc, Window owner)
    {
        var path = doc.ViewModel.CurrentSymbolPath;
        await doc.ViewModel.SaveSymbolCommand.ExecuteAsync(owner);
        if (path is not null && !doc.IsDirty)   // dirty cleared ⇒ save succeeded
            Messages.Success("Saved", path);
    }

    /// <summary>
    /// Saves a data display: writes in-place for materialized docs, or shows a .cdd picker
    /// for scratch docs (then materializes and tracks the result).
    /// Returns true on success, false when cancelled.
    /// </summary>
    private async Task<bool> SaveDataDisplayDoc(DataDisplayDocument dd, Window owner)
    {
        var window = dd.ViewModel.Window;
        if (dd.FilePath is { } path)
        {
            await window.SaveAllAsync(path);
            Messages.Success("Saved", path);
            return true;
        }

        var result = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title              = "Save Data Display",
            SuggestedFileName  = dd.Id,
            DefaultExtension   = "cdd",
            FileTypeChoices    = [new Avalonia.Platform.Storage.FilePickerFileType("circuitRF Data Display") { Patterns = ["*.cdd"] }],
        });
        if (result is null) return false;

        var picked = Path.GetFullPath(result.Path.LocalPath);
        await window.SaveAllAsync(picked);
        _scratchDataDisplays.Remove(dd);
        dd.Materialize(picked);
        _openDocsByPath[picked] = dd;
        Messages.Success("Saved", picked);
        return true;
    }

    /// <summary>
    /// Shows the two-option offer dialog for a scratch symbol and dispatches to the
    /// chosen path: "Save to Cell…" (cell + symbol/ subfolder) or "Save as File" (orphan .csym).
    /// </summary>
    private async Task SaveScratchSymbol(SymbolEditorDocument doc, Window window)
    {
        var offerDialog = new Views.Dialogs.SaveChangesDialog(
            "Save this symbol to a cell, or as a standalone file?",
            saveLabel:     "Save to Cell…",
            dontSaveLabel: "Save as File",
            cancelLabel:   "Cancel",
            title:         "Save Symbol");
        await offerDialog.ShowDialog(window);

        switch (offerDialog.Result)
        {
            case SaveChangesResult.Save:  // "Save to Cell…"
                if (CurrentWorkspacePath is not null)
                    await SaveScratchSymbolToCell(doc, window);
                else
                    await SaveScratchSymbolAsFile(doc, window);  // no workspace — fall through to file
                break;

            case SaveChangesResult.DontSave:  // "Save as File"
                await SaveScratchSymbolAsFile(doc, window);
                break;
        }
        // Cancel → no-op.
    }

    /// <summary>
    /// "Save to Cell…" branch: prompts for a cell name, creates the cell folder if needed,
    /// writes the .csym into cell/symbol/, and materializes the document.
    /// </summary>
    private async Task SaveScratchSymbolToCell(SymbolEditorDocument doc, Window window)
    {
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        var dialog   = new InputNameDialog("Save to Cell", "Cell name:");
        var cellName = await dialog.ShowDialog<string?>(window);
        if (cellName is null) return;

        var reason = NameValidator.Validate(cellName);
        if (reason is not null)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        var cellDir   = Path.Combine(workspaceDir, cellName);
        var symbolDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        var ext       = CellFolder.ViewExtension(ViewType.Symbol);
        var filePath  = Path.Combine(symbolDir, cellName + ext);

        if (File.Exists(filePath))
        {
            Messages.Error($"Symbol '{cellName}{ext}' already exists in cell '{cellName}'.");
            return;
        }

        try
        {
            // Create cell folder + symbol subfolder (idempotent if cell already exists).
            if (!Directory.Exists(cellDir))
                CellFolder.CreateCellFolder(workspaceDir, cellName);
            else if (!Directory.Exists(symbolDir))
                Directory.CreateDirectory(symbolDir);

            SymbolPersistence.SaveToFile(filePath, doc.ViewModel.EditableSymbol.ToSymbol());

            _scratchSymbols.Remove(doc);
            doc.Materialize(filePath);
            _openDocsByPath[filePath] = doc;

            OnSymbolSaved(filePath);
            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Saved", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to save symbol: {ex.Message}");
        }
    }

    /// <summary>
    /// "Save as File" branch: shows the file picker via the VM's existing SaveSymbolAsCommand,
    /// then materializes the document so it is no longer tracked as scratch.
    /// </summary>
    private async Task SaveScratchSymbolAsFile(SymbolEditorDocument doc, Window window)
    {
        var pathBefore = doc.ViewModel.CurrentSymbolPath;
        await doc.ViewModel.SaveSymbolAsCommand.ExecuteAsync(window);
        var pathAfter = doc.ViewModel.CurrentSymbolPath;

        if (pathAfter is null || pathAfter == pathBefore) return;  // user cancelled the picker

        // PerformSave already set vm.CurrentSymbolPath + vm.IsDirty=false + fired SymbolSaved.
        // Complete the scratch → materialized transition on the document.
        _scratchSymbols.Remove(doc);
        doc.Materialize(pathAfter);
        _openDocsByPath[pathAfter] = doc;

        Messages.Success("Saved", pathAfter);
    }

    /// <summary>
    /// "Save Symbol As…" — saves the active symbol document to a new, user-picked .csym path,
    /// mirroring <see cref="SaveLooseLayout"/>'s own shape exactly: scratch →
    /// <see cref="SaveScratchSymbolAsFile"/> (already exists, already used by the generic save/close
    /// flow); already-materialized → the VM's own <c>SaveSymbolAsCommand</c> (always re-prompts for a
    /// path) followed by <see cref="SymbolEditorDocument.OnSavedAs"/>. Disabled (greyed out) unless a
    /// symbol document is the active dockable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSymbolDocumentActive))]
    private async Task SaveLooseSymbol(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (ResolveActiveDocumentForCommands() is not SymbolEditorDocument doc)
        {
            Messages.Info("No symbol to save.");
            return;
        }

        if (doc.IsScratch)
        {
            await SaveScratchSymbolAsFile(doc, window);
            return;
        }

        var pathBefore = doc.ViewModel.CurrentSymbolPath;
        await doc.ViewModel.SaveSymbolAsCommand.ExecuteAsync(window);
        var pathAfter = doc.ViewModel.CurrentSymbolPath;
        if (pathAfter is null || pathAfter == pathBefore) return; // user cancelled the picker

        if (pathBefore is not null && pathBefore != pathAfter)
            _openDocsByPath.Remove(pathBefore);
        _openDocsByPath[pathAfter] = doc;
        doc.OnSavedAs(pathAfter, Path.GetFileNameWithoutExtension(pathAfter));

        OnSymbolSaved(pathAfter);
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Saved", pathAfter);
    }

    // ---- Save scratch layout --------------------------------------------------

    /// <summary>
    /// Routes ⌘S for a single LayoutDocument.
    /// Scratch → two-option offer dialog ("Save to Cell…" / "Save as File").
    /// Materialized → VM's existing SaveLayoutCommand (writes to CurrentLayoutPath).
    /// </summary>
    private async Task SaveSingleLayoutDocument(LayoutDocument doc, Window window)
    {
        if (doc.IsScratch)
            await SaveScratchLayout(doc, window);
        else
            await SaveMaterializedLayoutDoc(doc, window);
    }

    /// <summary>
    /// Saves an already-materialized layout via its VM command and logs one "Saved" message, then —
    /// mirroring the schematic's own <c>HierarchySaveTests</c> behaviour exactly (brief-L3b-hierarchy-
    /// navigation.md §3) — persists every OTHER dirty pushed-in sub-cell session in this doc's nav
    /// stack to its own <c>.clay</c>. Saving while pushed in therefore writes the sub-cell's file; the
    /// base is written too (unconditionally, via the VM's own save command, same as always), but if
    /// the base itself wasn't dirty its content is unchanged, so "the parent is unmodified on disk"
    /// holds in every practical sense (identical bytes rewritten).
    /// </summary>
    private async Task SaveMaterializedLayoutDoc(LayoutDocument doc, Window owner)
    {
        var path = doc.ViewModel.CurrentLayoutPath;
        await doc.ViewModel.SaveLayoutCommand.ExecuteAsync(owner);
        if (path is not null && !doc.ViewModel.IsDirty)   // base's own dirty cleared ⇒ base save succeeded
        {
            NotifyLayoutSessionSaved(path);
            Messages.Success("Saved", path);
        }

        foreach (var (session, _) in doc.NavFrames)
        {
            if (ReferenceEquals(session, doc.ViewModel)) continue;   // base handled above
            if (!session.IsDirty) continue;                          // clean frame — skip
            if (!_layoutRegistry.TryGetPath(session, out var subPath) || subPath is null) continue;
            try
            {
                LayoutPersistence.SaveToFile(subPath, session.Model);
                session.MarkSaved();
                NotifyLayoutSessionSaved(subPath);
                Messages.Success("Saved", subPath);
            }
            catch (Exception ex)
            {
                Messages.Error($"Failed to save '{subPath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shows the two-option offer dialog for a scratch layout and dispatches to the
    /// chosen path: "Save to Cell…" (cell + layout/ subfolder) or "Save as File" (orphan .clay).
    /// </summary>
    private async Task SaveScratchLayout(LayoutDocument doc, Window window)
    {
        var offerDialog = new Views.Dialogs.SaveChangesDialog(
            "Save this layout to a cell, or as a standalone file?",
            saveLabel:     "Save to Cell…",
            dontSaveLabel: "Save as File",
            cancelLabel:   "Cancel",
            title:         "Save Layout");
        await offerDialog.ShowDialog(window);

        switch (offerDialog.Result)
        {
            case SaveChangesResult.Save:  // "Save to Cell…"
                if (CurrentWorkspacePath is not null)
                    await SaveScratchLayoutToCell(doc, window);
                else
                    await SaveScratchLayoutAsFile(doc, window);  // no workspace — fall through to file
                break;

            case SaveChangesResult.DontSave:  // "Save as File"
                await SaveScratchLayoutAsFile(doc, window);
                break;
        }
        // Cancel → no-op.
    }

    /// <summary>
    /// "Save to Cell…" branch: prompts for a cell name, creates the cell folder if needed,
    /// writes the .clay into cell/layout/, and materializes the document.
    /// </summary>
    private async Task SaveScratchLayoutToCell(LayoutDocument doc, Window window)
    {
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        var dialog   = new InputNameDialog("Save to Cell", "Cell name:");
        var cellName = await dialog.ShowDialog<string?>(window);
        if (cellName is null) return;

        var reason = NameValidator.Validate(cellName);
        if (reason is not null)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        var cellDir   = Path.Combine(workspaceDir, cellName);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var ext       = CellFolder.ViewExtension(ViewType.Layout);
        var filePath  = Path.Combine(layoutDir, cellName + ext);

        if (File.Exists(filePath))
        {
            Messages.Error($"Layout '{cellName}{ext}' already exists in cell '{cellName}'.");
            return;
        }

        try
        {
            // Create cell folder + layout subfolder (idempotent if cell already exists).
            if (!Directory.Exists(cellDir))
                CellFolder.CreateCellFolder(workspaceDir, cellName);
            else if (!Directory.Exists(layoutDir))
                Directory.CreateDirectory(layoutDir);

            LayoutPersistence.SaveToFile(filePath, doc.ViewModel.Model);

            _scratchLayouts.Remove(doc);
            doc.Materialize(filePath);
            _openDocsByPath[filePath] = doc;
            RegisterLayoutSession(filePath, doc.ViewModel);   // now push-in-able from elsewhere
            doc.RefreshForeignMarking(); // §4: now workspace-bound (saved into the current workspace)

            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Saved", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to save layout: {ex.Message}");
        }
    }

    /// <summary>
    /// "Save as File" branch: shows the file picker via the VM's existing SaveLayoutAsCommand,
    /// then materializes the document so it is no longer tracked as scratch.
    /// </summary>
    private async Task SaveScratchLayoutAsFile(LayoutDocument doc, Window window)
    {
        var pathBefore = doc.ViewModel.CurrentLayoutPath;
        await doc.ViewModel.SaveLayoutAsCommand.ExecuteAsync(window);
        var pathAfter = doc.ViewModel.CurrentLayoutPath;

        if (pathAfter is null || pathAfter == pathBefore) return;  // user cancelled the picker

        // PerformSave already set vm.CurrentLayoutPath + vm.IsDirty=false + fired LayoutSaved.
        // Complete the scratch → materialized transition on the document.
        _scratchLayouts.Remove(doc);
        doc.Materialize(pathAfter);
        _openDocsByPath[pathAfter] = doc;
        RegisterLayoutSession(pathAfter, doc.ViewModel);   // now push-in-able from elsewhere

        // brief-foreign-documents.md R-fgn-3/§2.1: a scratch layout resolved technology from whichever
        // workspace was CURRENTLY open (FallbackWorkspaceTechDir); once materialized to a real path,
        // re-resolve against THAT path's own ancestor workspace — it may differ (saved outside any
        // workspace, or into a different one via Save As on a foreign document elsewhere) and the R-fgn-4
        // prompt (if genuinely parent-less) is only ever reached through a live ResolveTechFor call.
        doc.ViewModel.ApplyTechResolution(ResolveTechFor(doc.ViewModel.Model.TechRef, pathAfter));
        doc.RefreshForeignMarking(); // §4: IsForeign/SourceWorkspaceName may have changed with the path

        Messages.Success("Saved", pathAfter);
    }

    // ---- Quit ----------------------------------------------------------------

    [RelayCommand]
    private void QuitApplication()
        => (App.Current as App)?.Quit();

    // ---- Test messages command (Help → Post Test Messages) ------------------

    /// <summary>Open the bundled User Documentation in the default browser (Help menu).</summary>
    [RelayCommand]
    private void OpenDocumentation() => DocLauncher.Open();

    [RelayCommand]
    private void PostTestMessages()
    {
        Messages.Info("Info: simulation started for TestBench PA_TestBench.");
        Messages.Success("Success: simulation converged in 12 Newton iterations.");
        Messages.Warning("Warning: node n_drain approaches supply rail — check bias.");
        // Demonstrate clickable file link (path to a real file in the project).
        var netlistPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "testdata", "Hero2", "hero2.cnl");
        netlistPath = Path.GetFullPath(netlistPath);
        Messages.Error($"Error: netlist parse failed.", File.Exists(netlistPath) ? netlistPath : null);
    }
}
