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
                image.SavePlotFileAsync = PickPlotOutput;
                AddHandler(DragDrop.DragOverEvent, OnDragOver);
                AddHandler(DragDrop.DropEvent, OnDrop);
                AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Tunnel);
                AddHandler(KeyDownEvent, OnFitKey, RoutingStrategies.Tunnel);
                break;
        }
        FitToScreen();
        Closed += (_, _) => vm.Close();
    }

    /// <summary>
    /// Designer round 28 — the picture dialog opens 880 tall, and on a laptop's working area its Create button sat under
    /// the Windows taskbar. Shrunk to the screen as the workspace window is (<see cref="Views.WorkspaceWindowPlacement.Fit"/>);
    /// the minimum gives way too, since a minimum taller than the screen is the same cut-off button.
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            if ((Screens?.ScreenFromWindow(this) ?? Screens?.Primary) is not { } screen) return;
            double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0, margin = Views.WorkspaceWindowPlacement.EdgeMargin;
            MinWidth  = Math.Min(MinWidth,  screen.WorkingArea.Width  / scaling - margin);
            MinHeight = Math.Min(MinHeight, screen.WorkingArea.Height / scaling - margin);
            (Width, Height) = Views.WorkspaceWindowPlacement.Fit(Width, Height, screen.WorkingArea.Width, screen.WorkingArea.Height,
                                                                screen.Scaling, MinWidth, MinHeight);
        }
        catch
        {
            // No usable display information (a headless host): the declared size stands.
        }
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
            SuggestedFileName = "parts",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Create, for a plot (brief-img-14 R-im14-12): where the Touchstone or the CSV goes — the workspace folder,
    /// named after the first picture, by default.</summary>
    private async Task<string?> PickPlotOutput(string suggested, string folder)
    {
        string ext = Path.GetExtension(suggested).TrimStart('.');
        var start = await StorageProvider.TryGetReachableFolderAsync(folder);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _vm?.Title,
            SuggestedFileName = suggested,
            SuggestedStartLocation = start,
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(ext == "csv" ? "CSV" : "Touchstone") { Patterns = [$"*.{ext}"] }],
        });
        return file?.TryGetLocalPath();
    }

    // ── a picture: the clipboard and a drop (R-im5-2) ───────────────────────────────────────────────

    /// <summary>The clipboard's picture, read as Edit ▸ Paste Image as … reads it (<see cref="CircuitRF.Ui.Clipboard.ImageClipboard.TryReadAsync"/>).
    /// A failed read is the same as none, never an exception.</summary>
    private async Task<ImageSourceResult?> ReadClipboardPicture()
    {
        try { return await CircuitRF.Ui.Clipboard.ImageClipboard.TryReadAsync(this); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Cmd/Ctrl+V reads the clipboard's picture — unless a text box has the keyboard, where it pastes text.</summary>
    private void OnPasteKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (_vm is ImageSourceViewModel image) image.PasteCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>F fits the picture to the canvas — unless a text box has the keyboard, where it types an F.</summary>
    private void OnFitKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (_vm is not ImageSourceViewModel { HasPicture: true } image) return;
        image.FitPictureCommand.Execute(null);
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

    /// <summary>Help opens the reference on what is on screen (brief-ptu-2 R-ptu2-3): a picture's section of Create from
    /// Image, or the artwork flow's own page.</summary>
    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is ImageSourceViewModel image) DocLauncher.Open("reference/create-from-image.html", image.CurrentHelpAnchor);
        else DocLauncher.Open("reference/artwork-to-schematic.html");
    }

    /// <summary>Cancel stops a Create in flight and leaves the dialog open (R-dfr22-2-7); otherwise it closes it.</summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is { IsCreating: true } vm) vm.CancelRunCommand.Execute(null);
        else Close();
    }
}
