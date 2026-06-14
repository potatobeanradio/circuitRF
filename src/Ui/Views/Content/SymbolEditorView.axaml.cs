using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Content;

public partial class SymbolEditorView : UserControl
{
    // ── Port-count refresh on activation ─────────────────────────────────────────
    // Mirrors ProjectTreeView's on-focus refresh pattern.
    // Two triggers cover all three scenarios:
    //   1. Tab-switch in the main window → WorkspaceViewModel.OnDocumentDockPropertyChanged (always first)
    //   2. App window / torn-off host window gains OS focus → OnHostWindowActivated
    //   3. User clicks from a tool panel back to the canvas (same window) → OnViewGotFocus

    private Window? _attachedWindow;
    private bool    _portRefreshPending;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_attachedWindow is not null)
            _attachedWindow.Activated += OnHostWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attachedWindow is not null)
        {
            _attachedWindow.Activated -= OnHostWindowActivated;
            _attachedWindow = null;
        }
    }

    // Window (main or torn-off host) gains OS focus.
    private void OnHostWindowActivated(object? sender, EventArgs e) => SchedulePortRefresh();

    // Focus enters this view from a sibling panel (Properties, Project Tree, etc.).
    private void OnViewGotFocus(object? sender, RoutedEventArgs e) => SchedulePortRefresh();

    // Debounced port-count re-read.  SetExternalPortCount has its own no-op guard so
    // extra calls are cheap; the flag prevents redundant Background-queue entries.
    private void SchedulePortRefresh()
    {
        if (_portRefreshPending) return;
        _portRefreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _portRefreshPending = false;
            (DataContext as SymbolEditorDocument)?.ViewModel.RefreshPortCountFromDisk();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Inline text edit state ────────────────────────────────────────────────────

    private SymbolEditorViewModel? _subscribedVm;
    private SymbolEditorViewModel.TextEditRequest? _editReq;

    // ── Constructor ───────────────────────────────────────────────────────────────

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

        // Bubble GotFocus: fires when focus enters this view from outside (tool panel → canvas).
        // Debounced via _portRefreshPending so rapid intra-view focus moves coalesce to one read.
        this.AddHandler(
            InputElement.GotFocusEvent,
            OnViewGotFocus,
            RoutingStrategies.Bubble);

        // Clipboard shortcuts (async; handled here, not in the canvas — mirrors SchematicView).
        SymbolEditorCanvasCtrl.ClipboardCopyRequested  += async (_, _) => await OnClipboardCopy();
        SymbolEditorCanvasCtrl.ClipboardCutRequested   += async (_, _) => await OnClipboardCut();
        SymbolEditorCanvasCtrl.ClipboardPasteRequested += async (_, _) => await OnClipboardPaste();

        // Inline text edit — subscribe to VM events and viewport changes.
        DataContextChanged += OnDataContextChanged;
        SymbolEditorCanvasCtrl.ViewportChanged += (_, _) => RepositionInlineEditBox();
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e)
        => SymbolEditorCanvasCtrl.ZoomToFit();

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (InlineEditBox.IsKeyboardFocusWithin) return;   // inline box owns its own Enter/Esc
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

    // ── Inline text edit ──────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null) _subscribedVm.TextEditRequested -= OnTextEditRequested;
        _subscribedVm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (_subscribedVm is not null) _subscribedVm.TextEditRequested += OnTextEditRequested;
    }

    private void OnTextEditRequested(SymbolEditorViewModel.TextEditRequest req)
    {
        _editReq = req;
        double zoom = SymbolEditorCanvasCtrl.CurrentZoom;
        InlineEditBox.FontSize = Math.Max(zoom * req.FontSize, 9.0);
        var (sx, sy) = SymbolEditorCanvasCtrl.WorldToScreen(req.WorldX, req.WorldY);
        InlineEditBox.Margin = new Thickness(sx - 4, sy - 2, 0, 0);
        InlineEditBox.Text   = req.Content;
        InlineEditBox.IsVisible = true;
        Dispatcher.UIThread.Post(
            () => { InlineEditBox.Focus(); InlineEditBox.SelectAll(); },
            DispatcherPriority.Input);
    }

    private void RepositionInlineEditBox()
    {
        if (!InlineEditBox.IsVisible || _editReq is not { } req) return;
        double zoom = SymbolEditorCanvasCtrl.CurrentZoom;
        InlineEditBox.FontSize = Math.Max(zoom * req.FontSize, 9.0);
        var (sx, sy) = SymbolEditorCanvasCtrl.WorldToScreen(req.WorldX, req.WorldY);
        InlineEditBox.Margin = new Thickness(sx - 4, sy - 2, 0, 0);
    }

    private void OnInlineEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitInlineEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape)         { DismissInlineEdit(); e.Handled = true; }
    }

    private void OnInlineEditLostFocus(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (InlineEditBox.IsVisible && !InlineEditBox.IsKeyboardFocusWithin) CommitInlineEdit();
        }, DispatcherPriority.Background);

    private void CommitInlineEdit()
    {
        if (_editReq is not { } req) { DismissInlineEdit(); return; }
        string text = InlineEditBox.Text ?? "";
        DismissInlineEdit();
        (DataContext as SymbolEditorDocument)?.ViewModel.CommitTextEdit(req.Index, text);
    }

    private void DismissInlineEdit()
    {
        _editReq = null;
        InlineEditBox.IsVisible = false;
        SymbolEditorCanvasCtrl.Focus();
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
