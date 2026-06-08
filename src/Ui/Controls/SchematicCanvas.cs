using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

// ── Event arg types ────────────────────────────────────────────────────────────

public sealed class TextLabelHitArgs : EventArgs
{
    public SchematicHitTest.HitResult HitResult { get; }
    public double ScreenX { get; }
    public double ScreenY { get; }
    public TextLabelHitArgs(SchematicHitTest.HitResult hit, double sx, double sy)
        => (HitResult, ScreenX, ScreenY) = (hit, sx, sy);
}

public sealed class WireHitArgs : EventArgs
{
    public string WireId  { get; }
    public double WorldX  { get; }
    public double WorldY  { get; }
    public double ScreenX { get; }
    public double ScreenY { get; }
    public WireHitArgs(string id, double wx, double wy, double sx, double sy)
        => (WireId, WorldX, WorldY, ScreenX, ScreenY) = (id, wx, wy, sx, sy);
}

/// <summary>
/// Custom Avalonia control that renders a SchematicModel via SkiaSharp.
/// Middle mouse button always pans.
/// Left button delegates to the active tool in EditContext (select / wire / place).
/// Right click triggers the ContextMenu (hit-test result stored in ContextMenuTargetId).
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
            if (_editContext is null)
                _index = value is not null ? new SchematicSpatialIndex(value) : null;
            _needsInitialFit = value is not null;
            InvalidateVisual();
        }
    }

    // ── DirectProperty: EditContext ──────────────────────────────────────────

    public static readonly DirectProperty<SchematicCanvas, SchematicViewModel?> EditContextProperty =
        AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicViewModel?>(
            nameof(EditContext), o => o.EditContext, (o, v) => o.EditContext = v);

    private SchematicViewModel? _editContext;
    public SchematicViewModel? EditContext
    {
        get => _editContext;
        set
        {
            if (_editContext is not null)
            {
                _editContext.PropertyChanged -= OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = null;
            }

            SetAndRaise(EditContextProperty, ref _editContext, value);

            if (_editContext is not null)
            {
                _editContext.PropertyChanged += OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = ZoomToRect;
                SyncFromVm();
                UpdateCursor();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SchematicViewModel.RenderModel)
                           or nameof(SchematicViewModel.SpatialIndex)
                           or nameof(SchematicViewModel.Overlay))
            SyncFromVm();
        else if (e.PropertyName == nameof(SchematicViewModel.ActiveTool))
            UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_isLeftPanning || _isPanning)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }
        Cursor = _editContext?.ActiveTool switch
        {
            SchematicViewModel.Tool.Pan        => new Cursor(StandardCursorType.Hand),
            SchematicViewModel.Tool.Wire       => new Cursor(StandardCursorType.Cross),
            SchematicViewModel.Tool.ZoomBox    => new Cursor(StandardCursorType.Cross),
            SchematicViewModel.Tool.MoveLabels => new Cursor(StandardCursorType.SizeAll),
            _                                  => Cursor.Default,
        };
    }

    private void SyncFromVm()
    {
        if (_editContext is null) return;
        _model   = _editContext.RenderModel;
        _index   = _editContext.SpatialIndex;
        _overlay = _editContext.Overlay;
        _needsInitialFit = _model is not null && _needsInitialFit;
        InvalidateVisual();
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

    private double _panX;
    private double _panY;
    private double _zoom = 1.0;

    /// <summary>Current zoom level (world units per logical pixel). Read by code-behind to size the inline edit box.</summary>
    public double CurrentZoom => _zoom;

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 0.0005;
    private const double MaxZoom    = 50.0;

    // ── Pan state — middle mouse only ─────────────────────────────────────────

    private bool   _isPanning;
    private Point  _panDragStartScreen;
    private double _panDragStartPanX;
    private double _panDragStartPanY;

    // Left-button Pan tool
    private bool   _isLeftPanning;

    // ── Context menu tracking ─────────────────────────────────────────────────

    /// <summary>ID of the component (or wire) that was right-clicked. Null if background.</summary>
    public string? ContextMenuTargetId { get; private set; }

    // ── Internal state ────────────────────────────────────────────────────────

    private SchematicSpatialIndex? _index;
    private SchematicOverlay?      _overlay;
    private bool _needsInitialFit;

    // ── Events (raised to code-behind) ────────────────────────────────────────

    public event EventHandler<EditableComponent>?  ComponentDoubleTapped;
    public event EventHandler<TextLabelHitArgs>?   TextLabelDoubleTapped;
    public event EventHandler<WireHitArgs>?        WireDoubleTapped;

    // Clipboard shortcuts — raised so code-behind can do the async clipboard work.
    public event EventHandler? ClipboardCopyRequested;
    public event EventHandler? ClipboardCutRequested;
    public event EventHandler? ClipboardPasteRequested;

    // Fired after any pan or zoom change so overlays (e.g. inline edit box) can reposition.
    public event EventHandler? ViewportChanged;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SchematicCanvas()
    {
        Focusable = true;

        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
        KeyDown             += OnKeyDown;
        DoubleTapped        += OnDoubleTapped;

        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _model is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            LayoutUpdated -= OnLayoutUpdated;
            ZoomToFitInternal(Bounds.Width, Bounds.Height);
            InvalidateVisual();
        }
    }

    // ── Visual tree ──────────────────────────────────────────────────────────

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

        // Apply system accent color to rubber-band
        if (Application.Current?.TryGetResource("SystemAccentColor", ActualThemeVariant, out var res) == true
            && res is Avalonia.Media.Color avColor)
        {
            var accent = new SKColor(avColor.R, avColor.G, avColor.B);
            theme = theme.WithAccent(accent);
        }

        long prevTicks = Volatile.Read(ref SchematicRenderer.LastFrameTicks);
        context.Custom(new SchematicDrawOperation(
            new Rect(Bounds.Size), _model, _index,
            _panX, _panY, _zoom, theme, prevTicks, _showFps, _overlay));
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal(Bounds.Width, Bounds.Height);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomToFitInternal(double canvasW, double canvasH)
    {
        if (_model is null || canvasW < 1 || canvasH < 1) return;
        double worldW = Math.Max(_model.BbMaxX - _model.BbMinX, 1);
        double worldH = Math.Max(_model.BbMaxY - _model.BbMinY, 1);
        const double pad = 0.05;
        _zoom = Math.Clamp(Math.Min(canvasW / worldW, canvasH / worldH) * (1.0 - 2 * pad), MinZoom, MaxZoom);
        double scaledW = worldW * _zoom;
        double scaledH = worldH * _zoom;
        _panX = _model.BbMinX - (canvasW  - scaledW) / (2 * _zoom);
        _panY = _model.BbMinY - (canvasH - scaledH) / (2 * _zoom);
    }

    public void ZoomToPage() { _panX = 0; _panY = 0; _zoom = 1.0; InvalidateVisual(); ViewportChanged?.Invoke(this, EventArgs.Empty); }

    private void ZoomToRect(double x0, double y0, double x1, double y1)
    {
        double worldW = Math.Abs(x1 - x0);
        double worldH = Math.Abs(y1 - y0);
        if (worldW < 1 || worldH < 1) return;
        const double pad = 0.05;
        _zoom = Math.Clamp(
            Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2.0 * pad),
            MinZoom, MaxZoom);
        double cx = (x0 + x1) / 2.0;
        double cy = (y0 + y1) / 2.0;
        _panX = cx - Bounds.Width  / (2.0 * _zoom);
        _panY = cy - Bounds.Height / (2.0 * _zoom);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── World ↔ screen ────────────────────────────────────────────────────────

    private double ScreenToWorldX(double sx) => sx / _zoom + _panX;
    private double ScreenToWorldY(double sy) => sy / _zoom + _panY;

    /// <summary>Converts a world position to canvas screen coordinates.</summary>
    public (double X, double Y) WorldToScreen(double wx, double wy)
        => ((wx - _panX) * _zoom, (wy - _panY) * _zoom);

    // ── Pointer — press ───────────────────────────────────────────────────────

    private void OnPointerPressed(object? _, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        // Middle mouse → pan (always, regardless of tool)
        if (props.IsMiddleButtonPressed)
        {
            _isPanning          = true;
            _panDragStartScreen = pos;
            _panDragStartPanX   = _panX;
            _panDragStartPanY   = _panY;
            e.Pointer.Capture(this);
            UpdateCursor();   // show hand cursor immediately (macOS needs explicit update after capture)
            return;
        }

        // Right mouse → hit test for context menu; Avalonia shows ContextMenu on release
        if (props.IsRightButtonPressed)
        {
            ContextMenuTargetId = null;
            if (_editContext is not null && _model is not null && _index is not null)
            {
                var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy);
                if (hit.Kind is SchematicHitTest.HitKind.Component
                    or SchematicHitTest.HitKind.ComponentType
                    or SchematicHitTest.HitKind.ComponentName
                    or SchematicHitTest.HitKind.ComponentParam)
                {
                    ContextMenuTargetId = hit.Id;
                    // Also select the right-clicked component if not already selected
                    _editContext.SelectIfUnselected(hit.Id);
                }
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (_editContext is not null)
        {
            if (_editContext.ActiveTool == SchematicViewModel.Tool.Pan)
            {
                _isLeftPanning      = true;
                _panDragStartScreen = pos;
                _panDragStartPanX   = _panX;
                _panDragStartPanY   = _panY;
                UpdateCursor();
                e.Pointer.Capture(this);
                return;
            }

            _editContext.OnPointerPressed(wx, wy, e.KeyModifiers, pos.X, pos.Y);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
        {
            _isLeftPanning      = true;
            _panDragStartScreen = pos;
            _panDragStartPanX   = _panX;
            _panDragStartPanY   = _panY;
            e.Pointer.Capture(this);
        }
    }

    // ── Pointer — move ────────────────────────────────────────────────────────

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        if (_isPanning || _isLeftPanning)
        {
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / _zoom;
            _panY = _panDragStartPanY - (pos.Y - _panDragStartScreen.Y) / _zoom;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (_editContext is not null)
        {
            _editContext.OnPointerMoved(wx, wy, leftDown, pos.X, pos.Y, e.KeyModifiers);
            InvalidateVisual();
        }
    }

    // ── Pointer — release ─────────────────────────────────────────────────────

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)     { _isPanning = false;     UpdateCursor(); e.Pointer.Capture(null); return; }
        if (_isLeftPanning) { _isLeftPanning = false; UpdateCursor(); e.Pointer.Capture(null); return; }

        if (_editContext is not null)
        {
            var pos = e.GetPosition(this);
            _editContext.OnPointerReleased(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y),
                                           e.KeyModifiers);
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        else
        {
            e.Pointer.Capture(null);
        }
    }

    // ── Double-tap ────────────────────────────────────────────────────────────

    private void OnDoubleTapped(object? _, TappedEventArgs e)
    {
        if (_editContext is null || _model is null || _index is null) return;
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        // A2: Double-click while drawing a wire finishes it and keeps the drawn segments.
        // The second PointerPressed has already added the endpoint via HandleWirePress.
        if (_editContext.ActiveTool == SchematicViewModel.Tool.Wire && _editContext.IsDrawingWire)
        {
            _editContext.FinishCurrentWire();
            _editContext.SetSelectTool();
            InvalidateVisual();
            return;
        }

        var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy);

        switch (hit.Kind)
        {
            case SchematicHitTest.HitKind.ComponentType:
            case SchematicHitTest.HitKind.ComponentName:
            case SchematicHitTest.HitKind.ComponentParam:
            {
                // Mirror DrawLabels positioning exactly so the edit box overlays the rendered text.
                // The hit result carries the geometric hitbox centre (for click detection), which
                // differs from the actual text render position — do not use WorldToScreen(hit.LabelWorldX/Y).
                var editComp = _editContext.EditModel.FindComponent(hit.Id);
                if (editComp is null) break;

                // SubIndex for ComponentParam is the full-list parameter index.
                // The visual row is 2 + count of shown params that precede it.
                int row;
                if (hit.Kind == SchematicHitTest.HitKind.ComponentType) row = 0;
                else if (hit.Kind == SchematicHitTest.HitKind.ComponentName) row = 1;
                else
                {
                    int dispIdx = 0;
                    for (int pi = 0; pi < editComp.Parameters.Count && pi < hit.SubIndex; pi++)
                    {
                        var pp = editComp.Parameters[pi];
                        if (pp.ShowOnSchematic && !string.IsNullOrEmpty(pp.Expression)) dispIdx++;
                    }
                    row = 2 + dispIdx;
                }

                var (oDx, oDy)  = editComp.GetLabelOffset(row);
                var (cpx, cpy)  = WorldToScreen(editComp.X, editComp.Y);
                double textSize = Math.Max(_zoom * 70, 4.0);        // matches renderer (no upper cap)
                double lx = cpx - Math.Min(_zoom * 155, 160.0) + oDx * _zoom;  // text left edge
                double ly = cpy + Math.Min(_zoom * 120, 150.0) + textSize
                            + row * (textSize + 2) + oDy * _zoom;  // Skia baseline

                // For parameter rows the rendered label is "<Name> = <Expression> <Unit>".
                // Offset lx past the "<Name> = " prefix so the edit box overlays only the
                // expression+unit value. Use Skia MeasureText for the exact pixel width —
                // the fixed-ratio estimate (chars × 0.55) is inaccurate for IBM Plex Sans and
                // causes a zoom-proportional error.
                if (hit.Kind == SchematicHitTest.HitKind.ComponentParam
                    && hit.SubIndex < editComp.Parameters.Count)
                {
                    var p = editComp.Parameters[hit.SubIndex];
                    if (!string.IsNullOrEmpty(p.Name))
                    {
                        string prefix = $"{p.Name} = ";
                        using var mf = new SKFont(SkiaFonts.PlexRegular, (float)textSize);
                        lx += mf.MeasureText(prefix);
                    }
                }

                TextLabelDoubleTapped?.Invoke(this, new TextLabelHitArgs(hit, lx, ly));
                break;
            }
            case SchematicHitTest.HitKind.Component:
            {
                var comp = _editContext.EditModel.FindComponent(hit.Id);
                if (comp is not null)
                    ComponentDoubleTapped?.Invoke(this, comp);
                break;
            }
            case SchematicHitTest.HitKind.Wire:
            case SchematicHitTest.HitKind.WireSegment:
            case SchematicHitTest.HitKind.WireEndpoint:
            {
                // Determine offset direction based on wire orientation at click point
                var wire = _editContext.EditModel.FindWire(hit.Id);
                double labelWx = wx, labelWy = wy;
                if (wire is { Points.Count: >= 2 })
                {
                    // Find which segment was clicked to determine orientation
                    bool horizontal = IsHorizontalAt(wire, wx, wy);
                    if (horizontal)
                        labelWy -= 50;  // offset above horizontal segment
                    else
                        labelWx += 50;  // offset right of vertical segment
                }
                // WorldX/Y = net-label world placement; ScreenX/Y = actual click position for TextBox centering
                WireDoubleTapped?.Invoke(this, new WireHitArgs(hit.Id, labelWx, labelWy, pos.X, pos.Y));
                break;
            }
        }
    }

    private static bool IsHorizontalAt(EditableWire wire, double wx, double wy)
    {
        const double tol = 8.0;
        var pts = wire.Points;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (!SchematicGeometry.PointOnSegment(wx, wy, pts[i].X, pts[i].Y, pts[i+1].X, pts[i+1].Y, tol))
                continue;
            return Math.Abs(pts[i+1].Y - pts[i].Y) < tol;
        }
        return true;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        if (_editContext is null) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        // Clipboard shortcuts — handled async by code-behind.
        if (ctrl && e.Key == Key.C) { ClipboardCopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { ClipboardCutRequested?.Invoke(this, EventArgs.Empty);  e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { ClipboardPasteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }

        // F5 — Move Labels (invoked synchronously; BeginMoveLabels snapshots selection state).
        if (e.Key == Key.F5)
        {
            _editContext.BeginMoveLabels();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _editContext.OnKeyDown(e.Key, e.KeyModifiers);
        InvalidateVisual();
    }

    // ── Scroll-wheel zoom ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies a scroll-wheel zoom step at a given canvas-local position.
    /// Called by the canvas's own wheel handler and by overlays (e.g. inline edit box)
    /// that need to forward wheel events so zoom works even when the mouse is over them.
    /// </summary>
    public void ZoomAtPoint(Point canvasPos, double deltaY)
    {
        double wx = canvasPos.X / _zoom + _panX;
        double wy = canvasPos.Y / _zoom + _panY;
        double factor = deltaY > 0 ? 1.0 / ZoomFactor : ZoomFactor;
        _zoom = Math.Clamp(_zoom / factor, MinZoom, MaxZoom);
        _panX = wx - canvasPos.X / _zoom;
        _panY = wy - canvasPos.Y / _zoom;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        ZoomAtPoint(e.GetPosition(this), e.Delta.Y);
        e.Handled = true;
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
        private readonly SchematicOverlay?       _overlay;

        public SchematicDrawOperation(
            Rect bounds, SchematicModel? model, SchematicSpatialIndex? index,
            double panX, double panY, double zoom,
            SchematicRenderTheme theme, long prevTicks, bool showFps, SchematicOverlay? overlay)
        {
            _bounds    = bounds; _model = model; _index = index;
            _panX = panX; _panY = panY; _zoom = zoom;
            _theme = theme; _prevTicks = prevTicks; _showFps = showFps; _overlay = overlay;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            SchematicRenderer.Draw(
                lease.SkCanvas, (_bounds.Width, _bounds.Height),
                _model, _index, _panX, _panY, _zoom, _theme,
                _prevTicks, _showFps, _overlay);
        }

        public void Dispose() { }
    }
}
