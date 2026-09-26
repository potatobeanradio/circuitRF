using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// brief-em3d-28 — the 3D view's shell. Wires the pane's frames to the overlay's redraw, shows a
/// present fault in the pane's place, scrolls the object tree to what a click selected, and follows the
/// application theme: a light/dark switch or a new theme regenerates the scene, because colours are
/// baked into the vertices (a regeneration, not a per-frame cost).
/// </summary>
public partial class Viewer3DView : UserControl
{
    private Viewer3DViewModel? _vm;
    private readonly ContextMenu _menu = new();

    public Viewer3DView()
    {
        InitializeComponent();
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        Pane.ContextMenuRequested += () => _menu.Open(Pane);
        var copy = new MenuItem { Header = $"Copy Picture ({Viewer3DViewModel.CopyScale}× the window)" };
        copy.Click += OnCopyPicture;
        var export = new MenuItem { Header = "Export Picture…" };
        export.Click += OnExportPicture;
        _menu.Items.Add(copy);
        _menu.Items.Add(export);
        Pane.FaultChanged += why =>
        {
            FaultText.Text = why is null ? "" : "The 3D view cannot draw here: " + why;
            FaultText.IsVisible = why is not null;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RevealRequested -= Reveal;
        _vm = (DataContext as Viewer3DDocument)?.ViewModel;
        if (_vm is not null)
        {
            _vm.RevealRequested += Reveal;
            ApplyBackground();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        ActualThemeVariantChanged += OnVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeService.ThemeChanged -= OnThemeChanged;
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _vm?.Invalidate();

    private void OnVariantChanged(object? sender, EventArgs e)
    {
        ApplyBackground();
        _vm?.Invalidate();
    }

    private void ApplyBackground()
    {
        if (_vm is null) return;
        _vm.View.Background = ThemeService.CurrentVariant == ColorVariant.Dark ? (0.12f, 0.13f, 0.15f) : (0.93f, 0.94f, 0.96f);
    }

    /// <summary>The pane's size in DEVICE pixels — what a picture's multiple is of.</summary>
    private (int W, int H) PanePixels()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return ((int)Math.Ceiling(Pane.Bounds.Width * scale), (int)Math.Ceiling(Pane.Bounds.Height * scale));
    }

    /// <summary>
    /// The context menu's Copy: the view drawn offscreen by the GPU at 4× the pane's device pixels, read
    /// back (Export picture's own path), the legend and caption painted on as the export options say, and
    /// put on the clipboard as an image. The composing runs off the UI thread; only the read-back holds it.
    /// </summary>
    private async void OnCopyPicture(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is null) return;
        var (w, h) = PanePixels();
        var shot = _vm.CapturePicture(w, h, Viewer3DViewModel.CopyScale, out string? error);
        if (shot is null) { _vm.PictureText = "The picture could not be copied: " + error; return; }
        _vm.PictureText = "Copying the picture…";
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var pixels = shot.Compose();
                return Clipboard.ImageClipboard.FromSkia(pixels);
            });
            bool done = await Clipboard.ImageClipboard.SetAsync(this, bitmap);
            _vm.PictureText = done
                ? $"Copied the view at {shot.Width:N0} × {shot.Height:N0} pixels" +
                  (shot.Scale < Viewer3DViewModel.CopyScale ? $" ({shot.Scale:0.##}× the window: a picture's side is at most {FieldPicture.MaxSide:N0} pixels)." : ".")
                : "The picture could not be copied: this window has no clipboard.";
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            _vm.PictureText = "The picture could not be copied: " + ex.Message;
        }
    }

    /// <summary>
    /// brief-em3d-29 R-em3d29-5 — Export picture…: the view drawn offscreen by the GPU at the chosen multiple
    /// of the pane's DEVICE-pixel size, read back, and written as PNG where the user says.
    /// </summary>
    private async void OnExportPicture(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var (w, h) = PanePixels();
        var png = _vm.ExportPng(w, h, out string? error);
        if (png is null) { _vm.PictureText = "The picture could not be made: " + error; return; }
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Picture",
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_vm.CemPath) + "-3d.png",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        _vm.PictureText = $"Exported the view as {file.Name}.";
    }

    /// <summary>ItemsControl.ScrollIntoView resolves an item among the TOP-level items only, and a leaf
    /// sits under a group whose children are not realized until it expands — so the group is expanded
    /// first and the leaf's container brought into view once layout has made it.</summary>
    private void Reveal(Viewer3DTreeItem item)
    {
        foreach (var c in ObjectTree.GetRealizedContainers())
            if (c is TreeViewItem group && group.DataContext is Viewer3DTreeGroup g && g.Items.Contains(item))
            {
                group.IsExpanded = true;
                Dispatcher.UIThread.Post(() => group.ContainerFromItem(item)?.BringIntoView(), DispatcherPriority.Loaded);
                return;
            }
    }
}
