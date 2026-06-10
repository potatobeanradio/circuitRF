using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using CircuitRF.Ui.Commands;
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
public partial class WorkspaceViewModel : ViewModelBase, ITreeActions
{
    // ---- Dock layout ---------------------------------------------------------

    private readonly CircuitRfDockFactory _factory;

    // ---- Open-document tracking (dedup by absolute path) --------------------

    // Maps absolute path → the open dockable.  Checked before opening a new tab.
    // Not persisted; rebuilt from the Dock state on workspace open.
    private readonly Dictionary<string, IDockable> _openDocsByPath
        = new(StringComparer.OrdinalIgnoreCase);

    // Scratch documents have no path so they cannot go in _openDocsByPath.
    // Tracked here for enumeration by save/rebuild operations (steps 2+).
    // NOTE (step 1): entries are not removed when a scratch tab is closed — that
    // cleanup and the close-prompt are added in step 2/3.
    private readonly List<SchematicDocument> _scratchDocs = [];

    [ObservableProperty] private IRootDock? _layout;

    // ---- Infrastructure ------------------------------------------------------

    public IMessageSink Messages { get; }

    // ---- Per-document undo routing ------------------------------------------

    // The active editable document; null when no undoable document is active.
    private IUndoableDocument? _activeUndoTarget;

    // Windows that already have undo/redo KeyBindings injected (Dock float support).
    private readonly HashSet<Window> _wiredHostWindows = [];

    public string UndoDescription => _activeUndoTarget?.UndoRedo.UndoDescription ?? "Undo";
    public string RedoDescription => _activeUndoTarget?.UndoRedo.RedoDescription ?? "Redo";

    // ---- Window title --------------------------------------------------------

    [ObservableProperty] private string _windowTitle = "circuitRF";
    [ObservableProperty] private string? _currentWorkspacePath;

    // Last-used parent directory for the New Workspace dialog (in-memory, not persisted).
    // Seeds the Location field so repeated New Workspace dialogs start at the same folder.
    private string _lastWorkspaceParentDir =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ---- Recent Workspaces (persisted in AppPreferences) --------------------

    private readonly AppPreferences _preferences;
    private readonly List<string> _recentWorkspaces;

    // Observable collection of menu items for the in-window "Open Recent" submenu.
    // Rebuilt by RebuildRecentMenuItems() after every push/clear.
    public ObservableCollection<Control> RecentMenuItems { get; } = new();

    // True when the recent list is non-empty; drives IsEnabled on the "Open Recent" MenuItem.
    public bool HasRecentWorkspaces => _recentWorkspaces.Count > 0;

    // Exposed for the NativeMenu code-behind rebuild.
    public IReadOnlyList<string> RecentWorkspacesList => _recentWorkspaces;

    // Fired (on UI thread) after any push or clear so the NativeMenu code-behind can rebuild.
    public event Action? RecentWorkspacesChanged;

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
    }

    // ---- Constructor ---------------------------------------------------------

    public WorkspaceViewModel()
    {
        _factory = new CircuitRfDockFactory();

        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;

        // Wire tree-item actions before any workspace is loaded so actions are available
        // the moment SetWorkspace builds the first VM tree.
        _factory.ProjectTreeTool?.SetActions(this);

        Messages = _factory.MessagesTool
            ?? throw new InvalidOperationException("DockFactory must expose MessagesTool.");

        // Notify PropertiesTool when the active document tab changes (active schematic tracking).
        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnDocumentDockPropertyChanged;

        // Load persisted preferences and seed the recent list.
        _preferences     = AppPreferencesIo.Load();
        _recentWorkspaces = new List<string>(_preferences.RecentWorkspaces ?? []);
        RebuildRecentMenuItems();

        // Auto-open one scratch schematic so the app lands directly on an editable canvas.
        // Step 2/3 will add save-on-close; for now closing the tab loses it (noted).
        NewScratchSchematic();
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

    // ---- File commands -------------------------------------------------------

    [RelayCommand]
    private async Task NewWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null) return;

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
            WorkspacePersistence.SaveToFile(cwsPath, new CwsFile());

            // Update tracked location to the chosen parent (seeds the next New Workspace dialog).
            _lastWorkspaceParentDir = result.ParentDir;

            SetActiveUndoTarget(null);
            _openDocsByPath.Clear();
            _scratchDocs.Clear();
            CurrentWorkspacePath = cwsPath;

            var newLayout = _factory.CreateDefaultLayout();
            _factory.InitLayout(newLayout);
            Layout = newLayout;

            // CreateDefaultLayout replaced ProjectTreeTool with a fresh instance — re-wire it.
            _factory.ProjectTreeTool?.SetActions(this);
            _factory.ProjectTreeTool?.SetWorkspace(workspaceDir);

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

        // Update tracked location to the parent of the opened workspace folder.
        _lastWorkspaceParentDir = Path.GetDirectoryName(workspaceDir) ?? _lastWorkspaceParentDir;

        CurrentWorkspacePath = cwsPath;

        // Apply workspace color scheme if recorded.
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(cwsPath);
            if (cws.ColorSchemeName is { } schemeName)
                ThemeService.Active = ThemeResolver.Resolve(schemeName, workspaceDir);
        }
        catch { }

        PushRecent(cwsPath);
        Messages.Success($"Opened: {cwsPath}");
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

    private void WriteWorkspaceFile(string path)
    {
        try
        {
            var activeName = ThemeService.Active.Name;
            var ws = new CwsFile
            {
                // Member files and dock layout serialization deferred — workspace manifest
                // scaffolding is wired here; full member tracking in later phases.
                ColorSchemeName = activeName != "Default" ? activeName : null,
            };
            WorkspacePersistence.SaveToFile(path, ws);
            Messages.Success($"Saved: {path}", path);
        }
        catch (Exception ex)
        {
            Messages.Error($"Workspace save failed: {ex.Message}");
        }
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
    private void OpenRecentWorkspace(string? cwsPath)
    {
        if (cwsPath is null) return;

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

        var workspaceDir = Path.GetDirectoryName(cwsPath)!;
        _lastWorkspaceParentDir = Path.GetDirectoryName(workspaceDir) ?? _lastWorkspaceParentDir;
        CurrentWorkspacePath = cwsPath;

        try
        {
            var cws = WorkspacePersistence.LoadFromFile(cwsPath);
            if (cws.ColorSchemeName is { } schemeName)
                ThemeService.Active = ThemeResolver.Resolve(schemeName, workspaceDir);
        }
        catch { }

        PushRecent(cwsPath);
        Messages.Success($"Opened: {cwsPath}");
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
        _preferences.RecentWorkspaces = _recentWorkspaces.Count > 0
            ? new List<string>(_recentWorkspaces)
            : null;
        AppPreferencesIo.Save(_preferences);
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
        if (_activeUndoTarget?.UndoRedo is { } old)
            old.PropertyChanged -= OnActiveStackPropertyChanged;

        _activeUndoTarget = target;

        if (_activeUndoTarget?.UndoRedo is { } stack)
            stack.PropertyChanged += OnActiveStackPropertyChanged;

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

    // Cut / Copy / Paste / Select All — no-ops at the window level.
    // Each active control (TextBox, SchematicCanvas) handles clipboard natively via its own
    // key routing.  These stubs satisfy NativeMenuItem Command bindings without interfering.
    [RelayCommand] private void Cut()       { }
    [RelayCommand] private void Copy()      { }
    [RelayCommand] private void Paste()     { }
    [RelayCommand] private void SelectAll() { }

    // ---- View commands -------------------------------------------------------

    [RelayCommand]
    private void ResetLayout()
    {
        var newLayout = _factory.CreateDefaultLayout();
        _factory.InitLayout(newLayout);
        Layout = newLayout;
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

    [RelayCommand] private void RunAnalysis()  { Messages.Warning("Run: no TestBench configured yet (6e)."); }
    [RelayCommand] private void StopAnalysis() { Messages.Info("Stop: no analysis running."); }

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
        // filePath = null → scratch; IsScratch = true, IsDirty = true, Title = "• <title>"
        var doc   = new SchematicDocument(title, vm) { Messages = Messages };

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
            var doc = new SymbolEditorDocument(Path.GetFileNameWithoutExtension(path), vm);
            _factory.OpenDocument(doc);
            Messages.Success($"Opened: {path}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open symbol: {ex.Message}");
        }
    }

    // ---- ITreeActions — double-click, context-menu actions ------------------

    // ── Open / activate (dedup by absolute path) ──────────────────────────────

    /// <inheritdoc/>
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

            default:
                // Folder nodes, data displays, colour themes, etc. → no-op (no viewer yet)
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
            editable.UserEditable = true;
            var vm  = new SymbolEditorViewModel(editable) { CurrentSymbolPath = absolutePath };
            vm.SymbolSaved += OnSymbolSaved;
            var doc = new SymbolEditorDocument(Path.GetFileNameWithoutExtension(absolutePath), vm);
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            Messages.Success($"Opened: {absolutePath}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open symbol: {ex.Message}");
        }
    }

    private void OpenOrActivateSchematic(string absolutePath)
    {
        if (ActivateIfOpen(absolutePath)) return;

        try
        {
            var (editModel, _, cellName) = SchematicPersistence.LoadFromFile(absolutePath);
            var vm  = new SchematicViewModel(editModel, Messages);
            var title = string.IsNullOrWhiteSpace(cellName)
                ? Path.GetFileNameWithoutExtension(absolutePath)
                : cellName;
            var doc = new SchematicDocument(title, vm, absolutePath) { Messages = Messages };
            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            Messages.Success($"Opened: {absolutePath}");
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open schematic: {ex.Message}");
        }
    }

    private void OpenOrActivateCellPlaceholder(string absolutePath, string cellName)
    {
        if (ActivateIfOpen(absolutePath)) return;

        // Cell parameter editor is step 6 — open a placeholder stub tab.
        var doc = new StubDocument($"Cell: {cellName}", StubDocument.StubKind.Welcome);
        _factory.OpenDocument(doc);
        _openDocsByPath[absolutePath] = doc;
        Messages.Info($"Cell parameter editor coming in step 6 — '{cellName}' placeholder opened.");
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

            Messages.Success($"'{filename}' is now the primary view.");
        }
        catch (Exception ex)
        {
            Messages.Error($"Make Primary failed: {ex.Message}");
        }
    }

    // ── Live-update helpers (cell-ref resolver + schematic rebuild) ──────────

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

    // ── Creation actions ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task NewCellAsync(ProjectTreeNodeViewModel parentNode)
    {
        var parentDir  = parentNode.AbsolutePath;
        var mainWindow = GetMainWindow();
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
            Messages.Success($"Cell '{name}' created.");
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
        var mainWindow   = GetMainWindow();
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
            Messages.Success($"Cell '{name}' created.");
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

        var mainWindow = GetMainWindow();
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
            var doc = new SymbolEditorDocument(name, vm);
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;

            Messages.Success($"Symbol '{name}{ext}' created and opened.");
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

        var mainWindow = GetMainWindow();
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
            var vm  = new SchematicViewModel(emptyModel, Messages);
            var doc = new SchematicDocument(name, vm, filePath) { Messages = Messages };
            _factory.OpenDocument(doc);
            _openDocsByPath[filePath] = doc;

            Messages.Success($"Schematic '{name}{ext}' created and opened.");
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to create schematic: {ex.Message}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Returns the main workspace window, or null if not available.</summary>
    private Window? GetMainWindow()
        => (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // ---- Active-document tracking (Properties region) ───────────────────────

    private void OnDocumentDockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "ActiveDockable") return;

        var activeDockable = _factory.DocumentDock?.ActiveDockable;

        // Properties panel — tracks only schematics.
        var activeVm = activeDockable is SchematicDocument schDoc ? schDoc.ViewModel : null;
        _factory.PropertiesTool?.SetActiveSchematic(activeVm);

        // Undo routing — follows any IUndoableDocument for main-window tabs.
        SetActiveUndoTarget(activeDockable as IUndoableDocument);

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
