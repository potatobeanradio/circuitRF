using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Custom Avalonia control that renders a Symbol via the shared SchematicRenderer.DrawSymbol,
/// with pan/zoom, a fine-grid display, and a selection overlay.
/// Mirrors SchematicCanvas at symbol scale (no LOD / spatial index needed).
/// Middle mouse always pans; left mouse delegates to SymbolEditorViewModel.
/// </summary>
public sealed class SymbolEditorCanvas : Control
{
    // ── DirectProperty: ViewModel ────────────────────────────────────────────

    public static readonly DirectProperty<SymbolEditorCanvas, SymbolEditorViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<SymbolEditorCanvas, SymbolEditorViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private SymbolEditorViewModel? _viewModel;
    public SymbolEditorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnVmPropertyChanged;

            SetAndRaise(ViewModelProperty, ref _viewModel, value);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnVmPropertyChanged;
                _viewModel.CanvasZoom = _zoom;
                SyncFromVm();
                UpdateCursor();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SymbolEditorViewModel.RenderSymbol)
                           or nameof(SymbolEditorViewModel.Overlay))
            SyncFromVm();
        else if (e.PropertyName is nameof(SymbolEditorViewModel.ActiveTool)
                                or nameof(SymbolEditorViewModel.IsLocked))
            UpdateCursor();
    }

    private void SyncFromVm()
    {
        if (_viewModel is null) return;
        _renderSymbol = _viewModel.RenderSymbol;
        _overlay      = _viewModel.Overlay;
        _needsInitialFit = _renderSymbol is not null && _needsInitialFit;
        InvalidateVisual();
    }

    // Crosshair for every drawing / pin tool; arrow for Select and when locked.
    private void UpdateCursor()
    {
        if (_isPanning) return;  // don't override the hand cursor while panning
        bool useCross = _viewModel is { IsLocked: false } vm
                        && vm.ActiveTool != SymbolEditorViewModel.Tool.Select;
        Cursor = useCross ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
    }

    // ── Viewport state ────────────────────────────────────────────────────────

    private double _panX;
    private double _panY;
    private double _zoom = 1.0;

    private const double ZoomFactor          = 1.15;
    private const double MinZoom             = 0.02;
    private const double MaxZoom             = 100.0;
    private const double DefaultNewSymbolZoom = 2.0;   // 200 px per grid square for blank symbols

    // ── Bitmap context-menu target ────────────────────────────────────────────
    // Set on right-click by OnPointerPressed; read by SymbolEditorView.OnBitmapContextMenuOpening.
    // −1 means no bitmap under the pointer; Avalonia cancels the ContextMenu in that case.
    public int BitmapContextPrimIdx { get; private set; } = -1;

    // ── Viewport public API (used by the inline edit box to position itself) ────

    public double CurrentZoom => _zoom;

    public (double X, double Y) WorldToScreen(double wx, double wy)
        => ((wx - _panX) * _zoom, (wy - _panY) * _zoom);

    public event EventHandler? ViewportChanged;

    // ── Clipboard events (async work handled by SymbolEditorView code-behind) ────

    public event EventHandler? ClipboardCopyRequested;
    public event EventHandler? ClipboardCutRequested;
    public event EventHandler? ClipboardPasteRequested;

    // ── Pan state ─────────────────────────────────────────────────────────────

    private bool   _isPanning;

    // The pointer this canvas currently holds capture on, or null. Tracked — rather than relying on
    // the matching PointerReleased to arrive — because a VM pointer handler can open a MODAL window
    // while the press is still being processed, and the release that would have freed the capture then
    // goes to the modal instead. See ReleasePointerCapture.
    private IPointer? _capturedPointer;
    private Point  _panDragStartScreen;
    private double _panDragStartPanX;
    private double _panDragStartPanY;

    // ── Render state ──────────────────────────────────────────────────────────

    private Symbol?              _renderSymbol;
    private SymbolEditorOverlay  _overlay      = SymbolEditorOverlay.Empty;
    private ColorTheme           _activeTheme  = ColorTheme.BuiltIn;
    private bool                 _needsInitialFit = true;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SymbolEditorCanvas()
    {
        Focusable = true;
        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
        KeyDown             += OnKeyDown;
        TextInput           += OnTextInput;
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        LayoutUpdated += OnLayoutUpdated;

        // Image file drop target — accepts image files from the OS, ignores everything else.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnImageFileDragOver);
        AddHandler(DragDrop.DropEvent,     OnImageFileDrop);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _renderSymbol is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            LayoutUpdated -= OnLayoutUpdated;
            ZoomToFitInternal();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    // ── Visual tree ───────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        _activeTheme = ThemeService.Active;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _activeTheme = ThemeService.Active;
        InvalidateVisual();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme   = SchematicRenderTheme.FromTheme(_activeTheme, variant);

        if (Application.Current?.TryGetResource("SystemAccentColor", ActualThemeVariant, out var res) == true
            && res is Avalonia.Media.Color avColor)
        {
            var accent = new SKColor(avColor.R, avColor.G, avColor.B);
            theme = theme.WithAccent(accent);
        }

        context.Custom(new SymbolEditorDrawOperation(
            new Rect(Bounds.Size), _renderSymbol, _overlay,
            _panX, _panY, _zoom, theme));
    }

    // ── Zoom to fit ───────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void ZoomToFitInternal()
    {
        if (_renderSymbol is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(_renderSymbol.Primitives);
        // Include pins so they're framed too (and so a pins-only symbol isn't treated as blank).
        const double pinPad = 10.0;   // world units: pin dot + a little label room
        foreach (var p in _renderSymbol.Pins)
        {
            bbMinX = Math.Min(bbMinX, p.LocalX - pinPad);
            bbMinY = Math.Min(bbMinY, p.LocalY - pinPad);
            bbMaxX = Math.Max(bbMaxX, p.LocalX + pinPad);
            bbMaxY = Math.Max(bbMaxY, p.LocalY + pinPad);
        }

        // Blank/new symbol — use a larger default zoom centered at the origin so the
        // editing area is immediately comfortable (200 px per connection-grid square).
        if (bbMinX >= bbMaxX || bbMinY >= bbMaxY)
        {
            _zoom = DefaultNewSymbolZoom;
            _panX = -Bounds.Width  / (2.0 * _zoom);
            _panY = -Bounds.Height / (2.0 * _zoom);
            if (_viewModel is not null) _viewModel.CanvasZoom = _zoom;
            return;
        }

        const double pad = 0.1;
        double worldW = bbMaxX - bbMinX;
        double worldH = bbMaxY - bbMinY;
        _zoom = Math.Clamp(
            Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2 * pad),
            MinZoom, MaxZoom);
        double cx = (bbMinX + bbMaxX) * 0.5;
        double cy = (bbMinY + bbMaxY) * 0.5;
        _panX = cx - Bounds.Width  / (2.0 * _zoom);
        _panY = cy - Bounds.Height / (2.0 * _zoom);
        if (_viewModel is not null)
            _viewModel.CanvasZoom = _zoom;
    }

    // ── World ↔ screen ─────────────────────────────────────────────────────

    private double ScreenToWorldX(double sx) => sx / _zoom + _panX;
    private double ScreenToWorldY(double sy) => sy / _zoom + _panY;

    /// <summary>R-bmp-5's Insert Bitmap toolbar button — routes through the EXISTING
    /// <see cref="SymbolEditorViewModel.DropBitmap"/> with no new placement logic, exactly the way
    /// dragging a file onto the canvas already does; the only difference is the placement point is
    /// the current viewport centre instead of a drop position.</summary>
    public void InsertBitmapAtViewportCenter(string path)
        => _viewModel?.DropBitmap(path, ScreenToWorldX(Bounds.Width / 2.0), ScreenToWorldY(Bounds.Height / 2.0));

    // ── Pointer ───────────────────────────────────────────────────────────────

    private void OnPointerPressed(object? _, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos   = e.GetPosition(this);

        if (props.IsMiddleButtonPressed)
        {
            _isPanning          = true;
            _panDragStartScreen = pos;
            _panDragStartPanX   = _panX;
            _panDragStartPanY   = _panY;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        // Right click → hit-test for bitmap context menu; Avalonia opens ContextMenu on release.
        if (props.IsRightButtonPressed)
        {
            BitmapContextPrimIdx = -1;
            if (_viewModel is not null)
            {
                double wx = ScreenToWorldX(pos.X);
                double wy = ScreenToWorldY(pos.Y);
                var result = _viewModel.OnPointerRightPressed(wx, wy);
                if (result.HasValue) BitmapContextPrimIdx = result.Value.PrimIdx;
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (_viewModel is not null)
        {
            _viewModel.OnPointerPressed(
                ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), e.KeyModifiers, e.ClickCount);
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            InvalidateVisual();
        }
    }

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / _zoom;
            _panY = _panDragStartPanY - (pos.Y - _panDragStartScreen.Y) / _zoom;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        _viewModel?.OnPointerMoved(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), leftDown);
        InvalidateVisual();
    }

    /// <summary>
    /// Drops any pointer capture this canvas is holding, without waiting for the matching
    /// PointerReleased.
    ///
    /// <para><b>Why this is needed at all</b> (owner, 2026-08-17: "pressing Cancel in the Pin dialog
    /// doesn't close it; I am forced to press cancel twice"). <see cref="OnPointerPressed"/> calls into
    /// the view model FIRST and captures the pointer AFTER — and a VM handler can raise a request that
    /// the view answers by opening a modal window (the pin port-number dialog). By the time capture is
    /// taken the modal is already up, so the release that would have freed it is delivered to the modal
    /// instead and this canvas keeps the pointer for as long as the dialog lives. The dialog's first
    /// click is then spent breaking that capture rather than pressing the button under it — which reads
    /// exactly as a button that needs pressing twice.</para>
    ///
    /// <para>Called by the view immediately before it shows such a dialog. Harmless when there is
    /// nothing captured.</para>
    /// </summary>
    public void ReleasePointerCapture()
    {
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
    }

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            _capturedPointer = null;
            UpdateCursor();  // restore tool cursor (crosshair or default)
            return;
        }

        if (_viewModel is not null)
        {
            var pos = e.GetPosition(this);
            _viewModel.OnPointerReleased(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y));
            e.Pointer.Capture(null);
            _capturedPointer = null;
            InvalidateVisual();
        }
    }

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        // F key: zoom to fit — suppressed while typing text so 'f' reaches the text buffer.
        if (e.Key == Key.F
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0
            && _viewModel?.IsTypingText != true)
        {
            ZoomToFit(); e.Handled = true; return;
        }

        // Clipboard shortcuts — raised so code-behind can do the async clipboard work.
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl)
        {
            if (e.Key == Key.C) { ClipboardCopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
            if (e.Key == Key.X) { ClipboardCutRequested?.Invoke(this, EventArgs.Empty);  e.Handled = true; return; }
            if (e.Key == Key.V) { ClipboardPasteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        }

        _viewModel?.OnKeyDown(e.Key, e.KeyModifiers);
    }

    private void OnTextInput(object? _, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 })
            _viewModel?.OnTextInput(e.Text);
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        var pos    = e.GetPosition(this);
        double wx  = pos.X / _zoom + _panX;
        double wy  = pos.Y / _zoom + _panY;
        double fac = e.Delta.Y > 0 ? 1.0 / ZoomFactor : ZoomFactor;
        _zoom = Math.Clamp(_zoom / fac, MinZoom, MaxZoom);
        _panX = wx - pos.X / _zoom;
        _panY = wy - pos.Y / _zoom;
        if (_viewModel is not null) _viewModel.CanvasZoom = _zoom;
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    // ── Image file DnD ───────────────────────────────────────────────────────

    private void OnImageFileDragOver(object? _, DragEventArgs e)
    {
        DropDiagnostics.Dump("SymbolEditorCanvas.DragOver", e);
        if (TryExtractImagePath(e) is not null)
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void OnImageFileDrop(object? _, DragEventArgs e)
    {
        DropDiagnostics.Dump("SymbolEditorCanvas.Drop", e);
        var path = TryExtractImagePath(e);
        if (path is null || _viewModel is null) return;
        var pos = e.GetPosition(this);
        _viewModel.DropBitmap(path, ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y));
        e.Handled = true;
        InvalidateVisual();
    }

    // The OS surfaces a dropped file under DataFormat.File. The payload TYPE varies by platform:
    // macOS (Avalonia.Native) returns a SINGLE IStorageItem; other backends may return an
    // IEnumerable<IStorageItem>. Handle both, plus a bare path string, defensively.
    private static string? TryExtractImagePath(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);

            string? path = raw switch
            {
                IStorageItem single             => single.Path?.LocalPath,
                IEnumerable<IStorageItem> files => files.FirstOrDefault()?.Path?.LocalPath,
                string s                        => s,
                _                               => null,
            };

            if (path is not null && IsImageExtension(path)) return path;
        }
        return null;
    }

    private static bool IsImageExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class SymbolEditorDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                 _bounds;
        private readonly Symbol?              _symbol;
        private readonly SymbolEditorOverlay  _overlay;
        private readonly double               _panX, _panY, _zoom;
        private readonly SchematicRenderTheme _theme;

        public SymbolEditorDrawOperation(
            Rect bounds, Symbol? symbol, SymbolEditorOverlay overlay,
            double panX, double panY, double zoom, SchematicRenderTheme theme)
        {
            _bounds  = bounds; _symbol = symbol; _overlay = overlay;
            _panX    = panX;   _panY   = panY;   _zoom    = zoom;
            _theme   = theme;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect  Bounds   => _bounds;
        public bool  HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            SymbolEditorRenderer.Draw(
                lease.SkCanvas, (_bounds.Width, _bounds.Height),
                _symbol, _overlay, _panX, _panY, _zoom, _theme);
        }

        public void Dispose() { }
    }
}
