using System;
using System.Collections.Generic;
using System.IO;
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

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Main ViewModel for the Workspace window. Owns the Dock layout, undo/redo stack,
/// message sink, and all menu/toolbar commands. The GUI never simulates the design layer
/// directly — it always builds/edits the design layer, then asks the engine to elaborate
/// and run (6e). For 6b this is the frame: layout + commands wired but stubbed.
/// </summary>
public partial class WorkspaceViewModel : ViewModelBase
{
    // ---- Dock layout ---------------------------------------------------------

    private readonly CircuitRfDockFactory _factory;

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

    partial void OnCurrentWorkspacePathChanged(string? value)
    {
        WindowTitle = value is not null
            ? $"{Path.GetFileNameWithoutExtension(value)} — circuitRF"
            : "circuitRF";
    }

    // ---- Constructor ---------------------------------------------------------

    public WorkspaceViewModel()
    {
        _factory = new CircuitRfDockFactory();

        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;

        Messages = _factory.MessagesTool
            ?? throw new InvalidOperationException("DockFactory must expose MessagesTool.");

        // Wire project tree double-click → open stub Content tab.
        if (_factory.ProjectTreeTool is { } tree)
            tree.OpenItemRequested = OpenTreeItem;

        // Notify PropertiesTool when the active document tab changes (active schematic tracking).
        if (_factory.DocumentDock is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnDocumentDockPropertyChanged;

        // Post a welcome message.
        Messages.Info("circuitRF ready. Open a workspace or add a library to get started.");
    }

    // ---- File commands -------------------------------------------------------

    [RelayCommand]
    private void NewWorkspace()
    {
        SetActiveUndoTarget(null);
        CurrentWorkspacePath = null;
        // Reset Dock layout to default.
        var newLayout = _factory.CreateDefaultLayout();
        _factory.InitLayout(newLayout);
        Layout = newLayout;
        Messages.Clear();
        Messages.Info("New workspace created.");
    }

    [RelayCommand]
    private async Task OpenWorkspace(Window? owner)
    {
        if (owner is null) return;
        var result = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Workspace",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("circuitRF Workspace") { Patterns = new[] { "*.cws" } },
                new FilePickerFileType("All Files")           { Patterns = new[] { "*.*" } },
            },
        });

        if (result.Count == 0) return;
        var path = result[0].Path.LocalPath;
        CurrentWorkspacePath = path;

        // Apply workspace color scheme if recorded.
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(path);
            if (cws.ColorSchemeName is { } schemeName)
                ThemeService.Active = ThemeResolver.Resolve(schemeName, Path.GetDirectoryName(path));
        }
        catch { }

        Messages.Success($"Opened: {path}");
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
        if (owner is null) return;
        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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

    // ---- Project Tree double-click ------------------------------------------

    private void OpenTreeItem(ProjectTreeItemViewModel item)
    {
        if (item.Kind == ProjectTreeItemKind.DataDisplay)
        {
            var stub = new StubDocument(item.Name, StubDocument.StubKind.DataDisplay);
            _factory.OpenDocument(stub);
            return;
        }

        if (item.Kind is ProjectTreeItemKind.Cell or ProjectTreeItemKind.TestBench)
        {
            var renderModel = item.Name == "StressTest10k"
                ? SchematicModelBuilder.GenerateStressTest(10_000)
                : SchematicModelBuilder.BuildHero2PA();

            var editModel = SchematicEditModel.FromRenderModel(renderModel);
            var vm        = new SchematicViewModel(editModel, Messages);
            var doc       = new SchematicDocument(item.Name, vm) { Messages = Messages };
            _factory.OpenDocument(doc);
            return;
        }
    }

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
