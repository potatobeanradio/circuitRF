using System.Diagnostics;
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
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Custom Avalonia control that renders a SchematicModel via SkiaSharp.
///
/// Render path: Control.Render → ICustomDrawOperation → ISkiaSharpApiLease → SchematicRenderer.
/// This is the officially recommended Avalonia 11+ approach (same as splotRF's PlotControl).
/// NOT SKCanvasView.
///
/// Interaction:
///   Left-drag  → pan
///   Scroll     → zoom centred on cursor world position
///   ZoomToFit  → fits the schematic bounds to the canvas (called by toolbar button)
///
/// Performance: the model is immutable (6c read-only). The spatial index is built once on
/// model assignment. The draw operation captures a snapshot of pan/zoom on the UI thread
/// and renders on the compositor thread.
/// </summary>
public sealed class SchematicCanvas : Control
{
    // ── DirectProperty: Model ────────────────────────────────────────────────

    public static readonly DirectProperty<SchematicCanvas, SchematicModel?> ModelProperty =
        AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicModel?>(
            nameof(Model), o => o.Model, (o, v) => o.Model = v);

    private SchematicModel? _model;

    public SchematicModel? Model
    {
        get => _model;
        set
        {
            SetAndRaise(ModelProperty, ref _model, value);
            _index          = value is not null ? new SchematicSpatialIndex(value) : null;
            _needsInitialFit = value is not null;
            InvalidateVisual();
        }
    }

    // ── DirectProperty: ShowFps ──────────────────────────────────────────────

    public static readonly DirectProperty<SchematicCanvas, bool> ShowFpsProperty =
        AvaloniaProperty.RegisterDirect<SchematicCanvas, bool>(
            nameof(ShowFps), o => o.ShowFps, (o, v) => o.ShowFps = v);

    private bool _showFps = true;

    public bool ShowFps
    {
        get => _showFps;
        set { SetAndRaise(ShowFpsProperty, ref _showFps, value); InvalidateVisual(); }
    }

    // ── Viewport state ───────────────────────────────────────────────────────

    private double _panX;         // world X at left edge of canvas
    private double _panY;         // world Y at top edge of canvas
    private double _zoom = 1.0;   // pixels per world unit

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 0.0005;
    private const double MaxZoom    = 50.0;

    // ── Drag state ───────────────────────────────────────────────────────────

    private bool   _isDragging;
    private Point  _dragStartScreen;
    private double _dragStartPanX;
    private double _dragStartPanY;

    // ── Spatial index (rebuilt when model changes) ───────────────────────────

    private SchematicSpatialIndex? _index;

    // ── Initial-fit flag ─────────────────────────────────────────────────────

    private bool _needsInitialFit;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SchematicCanvas()
    {
        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;

        // Invalidate on theme/resource changes (dark ↔ light switch)
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();

        // Fit on first layout
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _model is not null &&
            Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            LayoutUpdated -= OnLayoutUpdated;
            ZoomToFitInternal(Bounds.Width, Bounds.Height);
            InvalidateVisual();
        }
    }

    // ── Visual tree attach / detach ───────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is TopLevel tl)
            tl.SizeChanged += OnTopLevelSizeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is TopLevel tl)
            tl.SizeChanged -= OnTopLevelSizeChanged;
    }

    private void OnTopLevelSizeChanged(object? s, SizeChangedEventArgs e) => InvalidateVisual();

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        var  theme  = isDark ? SchematicRenderTheme.Dark : SchematicRenderTheme.Light;
        long prevTicks = Volatile.Read(ref SchematicRenderer.LastFrameTicks);

        context.Custom(new SchematicDrawOperation(
            new Rect(Bounds.Size), _model, _index,
            _panX, _panY, _zoom, theme, prevTicks, _showFps));
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Fits the schematic bounding box to the current canvas area with padding.</summary>
    public void ZoomToFit()
    {
        ZoomToFitInternal(Bounds.Width, Bounds.Height);
        InvalidateVisual();
    }

    private void ZoomToFitInternal(double canvasW, double canvasH)
    {
        if (_model is null || canvasW < 1 || canvasH < 1) return;

        double worldW = _model.BbMaxX - _model.BbMinX;
        double worldH = _model.BbMaxY - _model.BbMinY;
        if (worldW < 1) worldW = 1;
        if (worldH < 1) worldH = 1;

        const double pad = 0.05; // 5% padding on each side
        _zoom = Math.Min(canvasW / worldW, canvasH / worldH) * (1.0 - 2 * pad);
        _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom);

        // Center the schematic
        double scaledW = worldW * _zoom;
        double scaledH = worldH * _zoom;
        _panX = _model.BbMinX - (canvasW - scaledW) / (2 * _zoom);
        _panY = _model.BbMinY - (canvasH - scaledH) / (2 * _zoom);
    }

    /// <summary>Resets to zoom = 1 and pan = (0, 0).</summary>
    public void ZoomToPage()
    {
        _panX = 0; _panY = 0; _zoom = 1.0;
        InvalidateVisual();
    }

    // ── Pointer — press ───────────────────────────────────────────────────────

    private void OnPointerPressed(object? _, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging       = true;
            _dragStartScreen  = e.GetPosition(this);
            _dragStartPanX    = _panX;
            _dragStartPanY    = _panY;
            e.Pointer.Capture(this);
        }
    }

    // ── Pointer — move (pan) ─────────────────────────────────────────────────

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        _panX = _dragStartPanX - (pos.X - _dragStartScreen.X) / _zoom;
        _panY = _dragStartPanY - (pos.Y - _dragStartScreen.Y) / _zoom;
        InvalidateVisual();
    }

    // ── Pointer — release ────────────────────────────────────────────────────

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }
    }

    // ── Scroll-wheel zoom (cursor-centred) ───────────────────────────────────

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);

        // World point under cursor before zoom
        double wx = pos.X / _zoom + _panX;
        double wy = pos.Y / _zoom + _panY;

        double factor = e.Delta.Y > 0 ? 1.0 / ZoomFactor : ZoomFactor;
        _zoom = Math.Clamp(_zoom / factor, MinZoom, MaxZoom);

        // Keep world point under cursor fixed
        _panX = wx - pos.X / _zoom;
        _panY = wy - pos.Y / _zoom;

        e.Handled = true;
        InvalidateVisual();
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class SchematicDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                    _bounds;
        private readonly SchematicModel?         _model;
        private readonly SchematicSpatialIndex?  _index;
        private readonly double                  _panX, _panY, _zoom;
        private readonly SchematicRenderTheme    _theme;
        private readonly long                    _prevTicks;
        private readonly bool                    _showFps;

        public SchematicDrawOperation(
            Rect bounds, SchematicModel? model, SchematicSpatialIndex? index,
            double panX, double panY, double zoom,
            SchematicRenderTheme theme, long prevTicks, bool showFps)
        {
            _bounds    = bounds;
            _model     = model;
            _index     = index;
            _panX      = panX;  _panY  = panY;  _zoom  = zoom;
            _theme     = theme;
            _prevTicks = prevTicks;
            _showFps   = showFps;
        }

        // Always return false — forces a fresh draw each time InvalidateVisual is called.
        public bool Equals(ICustomDrawOperation? other) => false;

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas      = lease.SkCanvas;

            SchematicRenderer.Draw(
                canvas,
                (_bounds.Width, _bounds.Height),
                _model, _index,
                _panX, _panY, _zoom,
                _theme,
                _prevTicks,
                _showFps);
        }

        public void Dispose() { }
    }
}
