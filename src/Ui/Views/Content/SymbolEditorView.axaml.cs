using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Content;

public partial class SymbolEditorView : UserControl
{
    public SymbolEditorView()
    {
        InitializeComponent();

        // Focus-independent shortcut handler — mirrors SchematicView.OnViewKeyDownTunnel.
        // Toolbar button clicks steal focus; Window.KeyBindings mark Escape handled before
        // visual-tree routing, so a plain OnKeyDown override never fires.
        this.AddHandler(
            InputElement.KeyDownEvent,
            OnViewKeyDownTunnel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Clipboard shortcuts (async; handled here, not in the canvas — mirrors SchematicView).
        SymbolEditorCanvasCtrl.ClipboardCopyRequested  += async (_, _) => await OnClipboardCopy();
        SymbolEditorCanvasCtrl.ClipboardCutRequested   += async (_, _) => await OnClipboardCut();
        SymbolEditorCanvasCtrl.ClipboardPasteRequested += async (_, _) => await OnClipboardPaste();
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e)
        => SymbolEditorCanvasCtrl.ZoomToFit();

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;
        var vm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (vm is null) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl) return;

        // While the user is typing a text primitive, suppress all global shortcuts except
        // Escape (which commits/cancels) — characters must reach OnTextInput unobstructed.
        if (vm.IsTypingText && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.Escape:
                // Delegate to VM — handles text mode, pin mode, and general Esc correctly.
                vm.OnKeyDown(e.Key, e.KeyModifiers);
                e.Handled = true;
                break;
            case Key.S:
                vm.SetActiveToolCommand.Execute("Select");
                e.Handled = true;
                break;
            case Key.F:
                SymbolEditorCanvasCtrl.ZoomToFit();
                e.Handled = true;
                break;
        }
    }

    // ── Clipboard (Ctrl+C / Ctrl+X / Ctrl+V) ─────────────────────────────────

    private async Task OnClipboardCopy()
    {
        if (DataContext is not SymbolEditorDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await doc.ViewModel.ClipboardCopyAsync(clipboard);
    }

    private async Task OnClipboardCut()
    {
        if (DataContext is not SymbolEditorDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await doc.ViewModel.ClipboardCopyAsync(clipboard, cut: true);
    }

    private async Task OnClipboardPaste()
    {
        if (DataContext is not SymbolEditorDocument doc) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await doc.ViewModel.ClipboardPasteAsync(clipboard);
    }

    // ── Bitmap context menu ───────────────────────────────────────────────────

    private void OnBitmapContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (SymbolEditorCanvasCtrl.BitmapContextPrimIdx < 0)
            e.Cancel = true;
    }

    private async void OnCtxBitmapResolvePath(object? sender, RoutedEventArgs e)
    {
        var vm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (vm is null) return;
        int primIdx = SymbolEditorCanvasCtrl.BitmapContextPrimIdx;
        if (primIdx < 0) return;

        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Resolve Bitmap Path",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp" }
                }
            }
        });
        if (files.Count > 0)
            vm.ResolveBitmapPath(primIdx, files[0].Path.LocalPath);
    }

    private void OnCtxBitmapRefreshCache(object? sender, RoutedEventArgs e)
    {
        var vm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (vm is null) return;
        vm.RefreshBitmapCache(SymbolEditorCanvasCtrl.BitmapContextPrimIdx);
        SymbolEditorCanvasCtrl.InvalidateVisual();
    }
}
