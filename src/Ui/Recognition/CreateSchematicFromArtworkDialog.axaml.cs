using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Ui.Recognition;

/// <summary>
/// Design ▸ Create Schematic from Artwork…, and Create Schematic / Layout from Image… (brief-artsch-8-gui-command.md
/// R-as8-2; brief-img-5-dialog.md R-im5-1) — one window, its source chosen by its view model. Non-modal. The window owns
/// only what needs a window — the file pickers, the clipboard, a drop — and hands each to the view model as a function;
/// everything the dialog decides is the view model's, so it is tested without one.
/// </summary>
public partial class CreateSchematicFromArtworkDialog : Window
{
    private readonly RecognitionSessionViewModel? _vm;

    public CreateSchematicFromArtworkDialog() => InitializeComponent();

    public CreateSchematicFromArtworkDialog(RecognitionSessionViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.BrowseModelAsync = () => PickOpen("Touchstone model", new FilePickerFileType("Touchstone") { Patterns = ["*.s2p", "*.S2P"] });
        vm.PickImportPathAsync = () => PickOpen("Import parts table", new FilePickerFileType("CSV") { Patterns = ["*.csv"] });
        vm.PickExportPathAsync = PickExport;
        switch (vm)
        {
            case CreateSchematicFromArtworkViewModel artwork:
                artwork.PickFileAsync = title => PickOpen(title, null);
                break;
            case ImageSourceViewModel image:
                // The picture is the centre of this dialog, so it opens larger (R-im5-3).
                Width = 1320;
                Height = 880;
                MinWidth = 980;
                MinHeight = 640;
                Report.StripMaxHeight = 96;   // the canvas is what this dialog is for
                image.PickPictureAsync = () => PickOpen("Picture", new FilePickerFileType("Pictures")
                {
                    Patterns = [.. RasterImage.Extensions.Select(e => "*" + e)],
                });
                image.PastePictureAsync = ReadClipboardPicture;
                AddHandler(DragDrop.DragOverEvent, OnDragOver);
                AddHandler(DragDrop.DropEvent, OnDrop);
                AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Tunnel);
                break;
        }
        Closed += (_, _) => vm.Close();
    }

    private async Task<string?> PickOpen(string title, FilePickerFileType? type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{_vm?.Title} — {title}",
            AllowMultiple = false,
            FileTypeFilter = type is null
                ? [new FilePickerFileType("All Files") { Patterns = ["*.*"] }]
                : [type, new FilePickerFileType("All Files") { Patterns = ["*.*"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickExport()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"{_vm?.Title} — Export parts table",
            SuggestedFileName = "parts.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        return file?.TryGetLocalPath();
    }

    // ── a picture: the clipboard and a drop (R-im5-2) ───────────────────────────────────────────────

    /// <summary>The clipboard's picture: a bitmap, encoded to PNG, or a copied picture file read as itself. Null when it
    /// holds neither; a failed read is the same as none, never an exception.</summary>
    private async Task<ImageSourceResult?> ReadClipboardPicture()
    {
        if (Clipboard is not { } clipboard) return null;
        try
        {
            if (await clipboard.TryGetFileAsync() is { } file && file.TryGetLocalPath() is { } path && File.Exists(path))
                return ImageSource.FromFile(path);
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using var png = new MemoryStream();
                bitmap.Save(png);
                return ImageSource.FromBytes(png.ToArray(), null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>Cmd/Ctrl+V reads the clipboard's picture — unless a text box has the keyboard, where it pastes text.</summary>
    private void OnPasteKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (_vm is ImageSourceViewModel image) image.PasteCommand.Execute(null);
        e.Handled = true;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ImageReadingPanel.DroppedPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is ImageSourceViewModel image && ImageReadingPanel.DroppedPath(e) is { } path) image.LoadFile(path);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
