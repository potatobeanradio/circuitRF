using CircuitRF.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using Avalonia.Controls;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;
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
using RfCore.Loadpull;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.TechImport;
using CircuitRF.Ui.Messages;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Archive;
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
    private readonly List<HarmonicaDocument>    _scratchHarmonicas   = [];
    private readonly List<LayoutDocument>       _scratchLayouts      = [];

    // ---- Technology cache (L0c) -----------------------------------------------

    // Owned for the lifetime of a workspace; replaced (not just cleared) on every
    // NewWorkspace/SwitchToWorkspace/ResetToBlankShell so a fresh subscription always matches
    // the current instance — see ResetTechCache.
    private TechnologyCache _techCache = new();

    /// <summary>
    /// One-load-per-file cache for `.wasm` assembly rule files, reset alongside
    /// <see cref="_techCache"/> for the same reason — a rule file resolved for the previous workspace
    /// must not survive into the next one.
    /// </summary>
    private WasmCache _wasmCache = new();

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
    private IEditHistoryDocument? _activeUndoTarget;

    // Last schematic document made active — kept so the Analyses panel + Run button survive focusing a
    // data display / symbol / cell tab. Cleared when this doc is closed or the workspace changes.
    private SchematicDocument? _lastActiveSchematicDoc;

    // True while the Analyses panel is the focused dockable. A tool panel is not a document, so it
    // never becomes DocumentDock.ActiveDockable — which is what Run/Stop are otherwise gated on —
    // and clicking into the panel used to grey out ⌘R while the panel's OWN Run button beside it
    // stayed live. See CanRunAnalysis.
    private bool _analysesPanelFocused;

    // R-h9a-3: the harmonicaRF document currently holding the docked macOS menu-bar takeover (null
    // when none does). Tracked so a focus change or workspace reset can tell the OLD holder to give
    // the menu bar back before anything else happens. Cleared alongside _lastActiveSchematicDoc at
    // every workspace-lifecycle reset point and in OnDockableClosed.
    private HarmonicaDocument? _harmonicaDockedFocusDoc;

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

    // ── The document pane the user is actually working in (side-by-side splits) ──────────────────
    //
    // Owner report, 2026-08-29: with a .clay and a .csch docked SIDE BY SIDE, ⌘S saved the layout and
    // never the schematic. `_factory.DocumentDock` is the PRIMARY pane and only the primary — a split
    // document area is several IDocumentDocks (CircuitRfDockFactory.BuildDocumentArea builds one per
    // restored region, and Dock's own drag/drop makes more at runtime), and nothing ever re-pointed
    // that field or subscribed to the other panes. So "the active document" was pinned to whatever
    // pane 0 happened to show, and every command that resolves through here — Save, Close Window, Run
    // Analysis, Generate Netlist, Check Design Rules, the exports, the undo target — targeted it.
    //
    // Tracked as the DOCK rather than the document so the answer stays right when the user switches
    // tabs inside that pane without touching the other one. Cleared whenever it is no longer part of
    // the shell's own layout (a pane collapses when its last document closes), which falls back to the
    // primary — never to a dangling dock that is no longer on screen.
    //
    // Typed IDock, not IDocumentDock, and that is not defensive: a pane the user makes by DRAGGING a
    // document to an edge is a plain ProportionalDock holding the document, because
    // FactoryBase.CreateSplitLayout wraps a non-IDock dockable in CreateProportionalDock(). Only a
    // pane rebuilt from a saved .cws is a real DocumentDock. DockLayoutCapture.BuildRegion already
    // documents this trap — it shipped broken once for exactly this reason — so a pane is identified
    // here the same way it is there: by what it HOLDS, not by its type.
    private IDock? _activeDocumentPane;

    // Document docks currently subscribed to OnDocumentDockPropertyChanged. A HashSet because a
    // re-scan runs after every layout rebuild and every dock/undock, and re-subscribing a dock twice
    // would run the whole activation fan-out twice per tab change.
    private readonly HashSet<System.ComponentModel.INotifyPropertyChanged> _subscribedDocumentDocks = [];

    private IDockable? ResolveActiveDocumentForCommands()
        => _focusedWindowDocument
        ?? ActiveDocumentPaneInShell?.ActiveDockable
        ?? _factory.DocumentDock?.ActiveDockable;

    /// <summary>
    /// The tracked pane, but only while it is still part of the shell's own layout — a pane that
    /// collapsed (Dock removes an <c>IsCollapsable</c> document dock when its last tab closes) would
    /// otherwise go on naming a document nothing can show.
    /// </summary>
    private IDock? ActiveDocumentPaneInShell
        => _activeDocumentPane is { } pane && EnumerateDocumentPanes().Contains(pane) ? pane : null;

    /// <summary>
    /// Every document pane in the shell's own layout — every dock that DIRECTLY holds a document,
    /// which is the same rule <c>DockLayoutCapture.BuildRegion</c> uses to find the panes it writes to
    /// the .cws, so "which panes exist" gets one answer in both places. Follows <c>VisibleDockables</c>
    /// only, never a root's <c>Windows</c>, so a torn-off document's own root is out of scope — those
    /// are resolved by <see cref="_focusedWindowDocument"/> instead.
    /// </summary>
    private IEnumerable<IDock> EnumerateDocumentPanes()
        => Layout is { } root ? Docking.DockLayoutCapture.EnumerateDocumentPanes(root) : [];

    /// <summary>
    /// Subscribes <see cref="OnDocumentDockPropertyChanged"/> to EVERY document pane in the shell,
    /// not just <c>_factory.DocumentDock</c>. Idempotent, and safe to call after any layout change:
    /// panes that have gone are dropped, panes that are new are added.
    /// </summary>
    private void SubscribeToDocumentPanes()
    {
        var live = EnumerateDocumentPanes()
            .OfType<System.ComponentModel.INotifyPropertyChanged>()
            .ToHashSet();

        foreach (var gone in _subscribedDocumentDocks.Where(d => !live.Contains(d)).ToList())
        {
            gone.PropertyChanged -= OnDocumentDockPropertyChanged;
            _subscribedDocumentDocks.Remove(gone);
        }

        foreach (var pane in live.Where(_subscribedDocumentDocks.Add))
            pane.PropertyChanged += OnDocumentDockPropertyChanged;
    }

    /// <summary>
    /// Records which pane a document lives in, for the split-document-area resolution above. Called
    /// from every path that makes a document current WITHOUT changing any dock's
    /// <c>ActiveDockable</c> — clicking back into the canvas of a tab that was already active in its
    /// own pane is the everyday one, and it is exactly the case a side-by-side layout produces.
    /// </summary>
    private void MarkActiveDocumentPane(IDockable document)
    {
        if (document.Owner is IDock pane) _activeDocumentPane = pane;
    }

    // Through the DOCUMENT, not its command stack: with two histories in play the label has to describe
    // the entry Undo would really take, or it names a shape move while Ctrl+Z undoes a wire drag.
    public string UndoDescription => _activeUndoTarget?.UndoLastDescription ?? "Undo";
    public string RedoDescription => _activeUndoTarget?.RedoLastDescription ?? "Redo";

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

        // Driven from the property change rather than from each workspace-reset path, because the
        // resolver must see the NEW root: a hand-placed call ordered before this assignment would
        // point the new workspace's generators at the old workspace's folder, and the failure — a
        // cell resolving to the wrong kit's script — is silent.
        ResetPCellGenerators(dir);

        NewCellInWorkspaceCommand.NotifyCanExecuteChanged();
        NewFolderInWorkspaceCommand.NotifyCanExecuteChanged();
        ExportDataCommand.NotifyCanExecuteChanged();
        CloseWorkspaceCommand.NotifyCanExecuteChanged();
        ArchiveWorkspaceCommand.NotifyCanExecuteChanged();
        ImportGdsiiLibraryCommand.NotifyCanExecuteChanged();
        ImportDxfLibraryCommand.NotifyCanExecuteChanged();
        ImportBoardCommand.NotifyCanExecuteChanged();
        ManagePdksCommand.NotifyCanExecuteChanged();

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
        RestoreInstalledPdks();
        RebuildRecentMenuItems();

        // Wire tree-item actions before any workspace is loaded so actions are available
        // the moment SetWorkspace builds the first VM tree. RefreshRecent() now sees the
        // populated recent list seeded above.
        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        // Notify PropertiesTool when the active document tab changes (active schematic tracking).
        // EVERY document pane, not just the primary — see _activeDocumentPane for what subscribing
        // only _factory.DocumentDock cost in a side-by-side split.
        SubscribeToDocumentPanes();
        WireAnalysesRun();

        // Wire close-tab prompt: before a dockable is removed, show Save/Don't Save/Cancel
        // for dirty/scratch documents. FactoryBase.DockableClosed fires from base.CloseDockable
        // and cleans up _scratchDocs/_openDocsByPath.
        _factory.CloseDockableConfirm = ConfirmCloseDockable;
        WireDockArrangementPersistence();
        _factory.DockableClosed += (_, args) => { if (args.Dockable is not null) OnDockableClosed(args.Dockable); };

        // Owner report, 2026-08-14: "Document tabs did not update/render when a document was
        // docked." Docking (e.g. dragging a torn-off document's tab back into a tab strip) moves a
        // dockable into a fresh position in the visual tree via Dock's own drag/drop machinery,
        // which does not always land in the same layout pass a normal binding update would —
        // exactly the class of bug the DockDocumentControlCachedContentTemplate fix already
        // addresses for a document's BODY (see src/Ui/CLAUDE.md's Dock GOTCHA), but that fix is
        // scoped to content presentation and does not reach the tab STRIP itself. Nudging every
        // open window's layout after the dock completes is what incidentally already "fixed" this
        // for anyone who happened to toggle a panel or resize afterward; doing it here makes that
        // nudge automatic instead of something the user has to trigger by hand.
        _factory.DockableDocked += (_, _) =>
        {
            // A document dropped BESIDE another creates a document pane Dock builds itself, which no
            // earlier scan can have seen. Without this, splitting the document area at runtime leaves
            // the new pane unsubscribed and its documents unreachable by every active-document command.
            SubscribeToDocumentPanes();

            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime desktop) return;
            foreach (var window in desktop.Windows)
            {
                window.InvalidateMeasure();
                window.InvalidateArrange();
                window.InvalidateVisual();
            }
        };

        // Dock's own focus signal, which is the ONE notification that fires for every document type
        // when the user clicks into a pane whose active tab did not change — DocumentControl adds a
        // TUNNEL PointerPressed handler that calls SetFocusedDockable for whatever its dock has
        // active. A side-by-side split needs exactly that, and it is why UNDO went to the wrong
        // document: a pane made by dragging a document to an edge holds one document, so its
        // ActiveDockable never changes and OnDocumentDockPropertyChanged — the only thing that called
        // SetActiveUndoTarget for the shell — never fired when the user moved between the panes.
        // Clicking the TAB of a single-document pane is the same story, which is why the editors'
        // own CanvasInteracted hooks were not enough on their own.
        //
        // Routed through the SAME ActivateDocument the tab change uses, so a pane switch and a tab
        // switch leave the shell in identical states rather than in two nearly-identical ones.
        //
        // Guarded on the pane AND the document: SetFocusedDockable runs inside SetActiveDockable,
        // which the CanvasInteracted handlers call, so an unguarded fan-out here would re-enter.
        _factory.FocusedDockableChanged += (_, args) =>
        {
            // A TOOL panel is not a document, however much Dock's type hierarchy says otherwise.
            //
            // Dock.Model.Mvvm's `Tool` declares `IDocument` (verified by decompiling
            // Dock.Model.Mvvm 12.0.0.2, not assumed), so the IDocument test below passes for the
            // Properties, Project and Library panels, and a ToolDock satisfies `is IDock` as well —
            // neither half of the old guard excluded anything. Clicking anywhere inside the
            // Properties inspector therefore ran ActivateDocument on the Properties panel ITSELF,
            // fell through to its no-document-type branch, and called SetActiveSchematic(null):
            // the cell properties the user was looking at vanished mid-click and were replaced by
            // "Select object to inspect its properties" (owner, 2026-08-29).
            //
            // The same thing racing a tree click is why the inspector only SOMETIMES showed a cell's
            // properties, and it is what put a hidden ComboBox under a live pointer press — see
            // Controls/HiddenComboBoxInputGuard for where that ended up.
            // ⌘R follows the Analyses panel. The panel is a tool, so it is never the DocumentDock's
            // ActiveDockable and the early return below is where its focus would otherwise be lost.
            SetAnalysesPanelFocused(ReferenceEquals(args.Dockable, _factory.AnalysesTool));

            if (args.Dockable is ITool) return;

            if (args.Dockable is not IDocument document || document.Owner is not IDock pane) return;
            if (ReferenceEquals(pane, _activeDocumentPane)
                && ReferenceEquals(document, _lastActivatedDocument)) return;

            _activeDocumentPane = pane;
            ActivateDocument(document, requestActivationFocus: false);
        };

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

        LoadExternalDeviceProviders();

        // L3b — CellLayoutResolver is a static, process-lifetime class (unlike the per-workspace
        // _techCache), so this subscribes exactly once, ever; never re-subscribed per workspace reset.
        CellLayoutResolver.LiveViewChanged += OnCellLayoutLiveViewChanged;

        // Same shape and the same reason: a process-lifetime static, subscribed once.
        ProcessDeviceWorkerTransport.Starting += OnDeviceWorkerStarting;
        ProcessDeviceWorkerTransport.Logged   += OnDeviceWorkerLogged;

        Messages.Info("circuitRF ready.");
    }

    /// <summary>
    /// Says that a worker is being started, once per worker.
    ///
    /// <para><b>The gap this closes.</b> Starting a worker is the one step in evaluating an external
    /// model that a user waits on and cannot see — the model library is loaded and its device types
    /// read, and on a Mac that happens inside a virtual machine which has to boot first. Until it
    /// finishes, a run that is proceeding normally looks exactly like one that has hung, and the
    /// first thing printed after it is whatever the run says NEXT, which is usually a result or a
    /// failure and never mentions the worker.</para>
    ///
    /// <para><b>Once, and only once.</b> The event is raised where the process is actually created,
    /// and the registry keeps what it resolved — so every device after the first uses the worker
    /// already running and nothing further is said. A worker genuinely started a second time (a
    /// different kit, or the same one after the workspace changed) is a second thing happening and
    /// is reported as one.</para>
    ///
    /// <para>Posted through the dispatcher: this arrives on whichever thread the run is on.</para>
    /// </summary>
    private void OnDeviceWorkerStarting(DeviceWorkerStart start)
    {
        // A worker started only to be asked what it implements says nothing. It is not a run waiting
        // on anything — it is started, described and shut down — and there is one per compiled model
        // in the kit, so this line appeared several times during a workspace OPEN, each time
        // promising that a run was about to wait on it. What the scan FOUND is already reported once,
        // by the install that asked for it.
        if (start.ForDiscovery) return;

        string forWhat = string.IsNullOrWhiteSpace(start.Provider)
            ? "an external device model"
            : $"'{start.Provider}'";

        string text = $"Starting the worker that evaluates {forWhat} " +
                      $"({Path.GetFileName(start.Command)}). The first device waits for it to load " +
                      "its models; the rest of the run does not.";

        if (Dispatcher.UIThread.CheckAccess()) Messages.Info(text);
        else Dispatcher.UIThread.Post(() => Messages.Info(text));
    }

    /// <summary>
    /// Passes a worker's own log through to the run log, when it has been asked for
    /// (<c>CRF_WORKER_LOG</c> in the environment).
    ///
    /// <para><b>Why this is worth a switch.</b> A worker MEASURES things nobody declares — which of
    /// a model's nodes are free unknowns, which pins carry a temperature, whether the model's own
    /// Jacobian agrees with its own currents — and those measurements decide how the device is
    /// stamped. A measurement that comes out differently on two machines produces no error on
    /// either: the device stamps cleanly, every number stays finite, and the only symptom is that
    /// one of them will not converge. The worker says exactly what it found, and until now nobody
    /// could read it unless something threw.</para>
    ///
    /// <para>Off by default: it is per-line, and a worker under a failing solve has a lot to say.</para>
    /// </summary>
    private void OnDeviceWorkerLogged(DeviceWorkerLogLine log)
    {
        string who  = string.IsNullOrWhiteSpace(log.Provider) ? "worker" : $"worker '{log.Provider}'";
        string text = $"{who}: {log.Line}";

        if (Dispatcher.UIThread.CheckAccess()) Messages.Info(text);
        else Dispatcher.UIThread.Post(() => Messages.Info(text));
    }

    // ---- Technology resolution (L0c) ------------------------------------------

    /// <summary>
    /// Replaces the technology cache with a fresh instance and (re)subscribes the live-refresh
    /// handler. Called once from the constructor and again from every workspace-lifetime reset
    /// (NewWorkspace / SwitchToWorkspace / ResetToBlankShell) so stale cached entries from the
    /// previous workspace can never leak into the new one.
    /// </summary>
    /// <summary>
    /// The workspace's own PCell generator resolver, or null when no workspace is open. Disposed and
    /// replaced on every workspace-lifetime reset — see <see cref="ResetPCellGenerators"/>.
    /// </summary>
    private CircuitRF.Ui.Layout.PCells.Wire.PCellWorkerResolver? _pcellResolver;

    /// <summary>This installation's record of which kits' scripts may run. Rebuilt with the resolver so
    /// a decision made in one workspace is visible in the next without a restart.</summary>
    private CircuitRF.Ui.Layout.PCells.Wire.PCellTrustStore? _pcellTrust;

    /// <summary>
    /// Points the PCell registry at <paramref name="workspaceRootDir"/>'s generator scripts, after
    /// ending whatever the previous workspace had running.
    ///
    /// <para><b>Disposing the resolver is what actually ends the interpreters</b> — clearing the
    /// registry only drops circuitRF's references to them. Getting that backwards leaves a Python
    /// process per kit running with nothing to talk to, which is a leak the user cannot see and
    /// cannot clean up.</para>
    ///
    /// <para>A RESOLVER, not a provider: opening a workspace starts no interpreter. One is started
    /// the first time a design actually places a cell that kit generates.</para>
    /// </summary>
    private void ResetPCellGenerators(string? workspaceRootDir)
    {
        CircuitRF.Ui.Layout.PCells.PCellRegistry.ClearResolvers();

        var previous = _pcellResolver;
        _pcellResolver = null;
        try { previous?.Dispose(); } catch { /* teardown must not fail a workspace switch */ }

        _pcellTrust = null;
        KitLayoutGenerators.SetRefresher(null);
        if (string.IsNullOrWhiteSpace(workspaceRootDir)) { ReloadPCellGeneratorsCommand.NotifyCanExecuteChanged(); return; }

        try
        {
            // B6: a kit's scripts run only with this installation's explicit permission. The gate is
            // handed to the resolver, not applied here, so the refusal happens at the one point that
            // would otherwise launch an interpreter — including on paths that never went near a prompt.
            var trust = CircuitRF.Ui.Layout.PCells.Wire.PCellTrustStore.UserLocal();
            _pcellTrust = trust;

            var resolver = new CircuitRF.Ui.Layout.PCells.Wire.PCellWorkerResolver(
                workspaceRootDir!, findInterpreter: null, report: m => Messages.Warning(m),
                trust: trust.Decide);

            // Replayed, not re-derived — see CwsFile.PythonInterpreter's own note. Read straight off
            // disk rather than from a cached CwsFile, because this runs on the workspace-path change
            // and nothing has necessarily loaded one yet.
            resolver.Recorded = ReadRecordedPythonInterpreter(workspaceRootDir!);
            resolver.InterpreterChosen += RecordPythonInterpreter;

            _pcellResolver = resolver;
            CircuitRF.Ui.Layout.PCells.PCellRegistry.AddResolver(resolver);

            // Asked from the MANIFEST SCAN, which reads JSON and starts nothing — so the question is
            // put up front, in a calm moment, without costing the laziness B3 deliberately built.
            var pending = resolver.Kits
                .Where(k => trust.Decide(k.Directory) == CircuitRF.Ui.Layout.PCells.Wire.PCellTrustDecision.Unknown)
                .ToList();
            if (pending.Count > 0) RequestPCellConsent(pending);

            // Asked when a part is resolved against a map the background reading has not filled yet.
            // See KitLayoutGenerators.SetRefresher for why a lookup is allowed to trigger a reading.
            KitLayoutGenerators.SetRefresher(RefreshPCellGeneratorsNow);

            RefreshPCellPaletteItems();
        }
        catch (Exception ex)
        {
            // A kit's generated artwork failing to become available must never stop a workspace
            // opening — the user's design is their data, and every such cell still draws as the
            // existing Not Found placeholder.
            Messages.Warning($"PCell generators could not be made available: {ex.Message}");
        }

        ReloadPCellGeneratorsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Puts B6's consent question, once per kit per installation, and applies the answer.
    ///
    /// <para><b>Deferred off the workspace-path change, not shown from inside it.</b> This runs from a
    /// property-changed handler in the middle of opening a workspace; a modal dialog there would
    /// re-enter the open. Posting at Background priority lets the open finish first, and the workspace
    /// is re-checked when the prompt finally runs because the user may have switched away meanwhile —
    /// answering a question about a workspace that is no longer open would record the wrong thing.</para>
    /// </summary>
    private void RequestPCellConsent(IReadOnlyList<CircuitRF.Ui.Layout.PCells.Wire.PCellKit> pending)
    {
        string? askedFor = CurrentWorkspacePath;

        Dispatcher.UIThread.Post(async () =>
        {
            if (!string.Equals(CurrentWorkspacePath, askedFor, StringComparison.Ordinal)) return;
            if (_pcellTrust is not { } trust || _pcellResolver is not { } resolver) return;

            bool? allowed;
            try { allowed = await Views.Dialogs.PCellTrustDialog.ShowAsync(ResolveOwner(null), pending); }
            catch (Exception ex)
            {
                // A prompt that could not be shown records NOTHING — never a refusal, and certainly
                // never permission. The kit's cells draw as placeholders and the question stands.
                Messages.Warning($"Permission for generated artwork could not be requested: {ex.Message}");
                return;
            }

            if (allowed is null) return; // dismissed without answering — ask again next time

            foreach (var kit in pending) trust.Record(kit.Directory, allowed.Value);

            if (!allowed.Value)
            {
                Messages.Info($"Generated artwork from {Plural(pending.Count, "kit")} will draw as " +
                              "placeholders. Settings ▸ General ▸ Generated Artwork asks again.");
                return;
            }

            // The resolver already concluded that these kits could not run; that conclusion is cached,
            // as are any generators resolved through it. Both have to go before the cells can appear.
            resolver.StopProviders();
            CircuitRF.Ui.Layout.PCells.PCellRegistry.InvalidateResolved();

            if (CurrentWorkspacePath is { } cws)
            {
                try { RegenerateAllGeneratedCells(cws); }
                catch (Exception ex)
                {
                    Messages.Warning($"Generated cells could not be rebuilt after granting permission: {ex.Message}");
                }
            }

            RefreshAllOpenLayoutTech();
            // The kits could not be listed while they were untrusted, so the palette and the
            // part-to-cell map were both built from nothing. Read them again now they may run.
            RefreshPCellPaletteItems();
            Messages.Success($"Generated artwork from {Plural(pending.Count, "kit")} is allowed to run on this machine.");
        }, DispatcherPriority.Background);
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private bool CanReloadPCellGenerators() => _pcellResolver is not null;

    /// <summary>
    /// B7's authoring loop: edit a generator script, press this, see the artwork change — without
    /// closing the workspace.
    ///
    /// <para><b>Four things are stale after a script edit, and all four have to go.</b> The running
    /// interpreter (it loaded the old code), the manifest scan (the kit may declare different files
    /// now), the per-kit CONTENT HASH (cached once per session — leave it and the edit resolves to the
    /// cell the previous version wrote, so the edit appears to do nothing), and the generator delegates
    /// the registry handed out. <see cref="PCellWorkerResolver.Rescan"/> covers the first three;
    /// <c>PCellRegistry.InvalidateResolved</c> the fourth.</para>
    ///
    /// <para>Repointing is NOT undoable, and that is deliberate: this is a cache refresh, like the
    /// live technology reload, not an edit the user made. The affected documents are marked dirty so
    /// the new references are saved.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReloadPCellGenerators))]
    private void ReloadPCellGenerators()
    {
        if (_pcellResolver is not { } resolver || CurrentWorkspacePath is not { } cwsPath) return;
        if (_pcellTrust is not { } trust) return;

        resolver.Rescan();
        CircuitRF.Ui.Layout.PCells.PCellRegistry.InvalidateResolved();

        // A kit added since the workspace opened has never been asked about, and Unknown does not run.
        var pending = resolver.Kits
            .Where(k => trust.Decide(k.Directory) == CircuitRF.Ui.Layout.PCells.Wire.PCellTrustDecision.Unknown)
            .ToList();
        if (pending.Count > 0) RequestPCellConsent(pending);

        // Open documents are repointed in memory; rewriting their files underneath them would fight
        // whatever is unsaved. Everything else is repointed on disk.
        var openLayouts = _scratchLayouts.Concat(_openDocsByPath.Values.OfType<LayoutDocument>()).ToList();
        var openPaths = openLayouts
            .Select(d => d.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try { RegenerateAllGeneratedCells(cwsPath, openPaths); }
        catch (Exception ex) { Messages.Warning($"Generated cells could not be rebuilt: {ex.Message}"); }

        int repointed = 0;
        foreach (var doc in openLayouts)
        {
            int moved;
            try { moved = RepointOpenLayout(doc, cwsPath); }
            catch (Exception ex)
            {
                Messages.Warning($"'{doc.Title}' could not be updated after the reload: {ex.Message}");
                continue;
            }

            if (moved == 0) continue;
            repointed += moved;
            doc.ViewModel.IsDirty = true;
            doc.ViewModel.Model.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }

        RefreshAllOpenLayoutTech();

        // What the resolver offers has just been re-decided, so which layout cell each kit part
        // places has to be re-decided with it. Without this a kit whose cell library was declared
        // during THIS session — the ordinary shape of importing a kit into an open workspace —
        // keeps an empty generator map for the rest of it, and every part placed from it is reported
        // as having no artwork.
        RefreshPCellPaletteItems();

        Messages.Success(repointed > 0
            ? $"Generated artwork reloaded — {Plural(repointed, "placed cell")} moved to newly generated artwork."
            : "Generated artwork reloaded.");
    }

    /// <summary>Rebuilds and repoints one open layout, using the same pass the on-disk sweep uses so
    /// an open document and a closed one can never end up repointed differently.</summary>
    private int RepointOpenLayout(LayoutDocument doc, string cwsPath)
    {
        var techCache = new Dictionary<string, Technology?>(StringComparer.OrdinalIgnoreCase);
        Technology? ResolveTech(string? techIdentity)
        {
            if (string.IsNullOrEmpty(techIdentity)) return null;
            if (techCache.TryGetValue(techIdentity, out var cached)) return cached;
            Technology? tech = null;
            try { if (File.Exists(techIdentity)) tech = TechPersistence.LoadFromFile(techIdentity); }
            catch { /* best-effort — a missing .ctech regenerates on the fallback palette */ }
            techCache[techIdentity] = tech;
            return tech;
        }

        return GeneratedCellsLifecycle.Regenerate(
            Path.GetDirectoryName(cwsPath)!, doc.ViewModel.Model, ResolveTech, m => Messages.Warning(m));
    }

    private static string? ReadRecordedPythonInterpreter(string workspaceRootDir)
    {
        try
        {
            string cws = Path.Combine(workspaceRootDir, ".cws");
            return File.Exists(cws) ? WorkspacePersistence.LoadFromFile(cws).PythonInterpreter : null;
        }
        catch { return null; } // a .cws we cannot read is the workspace's problem, not this decision's
    }

    /// <summary>
    /// Writes the settled interpreter back, so the next open replays it instead of probing.
    ///
    /// <para>Written immediately rather than at the next save: settling on an interpreter is not an
    /// edit to a document the user might reasonably discard, and the whole point of recording it is
    /// that the NEXT open is fast.</para>
    /// </summary>
    private void RecordPythonInterpreter(CircuitRF.Ui.Layout.PCells.Wire.PythonInterpreter chosen)
    {
        if (CurrentWorkspacePath is not { } cwsPath) return;
        try
        {
            var cws = File.Exists(cwsPath) ? WorkspacePersistence.LoadFromFile(cwsPath) : new CwsFile();
            string record = chosen.ToRecord();
            if (string.Equals(cws.PythonInterpreter, record, StringComparison.Ordinal)) return;

            cws.PythonInterpreter = record;
            WorkspacePersistence.SaveToFileAtomic(cwsPath, cws);
        }
        catch (Exception ex)
        {
            // Failing to RECORD the decision must never undo having MADE it — the generators are
            // already running; the only cost is probing again next time.
            Messages.Warning($"The chosen Python interpreter could not be recorded: {ex.Message}");
        }
    }

    private void ResetTechCache()
    {
        _techCache = new TechnologyCache();
        _techCache.TechnologyChanged += OnTechnologyChanged;
        _wasmCache = new WasmCache();
        _assemblyRulesAsked.Clear();

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

        // The ancestor-.cws walk, the .cws's DefaultTechRef read and the resolve itself are
        // TechnologyResolver's — shared with `circuitrf em`, which has no "current workspace" to fall
        // back to and must apply exactly this rule (brief-cli-em-verb.md R-emcli-5). What is left
        // here is what only the GUI has: posting the diagnostics, and R-fgn-4's orphan prompt.
        var (resolution, ownCwsPath) = TechnologyResolver.ResolveForDocument(
            techRef, normalizedClayPath, CurrentWorkspacePath, _techCache);

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
    /// Applies the Settings ▸ On Launch ▸ Window Layout preference. Called once after the window is
    /// shown, and again by View ▸ Reset Layout through <see cref="PerformLayoutReset"/>.
    ///
    /// <para>The two "focus" presets only need the right tab brought to the front — the shell already
    /// opens in that arrangement. <see cref="WindowLayout.ProjectTreeAndLibrary"/> is a genuinely
    /// different arrangement, so it rebuilds the dock tree (documents preserved, exactly as Reset
    /// Layout does).</para>
    /// </summary>
    public void ApplyWindowLayout(WindowLayout preset)
    {
        if (preset == WindowLayout.ProjectTreeAndLibrary)
            RebuildLayoutFrom(Docking.DockLayoutDefaults.For(preset));

        FocusPaneFor(preset);
    }

    /// <summary>Brings the pane a Window Layout preset names to the front of its group.</summary>
    private void FocusPaneFor(WindowLayout preset)
    {
        IDockable? target = preset == WindowLayout.LibraryFocus
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

            case LaunchAction.NewLayout:
                _factory.RemoveWelcomeStub();
                NewLayout();
                break;

            case LaunchAction.NewHarmonica:
                _factory.RemoveWelcomeStub();
                NewHarmonica();
                break;
        }
    }

    /// <summary>
    /// After a new workspace is created, open whatever the On Launch preference names — New
    /// Schematic, New Symbol, New Data Display, or nothing (Welcome stays). New Workspace / Open
    /// Workspace fall back to New Schematic here — re-entering the New Workspace flow mid-creation
    /// would recurse. This is what makes "New Schematic" as the configured On Launch action apply
    /// consistently whether the user launches the app fresh or creates a workspace mid-session.
    /// </summary>
    private void ApplyOnLaunchActionForNewWorkspace()
    {
        var action = AppPreferencesIo.Load().LaunchAction ?? LaunchAction.Welcome;
        if (action is LaunchAction.NewWorkspace or LaunchAction.OpenWorkspace)
            action = LaunchAction.NewSchematic;

        switch (action)
        {
            case LaunchAction.Welcome:
                // Leave the fresh Welcome stub (from CreateDefaultLayout) showing.
                break;

            case LaunchAction.NewSchematic:
                _factory.RemoveWelcomeStub();
                NewScratchSchematic();
                break;

            case LaunchAction.NewSymbol:
                _factory.RemoveWelcomeStub();
                NewScratchSymbol();
                break;

            case LaunchAction.NewDataDisplay:
                _factory.RemoveWelcomeStub();
                NewDataDisplay();
                break;

            case LaunchAction.NewLayout:
                _factory.RemoveWelcomeStub();
                NewLayout();
                break;

            case LaunchAction.NewHarmonica:
                _factory.RemoveWelcomeStub();
                NewHarmonica();
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
            ResetHarmonicaDockedFocusTracking();
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
            _scratchHarmonicas.Clear();
            _registry.Clear();
            _layoutRegistry.Clear();
            ResetTechCache();
            CurrentWorkspacePath = cwsPath;

            // Honor Settings ▸ On Launch ▸ Window Layout here too — this is the shell's clean-slate
            // rebuild, the same case ApplyWindowLayout/PerformLayoutReset cover for launch and Reset
            // Layout, but New Workspace never routed through either, so it silently reverted to the
            // hardcoded §2.0 default.
            var windowLayoutPreset = AppPreferencesIo.Load().WindowLayout ?? WindowLayout.ProjectTreeAndLibrary;
            var newLayout = _factory.CreateDefaultLayout(Docking.DockLayoutDefaults.For(windowLayoutPreset));
            _factory.InitLayout(newLayout);
            Layout = newLayout;
            FocusPaneFor(windowLayoutPreset);
            _factory.PaletteTool?.SetPlacementService(PlacementService);
            _factory.PaletteTool?.SetMru(_recentlyPlaced);
            RestoreInstalledPdks();

            // CreateDefaultLayout replaced all tool instances and the DocumentDock — re-wire them.
            _factory.ProjectTreeTool?.SetActions(this);
            SubscribeToFilterState();
            SubscribeToTreeSelection();
            _factory.ProjectTreeTool?.SetWorkspace(workspaceDir);

            // Re-subscribe to the new document panes (instances replaced by CreateDefaultLayout).
            SubscribeToDocumentPanes();
            WireAnalysesRun();

            ApplyOnLaunchActionForNewWorkspace();

            PushRecent(cwsPath);
            Messages.Clear();
            Messages.Success($"New workspace '{result.Name}' created.");

            // R-dock-9: hiding the dockers is a view preference, so it survives a workspace switch.
            ReapplyCollapsedStateIfNeeded();
            ApplyShowDockersOnLaunchPreference(AppPreferencesIo.Load().ShowDockersOnLaunch ?? true);
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

    // ── Archive / Unarchive (owner request, 2026-08-15) ───────────────────────

    /// <summary>
    /// File ▸ Archive Workspace… — zips the workspace so it can be handed to someone on another
    /// machine.
    ///
    /// <para>Unsaved work is offered up first, through the SAME prompt closing a workspace uses.
    /// An archive is built from what is on disk, so archiving over the top of unsaved edits would
    /// produce an archive of a design nobody has — a failure that stays invisible until the
    /// recipient opens it. Declining is still allowed (the archive is then honestly of the saved
    /// state); cancelling stops.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private async Task ArchiveWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || CurrentWorkspacePath is null) return;

        if (HasAnyDirtyWork(includeFloated: false) &&
            !await PromptSaveBeforeClose(window, "archiving the workspace", includeFloated: false))
            return;

        // The .cws itself is always refreshed: the dock arrangement and open-document list are what
        // the recipient's window will come up in, and they only reach disk from here.
        WriteWorkspaceFile(CurrentWorkspacePath, silent: true);

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        WorkspaceArchivePlan plan;
        try { plan = WorkspaceArchiveScanner.Scan(workspaceDir); }
        catch (Exception ex) { Messages.Error($"Archive: could not read the workspace — {ex.Message}"); return; }

        if (await new ArchiveWorkspaceDialog(plan).ShowDialog<bool>(window) is not true) return;

        var suggested = Path.GetFileName(workspaceDir.TrimEnd(Path.DirectorySeparatorChar));
        var target = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Archive Workspace",
            SuggestedFileName = suggested,
            DefaultExtension  = "zip",
            FileTypeChoices   = [new FilePickerFileType("Zip Archive") { Patterns = ["*.zip"] }],
        });
        if (target is null) return;

        var zipPath = target.Path.LocalPath;
        try
        {
            var result = await Task.Run(() => WorkspaceArchiveWriter.Write(plan, zipPath));

            Messages.Success(
                $"Archived {result.FileCount} file(s) to {Path.GetFileName(zipPath)} " +
                $"({WorkspaceArchivePlan.FormatSize(result.ZipBytes)}).");

            if (result.Repointed.Count > 0)
                Messages.Info($"Repointed references in {result.Repointed.Count} file(s) to the archived copies.");

            // Said out loud rather than left for the recipient to discover: these are the references
            // that will arrive broken, and the user is the only one who can still do anything about it.
            foreach (var external in result.StillExternal)
                Messages.Warning($"Not included, so this reference will not resolve for the recipient: {external}");

            // The same warning for the other way a reference arrives dead: a results file a document
            // plots that the user unticked. Nothing is broken about the PATH, so the repointing pass
            // has nothing to say about it — only the writer knows the file is not in the zip.
            foreach (var excluded in result.ExcludedResults)
                Messages.Warning($"Not included, so a display that plots it will come up empty: {excluded}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Archive failed: {ex.Message}");
        }
    }

    /// <summary>
    /// File ▸ Unarchive Workspace… — the reverse: pick a <c>.zip</c>, pick where it goes, unpack it,
    /// and open what came out.
    /// </summary>
    [RelayCommand]
    private async Task UnarchiveWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Unarchive Workspace",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Workspace Archive") { Patterns = ["*.zip"] }],
        });
        if (picked.Count == 0) return;

        IStorageFolder? startLocation = null;
        try { startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Unarchive Into",
            AllowMultiple          = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0) return;

        var zipPath     = picked[0].Path.LocalPath;
        var destination = folders[0].Path.LocalPath;

        ArchiveExtractResult extracted;
        try { extracted = await Task.Run(() => WorkspaceArchiveExtractor.Extract(zipPath, destination)); }
        catch (Exception ex) { Messages.Error($"Unarchive failed: {ex.Message}"); return; }

        foreach (var rejected in extracted.Rejected)
            Messages.Warning($"Archive entry refused for pointing outside the destination: {rejected}");

        Messages.Success($"Unarchived {extracted.FileCount} file(s) into {extracted.WorkspaceDir}.");

        if (extracted.CwsPath is null)
        {
            Messages.Warning("That archive holds no .cws, so there is no workspace to open.");
            return;
        }

        if (HasAnyDirtyWork(includeFloated: false) &&
            !await PromptSaveBeforeClose(window, "opening the unarchived workspace", includeFloated: false))
            return;

        _lastWorkspaceParentDir = destination;
        SwitchToWorkspace(extracted.CwsPath);
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

            // Null means "the shipped default", so the name is recorded only when it is something
            // else — and what the default IS moved on 2026-08-17 (ThemeResolver.DefaultThemeName).
            ws.ColorSchemeName = ThemeService.Active.Name == ThemeResolver.DefaultThemeName
                ? null : ThemeService.Active.Name;

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
                    else if (dockable is EmSetupDocument emDocKind)
                    {
                        docPath = emDocKind.FilePath;
                        kind    = "emsetup";
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
                    EmSetupDocument aem                  => aem.FilePath,
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
            // "Access to the path … is denied" is the same sentence for an OS privacy block and a
            // real permissions problem, and on macOS it points at the wrong one — the file's own
            // permissions are normal. Let the diagnostic name the actual cause where it can.
            if (FileAccessDiagnostics.TryDescribe(path, ex) is { } diagnostic)
                Messages.PostDiagnostic(diagnostic, path);
            else
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
    /// comment for the full policy story.
    ///
    /// <para><b>Every call site is now gated on
    /// <see cref="GeneratedCellsLifecycle.WipeOnOpenAndClose"/>, which is off.</b> The gate lives
    /// here, in the one wrapper the three of them share, so turning the original policy back on is a
    /// single flag and nothing else. What the folder cost when it was wiped and rebuilt on every
    /// open, and why that stopped being cheap, is on that property.</para></summary>
    private void DeleteGeneratedCellsFolder(string cwsPath)
    {
        if (!GeneratedCellsLifecycle.WipeOnOpenAndClose) return;

        try { GeneratedCellsLifecycle.DeleteGeneratedCellsFolder(Path.GetDirectoryName(cwsPath)!); }
        catch (Exception ex) { Messages.Warning($"Could not clear the generated-cell cache: {ex.Message}"); }
    }

    /// <summary>R-L5g-8: thin wrapper over <see cref="GeneratedCellsLifecycle.RegenerateAll"/>, supplying
    /// a small memoized <c>.ctech</c> loader as the technology resolver.</summary>
    private void RegenerateAllGeneratedCells(string cwsPath, IReadOnlySet<string>? skipPaths = null)
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

        // Reported, not swallowed: a generator that will not run is exactly what an author needs
        // told, and one message per distinct reason is the difference between a report and a flood.
        var said = new HashSet<string>(StringComparer.Ordinal);
        void Report(string m) { if (said.Add(m)) Messages.Warning(m); }

        var outcome = GeneratedCellsLifecycle.RegenerateAll(
            Path.GetDirectoryName(cwsPath)!, ResolveTech, Report, skipPaths);

        // Silent when nothing moved, which is every ordinary open.
        if (outcome.InstancesRepointed > 0)
            Messages.Info($"{Plural(outcome.InstancesRepointed, "placed cell")} moved to newly generated " +
                          $"artwork after a generator change ({Plural(outcome.LayoutsRewritten, "layout")} updated).");

        // Also silent on an ordinary open, because nothing has gone stale on one. Said when it does
        // happen so that a generator or technology edit, which is what leaves the old cells behind,
        // reads as one event rather than as artwork quietly changing.
        if (outcome.CellsPruned > 0)
            Messages.Info($"{Plural(outcome.CellsPruned, "generated cell")} no layout still uses " +
                          $"{(outcome.CellsPruned == 1 ? "was" : "were")} removed from the cache.");
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
        // A fresh dock layout brings a fresh Analyses panel, so the old panel's focus must not
        // outlive it and leave Run enabled against a schematic that is no longer open.
        SetAnalysesPanelFocused(false);
        ResetHarmonicaDockedFocusTracking();
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
            _scratchHarmonicas.Clear();
        _registry.Clear();
        _layoutRegistry.Clear();
        ResetTechCache();
        CurrentWorkspacePath = cwsPath;

        // Honor Settings ▸ On Launch ▸ Window Layout for the clean-slate rebuild — a saved .cws
        // arrangement (applied below via ApplyRestoredDockLayout, when present) still wins, but a
        // workspace with no saved layout block should fall back to the chosen preset, not the
        // hardcoded §2.0 default.
        var windowLayoutPreset = AppPreferencesIo.Load().WindowLayout ?? WindowLayout.ProjectTreeAndLibrary;

        // Suppressed: this CLEAN-SLATE rebuild is not an arrangement the user chose, and the workspace's
        // own saved one is applied a few steps below. Left unsuppressed, the rebuild's own dock events
        // would arm a debounced `.cws` write of the DEFAULT layout — which lands three seconds later,
        // after the restore, and only looks harmless while the restore succeeds. See
        // WhileRebuildingLayout.
        WhileRebuildingLayout(() =>
        {
            var newLayout = _factory.CreateDefaultLayout(Docking.DockLayoutDefaults.For(windowLayoutPreset));
            _factory.InitLayout(newLayout);
            Layout = newLayout;
            FocusPaneFor(windowLayoutPreset);
        });

        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        RestoreInstalledPdks();

        // CreateDefaultLayout replaced all tool instances and the DocumentDock — re-wire them.
        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();
        _factory.ProjectTreeTool?.SetWorkspace(workspaceDir);

        // Re-subscribe to the new document panes (instances replaced by CreateDefaultLayout).
        SubscribeToDocumentPanes();
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
                case "emsetup" when File.Exists(absPath):
                    OpenOrActivateEmSetup(absPath);
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
            // Tab selection ONLY — deliberately NOT ActivateOpenDocument. Restoring which tab was
            // last active is not a user asking for a window: floating windows are restored
            // separately by RestoreFloatingDocumentWindows, and raising-and-focusing here would
            // fight that pass and leave focus wherever the race landed. Every other activate path
            // is a direct gesture and does bring its window forward.
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
            // Same reasoning as the save path above. This one matters more: it fires on every open,
            // so a privacy block reads as "your workspace is corrupt" repeated at every launch.
            if (FileAccessDiagnostics.TryDescribe(cwsPath, ex) is { } diagnostic)
                Messages.PostDiagnostic(diagnostic, cwsPath);
            else
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
            Messages.Error($"Workspace '{missingName}' was not found.");
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
    /// File → <b>Close Window</b> (Ctrl+W / Cmd+W; owner request, 2026-08-25). Closes THE ACTIVE
    /// DOCUMENT of whichever window is in front — the shell's own active tab while the shell has
    /// focus, a torn-off document window's own document while IT has focus — through the SAME
    /// <c>CircuitRfDockFactory.CloseDockable</c>/<see cref="ConfirmCloseDockable"/> path a docked
    /// tab's own close button already uses, so an unsaved document gets exactly one prompt, from the
    /// one place that asks.
    ///
    /// <para>This supersedes the earlier single dynamic-header item (brief-file-menu-restructure.md
    /// R-menu-3's <c>CloseWorkspaceOrWindow</c>, which read "Close Window" only while a torn-off
    /// document had focus and "Close Workspace" otherwise): with a dedicated item of its own, that
    /// item's Window branch would render TWICE in a torn-off window's File menu. The Close Workspace
    /// item below it is now unconditionally the whole-workspace teardown, on every surface — which is
    /// also what the project tree's own "Close Workspace" context item has always meant.</para>
    ///
    /// <para><b>Disabled when there is no window to close</b> (owner, same request): no active
    /// document at all, or a floating TOOL panel has focus. The tool-panel exclusion is R-dock-13's
    /// rule, unchanged — a tool panel belongs to the workspace, not to a document, and it
    /// deliberately does NOT clear <see cref="_focusedWindowDocument"/>, so Save/Save-As stay enabled
    /// and keep acting on the last active DOCUMENT; what it governs is only this Close item.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseWindow))]
    private void CloseWindow()
    {
        if (ResolveActiveDocumentForCommands() is not { } doc) return;
        _factory.CloseDockable(doc);
    }

    private bool CanCloseWindow()
        => !_focusedWindowIsToolOnly && ResolveActiveDocumentForCommands() is not null;

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
        // A fresh dock layout brings a fresh Analyses panel, so the old panel's focus must not
        // outlive it and leave Run enabled against a schematic that is no longer open.
        SetAnalysesPanelFocused(false);
        ResetHarmonicaDockedFocusTracking();

        // Split every tracked MATERIALIZED document by docked-vs-floated; the docked ones close, and
        // so do the floated ones that BELONG to this workspace (see
        // CloseFloatedDocumentsOwnedByWorkspace — a foreign torn-off document still survives).
        var stillOpen = new List<IDockable>();
        foreach (var dockable in _openDocsByPath.Values.ToList())
        {
            if (IsDockableDocked(dockable) || FloatedDocumentClosesWithWorkspace(dockable))
            {
                _factory.ForceCloseDockable(dockable);
                // DISCARD, not retire — and the difference is the bug this fixes. Retiring refuses to
                // drop a DIRTY session, which is right in general and wrong here: by this point the
                // user has been prompted and answered Don't Save, so the unsaved state is deliberately
                // gone. Keeping the dirty flag made the NEXT workspace open prompt to save a document
                // belonging to the workspace just closed.
                //
                // Still scoped per path rather than a blanket registry Clear(), which would also tear
                // down a surviving floated document's own push-in/undo session.
                if (dockable is SchematicDocument closedSd && closedSd.FilePath is { } schPath)
                    DiscardSessionIfUnreferenced(schPath);
                else if (dockable is LayoutDocument closedLd && closedLd.FilePath is { } layPath)
                    DiscardLayoutSessionIfUnreferenced(layPath);
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
        var stillOpenScratchHarmonicas   = _scratchHarmonicas.Where(d   => !IsDockableDocked(d)).ToList();

        _openDocsByPath.Clear();
        foreach (var dockable in stillOpen)
        {
            var path = dockable switch
            {
                SchematicDocument sd            => sd.FilePath,
                SymbolEditorDocument syed        => syed.ViewModel.CurrentSymbolPath,
                LayoutDocument lad               => lad.FilePath,
                DataDisplayDocument dd           => dd.FilePath,
                HarmonicaDocument had            => had.FilePath,
                TechDocument td                  => td.FilePath,
                EmSetupDocument emd           => emd.FilePath,
                CellParameterEditorDocument cpd  => Path.GetDirectoryName(cpd.ViewModel.EditModel.CcellPath),
                _ => null,
            };
            if (path is not null) _openDocsByPath[path] = dockable;
        }

        _scratchDocs.Clear();         _scratchDocs.AddRange(stillOpenScratchDocs);
        _scratchSymbols.Clear();      _scratchSymbols.AddRange(stillOpenScratchSymbols);
        _scratchLayouts.Clear();      _scratchLayouts.AddRange(stillOpenScratchLayouts);
        _scratchDataDisplays.Clear(); _scratchDataDisplays.AddRange(stillOpenScratchDataDisplays);
        _scratchHarmonicas.Clear();   _scratchHarmonicas.AddRange(stillOpenScratchHarmonicas);

        // Session registries: NOT a blanket Clear() — a surviving floated schematic/layout's own
        // push-in session must stay registered.
        //
        // Run AFTER the survivor lists are rebuilt above, because "is anything still referring to this
        // session" is only answerable once _openDocsByPath holds exactly the survivors. Before that it
        // still lists documents we just force-closed, and every discard would be refused.
        //
        // This sweep is what catches a dirty session with NO document of its own — a sub-cell that was
        // pushed into, edited and popped out of. The loop above only walks open documents, so such a
        // session was never reached: it stayed dirty and made the next workspace open prompt to save a
        // document belonging to the workspace just closed.
        DiscardUnreferencedDirtySessions();

        ResetTechCache();

        CurrentWorkspacePath = null;   // fires OnCurrentWorkspacePathChanged → tree.ClearWorkspace()

        // Honor Settings ▸ On Launch ▸ Window Layout for the clean-slate rebuild — see the matching
        // note in NewWorkspace/SwitchToWorkspace.
        var windowLayoutPreset = AppPreferencesIo.Load().WindowLayout ?? WindowLayout.ProjectTreeAndLibrary;

        // Suppressed: this CLEAN-SLATE rebuild is not an arrangement the user chose, and the workspace's
        // own saved one is applied a few steps below. Left unsuppressed, the rebuild's own dock events
        // would arm a debounced `.cws` write of the DEFAULT layout — which lands three seconds later,
        // after the restore, and only looks harmless while the restore succeeds. See
        // WhileRebuildingLayout.
        WhileRebuildingLayout(() =>
        {
            var newLayout = _factory.CreateDefaultLayout(Docking.DockLayoutDefaults.For(windowLayoutPreset));
            _factory.InitLayout(newLayout);
            Layout = newLayout;
            FocusPaneFor(windowLayoutPreset);
        });

        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        RestoreInstalledPdks();

        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        SubscribeToDocumentPanes();
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
        // The palette is REPUBLISHED, not reloaded. This used to call RestoreInstalledPdks, which
        // re-reads every referenced kit from disk — so placing a single component re-imported the
        // whole kit set (measured at ~400 ms per placement against a real one) purely to get the kit
        // tiles back beside the freshly-reordered MRU. Nothing about a placement can change what a
        // kit holds; only what is shown alongside it.
        PublishKitPaletteItems();

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

    // Through UndoLast/CanUndoLast rather than UndoRedo directly: a document may hold more than one
    // edit history (a layout showing a wirebond cell has the wires' snapshot stack beside its own
    // command stack, WB40), and only the document can say which of them the user edited last. Defaulted
    // on IUndoableDocument to the single-stack behaviour, so every other document type is unaffected.
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _activeUndoTarget?.UndoLast();
    private bool CanUndo() => _activeUndoTarget?.CanUndoLast ?? false;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _activeUndoTarget?.RedoLast();
    private bool CanRedo() => _activeUndoTarget?.CanRedoLast ?? false;

    private void SetActiveUndoTarget(IEditHistoryDocument? target)
    {
        if (_subscribedUndoStack is { } old)
            old.PropertyChanged -= OnActiveStackPropertyChanged;
        _subscribedUndoStack = null;

        if (_activeUndoDoc is { } oldDoc)
            oldDoc.ActiveViewModelChanged -= OnActiveDocFrameChanged;
        _activeUndoDoc = null;

        if (_activeUndoLayoutDoc is { } oldLayoutDoc)
            oldLayoutDoc.ActiveViewModelChanged -= OnActiveDocFrameChanged;
        _activeUndoLayoutDoc = null;

        _activeUndoTarget = target;

        // A schematic tab can retarget its undo stack via Push In / Pop Out WITHOUT the active
        // dockable changing — follow those frame changes so Undo/Redo stay routed and enabled.
        if (target is SchematicDocument sd)
        {
            _activeUndoDoc = sd;
            sd.ActiveViewModelChanged += OnActiveDocFrameChanged;
        }

        // A LAYOUT tab retargets its stack on Push In / Pop Out for exactly the same reason
        // (LayoutDocument.UndoRedo follows ActiveViewModel) and was never followed — so Undo stayed
        // hooked to the parent cell's stack after pushing into a sub-cell. Found while wiring the
        // wire history below; same shape, same one-line answer.
        if (target is LayoutDocument layoutDoc)
        {
            _activeUndoLayoutDoc = layoutDoc;
            layoutDoc.ActiveViewModelChanged += OnActiveDocFrameChanged;
        }

        HookActiveStack();
    }

    private LayoutDocument? _activeUndoLayoutDoc;

    // The wire history the Undo command is currently following, or null. A wirebond cell's wires
    // (WB40) are a second edit history on one document, and it raises no UndoRedoStack notification —
    // so without this the menu item and the keybinding stay DISABLED after a wire edit and Ctrl+Z
    // appears to do nothing, which is exactly what the owner reported (2026-08-17).
    private LayoutEditorViewModel? _subscribedWireHistory;

    // The exact stack OnActiveStackPropertyChanged is subscribed to. Tracked separately because a
    // SchematicDocument's UndoRedo changes on Push In / Pop Out — we must unsubscribe the stack we
    // actually hooked, not whatever UndoRedo returns after a retarget.
    private UndoRedoStack? _subscribedUndoStack;

    // The active schematic doc whose ActiveViewModelChanged we're following (hierarchy retarget).
    private SchematicDocument? _activeUndoDoc;

    // The Data Display Undo command whose CanExecuteChanged is currently driving the shell's own
    // Undo/Redo enablement — the Data Display counterpart of _subscribedUndoStack.
    private System.Windows.Input.ICommand? _subscribedDisplayUndoCommand;

    // A Data Display's history moved. Redo rides the same notification: DisplayWindowViewModel
    // raises both commands' CanExecuteChanged from one StateChanged handler, so one subscription
    // is enough and a second would only fire the same fan-out twice.
    private void OnActiveDisplayUndoStateChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    // (Re)subscribe to the active target's CURRENT stack and refresh Undo/Redo command + labels.
    private void HookActiveStack()
    {
        if (_subscribedUndoStack is { } old)
            old.PropertyChanged -= OnActiveStackPropertyChanged;
        _subscribedUndoStack = null;

        if (_activeUndoTarget is IUndoableDocument { UndoRedo: { } stack })
        {
            _subscribedUndoStack = stack;
            stack.PropertyChanged += OnActiveStackPropertyChanged;
        }

        // A Data Display keeps no UndoRedoStack (see IEditHistoryDocument), so the notification that
        // its history moved arrives on its own commands' CanExecuteChanged instead. Without this the
        // Edit menu item and the toolbar button stay stuck at whatever they were when the document
        // took focus — disabled after the first plot move, which on macOS also means the app-global
        // Cmd+Z is inert.
        if (_subscribedDisplayUndoCommand is { } oldDisplayCmd)
            oldDisplayCmd.CanExecuteChanged -= OnActiveDisplayUndoStateChanged;
        _subscribedDisplayUndoCommand = null;

        if (_activeUndoTarget is DataDisplay.DataDisplayDocument ddUndo)
        {
            _subscribedDisplayUndoCommand = ddUndo.ViewModel.Window.UndoCommand;
            _subscribedDisplayUndoCommand.CanExecuteChanged += OnActiveDisplayUndoStateChanged;
        }

        if (_subscribedWireHistory is { } oldWires)
            oldWires.WireHistoryChanged -= OnWireHistoryChanged;
        _subscribedWireHistory = null;

        if (_activeUndoTarget is LayoutDocument { ActiveViewModel: { WireDesign: not null } wireVm })
        {
            _subscribedWireHistory = wireVm;
            wireVm.WireHistoryChanged += OnWireHistoryChanged;
        }

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    // Push In / Pop Out on the active schematic or layout swaps its UndoRedo stack; re-hook to the new
    // one — and, for a layout, to whatever wire history the new frame has (or has not).
    private void OnActiveDocFrameChanged(object? sender, EventArgs e) => HookActiveStack();

    // A wire edit, undo or redo moved a history the UndoRedoStack knows nothing about.
    private void OnWireHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

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
        // Per-window: a torn-off document window's own Cut/Copy/Paste must act on ITS document, never
        // the main shell's — same rule as every other per-window command (see ResolveOwner/
        // ResolveActiveDocumentForCommands's own note).
        var active = ResolveActiveDocumentForCommands();
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
        else if (active is Layout.LayoutDocument layDoc)
        {
            if (paste)    layDoc.RequestPaste();
            else if (cut) layDoc.RequestCut();
            else          layDoc.RequestCopy();
        }
        else if (active is WBond.WBondDocument wbDoc)
        {
            if (paste)    wbDoc.RequestPaste();
            else if (cut) wbDoc.RequestCut();
            else          wbDoc.RequestCopy();
        }
    }

    private IClipboard? GetClipboard()
    {
        var window = ResolveOwner(null);
        return window is not null ? TopLevel.GetTopLevel(window)?.Clipboard : null;
    }

    // ---- View commands -------------------------------------------------------

    [RelayCommand]
    private void ResetLayout() => PerformLayoutReset("Layout reset to default.");

    // Per ui-design.md's own definition: "Fit Windows to Frame — reset/fit the dock layout to the
    // frame." Shares ResetLayout's mechanism (re-host the existing panels into a fresh proportional
    // skeleton, documents preserved) — the two toolbar affordances describe the same operation.
    private void PerformLayoutReset(string message)
    {
        // "Reset" means "back to the layout you chose in Settings ▸ On Launch ▸ Window Layout" —
        // that setting is the ONLY place a layout is chosen, so this menu deliberately offers no
        // options of its own.
        var preset = AppPreferencesIo.Load().WindowLayout ?? WindowLayout.ProjectTreeAndLibrary;

        RebuildLayoutFrom(Docking.DockLayoutDefaults.For(preset));
        FocusPaneFor(preset);

        // Resetting the layout to the default means showing the panels — leaving the collapsed
        // toggle armed here would produce a "reset" that still hides everything.
        DockersCollapsed   = false;
        _preCollapseLayout = null;

        Messages.Info(message);
    }

    /// <summary>
    /// Re-hosts the existing DocumentDock and tool instances into a fresh skeleton built from
    /// <paramref name="state"/>. Documents, active tab, and per-document selection are kept; only
    /// panel positions/proportions change.
    /// </summary>
    private void RebuildLayoutFrom(Docking.CwsDockLayout state)
    {
        var newLayout = _factory.CreateLayoutPreservingContent(state);
        _factory.InitLayout(newLayout);
        Layout = newLayout;
        // A preserved-content rebuild re-hosts the document area, and a saved split brings back panes
        // this view model has never seen — the primary is carried over, the others are built fresh.
        SubscribeToDocumentPanes();
        _factory.PaletteTool?.SetPlacementService(PlacementService);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        // Same reason as the MRU push: CreateLayoutPreservingContent hands back a NEW PaletteTool, so
        // the kit tiles have to be published into it again — but moving panels around cannot change
        // what a kit holds, and re-reading every one of them from disk to rearrange a dock is the
        // most expensive way to do the cheapest thing here.
        PublishKitPaletteItems();
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        // The tree was REPLACED, so any panel may have appeared, vanished or changed which tab is in
        // front — the one thing a toolbar toggle cannot work out for itself. `ApplyDockLayout` raises this
        // for the same reason; this path (Window Layout preset, Reset Layout) does not go through it, and
        // that is precisely how the Library button came up unlit at launch with a visible Library panel:
        // the shell is BUILT tabbed (Library behind Project Tree, so genuinely not in view), then rebuilt
        // into the ProjectTreeAndLibrary preset a moment later — and nothing told the button.
        RaiseToolPanelVisibilityChanged();
    }

    // Dispatches to whichever document is currently focused (per-window, see
    // ResolveActiveDocumentForCommands) — .csch, .clay, .csym, .wBond each raise their own
    // ZoomToFitRequested event, which the already-subscribed view runs against its real canvas(es).
    [RelayCommand]
    private void ZoomToFit()
    {
        switch (ResolveActiveDocumentForCommands())
        {
            case SchematicDocument sd: sd.RequestZoomToFit(); break;
            case Layout.LayoutDocument ld: ld.RequestZoomToFit(); break;
            case SymbolEditorDocument symd: symd.RequestZoomToFit(); break;
            case WBond.WBondDocument wbd: wbd.RequestZoomToFit(); break;
            default: Messages.Info("Zoom to Fit: no document is focused."); break;
        }
    }
    // HideShowDockers lives in WorkspaceViewModel.Docking.cs — it is a real full-canvas toggle now.
    [RelayCommand] private void FitWindowsToFrame() => PerformLayoutReset("Windows fit to frame.");

    // The Messages toolbar button used to run a ToggleMessagesRegion command of its own, which only ever
    // made the panel the active tab — it could not close one, and said nothing about whether the panel was
    // on screen. It is one of the three panel toggles now (ToggleToolPanelCommand, "Messages"), so there is
    // nothing left here: a second, weaker way to show the same panel is exactly what made the button read
    // as broken when pressed twice.

    // ---- Simulate commands ---------------------------------------------------

    /// <summary>
    /// Extracts the active schematic, writes netlist.cnl, then runs the engine chain
    /// (CnlReader → Elaborator → analysis engine → DataSet) on a background thread.
    /// Reports progress and results via Messages; holds DataSets for Phase 7.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunAnalysis))]
    private async Task RunAnalysis()
    {
        var doc = (_factory.DocumentDock?.ActiveDockable as SchematicDocument) ?? _lastActiveSchematicDoc;
        if (doc is null) { Messages.Warning("Run: no schematic is active."); return; }
        await RunSchematicDocAsync(doc);
    }

    // The Run/Stop toolbar buttons (and the Simulate menu / Ctrl+R sharing the same command) are
    // greyed out whenever a .csch document is not the active dockable — not merely retained-schematic
    // aware, per the owner's explicit request. This is distinct from the Analyses panel's own Run
    // button, which deliberately keeps working off _lastActiveSchematicDoc (brief-analyses-toolbar-run-retain).
    //
    // ...with ONE addition (owner, 2026-08-29): the Analyses panel itself counts as a place to run
    // from. Focusing that panel — to pick an analysis, edit a sweep, then run it — is not focusing
    // "something other than a schematic" in any sense the user means; and the panel's own Run button
    // sitting live beside a greyed-out ⌘R was the visible contradiction. A tool panel is never the
    // DocumentDock's ActiveDockable, so the retained schematic is what the enablement (and
    // RunAnalysis's own fallback, which already reads it) resolves against. Everything else is
    // unchanged: focus a Data Display or a symbol editor and Run still greys out.
    private bool CanRunAnalysis() =>
        _runCts is null && HasARunnableSchematicInFocus;

    /// <summary>
    /// True when the user is looking at somewhere a run can be started FROM: a schematic document,
    /// or the Analyses panel with a schematic still retained behind it. Shared by Run and Stop so
    /// the pair can never disagree about whether this surface owns the run.
    /// </summary>
    private bool HasARunnableSchematicInFocus =>
        _factory.DocumentDock?.ActiveDockable is SchematicDocument
        || (_analysesPanelFocused && _lastActiveSchematicDoc is not null);

    /// <summary>
    /// Records whether the Analyses panel holds focus and re-evaluates Run/Stop. Called on every
    /// focus change, including the ones that move focus AWAY from the panel — a
    /// <c>[RelayCommand(CanExecute=…)]</c> is never re-evaluated on its own.
    /// </summary>
    private void SetAnalysesPanelFocused(bool focused)
    {
        if (_analysesPanelFocused == focused) return;
        _analysesPanelFocused = focused;
        RunAnalysisCommand.NotifyCanExecuteChanged();
        StopAnalysisCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Live cancellation source for the run in flight; null when nothing is running. It is what makes
    /// Stop real — the engines check its token at every point boundary — and what gates Run/Stop
    /// enablement, so the two can never both be available.
    /// </summary>
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// The run's stop as the UI hands it around: the Stop button, the Simulate ▸ Stop menu item and the
    /// live row's right-click ▸ Cancel all go through THIS, so the request is one request and every
    /// surface showing it settles together. Null when nothing is running.
    /// </summary>
    private RunCancellation? _runCancellation;

    private void SetRunning(CancellationTokenSource? cts)
    {
        _runCts = cts;
        RunAnalysisCommand.NotifyCanExecuteChanged();
        StopAnalysisCommand.NotifyCanExecuteChanged();
    }

    private async Task RunSchematicDocAsync(SchematicDocument activeDoc)
    {
        // The Analyses panel's own Run button reaches this directly rather than through
        // RunAnalysisCommand, so its CanExecute gate does not cover this path. One run at a time:
        // two concurrent runs would write the same netlist.cnl and the same results file.
        if (_runCts is not null) { Messages.Warning("Run: a simulation is already running."); return; }

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
            Diagnostics.CrashReporter.Note($"run: '{testBenchName}' netlist written to {netlistPath}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Netlist write failed: {ex.Message}");
            return;
        }

        string? workspaceRoot = CurrentWorkspacePath is not null
            ? Path.GetDirectoryName(CurrentWorkspacePath)
            : null;

        // Step 2: work out what the run WILL do, and say so BEFORE running any of it. This is the
        // whole point of the plan/execute split — a nested sweep can be tens of thousands of points
        // and many minutes, and "11 pt(s) over VGS x 101 pt(s) over VDS = 1,111 total pt(s)" is only
        // actionable while there is still something to stop.
        RunPlan plan;
        try
        {
            plan = await Task.Run(() => SchematicRunService.Prepare(netlistPath, workspaceRoot));
        }
        catch (Exception ex)
        {
            Messages.Error($"Run failed unexpectedly: {ex.Message}");   // defensive: Prepare never throws
            return;
        }

        if (plan.Status != RunStatus.Success)
        {
            if (plan.Status == RunStatus.NoAnalysis) Messages.Info(plan.StatusMessage);
            else                                     Messages.Error(plan.StatusMessage);
            return;
        }

        foreach (var line in plan.Lines)
            Messages.Info(line);

        Diagnostics.CrashReporter.Note(
            $"run: '{testBenchName}' planned — {plan.Analyses.Count} analysis, {plan.TotalWorkUnits} work unit(s)");
        foreach (var line in plan.Lines)
            Diagnostics.CrashReporter.Note($"run:   {line}");

        // Step 3: run the engine on a background thread so the UI stays responsive — and so Stop has
        // a thread to interrupt.
        var live = Messages.BeginProgress($"Running '{testBenchName}'…");

        RunResult result;
        using (var cts = new CancellationTokenSource())
        {
            // The Stop button, the Simulate ▸ Stop menu item and the live row's own right-click ▸
            // Cancel are ONE request through ONE object (owner, 2026-08-19). Whichever the user
            // reaches for, the other two go grey — the handle refuses a second ask, and CanStopAnalysis
            // reads the same token — so nothing offers to stop a run that is already stopping.
            var cancellation = new RunCancellation($"the run of '{testBenchName}'", () => RequestStop(cts));
            _runCancellation = cancellation;
            live.BindCancellation(cancellation);

            var control = new RunControl
            {
                Token = cts.Token,
                Total = plan.TotalWorkUnits,
                // Progress<T> captures the UI SynchronizationContext here, so every observation lands
                // on the UI thread without the engine knowing anything about threading.
                Progress = new Progress<RunProgress>(p => ReportRunProgress(live, testBenchName, p)),
            };

            // Set INSIDE the try, so no path between here and the finally can leave the run flagged as
            // in flight — which would disable Run and leave Stop pointing at a completed run forever.
            try
            {
                SetRunning(cts);
                result = await Task.Run(() => SchematicRunService.Execute(plan, control));
            }
            catch (Exception ex)
            {
                // Defensive — Execute never throws, but guard anyway.
                live.Complete(MessageLevel.Error, $"Run failed unexpectedly: {ex.Message}");
                return;
            }
            finally
            {
                cancellation.Finish();
                _runCancellation = null;
                SetRunning(null);
                Diagnostics.CrashReporter.Note($"run: '{testBenchName}' left the engine");
            }
        }

        if (result.Status == RunStatus.Cancelled)
        {
            // Appended, not replaced: "…DC1  1,194 / 2,525 - cancelled" says how far it got, which is
            // the one thing worth knowing about a run somebody stopped. keepBar: false (owner
            // request, 2026-08-14) — the bar glyph goes once the run settles; the text stays.
            live.Finish(MessageLevel.Warning, "cancelled, no results written", keepBar: false);
            foreach (var n in result.Notes)    Messages.Info(n);
            foreach (var w in result.Warnings) Messages.Warning(w);
            Messages.Info($"Stopped '{testBenchName}'.");
            return;
        }

        // Step 4: surface the result. The outcome is APPENDED to the run's own live row rather than
        // written on a line of its own — the row already names the analysis and its point count, so a
        // separate "1 analysis run(s) complete" would be a second line carrying less than the first.
        // Owner request, 2026-08-14: the bar glyph is dropped once the row settles (keepBar: false)
        // — only the text, including this appended outcome, remains. A short "Finished" line follows,
        // purely for its timestamp: the live row's own timestamp is when the run STARTED.
        // Notes first, warnings after: what the run WORKED OUT reads before what it is unhappy
        // about, and the two are separable at a glance rather than one undifferentiated block.
        foreach (var n in result.Notes)
            Messages.Info(n);

        foreach (var w in result.Warnings)
            Messages.Warning(w);

        switch (result.Status)
        {
            case RunStatus.NoAnalysis:
                live.Finish(MessageLevel.Info, result.StatusMessage, keepBar: false);
                Messages.Info($"Finished '{testBenchName}'.");
                break;
            case RunStatus.EngineError:
                live.Finish(MessageLevel.Error, result.StatusMessage, keepBar: false);
                Messages.Info($"Finished '{testBenchName}'.");
                break;
            case RunStatus.Success:
                live.Finish(MessageLevel.Success, result.StatusMessage, keepBar: false);
                Messages.Info($"Finished '{testBenchName}'.");

                // Loadpull / Loadpull-Pursuit outcome counts. Reported per analysis and only for the
                // ones that actually swept a termination grid — Describe returns null for every other
                // analysis type, so nothing else in the run gains a line.
                foreach (var ar in result.Results)
                    if (LoadpullRunSummary.Describe(ar.Data) is { } summary)
                        Messages.Info($"{ar.Name}: {summary}");

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

    /// <summary>
    /// Rewrites the live "Running…" line from one engine progress observation. Runs on the UI thread —
    /// <see cref="Progress{T}"/> captured the context when it was constructed.
    /// </summary>
    internal static void ReportRunProgress(IProgressMessage live, string benchName, RunProgress p)
    {
        // The text is everything that stays PUT for the whole run; the counter — the only part that
        // changes — goes to the row's own trailing element, after the bar. Move the counter back into
        // the text and the bar starts moving with it again.
        //
        // p.Stage (the current analysis name) is deliberately NOT shown: on a multi-analysis run it
        // changes mid-row, which is exactly the kind of width change this split exists to keep off
        // the bar's left. It still drives an immediate progress report at each analysis boundary.
        if (p.Total > 0)
            live.Update($"Running '{benchName}'",
                        FormatCounter(p.Completed, p.Total),
                        100.0 * p.Completed / p.Total);
        else
            live.Update($"Running '{benchName}'…", indeterminate: true);

        // A crash report's most useful line about a long sweep is HOW FAR IT GOT, and nothing else
        // records that — the live row is in memory and dies with the process. Throttled hard: this
        // fires per progress observation, which on a fast analysis is thousands of times a second.
        if (_lastRunBreadcrumb.Elapsed >= TimeSpan.FromSeconds(10))
        {
            _lastRunBreadcrumb.Restart();
            Diagnostics.CrashReporter.Note(
                $"run: '{benchName}' at {p.Completed} / {p.Total}"
                + (string.IsNullOrEmpty(p.Stage) ? "" : $" (stage '{p.Stage}')"));
        }
    }

    /// <summary>Throttle for the run-progress breadcrumb above. Static because the breadcrumb is a
    /// per-process diagnostic, not per-workspace state.</summary>
    private static readonly Stopwatch _lastRunBreadcrumb = Stopwatch.StartNew();

    /// <summary>
    /// "1,194 / 2,525" — the counter, formatted in the CURRENT culture (a German user reads "2.525").
    ///
    /// <para><b>Deliberately not padded.</b> Space-padding to a constant character count does NOT give
    /// a constant WIDTH in a proportional UI font — a space is roughly half a digit — so the row still
    /// twitched as pad characters turned into digits. The row keeps this steady by RIGHT-ALIGNING it
    /// in a fixed-width box instead, which pins the "/" and the denominator and lets the numerator
    /// grow leftwards into the gap. Re-adding a PadLeft here would fight that alignment.</para>
    /// </summary>
    internal static string FormatCounter(long completed, long total)
        => $"{completed.ToString("N0", CultureInfo.CurrentCulture)} / {total.ToString("N0", CultureInfo.CurrentCulture)}";

    [RelayCommand(CanExecute = nameof(CanStopAnalysis))]
    private void StopAnalysis()
    {
        if (_runCts is not { } cts) { Messages.Info("Stop: nothing is running."); return; }

        // Through the HANDLE when there is one, so the live row's own Cancel greys out with this
        // button — the two are one request, and a menu still offering to stop a run that is already
        // stopping is the same lie a live Stop button would be.
        if (_runCancellation is { } cancellation) cancellation.Cancel();
        else                                      RequestStop(cts);
    }

    /// <summary>
    /// The one place a run is asked to stop — reached from the Stop button/menu and from the live
    /// row's right-click ▸ Cancel, so the two cannot say different things or cancel different runs.
    /// </summary>
    private void RequestStop(CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;

        cts.Cancel();

        // The toolbar's Stop and the menu item go grey the moment the request lands: a second press
        // would do nothing, and a control that stays live while its work is already stopping reads as
        // the first press having been missed.
        StopAnalysisCommand.NotifyCanExecuteChanged();

        // Says what actually happens rather than implying an instant halt: the engines check the token
        // at point boundaries (a sweep point, a frequency, a loadpull termination), never inside a
        // factorization or a Newton loop — so a sweep stops within one point while a lone HB solve
        // still has to finish. That granularity is what makes cancellation cheap enough to be always
        // on; checking inside the inner numerical loops is exactly what this engine cannot afford.
        Messages.Info("Stopping — the run ends after the point in progress; no results will be written.");
    }

    private bool CanStopAnalysis() =>
        _runCts is { IsCancellationRequested: false } && HasARunnableSchematicInFocus;

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

        // The corners this design is set to, resolved through the kits the workspace references.
        // Every way that can fail — a kit no longer referenced, a section it no longer declares, a
        // file that has moved — comes back as a conflict rather than as silence, because a design
        // running at a corner nobody chose produces numbers that are wrong and entirely plausible.
        var cornerProblems = new List<string>();
        var cornerVars = WorkspaceCorners.BindingsFor(
            AvailableCornerAxes, model.CornerSelections, cornerProblems);

        var result = NetExtractor.Extract(model, testBenchName, cells: this, cornerVariables: cornerVars);
        var conflicts = cornerProblems.Count == 0
            ? result.Conflicts
            : (IReadOnlyList<string>)[.. cornerProblems, .. result.Conflicts];

        var header = $"netlist.cnl — generated from TestBench \"{testBenchName}\"" +
                     $" at {DateTime.UtcNow:O}";
        var text = CnlWriter.Write(result.TestBench, result.Library, header);

        File.WriteAllText(tmpPath, text, System.Text.Encoding.UTF8);
        File.Move(tmpPath, targetPath, overwrite: true);

        return (targetPath, conflicts);
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
        var resolved = ResolveOwner(owner);
        if (resolved is null) return;
        await new Views.Dialogs.AboutWindow().ShowDialog(resolved);
    }

    [RelayCommand]
    private async Task ShowSettings(Window? owner)
    {
        var resolved = ResolveOwner(owner);
        if (resolved is null) return;
        var workspaceDir = CurrentWorkspacePath is not null
            ? Path.GetDirectoryName(CurrentWorkspacePath)
            : null;
        var w = new Views.Dialogs.SettingsView(workspaceDir);

        // Help > Check for Updates... is disabled while automatic updates are off, and this dialog is
        // where that gets turned on and off. CanExecute is only re-evaluated when the command says
        // so, so without this the menu item kept whatever state it had when this view-model was
        // constructed.
        w.Closed += (_, _) => RefreshUpdateCommandState();

        w.Show(resolved);
        await Task.CompletedTask;
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
        vm.WorkspaceDisplayUnitProvider = WorkspaceDisplayUnit;
        vm.CellResolverProvider         = () => this;
        vm.UpdateWBondLayout            = UpdateLayoutForWBond;
        vm.DocumentName                 = title;   // no file yet; the tab's title is what it is called
        // filePath = null → scratch; IsScratch = true, IsDirty = false (starts clean), Title = "<title>"
        var doc   = new SchematicDocument(title, vm) { Messages = Messages, Hierarchy = this };
        HookSchematicCanvasFocus(doc);

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
        // A scratch layout has no path yet, so no .cem can reference it — but it gets one the moment
        // it is saved, and CurrentLayoutPath is read live, so the same one line covers both.
        vm.Model.Changed += (_, _) => NotifyEmSetupsLayoutChanged(vm.CurrentLayoutPath);
        vm.RequestAddLayerToTechnology += OnLayoutRequestAddLayerToTechnology;
        vm.WireSidecarRemoved += OnWireSidecarRemoved;
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

                // The kit's own statement about its pins, if it ships one, read from beside the GDSII
                // file rather than from the workspace: it describes THAT kit, and travels with it.
                // Absent is silent (nearly every kit states nothing); present-but-unreadable is
                // reported, because those are two different situations needing two different answers.
                var pinRules = PinInferenceRules.Load(
                    Path.Combine(Path.GetDirectoryName(files[0].Path.LocalPath)!, PinInferenceRules.FileName),
                    out string? rulesProblem);
                if (rulesProblem is not null)
                    Dispatcher.UIThread.Post(() => Messages.Warning(rulesProblem));

                return CircuitRF.Ui.Layout.Interchange.GdsiiImport.Import(
                    stream, workspaceDir, techRes.Tech, LayoutUnits.DefaultDbuPerMicron,
                    preferSourceResolution: false,
                    pinRules: pinRules,
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
    /// than guessing one to open.
    ///
    /// <para>MANY tops is not pathological — it is what a device LIBRARY looks like, where every
    /// primitive is its own top and only the via arrays and corner pieces are referenced by anything
    /// (C1: a real one measured 46 tops out of 56 structures). So the many case is truncated the same
    /// way the created-cell list already is; listing all of them buries the counts that precede it in
    /// the same message.</para></summary>
    internal static string DescribeTopLevelCells(IReadOnlyList<string> topLevelCellDirs) => topLevelCellDirs.Count switch
    {
        0 => "No distinct top-level cell — every structure is referenced by another.",
        1 => $"Top-level cell: \"{Path.GetFileName(topLevelCellDirs[0])}\".",
        _ => $"Top-level cells: {FormatTruncatedNameList([.. topLevelCellDirs.Select(Path.GetFileName)])}.",
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

    // ── Import Board (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md) ──────────────────────────
    // PcbImport does the read/reconcile/CellFolder-creation work; this method is only file picking (UI
    // firewall), workspace/technology context, and the layer-mapping dialog bridge — mirrors
    // ImportDxfLibraryAsync exactly, minus the units prompt (this format's coordinates are always
    // millimetres and never need one, R-L4d-2).
    //
    // IMPORT ONLY. There is deliberately no matching Export entry and none is planned (§1): emitting a
    // board file means authoring board-setup and design-rule state circuitRF has no opinion about, in a
    // file that is then the user's to fabricate from. Export DXF already serves the outward handoff.

    [RelayCommand(CanExecute = nameof(CanImportBoard))]
    private Task ImportBoard(Window? owner) => ImportBoardAsync(owner);
    private bool CanImportBoard() => CurrentWorkspacePath is not null;

    private async Task ImportBoardAsync(Window? owner)
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(owner);
        if (window is null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import Board",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Board") { Patterns = ["*.kicad_pcb"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var techRes = ResolveTechFor(null, null); // the workspace's own default technology
        var boardName = Path.GetFileNameWithoutExtension(files[0].Path.LocalPath);

        // Owner, 2026-08-25: one board file can produce dozens of cells, and dropped at the workspace
        // root they bury everything the user actually authored. They go in a folder named after the
        // file instead — a real directory, not a synthetic tree group, because a cell in a sub-folder
        // already works everywhere (the scanner recurses, the picker recurses, and a CellRef is a
        // relative path) and PcbImport already took its parent directory as a parameter.
        // ImportFolder.UniqueName is what stops a second import of the same board merging into the
        // first one's folder.
        var importDir = CircuitRF.Ui.Layout.Interchange.ImportFolder.Create(workspaceDir, boardName);

        CircuitRF.Ui.Layout.Interchange.PcbImport.ImportResult result;
        try
        {
            result = await Task.Run(() =>
            {
                using var stream = File.OpenRead(files[0].Path.LocalPath);
                return CircuitRF.Ui.Layout.Interchange.PcbImport.Import(
                    stream, importDir, boardName, techRes.Tech, LayoutUnits.DefaultDbuPerMicron,
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
            // "nothing was created" has to stay literally true, so the folder made for this import
            // goes with it — RemoveIfEmpty declines if the import got far enough to write into it.
            CircuitRF.Ui.Layout.Interchange.ImportFolder.RemoveIfEmpty(importDir);
            Messages.Error($"Import Board: {ex.Message}");
            return;
        }

        foreach (var msg in result.Messages) Messages.Info(msg);

        if (result.Cancelled)
        {
            CircuitRF.Ui.Layout.Interchange.ImportFolder.RemoveIfEmpty(importDir);
            Messages.Info("Import Board cancelled — nothing was created.");
            return;
        }

        ApplyBoardImportToTechnology(techRes, result);

        _factory.ProjectTreeTool?.Refresh();
        Messages.Success(
            $"Imported {result.CreatedCellDirs.Count} cell(s) from the board file into "
            + $"'{Path.GetFileName(importDir)}'.");
        if (result.BoardCellDir is { } boardDir) OpenPrimaryLayoutIfResolvable(boardDir);
    }

    /// <summary>
    /// Installs the board's own layers and stackup on the resolved technology as a LIVE (unsaved)
    /// override — the same <c>TechnologyCache.SetLive</c> seam a cross-technology paste's "Add to
    /// technology" already uses (<c>LayoutEditorViewModel.ApplyFragmentReconciliation</c>).
    ///
    /// <para><b>Live rather than written, deliberately.</b> §4 is the reason this phase exists: a board
    /// file's per-layer thickness, permittivity and loss tangent are most of a <c>.ctech</c> arriving for
    /// free, and returning them in a record nobody applies would leave that value on the floor. But a
    /// <c>.ctech</c> is a PROCESS file, possibly shared by every cell in the workspace, and rewriting it
    /// as a side effect of opening a board is not something the user asked for. A live override is
    /// visible immediately, is what every open layout resolves against, and survives only until the user
    /// deliberately saves the technology.</para>
    ///
    /// <para>An existing stackup is never replaced — R-L4d-6's rule that nothing be silently overwritten
    /// runs in this direction too. What was recovered is reported either way, so the user can compare.</para>
    /// </summary>
    private void ApplyBoardImportToTechnology(
        TechResolution techRes, CircuitRF.Ui.Layout.Interchange.PcbImport.ImportResult result)
    {
        if (techRes.Tech is not { } tech || techRes.ResolvedPath is not { } techPath) return;
        if (result.LayersToAdd.Count == 0 && result.Stackup is null) return;

        var clone = TechPersistence.Deserialize(TechPersistence.Serialize(tech));

        int added = 0;
        foreach (var def in result.LayersToAdd)
        {
            if (clone.Layers.Any(l => l.Key == def.Key)) continue;
            clone.Layers.Add(def);
            added++;
        }

        bool stackupApplied = false;
        if (result.Stackup is { } imported)
        {
            if (clone.Stackup.Layers.Count == 0)
            {
                clone.Stackup = imported;
                stackupApplied = true;
            }
            else
            {
                Messages.Warning(
                    $"\"{clone.Name}\" already declares a stackup of {clone.Stackup.Layers.Count} layer(s), " +
                    $"so the board's own {imported.Layers.Count}-layer stackup was NOT applied. " +
                    "Compare them in the technology editor and choose.");
            }
        }

        if (added == 0 && !stackupApplied) return;

        // The technology editor may already be open on this file. It holds its OWN working copy, so a
        // bare SetLive would leave that copy — the one the user is looking at, and the one its Save
        // writes — without the board's layers, and saving it would then overwrite the override with a
        // technology that never had them. Route through the editor when there is one; it fires
        // TechLiveChanged, which installs the override anyway, so the two paths converge.
        if (_openDocsByPath.TryGetValue(Path.GetFullPath(techPath), out var open) is false)
            _openDocsByPath.TryGetValue(techPath, out open);

        if (open is TechDocument techDoc)
            techDoc.ViewModel.ReplaceWorkingAsEdit(clone, "Layers and stackup recovered from a board import");
        else
            _techCache.SetLive(techPath, clone);

        Messages.Info(
            $"Technology \"{clone.Name}\" updated in this session: " +
            $"{added} layer(s) added{(stackupApplied ? $", stackup replaced with the board's {clone.Stackup.Layers.Count} layer(s)" : "")}. " +
            "Nothing was written to disk — open the technology and save it to keep this.");
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

    // ── Import PDK ────────────────────────────────────────────────────────────

    /// <summary>
    /// File → Import → PDK…  Lets the user point at a process design kit — a folder or a .zip —
    /// and reports what circuitRF made of it.
    ///
    /// <para>The dialog is shown for EVERY outcome, not only failures. Kits arrive in many formats
    /// and circuitRF reads a few of them, so "understood some of this" is the normal result and the
    /// user needs to see which parts came through, what was recognised but unreadable, and what was
    /// not recognised at all. A silent success would hide the artwork a kit ships but circuitRF
    /// cannot yet draw.</para>
    /// </summary>
    /// <summary>
    /// The management surface for this workspace's PDK references — add, remove, reveal, validate.
    ///
    /// <para>Required rather than optional: a kit's parts are held in memory now and no longer appear
    /// in the Project Tree, so without this a workspace's dependency on a kit would be invisible until
    /// something failed to resolve, with nowhere to go and repair it.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasWorkspaceOpen))]
    private async Task ManagePdks(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || CurrentWorkspacePath is null) return;

        string wsRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var cws  = TryLoadCws(CurrentWorkspacePath);
        var refs = cws.PdkRefs ?? [];

        // Collected during the dialog and acted on after it closes — see Context.KitAdded for why the
        // offer cannot be made from inside a modal.
        var added = new List<(string Kit, string Path)>();

        await ManagePdksDialog.ShowAsync(window, new ManagePdksDialog.Context(
            WorkspaceRootDir: wsRoot,
            Refs:             refs,
            PlacedPartRefs:   PlacedKitPartRefs(),
            Save:             () =>
            {
                // Written straight through rather than at the next save: a reference the user just
                // repaired must survive closing the workspace without saving a document.
                try
                {
                    var latest = TryLoadCws(CurrentWorkspacePath!);
                    latest.PdkRefs = refs;
                    WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath!, latest);
                }
                catch (Exception ex)
                {
                    Messages.Warning($"Manage PDKs — the change could not be recorded: {ex.Message}");
                }
            },
            Reveal:           RevealPathInFileManager,
            Loaded:           ReloadAllReferencedKits,
            Report:           (level, text) => Messages.Post(level, text),
            KitAdded:         (kit, kitPath) => added.Add((kit, kitPath))));

        foreach (var (kit, kitPath) in added)
            await OfferTechnologyFromKitAsync(window, kit, kitPath);
    }

    private bool HasWorkspaceOpen() => CurrentWorkspacePath is not null;

    /// <summary>
    /// The model-library packages this workspace references. A part kit finds its models by sitting
    /// beside them; once it is referenced from elsewhere that adjacency is gone, and this is the
    /// workspace saying where they went.
    /// </summary>
    private IReadOnlyList<string> WorkspaceLibraryRoots()
    {
        if (CurrentWorkspacePath is null) return [];

        return PdkReferenceManager.LibraryRootsIn(
            Path.GetDirectoryName(CurrentWorkspacePath)!,
            TryLoadCws(CurrentWorkspacePath).PdkRefs ?? []);
    }

    /// <summary>
    /// Re-reads EVERY referenced kit after the Manage PDKs dialog changed the reference set, and
    /// re-wires the palette, the provider resolver and open schematics from the result.
    ///
    /// <para><b>Every kit, not just the one that changed — and that is the whole point.</b> Adding a
    /// model-library package changes what OTHER kits can resolve: a part kit imported before the
    /// package was referenced settled on "no library found", and nothing would revisit that. The
    /// symptom is a kit that looks fine in this dialog and fails at Run with "no kit settled on a way
    /// to evaluate its devices", staying that way until the workspace is reopened.</para>
    ///
    /// <para>Removing a package has the same reach in the other direction, so both go through here.</para>
    /// </summary>
    private void ReloadAllReferencedKits() => RestoreInstalledPdks();

    /// <summary>
    /// Every kit-part reference placed in a schematic that is currently OPEN.
    ///
    /// <para>Deliberately the open documents rather than every <c>.csch</c> on disk: this drives a
    /// warning before a removal, and scanning a whole workspace to produce one would make the dialog
    /// wait on file I/O for a count. It therefore under-reports a design that is not open, which is
    /// why removal is stated as reversible rather than as safe because nothing uses the kit.</para>
    /// </summary>
    private IReadOnlyList<string> PlacedKitPartRefs()
    {
        var models = _openDocsByPath.Values.OfType<SchematicDocument>()
            .Concat(_scratchDocs)
            .Select(d => d.ViewModel.EditModel);

        return [.. models.SelectMany(m => m.Components)
                         .Select(c => c.CellRef)
                         .Where(PdkKitRegistry.IsKitRef)
                         .Select(r => r!)
                         .Distinct(StringComparer.Ordinal)];
    }

    [RelayCommand]
    private async Task ImportPdk(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var choice = await PdkImportPromptDialog.PickAsync(window);
        if (choice is null) return;

        string? path = null;
        if (choice == PdkImportPromptDialog.Choice.Folder)
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Import PDK — choose the kit folder",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            path = folders[0].Path.LocalPath;
        }
        else
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import PDK — choose the kit archive",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Kit archive") { Patterns = ["*.zip"] }],
            });
            if (files.Count == 0) return;
            path = files[0].Path.LocalPath;
        }

        if (string.IsNullOrEmpty(path)) return;

        // An archive is UNPACKED, then imported as the folder it became. Reading it in place produced
        // a kit with no artwork, no models, no settings and a recorded location that was the .zip —
        // so the next workspace open reported the kit folder missing and every part placed from it
        // went unresolved. See KitArchive for the full account.
        if (KitArchive.IsArchive(path))
        {
            path = await UnpackKitArchiveAsync(window, path);
            if (path is null) return;
        }

        // Everything slow about an import happens between the picker closing and the report opening,
        // and until now none of it said anything: reading a real kit is hundreds of milliseconds of
        // file enumeration and netlist parsing, installing it starts one worker per compiled model,
        // and looking for process data walks the tree again. A live row is the one place a user can
        // see that the application is working rather than stuck. Indeterminate throughout — none of
        // the three stages has an honest denominator until it has already finished.
        var live = Messages.BeginProgress($"Import PDK — reading {Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}…");
        live.Update("Import PDK — reading the kit…", indeterminate: true);

        PdkImportReport report;
        try
        {
            report = await Task.Run(() => PdkImporter.Import(path));
        }
        catch (Exception ex)
        {
            live.Complete(MessageLevel.Error, $"Import PDK failed: {ex.Message}");
            return;
        }

        ImportedPdks.Add(report);

        live.Update($"Import PDK — {report.KitName}: installing its parts…", indeterminate: true);
        var outcome = await InstallPdkIntoPaletteAsync(report);

        // Looked for BEFORE the report dialog opens, not after it closes, and that ordering is the
        // whole fix for what this felt like. The scan walks the kit again and can take a while; run
        // after the dialog it left the user looking at a dismissed report and nothing happening,
        // followed by a question arriving out of nowhere. Asked as the row's last stage, the answer
        // is already in hand when the report closes and the offer follows it immediately.
        live.Update($"Import PDK — {report.KitName}: looking for process data…", indeterminate: true);
        var techScan = await ScanForKitTechnologyAsync(report);

        // A count of what was and was not recognised is a TALLY, not a verdict — a vendor kit
        // routinely carries far more than circuitRF reads, so a warning here fires on a completely
        // successful import and trains the user to ignore the level. Only a genuinely failed
        // import (nothing usable, or an unreadable path) escalates.
        var level = report.Status is PdkImportStatus.NotRecognized or PdkImportStatus.Failed
            ? MessageLevel.Error
            : MessageLevel.Info;
        live.Complete(level,
            $"Import PDK — {report.KitName}: " +
            $"{PdkPartInstaller.Plural(report.Parts.Count, "part", "parts")}, " +
            $"{PdkPartInstaller.Plural(report.Supported.Count(), "file", "files")} read, " +
            $"{report.KnownGaps.Count()} recognised but unsupported, " +
            $"{report.Unrecognized.Count()} unrecognised.");

        ReportPdkPlaceability(report, outcome);

        await PdkImportReportDialog.ShowAsync(window, report);
        await OfferTechnologyFromKitAsync(window, report, techScan);
    }

    /// <summary>
    /// Unpacks a kit archive into the workspace and returns the kit's folder, or null when it could
    /// not be unpacked or the user declined to replace what was already there.
    /// </summary>
    private async Task<string?> UnpackKitArchiveAsync(Window window, string archivePath)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Error(
                "Open or create a workspace before importing a kit archive — the kit is unpacked " +
                $"into it, under '{KitArchive.KitsFolderName}/'.");
            return null;
        }

        string workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string destination  = KitArchive.DestinationFor(archivePath, workspaceDir);

        // Asked, never assumed: an unpacked kit is ordinary files in the workspace, and someone may
        // have edited them — a manifest written by hand is exactly the kind of thing that lives there.
        bool overwrite = false;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var answer = await new SaveChangesDialog(
                $"'{Path.GetFileName(destination)}' is already unpacked in this workspace.\n\n" +
                "Replace it with the contents of this archive? Anything edited in that folder is lost.",
                saveLabel:     "Replace",
                dontSaveLabel: null,
                cancelLabel:   "Cancel",
                title:         "Import PDK").ShowDialog<SaveChangesResult>(window);

            if (answer != SaveChangesResult.Save) return null;
            overwrite = true;
        }

        var live = Messages.BeginProgress($"Import PDK — unpacking {Path.GetFileName(archivePath)}…");
        live.Update("Import PDK — unpacking the archive…", indeterminate: true);
        try
        {
            string kitDir = await Task.Run(
                () => KitArchive.ExtractInto(archivePath, workspaceDir, overwrite));
            live.Complete(MessageLevel.Info,
                $"Import PDK — unpacked '{Path.GetFileName(archivePath)}' into " +
                $"{WorkspaceRefs.ToStoredRef(kitDir, workspaceDir)}.");
            return kitDir;
        }
        catch (Exception ex)
        {
            live.Complete(MessageLevel.Error, $"Import PDK — the archive could not be unpacked: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The process data <paramref name="report"/>'s kit carries, or null when there is none to look
    /// at — an archive, a kit whose import saw no layer data, or a scan that would not run.
    ///
    /// <para>Separated from the offer so the WALK can happen while a progress row is up and the
    /// QUESTION can happen the moment the user is ready for it. They used to be one call, which is
    /// why the question arrived after a silent pause.</para>
    /// </summary>
    private static async Task<TechnologyScanResult?> ScanForKitTechnologyAsync(PdkImportReport report)
    {
        if (report.LayerTechnology is null) return null;
        if (string.IsNullOrEmpty(report.RootPath) || !Directory.Exists(report.RootPath)) return null;

        try   { return await Task.Run(() => ProcessTechnologyImport.Scan(report.RootPath)); }
        catch { return null; }   // the kit loaded; a failed look for something extra must not undo that
    }

    /// <summary>
    /// A kit that carries process data can build a technology; offer it as part of the import.
    ///
    /// <para><b>Why offering matters rather than just reporting.</b> The import already SAID it found
    /// layer technology, and stopped there — leaving the user to know that File ▸ Import ▸ Technology
    /// exists, that it applies to the folder they just imported, and to go and do it. Everything the
    /// two halves of a kit are for — placing a part and drawing its artwork on that process's own
    /// layers — needs both, so the second half is offered where the first one finished.</para>
    ///
    /// <para>Asked rather than done: a technology is workspace-scoped configuration, may merge into
    /// one already there, and the user may be importing a kit purely for its schematic parts.</para>
    /// </summary>
    /// <param name="alreadyScanned">
    /// What <see cref="ScanForKitTechnologyAsync"/> already found, when the caller ran the scan under
    /// its own progress row. Null means "look now" — the shape the Manage PDKs door still uses.
    /// </param>
    private async Task OfferTechnologyFromKitAsync(
        Window window, PdkImportReport report, TechnologyScanResult? alreadyScanned = null)
    {
        // An archive has no folder to scan, and a kit whose import saw no layer data has nothing to
        // offer. This is a cheap pre-filter on a fact the IMPORT already established; the Manage PDKs
        // door has no report to consult and goes straight to the scan below, which is the real test.
        if (report.LayerTechnology is null) return;
        await OfferTechnologyFromKitAsync(window, report.KitName, report.RootPath, alreadyScanned);
    }

    /// <summary>
    /// The one implementation behind both doors into a kit — File ▸ Import ▸ PDK and Manage PDKs ▸
    /// Add. They put the same kit into the same workspace, so they must offer the same things; the
    /// Add door offered only the parts, which left a repaired or newly-referenced kit with no
    /// technology and nothing to say one was available.
    /// </summary>
    private async Task OfferTechnologyFromKitAsync(
        Window window, string kitName, string? rootPath, TechnologyScanResult? alreadyScanned = null)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return;

        if (CurrentWorkspacePath is null)
        {
            Messages.Info(
                $"\"{kitName}\" also carries process data. Open or create a workspace, then " +
                "use File ▸ Import ▸ Technology on the kit's folder to build a technology from it.");
            return;
        }

        TechnologyScanResult scan;
        if (alreadyScanned is { } done) scan = done;
        else
        {
            try   { scan = await Task.Run(() => ProcessTechnologyImport.Scan(rootPath)); }
            catch { return; }   // the kit loaded; a failed look for something extra must not undo that
        }

        // Silent when there is nothing there at all. A kit with no process data is the ordinary case
        // on this door (a model-library package, for one), and a line about it on every Add would be
        // noise. The message below fires only for a kit that HAS layer data and still cannot be built
        // from, which is the case worth explaining.
        if (!scan.HasStack)
        {
            if (scan.LayerTables.Count > 0)
                Messages.Info(
                    $"\"{kitName}\" carries layer data but no process stack description, so a " +
                    "technology cannot be built from it automatically.");
            return;
        }

        string what = scan.HasRuleDeck
            ? "a process stack, layer table and design-rule deck"
            : "a process stack";

        var answer = await new SaveChangesDialog(
            $"\"{kitName}\" also carries {what}.\n\n" +
            "Build a technology from it now? Layer names, colours, the stackup and any readable " +
            "design rules come across, and layouts in this workspace can then use them.",
            saveLabel:     "Build Technology…",
            dontSaveLabel: null,
            cancelLabel:   "Not Now",
            title:         "Import PDK").ShowDialog<SaveChangesResult>(window);

        if (answer != SaveChangesResult.Save) return;

        // Handed on, so the technology dialog opens on the scan already in hand rather than repeating
        // the walk the offer was made from. Three scans of the same tree used to run for one import.
        await RunTechnologyImportAsync(window, rootPath, scan);
    }

    /// <summary>Kits imported this session, newest last. The component palette reads this.</summary>
    public ObservableCollection<PdkImportReport> ImportedPdks { get; } = [];

    /// <summary>Palette entries contributed by every kit imported this session, in import order.</summary>
    private readonly List<PaletteItem> _pdkPaletteItems = [];

    /// <summary>
    /// Registers external device providers found in the plug-in folders, once at startup.
    ///
    /// <para>A kit part simulates through a provider; without one it places and netlists but cannot
    /// be evaluated. Providers are plug-ins because they are bound to whoever supplies the device
    /// model — circuitRF ships the seam, not the model.</para>
    ///
    /// <para>Silent when nothing is installed: an empty plug-in folder is the normal case, and a
    /// startup message about it every launch would be noise. Failures ARE reported — a provider
    /// that quietly fails to load resurfaces much later as an incomprehensible
    /// "provider not available" mid-simulation.</para>
    /// </summary>
    /// <summary>
    /// Repopulates the Library palette from kits already installed in the newly-opened workspace.
    /// Replaces whatever the previous workspace contributed — kit parts belong to the workspace
    /// their cells live in, not to the session.
    /// </summary>
    private void RestoreInstalledPdks()
    {
        _pdkPaletteItems.Clear();
        PdkKitRegistry.Clear();     // kit references belong to the workspace that named them
        KitLayoutGenerators.Clear();
        _kitManifests.Clear();
        // Generated wBond symbols are keyed by absolute path, so they are not workspace-scoped the
        // way a kit reference is — but a stale entry would survive a workspace's files being edited
        // outside circuitRF, and this is the one moment the whole session's assumptions are already
        // being rebuilt.
        WBondSymbolProvider.InvalidateAll();
        // A peeked SPICE file is keyed by mtime, so an edit made OUTSIDE circuitRF is already picked
        // up on the next read; this drops the entries for files the departing workspace named, which
        // will not be asked for again.
        SpiceModelPeek.InvalidateAll();

        string? wsRoot = CurrentWorkspacePath is null
            ? null
            : Path.GetDirectoryName(CurrentWorkspacePath);

        var broken = new List<string>();

        if (wsRoot is not null)
        {
            // Start a fresh log for THIS open, so it describes this load rather than accumulating
            // every one before it.
            PdkLoadLog.Begin(wsRoot);

            var refs = TryLoadCws(CurrentWorkspacePath!).PdkRefs ?? [];

            // Library packages are resolved FIRST: a part kit's devices are matched against them, so
            // loading a kit before knowing where the models are would settle "no library found" and
            // record it.
            var libraryRoots = PdkReferenceManager.LibraryRootsIn(wsRoot, refs);

            foreach (var r in refs.Where(x => x.IsLibraryOnly))
                if (!Directory.Exists(WorkspaceRefs.Resolve(r.Path, wsRoot)))
                {
                    PdkLoadLog.Record(wsRoot, r.Provider, "the model-library folder does not exist.");
                    broken.Add(r.Provider);
                }

            // READ on worker threads, APPLY here. Reading a kit is a few hundred milliseconds of
            // file enumeration and netlist parsing (measured at ~390 ms on a real kit: ~120 ms
            // scanning 1,266 files, ~170 ms discovering parts), and none of it needs the UI thread —
            // it is a pure function of what is on disk. The apply half, which is everything that
            // touches PdkKitRegistry, the palette and the workspace's own files, stays on this
            // thread and in reference order, so nothing about WHAT is loaded or in what order
            // changes.
            //
            // Started together rather than one after another: a workspace referencing several kits
            // now reads them at once instead of paying for each in turn. With a single kit this
            // moves the work off the UI thread's stack without shortening it.
            var kits = refs.Where(x => !x.IsLibraryOnly).ToList();
            var reads = kits
                .Select(r => Task.Run(() => ReadReferencedKit(r, wsRoot, libraryRoots)))
                .ToArray();

            for (int i = 0; i < kits.Count; i++)
            {
                var r = kits[i];
                try
                {
                    // GetAwaiter().GetResult() rather than .Result so a kit that threw surfaces its
                    // OWN exception to the catch below — the message goes in the load log, and
                    // "one or more errors occurred" is not a message about anything.
                    var read = reads[i].GetAwaiter().GetResult();
                    if (ApplyReferencedKit(r, wsRoot, read) is not { } loaded) { broken.Add(r.Provider); continue; }
                    _pdkPaletteItems.AddRange(loaded);
                }
                catch (Exception ex)
                {
                    // A kit that cannot be read never stops a workspace from opening; the design is
                    // the user's data and a missing dependency degrades rather than denies.
                    PdkLoadLog.Record(wsRoot, r.Provider, ex.Message);
                    broken.Add(r.Provider);
                }
            }
        }

        // ONE summary per open, never one per part: a kit with forty parts must not produce forty
        // warnings. Silent when everything resolved — an open that reports its own success trains
        // the user to ignore the level.
        if (broken.Count > 0)
            Messages.Warning(
                $"{broken.Count} referenced PDK(s) could not be loaded ({string.Join(", ", broken)}). " +
                $"Parts placed from them show as unresolved until the reference is repaired in " +
                $"File ▸ Manage PDKs.",
                PdkLoadLog.PathFor(wsRoot!));

        PublishKitPaletteItems();

        RegisterKitProviderResolver(wsRoot);
        RefreshCornerAxes(wsRoot);

        // The PCell resolver scans the workspace when the workspace PATH changes, which is before the
        // kits are read — so a declaration just written for a kit is invisible to it until it looks
        // again. Only when something was actually written: a rescan on every open would re-ask the
        // trust question and re-pay the scan for nothing.
        if (_pcellDeclarationsAdded)
        {
            _pcellDeclarationsAdded = false;
            ReloadPCellGenerators();
        }
    }


    /// <summary>
    /// The corner choices this workspace's referenced kits offer. Empty when no kit declares any —
    /// which is the ordinary case, and is what keeps the Corners block out of the Analyses panel
    /// entirely for every user who will never need one.
    ///
    /// <para>Read from what <c>.cws</c> recorded, so this costs no kit read. Rebuilt whenever the
    /// referenced kits are.</para>
    /// </summary>
    public IReadOnlyList<WorkspaceCornerAxis> AvailableCornerAxes { get; private set; } = [];

    private void RefreshCornerAxes(string? workspaceRootDir)
    {
        AvailableCornerAxes = workspaceRootDir is null || CurrentWorkspacePath is null
            ? []
            : WorkspaceCorners.From(workspaceRootDir, TryLoadCws(CurrentWorkspacePath).PdkRefs);

        _factory.AnalysesTool?.SetCornerAxes(AvailableCornerAxes);
    }

    /// <summary>
    /// Learns which parametric cells each kit offers, then republishes the palette.
    ///
    /// <para>Listing may START a kit's interpreter (a script's own <c>describe</c> is the only source
    /// of its generator list), so it runs off the UI thread and publishes back on it.</para>
    ///
    /// <para><b>Every path that can change what the resolver would answer calls this</b> — the
    /// workspace opening, a kit declaring its cell library for the first time, a reload, and consent
    /// being granted. It used to be reachable only from the workspace-path change, so a kit imported
    /// into an already-open workspace never got its layout cells into
    /// <see cref="KitLayoutGenerators"/>: its parts placed on a schematic, and every one of them was
    /// then reported as having no artwork until the workspace was closed and reopened.</para>
    /// </summary>
    private void RefreshPCellPaletteItems()
    {
        var resolver = _pcellResolver;
        if (resolver is null) return;

        // Which refresh this is. A slower earlier pass must not land on top of a later one and
        // republish what the resolver said before a rescan — the symptom would be a palette and a
        // generator map describing a kit that has since been reloaded.
        int generation = ++_pcellRefreshGeneration;

        _ = Task.Run(() =>
        {
            if (CollectPCellGeneratorInfo(resolver, out var byKit, out var models, out var declared,
                                          out string? problem) is false)
            {
                if (problem is not null)
                    Dispatcher.UIThread.Post(() => Messages.Warning(problem));
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _pcellRefreshGeneration) return;
                ApplyPCellGeneratorInfo(byKit, models, declared);
            });
        });
    }

    /// <summary>Counts the refreshes started, so a superseded one can be discarded when it lands.</summary>
    private int _pcellRefreshGeneration;

    /// <summary>
    /// Asks <paramref name="resolver"/> what its kits offer. Does no UI work of its own, so the same
    /// reading serves the background refresh and the synchronous fallback below.
    /// </summary>
    private static bool CollectPCellGeneratorInfo(
        CircuitRF.Ui.Layout.PCells.Wire.PCellWorkerResolver resolver,
        out IReadOnlyDictionary<string, string> byKit,
        out Dictionary<string, string> models,
        out Dictionary<string, IReadOnlyList<string>> declaredParams,
        out string? problem)
    {
        models         = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        declaredParams = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        problem        = null;

        try { byKit = resolver.KitNameByGeneratorId; }
        catch (Exception ex)
        {
            byKit   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            problem = $"A kit's parametric cells could not be listed for the palette: {ex.Message}";
            return false;
        }

        var builtIn = new HashSet<string>(
            CircuitRF.Ui.Layout.PCells.PCellRegistry.KnownGeneratorIds, StringComparer.OrdinalIgnoreCase);

        // A model name a generator does not declare is simply absent — most do not, and the match
        // step that reads this is defined to do nothing without one.
        foreach (var gid in byKit.Keys)
        {
            if (builtIn.Contains(gid)) continue;
            try
            {
                if (resolver.DeclaredDefaults(gid) is not { } d) continue;

                // What each cell ACCEPTS — read from the SAME declaration, so it costs nothing
                // beyond the describe already being paid for. KitPaletteMerge's fourth step needs
                // it; see there.
                declaredParams[gid] = [.. d.Keys];

                if (d.FirstOrDefault(kv => string.Equals(kv.Key, "model", StringComparison.OrdinalIgnoreCase))
                       is { Key: not null } hit
                    && hit.Value.ToString() is { Length: > 0 } raw)
                {
                    // A declared default is a kinded value; the model name is its text.
                    int colon = raw.IndexOf(':');
                    models[gid] = (colon >= 0 ? raw[(colon + 1)..] : raw).Trim();
                }
            }
            catch { /* one generator that will not describe itself must not cost the others */ }
        }

        return true;
    }

    /// <summary>Records what a reading found and republishes. UI thread.</summary>
    private void ApplyPCellGeneratorInfo(
        IReadOnlyDictionary<string, string> byKit,
        IReadOnlyDictionary<string, string> models,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredParams)
    {
        var builtIn = new HashSet<string>(
            CircuitRF.Ui.Layout.PCells.PCellRegistry.KnownGeneratorIds, StringComparer.OrdinalIgnoreCase);

        _pcellGeneratorKits.Clear();
        _pcellGeneratorModels.Clear();
        _pcellGeneratorParameters.Clear();
        foreach (var kv in byKit)
            if (!builtIn.Contains(kv.Key))
                _pcellGeneratorKits[kv.Key] = kv.Value;
        foreach (var kv in models)
            _pcellGeneratorModels[kv.Key] = kv.Value;
        foreach (var kv in declaredParams)
            _pcellGeneratorParameters[kv.Key] = kv.Value;

        PublishKitPaletteItems();
    }

    /// <summary>
    /// The last-resort reading, taken on the thread that asked. Installed as
    /// <see cref="KitLayoutGenerators.SetRefresher"/>'s hook, so a part being resolved against a map
    /// that is still empty gets an answer instead of "this kit says nothing about its layout cells".
    ///
    /// <para><b>Costs nothing once the background pass has landed</b> — it returns immediately when
    /// the generator map is already populated, which is the ordinary case. It is only reached in the
    /// window between a kit being declared and its interpreter having answered, and starting one here
    /// is no more than the <c>PCellRegistry.TryGet</c> immediately after it would do anyway.</para>
    /// </summary>
    private bool RefreshPCellGeneratorsNow()
    {
        if (_pcellGeneratorKits.Count > 0) return false;
        if (_pcellResolver is not { } resolver) return false;

        if (!CollectPCellGeneratorInfo(resolver, out var byKit, out var models, out var declared, out _))
            return false;
        if (byKit.Count == 0) return false;

        // Counts as a refresh, so a background pass started BEFORE this one is discarded when it
        // lands rather than replacing a newer reading with an older one.
        _pcellRefreshGeneration++;
        ApplyPCellGeneratorInfo(byKit, models, declared);
        return true;
    }

    /// <summary>Publishes the kit section of the palette — one tile per part, carrying every view
    /// that part can be placed as. The matching rules live in <see cref="KitPaletteMerge"/>, which is
    /// framework-free and therefore testable on its own.</summary>
    private void PublishKitPaletteItems()
    {
        var composed = KitPaletteMerge.Compose(
            _pdkPaletteItems, _pcellGeneratorKits, _pcellGeneratorModels, _pcellGeneratorParameters);
        // The same answer, published for Update-Layout-from-Schematic, so a part that places one view
        // from its tile places the other from the design.
        KitLayoutGenerators.Publish(composed);
        _factory.PaletteTool?.SetPdkParts(composed);
    }

    /// <summary>Each kit generator's own declared model name, when it declares one — the identity a
    /// kit's schematic part and its layout cell share. See <see cref="KitPaletteMerge.Compose"/>.</summary>
    private readonly Dictionary<string, string> _pcellGeneratorModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each kit generator's own declared parameter names — the tie-break for a model claimed
    /// by more than one cell. See <see cref="KitPaletteMerge.Compose"/>.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _pcellGeneratorParameters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kit-contributed generator ids and the kit each came from. Kept apart from
    /// <see cref="_pdkPaletteItems"/> so a kit re-import rebuilds one without discarding the other,
    /// and merged into one tile per part at publish time.</summary>
    private readonly Dictionary<string, string> _pcellGeneratorKits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settled settings per loaded kit, for the provider resolver.</summary>
    private readonly List<(string Kit, DeviceWorkerManifest Manifest)> _kitManifests = [];

    /// <summary>
    /// Records a kit in <c>.cws</c>: where it is, and what circuitRF settled about it. Never its
    /// translated content — that is the vendor's, and is rebuilt on every open.
    ///
    /// <para>Written immediately rather than at the next save, so a workspace that is closed without
    /// saving still remembers a kit that was just imported — the import is not an edit to a document
    /// the user might reasonably discard.</para>
    /// </summary>
    private void RecordPdkReference(string workspaceRootDir, string kitName, string kitPath,
                                    JsonNode? settings, IReadOnlyList<PdkCornerAxis>? corners)
    {
        try
        {
            var cws  = TryLoadCws(CurrentWorkspacePath!);
            var refs = cws.PdkRefs ?? [];
            // The corners this kit already had recorded, so a re-record that has not re-derived them
            // (a settings-only write on open) never silently drops them.
            var keptCorners = refs.FirstOrDefault(r =>
                string.Equals(r.Provider, kitName, StringComparison.OrdinalIgnoreCase))?.Corners;
            refs.RemoveAll(r => string.Equals(r.Provider, kitName, StringComparison.OrdinalIgnoreCase));
            refs.Add(new CwsPdkRef
            {
                Path               = WorkspaceRefs.ToStoredRef(kitPath, workspaceRootDir),
                Provider           = kitName,
                TranslationVersion = DsnSymbolReader.TranslationVersion,
                Settings           = settings,
                Corners            = PdkReferenceManager.ToStoredCorners(corners) ?? keptCorners,
            });
            cws.PdkRefs = refs;
            WorkspacePersistence.SaveToFileAtomic(CurrentWorkspacePath!, cws);
        }
        catch (Exception ex)
        {
            Messages.Warning($"Import PDK — the kit was loaded but could not be recorded in this " +
                             $"workspace, so it will not be there next time: {ex.Message}");
        }
    }

    /// <summary>
    /// What reading one referenced kit off disk produced, before any of it has been applied.
    ///
    /// <para><see cref="Refusal"/> non-null means the kit was found but not read, and says why in the
    /// user's own terms; <see cref="Missing"/> means there was nothing there to read. Both are
    /// carried as values rather than acted on inside the read, because the read runs on a worker
    /// thread and reporting is the apply half's job.</para>
    /// </summary>
    private readonly record struct KitRead(
        string KitPath,
        PdkImportReport? Report,
        PdkPartInstaller.InstallOutcome? Outcome,
        string? Refusal,
        bool Missing);

    /// <summary>
    /// Reads one referenced kit off disk. <b>Runs on a worker thread</b> — a few hundred milliseconds
    /// of file enumeration and netlist parsing, and a pure function of what is on disk.
    ///
    /// <para>Touches no view-model, registry or palette state, posts no message and writes no file:
    /// everything that does is <see cref="ApplyReferencedKit"/>, on the UI thread. <b>Keep it that
    /// way</b> — it is the only reason reading several kits at once is safe.</para>
    ///
    /// <para>The recorded settings are handed back in, so the library discovery and variant choices
    /// are READ rather than re-derived — which is what keeps an open both fast and repeatable.</para>
    /// </summary>
    private static KitRead ReadReferencedKit(
        CwsPdkRef r, string workspaceRootDir, IReadOnlyList<string> libraryRoots)
    {
        string kitPath = WorkspaceRefs.Resolve(r.Path, workspaceRootDir);
        if (!Directory.Exists(kitPath))
            return new KitRead(kitPath, null, null, null, Missing: true);

        // A reader change moves pins, and wires attached to them silently disconnect. Refused and
        // reported rather than applied — the upgrade is the user's to ask for.
        if (r.TranslationVersion != 0 && r.TranslationVersion != DsnSymbolReader.TranslationVersion)
            return new KitRead(kitPath, null, null,
                $"'{r.Provider}' was translated by an older reader (version {r.TranslationVersion}; " +
                $"this build uses {DsnSymbolReader.TranslationVersion}). It was NOT re-translated: " +
                $"pin positions could move and disconnect wires. Re-import it from File ▸ Manage PDKs " +
                $"when you are ready.",
                Missing: false);

        var report  = PdkImporter.Import(kitPath);
        var outcome = PdkPartInstaller.Install(report, r.Settings, libraryRoots);
        return new KitRead(kitPath, report, outcome, null, Missing: false);
    }

    /// <summary>
    /// Applies what <see cref="ReadReferencedKit"/> read. Returns the kit's palette entries, or null
    /// when it could not be reached or was refused.
    ///
    /// <para><b>UI thread.</b> This is the half that mutates the kit registry, records derived
    /// settings back into <c>.cws</c>, declares the kit's own cell library and posts messages — and
    /// it still runs one kit at a time, in reference order, exactly as it did while the read was
    /// inline. Only WHERE the reading happens changed.</para>
    /// </summary>
    private IReadOnlyList<PaletteItem>? ApplyReferencedKit(
        CwsPdkRef r, string workspaceRootDir, KitRead read)
    {
        string kitPath = read.KitPath;

        if (read.Missing)
        {
            PdkLoadLog.Record(workspaceRootDir, r.Provider, $"the kit folder '{kitPath}' does not exist.");
            return null;
        }

        if (read.Refusal is { } refusal)
        {
            Messages.Warning(refusal);
            return null;
        }

        var report  = read.Report!;
        var outcome = read.Outcome!;

        PdkKitRegistry.SetKit(r.Provider, outcome.Parts ?? [], outcome.OsdiModels);

        // From what the install SETTLED, never from what was recorded. They differ exactly when the
        // recorded settings were absent (or stale) and had to be derived — which is the case that
        // matters, because building the manifest from the null then leaves the resolver with nothing
        // and every kit part fails at Run with "no kit settled on a way to evaluate its devices".
        if (PdkPartInstaller.ManifestFrom(outcome.Settings, kitPath, r.Provider) is { } m)
            _kitManifests.Add((r.Provider, m));

        // Derived settings are recorded so the NEXT open replays them instead of working them out
        // again. Measured: ~0.5 ms replayed against ~200 ms derived, because discovery byte-scans
        // candidate builds across a multi-MB package. Only written when it actually changed, so an
        // ordinary open touches nothing.
        //
        // The corners are recorded on the same terms and for the same reason: learning them means
        // reading every netlist in the kit, and the answer only changes when the kit does.
        // "New" means DIFFERENT, not merely absent. Settings are also re-derived when the recorded
        // ones no longer resolve here — a workspace opened on another operating system, where the
        // entry that applies names paths belonging to the machine that wrote it. Recording only the
        // absent case would leave that workspace deriving again on every single open, and still
        // carrying the settings that do not work.
        bool settingsAreNew = outcome.Settings is not null
                              && (r.Settings is null || !JsonNode.DeepEquals(outcome.Settings, r.Settings));
        bool cornersAreNew  = (r.Corners is null || r.Corners.Count == 0)
                              && outcome.CornerAxes is { Count: > 0 };
        if (settingsAreNew || cornersAreNew)
            RecordPdkReference(workspaceRootDir, r.Provider, kitPath,
                               outcome.Settings ?? r.Settings, outcome.CornerAxes);

        DeclareKitPCellLibrary(r.Provider, kitPath, workspaceRootDir);

        return outcome.Items;
    }

    /// <summary>
    /// Set when a kit's parametric-cell library was declared for the first time during this load, so
    /// the resolver — which scanned the workspace BEFORE the kits were read — is told to look again.
    /// </summary>
    private bool _pcellDeclarationsAdded;

    /// <summary>
    /// Makes a kit's own parametric cells reachable, if it has any.
    ///
    /// <para><b>The gap this closes.</b> circuitRF could already RUN a kit's cell scripts; the only way
    /// to declare one was a <c>pcell-generators.json</c> written by hand, and a vendor kit knows nothing
    /// about circuitRF and ships none — so an imported kit's layout artwork was unreachable however
    /// complete its own cell library was. The library is found structurally (see
    /// <see cref="KitPCellLibrary"/>) and the declaration is written into the WORKSPACE, where it is
    /// ordinary, editable text rather than a decision buried in the product.</para>
    ///
    /// <para>Silent when the kit has no cell library — the ordinary case for a kit whose layouts are
    /// fixed artwork — and never fatal: a declaration that could not be written costs that kit's
    /// layout cells, not the import.</para>
    /// </summary>
    private void DeclareKitPCellLibrary(string kitName, string kitPath, string workspaceRootDir)
    {
        try
        {
            var pkg = CircuitRF.Ui.Layout.PCells.Wire.KitPCellLibrary.Find(kitPath, out var alsoFound);
            if (pkg is null) return;

            // kitPath is passed so the declaration is anchored on the kit rather than written out as
            // an absolute path. This is what makes repairing a moved kit in Manage PDKs repair its
            // layout cells too — the parts and the artwork now follow ONE recorded location.
            string? dir = CircuitRF.Ui.Layout.PCells.Wire.KitPCellLibrary.EnsureDeclared(
                workspaceRootDir, kitName, pkg, out string? problem, out bool created,
                kitRoot: kitPath);

            if (dir is null)
            {
                Messages.Warning(
                    $"'{kitName}' ships a parametric-cell library ({pkg.PackageName}), but circuitRF " +
                    $"could not record it: {problem} Its parts still place and draw; only their layout " +
                    $"artwork needs it.");
                return;
            }

            if (!created) return;   // already declared — nothing changed, so nothing to say

            _pcellDeclarationsAdded = true;

            string extra = alsoFound.Count > 0
                ? $" It also holds {alsoFound.Count} other cell package(s) " +
                  $"({string.Join(", ", alsoFound.Take(3).Select(p => p.PackageName))}" +
                  $"{(alsoFound.Count > 3 ? ", …" : "")}); edit that folder to register one instead."
                : "";

            Messages.Success(
                $"'{kitName}' offers {pkg.CellModuleCount} parametric layout cell(s) " +
                $"({pkg.PackageName}). circuitRF recorded how to run them in " +
                $"'{Path.GetFileName(dir)}'.{extra}",
                dir);
        }
        catch (Exception ex)
        {
            Messages.Warning($"'{kitName}': its parametric-cell library could not be examined: {ex.Message}");
        }
    }

    /// <summary>
    /// Points device-provider resolution at this workspace's own installed kits, so a kit part can
    /// be simulated with nothing configured: import, place, Run.
    ///
    /// <para>Registering a RESOLVER rather than providers means no worker process starts here. One
    /// starts the first time a design actually asks for that kit's devices — a workspace may hold
    /// many kits, and any one design typically uses none of them.</para>
    ///
    /// <para>Called from <see cref="RestoreInstalledPdks"/>, which already runs at every point the
    /// palette is re-wired to a workspace. That is the same set of moments the kit folder changes,
    /// so the two cannot drift apart.</para>
    /// </summary>
    private void RegisterKitProviderResolver(string? workspaceRootDir)
    {
        // Ends any worker started for the workspace being left behind. Providers registered by the
        // application itself (plug-in assemblies) are deliberately not touched.
        ExternalDeviceRegistry.ResetResolved();

        if (string.IsNullOrWhiteSpace(workspaceRootDir)) return;

        try
        {
            // From the settings the workspace recorded, not by searching folders — there is no
            // folder to search any more. A resolver rather than a registered provider, so opening a
            // workspace starts no worker processes and a kit the design never uses is never launched.
            ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver(_kitManifests));
        }
        catch (Exception ex)
        {
            Messages.Warning($"Imported kits could not be made available for simulation: {ex.Message}");
        }
    }

    private void LoadExternalDeviceProviders()
    {
        ExternalProviderLoader.LoadReport report;
        try
        {
            report = ExternalProviderLoader.LoadDefaults();
        }
        catch (Exception ex)
        {
            Messages.Warning($"Device providers could not be loaded: {ex.Message}");
            return;
        }

        if (report.LoadedAnything)
            Messages.Info($"Device provider(s) registered: {string.Join(", ", report.Registered)}.");

        foreach (var d in report.Diagnostics)
            Messages.Warning($"Device provider — {d}");
    }

    /// <summary>
    /// Installs a just-imported kit's parts into the Library Palette: its symbols become cells the
    /// workspace can place, and its own browser icons become the palette tiles.
    ///
    /// <para>Re-importing a kit REPLACES its previous entries rather than adding a second copy —
    /// keyed on kit name, which is what a user re-importing after fixing something expects.</para>
    /// </summary>
    /// <summary>
    /// <see cref="InstallPdkIntoPalette"/> with the READ half on a worker thread.
    ///
    /// <para>Installing is not the cheap end of an import: it reads the kit's symbols, and it starts
    /// one worker process per compiled model to ask what each implements. On the UI thread that is a
    /// frozen window for as long as it takes, right after a file picker closed — which is the part of
    /// an import that felt like nothing was happening. Everything that touches registries, the
    /// palette or the workspace's files still runs here, after the await.</para>
    /// </summary>
    private async Task<PdkPartInstaller.InstallOutcome?> InstallPdkIntoPaletteAsync(PdkImportReport report)
    {
        var roots = WorkspaceLibraryRoots();

        PdkPartInstaller.InstallOutcome outcome;
        try
        {
            outcome = await Task.Run(() => PdkPartInstaller.Install(report, libraryRoots: roots));
        }
        catch (Exception ex)
        {
            Messages.Warning($"Import PDK — the palette could not be updated: {ex.Message}");
            return null;
        }

        return ApplyInstalledPdk(report, outcome);
    }

    private PdkPartInstaller.InstallOutcome? InstallPdkIntoPalette(PdkImportReport report)
    {
        PdkPartInstaller.InstallOutcome outcome;
        try
        {
            outcome = PdkPartInstaller.Install(report, libraryRoots: WorkspaceLibraryRoots());
        }
        catch (Exception ex)
        {
            Messages.Warning($"Import PDK — the palette could not be updated: {ex.Message}");
            return null;
        }

        return ApplyInstalledPdk(report, outcome);
    }

    /// <summary>The UI-thread half of an install: registry, palette, and what the workspace records.</summary>
    private PdkPartInstaller.InstallOutcome? ApplyInstalledPdk(
        PdkImportReport report, PdkPartInstaller.InstallOutcome outcome)
    {
        string? wsRoot = CurrentWorkspacePath is null
            ? null
            : Path.GetDirectoryName(CurrentWorkspacePath);

        // Held in memory and referenced by the workspace — nothing is written into it. Re-importing
        // a kit REPLACES what was held for it rather than adding a second copy.
        //
        // Registered under the name the INSTALLER settled on, never the report's own: every part
        // reference was built from that one, and a report carrying no name falls back to a default.
        // Using the report's here would leave every reference pointing at a kit nobody registered.
        string kit = outcome.KitName;

        PdkKitRegistry.SetKit(kit, outcome.Parts ?? [], outcome.OsdiModels);
        _kitManifests.RemoveAll(k => string.Equals(k.Kit, kit, StringComparison.OrdinalIgnoreCase));
        if (PdkPartInstaller.ManifestFrom(outcome.Settings, report.RootPath, kit) is { } m)
            _kitManifests.Add((kit, m));

        if (wsRoot is not null)
        {
            RecordPdkReference(wsRoot, kit, report.RootPath, outcome.Settings, outcome.CornerAxes);

            // The Corners block follows what the workspace references, so a kit that declares
            // corners must make it appear the moment it is imported, not on the next open.
            RefreshCornerAxes(wsRoot);

            // Its layout cells, if it has any. Declared here as well as on a workspace open so the
            // kit is complete the moment it is imported, rather than on the next open.
            _pcellDeclarationsAdded = false;
            DeclareKitPCellLibrary(kit, report.RootPath, wsRoot);
            if (_pcellDeclarationsAdded)
            {
                _pcellDeclarationsAdded = false;
                ReloadPCellGenerators();
            }
        }

        _pdkPaletteItems.RemoveAll(i => string.Equals(i.Pdk?.KitName, kit, StringComparison.Ordinal));
        _pdkPaletteItems.AddRange(outcome.Items);
        PublishKitPaletteItems();
        RegisterKitProviderResolver(wsRoot);

        // What the import worked out is neutral status, not a warning. Reporting a successful
        // discovery at Warning trains the user to ignore the level, which costs them the one line
        // that IS a warning.
        foreach (var n in outcome.Notes ?? [])
            Messages.Info($"Import PDK — {n}");

        foreach (var d in outcome.Diagnostics)
            Messages.Warning($"Import PDK — {d}");

        if (outcome.Items.Count > 0 && wsRoot is null)
            Messages.Warning("Import PDK — no workspace is open, so this kit's symbols were not installed. " +
                             "Open or create a workspace and import again to place its parts.");

        return outcome;
    }

    /// <summary>
    /// The LAST line of an import, and the one that answers the only question that matters: can
    /// anything be placed now?
    ///
    /// <para>Success when at least one part reached the palette, warning when none did — a kit that
    /// read cleanly but yields nothing placeable is exactly the case a tally of file counts hides.
    /// It carries its own reason, so "no parts" never has to be worked out from the lines above.</para>
    /// </summary>
    private void ReportPdkPlaceability(PdkImportReport report, PdkPartInstaller.InstallOutcome? outcome)
    {
        int placeable = outcome?.Items.Count ?? 0;

        if (placeable > 0)
        {
            string omitted = outcome!.OmittedNotPlaceable > 0
                ? $" {outcome.OmittedNotPlaceable} further part(s) have no symbol and are internal to the " +
                  "kit; they are listed in the import report but kept out of the palette."
                : "";
            Messages.Success($"Import PDK — \"{report.KitName}\": {placeable} part(s) available to place " +
                             $"from the Library palette " +
                             $"({PdkPartInstaller.Plural(outcome.IconsFound, "icon", "icons")}, " +
                             $"{outcome.SymbolsInstalled} symbol(s) installed).{omitted}");
            return;
        }

        string reason =
            outcome is null                       ? "the palette could not be updated"
            : CurrentWorkspacePath is null        ? "no workspace is open"
            : outcome.OmittedNotPlaceable > 0     ? "no part has a symbol circuitRF can read"
            : report.Parts.Count == 0             ? "the kit declares no parts"
            :                                       "no part could be installed";
        Messages.Warning($"Import PDK — \"{report.KitName}\": no parts are available to place — {reason}. " +
                         "The import report lists what was found.");
    }

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
                // The 4-layer starter exists only as an authored .ctech — R-misc-6's "one authored
                // representation, not two". There is deliberately no Pcb4Layer() beside
                // Pcb2Layer(): a C# transcription of it would be a second copy to drift.
                NewTechnologyStarter.Pcb4  => ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz"),
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

    /// <summary>
    /// File ▸ New ▸ EM Setup… — creates a <c>.cem</c> beside the layout it analyses (D1/R-em-9:
    /// workspace-scoped, never scratch). <b>R18's 30-second target is reachable because the defaults
    /// are already right, not because the dialogs are fast</b> — a fresh setup arrives at
    /// <c>EmMeshSettings.Default</c>, 50 Ω, and a 1–20 GHz / 101-point sweep, so the only thing the
    /// user must supply is which layout.
    /// </summary>
    [RelayCommand]
    private async Task NewEmSetup(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (CurrentWorkspacePath is null)
        {
            Messages.Info("Open or create a workspace first.");
            return;
        }

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        // Default to the active layout, which is overwhelmingly the one the user means.
        string layoutRef = "";
        if (ResolveActiveDocumentForCommands() is LayoutDocument activeLayout &&
            activeLayout.FilePath is { } lp && !WorkspaceRootFinder.IsOutside(lp, workspaceDir))
            layoutRef = Path.GetRelativePath(workspaceDir, lp).Replace('\\', '/');

        string stem = layoutRef.Length > 0
            ? Path.GetFileNameWithoutExtension(layoutRef)
            : "EmSetup";

        var emDir = Path.Combine(workspaceDir, "em");
        var name  = await new Views.Dialogs.InputNameDialog(
            "New EM Setup", "Name:", NextFreeEmSetupName(emDir, stem)).ShowDialog<string?>(window);
        if (name is null) return;
        if (!NameValidator.IsValid(name))
        {
            Messages.Error($"'{name}' is not a usable name. {NameValidator.Validate(name)}");
            return;
        }

        try
        {
            Directory.CreateDirectory(emDir);
            var setup = new EmSetup { Name = name, LayoutRef = layoutRef };
            var path  = Path.Combine(emDir, name + EmSetupPersistence.Extension);
            if (File.Exists(path))
            {
                Messages.Error($"An EM setup named '{name}' already exists.");
                return;
            }
            EmSetupPersistence.SaveToFile(path, setup);
            _factory.ProjectTreeTool?.Refresh();
            OpenOrActivateEmSetup(path);
            Messages.Success("Created EM setup", path);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create EM setup: {ex.Message}");
        }
    }

    /// <summary>
    /// The Layout Editor's own EM button (owner request, 2026-08-09): <b>one gesture from a layout to
    /// its EM setup.</b> The <c>.cem</c> is named after the layout file — <c>Amp.clay</c> → <c>Amp.cem</c>
    /// — and if that setup already exists it is opened and focused rather than a second one being
    /// created. This is the only EM entry point outside the <c>.cem</c> editor itself; before it, a
    /// user had to know that File ▸ New ▸ EM Setup… existed at all.
    ///
    /// <para><b>Why <c>&lt;workspace&gt;/em/</c> and not beside the <c>.clay</c>.</b> The owner's
    /// naming rule ("remove .clay, add .cem") is about the FILE NAME; the directory is ours to pick,
    /// and beside the layout is the one place it must not go: a cell's <c>layout/</c> sub-folder is
    /// enumerated by <c>WorkspaceScanner.BuildCellNode</c> with <c>"*" + ViewExtension(vt)</c>, i.e.
    /// <c>*.clay</c> only — a <c>.cem</c> written there is INVISIBLE in the project tree. <c>em/</c>
    /// is an ordinary user folder, listed by extension, and is where File ▸ New ▸ EM Setup… already
    /// puts them, so both doors agree.</para>
    /// </summary>
    public void OpenOrCreateEmSetupForLayout(string clayPath)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Info("Open or create a workspace first — an EM setup is workspace-scoped.");
            return;
        }

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        if (WorkspaceRootFinder.IsOutside(clayPath, workspaceDir))
        {
            Messages.Warning("This layout is outside the open workspace, so it has no EM setup here. " +
                             "Save it into the workspace first.");
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(clayPath);
        var    emDir = Path.Combine(workspaceDir, "em");
        var    path  = Path.Combine(emDir, stem + EmSetupPersistence.Extension);

        if (File.Exists(path))
        {
            OpenOrActivateEmSetup(path);
            return;
        }

        try
        {
            Directory.CreateDirectory(emDir);
            var layoutRef = Path.GetRelativePath(workspaceDir, clayPath).Replace('\\', '/');
            EmSetupPersistence.SaveToFile(path, new EmSetup { Name = stem, LayoutRef = layoutRef });
            _factory.ProjectTreeTool?.Refresh();
            OpenOrActivateEmSetup(path);
            Messages.Success("Created EM setup", path);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create EM setup: {ex.Message}");
        }
    }

    private static string NextFreeEmSetupName(string emDir, string stem)
    {
        string candidate = stem;
        for (int n = 2; File.Exists(Path.Combine(emDir, candidate + EmSetupPersistence.Extension)); n++)
            candidate = $"{stem}{n}";
        return candidate;
    }

    /// <summary>
    /// File ▸ Import ▸ Technology… — builds a <c>.ctech</c> from a process kit's own technology files.
    ///
    /// <para>Everything imported comes out of the kit at run time; circuitRF holds no knowledge of any
    /// particular process. What it derives — and, more importantly, everything it could NOT derive
    /// cleanly — is reported to Messages, because a stackup that is silently approximate produces
    /// numbers that converge and are wrong.</para>
    /// </summary>
    /// <summary>
    /// Opens a `.ctech` from anywhere — <b>no workspace required</b>.
    ///
    /// <para>A technology is a portable, self-contained file. Requiring a workspace to look at one
    /// was an accident of the only entry points being the project tree and the import flow, not a
    /// property of the format: <see cref="OpenOrActivateTech"/> has always needed nothing but a
    /// path. Someone sent a `.ctech` and wanting to read its rules should not have to create a
    /// workspace first.</para>
    /// </summary>
    [RelayCommand]
    private async Task OpenTechnologyFile(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Technology",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Technology") { Patterns = ["*.ctech"] }],
        });
        if (files.Count == 0) return;

        OpenOrActivateTech(files[0].Path.LocalPath);
    }

    [RelayCommand]
    private async Task ImportTechnology(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        if (CurrentWorkspacePath is null)
        {
            Messages.Info("Open or create a workspace first — an imported technology is saved into it.");
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Import Technology — choose the folder holding the process data",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;

        await RunTechnologyImportAsync(window, folders[0].Path.LocalPath);
    }

    /// <summary>
    /// Scans <paramref name="root"/> for process data and, if there is enough to build from, takes
    /// the user through choosing and installing a technology.
    ///
    /// <para><b>Shared with the kit import deliberately.</b> A kit that carries process data is the
    /// same job reached from a different door — offering it there and building a second, slightly
    /// different flow is how the two would come to disagree about merging, defaults or reporting.</para>
    /// </summary>
    /// <param name="alreadyScanned">A scan the caller has already run, so one import does not walk
    /// the same tree twice. Null means scan now — the shape File ▸ Import ▸ Technology uses.</param>
    private async Task RunTechnologyImportAsync(
        Window window, string root, TechnologyScanResult? alreadyScanned = null)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Info("Open or create a workspace first — an imported technology is saved into it.");
            return;
        }

        TechnologyScanResult scan;
        if (alreadyScanned is { } done) scan = done;
        else
        {
            var scanning = Messages.BeginProgress($"Import Technology — scanning {Path.GetFileName(root.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}…");
            scanning.Update("Import Technology — scanning for process data…", indeterminate: true);
            try
            {
                scan = await Task.Run(() => ProcessTechnologyImport.Scan(root));
                scanning.Complete(MessageLevel.Info, "Import Technology — scan complete.");
            }
            catch (Exception ex)
            {
                scanning.Complete(MessageLevel.Error, $"Import Technology failed while scanning: {ex.Message}");
                return;
            }
        }

        if (!scan.HasStack)
        {
            foreach (var note in scan.Notes) Messages.Warning($"Import Technology — {note}");
            return;
        }

        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var techDir      = Path.Combine(workspaceDir, "tech");

        var choice = await new ImportTechnologyDialog(
            scan, techDir, root, NextFreeTechName(techDir)).ShowDialog<ImportTechnologyResult?>(window);
        if (choice is null) return;

        // Notes from the SCAN are worth saying even once a choice has been made — "no layer table was
        // found" explains a technology that arrives with an empty layer list.
        foreach (var note in scan.Notes) Messages.Info($"Import Technology — {note}");

        try
        {
            var result = await Task.Run(
                () => ProcessTechnologyImport.Import(
                    choice.StackFilePath, choice.LayerTablePath,
                    choice.RuleDeckPaths, choice.RuleValueTablePaths));

            var tech = result.Technology;
            tech.Name = choice.Name;

            Directory.CreateDirectory(techDir);
            var techPath = Path.Combine(techDir, $"{choice.Name}.ctech");

            // Re-importing over an existing technology used to overwrite the file outright — every
            // hand-authored rule, edited colour and chosen ground reference gone, with no prompt and
            // no record. Ask, and offer the answer that does not lose work.
            if (File.Exists(techPath))
            {
                var existing = TryLoadTechnologyForMerge(techPath);
                if (existing is null) return;

                var conflicts = TechnologyMerge.FindConflicts(
                    existing, tech, TechnologyMerge.SectionsPresentIn(tech));

                var merge = await new TechnologyMergeDialog(
                    Path.GetFileName(techPath), TechnologyMerge.SectionsPresentIn(tech),
                    isReimport: true, conflicts).ShowDialog<TechnologyMergeResult?>(window);
                if (merge is null) return;

                if (merge.ReplaceWholeFile)
                {
                    TechPersistence.SaveToFile(techPath, tech);
                    Messages.Warning($"Replaced \"{choice.Name}\" entirely — any edits made to it are gone.");
                }
                else
                {
                    var report = TechnologyMerge.Merge(
                        existing, tech, merge.Sections, merge.Mode, merge.ReplaceKeys);
                    TechPersistence.SaveToFile(techPath, existing);
                    tech = existing;
                    Messages.Success($"Merged into \"{choice.Name}\" — {report.Summary()}", techPath);
                    foreach (var w in report.Warnings) Messages.Warning($"Import Technology — {w}");
                }

                _techCache.Invalidate(techPath);
            }
            else
            {
                TechPersistence.SaveToFile(techPath, tech);
            }

            if (choice.SetAsDefault)
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

            int conductors = tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor);
            int vias       = tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Via);
            Messages.Success(
                $"Imported technology \"{tech.Name}\" — {tech.Layers.Count} layer(s), {conductors} " +
                $"conductor(s), {vias} via(s), {tech.DrcRules.Count} rule(s).", techPath);

            // Every derivation that had to give, said individually rather than rolled into a count.
            foreach (var note in result.Notes) Messages.Warning($"Import Technology — {note}");

            // The editor's own validation is the last word on whether the result is usable, so it is
            // run here rather than left for the user to discover on the Stackup tab.
            foreach (var problem in TechValidation.Validate(tech))
                Messages.Warning($"Import Technology — {problem}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Import Technology failed: {ex.Message}");
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

    /// <summary>The absolute <c>.ctech</c> path a given <c>.clay</c> resolves to, or null when it
    /// resolves to none. Exposed so the EM setup panel's "Edit technology…" link can reach the ONE
    /// editor for process data (R-em-12) instead of growing a second stackup editor.</summary>
    public string? ResolvedTechPathFor(string clayAbsolutePath)
    {
        LayoutView? view = null;
        foreach (var open in _openDocsByPath.Values.OfType<LayoutDocument>())
            if (open.FilePath is { } fp &&
                string.Equals(Path.GetFullPath(fp), Path.GetFullPath(clayAbsolutePath),
                              StringComparison.OrdinalIgnoreCase))
            { view = open.ViewModel.Model; break; }

        if (view is null)
        {
            if (!File.Exists(clayAbsolutePath)) return null;
            try { view = LayoutPersistence.LoadFromFile(clayAbsolutePath); }
            catch { return null; }
        }
        return ResolveTechFor(view.TechRef, clayAbsolutePath).ResolvedPath;
    }

    /// <summary>Loads a `.ctech` for a merge, reporting rather than throwing.</summary>
    private Technology? TryLoadTechnologyForMerge(string path)
    {
        try { return TechPersistence.LoadFromFile(path); }
        catch (Exception ex)
        {
            Messages.Error($"Could not read \"{Path.GetFileName(path)}\": {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Brings chosen sections of ANOTHER technology into the one currently being edited — the
    /// mix-and-match path. Covers "take just this process's DRC rules" and "reuse that layer table"
    /// with one mechanism, because both are the same operation with a different section selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsTechDocumentActive))]
    private async Task ImportIntoTechnology(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;
        if (ResolveActiveDocumentForCommands() is not TechDocument doc) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import from Technology",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Technology") { Patterns = ["*.ctech"] }],
        });
        if (files.Count == 0) return;

        var source = TryLoadTechnologyForMerge(files[0].Path.LocalPath);
        if (source is null) return;

        var available = TechnologyMerge.SectionsPresentIn(source);
        if (available == TechSection.None)
        {
            Messages.Warning("That technology carries no layers, stackup or rules to import.");
            return;
        }

        var conflicts = TechnologyMerge.FindConflicts(doc.ViewModel.Working, source, available);

        var choice = await new TechnologyMergeDialog(
            Path.GetFileName(files[0].Path.LocalPath), available, isReimport: false, conflicts)
            .ShowDialog<TechnologyMergeResult?>(window);
        if (choice is null) return;

        var report = doc.ViewModel.MergeFrom(source, choice.Sections, choice.Mode, choice.ReplaceKeys);
        Messages.Success($"Import from technology — {report.Summary()}");
        foreach (var w in report.Warnings) Messages.Warning($"Import from technology — {w}");
    }

    /// <summary>
    /// Writes chosen sections of the technology being edited to a new `.ctech` — what "send someone
    /// my DRC rules" does. The result is an ordinary technology file with the other sections empty,
    /// so the receiving side needs no special knowledge of how it was produced.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsTechDocumentActive))]
    private async Task ExportTechnologySections(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;
        if (ResolveActiveDocumentForCommands() is not TechDocument doc) return;

        var tech = doc.ViewModel.Working;
        var available = TechnologyMerge.SectionsPresentIn(tech);
        if (available == TechSection.None)
        {
            Messages.Warning("This technology has nothing to export yet.");
            return;
        }

        var choice = await new TechnologyExportDialog(tech.Name, available)
            .ShowDialog<TechSection?>(window);
        if (choice is null || choice == TechSection.None) return;

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Technology Sections",
            SuggestedFileName = $"{tech.Name}-export.ctech",
            DefaultExtension = "ctech",
            FileTypeChoices = [new FilePickerFileType("Technology") { Patterns = ["*.ctech"] }],
        });
        if (file is null) return;

        try
        {
            var extracted = TechnologyMerge.Extract(tech, choice.Value, $"{tech.Name} (export)");
            TechPersistence.SaveToFile(file.Path.LocalPath, extracted);
            Messages.Success(
                $"Exported {extracted.Layers.Count} layer(s), {extracted.Stackup.Layers.Count} " +
                $"stackup entr(ies), {extracted.DrcRules.Count} rule(s).", file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Export failed: {ex.Message}");
        }
    }

    private bool IsTechDocumentActive() => ResolveActiveDocumentForCommands() is TechDocument;

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

            // A live (unsaved) override — installed by Import Board's layer/stackup recovery, or by a
            // cross-technology paste's "Add to technology" — is what every open LAYOUT is already
            // resolving against. Opening the editor on the on-disk file instead would show the user a
            // technology that nothing in the session is actually using, and would silently discard the
            // recovered layers the moment they saved. It arrives as an ordinary undoable edit, so the
            // tab is visibly dirty and Ctrl+Z backs it out.
            ApplyLiveTechOverrideToEditor(vm, absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open technology: {ex.Message}");
        }
    }

    /// <summary>Pushes the live (unsaved) technology override for <paramref name="absolutePath"/>, if
    /// one is installed, into a freshly-opened editor as an undoable edit. No-op when there is none, or
    /// when it already matches what the editor loaded.</summary>
    private void ApplyLiveTechOverrideToEditor(TechEditorViewModel vm, string absolutePath)
    {
        if (!_techCache.HasLiveOverride(absolutePath)) return;
        if (_techCache.Get(absolutePath) is not { } live) return;

        vm.ReplaceWorkingAsEdit(live, "Unsaved technology changes from this session");
        Messages.Info(
            $"\"{live.Name}\" is showing unsaved changes made earlier in this session " +
            "(a board import, or a paste that added layers). Save to keep them.");
    }

    /// <summary>
    /// Opens (or focuses) a <c>.cem</c> EM setup as an ordinary editor document. Mirrors
    /// <see cref="OpenOrActivateTech"/> exactly — R-em-9: a <c>.cem</c> is workspace-scoped and
    /// never scratch, so there is no materialize path.
    /// </summary>
    public void OpenOrActivateEmSetup(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        try
        {
            var setup = EmSetupPersistence.LoadFromFile(absolutePath);
            if (setup.Name.Length == 0) setup.Name = Path.GetFileNameWithoutExtension(absolutePath);
            var vm = new EmSetupEditorViewModel(absolutePath, setup)
            {
                ResolveLayout = r => ResolveEmLayout(absolutePath, r),
                MakeLayoutRef = abs => MakeEmLayoutRef(absolutePath, abs),
                RunRequested  = RunEmSetupAsync,
                MeshRequested = MeshEmSetupAsync,
                ResultsRootProvider = () => GetResultsRoot(),
            };
            vm.SaveError        += m => Messages.Error(m);
            vm.EmSetupSaved     += p => Messages.Success("Saved", p);
            // R-em-15/17: the mesh "renders automatically in the layout view" (D2), and is dropped
            // there the moment the setup's own state says it is no longer current.
            vm.AnalysisRefreshed += () => PushEmMeshToLayout(vm);
            vm.Refresh();

            var doc = new EmSetupDocument(Path.GetFileName(absolutePath), vm, absolutePath);

            // Save As follows the new file, so the open-document map has to follow with it —
            // otherwise reopening the .cem from the tree would mint a SECOND live view of one file.
            // Subscribed after construction so the document's own OnSavedAs (which retitles) has
            // already run by the time the key moves.
            vm.EmSetupSavedAs += newPath =>
            {
                _openDocsByPath.Remove(absolutePath);
                _openDocsByPath[newPath] = doc;
                _factory.ProjectTreeTool?.Refresh();
                Messages.Success("Saved", newPath);
            };

            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookEmSetupDirty(doc);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open EM setup: {ex.Message}");
        }
    }

    /// <summary>
    /// R-em-10: resolves a <c>.cem</c>'s workspace-relative layout reference to the LIVE geometry —
    /// the open editor's own model when that layout is open, so an unsaved edit is what gets
    /// analysed, otherwise a fresh read from disk. Re-running after a layout edit picks the edit up
    /// only because the geometry is read HERE, at use time, and never embedded in the <c>.cem</c>.
    /// </summary>
    /// <summary>
    /// The exact inverse of <see cref="ResolveEmLayout"/>'s own base-directory rule: relative to the
    /// workspace root when the layout sits inside it, absolute otherwise. Written as one method
    /// beside its inverse so the pair cannot drift — a Change Layout that wrote a reference the
    /// resolver could not read would look like a corrupt file rather than a bad conversion.
    /// </summary>
    private string MakeEmLayoutRef(string cemPath, string absoluteClayPath)
        => EmSetupResolver.MakeLayoutRef(cemPath, absoluteClayPath, CurrentWorkspacePath);

    /// <summary>
    /// The absolute <c>.clay</c> path an <see cref="EmSetup.LayoutRef"/> names, or null when it names
    /// nothing. Split out of <see cref="ResolveEmLayout"/> so <see cref="NotifyEmSetupsLayoutChanged"/>
    /// can ask "does this .cem point at THAT layout?" without loading the geometry — the two must
    /// agree about what a reference resolves to, and one method is how that is guaranteed.
    /// </summary>
    private string? ResolveEmLayoutPath(string cemPath, string layoutRef)
        => EmSetupResolver.ResolveLayoutPath(cemPath, layoutRef, CurrentWorkspacePath);

    private EmLayoutSource? ResolveEmLayout(string cemPath, string layoutRef)
    {
        // The path rules and the disk read are EmSetupResolver's — shared verbatim with `circuitrf em`
        // (brief-cli-em-verb.md R-emcli-1/R-emcli-5), because a headless run that resolved a different
        // layout or a different technology than Simulate would be worse than no verb at all.
        //
        // What stays here is what only the GUI has: the LIVE model of an already-open .clay, so an
        // unsaved edit is what gets analysed, and the session technology override / orphan prompt that
        // ResolveTechFor wraps around the shared walk.
        var resolution = EmSetupResolver.Resolve(
            cemPath, layoutRef, CurrentWorkspacePath, _techCache, LiveLayoutView);

        if (resolution.Source is not { } source) return null;

        // Re-run through ResolveTechFor so the session override (R-fgn-4) and the orphan-technology
        // prompt still apply, and so the diagnostics reach Messages the way every other resolution's
        // do. Resolution itself is cached, so this is not a second file read.
        var tech = ResolveTechFor(source.View.TechRef, source.AbsolutePath);
        return source with { Technology = tech.Tech };
    }

    /// <summary>The live model of an already-open <c>.clay</c>, or null when that path is not open —
    /// <see cref="EmSetupResolver.Resolve"/>'s hook, and the one part of EM layout resolution that
    /// cannot be shared with a headless run.</summary>
    private LayoutView? LiveLayoutView(string absoluteClayPath)
    {
        foreach (var open in _openDocsByPath.Values.OfType<LayoutDocument>())
            if (open.FilePath is { } fp &&
                string.Equals(Path.GetFullPath(fp), absoluteClayPath, StringComparison.OrdinalIgnoreCase))
                return open.ViewModel.Model;
        return null;
    }

    /// <summary>
    /// Coalesces a burst of layout edits into one refresh per <c>.clay</c>. Keyed by absolute path,
    /// and touched from <see cref="LayoutModel.Changed"/> — see
    /// <see cref="NotifyEmSetupsLayoutChanged"/> for why that cannot do the work inline.
    /// </summary>
    private readonly HashSet<string> _emLayoutRefreshPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <b>An open EM setup re-reads its layout when that layout is edited.</b> Owner report,
    /// 2026-08-25: "placed 3 ports in my .clay drawing, but only 2 ports show up in the .cem."
    ///
    /// <para>Everything downstream was correct — the extractor resolved all three, and a Simulate
    /// would have run all three, because <c>EmRunService</c> re-extracts from the live
    /// <c>LayoutView</c> at run time. What was wrong is that <b>nothing ever re-ran
    /// <see cref="EmSetupEditorViewModel.Refresh"/> after the <c>.cem</c> was opened</b>: the panel's
    /// port list, mesh summary and blocking reason were a snapshot taken at open time and refreshed
    /// only when a setting inside the panel was committed or Mesh was pressed. Add a port with the
    /// panel already open and it went on showing the port list from before. The port count is
    /// derived from the geometry precisely so a user never types it, which makes a silently stale
    /// one indistinguishable from the extractor having missed a port.</para>
    ///
    /// <para><b><see cref="EmSetupEditorViewModel.InvalidateMesh"/> already documented itself as
    /// "called by the workspace when the referenced .clay changes" — and no such call existed.</b>
    /// This is that call. Both halves are needed: Invalidate drops a mesh report that describes
    /// artwork that has since changed, and Refresh re-extracts to replace it.</para>
    ///
    /// <para><b>Posted, never inline.</b> <see cref="LayoutModel.NotifyChanged"/> raises
    /// <c>Changed</c> while holding <c>RenderLock</c>, and every subscriber there is required to be a
    /// cheap non-blocking invalidate — a flatten plus two extractions is neither. Posting at
    /// Background priority also collapses a burst of edits (a paste, a multi-shape delete) into one
    /// refresh instead of one per command.</para>
    /// </summary>
    private void NotifyEmSetupsLayoutChanged(string? absClayPath)
    {
        if (string.IsNullOrWhiteSpace(absClayPath)) return;

        string key;
        try { key = Path.GetFullPath(absClayPath); }
        catch (ArgumentException) { return; }
        catch (NotSupportedException) { return; }

        lock (_emLayoutRefreshPending)
            if (!_emLayoutRefreshPending.Add(key)) return;   // one already queued for this layout

        Dispatcher.UIThread.Post(() =>
        {
            lock (_emLayoutRefreshPending) _emLayoutRefreshPending.Remove(key);

            foreach (var doc in _openDocsByPath.Values.OfType<EmSetupDocument>().ToList())
            {
                var vm = doc.ViewModel;
                if (ResolveEmLayoutPath(vm.FilePath, vm.Working.LayoutRef) is not { } target ||
                    !string.Equals(target, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                vm.InvalidateMesh();
                vm.Refresh();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// R-em-18 — <c>RunSchematicDocAsync</c>'s five steps with a different middle: background
    /// <c>Task.Run</c>, <c>Messages</c> for warnings FIRST, then the results write,
    /// <see cref="RefreshOpenDataDisplaysAsync"/> and
    /// <see cref="AutoOpenOrCreateDataDisplayAsync"/>. No new results plumbing and no new result
    /// type — the kernel already returns a <c>DataSet</c> carrying S, per-port Z0 and the "tline"
    /// group, and this path must not filter any of it out on the way to Data Display.
    /// </summary>
    private async Task RunEmSetupAsync(EmSetupEditorViewModel vm)
    {
        var baseDir = CurrentWorkspacePath is { } cws
            ? Path.GetDirectoryName(cws)!
            : _recovery.SessionDir;
        var resultsRoot = Path.Combine(baseDir, "results");

        var source = ResolveEmLayout(vm.FilePath, vm.Working.LayoutRef);
        var setup  = vm.Working.Clone();

        // TWO live rows, because an EM run has two questions with two different answers and one bar
        // cannot carry both. A full-wave frequency point costs tens of seconds at the shipping mesh
        // (L8d/L9d: 48 s and 71.9 s de-embedded), so a single bar over the point count would sit
        // still for a minute at a time — indistinguishable from a hung run, which is the exact
        // complaint this addresses. The sweep row answers "how far through the run"; the stage row
        // answers "what is it doing right now", and moves within a single point.
        int pointCount = ResolveEmPointCount(setup);

        // THE RESOLVED KERNEL, NOT THE SETUP'S REQUEST (owner report, 2026-08-29). The setup may say
        // Auto, but the panel has already run both extractors and put the registry's answer in
        // SelectedKernel — it is the kernel name the panel is showing the user at the moment they
        // press Simulate. Reading the request instead made two things wrong at once: the start line
        // could only hedge about a choice that was in fact already made, and an Auto setup that
        // resolves to the CROSS-SECTION kernel counted as adaptive here, which drove the sweep row
        // indeterminate for a run that solves every point against a perfectly good denominator.
        var kernel     = vm.SelectedKernel;
        bool adaptive  = kernel == Engine.Mom.EmAnalysisKind.Planar && setup.AdaptiveSampling;

        // Owner request, 2026-08-11: say what is starting BEFORE anything long begins. A full-wave
        // point costs tens of seconds, so the first thing a user sees after pressing Simulate must
        // not be an empty bar — the point count and whether adaptive sampling is in play are the two
        // facts that tell them how long to expect and how to read the result.
        Messages.Info(EmRunStartText(setup, pointCount, kernel));

        var sweepLive = Messages.BeginProgress($"EM '{setup.Name}'");
        var stageLive = Messages.BeginProgress($"EM '{setup.Name}' — starting");

        EmRunResult result;
        using (var cts = new CancellationTokenSource())
        {
            var control = new RunControl
            {
                Token = cts.Token,
                // Adaptive sampling decides how many points it actually solves as it goes, so there
                // is no honest denominator for it — it is reported indeterminate with a live count
                // rather than against a budget the run will usually stop well short of.
                Total    = adaptive ? 0 : pointCount,
                Progress = new Progress<RunProgress>(
                    p => ReportEmProgress(sweepLive, stageLive, setup.Name, p, adaptive)),
            };

            // ONE handle, THREE surfaces: the panel's Cancel button, the sweep row's bar and the
            // stage row's bar (owner, 2026-08-19). The two rows are two views of one computation — a
            // Cancel that stopped only the half its bar was drawing would be a Cancel that stops
            // nothing — and the panel button routes through the same object so a press on either
            // greys out both.
            var cancellation = new RunCancellation($"the EM run '{setup.Name}'", () =>
            {
                // Owner request: pressing Cancel says so IMMEDIATELY, and says what "cancel" means
                // here — cancellation lands at a work boundary, so a run mid-solve does not stop the
                // instant the button is pressed and silence would read as the button doing nothing.
                Messages.Info("Stopping the EM analysis. It stops at the next work boundary.");
                vm.IsCancelling = true;
                cts.Cancel();
            });
            sweepLive.BindCancellation(cancellation);
            stageLive.BindCancellation(cancellation);

            // Set INSIDE the try, so no path from here to the finally can leave the panel stuck
            // showing Cancel for a run that is already over.
            try
            {
                vm.CancelRequested = cancellation.Cancel;
                vm.IsRunning       = true;
                // R-emp-6/R-emcli-3 — the core cap is a MACHINE preference, so it is read HERE, on the
                // UI side that owns the preferences file, and handed to the run service as an
                // argument. EmRunService itself lives in CircuitRF.Design and cannot reach it.
                result = await Task.Run(() => EmRunService.Run(
                    setup, source, resultsRoot, default, control, EmSolveCorePreference.Preferred));
            }
            catch (Exception ex)
            {
                stageLive.Complete(MessageLevel.Info, $"EM '{setup.Name}' — stopped");
                sweepLive.Complete(MessageLevel.Error, $"The EM run failed: {ex.Message}");
                return;
            }
            finally
            {
                cancellation.Finish();
                vm.IsRunning       = false;
                vm.IsCancelling    = false;
                vm.CancelRequested = null;
            }
        }

        // ── A FAILED run: the error is the LAST line, and nothing follows it ───────────────────
        //
        // Owner report, 2026-08-11: "if there's an error I get many info messages after it. There's
        // no need to send those — we want the user to focus on the error."
        //
        // Both live rows were posted BEFORE the run, so they sit near the top of the panel and
        // whatever they are finished with lands there, not at the bottom. Finishing the sweep row
        // with the error therefore put the error ABOVE the pile of notes rather than after it. So on
        // a failure the error is posted as its OWN message, last, and the two progress rows settle
        // quietly in place.
        //
        // The engine's descriptive NOTES are dropped entirely here rather than merely reordered.
        // They are the run explaining what it did; a run that produced no answer has nothing to
        // explain, and stacking a dozen of them around the one line that matters is what buried it.
        // WARNINGS still go out — they are things to act on — and they go BEFORE the error so the
        // error keeps the last position.
        if (result.Status is EmRunStatus.NoLayout or EmRunStatus.Refused or EmRunStatus.EngineError)
        {
            // Two rows have to be resolved (each carries a bar), so they must say DIFFERENT things —
            // the same sentence twice reads as a duplicated message, which is what the owner saw.
            // Same split the exception path above already uses: the stage row says it stopped, the
            // sweep row points at the error. Deliberately NOT "not solved" (owner request): the
            // error below already says the run failed.
            stageLive.Complete(MessageLevel.Info, $"EM '{setup.Name}' — stopped");
            sweepLive.Complete(MessageLevel.Info, $"EM '{setup.Name}' — see the error below");

            foreach (var w in result.Warnings)     Messages.Warning(w);
            foreach (var e in result.Errors ?? []) Messages.Error(e);

            // Through the render point, not as a bare sentence: the diagnostic's ID is what a future
            // dedup ("this sweep refused at 400 points") or filter ("every technology-resolution
            // failure") has to key on, and the id only exists up to here. Falls back to the rendered
            // string for a result that predates the conversion (brief-localization-groundwork.md
            // R-loc-5 §8.3).
            if (result.Diagnostic is { } diagnostic)
                Messages.PostDiagnostic(diagnostic);
            else
                Messages.Error(result.Error ?? "The EM solve failed.");
            return;
        }

        // The stage row's job is over the moment the sweep is: it names a step, not an outcome, so
        // it collapses to a plain line rather than being left with a half-finished bar on screen.
        //
        // Owner report, 2026-08-11: this used to say "solve finished" UNCONDITIONALLY, above the
        // status switch — so a run the user had just stopped ended with a line claiming the solve had
        // finished. It reads as an answer having been produced, which is the one thing a stopped run
        // must not imply.
        stageLive.Complete(MessageLevel.Info, result.Status == EmRunStatus.Cancelled
            ? $"EM '{setup.Name}' — stopped before a solution was reached"
            : $"EM '{setup.Name}' — solve finished");

        // Three channels, three icons (owner report, 2026-08-09: "a lot of the Messages after the EM
        // sim have the yellow warning icon; change those to info"). The engine's descriptive output —
        // which kernel ran and why, the mesh's own sentences, RLGC, ports, how many shapes came from
        // instances — is the run explaining itself, not a problem: Info. A channel that says
        // "warning" about everything teaches people to ignore it, which costs the ones that matter.
        foreach (var n in result.Notes ?? [])    Messages.Info(n);
        foreach (var w in result.Warnings)       Messages.Warning(w);
        foreach (var e in result.Errors ?? [])   Messages.Error(e);

        if (result.Status == EmRunStatus.Cancelled)
        {
            // Appended, not replaced: the row keeps the point count it reached, which is the one
            // thing worth knowing about a run somebody stopped. keepBar: false (owner request,
            // 2026-08-14) — the bar glyph goes once the run settles; the text stays.
            sweepLive.Finish(MessageLevel.Warning, "EM stopped — no solution was written", keepBar: false);
            if (adaptive)
                Messages.Info(
                    "Adaptive frequency sampling was on, so the points it had solved are not a " +
                    "usable sweep on their own — the published grid is only complete once " +
                    "refinement finishes. Nothing was written.");
            return;
        }

        // Owner request, 2026-08-14: the bar glyph is dropped once the row settles (keepBar: false)
        // — only the appended summary text remains.
        sweepLive.Finish(MessageLevel.Success, EmRunSummary(result, adaptive, pointCount), keepBar: false);

        if (result.MeshReport is { } meshReport) vm.AdoptMeshReport(meshReport);
        if (result.PlanarMesh is { } planarMesh)
        {
            vm.AdoptPlanarResult(planarMesh, result.PlanarSolve);
            vm.AdoptCurrentDensity(result.CurrentDensity, result.PlanarPorts ?? []);
        }
        if (result.SnpPath is { } snp) Messages.Success("Wrote s-parameters", snp);
        if (result.NpyPath is { } npyWritten) Messages.Success("Wrote results", npyWritten);

        if (result.NpyPath is { } npy)
        {
            await RefreshOpenDataDisplaysAsync([npy]);
            // ResolveNpyKey, not ResolveResultKey — the EM results file (and therefore the .cdd named
            // after it) is deliberately distinct from the schematic's, or an EM run on cell "MLin"
            // replaces MLin's schematic results and its Data Display. See EmRunService.NpyKeySuffix.
            await AutoOpenOrCreateDataDisplayAsync(baseDir, EmRunService.ResolveNpyKey(setup), npy);
        }
    }

    /// <summary>
    /// The Mesh button, off the UI thread, with a live row and a Cancel.
    ///
    /// <para><b>One row, not two.</b> Meshing has no outer/inner split to carry — it is one pass with
    /// one honest denominator (grid rows against the metal), so a second bar would be a second way of
    /// saying the same thing. The sweep gets two rows because a frequency point is itself minutes
    /// long; a mesh row is not.</para>
    ///
    /// <para>The MESH button is the one that turns into Cancel, because it is the one that started
    /// the work — and Simulate is disabled meanwhile, so the two can never overlap and mesh the same
    /// problem twice at once.</para>
    /// </summary>
    private async Task MeshEmSetupAsync(EmSetupEditorViewModel vm)
    {
        string name = vm.Working.Name;
        var live = Messages.BeginProgress($"Meshing '{name}'");

        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token    = cts.Token,
            Progress = new Progress<RunProgress>(p => ReportEmMeshProgress(live, name, p)),
        };

        // Same one-handle rule as the EM run: the panel's Cancel button and the row's right-click ▸
        // Cancel are one request, and the panel says so while the stop is pending.
        var cancellation = new RunCancellation($"meshing '{name}'", () =>
        {
            Messages.Info("Stopping the mesh. It stops at the next grid row.");
            vm.IsCancelling = true;
            cts.Cancel();
        });
        live.BindCancellation(cancellation);

        try
        {
            vm.CancelMeshRequested = cancellation.Cancel;
            vm.IsMeshing           = true;

            // THREE PHASES, and the boundaries are load-bearing (owner report, 2026-08-09: "I
            // pressed the mesh button but got: the calling thread cannot access this object because a
            // different thread owns it"). The first version pushed the whole of BuildActiveMesh onto
            // the pool — but that method writes observable view-model properties, which raise
            // PropertyChanged straight into bound Avalonia controls, and fires AnalysisRefreshed,
            // which the workspace turns into opening a layout document. Only the MESHER is poolable.
            //
            //   1. UI    — Refresh + resolve + flatten + extract, and every state write they imply.
            //              Flatten/extract read the LIVE LayoutView of an open document, so moving
            //              them off-thread would trade a crash for a data race.
            //   2. POOL  — SurfaceMesher.Mesh on the extracted snapshot. The part that can take
            //              minutes, and the only part that touches nothing shared.
            //   3. UI    — adopt the report.
            vm.Refresh();

            if (vm.IsPlanarAnalysis)
            {
                if (vm.PreparePlanarMesh() is { } problem)
                {
                    var report = await Task.Run(() => vm.ComputePlanarMesh(problem, control));
                    vm.AdoptPlanarMeshReport(report);
                }
                // else: nothing to mesh, and Prepare already wrote the reason into the panel.
            }
            else
            {
                // The cross-section mesher is a 1-D boundary discretisation — orders of magnitude
                // cheaper than the planar one, and not worth a thread hop of its own.
                vm.BuildMesh();
            }
        }
        catch (OperationCanceledException)
        {
            live.Finish(MessageLevel.Warning, "stopped", keepBar: false);
            return;
        }
        catch (Exception ex)
        {
            live.Complete(MessageLevel.Error, $"Meshing '{name}' failed: {ex.Message}");
            return;
        }
        finally
        {
            cancellation.Finish();
            vm.IsMeshing           = false;
            vm.IsCancelling        = false;
            vm.CancelMeshRequested = null;
        }

        // Owner request, 2026-08-14: the bar glyph is dropped once the row settles (keepBar: false)
        // — only the appended outcome text remains, same as the Analysis/EM rows above.
        live.Finish(MessageLevel.Success, vm.MeshOutcomeText(), keepBar: false);
    }

    /// <summary>Drives the mesh row. The mesher reports through the STAGE counter only (it also runs
    /// inside a sweep, where the outer counter means frequency points), so the bar reads from there.</summary>
    internal static void ReportEmMeshProgress(IProgressMessage live, string setupName, RunProgress p)
    {
        string what = string.IsNullOrEmpty(p.Stage) ? "starting" : p.Stage;
        if (p.StageTotal > 0)
            live.Update($"Meshing '{setupName}' — {what}",
                        FormatCounter(p.StageCompleted, p.StageTotal),
                        100.0 * p.StageCompleted / p.StageTotal);
        else
            live.Update($"Meshing '{setupName}' — {what}", indeterminate: true);
    }

    /// <summary>
    /// How many frequency points the sweep asked for — the sweep row's denominator. Never throws: an
    /// unresolvable sweep is a refusal the run itself reports far better than a progress bar could,
    /// so this falls back to indeterminate (0) and lets that happen.
    /// </summary>
    internal static int ResolveEmPointCount(EmSetup setup)
    {
        try { return setup.Frequency.Expand().Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Drives BOTH EM rows from one observation. The sweep row carries the point count; the stage row
    /// carries what the current point is doing.
    ///
    /// <para><b>Same split as <see cref="ReportRunProgress"/>, for the same reason:</b> everything
    /// that changes goes in the trailing counter, after the bar, and the text before the bar is
    /// constant for the whole run — anything that grows to the bar's LEFT moves the bar with it.
    /// The stage row is the one exception and it is deliberate: its text is the changing part (it
    /// IS the answer to "what is it doing"), so its own counter stays fixed-width instead.</para>
    /// </summary>
    internal static void ReportEmProgress(
        IProgressMessage sweepLive, IProgressMessage stageLive,
        string setupName, RunProgress p, bool adaptive)
    {
        if (p.Total > 0)
            sweepLive.Update($"EM '{setupName}'",
                             FormatCounter(p.Completed, p.Total),
                             100.0 * p.Completed / p.Total);
        else
            sweepLive.Update($"EM '{setupName}'",
                             adaptive
                                 ? $"{p.Completed.ToString("N0", CultureInfo.CurrentCulture)} point(s) solved"
                                 : null,
                             indeterminate: true);

        string what = string.IsNullOrEmpty(p.Stage) ? "starting" : p.Stage;
        if (p.StageTotal > 0)
            stageLive.Update($"EM '{setupName}' — {what}",
                             FormatCounter(p.StageCompleted, p.StageTotal),
                             100.0 * p.StageCompleted / p.StageTotal);
        else
            stageLive.Update($"EM '{setupName}' — {what}", indeterminate: true);
    }

    /// <summary>
    /// The line posted the moment Simulate is pressed (owner request, 2026-08-11) — before the first
    /// long piece of work, so it is genuinely immediate.
    ///
    /// <para><b>It states what WILL happen, and never hedges (owner report, 2026-08-29).</b> The
    /// earlier wording said adaptive sampling "will be used if the full-wave analysis is chosen",
    /// which reads as the solver not knowing its own mind at the moment it starts. It does know:
    /// <paramref name="kernel"/> is the registry's answer, already resolved by the panel's own
    /// Refresh from both extractors' verdicts, and it is the kernel name the panel is displaying
    /// when the button is pressed. <see cref="EmAnalysisKind.Auto"/> is a request, never an outcome,
    /// so it never reaches here.</para>
    ///
    /// <para><b>Where the answer contradicts the checkbox, the sentence says WHY.</b> Adaptive
    /// sampling is a property of the full-wave sweep — it models the points it does not solve — and
    /// the cross-section kernel has nothing to model, so a setup with the box ticked that resolves
    /// to kernel A is told that, rather than being told something it will not do.</para>
    /// </summary>
    internal static string EmRunStartText(EmSetup setup, int pointCount, EmAnalysisKind kernel)
    {
        string points = pointCount > 0
            ? $"{pointCount.ToString("N0", CultureInfo.CurrentCulture)} frequency point(s)"
            : "a frequency sweep whose point count could not be resolved";

        string sampling = kernel != Engine.Mom.EmAnalysisKind.Planar
            ? setup.AdaptiveSampling
                ? "adaptive frequency sampling is on, but it applies to the full-wave analysis only " +
                  "and this run is the cross-section analysis — every point is solved"
                : "adaptive frequency sampling does not apply to the cross-section analysis — every " +
                  "point is solved"
            : setup.AdaptiveSampling
                ? "adaptive frequency sampling is on — it solves a subset and models the rest"
                : "adaptive frequency sampling is off — every point is solved";

        return $"EM analysis started: '{setup.Name}' over {points}. {sampling}.";
    }

    /// <summary>The sweep row's own outcome, appended to the end of the row it already owns — so the
    /// finished line still says which setup ran and how many points it got through.</summary>
    internal static string EmRunSummary(EmRunResult result, bool adaptive, int requestedPoints)
    {
        string points = requestedPoints > 0
            ? $"{requestedPoints.ToString("N0", CultureInfo.CurrentCulture)} frequency point(s)"
            : "the frequency sweep";

        // Adaptive publishes the user's whole grid but only SOLVES some of it, and saying so is the
        // difference between a number a user can trust and one they have to go looking for.
        //
        // "AND THE REST" IS NOT ALWAYS A NON-EMPTY SET (owner report, 2026-08-29). Adaptive sampling
        // skips a point only when the interpolant already predicts it inside the tolerance, so on a
        // grid whose neighbouring points differ by far more than that — the panel's own default
        // sweep is routinely one — refinement runs to the grid floor and every requested point is
        // solved. That is the feature working, not failing, and the row now says which of the two
        // happened rather than promising a modelled remainder that does not exist.
        if (adaptive && result.PlanarSolve is { } ps && ps.SolvedFrequencies.Count > 0)
            return ps.SolvedFrequencies.Count >= requestedPoints && requestedPoints > 0
                ? $"solved — {points}, every one solved by the full-wave kernel: adjacent points of " +
                  "this sweep differ by more than the adaptive tolerance, so none could be modelled " +
                  "from its neighbours. A finer sweep is where adaptive sampling saves time"
                : $"solved — {points}, {ps.SolvedFrequencies.Count:N0} solved by the full-wave " +
                  "kernel and the rest modelled from those";

        // Adaptive was ASKED for but the run did not report a solved set — which is what happens when
        // the registry picked the cross-section kernel, where every point is closed-form anyway.
        if (adaptive)
            return $"solved — {points}, every point solved (adaptive sampling did not apply)";

        return $"solved — {points}";
    }

    /// <summary>
    /// Copies a <c>.cem</c>'s current mesh onto the layout it analyses.
    ///
    /// <para><b>Owner report, 2026-08-09: "I was expecting to see a mesh rendered overtop of my
    /// .clay file's rendering."</b> The mesh has no home except an OPEN layout document, and this
    /// used to return silently when that layout was not open — so pressing Mesh with only the
    /// <c>.cem</c> on screen produced a correct mesh that had nowhere to be drawn and said nothing
    /// about it. A mesh is a picture; producing one and showing nothing is indistinguishable from
    /// doing nothing. It now OPENS the layout, once, when there is genuinely a mesh to show.</para>
    ///
    /// <para>Guarded on a non-null report so an ordinary <c>Refresh</c> (which fires this on every
    /// keystroke-committed field) never yanks a tab open behind the user.</para>
    /// </summary>
    private void PushEmMeshToLayout(EmSetupEditorViewModel vm)
    {
        if (ResolveEmLayout(vm.FilePath, vm.Working.LayoutRef) is not { } source) return;

        bool hasMesh = vm.MeshReport is not null || vm.PlanarMeshReport is not null;
        bool anyOpen = _openDocsByPath.Values.OfType<LayoutDocument>().Any(d =>
            d.FilePath is { } p && string.Equals(Path.GetFullPath(p), source.AbsolutePath, StringComparison.OrdinalIgnoreCase));

        if (hasMesh && !anyOpen)
        {
            OpenOrActivateLayout(source.AbsolutePath);
            Messages.Info("Opened the layout to show the mesh", source.AbsolutePath);
        }

        foreach (var open in _openDocsByPath.Values.OfType<LayoutDocument>())
            if (open.FilePath is { } fp &&
                string.Equals(Path.GetFullPath(fp), source.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                // Both meshes, always — L8b's D5: WHICH overlay draws follows from which report is
                // non-null, never from a mode. A cross-section setup leaves the planar one null and
                // vice versa, so pushing both is how "the right one shows" stays true with no branch.
                open.ViewModel.EmMeshReport     = vm.MeshReport;
                open.ViewModel.PlanarMeshReport = vm.PlanarMeshReport;
                // L8e — the per-cell current density, when a planar run has produced one. Null
                // outside that case, which the renderer takes as "draw plain cell boundaries".
                open.ViewModel.PlanarCurrentDensity  = vm.CurrentDensity;
                open.ViewModel.PlanarReferencePlanes = vm.ReferencePlanes;
                // The port TYPE lives in the .cem, so the layout cannot know it — this is the one
                // channel that carries it, and without it an internal delta gap would draw as an
                // edge port with its cut at the far end of the trace.
                AdoptPortTypes(open, vm, source.AbsolutePath);
            }
    }

    /// <summary>
    /// Hands a layout the port TYPES of the setup that just refreshed — and <b>says so when that
    /// takes them off a DIFFERENT setup that disagreed</b>.
    ///
    /// <para><b>The conflict is real and is not a bug to be designed away.</b> More than one
    /// <c>.cem</c> may analyse one <c>.clay</c>, and two of them may legitimately disagree about a
    /// port: a gap in the middle of a trace in one, driven from its ends in another. That is exactly
    /// why the type is an analysis setting rather than a property of the drawing. But there is only
    /// ONE layout on screen and it can draw only one of the two answers.</para>
    ///
    /// <para>So the layout NAMES its current owner, and a takeover that actually changes the marks
    /// is reported. Silence was the defect: the marks flipped when a user touched an unrelated field
    /// in the other setup, and nothing on screen connected the two. A takeover that changes nothing —
    /// the overwhelmingly common case, two setups that agree, or the same setup refreshing — says
    /// nothing, because a message nobody can act on is one they learn to skip.</para>
    /// </summary>
    private void AdoptPortTypes(LayoutDocument open, EmSetupEditorViewModel vm, string layoutPath)
    {
        var next  = vm.InternalPortMarkAnchors;
        var owner = vm.Working.Name is { Length: > 0 } n ? n : Path.GetFileName(vm.FilePath);

        var prev      = open.ViewModel.InternalPortMarks;
        string before = open.ViewModel.InternalPortMarksOwner;
        bool  differs = prev.Count != next.Count || !prev.All(next.Contains);

        open.ViewModel.InternalPortMarks     = next;
        open.ViewModel.InternalPortMarksOwner = owner;

        if (!differs || before.Length == 0 || string.Equals(before, owner, StringComparison.Ordinal))
            return;

        Messages.Info(
            $"Port marks on {Path.GetFileName(layoutPath)} now follow the EM setup '{owner}' " +
            $"({Describe(next)}); they were following '{before}' ({Describe(prev)}). Two EM setups " +
            "analyse this layout and disagree about a port's type — which is allowed, since a port " +
            "type belongs to the analysis rather than to the drawing. The layout can only draw one " +
            "of them.", layoutPath);

        // Counted BY KIND, because "2 internal ports" said of one delta gap and one internal port
        // describes neither of the two marks that just changed on screen.
        static string Describe(IReadOnlyList<(long X, long Y, PlanarPortKind Kind)> marks)
        {
            if (marks.Count == 0) return "no internal ports";

            int gaps = 0, vias = 0;
            foreach (var m in marks)
                if (m.Kind == PlanarPortKind.Internal) vias++; else gaps++;

            var parts = new List<string>(2);
            if (gaps   > 0) parts.Add(gaps   == 1 ? "1 internal delta-gap port" : $"{gaps} internal delta-gap ports");
            if (vias   > 0) parts.Add(vias   == 1 ? "1 internal port"     : $"{vias} internal ports");
            return string.Join(" and ", parts);
        }
    }

    /// <summary>Reflects a .cem editor's dirty state onto its own tree node's dirty dot — the exact
    /// mirror of <see cref="HookTechFileDirty"/>.
    ///
    /// <para><b>Owner-reported, 2026-08-21:</b> <i>"a dirty .cem does not show as dirty in the Project
    /// tree /em folder."</i> This hook called the .ctech setter, whose <c>NodeKind.TechFile</c> guard
    /// discarded a <c>.cem</c> node in silence. There is one setter for every file kind now
    /// (<see cref="Dock.ProjectTreeTool.SetFileDirty"/>), so the wrong one cannot be reached.</para>
    /// </summary>
    private void HookEmSetupDirty(EmSetupDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(EmSetupEditorViewModel.IsDirty)) return;
            _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, doc.ViewModel.IsDirty);
            RaiseFileMenuEnablementChanged();   // see HookTechFileDirty for why
        };
    }

    /// <summary>Reflects a .ctech editor's dirty state onto its tree node's dirty dot — mirrors
    /// <see cref="HookLayoutCellDirty"/>, except a technology has no owning cell: the node
    /// updated is the .ctech file node itself.
    ///
    /// <para><b>It also re-asks the File menu, and that half is not cosmetic.</b>
    /// <see cref="CanSaveAllDocuments"/> already answers "yes" for a dirty
    /// <see cref="TechDocument"/>, but nothing was re-EVALUATING it: the two enablement fan-outs
    /// fire on a tab switch, a window focus change, or a completed save, and the canvas-backed
    /// editors additionally re-ask on a canvas click (<see cref="OnSchematicCanvasInteracted"/> and
    /// its layout/symbol siblings). A .ctech editor is a FORM — it has no canvas, so none of those
    /// ever fired while the user typed into it, and File ▸ Save stayed greyed out for the whole
    /// editing session even though ⌘S/Ctrl+S worked (a <c>RelayCommand</c>'s CanExecute is
    /// evaluated fresh at invocation, so only the menu's APPEARANCE was stale). The dirty
    /// transition is the missing refresh point, and it is exactly here. User-reported,
    /// 2026-08-30.</para></summary>
    private void HookTechFileDirty(TechDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(TechEditorViewModel.IsDirty)) return;
            _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, doc.ViewModel.IsDirty);
            RaiseFileMenuEnablementChanged();
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

    /// <summary>
    /// Design ▸ Check Design Rules (docs/design/layout-view.md §9A) — the menu entry point.
    ///
    /// <para>Runs the check on the active layout, brings the DRC panel forward so the result is not
    /// reported into a panel nobody can see, and posts a one-line summary to Messages. R16b holds:
    /// nothing is blocked, nothing is modified, and the user stays where they were.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsCheckableDocumentActive))]
    private void CheckDesignRules()
    {
        if (ResolveDrcTargetLayout() is not { } vm) return;

        var result = vm.RunDrc();

        _factory.DrcTool?.SetActiveLayout(vm);
        ShowToolPanel(Docking.DockPanelIds.Drc);

        CircuitRF.Ui.Layout.Drc.DrcRunReport.Post(Messages, result);
    }

    /// <summary>
    /// The layout a check runs over: a layout document's own, or a <b>wBond document's REFERENCE
    /// layout</b> — which is where its wires live, since <c>WBondDocumentViewModel</c> installs the
    /// design there and the assembly half of the run is evaluated by the layout's own DRC.
    ///
    /// <para><b>Without this, a wirebond design could not be checked from the editor it is drawn
    /// in</b> (owner, 2026-08-19). The command was gated on a <c>LayoutDocument</c> being active and
    /// the DRC panel was explicitly emptied for a wBond document, so the one editor whose entire
    /// subject is wires was the one place with no way to check them.</para>
    /// </summary>
    private LayoutEditorViewModel? ResolveDrcTargetLayout() => ResolveActiveDocumentForCommands() switch
    {
        LayoutDocument doc  => doc.ActiveViewModel,
        WBondDocument  wdoc => wdoc.ViewModel.ReferenceLayout,
        _                   => null,
    };

    private bool IsCheckableDocumentActive() => ResolveDrcTargetLayout() is not null;

    /// <summary>
    /// Ctrl+K / Cmd+K — docs/design/layout-view.md §9B.6 R-rul-13: removes every in-design RULER
    /// annotation from the active layout, as ONE undo entry, with no confirmation prompt (the
    /// operation is undoable, and a prompt on an undoable action trains people to dismiss prompts).
    ///
    /// <para><b><c>Ctrl+Shift+K</c> is Check Design Rules and is deliberately untouched.</b> The two
    /// share a letter and are far enough apart in effect that the overlap is worth watching in review
    /// but not worth renaming — §9B.6 says so explicitly. Routed through
    /// <see cref="ResolveDrcTargetLayout"/> for the same reason that command is: a wBond document's
    /// rulers live on its REFERENCE layout.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsCheckableDocumentActive))]
    private void ClearAllRulers() => ResolveDrcTargetLayout()?.ClearAllRulers();

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportGdsii() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportGdsii();

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportDxf() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportDxf();

    /// <summary>R-menu-4: reads the PER-WINDOW active document (<see cref="ResolveActiveDocumentForCommands"/>),
    /// not the shell's own <c>DocumentDock.ActiveDockable</c> directly — so this stays correct while a
    /// torn-off layout window has focus.</summary>
    private bool IsLayoutDocumentActive() => ResolveActiveDocumentForCommands() is LayoutDocument;

    /// <summary>
    /// Whether the <c>P</c> / <c>A</c> panel keys apply right now — the active document is a layout that
    /// actually has wirebonds, and the user is not typing a label into it.
    ///
    /// <para>Asked by the SHELL WINDOW's own tunnel handler rather than by a view, and that is the whole
    /// point (owner, 2026-08-17, reported three times). A key handler that lives on a view and is gated on
    /// where keyboard focus is cannot survive an action that MOVES focus — which is exactly what showing
    /// or hiding a dockable does. The window sees every key in it whatever has focus, so the gate becomes
    /// a question about the DOCUMENT, which does not move.</para>
    /// </summary>
    public bool WirePanelKeysApply =>
        ResolveActiveDocumentForCommands() is LayoutDocument { ActiveViewModel: { } vm }
        && vm.HasWireDesign
        && !vm.IsTypingLabel;

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

    /// <summary>Board export — the outward half of L4d, and gated exactly like GDSII/DXF/Gerber. As
    /// with those, this command only decides whether a layout document is active; the picker, the
    /// fidelity report and the PcbExport.Analyze/Write calls live in the layout view's own
    /// code-behind.</summary>
    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void ExportBoard() => (ResolveActiveDocumentForCommands() as LayoutDocument)?.RequestExportBoard();

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
        WireDataDisplayTreeDirty(doc);
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
            ActivateOpenDocument(existing);
            return;
        }

        string title = Path.GetFileNameWithoutExtension(absPath);
        var newVm  = new DataDisplayDocumentViewModel();
        var newDoc = new DataDisplayDocument(title, newVm, filePath: absPath);
        _openDocsByPath[absPath] = newDoc;
        newVm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        newVm.Window.GetResultsRootAction = GetResultsRoot;
        WireDataDisplayLibraryEvents(newVm);
        WireDataDisplayTreeDirty(newDoc);
        _factory.OpenDocument(newDoc);

        // format_version check — and, since 2026-08-26, unparseable JSON — throw InvalidDataException.
        // A load that fails must leave NOTHING behind: without this the tab stayed open, registered in
        // _openDocsByPath, materialized at the same path and showing one empty plot, so the error
        // message the caller posted was contradicted by a document that looked ready to use — and
        // saving it (or a close-prompt "Save") overwrote the file the load had just refused to read.
        // Closing here puts the workspace back exactly where it was before the open was attempted, so
        // the file on disk stays recoverable. ForceCloseDockable bypasses the dirty-save prompt, which
        // is correct: a document that never loaded has nothing of the user's in it.
        try
        {
            await newVm.Window.LoadAllAsync(absPath, stream);
        }
        catch
        {
            _openDocsByPath.Remove(absPath);
            _factory.ForceCloseDockable(newDoc);
            throw;
        }
        newDoc.Materialize(absPath);
    }

    // ── Tools ▸ harmonicaRF (R-h45-13 / D10) ─────────────────────────────────
    //
    // D10 moves the Tools menu FORWARD from H7 to here, and the reason is a testing one rather than
    // a scheduling one: "a document nobody can open cannot be tested through the product path."
    // H7 fills the menu out; today it has exactly one entry.

    /// <summary>
    /// Opens a new harmonicaRF instrument (harmonicarf.md §1.2).
    ///
    /// <para><b>No workspace required</b>, deliberately — §1.2: harmonicaRF "works with or without a
    /// workspace open, and is structured so it can ship as a standalone binary". So this mirrors
    /// NewDataDisplay's scratch path rather than the workspace-gated New Cell path.</para>
    ///
    /// <para>The document opens on a real, converging device rather than an empty canvas: §1's whole
    /// claim is liveness, and an instrument that opens showing nothing has to be configured before it
    /// can demonstrate anything. The first solve is LAZY and coarse — the view triggers it on attach
    /// — so opening the window is not a blocking wait.</para>
    ///
    /// <para><b>It opens in its own window, not as a docked tab</b> (owner, 2026-08-19). harmonicaRF
    /// is an instrument, not a document of the workspace — it needs no workspace at all (§1.2) and
    /// ships as a standalone binary of its own — so a tab that displaces whatever schematic the user
    /// is tuning against is the wrong default. The window is sized like the shell and offset down-right
    /// by one title bar, so the workspace stays identifiable behind it.</para>
    ///
    /// <para><b>It is still an ordinary dockable</b>: <see cref="OpenDocumentInOwnWindow"/> is the same
    /// tear-off a user's own tab drag performs, so the window can be re-docked, is captured by the
    /// layout, and survives a layout rebuild exactly as a hand-torn-off one does. A float that cannot
    /// be built leaves the document docked rather than not opening it.</para>
    /// </summary>
    [RelayCommand]
    private void NewHarmonica()
    {
        var title = NextHarmonicaTitle();
        // R-h9r2-18a — a brand new document's tickle seeds from this installation's own preference.
        var vm    = new HarmonicaDocumentViewModel(new HarmonicaViewModel(HarmonicaTickleDefaults.SeedModel()));
        var doc   = new HarmonicaDocument(title, vm);
        _scratchHarmonicas.Add(doc);
        _factory.OpenDocument(doc);
        OpenDocumentInOwnWindow(doc);
    }

    /// <summary>
    /// Opens a <b>standalone Match Designer</b> — Tools ▸ Match Designer (owner, 2026-08-20).
    /// </summary>
    /// <remarks>
    /// <b>No workspace and no schematic required</b>, deliberately, for the same reason harmonicaRF
    /// needs none: the Designer synthesises a matching network from two terminations and a band, and
    /// none of that comes from a drawing. What a workspace supplies, when one is open, is only a
    /// starting FOLDER for Flatten to Cell — the standalone Designer writes its cell wherever the
    /// user points it, because that cell is referenced by no schematic and so has nothing to be
    /// relative to.
    ///
    /// <para>The window opens unowned and cascaded, exactly as the one a placed <c>Match</c> opens
    /// does, and appears in the Window menu through <c>ICrfMenuWindow</c> like every other standalone
    /// editor. It is NOT deduplicated: see <c>MatchDesignerWindow.ShowStandalone</c>.</para>
    /// </remarks>
    [RelayCommand]
    private void NewMatchDesigner() =>
        Views.Match.MatchDesignerWindow.ShowStandalone(CurrentWorkspaceRoot, ResolveOwner(null));

    /// <summary>
    /// Opens a blank wBond editor — wbond.md §10's third entry point.
    ///
    /// <para><b>No workspace and no layout context required</b>, deliberately: that entry point exists
    /// precisely for the case where there is none, and the user drags cells in from the project tree
    /// as references afterwards. The layout half of the editor simply shows an empty canvas until one
    /// arrives, which is a supported state rather than a broken one.</para>
    ///
    /// <para>The other two entry points (from a schematic's wBond symbol, and from a wire drawn in the
    /// Layout Editor) land on this same <see cref="WBondDocument"/>.</para>
    /// </summary>
    [RelayCommand]
    private void NewWBond()
    {
        var doc = new WBondDocument(title: NextWBondTitle());

        // §6.6/§10: a blank editor's layout view is where the user drags cells in from the project
        // tree as references. It needs a real (if empty) layout to drop into, or the existing
        // drag-drop path silently does nothing — TrackNewWBond creates it, for every entry point
        // rather than only for this one.
        TrackNewWBond(doc);

        _scratchWBonds.Add(doc);
        _factory.OpenDocument(doc);
    }

    /// <summary>
    /// The lowest free "Untitled-wBond-N" across every open wBond, scratch or path-keyed — the same
    /// shape <see cref="NextHarmonicaTitle"/> and <see cref="NextDataDisplayTitle"/> already use, so
    /// a new wBond tab is named the way every other new document tab is (owner, 2026-08-16).
    /// </summary>
    private string NextWBondTitle()
    {
        const string prefix = "Untitled-wBond-";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchWBonds) used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is WBondDocument wd) used.Add(wd.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Everything a wBond document needs from the workspace the moment it comes into existence:
    /// its assembly rules, and the answer to its toolbar's Save / Save As buttons.
    ///
    /// <para>Called at each of the three creation points, and <b>only</b> there — unlike
    /// <see cref="ResolveWBondAssemblyRules"/>, which is also re-run over already-open documents when
    /// the workspace's rule file changes, and would double-subscribe the save hook if it carried
    /// it.</para>
    /// </summary>
    private void TrackNewWBond(WBondDocument doc)
    {
        // FIRST, because everything below applies itself to a reference layout that already exists:
        // ResolveWBondAssemblyRules pushes the rule set into it, and ConfigureReferenceLayout hands
        // it the workspace's technology seam.
        //
        // A `.wBond` opened from disk with no embedded geometry had none at all, which cost it two
        // things silently: a cell dragged in from the project tree had nowhere to land, and — since
        // the DRC panel follows this layout — its wires could not be checked.
        doc.ViewModel.EnsureReferenceLayout(
            Path.Combine(_recovery.SessionDir, "wbond-reference", Guid.NewGuid().ToString("N")[..8]));

        ResolveWBondAssemblyRules(doc);
        doc.SaveRequested += saveAs => _ = SaveWBondDoc(doc, null, saveAs);

        // The wBond editor's layout half IS the Layout Editor's own view-model, so it needs the same
        // workspace seam every LayoutEditorViewModel gets — the technology resolvers and, through
        // WorkspaceTechDir, the workspace root a generated PCell cell is written into. Without it a
        // PCell dragged from the Library palette into a wBond editor was refused with "no workspace
        // is open" (owner, 2026-08-16). The setter applies it to a reference layout that already
        // exists and the document re-applies it to every later one.
        doc.ViewModel.ConfigureReferenceLayout = WireRetargetSeam;

        // WB39a — the wBond editor HOSTS LayoutEditorView over a LayoutDocument of its own, so it
        // gets Push Into Cell / Pop Out / Save exactly as a layout tab does. Same "apply now, and to
        // every later one" setter shape as ConfigureReferenceLayout above, and for the same reason:
        // that document is created on demand and replaced when a bundle is unpacked.
        doc.ViewModel.LayoutHierarchy = this;
    }

    private readonly List<WBondDocument> _scratchWBonds = [];

    /// <summary>
    /// Workspaces already asked about assembly rules this session, so a user who declines is not
    /// nagged on every check. Reset with the rest of the per-workspace caches.
    /// </summary>
    private readonly HashSet<string> _assemblyRulesAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Offers to point a workspace at assembly rules the first time a check actually needs them.
    ///
    /// <para><b>A new workspace deliberately ships no `.wasm`.</b> Most designs have no wirebonds, and
    /// creating a rule file in every workspace would put a document in the project tree that most
    /// users would have to learn about only to ignore. So the file is created ON DEMAND, at the one
    /// moment the user is already asking the question: they ran a check, the design has wires, and
    /// there are no assembly rules to check them against.</para>
    ///
    /// <para>Three answers, and declining is a real one: point at an existing file (the usual case —
    /// the house sent one), create a starter to edit against the house's document, or not now. A
    /// decline is remembered for the session, because a prompt that reappears on every check is one
    /// people learn to dismiss unread.</para>
    /// </summary>
    /// <returns>True when rules were installed and the caller should re-run the check.</returns>
    public async Task<bool> PromptForAssemblyRulesAsync(LayoutEditorViewModel layout, Window? owner)
    {
        if (layout.WireDesign is null) return false;
        if (layout.AssemblyRules?.Rules is not null) return false;
        if (CurrentWorkspacePath is not { } cwsPath) return false;
        if (!_assemblyRulesAsked.Add(cwsPath)) return false;

        var resolved = ResolveOwner(owner);
        if (resolved is null) return false;

        var answer = await new SaveChangesDialog(
            "This design has bond wires, and the workspace has no assembly rules for them. It was " +
            "checked against circuitRF's own built-in rule set instead, which is one rule: wires " +
            "must not come closer to each other than the clearance in Settings.\n\n" +
            "Assembly rules (a .wasm file) come from your assembly house and state what the bonder " +
            "can do — wire pitch, loop height, clearances, allowed wire. circuitRF does not create " +
            "one until you need it.",
            saveLabel:     "Choose File…",
            dontSaveLabel: "Create Default",
            cancelLabel:   "Not Now",
            title:         "Assembly Rules").ShowDialog<SaveChangesResult>(resolved);

        string workspaceDir = Path.GetDirectoryName(cwsPath)!;
        string? chosen = null;

        if (answer == SaveChangesResult.Save)
        {
            var picked = await resolved.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose assembly rules",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Assembly rules") { Patterns = ["*" + WasmPersistence.Extension] },
                ],
            });

            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return false;
            chosen = path;
        }
        else if (answer == SaveChangesResult.DontSave)
        {
            chosen = Path.Combine(workspaceDir, WasmDefaults.DefaultFileName);
            try
            {
                WasmPersistence.SaveToFile(chosen, WasmDefaults.CreateStarter());
                Messages.Success(
                    $"Created {WasmDefaults.DefaultFileName} with placeholder rules — edit it against " +
                    "your assembly house's own document before trusting a clean result.", chosen);
            }
            catch (Exception ex)
            {
                Messages.Error($"Could not create {WasmDefaults.DefaultFileName}: {ex.Message}");
                return false;
            }
        }
        else
        {
            return false;   // Not now — and not asked again this session.
        }

        // Recorded as the WORKSPACE default, relative where it lives inside the workspace, so every
        // other wBond design in it picks the same rules up with nothing further to configure.
        try
        {
            var cws = TryLoadCws(cwsPath);
            cws.DefaultAssemblyRef = MakeWorkspaceRelative(chosen, workspaceDir);
            WorkspacePersistence.SaveToFileAtomic(cwsPath, cws);
        }
        catch (Exception ex)
        {
            Messages.Warning($"The assembly rule reference could not be recorded in the workspace: {ex.Message}");
        }

        _wasmCache.Invalidate(chosen);

        // Push the newly-resolved rules onto every open wBond document, so a second design already
        // open does not have to be reopened to see them.
        foreach (var doc in _scratchWBonds.Concat(_openDocsByPath.Values.OfType<WBondDocument>()))
            ResolveWBondAssemblyRules(doc);

        return layout.AssemblyRules?.Rules is not null;
    }

    /// <summary>A path inside the workspace stored relative; anything outside stays absolute, because
    /// no encoding makes an outside reference portable (the R-dd-6 rule, applied here).</summary>
    private static string MakeWorkspaceRelative(string absolutePath, string workspaceDir)
    {
        string full = Path.GetFullPath(absolutePath);
        string root = Path.GetFullPath(workspaceDir);

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return full;

        return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Resolves a wBond document's assembly rules (wbond.md §8) and reports whatever the resolver had
    /// to say. Called at every point a wBond document comes into existence — new, opened, imported.
    ///
    /// <para><b>Finding none is silent.</b> A design that names no assembly house is the ordinary case
    /// for anyone who has not been given a rule file yet; saying "no assembly rules" on every open
    /// would be noise, and the DRC panel already states it where it matters. Only a reference that was
    /// STATED and could not be honoured is worth a message.</para>
    /// </summary>
    private void ResolveWBondAssemblyRules(WBondDocument doc)
    {
        string? workspaceDir = CurrentWorkspacePath is null
            ? null
            : Path.GetDirectoryName(CurrentWorkspacePath);

        string? defaultRef = null;
        if (CurrentWorkspacePath is not null)
        {
            try { defaultRef = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath).DefaultAssemblyRef; }
            catch { /* corrupt .cws — treated as "no default", matching ResolveTechFor */ }
        }

        var resolution = doc.ResolveAssemblyRules(workspaceDir, defaultRef, _wasmCache);

        foreach (var diagnostic in resolution.Diagnostics)
            Messages.Warning(diagnostic);
    }

    /// <summary>
    /// WB40 — the assembly rules a WIREBOND CELL's wire DRC checks against.
    ///
    /// <para>Same resolution order as a wBond document's (§M1: the file's own <c>AssemblyRef</c>,
    /// then the workspace default, then none) minus the first term, which a cell has nowhere to
    /// carry. Every outcome is non-fatal, and "none" is an absence of rules rather than a failure.</para>
    /// </summary>
    private WasmResolution? ResolveWorkspaceAssemblyRules(string absClayPath)
    {
        if (CurrentWorkspacePath is null) return null;

        string? defaultRef;
        try { defaultRef = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath).DefaultAssemblyRef; }
        catch { return null; }   // corrupt .cws — treated as "no default", matching ResolveTechFor

        return WasmResolver.Resolve(
            null, Path.GetDirectoryName(absClayPath),
            Path.GetDirectoryName(CurrentWorkspacePath), defaultRef, _wasmCache);
    }

    /// <summary>
    /// Opens a <c>.wBond</c> — the standalone route of §9.2, and the double-click route from the tree.
    ///
    /// <para>Embedded geometry is unpacked into the session's own scratch area rather than anywhere
    /// under the workspace: it is a decoded copy of what is already in the file, not project state,
    /// and writing it into the user's workspace would leave litter they never asked for.</para>
    /// </summary>
    public void OpenWBondPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (ActivateIfOpen(full)) return;

        try
        {
            string scratch = Path.Combine(_recovery.SessionDir, "wbond-embedded",
                                          Math.Abs(full.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture));

            var doc = WBondDocument.Open(full, scratch);
            TrackNewWBond(doc);

            _openDocsByPath[full] = doc;
            _factory.OpenDocument(doc);

            if (doc.HasEmbeddedGeometry)
                Messages.Info($"Opened {Path.GetFileName(full)} with its embedded layout geometry.");
        }
        catch (Exception ex)
        {
            // WB35: report, never fail silently and never substitute.
            Messages.Error($"Could not open {Path.GetFileName(full)}: {ex.Message}");
        }
    }

    /// <summary>
    /// File ▸ Import ▸ Wirebond Wires… — brings a <c>.wBond</c>'s WIRES into the active schematic
    /// (wbond.md §9.2 route 2, from a file picker rather than from the project tree).
    ///
    /// <para><b>Wires only.</b> A <c>.wBond</c> may also carry the layout artwork it was drawn over,
    /// and a schematic has nowhere to put artwork — that is exactly why a placed wBond no longer
    /// references the whole file. The artwork route is the sibling item below.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSchematicDocumentActive))]
    private async Task ImportWirebondWires(Window? window)
    {
        if (ResolveActiveDocumentForCommands() is not SchematicDocument sd)
        {
            Messages.Warning(
                "Open the schematic you want the wires in first — Import Wires brings them into the " +
                "schematic that is currently in front.");
            return;
        }

        if (await PickWBondAsync(window, "Import Wirebond Wires") is not { } path) return;
        sd.ViewModel.ImportWBondWires(path);
    }

    /// <summary>
    /// File ▸ Import ▸ Wirebond as Cell… — wires AND artwork (wbond.md §9.2 route 3).
    ///
    /// <para>Same body as the project tree's own "Add as Cell…": the wires become the cell's schematic
    /// view as a wBond component, and the design's embedded geometry becomes its layout view. One
    /// implementation, two doors.</para>
    /// </summary>
    [RelayCommand]
    private async Task ImportWirebondAsCell(Window? window)
    {
        if (await PickWBondAsync(window, "Import Wirebond as Cell") is not { } path) return;
        await AddWBondAsCellFromPathAsync(path);
    }

    /// <summary>The one <c>.wBond</c> file picker — so every import door offers the same filter.</summary>
    private async Task<string?> PickWBondAsync(Window? window, string title)
    {
        var owner = ResolveOwner(window);
        if (owner?.StorageProvider is not { } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("wBond design") { Patterns = ["*.wBond", "*.wbond"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    [RelayCommand]
    private async Task OpenWBondFile(Window? window)
    {
        var owner = ResolveOwner(window);
        if (owner?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open wBond",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("wBond design") { Patterns = ["*.wBond", "*.wbond"] }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) OpenWBondPath(path);
    }

    /// <summary>
    /// Saves the active wBond document, asking about geometry embedding first (WB33).
    ///
    /// <para>The plan is shown BEFORE the write, because a file that quietly lost parametricity on a
    /// PDK cell is discovered by whoever receives it — which is the worst possible moment to find
    /// out.</para>
    /// </summary>
    private async Task SaveWBondDoc(WBondDocument doc, Window? owner, bool saveAs = false)
    {
        string? target = saveAs ? null : doc.FilePath;

        if (target is null)
        {
            if (ResolveOwner(owner)?.StorageProvider is not { } storage) return;

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = saveAs ? "Save wBond As" : "Save wBond",
                SuggestedFileName = (doc.FilePath is { } p
                    ? Path.GetFileNameWithoutExtension(p)
                    : "wirebonds") + ".wBond",
                DefaultExtension = "wBond",
                FileTypeChoices = [new FilePickerFileType("wBond design") { Patterns = ["*.wBond"] }],
            });

            if (file?.TryGetLocalPath() is not { } chosen) return;
            target = chosen;
        }

        bool embed = false;
        var layout = doc.ViewModel.ReferenceLayout;

        // Asked only when there is geometry for the answer to be about (owner, 2026-08-16). A wBond
        // holding nothing but wires used to be shown "Include the layout geometry in this file?" with
        // nothing on either side of the choice — see WBondGeometryEmbedding.HasGeometryToEmbed.
        if (layout is not null &&
            WBondGeometryEmbedding.HasGeometryToEmbed(layout.Model) &&
            ResolveOwner(owner) is { } dialogOwner)
        {
            var plan = WBondGeometryEmbedding.Analyze(layout.Model, layout.InstanceBaseDir);
            var choice = await WBondSaveGeometryDialog.ShowAsync(dialogOwner, plan);

            if (choice == WBondSaveGeometryDialog.Choice.Cancel) return;
            embed = choice == WBondSaveGeometryDialog.Choice.Embed;
        }

        try
        {
            doc.Save(target, embed);
            _openDocsByPath[Path.GetFullPath(target)] = doc;
            Messages.Success($"Saved {Path.GetFileName(target)}", target);
            // No live-update seam any more, deliberately: a placed wBond CARRIES its wires
            // (WBondEmbedding), so saving a .wBond cannot change a schematic that was never pointed
            // at it. Bringing new wires into a placed component is File > Import > Wirebond Wires...,
            // which is an explicit act with its own array-drift check.
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not save {Path.GetFileName(target)}: {ex.Message}");
        }
    }

    // ── wbond.md §9.2 routes 2 and 3 — bringing a .wBond into a design ────────

    /// <summary>
    /// Route 2 (M3) — places this <c>.wBond</c>'s wires in the ACTIVE schematic as a component,
    /// wired to nothing, CARRYING the design (<c>WBondEmbedding</c>) rather than referencing it.
    ///
    /// <para><b>Why the project tree as well as File ▸ Import.</b> Route 2 is "this design, into that
    /// schematic", and the tree is where the user is already looking at the design; File ▸ Import ▸
    /// Wirebond Wires… is the same act reached from a file picker, for a design that is not in the
    /// workspace. Both land on <c>SchematicViewModel.CommitWBondPlacement</c>, so there is one
    /// placement path and they cannot drift. The palette tile is the third door and needs no design
    /// at all: it drops a component carrying the default one-array, one-wire design.</para>
    /// </summary>
    public Task AddWBondToSchematicAsync(ProjectTreeNodeViewModel node)
    {
        if (ResolveActiveDocumentForCommands() is not SchematicDocument sd)
        {
            Messages.Warning(
                "Open the schematic you want the wirebond in first — \"Add to Schematic\" places it " +
                "into the schematic that is currently in front.");
            return Task.CompletedTask;
        }

        if (sd.ViewModel.CommitWBondPlacement(node.AbsolutePath))
            Messages.Success($"Placed {Path.GetFileName(node.AbsolutePath)} in {sd.Title}.",
                             node.AbsolutePath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Route 3 (M4) — "someone sent me a package model": creates a cell whose LAYOUT view is the
    /// design's embedded geometry and whose SCHEMATIC view holds the wBond component.
    ///
    /// <para><b>A design carrying no embedded geometry is route 2, not a failure</b> — it is a
    /// perfectly ordinary wBond that references its geometry rather than carrying it, and creating a
    /// cell with an empty layout view would be inventing a view the file never had. It is diverted
    /// to route 2 and told so.</para>
    ///
    /// <para>Reuses <c>WBondGeometryEmbedding.Unpack</c> and <c>CellFolder.CreateCellFolder</c>;
    /// there is deliberately no second unpacker and no second cell-creation path. Unpack writes real
    /// cell folders rather than an in-memory overlay because <c>CellLayoutResolver.Resolve</c>
    /// requires <c>Directory.Exists</c> — WB-C's own finding, unchanged here.</para>
    /// </summary>
    public Task AddWBondAsCellAsync(ProjectTreeNodeViewModel node)
        => AddWBondAsCellFromPathAsync(node.AbsolutePath);

    /// <summary>Route 3, from a path — shared by the project tree and File ▸ Import.</summary>
    public async Task AddWBondAsCellFromPathAsync(string wbondPath)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Warning("Open a workspace first — a cell has to be created somewhere.");
            return;
        }

        WBondDesign design;
        try { design = WBondIo.ReadFile(wbondPath); }
        catch (Exception ex)
        {
            Messages.Error($"Could not read {Path.GetFileName(wbondPath)}: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(design.EmbeddedGeometryJson))
        {
            Messages.Info(
                $"\"{Path.GetFileName(wbondPath)}\" carries no embedded geometry, so there is no " +
                "layout view to create. Use \"Add to Schematic\" to place its wires as a component.",
                wbondPath);
            return;
        }

        if (design.Arrays.Count == 0)
        {
            Messages.Warning(
                $"\"{Path.GetFileName(wbondPath)}\" declares no wire arrays, so its schematic view " +
                "would have no pins. Group its wires into at least one array first.", wbondPath);
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        string workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string suggested    = Path.GetFileNameWithoutExtension(wbondPath);

        var dialog = new InputNameDialog("Add wBond as Cell", "Cell name:", suggested);
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        if (NameValidator.Validate(name) is { } reason)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        string cellDir = Path.Combine(workspaceDir, name);
        if (Directory.Exists(cellDir))
        {
            Messages.Error($"A cell named '{name}' already exists.");
            return;
        }

        try
        {
            CellFolder.CreateCellFolder(workspaceDir, name);

            // ── layout view ───────────────────────────────────────────────────
            // The bundle's own cells are unpacked BESIDE the cell's views (a `geometry/` folder of
            // its own) rather than inside `layout/`, so the cell's layout folder holds exactly one
            // thing — its primary `.clay` — the way every other cell's does.
            string geometryDir = Path.Combine(cellDir, "geometry");
            var unpacked = WBondGeometryEmbedding.Unpack(design.EmbeddedGeometryJson, geometryDir);
            if (unpacked is not ({ } rootView, { } unpackedBaseDir))
            {
                Messages.Error(
                    $"The geometry embedded in \"{Path.GetFileName(wbondPath)}\" is not in a form " +
                    "this version of circuitRF can read; the cell was created without a layout view.");
                _factory.ProjectTreeTool?.Refresh();
                return;
            }

            string layoutDir  = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            string layoutFile = name + CellFolder.ViewExtension(ViewType.Layout);
            string layoutPath = Path.Combine(layoutDir, layoutFile);

            // Unpack resolves the root's instances against a synthetic folder of its own; the .clay
            // is about to live somewhere else, so every CellRef is rebased through the SAME helper
            // Group-into-Cell and Flatten already use — never a second path-arithmetic copy.
            foreach (var inst in rootView.Instances)
                inst.CellRef = LayoutFlatten.RebaseCellRef(inst.CellRef, unpackedBaseDir, layoutDir);

            LayoutPersistence.SaveToFile(layoutPath, rootView);

            // ── schematic view ────────────────────────────────────────────────
            string schematicDir  = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            string schematicFile = name + CellFolder.ViewExtension(ViewType.Schematic);
            string schematicPath = Path.Combine(schematicDir, schematicFile);

            var built = WBondPlacement.TryBuild(
                wbondPath, workspaceDir, ComponentTypeRegistry.Get(SymbolKind.WBond).InstancePrefix + "1");
            if (built.Component is not { } comp)
            {
                Messages.Error(built.Error ?? "The wirebond component could not be created.");
                _factory.ProjectTreeTool?.Refresh();
                return;
            }

            var schematicModel = new SchematicEditModel();
            schematicModel.Components.Add(comp);
            SchematicPersistence.SaveToFile(schematicPath, schematicModel, cellName: name);

            // ── the cell's own .ccell names both primaries ────────────────────
            CellPersistence.SaveToFile(
                Path.Combine(cellDir, CellFolder.CcellFileName),
                new CcellFile { PrimarySchematic = schematicFile, PrimaryLayout = layoutFile });

            _factory.ProjectTreeTool?.Refresh();
            Messages.Success(
                $"Created cell '{name}' from {Path.GetFileName(wbondPath)} " +
                $"({design.Arrays.Count} array(s), {design.WireCount} wire(s)).",
                Path.Combine(cellDir, CellFolder.CcellFileName));

            OpenOrActivateSchematic(schematicPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not create a cell from {Path.GetFileName(wbondPath)}: {ex.Message}");
            _factory.ProjectTreeTool?.Refresh();
        }
    }

    /// <summary>
    /// Imports a wirebond table (WB36 / §9.3). Hand-placing 600 wires is not a workflow, and every
    /// packaging flow already has this table.
    /// </summary>
    /// <param name="window">Owner for the picker.</param>
    /// <remarks>
    /// The table becomes a NEW wBond document rather than merging into whichever one happens to be
    /// open. A merge would need rules the design does not state — what happens to an array of the
    /// same name, whether a repeated import duplicates or replaces — and guessing them would be the
    /// kind of silently-wrong answer a 600-wire table makes expensive to notice.
    /// </remarks>
    [RelayCommand]
    private async Task ImportWireTable(Window? window)
    {
        var owner = ResolveOwner(window);
        if (owner?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Wirebond Table",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Wirebond table (CSV)") { Patterns = ["*.csv"] }],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            var design = WireTableCsv.ReadFile(path);

            var doc = new WBondDocument(new WBondViewModel(design), title: NextWBondTitle());
            TrackNewWBond(doc);

            _scratchWBonds.Add(doc);
            _factory.OpenDocument(doc);

            Messages.Success(
                $"Imported {design.WireCount} wire(s) in {design.Arrays.Count} array(s) from {Path.GetFileName(path)}.",
                path);
        }
        catch (Exception ex)
        {
            // The reader names the offending line; passing that through is the whole value of it.
            Messages.Error($"Could not import {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a <c>.charm</c> into a document of its own — the double-click route (R-h8-10) and the
    /// one File ▸ Open entry point a workspace can offer for one.
    ///
    /// <para>An already-open document for the same file is ACTIVATED rather than opened twice, which
    /// is the same rule every other document type here follows. The load itself happens in the view
    /// (<c>HarmonicaView.LoadCharmFile</c>) — it is the one place that reports §8.1's unresolved
    /// references, and a second loader here would be a second answer about a missing model.</para>
    /// </summary>
    public void OpenHarmonicaPath(string charmPath)
    {
        if (!File.Exists(charmPath)) return;
        string full = Path.GetFullPath(charmPath);

        if (_openDocsByPath.TryGetValue(full, out var already))
        {
            // Through the one helper, not a bare SetActiveDockable: a document torn off into its own
            // window has to be RAISED, not merely selected behind the shell.
            ActivateOpenDocument(already);
            return;
        }

        var doc = new HarmonicaDocument(Path.GetFileNameWithoutExtension(full),
                                        new HarmonicaDocumentViewModel(), full);
        _openDocsByPath[full] = doc;
        _factory.OpenDocument(doc);

        // The view binds on the next layout pass, so the load is deferred to it — asking a document
        // with no view yet to load a file would have nowhere to report what it found.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var unresolved = doc.ViewModel.Harmonica.LoadCharm(
                    File.ReadAllText(full), Path.GetDirectoryName(full));
                if (unresolved.Count > 0)
                    Messages.Warning(string.Join("  ", unresolved.Select(u => u.Message)));
                doc.ViewModel.Harmonica.ResetSchedule();
                doc.ViewModel.Harmonica.RequestScheduledFrame(dragging: false);
            }
            catch (Exception ex) { Messages.Error($"Could not open '{full}': {ex.Message}"); }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// A harmonicaRF document has just been written to <paramref name="path"/>. Registers it by path
    /// (so opening it from the tree activates the tab rather than opening a second one) and refreshes
    /// the tree — open item 6's own gate: a <c>.charm</c> saved into an open workspace appears
    /// WITHOUT a reload.
    /// </summary>
    public void NotifyHarmonicaSaved(HarmonicaDocument doc, string path)
    {
        string full = Path.GetFullPath(path);

        // A Save-As moves the document to a new key; leaving the old one would make the tree open a
        // stale tab for a file that document no longer is.
        foreach (var stale in _openDocsByPath.Where(kv => ReferenceEquals(kv.Value, doc))
                                             .Select(kv => kv.Key).ToList())
            _openDocsByPath.Remove(stale);

        _scratchHarmonicas.Remove(doc);
        _openDocsByPath[full] = doc;

        _factory.ProjectTreeTool?.Refresh();
    }

    /// <summary>Lowest free "Untitled-harmonicaRF-N" across open harmonicaRF documents.</summary>
    private string NextHarmonicaTitle()
    {
        const string prefix = "Untitled-harmonicaRF-";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _scratchHarmonicas) used.Add(d.Id);
        foreach (var d in _openDocsByPath.Values)
            if (d is HarmonicaDocument hd) used.Add(hd.Id);

        for (int n = 1; ; n++)
        {
            var candidate = $"{prefix}{n}";
            if (!used.Contains(candidate)) return candidate;
        }
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

    /// <summary>
    /// Keeps the .cdd node in the project tree in step with an open Data Display's unsaved state —
    /// the same push <c>.csch</c> cells and <c>.ctech</c> files already had
    /// (<see cref="Dock.ProjectTreeTool.SetCellDirty"/>, <see cref="Dock.ProjectTreeTool.SetFileDirty"/>).
    ///
    /// <para><b>Owner-reported, 2026-08-21:</b> <i>"after I saved a .cdd file to my results directory,
    /// the project tree view still indicated it was dirty in the tree."</i> Nothing pushed a .cdd
    /// node's mark at all: it was written only when the tree was REBUILT, which happens on the
    /// workspace window's <c>Activated</c>. A save raises no <c>Activated</c>, so the mark a rescan
    /// had put there stayed until some later, unrelated focus change cleared it.</para>
    ///
    /// <para>The pushed value is <see cref="DataDisplay.ViewModels.DisplayWindowViewModel.HasUnsavedChanges"/>,
    /// not <c>DataDisplayDocumentViewModel.IsDirty</c> — the baseline comparison is the authoritative
    /// answer everywhere else in this file (close, quit, the Window menu, <see cref="IsNodeDirty"/>),
    /// so the tree must not disagree with the prompt that decides whether work is lost.</para>
    /// </summary>
    private void WireDataDisplayTreeDirty(DataDisplayDocument doc)
    {
        doc.ViewModel.Window.DirtyChanged += (_, _) =>
        {
            // Scratch documents have no node to mark; the save that gives them one refreshes the
            // tree instead (SaveDataDisplayDoc), which builds the new node already asking IsNodeDirty.
            if (doc.FilePath is { } fp)
                _factory.ProjectTreeTool?.SetFileDirty(fp, doc.ViewModel.Window.HasUnsavedChanges());
        };
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

    /// <summary>
    /// Opens (or activates) a document chosen by its FILE EXTENSION, wherever it lives on disk.
    ///
    /// <para>This is the by-path twin of <see cref="OpenNode"/>, which dispatches on
    /// <see cref="NodeKind"/> because the project tree has already classified the file. The
    /// operating system has not: a Finder / Explorer / file-manager double-click hands us nothing but
    /// a path, so the extension is all there is to go on. Both funnel to the same
    /// <c>OpenOrActivate*</c> methods, so a file opened from the desktop behaves exactly as the same
    /// file opened from the tree — same session registry, same dedup, same dirty hooks.</para>
    ///
    /// <para><b>Nothing here is workspace-aware, and that is the point.</b> Every opener below is
    /// happy with a path that is outside the open workspace, or with no workspace open at all, and
    /// the ORPHAN/foreign marking is computed live from the file's own ancestor <c>.cws</c> against
    /// <see cref="CurrentWorkspacePath"/> (brief-foreign-documents.md §4). So a file that IS part of
    /// the currently open workspace is opened as an ordinary document of that workspace — no special
    /// case is needed to get that, and adding one would be the way to get it wrong.</para>
    ///
    /// <para>Returns false for an extension circuitRF has no editor for, so a caller can say so
    /// rather than appearing to have opened something.</para>
    /// </summary>
    public bool OpenDocumentByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string abs;
        try   { abs = Path.GetFullPath(path); }
        catch { return false; }

        switch (Path.GetExtension(abs).ToLowerInvariant())
        {
            case ".csch":  OpenOrActivateSchematic(abs);   return true;
            // WB40 — a wirebond cell's wires live in a `.wBond` beside its `.clay` and are attached by
            // stem inside BuildLayoutSessionVm, the one funnel every layout open goes through. So the
            // overlay arrives with the artwork here too, and there is nothing extra to do for it.
            case ".clay":  OpenOrActivateLayout(abs);      return true;
            case ".csym":  OpenOrActivateSymbol(abs);      return true;
            // A `.cdd` may reference data sources relative to a workspace it is not being opened in.
            // Deliberately not guarded against: the display opens and the traces it cannot resolve
            // simply render nothing, which is the useful outcome for "let me look at this file".
            case ".cdd":   OpenOrActivateDataDisplay(abs); return true;
            case ".ctech": OpenOrActivateTech(abs);        return true;
            case ".cem":   OpenOrActivateEmSetup(abs);     return true;
            case ".charm": OpenHarmonicaPath(abs);         return true;
            case ".wbond": OpenWBondPath(abs);             return true;
            default:       return false;
        }
    }

    public void OpenNode(ProjectTreeNodeViewModel node)
    {
        // A Known File has no kind of its own — everything bookmarked in the .cws scans as
        // NodeKind.KnownFile — so a circuitRF document sitting in that list used to fall through to
        // the default no-op and simply not respond to a double-click. Classify it by extension and
        // let it take the ordinary route below: what it opens as is an ORPHAN document, since a
        // bookmarked path is normally outside the workspace and foreignness is decided by the file's
        // own path (brief-foreign-documents.md §1.1), not by the surface it was opened from.
        var kind = node.Kind;
        if (kind == NodeKind.KnownFile)
        {
            if (node.IsDirectory) return;                       // a folder bookmark opens nothing
            kind = WorkspaceScanner.ClassifyFile(node.AbsolutePath);
            if (kind is NodeKind.OtherFile or NodeKind.ColorThemeFile) return;  // no editor for it
            if (!File.Exists(node.AbsolutePath))
            {
                Messages.Error($"'{node.AbsolutePath}' is no longer there.");
                return;
            }
        }

        switch (kind)
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

            // Open item 6, settled: a .charm inside a workspace opens like any other document type.
            case NodeKind.HarmonicaFile:
                OpenHarmonicaPath(node.AbsolutePath);
                return;

            // §10/WB37: all three entry points land on the same document, and a .wBond in a workspace
            // opens like any other document type.
            case NodeKind.WBondFile:
                OpenWBondPath(node.AbsolutePath);
                return;

            case NodeKind.TechFile:
                OpenOrActivateTech(node.AbsolutePath);
                return;

            case NodeKind.EmSetupFile:
                OpenOrActivateEmSetup(node.AbsolutePath);
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
            HookSchematicCanvasFocus(doc);
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
        ActivateOpenDocument(existing);
        return true;
    }

    /// <summary>
    /// Show an already-open document because the USER asked for it — select its tab, bring its
    /// window forward, and give the editor the keyboard.
    ///
    /// <para>The one place that answers "open a document that is already open". It exists because
    /// there were four hand-rolled copies of the tab-selection half and only one of them ever grew
    /// the window half, so the same double-click worked for a schematic and did nothing visible for
    /// a torn-off Data Display. A second copy is how that comes back.</para>
    /// </summary>
    private void ActivateOpenDocument(IDockable dockable)
    {
        _factory.SetActiveDockable(dockable);
        BringDockableWindowToFront(dockable);
    }

    /// <summary>
    /// Brings the OS window showing <paramref name="dockable"/> to the front and gives it focus.
    ///
    /// <para><b>Why <see cref="IFactory.SetActiveDockable"/> is not enough.</b> It selects the TAB
    /// within the dockable's own dock and nothing more. When that dock is a torn-off window, the
    /// window stays exactly where it was — behind the shell, or on another desktop — so
    /// double-clicking the file in the project tree looked like it did nothing at all. Reported for
    /// a `.cdd`, but nothing here is document-type-specific: every kind reached this same path.</para>
    ///
    /// <para><b>Stealing focus is correct here, unlike R-dock-14/15.</b> That rule keeps a PASSIVE
    /// raise (floating panels following the shell on activation) from taking the keyboard. This is a
    /// direct user request to open a document, so the window it is in is exactly where focus belongs
    /// — the opposite case, not an exception to the rule.</para>
    ///
    /// <para>The shell is activated too, not just floats: the project tree is itself a tool that can
    /// be torn off, so "open a docked document from a floating tree" is an ordinary gesture and
    /// needs the shell brought forward the same way.</para>
    /// </summary>
    private void BringDockableWindowToFront(IDockable dockable)
    {
        try
        {
            // A FLOATING root carries its own IDockWindow; the shell's root does not — it is hosted
            // by a DockControl inside WorkspaceWindow — so a docked document falls through to the
            // shell branch. Host is null for a window built but never presented (headless tests),
            // which is why it is pattern-matched rather than assumed.
            Window? target = _factory.FindRoot(dockable) is IRootDock { Window.Host: Window host }
                ? host
                : ResolveOwner(null);

            if (target?.PlatformImpl is null) return;   // already closed — nothing to raise
            target.Activate();

            // The tab is selected and the window is up; the editor inside it still has to take the
            // keyboard, or the user lands on a focused window whose canvas ignores their first
            // keystroke. Deferred: the view may only bind on the next layout pass, and
            // ConsumeActivationFocus covers exactly that ordering.
            (dockable as IActivatableDocument)?.RequestActivationFocus();
        }
        catch (Exception ex)
        {
            Messages.Warning($"Could not bring the document's window to the front: {ex.Message}");
        }
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
        vm.WorkspaceDisplayUnitProvider = WorkspaceDisplayUnit;
        vm.CellResolverProvider         = () => this;
        vm.UpdateWBondLayout            = UpdateLayoutForWBond;
        return vm;
    }

    /// <summary>
    /// The workspace technology's own display unit, or null when nothing resolves — the <c>.ctech</c>'s
    /// <c>DefaultDisplayUnit</c>, read LIVE so a technology change or a workspace switch is picked up
    /// with no re-wiring (the same rule <see cref="WireRetargetSeam"/>'s own resolvers follow).
    ///
    /// <para>Owner, 2026-08-17: a length reported on a schematic surface — a wirebond's total wire
    /// length is the first — has to be in the unit the rest of the workspace is drawn in, not a
    /// hard-coded one.</para>
    /// </summary>
    private LayoutUnit? WorkspaceDisplayUnit() =>
        ResolveTechFor(techRef: null, clayPath: null).Tech?.DefaultDisplayUnit;

    /// <summary>
    /// Registers a VM in the session registry and subscribes to its UndoRedo so
    /// dirty state and cell-tree indicator stay in sync.
    /// </summary>
    private SchematicViewModel RegisterSession(string absNormalizedPath, SchematicViewModel vm)
    {
        // The one place every path-backed session learns what its file is called — a Save As
        // re-registers, so the name follows the file. See SchematicViewModel.DocumentName.
        vm.DocumentName = Path.GetFileName(absNormalizedPath);
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

    /// <summary>
    /// Drops a session AND its unsaved state. Only for leaving a workspace, where the user has already
    /// been prompted and declined to save — see <see cref="SchematicSessionRegistry.DiscardIfUnreferenced"/>.
    /// </summary>
    private void DiscardSessionIfUnreferenced(string absCschPath)
        => _registry.DiscardIfUnreferenced(Path.GetFullPath(absCschPath), IsSessionReferenced);

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
        // R-em-17 — an open .cem re-reads this layout whenever it is edited. Subscribed HERE, at the
        // one place a path-backed session VM is built, rather than in RegisterLayoutSession, which
        // runs again for the same VM on Save-As and would double-subscribe. CurrentLayoutPath is
        // read live for that same reason: a Save-As moves the session, and the .cem that cares is
        // the one pointed at wherever it now lives.
        vm.Model.Changed += (_, _) => NotifyEmSetupsLayoutChanged(vm.CurrentLayoutPath);
        vm.RequestAddLayerToTechnology += OnLayoutRequestAddLayerToTechnology;
        vm.WireSidecarRemoved += OnWireSidecarRemoved;
        WireRetargetSeam(vm);

        // WB40 — a wirebond cell holds a `.wBond` beside its `.clay`, and its wires ride over the
        // artwork as an overlay. Here rather than at either caller because this is the ONE funnel
        // both "open as a tab" and "push in" go through, so a cell pushed into from a parent gets its
        // wires on exactly the same terms as one opened directly. Also where the assembly rules the
        // wire DRC checks against are resolved, for the same reason.
        if (WBondCell.TryAttach(vm, absClayPath, m => Messages.Warning(m)))
            vm.AssemblyRules = ResolveWorkspaceAssemblyRules(absClayPath);

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

    /// <summary>
    /// Drops every dirty session nothing open still refers to, discarding its unsaved state.
    ///
    /// <para>Only ever called while LEAVING a workspace, after the user has been prompted and declined
    /// to save. A session belonging to a document that survives the switch — a torn-off foreign
    /// document, say — is referenced and is left alone.</para>
    /// </summary>
    private void DiscardUnreferencedDirtySessions()
    {
        foreach (string path in _registry.GetOrphanedDirtyPaths(IsSessionReferenced).ToList())
            DiscardSessionIfUnreferenced(path);

        foreach (string path in _layoutRegistry.GetOrphanedDirtyPaths(IsLayoutSessionReferenced).ToList())
            DiscardLayoutSessionIfUnreferenced(path);
    }

    /// <summary>Layout counterpart of <see cref="DiscardSessionIfUnreferenced"/>.</summary>
    private void DiscardLayoutSessionIfUnreferenced(string absClayPath)
    {
        var key = Path.GetFullPath(absClayPath);
        _layoutRegistry.DiscardIfUnreferenced(key, IsLayoutSessionReferenced);
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
    /// A save deleted a layout's <c>.wBond</c> because it has no wires left (WB40c) — the tree is
    /// showing a file that is gone, so it is re-read. Cheap, and only ever on the save that removed
    /// one, rather than on every layout save.
    /// </summary>
    private void OnWireSidecarRemoved(string removedPath) => _factory.ProjectTreeTool?.Refresh();

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

        // The instance is recorded so an overlay drawing world-coordinate geometry over this canvas
        // can walk down into the sub-cell's frame (wbond.md WB27). The layout editor ignores it.
        doc.PushIn(session, label, instance);
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
        doc.CanvasInteracted += () => OnSymbolCanvasInteracted(doc);
    }

    /// <summary>The user just clicked/focused back into this symbol editor's own canvas — the exact
    /// counterpart of <see cref="OnLayoutCanvasInteracted"/>, for the same reason and against the same
    /// failure. A project-tree click routes the Properties panel to a file inspector
    /// (<see cref="PropertiesTool.SetActiveFileInfo"/>, which clears the symbol context on its way
    /// past) without this document ever leaving the DocumentDock's active slot, so
    /// <c>OnDocumentDockPropertyChanged</c> never re-fires and the symbol inspector stays detached from
    /// its VM — clicking a pin or a primitive then changes nothing on screen.</summary>
    private void OnSymbolCanvasInteracted(SymbolEditorDocument doc)
    {
        MarkActiveDocumentPane(doc);
        _factory.SetActiveDockable(doc);
        _factory.PropertiesTool?.SetActiveSymbolEditor(doc.ViewModel);
        SetActiveUndoTarget(doc);
        ActiveSaveScope = SaveScope.SingleDoc;
    }

    /// <summary>
    /// Subscribes a schematic document's canvas-focus signal — the counterpart of
    /// <see cref="OnLayoutCanvasInteracted"/> and <see cref="OnSymbolCanvasInteracted"/>, which the
    /// schematic never had. Called from every path that opens one.
    /// </summary>
    private void HookSchematicCanvasFocus(SchematicDocument doc)
        => doc.CanvasInteracted += () => OnSchematicCanvasInteracted(doc);

    /// <summary>The user just clicked into this schematic's own canvas. Deliberately narrower than the
    /// layout and symbol handlers: those exist to repair a Properties panel a project-tree click had
    /// re-routed, and the schematic's Properties routing has no such hole. What it must do is say
    /// which document PANE is current, because with a side-by-side split that is not something any
    /// dock's ActiveDockable will report — the tab was already active in its own pane.</summary>
    private void OnSchematicCanvasInteracted(SchematicDocument doc)
    {
        MarkActiveDocumentPane(doc);
        _factory.SetActiveDockable(doc);
        SetActiveUndoTarget(doc);

        // Sets ActiveSaveScope from the newly-resolved document AND refreshes every File-menu
        // predicate — which matters beyond appearance on macOS, where a stale-disabled NativeMenu
        // item swallows its own ⌘S rather than letting the key reach the window's binding.
        RaiseFileMenuEnablementChanged();
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
        MarkActiveDocumentPane(doc);
        _factory.SetActiveDockable(doc);
        ActivateLayoutDocumentForProperties(doc);
        SetActiveUndoTarget(doc);
        ActiveSaveScope = SaveScope.SingleDoc;
    }

    /// <summary>
    /// Routes the Properties panel to a wBond document's wire context (wbond.md §6.9).
    ///
    /// <para>The panel follows <c>WBondViewModel.Selection</c>, which BOTH canvases write — so a wire
    /// picked in the layout view and one picked in the profile view land here identically, and this
    /// does not need to know which view did the picking.</para>
    ///
    /// <para>Deliberately NOT also routing the layout context: a wBond document has a reference layout
    /// too, and showing both panels would put two coordinate lists on screen with no way to tell which
    /// one an edit lands in.</para>
    /// </summary>
    private void ActivateWBondDocumentForProperties(WBondDocument doc)
    {
        WatchWBondProperties(doc);
        RefreshWBondPropertiesContext();

        // The DRC panel follows this document's REFERENCE LAYOUT, which is where its wires are
        // installed (WBondDocumentViewModel.OnReferenceLayoutChanged) and therefore the only place an
        // assembly check can run from.
        //
        // <b>It used to be emptied here</b>, and the consequence was not small: the one editor whose
        // entire subject is bond wires was the one place with no way to check them, so the wire rules
        // could only ever be run from a `.clay` that happened to have a wirebond cell in it (owner,
        // 2026-08-19). Nothing about the check is wBond-specific — it is the same run, the same
        // panel and the same waiver store — so this is a routing fix, not a second checker.
        _factory.DrcTool?.SetActiveLayout(doc.ViewModel.ReferenceLayout);

        // §10.1's second surface: here the wires ARE the document, so the two panels follow the
        // document's own editor rather than a cell's. The wBond editor already shows both inline —
        // these are the same controls, and a user who has torn one off keeps seeing it.
        _factory.WBondProfileTool?.SetActiveWBond(doc.ViewModel.Editor, doc.Title);
        _factory.WBondInductanceTool?.SetActiveWBond(doc.ViewModel.Editor, doc.Title);
    }

    /// <summary>
    /// The wBond document whose two selections the Properties panel is currently following, and its
    /// reference layout — held so the subscriptions can be moved when the active document changes.
    /// </summary>
    private WBondDocument? _wbondPropertiesDoc;

    private LayoutEditorViewModel? _wbondPropertiesLayout;

    /// <summary>
    /// Follows BOTH of a wBond document's selections, so the Properties panel shows whichever the user
    /// is actually working in.
    ///
    /// <para><b>The panel used to be pinned to the wire inspector</b> and never routed the layout
    /// context at all — so a cell instance, a PCell or a pad selected in the wBond editor's layout
    /// view had nowhere to be edited (owner, 2026-08-16: PCells "also want to edit these"). The stated
    /// reason for pinning it was that two coordinate lists on screen at once would be ambiguous about
    /// where an edit lands; that reason is intact, and this does not put both up — it picks one, from
    /// the selection that is actually non-empty.</para>
    /// </summary>
    private void WatchWBondProperties(WBondDocument doc)
    {
        if (!ReferenceEquals(_wbondPropertiesDoc, doc))
        {
            if (_wbondPropertiesDoc is { } previous)
            {
                previous.ViewModel.Editor.PropertyChanged -= OnWBondPropertiesSourceChanged;
                previous.ViewModel.PropertyChanged -= OnWBondPropertiesSourceChanged;
            }

            _wbondPropertiesDoc = doc;
            doc.ViewModel.Editor.PropertyChanged += OnWBondPropertiesSourceChanged;
            doc.ViewModel.PropertyChanged += OnWBondPropertiesSourceChanged;
        }

        // The reference layout is created on demand and replaced when a bundle is unpacked, so its
        // subscription is re-pointed separately from the document's.
        var layout = doc.ViewModel.ReferenceLayout;
        if (ReferenceEquals(_wbondPropertiesLayout, layout)) return;

        if (_wbondPropertiesLayout is not null)
            _wbondPropertiesLayout.PropertyChanged -= OnWBondPropertiesSourceChanged;

        _wbondPropertiesLayout = layout;
        if (layout is not null) layout.PropertyChanged += OnWBondPropertiesSourceChanged;
    }

    /// <summary>Drops both subscriptions — called the moment a non-wBond document becomes active.</summary>
    private void StopWatchingWBondProperties()
    {
        if (_wbondPropertiesDoc is { } doc)
        {
            doc.ViewModel.Editor.PropertyChanged -= OnWBondPropertiesSourceChanged;
            doc.ViewModel.PropertyChanged -= OnWBondPropertiesSourceChanged;
        }

        if (_wbondPropertiesLayout is not null)
            _wbondPropertiesLayout.PropertyChanged -= OnWBondPropertiesSourceChanged;

        _wbondPropertiesDoc = null;
        _wbondPropertiesLayout = null;
    }

    private void OnWBondPropertiesSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The layout view-model republishes Overlay on every selection change — that is the signal the
        // layout inspector itself already follows, reused here rather than a second one.
        if (e.PropertyName is not (nameof(WBondViewModel.Selection)
                                or nameof(WBondDocumentViewModel.ReferenceLayout)
                                or nameof(LayoutEditorViewModel.Overlay))) return;

        if (_wbondPropertiesDoc is { } doc)
        {
            WatchWBondProperties(doc);

            // The reference layout is created on demand and replaced when a bundle is unpacked, so
            // the DRC panel has to be re-pointed at the new one exactly as the Properties panel is —
            // otherwise a wBond that gained its artwork mid-session keeps a check pointed at the
            // layout it no longer has.
            if (e.PropertyName == nameof(WBondDocumentViewModel.ReferenceLayout))
                _factory.DrcTool?.SetActiveLayout(doc.ViewModel.ReferenceLayout);
        }

        RefreshWBondPropertiesContext();
    }

    /// <summary>
    /// Picks the context: the WIRE inspector whenever wires are selected, the LAYOUT inspector when
    /// they are not and layout geometry is, and the wire inspector (empty) when neither is — which is
    /// the resting state a wBond editor should look like.
    ///
    /// <para>Wires win a tie because they are what this editor is FOR: a layout selection can outlive
    /// a wire press (the overlay consumes a press on a wire without the layout editor seeing it, so it
    /// never gets to clear its own), and reading a stale one as the user's intent would flip the panel
    /// away from the wire they just clicked.</para>
    /// </summary>
    private void RefreshWBondPropertiesContext()
    {
        if (_factory.PropertiesTool is not { } panel) return;
        if (_wbondPropertiesDoc is not { } doc) return;

        bool wires = !doc.ViewModel.Editor.Selection.IsEmpty;
        var layout = doc.ViewModel.ReferenceLayout;

        bool geometry = !wires && layout is not null
                     && (layout.SelectedIndices.Count > 0 || layout.SelectedInstanceIndices.Count > 0);

        if (geometry) panel.SetActiveLayout(layout);
        else panel.SetActiveWire(doc.ViewModel.Editor);
    }

    private void ActivateLayoutDocumentForProperties(LayoutDocument doc)
    {
        WatchLayoutFrameProperties(doc);
        WatchWirebondCellProperties(doc);
        RefreshLayoutPropertiesContext(doc);
        // L5b: the violations panel follows the same active-layout signal — a DRC result belongs to
        // the layout that was checked, so showing one document's violations beside another document's
        // artwork would be worse than showing none.
        _factory.DrcTool?.SetActiveLayout(doc.ActiveViewModel);

        // wbond.md §10.1 (WB39a/M3): so do the two wBond panels, and that is the milestone — push into
        // a wirebond cell (WB40) and its wires' profile and its arrays' inductance are right there,
        // with no second editor to open. A layout with no wires leaves both saying so.
        _factory.WBondProfileTool?.SetActiveLayout(doc.ActiveViewModel);
        _factory.WBondInductanceTool?.SetActiveLayout(doc.ActiveViewModel);
    }

    /// <summary>
    /// Picks the Properties context for a LAYOUT document: the WIRE inspector when wires are selected,
    /// the layout inspector otherwise.
    ///
    /// <para><b>A wirebond cell had no wire routing at all</b> (owner, 2026-08-17: "the Properties
    /// inspector does not update when I click on a wire in the wBond layout hosted canvas"). Clicking a
    /// wire changed <c>WireEditor.Selection</c>, which the layout inspector cannot see and nothing else
    /// was watching — so the panel went on showing the artwork's own (empty) selection.</para>
    ///
    /// <para>The rule is <see cref="RefreshWBondPropertiesContext"/>'s, deliberately: wires win a tie
    /// because a LAYOUT selection can outlive a wire press — the overlay consumes a press on a wire
    /// without the layout editor seeing it, so it never gets to clear its own — and reading that stale
    /// one as the user's intent would flip the panel away from the wire they just clicked. A layout with
    /// no wires at all takes the layout branch unconditionally, exactly as it always did.</para>
    /// </summary>
    private void RefreshLayoutPropertiesContext(LayoutDocument doc)
    {
        if (_factory.PropertiesTool is not { } panel) return;

        var vm = doc.ActiveViewModel;

        if (vm.WireEditor is { } wires && !wires.Selection.IsEmpty) panel.SetActiveWire(wires);
        else panel.SetActiveLayout(vm);
    }

    /// <summary>
    /// Follows the active layout document's NAVIGATION FRAME, so Push In / Pop Out re-route every panel
    /// that reads off <c>ActiveViewModel</c>.
    ///
    /// <para><b>Owner report, 2026-08-25: "sometimes the Properties Inspector does not update to the
    /// object I selected in the Layout Editor… clicking on canvas and then clicking back on the object
    /// still does not update."</b> The panel is pointed at ONE <see cref="LayoutEditorViewModel"/> by
    /// <see cref="PropertiesTool.SetActiveLayout"/>, and it follows that instance's <c>Overlay</c>
    /// notifications and nothing else. A push-in swaps which instance the canvas is editing without the
    /// document ever leaving <c>DocumentDock.ActiveDockable</c> — so the panel went on listening to the
    /// PARENT frame, and every selection made in the sub-cell was invisible to it.</para>
    ///
    /// <para><b>Why clicking away and back could not clear it, which is what makes this the reported
    /// bug rather than a near miss.</b> The one repair path — <see cref="OnLayoutCanvasInteracted"/> —
    /// is raised from the canvas's <c>GotFocus</c>, and GotFocus does not re-fire when focus is already
    /// on the canvas. Push-in's own gesture is a double-click ON the canvas, so focus never leaves it:
    /// the panel is stuck for the rest of the session in that frame. Pushing in from the TOOLBAR button
    /// moves focus to the button, so the next canvas click does repair it — which is exactly why the
    /// symptom is intermittent.</para>
    ///
    /// <para>Re-running <see cref="ActivateLayoutDocumentForProperties"/> (rather than just re-pointing
    /// the Properties panel) is deliberate: the DRC panel and the two wBond panels are pointed at
    /// <c>ActiveViewModel</c> in that same method and had the identical gap — a pushed-in frame's
    /// violations and wires were the parent's.</para>
    /// </summary>
    private void WatchLayoutFrameProperties(LayoutDocument doc)
    {
        if (ReferenceEquals(_layoutFramePropertiesDoc, doc)) return;

        if (_layoutFramePropertiesDoc is { } previous)
            previous.ActiveViewModelChanged -= OnLayoutFrameChangedForProperties;

        _layoutFramePropertiesDoc = doc;
        doc.ActiveViewModelChanged += OnLayoutFrameChangedForProperties;
    }

    /// <summary>Drops it — called the moment a non-layout document becomes active, on the same "no
    /// document type can leave another's context on screen" principle as the DRC panel.</summary>
    private void StopWatchingLayoutFrameProperties()
    {
        if (_layoutFramePropertiesDoc is { } doc)
            doc.ActiveViewModelChanged -= OnLayoutFrameChangedForProperties;

        _layoutFramePropertiesDoc = null;
    }

    private LayoutDocument? _layoutFramePropertiesDoc;

    // Re-entry is not possible: ActivateLayoutDocumentForProperties calls WatchLayoutFrameProperties
    // with the SAME document, which returns immediately, and nothing on this path raises
    // ActiveViewModelChanged.
    private void OnLayoutFrameChangedForProperties(object? sender, EventArgs e)
    {
        if (_layoutFramePropertiesDoc is { } doc) ActivateLayoutDocumentForProperties(doc);
    }

    /// <summary>
    /// Follows a wirebond cell's WIRE selection, so the Properties panel can switch to it. Re-pointed on
    /// every activation and on every push-in, since each frame has its own wires (or none).
    /// </summary>
    private void WatchWirebondCellProperties(LayoutDocument doc)
    {
        var wires = doc.ActiveViewModel.WireEditor;
        if (ReferenceEquals(_wirebondCellPropertiesEditor, wires))
        {
            _wirebondCellPropertiesDoc = doc;
            return;
        }

        if (_wirebondCellPropertiesEditor is not null)
            _wirebondCellPropertiesEditor.PropertyChanged -= OnWirebondCellSelectionChanged;

        _wirebondCellPropertiesDoc = doc;
        _wirebondCellPropertiesEditor = wires;

        if (wires is not null) wires.PropertyChanged += OnWirebondCellSelectionChanged;
    }

    /// <summary>Drops it — called the moment a non-layout document becomes active.</summary>
    private void StopWatchingWirebondCellProperties()
    {
        if (_wirebondCellPropertiesEditor is not null)
            _wirebondCellPropertiesEditor.PropertyChanged -= OnWirebondCellSelectionChanged;

        _wirebondCellPropertiesEditor = null;
        _wirebondCellPropertiesDoc = null;
    }

    private LayoutDocument? _wirebondCellPropertiesDoc;
    private WBond.WBondViewModel? _wirebondCellPropertiesEditor;

    private void OnWirebondCellSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WBond.WBondViewModel.Selection)) return;
        if (_wirebondCellPropertiesDoc is { } doc) RefreshLayoutPropertiesContext(doc);
    }

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
            case NodeKind.EmSetupFile:
            {
                var emKey = Path.GetFullPath(node.AbsolutePath);
                return _openDocsByPath.Values.OfType<EmSetupDocument>().Any(d =>
                    d.IsDirty && string.Equals(Path.GetFullPath(d.FilePath), emKey, StringComparison.OrdinalIgnoreCase));
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
            case NodeKind.EmSetupFile:
                SaveEmSetupByPath(node.AbsolutePath);
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

    private void SaveEmSetupByPath(string absPath)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<EmSetupDocument>().FirstOrDefault(d =>
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
    public void OpenWorkspacePath(string cwsPath) => _ = OpenWorkspacePathAsync(cwsPath);

    /// <summary>
    /// The awaitable form of <see cref="OpenWorkspacePath"/>, for the one caller that has to know
    /// when the switch has finished: a desktop double-click can name a workspace AND documents at
    /// once, and a document opened before the switch completes is one the switch discards. Routed
    /// through the Recent command like every other workspace open, so the dirty-work prompt and the
    /// missing-file pruning happen exactly once, in one place.
    /// </summary>
    public Task OpenWorkspacePathAsync(string cwsPath) => OpenRecentWorkspaceCommand.ExecuteAsync(cwsPath);

    /// <inheritdoc/>
    void ITreeActions.ClearRecentWorkspaces()
    {
        _recentWorkspaces.Clear();
        SaveRecent();
        RebuildRecentMenuItems();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Case-insensitive, to match <see cref="PushRecent"/>'s own de-duplication — the list can hold a
    /// path whose spelling differs from the one the row was built from only if the two compare equal
    /// there, so removal has to use the same comparison or the entry would come back on the next scan.
    /// Nothing on disk is touched: the workspace stays where it is and can be reopened from Open.
    /// </remarks>
    void ITreeActions.RemoveRecentWorkspace(string cwsPath)
    {
        if (string.IsNullOrWhiteSpace(cwsPath)) return;
        if (_recentWorkspaces.RemoveAll(p =>
                string.Equals(p, cwsPath, StringComparison.OrdinalIgnoreCase)) == 0) return;
        SaveRecent();
        RebuildRecentMenuItems();
    }

    // ── ITreeActions: workspace-level items on the tree header ────────────────
    //  Each routes to the command the File menu already uses rather than repeating its work — the
    //  dirty-work prompt, the generated-cells cleanup and the picker all live in one place, and a
    //  second copy of any of them would drift.

    /// <inheritdoc/>
    public Task CloseWorkspaceFromTreeAsync() => CloseWorkspaceCommand.ExecuteAsync(null);

    /// <inheritdoc/>
    public Task ArchiveWorkspaceFromTreeAsync() => ArchiveWorkspaceCommand.ExecuteAsync(null);

    /// <inheritdoc/>
    public Task OpenWorkspaceFromTreeAsync() => OpenWorkspaceCommand.ExecuteAsync(null);

    /// <inheritdoc/>
    public Task UnarchiveWorkspaceFromTreeAsync() => UnarchiveWorkspaceCommand.ExecuteAsync(null);

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
    /// <remarks>
    /// A BROKEN reference reveals the nearest folder that still exists. A Known File whose target has
    /// been moved or deleted is exactly when the user reaches for Reveal — to go and look — and the
    /// old behaviour handed the path straight to the file manager, which opens somewhere unhelpful or
    /// nothing at all. Revealing <c>/myfiles/folder1/</c> for a missing
    /// <c>/myfiles/folder1/test.txt</c> puts them where the file was. Said out loud, because a
    /// silently substituted target would read as "the file is fine".
    /// </remarks>
    public void Reveal(ProjectTreeNodeViewModel node)
    {
        var path = node.AbsolutePath;
        if (File.Exists(path) || Directory.Exists(path))
        {
            RevealPathInFileManager(path);
            return;
        }

        var nearest = FileReveal.NearestExistingDirectory(path);
        if (nearest is null)
        {
            Messages.Error($"'{path}' is no longer there, and neither is any folder above it.");
            return;
        }

        Messages.Warning($"'{path}' is no longer there — showing '{nearest}' instead.");
        RevealPathInFileManager(nearest);
    }

    public void RevealPath(string absolutePath)
    {
        // A recent workspace can have been moved or deleted since it was recorded, and the reveal
        // itself would not say so — the file manager simply opens somewhere unhelpful. Checked here
        // rather than pruning the entry: the user asked where it is, and "it is not there any more"
        // is the answer to that question, not a reason to silently drop the row they clicked.
        if (!Directory.Exists(absolutePath) && !File.Exists(absolutePath))
        {
            Messages.Error($"'{absolutePath}' is no longer there.");
            return;
        }
        RevealPathInFileManager(absolutePath);
    }

    /// <summary>
    /// Shows a path in the platform's own file manager. Extracted so every surface that offers
    /// "Reveal" goes through one implementation — the platform detection and the per-platform
    /// argument forms are exactly the sort of thing a second copy gets subtly wrong.
    /// </summary>
    private void RevealPathInFileManager(string path)
    {
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
                // /select, highlights the file in Explorer; works for both files and folders.
                // ArgumentList form, matching every other launch site — `/select,<path>` is ONE
                // argument, so it is added as one rather than assembled into a command line here.
                Process.Start(new ProcessStartInfo("explorer", [$"/select,{path}"])
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
    public async Task CopyKnownFileToWorkspaceAsCellAsync(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Error("Open a workspace first — a cell is created inside one.");
            return;
        }

        var source = node.AbsolutePath;

        if (CellViewFileValidator.ViewTypeFor(source) is not { } viewType)
        {
            Messages.Error($"'{Path.GetFileName(source)}' is not a schematic, symbol or layout.");
            return;
        }

        // Validation runs BEFORE the name prompt, not after it: a file that can never become a cell
        // should not first ask the user to name one. Nothing is created on this path.
        if (CellViewFileValidator.DescribeDefect(source, viewType) is { } defect)
        {
            Messages.Error($"Cannot create a cell from '{Path.GetFileName(source)}' — {defect}");
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dialog = new InputNameDialog(
            "Copy to Workspace as Cell", "Cell name:", Path.GetFileNameWithoutExtension(source));
        var name = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid cell name: {reason}");
            return;
        }

        // The workspace ROOT, so the new cell lands at the top level of the tree.
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var newCellDir   = Path.Combine(workspaceDir, name);
        if (Directory.Exists(newCellDir))
        {
            Messages.Error($"A cell named '{name}' already exists.");
            return;
        }

        try
        {
            CellFolder.CreateCellFolder(workspaceDir, name);

            // A byte-for-byte copy, named after the cell — the same convention New Cell uses for its
            // primary schematic, and what makes the copy the SOLE file in its sub-folder and hence
            // that view's primary with no .ccell entry needed (CellFolder.ResolvePrimary, branch 2).
            // The .csch's own recorded CellName is left alone: it is re-derived from the file name on
            // every save, so rewriting it here would be a second authority for no gain.
            var dest = Path.Combine(
                CellFolder.SubFolderPath(newCellDir, viewType),
                name + CellFolder.ViewExtension(viewType));
            File.Copy(source, dest);

            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Created", dest);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create cell: {ex.Message}");
        }
    }

    // ── SPICE model cards and subcircuits ────────────────────────────────────

    /// <inheritdoc/>
    public Task CreateCellFromModelCardAsync(ProjectTreeNodeViewModel node)
        => CreateCellFromModelCardFromPathAsync(node.AbsolutePath);

    /// <summary>
    /// Turns one <c>.model</c> card or one <c>.subckt</c> definition into a cell — the project
    /// tree's "Copy to Workspace as Cell…" on a SPICE file, and File ▸ Import ▸ Model or
    /// Subcircuit…, are the SAME method, so the two doors cannot disagree about what an import
    /// produces.
    ///
    /// <para>The point of the whole feature is that a user never types a parameter table or a
    /// netlist in by hand, so everything the file states is carried and everything it states that
    /// circuitRF has no home for is REPORTED. An import that says only "created" would let a
    /// dropped substrate junction reach a measurement.</para>
    /// </summary>
    public async Task CreateCellFromModelCardFromPathAsync(string modelPath)
    {
        if (CurrentWorkspacePath is null)
        {
            Messages.Error("Open a workspace first — a cell is created inside one.");
            return;
        }

        // The file is READ BEFORE the user is asked to name anything: a file with nothing circuitRF
        // can build should not first ask what to call the cell it is not going to create. This is
        // CopyKnownFileToWorkspaceAsCellAsync's own rule, and for the same reason.
        var scan = SpiceCellImport.Scan(modelPath);

        string fileName = Path.GetFileName(modelPath);

        // A file that declares `.lib` SECTIONS and was read with none chosen has read nothing on
        // purpose — sections are alternatives. So it reaches the picker (which is where the section
        // is chosen) rather than the "holds nothing" refusal, which would be true of the read and
        // false of the file.
        bool offersSections = scan.SectionNames.Count > 0;

        if (scan.Error is { } error && !offersSections)
        {
            Messages.Error(error, modelPath);
            return;
        }

        if (scan.Supported.Count == 0 && !offersSections)
        {
            // Every refusal, not just the first: a kit file holding four MOSFETs and a bead should
            // say so once rather than one card at a time across four attempts.
            Messages.Error(
                $"{fileName} holds {scan.Candidates.Count} model card(s) and subcircuit(s), and "
                + "circuitRF can build none of them.", modelPath);
            foreach (var c in scan.Candidates)
                Messages.Info($"  {c.TypeLabel} {c.Name}: {c.Detail}", modelPath);
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        // One buildable definition in a file of one is not a choice, and a dialog offering a single
        // option is a click that asks nothing. Anything else goes to the picker — which lists the
        // refused ones too, with their reasons, and offers the sections when there are any.
        var pick = scan.Candidates.Count == 1 && !offersSections
            ? new SpiceCellPick([scan.Supported[0]], scan, null)
            : await SpiceCellPickerDialog.ShowAsync(mainWindow, modelPath, scan);
        if (pick is null) return;

        // ONE definition is still named by the user — that is the gesture as it has always been.
        // SEVERAL are not: a dialog per definition is worse than none, and a .subckt name is already
        // a folder name in every file that ships one, which is why it is the suggestion in the
        // single case too.
        var chosen = new List<(SpiceCellCandidate Candidate, string CellName)>();

        if (pick.Candidates.Count == 1)
        {
            var dialog = new InputNameDialog(
                "Import SPICE Definition as Cell", "Cell name:",
                SpiceCellImport.SuggestCellName(pick.Candidates[0]));
            var name = await dialog.ShowDialog<string?>(mainWindow);
            if (name is null) return;

            if (NameValidator.Validate(name) is { } reason)
            {
                Messages.Error($"Invalid cell name: {reason}");
                return;
            }

            chosen.Add((pick.Candidates[0], name));
        }
        else
        {
            foreach (var c in pick.Candidates)
                chosen.Add((c, SpiceCellImport.SuggestCellName(c)));
        }

        string workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;

        try
        {
            var result = SpiceCellImport.WriteMany(workspaceDir, chosen, pick.Scan, modelPath);

            _factory.ProjectTreeTool?.Refresh();

            Messages.Success(result.Report[0], result.SchematicPath);
            foreach (string line in result.Report.Skip(1))
                Messages.Warning(line, result.SchematicPath);

            OpenOrActivateSchematic(result.SchematicPath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not create a cell from {fileName}: {ex.Message}", modelPath);
            _factory.ProjectTreeTool?.Refresh();
        }
    }

    /// <summary>
    /// File ▸ Import ▸ Model or Subcircuit… — the same import, reached without bookmarking the
    /// file first.
    /// </summary>
    /// <remarks>
    /// The picker's filter is WIDER than the project tree's own extension test, deliberately: the
    /// tree offers its menu item on a bookmarked file with nothing having read it, so the extension
    /// is the whole of what decides there and a wide net would put a dead item on most of a
    /// workspace. Here the user has already said what the file is by choosing it, and vendor cards
    /// arrive as <c>.lib</c>, <c>.txt</c> and <c>.cir</c> at least as often as <c>.model</c>.
    /// </remarks>
    [RelayCommand]
    private async Task ImportModelCard(Window? window)
    {
        var owner = ResolveOwner(window);
        if (owner?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import SPICE Model Card or Subcircuit",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SPICE model card or subcircuit")
                {
                    Patterns =
                    [
                        "*.model", "*.mod", "*.subckt", "*.sub", "*.ckt",
                        "*.lib", "*.cir", "*.sp", "*.spi", "*.txt",
                    ],
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        await CreateCellFromModelCardFromPathAsync(path);
    }

    /// <inheritdoc/>
    public void RemoveKnownFile(ProjectTreeNodeViewModel node)
    {
        if (CurrentWorkspacePath is null) return;
        var path = node.AbsolutePath;
        CwsFile cws;
        try   { cws = WorkspacePersistence.LoadFromFile(CurrentWorkspacePath); }
        catch { cws = new CwsFile(); }

        // Match on the RESOLVED path, not the stored string: R-stb-10/11 stores an in-workspace
        // reference workspace-relative (with `/` separators) and only an outside one absolutely, so
        // comparing the node's absolute path against the raw entry never matches the relative form
        // and the removal silently does nothing. A hidden file opted in by name (.DS_Store, *.source)
        // is exactly that shape and is still shown in the tree, so this is reachable.
        var wsRoot = Path.GetDirectoryName(CurrentWorkspacePath) ?? "";
        cws.KnownFiles.RemoveAll(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WorkspaceRefs.Resolve(p, wsRoot), path, StringComparison.OrdinalIgnoreCase));
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
        // A New Cell always creates that cell's primary schematic (R-cc-1), so this IS a
        // schematic-creation prompt and offers the shipped templates.
        dialog.OfferSchematicTemplates(ShippedSchematicTemplates.All);
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
        await CreateAndOpenSchematicFileAsync(newCellDir, name, name, dialog.SelectedTemplate);
    }

    // ── New Folder (tree context menu + tree-header button) ─────────────────────
    //
    // Cells in a sub-folder are not a new capability — WorkspaceScanner.BuildUserFolderNode already
    // recurses, InstanceCellChoices.Collect already finds them and shows their relative path, and a
    // CellRef is a relative path that resolves from anywhere. What was missing was any way to make a
    // folder without leaving the application, which is what turned "organise a 50-cell board import"
    // into a file-manager task.
    //
    // Deliberately create-only: MOVING an existing cell into a folder would have to rewrite every
    // CellRef pointing at it, and CellUsageScanner.RewriteCellReferences today matches and rewrites
    // the last path SEGMENT (it was built for Rename), not a path prefix. Moving is therefore done in
    // the file manager for now, where the tree's existing broken-reference warning is what reports a
    // ref that no longer resolves — rather than an in-app move that rewrites references silently.

    /// <inheritdoc/>
    public Task NewFolderAsync(ProjectTreeNodeViewModel parentNode)
        => CreateFolderAsync(parentNode.AbsolutePath);

    /// <inheritdoc/>
    public Task NewFolderInWorkspaceAsync()
        => CurrentWorkspacePath is null
            ? Task.CompletedTask
            : CreateFolderAsync(Path.GetDirectoryName(CurrentWorkspacePath)!);

    [RelayCommand(CanExecute = nameof(CanNewFolderInWorkspace))]
    private Task NewFolderInWorkspace() => NewFolderInWorkspaceAsync();
    private bool CanNewFolderInWorkspace() => CurrentWorkspacePath is not null;

    private async Task CreateFolderAsync(string parentDir)
    {
        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dialog = new InputNameDialog("New Folder", "Folder name:");
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        // The same validator a cell name goes through: a folder here IS a workspace path segment,
        // and the characters that break one break the other.
        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid folder name: {reason}");
            return;
        }

        string newDir = Path.Combine(parentDir, name);
        if (Directory.Exists(newDir))
        {
            Messages.Error($"A folder named '{name}' already exists here.");
            return;
        }

        try
        {
            Directory.CreateDirectory(newDir);
            _factory.ProjectTreeTool?.Refresh();
            Messages.Success("Created", newDir);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create folder: {ex.Message}");
        }
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
        // A New Cell always creates that cell's primary schematic (R-cc-1), so this IS a
        // schematic-creation prompt and offers the shipped templates.
        dialog.OfferSchematicTemplates(ShippedSchematicTemplates.All);
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
        await CreateAndOpenSchematicFileAsync(newCellDir, name, name, dialog.SelectedTemplate);
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
        dialog.OfferSchematicTemplates(ShippedSchematicTemplates.All);
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid schematic name: {reason}");
            return;
        }

        await CreateAndOpenSchematicFileAsync(cellDir, cellNode.Name, name, dialog.SelectedTemplate);
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
    private async Task<bool> CreateAndOpenSchematicFileAsync(
        string cellDir, string cellName, string fileNameWithoutExt,
        ShippedSchematicTemplate? template = null)
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
            // Empty by default; a chosen template is parsed through the ordinary .csch reader, so
            // what lands here is exactly what the editor would have loaded from such a file. The
            // destination directory is supplied up front so any relative CellRef resolves from the
            // moment the model exists rather than only after its first save.
            //
            // A template that fails to parse must not cost the user their cell: it is reported and
            // the schematic is created empty, which is what they would have got without templates.
            var model = new SchematicEditModel();
            if (template is not null)
            {
                try
                {
                    model = ShippedSchematicTemplates.Load(template, schematicDir);
                }
                catch (Exception ex)
                {
                    Messages.Warning($"Template '{template.DisplayName}' could not be read ({ex.Message}) — created an empty schematic instead.");
                }
            }
            SchematicPersistence.SaveToFile(filePath, model, cellName: cellName);

            _factory.ProjectTreeTool?.Refresh();

            // Open in a schematic content tab (materialized — has a real file path).
            // Use BuildSessionVm so wiring matches GetOrCreateSession exactly.
            var vm  = BuildSessionVm(model);
            RegisterSession(filePath, vm);
            var doc = new SchematicDocument(fileNameWithoutExt + ext, vm, filePath) { Messages = Messages, Hierarchy = this };
            HookSchematicCanvasFocus(doc);
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

        // Optionally rename the primary file of EVERY view the cell has.
        //
        // Layout was missing here and nothing else made up for it: a renamed cell kept a `.clay`
        // named after the old cell, so the folder said one name and the file inside it said another
        // — and the .ccell's own PrimaryLayout still pointed at the old file name, which is why the
        // view kept opening and the drift stayed invisible. ViewExtension/ResolvePrimary/
        // UpdateCcellPrimary all handled Layout already; only this list did not.
        if (renamePrimaries)
        {
            foreach (var viewType in new[] { ViewType.Schematic, ViewType.Symbol, ViewType.Layout })
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

                    // The wires are attached to the .clay BY STEM (WB40) — layout/x.clay pairs with
                    // layout/x.wBond and with nothing else. So renaming the .clay without its .wBond
                    // does not leave a cosmetic mismatch, it DETACHES the wires: the layout reopens
                    // empty while the bonds sit in a file now paired with nothing. The two renames
                    // are one operation, and this is the only place that can do them together.
                    if (viewType == ViewType.Layout)
                        RenamePairedWBond(workspaceDir, subDir,
                            Path.GetFileNameWithoutExtension(res.ResolvedName), newName,
                            oldName, newName);
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
                case ViewType.Layout:    ccell.PrimaryLayout    = newPrimaryFileName; break;
            }
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch { /* non-fatal: .ccell update is best-effort for alpha */ }
    }

    /// <summary>
    /// Renames the <c>.wBond</c> paired with a just-renamed <c>.clay</c>, and repoints every schematic
    /// that links it.
    ///
    /// <para>A <c>.wBond</c> is not a view type and has no <c>.ccell</c> primary of its own, so nothing
    /// else in the rename covers it — but it is not optional decoration either: <c>WBondCell.Resolve</c>
    /// attaches wires to artwork by SHARED STEM, so the <c>.clay</c> rename above is what makes this
    /// necessary. Only the file that was actually paired with the old artwork is touched; a
    /// differently-named <c>.wBond</c> in the same folder belongs to a different <c>.clay</c> (or to
    /// nobody, which <c>WBondCell</c> already reports as an orphan) and is left alone.</para>
    ///
    /// <para>The rename and the repoint are one operation: a schematic stores its link as a path
    /// relative to itself, so the folder rename alone left it resolving fine and it is renaming the
    /// FILE that would break it. A repoint failure is reported and the rename is left standing — the
    /// file has already moved by then, and saying so beats a half-undone state nothing records.</para>
    /// </summary>
    private void RenamePairedWBond(
        string workspaceDir, string layoutDir,
        string oldStem, string newStem, string oldCellName, string newCellName)
    {
        var (outcome, error) = WBondCell.RenamePairedWires(layoutDir, oldStem, newStem);
        switch (outcome)
        {
            case WBondCell.RenameOutcome.NothingToRename:
                return;
            case WBondCell.RenameOutcome.Blocked:
                Messages.Warning(
                    $"Skipped renaming wirebonds: '{newStem}{WBondCell.FileExtension}' already exists, " +
                    $"so '{oldStem}{WBondCell.FileExtension}' is now attached to no layout.");
                return;
            case WBondCell.RenameOutcome.Failed:
                Messages.Warning($"Could not rename wirebond design: {error}");
                return;
        }

        var repointed = CellUsageScanner.RewriteWBondLinks(
            workspaceDir, layoutDir, oldStem, newStem, oldCellName, newCellName, out var failed);
        foreach (var f in failed)
            Messages.Warning($"Wirebond link rewrite failed: {f}");
        if (repointed.Count > 0)
            Messages.Info(
                $"Repointed {repointed.Count} schematic(s) at '{newStem}{WBondCell.FileExtension}'.");
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
            ActivateOpenDocument(existingDoc);
            return;
        }

        var newVm  = new DataDisplayDocumentViewModel();
        var newDoc = new DataDisplayDocument(Path.GetFileNameWithoutExtension(cddPath), newVm);
        _openDocsByPath[cddPath] = newDoc;   // register early so a re-entrant open dedups against it
        newVm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        newVm.Window.GetResultsRootAction = GetResultsRoot;
        WireDataDisplayLibraryEvents(newVm);
        WireDataDisplayTreeDirty(newDoc);
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
            // §4 (brief-dd-loadpull-contour-ux-round8): a loadpull run gets two contour plots
            // (Pout dBm, Efficiency) instead of the single arbitrary-cube trace below, which is
            // meaningless for loadpull data (anchor 5).
            if (entry.Data is { } loadpullDs && LoadpullRecognition.IsLoadpull(loadpullDs))
            {
                populated = PopulateLoadpullContourPlots(newVm, loadpullDs);
            }
            else
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
    /// §4 (brief-dd-loadpull-contour-ux-round8): populates the auto-created Data Display for a
    /// recognized loadpull run with two contour plots — Pout (dBm) left, Efficiency right, both at
    /// 3 dB compression — instead of <see cref="AutoOpenOrCreateDataDisplayAsync"/>'s normal single
    /// arbitrary-cube trace (meaningless for loadpull data, per the auto-create issue this brief
    /// fixes). Reuses the tab's single already-seeded plot as the LEFT plot (anchor 5 — only one
    /// plot exists after a plain auto-create; exactly two must exist here, never three); adds
    /// exactly one more via <c>AddPlot</c> with an explicit position (anchor 8 — never left to
    /// <c>ComputeNewPlotPosition</c>'s inferred grid). A metric whose cube is absent is skipped
    /// rather than producing an empty plot for it. Returns true when at least one contour was
    /// created (suppresses the caller's "no default plot" warning).
    ///
    /// <c>internal</c> (not <c>private</c>) solely so <c>CircuitRF.Ui.Tests</c> can exercise this
    /// directly via <c>InternalsVisibleTo</c> — <see cref="WorkspaceViewModel"/> itself cannot be
    /// constructed headlessly, but this method needs no instance state.
    /// </summary>
    internal static bool PopulateLoadpullContourPlots(DataDisplayDocumentViewModel newVm, DataSet ds)
    {
        var dataDisplay = newVm.Window.DataDisplay;
        var leftPlot     = dataDisplay?.Plots.FirstOrDefault();
        if (dataDisplay is null || leftPlot is null) return false;

        var views = LoadpullRecognition.FindLoadpullViews(ds);
        if (views.Count == 0) return false;   // unreachable — caller already checked IsLoadpull
        var view = views[0];

        bool HasMetricCube(string metric)
        {
            string spec = string.IsNullOrEmpty(view.Group) ? metric : $"{view.Group}.{metric}";
            return ds.Contains(spec);
        }

        // §4: Pout (dBm) left, Efficiency right — canonical cube names the engine emits
        // (LoadpullRecognition.FomCubes / AutoFillSummary's column list).
        var metrics = new[] { "Pout_dBm", "Efficiency" }.Where(HasMetricCube).ToList();
        if (metrics.Count == 0) return false;

        // §4a: one grid-plane decision shared by both plots, from the recognized view's GammaLoad
        // geometry — never from which termination cube happens to exist (both always do).
        var plane    = LoadpullRecognition.DetectGridPlane(ds, view);
        var plotType = plane == SurfacePlane.Gamma ? PlotType.Smith : PlotType.Rect;

        // Size per plot type — square for Smith/Polar, width/RectAspectRatio for Rect (the same
        // rule DataDisplayViewModel.AddPlot and brief DD-P §2 apply).
        bool   square = plotType is PlotType.Smith or PlotType.Polar;
        double w      = square ? 420.0 : 520.0;
        double h;
        if (square) h = 420.0;
        else
        {
            double ratio = AppSettingsViewModel.Instance.RectAspectRatio;
            h = ratio > 0 ? w / ratio : 360.0;
        }
        const double left0 = 30.0, top0 = 30.0, gap = 40.0;   // anchor 8: explicit, non-overlapping

        bool populated = false;
        for (int i = 0; i < metrics.Count; i++)
        {
            var target = i == 0 ? leftPlot : dataDisplay.AddPlot(
                plotType, FreqUnit.GHz, left: left0 + w + gap, top: top0, width: w, height: h);

            if (i == 0)
            {
                target.Left = left0; target.Top = top0; target.Width = w; target.Height = h;
                if (target.Inspector.PlotType != plotType) target.Inspector.PlotType = plotType;
            }

            if (!target.Inspector.CanAddContourTrace) continue;
            target.Inspector.AddContourTraceCommand.Execute(null);
            if (target.Inspector.Traces.Count == 0) continue;

            var trow = target.Inspector.Traces[0];
            trow.ContourConstraintKind  = ConstraintKind.Compression;
            trow.ContourConstraintValue = 3.0;   // already ContourData's default (:60) — pinned
                                                  // explicitly so a later default change can't
                                                  // silently alter this auto-created display.
            trow.ContourMetricName      = metrics[i];   // triggers the single RebuildContour() fit

            populated = true;
        }

        return populated;
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
    /// R-h9a-3 — on macOS, a DOCKED harmonicaRF document takes over the app menu bar while it is the
    /// active dockable, and gives it back on blur. <paramref name="nowActive"/> is the harmonicaRF
    /// document that is the active dockable right now (null when something else is). No-ops on
    /// non-macOS and when nothing actually changed (both guards match every other per-window focus
    /// hook in this file — see <see cref="TryWireWindowFocusTracking"/>'s own doc comment).
    /// </summary>
    /// <remarks>
    /// The old holder is told first (<c>Invoke(false)</c>), then the new one (<c>Invoke(true)</c>) —
    /// mirroring <see cref="HarmonicaMenuView.RecomputeAttachment"/>'s own detach-before-attach rule
    /// (R-h9a-1): a stale holder must release the <c>NativeMenu</c> instance before anything else
    /// claims the hosting window, or the platform exporter sees the same instance on two objects.
    /// Restoring circuitRF's OWN menu on blur reads it back off <c>Application.Current</c> rather than
    /// caching a second reference — <c>WorkspaceWindow.OnOpened</c> already captured the SAME instance
    /// there via <c>AttachNativeMenuAtApplicationScope</c>, so this is guaranteed to be circuitRF's own
    /// menu, never a rebuild, per the brief's own "must be the SAME reference" requirement.
    /// </remarks>
    private void UpdateHarmonicaDockedMenuFocus(HarmonicaDocument? nowActive)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (ReferenceEquals(_harmonicaDockedFocusDoc, nowActive)) return;

        _harmonicaDockedFocusDoc?.ViewModel.SetNativeMenuDockedFocus(false);
        _harmonicaDockedFocusDoc = nowActive;

        if (nowActive is not null)
        {
            nowActive.ViewModel.SetNativeMenuDockedFocus(true);
        }
        else
        {
            RestoreCircuitRfMenuBar();
        }
    }

    /// <summary>
    /// Re-attaches circuitRF's own <c>NativeMenu</c> (captured once, at startup, into
    /// <c>Application.Current</c> by <c>WorkspaceWindow.AttachNativeMenuAtApplicationScope</c>) onto
    /// the shell window — the harmonicaRF-takeover release path, and also a safe no-op to call
    /// whenever nothing was ever taken over (both null-checks below simply fail quietly).
    /// </summary>
    private static void RestoreCircuitRfMenuBar()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.Windows.OfType<Views.WorkspaceWindow>().FirstOrDefault() is not { } shell) return;
        if (Avalonia.Controls.NativeMenu.GetMenu(Avalonia.Application.Current) is not { } appMenu) return;

        Avalonia.Controls.NativeMenu.SetMenu(shell, appMenu);
    }

    /// <summary>
    /// Called at every workspace-lifecycle reset point (new/switch/close) and when the docked-focus
    /// holder's own dockable closes — the document that held the takeover is about to disappear
    /// (torn down or force-closed) with no further <c>Activated</c>/dock-property-changed event to
    /// drive <see cref="UpdateHarmonicaDockedMenuFocus"/> through its normal release path, so this
    /// restores circuitRF's own menu bar directly rather than leaving it pointed at a menu whose
    /// owning window is gone.
    /// </summary>
    private void ResetHarmonicaDockedFocusTracking()
    {
        if (_harmonicaDockedFocusDoc is null) return;
        _harmonicaDockedFocusDoc = null;
        if (OperatingSystem.IsMacOS()) RestoreCircuitRfMenuBar();
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

        // From the pane that RAISED it, not from _factory.DocumentDock — with a side-by-side split
        // those are different docks, and reading the primary made a tab change in the second pane
        // re-route every panel to whatever the FIRST pane happened to be showing.
        var pane = sender as IDock ?? _factory.DocumentDock;
        if (pane is not null) _activeDocumentPane = pane;

        ActivateDocument(pane?.ActiveDockable);
    }

    // The document ActivateDocument last ran for. A tab change and Dock's focus signal can both
    // report the same document — the second is what makes a pane switch visible when the pane holds
    // one document and its ActiveDockable therefore never changes — and the routing below is not
    // cheap enough to run twice for one gesture.
    private IDockable? _lastActivatedDocument;

    /// <summary>
    /// Everything that follows "this document is now the one being worked on": the Properties, DRC
    /// and wBond panels, the Analyses panel, the harmonicaRF menu takeover, the UNDO TARGET, the save
    /// scope, and every enablement predicate gated on the active document type.
    ///
    /// <para>Carved out of <see cref="OnDocumentDockPropertyChanged"/> so a PANE change can run it
    /// too. A dock's ActiveDockable is not a complete signal: a pane holding one document — which is
    /// exactly what dragging a document to an edge produces — never changes it, so clicking between
    /// two side-by-side panes routed nothing at all, and Undo went on targeting the pane the user had
    /// left (owner, 2026-08-29).</para>
    /// </summary>
    /// <param name="requestActivationFocus">
    /// Whether the document's view should grab keyboard focus. True for a TAB change, which is what
    /// that behaviour is for — the newly-shown editor takes the keyboard so shortcuts work without a
    /// preliminary click. False when the user's own click is what raised this: they have already put
    /// focus somewhere deliberately, and pulling it to the canvas would take the caret out of a
    /// toolbar field on first click.
    /// </param>
    private void ActivateDocument(IDockable? activeDockable, bool requestActivationFocus = true)
    {
        _lastActivatedDocument = activeDockable;

        // The activated editor view should grab keyboard focus so shortcuts (Select All, nudges, …)
        // work without a preliminary click on the canvas. The view focuses its canvas on the event, or
        // — if it binds after this fires (first open) — by consuming the pending flag on DataContext change.
        if (requestActivationFocus) (activeDockable as IActivatableDocument)?.RequestActivationFocus();

        // L5b: the violations panel follows the active LAYOUT and nothing else. Cleared FIRST and set
        // again only by the two branches that HAVE a layout — a LayoutDocument's own, and a
        // WBondDocument's reference layout — so no document type can leave a previous layout's
        // violations on screen beside unrelated artwork by simply not knowing about this panel.
        _factory.DrcTool?.SetActiveLayout(null);

        // wbond.md §10.1 — the two wBond panels follow the same rule, for the same reason: a wire
        // profile shown beside a schematic is worse than an empty panel that says so.
        _factory.WBondProfileTool?.SetActiveWBond(null);
        _factory.WBondInductanceTool?.SetActiveWBond(null);

        // The wBond selection watch is live only while a wBond document IS the active one — otherwise
        // its own selection changes would go on flipping the Properties panel while the user is
        // looking at a schematic. Dropped FIRST, and re-armed below by the wBond branch alone, on the
        // same "no document type can leave another's context on screen" principle as the DRC panel.
        if (activeDockable is not WBondDocument) StopWatchingWBondProperties();

        // The same rule for a wirebond CELL's wire selection: watched only while its layout document is
        // the active one, or its own selection changes would go on flipping the Properties panel while
        // the user is looking at something else.
        if (activeDockable is not LayoutDocument) StopWatchingWirebondCellProperties();

        // And the same for its navigation FRAME: a push-in in a background tab must not re-route the
        // panels away from whatever the user is actually looking at.
        if (activeDockable is not LayoutDocument) StopWatchingLayoutFrameProperties();

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
        else if (activeDockable is WBondDocument wbDocForProps)
        {
            RouteDataDisplayProperties(null);
            ActivateWBondDocumentForProperties(wbDocForProps);
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

        // R-h9a-3: docked harmonicaRF menu-bar takeover follows the ACTIVE dockable, mirroring the
        // Analyses/Properties routing just above.
        UpdateHarmonicaDockedMenuFocus(activeDockable as HarmonicaDocument);

        // Undo routing — follows any document with an edit history for main-window tabs. Through
        // IEditHistoryDocument rather than IUndoableDocument so a Data Display is reachable too.
        SetActiveUndoTarget(activeDockable as IEditHistoryDocument);

        // Save-scope: "Save" when a document tab is active, "Save All" otherwise.
        ActiveSaveScope = activeDockable is IUndoableDocument
            ? SaveScope.SingleDoc
            : SaveScope.AllDocs;

        // Hierarchy commands depend on the active schematic document + its selection.
        RewireHierarchySubscriptions();

        // Generate Netlist is enabled only when a schematic document is active.
        GenerateNetlistCommand.NotifyCanExecuteChanged();

        // Run/Stop Analysis (toolbar + Simulate menu + Ctrl+R) are enabled only when a schematic
        // document is the active dockable.
        RunAnalysisCommand.NotifyCanExecuteChanged();
        StopAnalysisCommand.NotifyCanExecuteChanged();

        // Design ▸ Check Design Rules is enabled when a layout document is active, and when a wBond
        // document is — a wirebond design is checked through its reference layout.
        CheckDesignRulesCommand.NotifyCanExecuteChanged();
        // Same document-type gate, same fan-out — see the standing gotcha note further down: a
        // [RelayCommand(CanExecute=...)] gated on the active document is NOT re-evaluated on its own.
        ClearAllRulersCommand.NotifyCanExecuteChanged();

        // Export GDSII/DXF (item 8) are enabled only when a layout document is active.
        ExportGdsiiCommand.NotifyCanExecuteChanged();
        // Standing gotcha (see this file's own L5 note): a [RelayCommand(CanExecute=...)] gated on
        // the active document type is NOT re-evaluated on its own — it must be added to BOTH
        // fan-outs, or it silently stays stuck at whatever it was on construction.
        ImportIntoTechnologyCommand.NotifyCanExecuteChanged();
        ExportTechnologySectionsCommand.NotifyCanExecuteChanged();
        ExportDxfCommand.NotifyCanExecuteChanged();
        // Gerber was missing from BOTH fan-outs since L4c, so File > Export > Gerber has been greyed
        // out permanently — the exact failure the comment above warns about, found while adding Board.
        ExportGerberCommand.NotifyCanExecuteChanged();
        ExportBoardCommand.NotifyCanExecuteChanged();

        // Save Schematic As… / Save Layout As… are each enabled only when their own document type
        // is the active dockable.
        SaveLooseSchematicCommand.NotifyCanExecuteChanged();
        SaveLooseLayoutCommand.NotifyCanExecuteChanged();
        SaveLooseSymbolCommand.NotifyCanExecuteChanged();
        SaveAllDocumentsCommand.NotifyCanExecuteChanged();

        // File ▸ Close Window is enabled only when there IS an active document to close, so it rides
        // BOTH fan-outs (this one for the shell's own tab changes, RaiseFileMenuEnablementChanged for
        // a torn-off window taking focus) — the standing gotcha noted just above.
        CloseWindowCommand.NotifyCanExecuteChanged();

        // Design menu (L5): each is enabled only when its own document type is the active dockable —
        // same rule, same fan-out, as the Save-As commands just above.
        UpdateLayoutFromSchematicCommand.NotifyCanExecuteChanged();
        ImportWirebondWiresCommand.NotifyCanExecuteChanged();
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
                //
                // Harmonica/WBond excluded: each owns its own per-window native-menu attach
                // (HarmonicaMenuView.RecomputeAttachment et al.); attaching ours too races theirs and
                // crashes the native exporter ("The menu being updated does not match.") on tear-off.
                if (shellWindow is not null && doc is not HarmonicaDocument and not WBondDocument)
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

        // Undo/Redo ride this fan-out too, and that is the whole of the owner's 2026-09-01 report: a
        // FLOATING Data Display in focus, Cmd+Z, and a schematic that was not in focus undid an edit.
        // Undo was routed ONLY from the shell's own dock (OnDocumentDockPropertyChanged), so a
        // torn-off window taking focus never moved it — yet the macOS menu bar is app-global and the
        // same NativeMenu is attached to every torn-off window, so Edit ▸ Undo's Cmd+Z fires the
        // shell's command from a window the shell was not tracking. Resolved through exactly the
        // per-window rule (R-menu-4) every File-menu command already uses, so "the active document"
        // has ONE answer.
        //
        // Retargets only when the resolution NAMES a document, never to null. This fan-out has nine
        // call sites, several of them a dirty-state notification that fires on every keystroke in a
        // .ctech or .cem form, and blanking the undo target from any of them would grey Undo out
        // mid-edit. Clearing it stays where it already was — ActivateDocument(null) and the three
        // workspace-reset points.
        if (doc is IEditHistoryDocument focusedHistory) SetActiveUndoTarget(focusedHistory);

        ExportGdsiiCommand.NotifyCanExecuteChanged();
        // Standing gotcha (see this file's own L5 note): a [RelayCommand(CanExecute=...)] gated on
        // the active document type is NOT re-evaluated on its own — it must be added to BOTH
        // fan-outs, or it silently stays stuck at whatever it was on construction.
        ImportIntoTechnologyCommand.NotifyCanExecuteChanged();
        ExportTechnologySectionsCommand.NotifyCanExecuteChanged();
        ExportDxfCommand.NotifyCanExecuteChanged();
        // Gerber was missing from BOTH fan-outs since L4c, so File > Export > Gerber has been greyed
        // out permanently — the exact failure the comment above warns about, found while adding Board.
        ExportGerberCommand.NotifyCanExecuteChanged();
        ExportBoardCommand.NotifyCanExecuteChanged();
        SaveLooseSchematicCommand.NotifyCanExecuteChanged();
        SaveLooseLayoutCommand.NotifyCanExecuteChanged();
        SaveLooseSymbolCommand.NotifyCanExecuteChanged();
        SaveAllDocumentsCommand.NotifyCanExecuteChanged();
        CloseWindowCommand.NotifyCanExecuteChanged();
        UpdateLayoutFromSchematicCommand.NotifyCanExecuteChanged();
        ImportWirebondWiresCommand.NotifyCanExecuteChanged();
        UpdateSchematicFromLayoutCommand.NotifyCanExecuteChanged();
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

    // Finds the first document with an edit history reachable from a window's DataContext.
    // Dock's HostWindow sets DataContext to the IDockWindow (an IDock) that contains
    // the layout with the floated dockable.
    private static IEditHistoryDocument? FindUndoDocInWindow(Window window)
    {
        if (window.DataContext is IEditHistoryDocument direct) return direct;
        if (window.DataContext is IDock dock) return FindUndoDocInDock(dock);
        return null;
    }

    private static IEditHistoryDocument? FindUndoDocInDock(IDock dock)
    {
        if (dock is IEditHistoryDocument ud) return ud;
        if (dock.ActiveDockable is IEditHistoryDocument active) return active;
        if (dock.ActiveDockable is IDock nestedActive)
        {
            var result = FindUndoDocInDock(nestedActive);
            if (result is not null) return result;
        }
        if (dock.VisibleDockables is null) return null;
        foreach (var dockable in dock.VisibleDockables)
        {
            if (dockable is IEditHistoryDocument ud2) return ud2;
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
    private void WireWindowUndo(Window window, IEditHistoryDocument undoDoc)
    {
        _wiredHostWindows.Add(window);

        // Through the DOCUMENT's UndoLast/CanUndoLast, exactly as the shell's own Undo command is: a
        // torn-off layout window showing a wirebond cell has the same two histories, and Ctrl+Z there
        // has to reach the same one.
        var undoCmd = new RelayCommand(undoDoc.UndoLast, () => undoDoc.CanUndoLast);
        var redoCmd = new RelayCommand(undoDoc.RedoLast, () => undoDoc.CanRedoLast);

        void OnStackChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) undoCmd.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) redoCmd.NotifyCanExecuteChanged();
        }
        // Only a stack-backed document HAS a stack to watch. A Data Display keeps its history in the
        // ported UndoRedoManager instead (see IEditHistoryDocument), and says it moved by raising its
        // own commands' CanExecuteChanged — so it needs its own subscription, exactly as the wire
        // history below does, or a torn-off display's Ctrl+Z stays disabled after the first edit.
        var stack       = (undoDoc as IUndoableDocument)?.UndoRedo;
        var displayUndo = (undoDoc as DataDisplay.DataDisplayDocument)?.ViewModel.Window.UndoCommand;

        void OnDisplayHistoryChanged(object? _, EventArgs __)
        { undoCmd.NotifyCanExecuteChanged(); redoCmd.NotifyCanExecuteChanged(); }

        if (stack is not null)       stack.PropertyChanged     += OnStackChanged;
        if (displayUndo is not null) displayUndo.CanExecuteChanged += OnDisplayHistoryChanged;

        // …and the WIRE history raises no UndoRedoStack notification of its own, so it needs its own
        // subscription or the binding stays disabled after a wire edit (WB40).
        LayoutEditorViewModel? wireVm = undoDoc is LayoutDocument { ActiveViewModel: { WireDesign: not null } vm } ? vm : null;
        void OnWiresChanged() { undoCmd.NotifyCanExecuteChanged(); redoCmd.NotifyCanExecuteChanged(); }
        if (wireVm is not null) wireVm.WireHistoryChanged += OnWiresChanged;

        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control),                       Command = undoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Meta),                          Command = undoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift),  Command = redoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Meta    | KeyModifiers.Shift),  Command = redoCmd });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Y, KeyModifiers.Control),                       Command = redoCmd });

        // Ctrl+S / Cmd+S saves THIS torn-off window's own document — the same command the shell's
        // own Ctrl+S runs, with this window handed to it explicitly so its R-menu-4 per-window
        // resolution names the document the user is actually looking at. Owner report, 2026-08-29:
        // a floating layout document did not save on ⌘S; it toggled geometry snap instead, because
        // Ctrl/Meta+S was bound ONLY on WorkspaceWindow (and, for the two document views that had
        // already hit this, on the view itself). Avalonia processes KeyBindings from the focused
        // element up to the root BEFORE raising the routed KeyDown, so in a CrfHostWindow the
        // keystroke met no binding and reached LayoutEditorViewModel.OnKeyDown, whose 's' toggle
        // then claimed it.
        //
        // Bound HERE rather than on each view, because this is not one view's problem: EVERY torn-off
        // document was missing the gesture, and the two views that bound it themselves
        // (DataDisplayView, EmSetupEditorView) still win — a binding nearer the focused element is
        // processed first and stops the walk, so their document-specific Save is unaffected.
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = SaveAllDocumentsCommand, CommandParameter = window });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Meta),    Command = SaveAllDocumentsCommand, CommandParameter = window });

        // Ctrl+W / Cmd+W closes THIS torn-off window's own document. A MenuItem's InputGesture is
        // display-only in Avalonia, so the TornOffFileMenuView copy of the File menu shows the
        // accelerator but cannot execute it — a KeyBinding on the window is what makes the key work
        // on Windows/Linux (macOS's app-global NativeMenu already carries the real Cmd+W). The command
        // resolves the target through the SAME per-window focus tracking the menu item uses, so it
        // acts on this window's document and never on the shell's.
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Control),                       Command = CloseWindowCommand });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Meta),                          Command = CloseWindowCommand });

        window.Closed += (_, _) =>
        {
            if (stack is not null)       stack.PropertyChanged        -= OnStackChanged;
            if (displayUndo is not null) displayUndo.CanExecuteChanged -= OnDisplayHistoryChanged;
            if (wireVm is not null) wireVm.WireHistoryChanged -= OnWiresChanged;
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

        // An EM setup, like a technology, is always materialized — a dirty one must be offered the
        // same Save / Don't Save / Cancel before its tab closes.
        if (dockable is EmSetupDocument emCloseDoc && emCloseDoc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{emCloseDoc.Id}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            switch (dlg.Result)
            {
                case SaveChangesResult.Cancel:   return false;
                case SaveChangesResult.DontSave: return true;
                case SaveChangesResult.Save:
                    emCloseDoc.ViewModel.SaveCommand.Execute(null);
                    return !emCloseDoc.IsDirty;
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

        // wBond editor document (owner, 2026-08-16: a dirty one closed silently). Same three answers
        // as every other document type, and the same rule for a cancelled picker: SaveWBondDoc leaves
        // the document dirty when the user backs out, and that must cancel the close too — otherwise
        // "Save" would quietly behave as "Don't Save".
        if (dockable is WBond.WBondDocument wbCloseDoc && wbCloseDoc.IsDirty)
        {
            var dlg = new Views.Dialogs.SaveChangesDialog(
                $"Save '{wbCloseDoc.Title?.TrimEnd(' ', '•')}' before closing?",
                title: "Unsaved Changes");
            await dlg.ShowDialog(window);

            switch (dlg.Result)
            {
                case SaveChangesResult.Cancel:   return false;
                case SaveChangesResult.DontSave: return true;
                case SaveChangesResult.Save:
                    await SaveWBondDoc(wbCloseDoc, window);
                    return !wbCloseDoc.IsDirty;
                default:                         return false;
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
        // A closed .cdd / .ctech / .cem can no longer hold unsaved work — IsNodeDirty answers each of
        // those from the OPEN documents alone, so once the document is gone the honest answer is
        // "clean" and the mark it pushed has to come off. Without this a "Don't Save" close leaves the
        // mark standing until an unrelated window Activated rebuilds the tree. Deliberately NOT done
        // for schematic/symbol/layout: a dirty session for those outlives its document in the session
        // registry (that is what the orphaned-dirty-session prompt is about), so their mark is still
        // true after the tab closes.
        string? closedFilePath = dockable switch
        {
            DataDisplayDocument d => d.FilePath,
            TechDocument d        => d.FilePath,
            EmSetupDocument d     => d.FilePath,
            _                     => null,
        };
        if (closedFilePath is not null)
            _factory.ProjectTreeTool?.SetFileDirty(closedFilePath, false);
        if (dockable is LayoutDocument scratchLayout)
            _scratchLayouts.Remove(scratchLayout);
        if (dockable is WBondDocument scratchWBond)
            _scratchWBonds.Remove(scratchWBond);

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
            // The panel may still hold focus, and Run/Stop now read the retained doc through it —
            // so the pair has to be re-asked here, or ⌘R stays live with nothing to run.
            RunAnalysisCommand.NotifyCanExecuteChanged();
            StopAnalysisCommand.NotifyCanExecuteChanged();
        }

        // R-h9a-3: a harmonicaRF document closing while it holds the docked menu-bar takeover must
        // give circuitRF's own menu back — nothing else will, since the tab (and its dock-property-
        // changed event) is gone.
        if (ReferenceEquals(dockable, _harmonicaDockedFocusDoc))
            ResetHarmonicaDockedFocusTracking();
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
        EmSetupDocument emd   => emd.IsDirty,
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

            // Active wBond — same rule as the data display above: the focused document's own Save.
            if (ResolveActiveDocumentForCommands() is WBondDocument activeWBond)
            {
                if (!activeWBond.IsDirty && activeWBond.FilePath is not null)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveWBondDoc(activeWBond, window);
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

            // SingleDoc scope for an active EM setup — R-em-9: never scratch, so a direct write,
            // exactly like the technology editor above.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                ResolveActiveDocumentForCommands() is EmSetupDocument singleEmDoc)
            {
                if (!singleEmDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                singleEmDoc.ViewModel.SaveCommand.Execute(null);
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
            var dirtyEmDocs = _openDocsByPath.Values
                .OfType<EmSetupDocument>()
                .Where(d => d.IsDirty)
                .ToList();

            bool anyDirty = dirtyScratch.Count > 0 || dirtyMaterialized.Count > 0
                         || dirtyScratchSymbols.Count > 0 || dirtyMaterializedSymbols.Count > 0
                         || dirtyScratchLayouts.Count > 0 || dirtyMaterializedLayouts.Count > 0
                         || dirtyTechDocs.Count > 0 || dirtyEmDocs.Count > 0;
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

            // Dirty EM setups — R-em-9: never scratch, so the same direct write.
            foreach (var emDoc in dirtyEmDocs)
                emDoc.ViewModel.SaveCommand.Execute(null);
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
            || _openDocsByPath.Values.OfType<EmSetupDocument>().Any(d => d.IsDirty && Keep(d))
            || _scratchDataDisplays.Any(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d))
            || _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d => d.ViewModel.Window.HasUnsavedChanges() && Keep(d))
            || _scratchWBonds.Any(d => d.IsDirty && Keep(d))
            || _openDocsByPath.Values.OfType<WBondDocument>().Any(d => d.IsDirty && Keep(d))
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
        var dirtyEmDocs = _openDocsByPath.Values
            .OfType<EmSetupDocument>()
            .Where(d => d.IsDirty && Keep(d))
            .ToList();
        var dirtyOrphanedSessions       = _registry.GetOrphanedDirtyPaths(IsSessionReferenced);
        var dirtyOrphanedLayoutSessions = _layoutRegistry.GetOrphanedDirtyPaths(IsLayoutSessionReferenced);

        int total = dirtyScratch.Count + dirtyMat.Count
                  + dirtyScratchSymbols.Count + dirtyMatSymbols.Count
                  + dirtyScratchDisplays.Count + dirtyMatDisplays.Count
                  + dirtyScratchLayouts.Count + dirtyMatLayouts.Count
                  + dirtyTechDocs.Count + dirtyEmDocs.Count
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
            : dirtyEmDocs.Count                > 0 ? dirtyEmDocs[0].Id
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
                foreach (var emDoc in dirtyEmDocs)
                    emDoc.ViewModel.SaveCommand.Execute(null);
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

        // Ends the workspace's generator interpreters deliberately, while there is still a chance to
        // ask them to shut down. The ProcessExit backstop in App only clears references — it cannot
        // wait for a process to close its own files.
        ResetPCellGenerators(null);

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
                vm.CellResolverProvider  = () => this;
                vm.DocumentName          = name;
                var doc = new SchematicDocument(name, vm) { Messages = Messages, Hierarchy = this };
                HookSchematicCanvasFocus(doc);
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

        // The ACTIVE document (R-menu-4: per-window, not the shell's own ActiveDockable) always wins —
        // "Save Schematic As…" means the one in front of the user, scratch or already-materialized.
        // The dirty-scratch fallback is only for the case where nothing schematic-shaped is active at
        // all; it used to run whenever the active document merely wasn't scratch, which silently
        // re-targeted a Save As on a materialized schematic at some other, unrelated scratch tab.
        var doc = ResolveActiveDocumentForCommands() as SchematicDocument
                  ?? _scratchDocs.FirstOrDefault(d => d.IsDirty);

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

    /// <summary>
    /// Suggested name for a schematic save picker. <c>doc.Id</c> is the tab identity and carries the
    /// file name WITH its ".csch" extension for any document opened from disk; the picker appends
    /// <c>DefaultExtension</c> itself, so handing it the full name is what put ".csch" in the
    /// suggested name TWICE ("SParamTest.csch.csch"). Scratch documents, whose Id is a plain title,
    /// are unaffected either way.
    /// </summary>
    internal static string SchematicPickerName(SchematicDocument doc) =>
        doc.Id.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
            ? doc.Id[..^".csch".Length]
            : doc.Id;

    /// <summary>Appends ".csch" when the picked path carries no extension at all (a picker that does
    /// not apply DefaultExtension must not produce an extension-less schematic file).</summary>
    internal static string EnsureCschExtension(string path) =>
        Path.HasExtension(path) ? path : path + ".csch";

    // Tier 2: save to a user-picked location + register as Known File in the open workspace.
    private async Task SaveLooseToWorkspace(SchematicDocument doc, Window owner)
    {
        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Schematic",
            SuggestedFileName = SchematicPickerName(doc),
            DefaultExtension  = "csch",
            FileTypeChoices   =
            [
                new FilePickerFileType("circuitRF Schematic") { Patterns = ["*.csch"] },
            ],
        });
        if (result is null) return;

        var filePath = EnsureCschExtension(result.Path.LocalPath);
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
                // Scratch → materialized transition. The file stem is passed as the new base title:
                // unlike LayoutDocument/SymbolEditorDocument, a SchematicDocument has no
                // path-to-title subscription, so Materialize with no name would leave the tab reading
                // "Untitled-Schematic-N" after the save.
                _scratchDocs.Remove(doc);
                _recovery.ClearDoc(doc);
                doc.Materialize(filePath, Path.GetFileNameWithoutExtension(filePath));
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
            SuggestedFileName = SchematicPickerName(doc),
            DefaultExtension  = "csch",
            FileTypeChoices   =
            [
                new FilePickerFileType("circuitRF Schematic") { Patterns = ["*.csch"] },
            ],
        });
        if (result is null) return;

        var filePath = EnsureCschExtension(result.Path.LocalPath);
        try
        {
            SchematicPersistence.SaveToFile(filePath, doc.ViewModel.EditModel, doc.Id);

            if (doc.IsScratch)
            {
                // Materialize (plain — no workspace registration, no Known-File entry). File stem as
                // the base title, for the same reason as the tier-2 branch above.
                _scratchDocs.Remove(doc);
                _recovery.ClearDoc(doc);
                doc.Materialize(filePath, Path.GetFileNameWithoutExtension(filePath));
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
        // The file did not exist when the tree was last scanned — a rescan is what puts a node there
        // at all, and the node it builds asks IsNodeDirty for itself, so it arrives clean.
        _factory.ProjectTreeTool?.Refresh();
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

    // ---- Crash reports -------------------------------------------------------

    /// <summary>
    /// Says so, once per launch, if the previous session did not exit cleanly — and links the report
    /// so it is one click to attach to a bug report. Posted to Messages rather than raised as a
    /// dialog: the user has just launched the application to get on with something, and a modal
    /// about a crash they already lived through would be in the way of that. The one thing they
    /// cannot do without help is FIND the file, which is exactly what the clickable path gives them.
    /// </summary>
    public void AnnouncePendingCrashReports()
    {
        var reports = Diagnostics.CrashReporter.TakePendingReports();
        foreach (string report in reports)
            Messages.Warning(
                "circuitRF did not shut down cleanly last time. A crash report was saved — " +
                "please send it with your bug report.", report);
    }

    /// <summary>Opens the folder crash reports are written to.</summary>
    [RelayCommand]
    private void OpenCrashReports()
    {
        try
        {
            string dir = Diagnostics.CrashReporter.Dir;
            Directory.CreateDirectory(dir);         // it need not exist yet — a user who has never crashed

            var reports = Diagnostics.CrashReporter.AllReports();
            if (reports.Count == 0)
            {
                // Still open the folder. "There are none" is the answer, and showing them the empty
                // folder is how they know WHERE none is, for the next time there is one.
                Messages.Info("No crash reports have been recorded.", dir);
                OpenPathExternal(dir);
                return;
            }

            Messages.Info($"{reports.Count} crash report(s).", dir);
            RevealPathInFileManager(reports[0]);
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not open the crash report folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Help ▸ Check for Updates… — the same check the background scheduler runs, ignoring the
    /// 24-hour throttle, and reporting through the <b>Message Panel rather than a dialog</b>.
    ///
    /// <para>This is the one place a network failure is allowed to be visible, because here the user
    /// explicitly asked. Everywhere else an unreachable feed is silent: an offline machine is the
    /// normal state for a large fraction of this application's users, and a recurring "couldn't
    /// check for updates" line would be a defect rather than a feature.</para>
    ///
    /// <para>The item is <b>disabled</b> when automatic updates are off — a manual check is still a
    /// network call, and "never checks for updates" has to mean what it says — and when the install
    /// site is read-only, where the notify-only path serves instead. <see cref="CanCheckForUpdates"/>
    /// is what the menu binds its enablement to.</para>
    ///
    /// <para><b>No "Relaunch" button appears here or anywhere else.</b> The application can be
    /// holding unsaved workspaces; a one-click relaunch invites data loss to save a keystroke.</para>
    /// </summary>
    /// <summary>
    /// <b>Not gated on <c>CanSelfUpdate</c>.</b> A notify-only install still checks and still posts a
    /// line with a link (R-AU-1), so disabling the item there left those users with no way at all to
    /// learn a new version existed — and made this method's own NotifyOnly branch unreachable on
    /// exactly the installs it was written for (found in a second review, 2026-08-25). Automatic
    /// updates being off is the one thing that does disable it, because a manual check is still a
    /// network call and "never checks" has to mean what it says.
    /// </summary>
    public bool CanCheckForUpdates => Updates.UpdatePolicy.Current.AutomaticUpdates;

    /// <summary>
    /// Re-asks <see cref="CanCheckForUpdates"/>. <c>CanExecute</c> is only re-evaluated when the
    /// command says so, so without this the Help item's enablement was whatever it was when this
    /// view-model was constructed and a Settings change never reached it.
    /// </summary>
    public void RefreshUpdateCommandState() => CheckForUpdatesCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        Messages.Info($"Checking for {Updates.UpdateApp.Name} updates...");

        Updates.CheckResult result;
        try   { result = await Updates.UpdateScheduler.CheckNowAsync(Messages); }
        catch (Exception ex)
        {
            Messages.Warning($"Could not reach the update server: {ex.Message}");
            return;
        }

        switch (result.Outcome)
        {
            case Updates.CheckOutcome.UpToDate:
                Messages.Info($"{Updates.UpdateApp.Name} {AppVersion.Display} is up to date.");
                break;

            case Updates.CheckOutcome.Staged:
                // A check that STAGED just now already posted the "updated ... in the background"
                // line, and saying it twice would be worse than saying it once. A check that found
                // the version already downloaded posted nothing — the announcement fired on an
                // earlier check — so the user who just asked would otherwise get silence.
                if (result.Detail == Updates.UpdateService.AlreadyStagedDetail)
                    Messages.Info(
                        $"{Updates.UpdateApp.Name} {result.Version} has already been downloaded and "
                        + $"will be used the next time {Updates.UpdateApp.Name} is relaunched.");
                break;

            case Updates.CheckOutcome.InsufficientSpace:
                // Reported by CheckAsync with the figures, because the user asked.
                break;

            case Updates.CheckOutcome.NotifyOnly:
                // CheckAsync posts the "<version> is available" line itself, once per version, and it
                // carries the REASON — which is not always the install location: a writable per-user
                // install whose binary is unsigned is notify-only too, and telling that user their
                // installation is in the wrong place would send them to re-install something that is
                // exactly where it should be.
                break;

            case Updates.CheckOutcome.Disabled:
                Messages.Info(result.Detail.Length > 0
                    ? result.Detail
                    : "Automatic updates are turned off in Settings, so no check was made.");
                break;

            case Updates.CheckOutcome.Cancelled:
                // The progress row said what happened, on the row the user cancelled. Reaching the
                // default here would report their own Cancel back to them as an unreachable server.
                break;

            default:
                Messages.Warning("Could not reach the update server. Nothing was changed.");
                break;
        }
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
