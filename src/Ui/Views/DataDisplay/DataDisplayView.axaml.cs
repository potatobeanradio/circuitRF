using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class DataDisplayView : UserControl
{
    public DataDisplayView()
    {
        InitializeComponent();
        Loaded               += OnLoaded;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is DataDisplayDocument doc)
        {
            doc.ViewModel.Window.RefreshAvailableDataSources();
            _ = doc.ViewModel.Window.CheckPasteStateAsync();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var win = doc.ViewModel.Window;

        win.SetOpenFileAction(DoOpenFileAsync);
        win.SetGetCanvasSizeAction(() => win.ActiveTab?.GetCanvasSize() ?? (800.0, 600.0));
        win.SetSaveDataDisplayAsAction(DoSaveDisplayAsAsync);
        win.SetOpenDataDisplayAction(DoOpenDisplayAsync);
        win.SetLoadRunResultsAction(DoLoadRunResultsAsync);
        win.SetExportDataAction(DoExportDataAsync);
        // Geometry not persisted — embedded Dock document, not a floating OS window.
        win.SetGetWindowGeometryAction(() => (0, 0, 0, 0));

        // Wire DataSourceLibrary helpers so context-menu and broken-entry operations work.
        win.DataSourceLibrary.ImportCommand = win.OpenFileCommand;

        win.DataSourceLibrary.CopyToClipboardFunc = async text =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb is not null) await cb.SetTextAsync(text);
        };

        win.SetRichCopyAction((containers, theme) =>
            PlotExporter.CopyContainersToClipboardAsync(this, containers, theme));
        win.SetGetClipboardTextAction(async () =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            return cb is null ? null : await cb.TryGetTextAsync();
        });

        win.DataSourceLibrary.FindMissingFileAsync = path => FindMissingSnpFileAsync(path);
    }

    private async Task DoOpenFileAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Load Data Files",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Data Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts", "*.npy" }
                },
                new FilePickerFileType("Touchstone Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts" }
                },
                new FilePickerFileType("NumPy Files") { Patterns = new[] { "*.npy" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var f in files)
            await doc.ViewModel.Window.DataSourceLibrary.LoadFileAsync(f.Path.LocalPath);
    }

    private async Task DoSaveDisplayAsAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Display",
            DefaultExtension  = "cdd",
            FileTypeChoices   = new[]
            {
                new FilePickerFileType("circuitRF Data Display") { Patterns = new[] { "*.cdd" } }
            }
        });
        if (file is null) return;

        try
        {
            await doc.ViewModel.Window.SaveAllAsync(file.Path.LocalPath, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Could not save display: {ex.Message}");
        }
    }

    private async Task DoOpenDisplayAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Display",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("circuitRF Data Display") { Patterns = new[] { "*.cdd" } },
                new FilePickerFileType("All Files")              { Patterns = new[] { "*.*" } }
            }
        });
        if (files.Count != 1) return;

        var file = files[0];
        try
        {
            await using var stream = await file.OpenReadAsync();
            // WorkspaceViewModel injects OpenFileAsNewDisplayAction so the file opens
            // as a new document tab.  Falls back to loading into this document when
            // running outside a workspace context (standalone / tests).
            await doc.ViewModel.Window.OpenFileAsNewDisplayAsync(file.Path.LocalPath, stream);
        }
        catch (InvalidDataException ex)
        {
            await ShowErrorAsync($"Cannot open display: {ex.Message}");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Could not open display: {ex.Message}");
        }
    }

    private async Task DoLoadRunResultsAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        IStorageFolder? suggestedStart = null;
        var resultsRoot = doc.ViewModel.Window.GetResultsRootAction?.Invoke();
        if (resultsRoot is not null && Directory.Exists(resultsRoot))
            suggestedStart = await sp.TryGetFolderFromPathAsync(resultsRoot);

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Load Run Results",
            AllowMultiple          = false,
            SuggestedStartLocation = suggestedStart
        });
        if (folders.Count != 1) return;

        var folder = folders[0].Path.LocalPath;
        var npyFiles = Directory.GetFiles(folder, "*.npy");
        if (npyFiles.Length == 0)
        {
            await ShowErrorAsync($"No .npy results in {folder}");
            return;
        }

        foreach (var path in npyFiles)
            await doc.ViewModel.Window.DataSourceLibrary.LoadFileAsync(path);
    }

    private async Task DoExportDataAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var win = doc.ViewModel.Window;

        string? resultsRoot = win.GetResultsRootAction?.Invoke();

        string? preselect = null;
        var srcAbs = win.DataSourceLibrary.SelectedDataSourceAbs;
        if (srcAbs is not null && srcAbs.EndsWith("run.npy", StringComparison.OrdinalIgnoreCase))
        {
            var sub = Path.GetDirectoryName(srcAbs);
            if (sub is not null) preselect = Path.GetFileName(sub);
        }

        var vm     = new DataExporterViewModel(resultsRoot, preselect);
        var owner  = TopLevel.GetTopLevel(this) as Window;
        await DataExporterDialog.ShowAsync(owner, vm);
    }

    private async Task ShowErrorAsync(string message)
    {
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        await new SaveChangesDialog(message, "OK", null, "", title: "Error")
            .ShowDialog(parent);
    }

    private async Task<string?> FindMissingSnpFileAsync(string missingFilePath)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return null;

        string missingFileName = Path.GetFileName(missingFilePath);
        string? startDir      = Path.GetDirectoryName(missingFilePath);

        IStorageFolder? suggestedDir = null;
        if (startDir is not null && Directory.Exists(startDir))
            suggestedDir = await sp.TryGetFolderFromPathAsync(startDir);

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = $"Locate: {missingFileName}",
            AllowMultiple          = false,
            SuggestedStartLocation = suggestedDir,
            FileTypeFilter         = new[]
            {
                new FilePickerFileType("Data Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts", "*.npy" }
                },
                new FilePickerFileType("Touchstone Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts" }
                },
                new FilePickerFileType("NumPy Files") { Patterns = new[] { "*.npy" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }
}
