using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class DataDisplayView : UserControl
{
    public DataDisplayView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var win = doc.ViewModel.Window;

        win.SetOpenFileAction(DoOpenFileAsync);
        win.SetGetCanvasSizeAction(() => win.ActiveTab?.GetCanvasSize() ?? (800.0, 600.0));
        win.SetSaveDataDisplayAsAction(DoSaveDisplayAsAsync);
        win.SetOpenDataDisplayAction(DoOpenDisplayAsync);
        // Geometry not persisted — embedded Dock document, not a floating OS window.
        win.SetGetWindowGeometryAction(() => (0, 0, 0, 0));

        // Wire SnpLibrary helpers so context-menu and broken-entry operations work.
        win.SnpLibrary.ImportCommand = win.OpenFileCommand;

        win.SnpLibrary.CopyToClipboardFunc = async text =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb is not null) await cb.SetTextAsync(text);
        };

        win.SnpLibrary.FindMissingFileAsync = path => FindMissingSnpFileAsync(path);
    }

    private async Task DoOpenFileAsync()
    {
        if (DataContext is not DataDisplayDocument doc) return;
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Load Touchstone Files",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Touchstone Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var f in files)
            await doc.ViewModel.Window.SnpLibrary.LoadFileAsync(f.Path.LocalPath);
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

    private async Task ShowErrorAsync(string message)
    {
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        await new SaveChangesDialog(message, "OK", null, "")
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
                new FilePickerFileType("Touchstone Files")
                {
                    Patterns = new[] { "*.s1p", "*.s2p", "*.s3p", "*.s4p",
                                       "*.s5p", "*.s6p", "*.snp", "*.ts" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }
}
