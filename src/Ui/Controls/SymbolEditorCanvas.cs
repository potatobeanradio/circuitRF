using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
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

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 0.02;
    private const double MaxZoom    = 50.0;

    // ── Pan state ─────────────────────────────────────────────────────────────

    private bool   _isPanning;
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
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _renderSymbol is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            LayoutUpdated -= OnLayoutUpdated;
            ZoomToFitInternal();
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
        InvalidateVisual();
    }

    private void ZoomToFitInternal()
    {
        if (_renderSymbol is null || Bounds.Width < 1 || Bounds.Height < 1) return;
        var (bbMinX, bbMinY, bbMaxX, bbMaxY) = SymbolGeometry.ComputeBb(_renderSymbol.Primitives);

        // Fall back to a default view if the symbol has no geometric primitives.
        if (bbMinX >= bbMaxX || bbMinY >= bbMaxY)
        {
            bbMinX = -300; bbMinY = -300; bbMaxX = 300; bbMaxY = 300;
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

        if (!props.IsLeftButtonPressed) return;

        if (_viewModel is not null)
        {
            _viewModel.OnPointerPressed(
                ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), e.KeyModifiers, e.ClickCount);
            e.Pointer.Capture(this);
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
            InvalidateVisual();
            return;
        }

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        _viewModel?.OnPointerMoved(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), leftDown);
        InvalidateVisual();
    }

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            UpdateCursor();  // restore tool cursor (crosshair or default)
            return;
        }

        if (_viewModel is not null)
        {
            var pos = e.GetPosition(this);
            _viewModel.OnPointerReleased(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y));
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        // F key: zoom to fit.
        if (e.Key == Key.F && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            ZoomToFit(); e.Handled = true; return;
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
        InvalidateVisual();
        e.Handled = true;
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
