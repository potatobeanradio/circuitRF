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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Messages;
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
public partial class WorkspaceViewModel : ViewModelBase, ITreeActions, IHierarchyHost, ICellResolver
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

    // Scratch documents have no path so they cannot go in _openDocsByPath.
    // Tracked here for enumeration by save/rebuild operations (steps 2+).
    // NOTE (step 1): entries are not removed when a scratch tab is closed — that
    // cleanup and the close-prompt are added in step 2/3.
    private readonly List<SchematicDocument>    _scratchDocs         = [];
    private readonly List<SymbolEditorDocument> _scratchSymbols      = [];
    private readonly List<DataDisplayDocument>  _scratchDataDisplays = [];

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

    // Windows that already have undo/redo KeyBindings injected (Dock float support).
    private readonly HashSet<Window> _wiredHostWindows = [];

    public string UndoDescription => _activeUndoTarget?.UndoRedo.UndoDescription ?? "Undo";
    public string RedoDescription => _activeUndoTarget?.UndoRedo.RedoDescription ?? "Redo";

    // ---- Window title --------------------------------------------------------

    [ObservableProperty] private string _windowTitle = "circuitRF";

    // ---- Last-run DataSets (held for Phase 7) --------------------------------
    // Populated by RunAnalysis after a successful engine run; visualised in Phase 7.
    private IReadOnlyList<DataSet> _lastRunDataSets = [];
    [ObservableProperty] private string? _currentWorkspacePath;

    // Last-used parent directory for the New Workspace dialog (in-memory, not persisted).
    // Seeds the Location field so repeated New Workspace dialogs start at the same folder.
    private string _lastWorkspaceParentDir =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ---- Recent Workspaces (persisted in AppPreferences) --------------------

    private readonly List<string> _recentWorkspaces;

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

        if (_factory.ProjectTreeTool is { } tree)
        {
            if (dir is not null)
                tree.SetWorkspace(dir);
            else
                tree.ClearWorkspace();
        }

        _factory.AnalysesTool?.SetWorkspaceDir(dir);
    }

    // ---- Constructor ---------------------------------------------------------

    public WorkspaceViewModel()
    {
        _factory = new CircuitRfDockFactory();

        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;
        _factory.PaletteTool?.SetPlacementService(PlacementService);

        // Wire tree-item actions before any workspace is loaded so actions are available
        // the moment SetWorkspace builds the first VM tree.
        _factory.ProjectTreeTool?.SetActions(this);
        SubscribeToFilterState();
        SubscribeToTreeSelection();

        // Notify PropertiesTool when the active document tab changes (active schematic tracking).
        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnDocumentDockPropertyChanged;

        // Load persisted preferences and seed the recent lists.
        var prefs         = AppPreferencesIo.Load();
        _recentWorkspaces = new List<string>(prefs.RecentWorkspaces ?? []);
        _recentlyPlaced   = ParseMruPlaced(prefs.RecentlyPlaced);
        _factory.PaletteTool?.SetMru(_recentlyPlaced);
        RebuildRecentMenuItems();

        // Wire close-tab prompt: before a dockable is removed, show Save/Don't Save/Cancel
        // for dirty/scratch documents. FactoryBase.DockableClosed fires from base.CloseDockable
        // and cleans up _scratchDocs/_openDocsByPath.
        _factory.CloseDockableConfirm = ConfirmCloseDockable;
        _factory.DockableClosed += (_, args) => { if (args.Dockable is not null) OnDockableClosed(args.Dockable); };

        // Autosave: periodic dirty-scratch serialization to the per-session recovery dir.
        _recovery = new RecoveryManager();
        StartAutosaveTimer();

        // Defer recovery-offer until the window is fully shown (Background priority).
        Avalonia.Threading.Dispatcher.UIThread.Post(
            CheckForRecovery, Avalonia.Threading.DispatcherPriority.Background);

        Messages.Info("circuitRF ready.");
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

        if (HasAnyDirtyWork() && !await PromptSaveBeforeClose(window, "creating a new workspace"))
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
            WorkspacePersistence.SaveToFileAtomic(cwsPath, new CwsFile());

            // Update tracked location to the chosen parent (seeds the next New Workspace dialog).
            _lastWorkspaceParentDir = result.ParentDir;

            SetActiveUndoTarget(null);
            _openDocsByPath.Clear();
            _scratchDocs.Clear();
            _scratchSymbols.Clear();
            _scratchDataDisplays.Clear();
            _registry.Clear();
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

            PushRecent(cwsPath);
            Messages.Clear();
            Messages.Success($"New workspace '{result.Name}' created.");
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

        if (HasAnyDirtyWork() && !await PromptSaveBeforeClose(window, "opening a workspace"))
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
            // DockLayout: Dock.Serializer not referenced in v1; field stays null until Dock.Serializer is added.

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

            // Persist documents currently open in the main DocumentDock.
            // Scratch docs (no path) and the welcome stub are never persisted.
            var dockables = _factory.DocumentDock?.VisibleDockables;
            if (dockables is not null)
            {
                var wsDir    = Path.GetDirectoryName(path)!;
                var docsList = new List<CwsOpenDocument>();
                int order    = 0;
                foreach (var dockable in dockables)
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

                    if (docPath is null || kind is null) continue;

                    string stored;
                    try   { stored = Path.GetRelativePath(wsDir, docPath); }
                    catch { stored = docPath; }
                    docsList.Add(new CwsOpenDocument { Path = stored, Kind = kind, TabOrder = order++ });
                }
                ws.OpenDocuments = docsList.Count > 0 ? docsList : null;

                // Persist active document path so the same tab is focused on restore.
                var active = _factory.DocumentDock?.ActiveDockable;
                string? activePath = null;
                if (active is SchematicDocument asd && asd.FilePath is not null)
                {
                    try   { activePath = Path.GetRelativePath(wsDir, asd.FilePath); }
                    catch { activePath = asd.FilePath; }
                }
                else if (active is SymbolEditorDocument asyed &&
                         asyed.ViewModel.CurrentSymbolPath is not null)
                {
                    try   { activePath = Path.GetRelativePath(wsDir, asyed.ViewModel.CurrentSymbolPath); }
                    catch { activePath = asyed.ViewModel.CurrentSymbolPath; }
                }
                else if (active is CellParameterEditorDocument acpd)
                {
                    var cellPath = Path.GetDirectoryName(acpd.ViewModel.EditModel.CcellPath);
                    if (cellPath is not null)
                    {
                        try   { activePath = Path.GetRelativePath(wsDir, cellPath); }
                        catch { activePath = cellPath; }
                    }
                }
                else if (active is DataDisplayDocument add && add.FilePath is not null)
                {
                    try   { activePath = Path.GetRelativePath(wsDir, add.FilePath); }
                    catch { activePath = add.FilePath; }
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
        _openDocsByPath.Clear();
        _scratchDocs.Clear();
        _scratchSymbols.Clear();
        _scratchDataDisplays.Clear();
        _registry.Clear();
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

        var cws = TryLoadCws(cwsPath);
        if (cws.ColorSchemeName is { } schemeName)
        {
            try { ThemeService.Active = ThemeResolver.Resolve(schemeName, workspaceDir); }
            catch { }
        }
        ApplyTreeViewState(cws.TreeViewState);
        RestoreOpenDocuments(cws, workspaceDir);

        PushRecent(cwsPath);
        Messages.Clear();
        Messages.Info("Opened", cwsPath);
    }

    /// <summary>
    /// Re-opens the documents listed in <paramref name="cws"/> into the current DocumentDock.
    /// Removes the welcome stub first (so the restored tabs are the only content).
    /// No-op when <see cref="CwsFile.OpenDocuments"/> is null or empty.
    /// </summary>
    private void RestoreOpenDocuments(CwsFile cws, string workspaceDir)
    {
        if (cws.OpenDocuments is not { Count: > 0 } docs) return;

        _factory.RemoveWelcomeStub();

        foreach (var entry in docs.OrderBy(d => d.TabOrder))
        {
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
            }
        }

        // Restore the previously active tab.
        if (cws.ActiveDocumentPath is { } activePath)
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

    [RelayCommand]
    private async Task AddLibrary(Window? owner)
    {
        // Stub for 6b — library management wired in 6c.
        Messages.Info("Add Library: not yet implemented (6c).");
        await Task.CompletedTask;
    }

    // ---- Recent Workspaces commands -----------------------------------------

    /// <summary>Open a workspace from the Recent list by its .cws path.</summary>
    [RelayCommand]
    private async Task OpenRecentWorkspace(string? cwsPath)
    {
        if (cwsPath is null) return;

        if (HasAnyDirtyWork())
        {
            var window = ResolveOwner(null);
            if (window is not null && !await PromptSaveBeforeClose(window, "opening a workspace"))
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
    [RelayCommand] private void SelectAll() { }

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
        Messages.Info("Layout reset to default.");
    }

    [RelayCommand] private void ZoomToFit()        { Messages.Info("Zoom to Fit: not yet implemented (6c)."); }
    [RelayCommand] private void HideShowDockers()  { Messages.Info("Hide/Show Dockers: use Dock title-bar controls to float/minimize regions."); }
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
        var activeDoc = _factory.DocumentDock?.ActiveDockable as SchematicDocument;
        if (activeDoc is null)
        {
            Messages.Warning("Run: no schematic is active.");
            return;
        }

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
        RunResult result;
        try
        {
            result = await Task.Run(() => SchematicRunService.RunNetlist(netlistPath));
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
                var written = RunResultsWriter.WriteResults(
                    baseDir,
                    RunResultsWriter.SchematicKey(activeDoc.FilePath, activeDoc.Id),
                    RunResultsWriter.OwnerIdentity(activeDoc.FilePath, activeDoc.Id),
                    result.Results,
                    Messages);
                await RefreshOpenDataDisplaysAsync(written);
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
    /// </summary>
    private void NewScratchSymbol()
    {
        var title    = NextScratchSymbolTitle();
        var editable = new EditableSymbol { UserEditable = true };
        var vm       = new SymbolEditorViewModel(editable);
        vm.SymbolSaved += OnSymbolSaved;
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

    // ---- Data Display commands ----------------------------------------------

    /// <summary>
    /// Creates a scratch Data Display tab immediately.
    /// Always enabled; no workspace required (scratch-first, same as New Schematic/Symbol).
    /// </summary>
    [RelayCommand]
    private void NewDataDisplay()
    {
        var title = NextDataDisplayTitle();
        var vm    = new DataDisplayDocumentViewModel();
        var doc   = new DataDisplayDocument(title, vm);
        _scratchDataDisplays.Add(doc);
        vm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        _factory.OpenDocument(doc);
        WireDataDisplayLibraryEvents(vm);
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
        _factory.OpenDocument(newDoc);
        WireDataDisplayLibraryEvents(newVm);

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

    // ---- Data Display library event wiring (Phase 7.2e) --------------------

    private void WireDataDisplayLibraryEvents(DataDisplayDocumentViewModel docVm)
    {
        docVm.Window.DataSourceLibrary.UnusualZ0Detected += OnUnusualZ0Detected;
    }

    private void OnUnusualZ0Detected(string path, Z0Kind kind, IReadOnlyList<Complex> z0)
    {
        var kindLabel = kind == Z0Kind.NonUniform ? "non-uniform" : "complex";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < z0.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var z1   = z0[i];
            string zFmt = Math.Abs(z1.Imaginary) < 1e-12
                ? $"{z1.Real:G4}Ω"
                : $"{z1.Real:G4}{(z1.Imaginary >= 0 ? "+" : "")}{z1.Imaginary:G4}jΩ";
            sb.Append($"port{i + 1}={zFmt}");
        }
        var fileName = Path.GetFileName(path);
        Messages.Warning($"Warning: \"{fileName}\" uses a {kindLabel} reference impedance — S-parameter results depend on it. Per-port Z0: {sb}");
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
        if (owner is null) return;
        var result = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        var path = result[0].Path.LocalPath;

        try
        {
            var symbol   = SymbolPersistence.LoadFromFile(path);
            var editable = EditableSymbol.FromSymbol(symbol);
            editable.UserEditable = true;  // user file — editable
            var vm  = new SymbolEditorViewModel(editable) { CurrentSymbolPath = path };
            var doc = new SymbolEditorDocument(Path.GetFileName(path), vm, path);
            _factory.OpenDocument(doc);
            Messages.Info("Opened", path);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open symbol: {ex.Message}");
        }
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

    private void OpenCellPrimary(ProjectTreeNodeViewModel cellNode, ViewType viewType)
    {
        var cellDir = cellNode.AbsolutePath;
        var pr      = CellFolder.ResolvePrimary(cellDir, viewType);
        if (pr.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || pr.ResolvedName is null)
        {
            var what = viewType == ViewType.Schematic ? "schematic" : "symbol";
            Messages.Info($"Cell '{Path.GetFileName(cellDir)}' has no primary {what}.");
            return;
        }
        var path = Path.Combine(CellFolder.SubFolderPath(cellDir, viewType), pr.ResolvedName);
        if (viewType == ViewType.Schematic) OpenOrActivateSchematic(path);
        else                                OpenOrActivateSymbol(path);
    }

    public void OpenNode(ProjectTreeNodeViewModel node)
    {
        switch (node.Kind)
        {
            case NodeKind.ViewFile:
                var ext = Path.GetExtension(node.AbsolutePath).ToLowerInvariant();
                if (ext == ".csym")  { OpenOrActivateSymbol(node.AbsolutePath);    return; }
                if (ext == ".csch")  { OpenOrActivateSchematic(node.AbsolutePath); return; }
                // .clay (layout) and other view-file types → deferred no-op
                return;

            case NodeKind.Cell:
                OpenOrActivateCellPlaceholder(node.AbsolutePath, node.Name);
                return;

            case NodeKind.DataDisplayFile:
                OpenOrActivateDataDisplay(node.AbsolutePath);
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
        return RegisterSession(key, BuildSessionVm(editModel));
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

    [RelayCommand(CanExecute = nameof(CanHierarchyPushIn))]
    private void HierarchyPushIn()
    {
        var doc  = ActiveSchematicDocument;
        var comp = GetSingleSelectedCellComp(doc?.ActiveViewModel);
        if (doc is null || comp is null) return;
        PushIntoCell(doc, comp);
    }
    private bool CanHierarchyPushIn()
    {
        var doc = ActiveSchematicDocument;
        return CanPushInto(GetSingleSelectedCellComp(doc?.ActiveViewModel),
                           doc?.ActiveViewModel.EditModel, out _);
    }

    [RelayCommand(CanExecute = nameof(CanHierarchyPopOut))]
    private void HierarchyPopOut()
    {
        var doc = ActiveSchematicDocument;
        if (doc is null) return;
        PopOutOf(doc);
    }
    private bool CanHierarchyPopOut() => ActiveSchematicDocument?.CanPopOut ?? false;

    [RelayCommand(CanExecute = nameof(CanHierarchyPushIn))]
    private void HierarchyOpenInNewTab()
    {
        var doc  = ActiveSchematicDocument;
        var comp = GetSingleSelectedCellComp(doc?.ActiveViewModel);
        if (doc is null || comp is null) return;
        OpenCellInNewTab(doc, comp);
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
        || _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d =>
               d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir));

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
                break;
            }
            case NodeKind.DataDisplayFile:
                await SaveDataDisplayByPathAsync(node.AbsolutePath, owner);
                break;
        }

        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
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

    private async Task SaveCellViewsAsync(string cellDir, Window owner)
    {
        foreach (var p in _registry.AllDirtyPaths.Where(p => IsViewInCell(p, cellDir)).ToList())
            SaveSchematicByPath(p);
        foreach (var doc in _openDocsByPath.Values.OfType<SymbolEditorDocument>()
                     .Where(d => d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir))
                     .ToList())
            await SaveMaterializedSymbolDoc(doc, owner);
        RefreshCellDirty(cellDir);
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

        if (!cws.KnownFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            cws.KnownFiles.Add(path);
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
        }
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

        var dialog = new InputNameDialog("New Symbol", "Symbol file name (without extension):");
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
        var cellDir      = cellNode.AbsolutePath;
        var schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        if (!Directory.Exists(schematicDir))
        {
            Messages.Error($"Schematic sub-folder not found in '{cellNode.Name}'.");
            return;
        }

        var mainWindow = ResolveOwner(null);
        if (mainWindow is null) return;

        var dialog = new InputNameDialog("New Schematic", "Schematic file name (without extension):");
        var name   = await dialog.ShowDialog<string?>(mainWindow);
        if (name is null) return;

        var reason = NameValidator.Validate(name);
        if (reason is not null)
        {
            Messages.Error($"Invalid schematic name: {reason}");
            return;
        }

        var ext      = CellFolder.ViewExtension(ViewType.Schematic);
        var filePath = Path.Combine(schematicDir, name + ext);
        if (File.Exists(filePath))
        {
            Messages.Error($"A file named '{name}{ext}' already exists.");
            return;
        }

        try
        {
            // Write an empty .csch, then open it for authoring.
            var emptyModel = new SchematicEditModel();
            SchematicPersistence.SaveToFile(filePath, emptyModel, cellName: cellNode.Name);

            _factory.ProjectTreeTool?.Refresh();

            // Open in a schematic content tab (materialized — has a real file path).
            // Use BuildSessionVm so wiring matches GetOrCreateSession exactly.
            var vm  = BuildSessionVm(emptyModel);
            RegisterSession(filePath, vm);
            var doc = new SchematicDocument(name + ext, vm, filePath) { Messages = Messages, Hierarchy = this };
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;

            Messages.Success("Created", filePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create schematic: {ex.Message}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // ---- Active-document tracking (Properties region) ───────────────────────

    private async Task RefreshOpenDataDisplaysAsync(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0) return;
        var displays = _openDocsByPath.Values.OfType<DataDisplayDocument>()
            .Concat(_scratchDataDisplays);
        foreach (var dd in displays)
            await dd.ViewModel.Window.DataSourceLibrary.ReloadChangedAsync(changedPaths);
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
                _factory.PropertiesTool?.SetActiveDataDisplay(_subscribedDisplayWindow?.ActiveInspector);
        };
        window.PropertyChanged += _displayInspectorHandler;
        _factory.PropertiesTool?.SetActiveDataDisplay(window.ActiveInspector);
    }

    private void OnDocumentDockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "ActiveDockable") return;

        var activeDockable = _factory.DocumentDock?.ActiveDockable;

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
        else
        {
            RouteDataDisplayProperties(null);
            var activeVm = activeDockable is SchematicDocument schDoc ? schDoc.ViewModel : null;
            _factory.PropertiesTool?.SetActiveSchematic(activeVm);
        }

        // Analyses panel — tracks only schematics.
        var schematicVm = activeDockable is SchematicDocument sd ? sd.ViewModel : null;
        string? schName = activeDockable is SchematicDocument sdName && sdName.FilePath is { } fp
            ? System.IO.Path.GetFileName(fp) : null;
        _factory.AnalysesTool?.SetActiveSchematic(schematicVm, schName);

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

        // A dockable may have just been floated into a Dock-generated HostWindow.
        // Defer one frame (Background) so the HostWindow is fully shown before we scan.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            TryWireHostWindowsUndo,
            Avalonia.Threading.DispatcherPriority.Background);
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
    }

    // ---- Save All documents (⌘S / Ctrl+S) ----------------------------------

    /// <summary>
    /// Routes ⌘S/Ctrl+S.  When a document tab is active (SingleDoc scope) saves only that
    /// document.  When a tool panel is active (AllDocs scope) saves all dirty documents and
    /// updates the .cws.
    /// </summary>
    [RelayCommand]
    private async Task SaveAllDocuments(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        try
        {
            // SingleDoc scope: save only the active document.
            if (ActiveSaveScope == SaveScope.SingleDoc &&
                _factory.DocumentDock?.ActiveDockable is SchematicDocument singleDoc)
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
                _factory.DocumentDock?.ActiveDockable is SymbolEditorDocument singleSymDoc)
            {
                if (!singleSymDoc.IsDirty)
                {
                    Messages.Info("Nothing to save.");
                    return;
                }
                await SaveSingleSymbolDocument(singleSymDoc, window);
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

            bool anyDirty = dirtyScratch.Count > 0 || dirtyMaterialized.Count > 0
                         || dirtyScratchSymbols.Count > 0 || dirtyMaterializedSymbols.Count > 0;
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
        }
        finally
        {
            // Save All always refreshes the .cws (open-doc snapshot + tree state) when a workspace
            // is open — even when nothing was dirty, and even when no documents are open (null list).
            // For single-doc saves the .cws is still written (persists open-doc list) but silently:
            // the user only asked to save one file, so the .cws message would be noise.
            if (CurrentWorkspacePath is not null)
                WriteWorkspaceFile(CurrentWorkspacePath, silent: ActiveSaveScope != SaveScope.AllDocs);
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

    /// <summary>True when any open document has unsaved content.</summary>
    public bool HasAnyDirtyWork()
        => _scratchDocs.Any(d => d.IsDirty)
        || _openDocsByPath.Values.OfType<SchematicDocument>().Any(d => d.IsDirty)
        || _scratchSymbols.Any(d => d.IsDirty)
        || _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d => d.IsDirty)
        || _scratchDataDisplays.Any(d => d.ViewModel.Window.HasUnsavedChanges())
        || _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d => d.ViewModel.Window.HasUnsavedChanges())
        || HasOrphanedDirtySession();

    /// <summary>
    /// Shows Save / Don't Save / Cancel for dirty work before a close/quit/open action.
    /// Returns true when it's safe to proceed (saved or discarded), false when cancelled.
    /// </summary>
    public async Task<bool> PromptSaveBeforeClose(Window owner, string context = "closing")
    {
        var dirtyScratch = _scratchDocs.Where(d => d.IsDirty).ToList();
        var dirtyMat     = _openDocsByPath.Values
            .OfType<SchematicDocument>()
            .Where(d => d.IsDirty && !d.IsScratch)
            .ToList();
        var dirtyScratchSymbols = _scratchSymbols.Where(d => d.IsDirty).ToList();
        var dirtyMatSymbols     = _openDocsByPath.Values
            .OfType<SymbolEditorDocument>()
            .Where(d => d.IsDirty && !d.IsScratch)
            .ToList();
        var dirtyScratchDisplays = _scratchDataDisplays
            .Where(d => d.ViewModel.Window.HasUnsavedChanges()).ToList();
        var dirtyMatDisplays = _openDocsByPath.Values
            .OfType<DataDisplayDocument>()
            .Where(d => d.ViewModel.Window.HasUnsavedChanges()).ToList();
        var dirtyOrphanedSessions = _registry.GetOrphanedDirtyPaths(IsSessionReferenced);

        int total = dirtyScratch.Count + dirtyMat.Count
                  + dirtyScratchSymbols.Count + dirtyMatSymbols.Count
                  + dirtyScratchDisplays.Count + dirtyMatDisplays.Count
                  + dirtyOrphanedSessions.Count;
        if (total == 0) return true;

        // Build a concise message naming the single doc or giving the count.
        string? firstId =
              dirtyScratch.Count          > 0 ? dirtyScratch[0].Id
            : dirtyScratchSymbols.Count   > 0 ? dirtyScratchSymbols[0].Id
            : dirtyMat.Count              > 0 ? dirtyMat[0].Id
            : dirtyMatSymbols.Count       > 0 ? dirtyMatSymbols[0].Id
            : dirtyMatDisplays.Count      > 0 ? dirtyMatDisplays[0].Id
            : dirtyScratchDisplays.Count  > 0 ? dirtyScratchDisplays[0].Id
            : dirtyOrphanedSessions.Count > 0 ? Path.GetFileNameWithoutExtension(dirtyOrphanedSessions[0])
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
                return true; // discard everything — caller proceeds

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
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
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
                vm.ComponentPlaced += OnComponentPlaced;
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
    /// </summary>
    [RelayCommand]
    private async Task SaveLooseSchematic(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

        // Prefer the active dockable if it's a scratch doc; else first dirty scratch doc.
        var doc = _factory.DocumentDock?.ActiveDockable as SchematicDocument;
        if (doc is null || !doc.IsScratch)
            doc = _scratchDocs.FirstOrDefault(d => d.IsDirty);

        if (doc is null)
        {
            Messages.Info("No scratch schematic to save.");
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

            // Scratch → materialized transition.
            _scratchDocs.Remove(doc);
            _recovery.ClearDoc(doc);
            doc.Materialize(filePath);
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

            // Materialize (plain — no workspace registration, no Known-File entry).
            _scratchDocs.Remove(doc);
            _recovery.ClearDoc(doc);
            doc.Materialize(filePath);
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

    // ---- Quit ----------------------------------------------------------------

    [RelayCommand]
    private void QuitApplication()
        => (App.Current as App)?.Quit();

    // ---- Test messages command (Help → Post Test Messages) ------------------

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
