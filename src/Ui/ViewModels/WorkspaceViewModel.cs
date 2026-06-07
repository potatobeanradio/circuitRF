using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Messages;
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

    public UndoRedoStack UndoRedo { get; } = new();
    public IMessageSink Messages { get; }

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

        // Post a welcome message.
        Messages.Info("circuitRF ready. Open a workspace or add a library to get started.");
    }

    // ---- File commands -------------------------------------------------------

    [RelayCommand]
    private void NewWorkspace()
    {
        UndoRedo.Reset();
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
                new FilePickerFileType("circuitRF Workspace") { Patterns = new[] { "*.crfw" } },
                new FilePickerFileType("All Files")           { Patterns = new[] { "*.*" } },
            },
        });

        if (result.Count == 0) return;
        var path = result[0].Path.LocalPath;
        // Stub: actual workspace deserialization wired in 6c.
        CurrentWorkspacePath = path;
        Messages.Success($"Opened: {path}");
    }

    [RelayCommand]
    private async Task SaveWorkspace(Window? owner)
    {
        if (CurrentWorkspacePath is not null)
        {
            // Stub: actual serialization wired in 6c.
            Messages.Success($"Saved: {CurrentWorkspacePath}");
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
            DefaultExtension = "crfw",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("circuitRF Workspace") { Patterns = new[] { "*.crfw" } },
            },
        });

        if (result is null) return;
        CurrentWorkspacePath = result.Path.LocalPath;
        // Stub: actual serialization wired in 6c.
        Messages.Success($"Saved: {CurrentWorkspacePath}");
    }

    [RelayCommand]
    private async Task AddLibrary(Window? owner)
    {
        // Stub for 6b — library management wired in 6c.
        Messages.Info("Add Library: not yet implemented (6c).");
        await Task.CompletedTask;
    }

    // ---- Edit commands (route through UndoRedoStack) -------------------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoRedo.Undo();
    private bool CanUndo() => UndoRedo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => UndoRedo.Redo();
    private bool CanRedo() => UndoRedo.CanRedo;

    // Cut / Copy / Paste / Select All — placeholder; real impl in 6d with schematic editor.
    [RelayCommand] private void Cut()       { Messages.Info("Cut: not yet implemented (6d)."); }
    [RelayCommand] private void Copy()      { Messages.Info("Copy: not yet implemented (6d)."); }
    [RelayCommand] private void Paste()     { Messages.Info("Paste: not yet implemented (6d)."); }
    [RelayCommand] private void SelectAll() { Messages.Info("Select All: not yet implemented (6d)."); }

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
        var w = new Views.Dialogs.SettingsView();
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

    // ---- Project Tree double-click ------------------------------------------

    private void OpenTreeItem(ProjectTreeItemViewModel item)
    {
        var doc = new StubDocument(
            item.Name,
            item.Kind == ProjectTreeItemKind.DataDisplay
                ? StubDocument.StubKind.DataDisplay
                : StubDocument.StubKind.Schematic);
        _factory.OpenDocument(doc);
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
