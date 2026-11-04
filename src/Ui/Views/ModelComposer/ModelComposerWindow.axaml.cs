using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.ModelComposer;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>
/// The Model Composer window (brief-modelcomposer-14 R-mc14-1 … R-mc14-3). One window per <c>.cmc</c>, non-modal, shown
/// unowned and cascaded off the workspace like railRF's, at the golden-ratio size.
/// </summary>
/// <remarks>
/// The code-behind places the panels, gives the space a hidden panel released to the ones still showing (a column's width
/// is not something a child's IsVisible reaches), and fills the view model's seams: the dispatcher, the pickers, the
/// importer, the save-changes dialog and the clipboard. Everything else is the view model's.
/// </remarks>
public partial class ModelComposerWindow : Window, ICrfDocumentWindow
{
    /// <summary>One window per document path. A new document is in no table until its first save.</summary>
    private static readonly Dictionary<string, ModelComposerWindow> Open = new(StringComparer.OrdinalIgnoreCase);

    private string? _openKey;
    private bool _closeConfirmed;
    private readonly GridLength _stepsWidth, _rightWidth;

    public ModelComposerWindow()
    {
        InitializeComponent();
        _stepsWidth = PaneGrid.ColumnDefinitions[0].Width;
        _rightWidth = PaneGrid.ColumnDefinitions[4].Width;
        OpenButton.Click += async (_, _) => await OpenDocumentAsync();
        RunMenuButton.Click += (_, _) => ShowRunMenu();
        HelpButton.Click += (_, _) => DocLauncher.Open("reference/model-composer.html");
        DataContextChanged += (_, _) => { if (Vm is { } vm) Install(vm); };
    }

    private ModelComposerViewModel? Vm => DataContext as ModelComposerViewModel;

    // ── opening ──────────────────────────────────────────────────────────────

    /// <summary>A new document — the Active / Passive chooser, then Save As on first save.</summary>
    public static ModelComposerWindow ShowNew(Window? owner, Action<string>? openSchematic = null,
                                             Func<string, string?, Window, Task>? publishToLibrary = null)
    {
        var window = new ModelComposerWindow { DataContext = new ModelComposerViewModel() };
        if (window.Vm is { } vm) { vm.OpenSchematic = openSchematic; window.SetPublisher(vm, publishToLibrary); }
        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>Opens <paramref name="path"/>, or raises the window already holding it. A refusal throws
    /// <see cref="CmcFormatException"/> naming its field.</summary>
    public static ModelComposerWindow ShowFile(string path, Window? owner, Action<string>? openSchematic = null,
                                              Func<string, string?, Window, Task>? publishToLibrary = null)
    {
        string key = Path.GetFullPath(path);
        if (Open.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return existing;
        }
        var vm = ModelComposerViewModel.OpenFile(key);
        vm.OpenSchematic = openSchematic;
        var window = new ModelComposerWindow { DataContext = vm };
        window.SetPublisher(vm, publishToLibrary);
        window.AdoptPath(key);
        ShowUnowned(window, owner);
        vm.BeginLoad();
        return window;
    }

    private static void ShowUnowned(ModelComposerWindow window, Window? owner)
    {
        window.Closed += (_, _) =>
        {
            if (window._openKey is { } k) Open.Remove(k);
            window.Vm?.Dispose();
        };
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            return;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        void CloseWithOwner(object? _, EventArgs __) => window.Close();
        owner.Closed += CloseWithOwner;
        window.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        var at = MatchWindowPlacement.Cascade(owner.Position, owner.RenderScaling, owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);
        window.Position = at;
        window.Show();
        window.Position = at;
        NewWindowFront.Keep(window, owner);
    }

    /// <summary>The workspace's Export to Library (brief vendorlib-17), owned by this window; kept so a document opened
    /// from this window publishes through the same workspace.</summary>
    private Func<string, string?, Window, Task>? _publishToLibrary;

    private void SetPublisher(ModelComposerViewModel vm, Func<string, string?, Window, Task>? publish)
    {
        _publishToLibrary = publish;
        vm.PublishToLibrary = publish is null ? null : (doc, cmodel) => publish(doc, cmodel, this);
        vm.RefreshExportToLibrary();
    }

    private void AdoptPath(string path)
    {
        string key = Path.GetFullPath(path);
        if (string.Equals(_openKey, key, StringComparison.OrdinalIgnoreCase)) return;
        if (_openKey is { } old) Open.Remove(old);
        _openKey = key;
        Open[key] = this;
    }

    private async Task OpenDocumentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Model Composer — Open",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Model Composer document") { Patterns = ["*" + CmcFile.Extension] }],
        });
        if (files.Count == 0) return;
        try
        {
            bool disposable = Vm is { DocumentPath: null, IsDirty: false };
            var opened = ShowFile(files[0].Path.LocalPath, disposable ? null : this, Vm?.OpenSchematic, _publishToLibrary);
            if (disposable && !ReferenceEquals(opened, this))
            {
                opened.Position = Position;
                Close();
            }
        }
        catch (Exception ex) when (ex is CmcFormatException or IOException or UnauthorizedAccessException)
        {
            Vm?.Error($"{Path.GetFileName(files[0].Path.LocalPath)} did not read: {ex.Message}");
        }
    }

    // ── the seams ────────────────────────────────────────────────────────────

    private async Task<string?> PickSave(ModelComposerViewModel vm, string title, string name, string kind, string pattern,
                                         string? folderPath = null)
    {
        IStorageFolder? start = (folderPath ?? vm.DocumentFolder) is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedStartLocation = start,
            SuggestedFileName = name,
            DefaultExtension = pattern.TrimStart('*', '.'),
            FileTypeChoices = [new FilePickerFileType(kind) { Patterns = [pattern] }],
        });
        return file?.Path.LocalPath;
    }

    private void Install(ModelComposerViewModel vm)
    {
        vm.PostToUi = a => Dispatcher.UIThread.Post(a);
        vm.PathAdopted += AdoptPath;
        vm.SaveRefusedFor = path =>
            Open.TryGetValue(Path.GetFullPath(path), out var other) && !ReferenceEquals(other, this)
                ? $"{Path.GetFileName(path)} is open in another Model Composer window; save under another name or close that window."
                : null;

        vm.PickSavePath = prompt => PickSave(vm, prompt.Title ?? "Save the Model Composer document", prompt.Name,
                                             "Model Composer document", "*" + CmcFile.Extension, prompt.Folder);
        vm.PickFile = async (title, patterns) =>
        {
            IStorageFolder? start = vm.DocumentFolder is { } folder ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = [new FilePickerFileType(title) { Patterns = [.. patterns] }],
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };
        vm.AskSaveChanges = question => new SaveChangesDialog(question, saveLabel: "Save", dontSaveLabel: "Don't Save",
                                                              cancelLabel: "Cancel", title: "Unsaved Changes")
                                        .ShowDialog<SaveChangesResult>(this);
        vm.ShowImporter = importer => new ModelImportDialog(importer).ShowDialog<CmcDataSet?>(this);
        vm.NetworkCopySink = async model =>
        {
            if (Clipboard is not { } clipboard) return;
            IntPtr owner = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await SchematicClipboard.CopyAsync(clipboard, model.Components, model.Wires, model.CanvasObjects, model.GridSize,
                                               netLabels: model.NetLabels, schematicDirectory: vm.DocumentFolder, ownerHwnd: owner);
        };
        vm.NetworkPasteSource = async () =>
        {
            if (Clipboard is not { } clipboard) return null;
            var payload = await SchematicClipboard.PasteAsync(clipboard, offsetX: 0, offsetY: 0);
            return payload is { } p ? (p.Comps, (IReadOnlyList<EditableWire>)p.Wires, (IReadOnlyList<EditableNetLabel>)p.NetLabels) : null;
        };

        // MC-15: Show testbench, Copy instance, the export and PDF pickers, and the report dialog.
        vm.RevealFile = path => FileReveal.Reveal(path);
        vm.CopyInstanceSink = async payload =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(payload);
        };
        vm.PickModelPath = name => PickSave(vm, "Export the model", name, "Composed model", "*" + CmodelFile.Extension);
        vm.PickVerilogAPath = suggested => PickSave(vm, "Export the model as Verilog-A", Path.GetFileNameWithoutExtension(suggested),
                                                     "Verilog-A source", "*.va", Path.GetDirectoryName(suggested));
        vm.PickPdfPath = name => PickSave(vm, "Export the report as PDF", name, "PDF", "*.pdf");
        vm.ShowReportDialog = report => new ModelReportDialog(report).ShowDialog(this);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelComposerViewModel.ShowSteps) or nameof(ModelComposerViewModel.ShowPlots)
                               or nameof(ModelComposerViewModel.ShowNetwork) or nameof(ModelComposerViewModel.ShowValues))
                SyncPanes();
            if (e.PropertyName is nameof(ModelComposerViewModel.StatusLevel)) SyncStatus();
        };
        SyncPanes();
        SyncStatus();
    }

    private void ShowRunMenu()
    {
        if (Vm is not { } vm) return;
        var menu = new ContextMenu
        {
            ItemsSource = vm.RunItems.Select(i => new MenuItem { Header = i.Label, Command = vm.RunCommand, CommandParameter = i.Key }).ToList(),
        };
        menu.Open(RunMenuButton);
    }

    private void SyncStatus()
    {
        if (Vm is not { } vm) return;
        StatusLine.Classes.Set("warn", vm.StatusLevel != ModelStatusLevel.Info);
    }

    /// <summary>Gives the space a hidden panel released to the panels still showing.</summary>
    private void SyncPanes()
    {
        if (Vm is not { } vm) return;
        bool steps = vm.ShowSteps, plots = vm.ShowPlots, right = vm.ShowRightColumn;
        var cols = PaneGrid.ColumnDefinitions;
        cols[0].Width = !steps ? new GridLength(0) : plots || right ? _stepsWidth : new GridLength(1, GridUnitType.Star);
        cols[2].Width = plots ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        cols[4].Width = !right ? new GridLength(0) : plots ? _rightWidth : new GridLength(1, GridUnitType.Star);
        LeftSplitter.IsVisible = steps && (plots || right);
        RightSplitter.IsVisible = right && plots;
        var rows = RightColumn.RowDefinitions;
        rows[0].Height = vm.ShowNetwork ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rows[2].Height = vm.ShowValues ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightRowSplitter.IsVisible = vm.ShowNetwork && vm.ShowValues;
    }

    // ── closing a window closes a document, so it asks ───────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Vm is not { } vm || (!vm.IsDirty && !vm.ViewChanged))
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _ = ReissueCloseAsync();
    }

    private async Task ReissueCloseAsync()
    {
        if (await ConfirmCloseAsync()) Close();
        else (Avalonia.Application.Current as App)?.AbortQuit();
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (_closeConfirmed) return true;
        if (Vm is { } vm && !await vm.ConfirmCloseAsync()) return false;
        _closeConfirmed = true;
        return true;
    }
}
