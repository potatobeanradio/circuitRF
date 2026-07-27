using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Custom Avalonia control that renders a <see cref="LayoutView"/> via <see cref="LayoutRenderer"/>
/// and dispatches pointer/keyboard/text input to <see cref="LayoutEditorViewModel"/>'s drawing-tool
/// state machine (docs/sonnet-briefs/brief-L1b-drawing-tools.md — fills L1a's marked seam). Clones
/// <c>SymbolEditorCanvas</c>'s shape: viewport state (<c>_panX</c>/<c>_panY</c>/<c>_zoom</c>) is
/// owned by the canvas, mirrored out via <see cref="ViewportChanged"/> for readouts (metadata bar,
/// rulers); a single <see cref="ViewModel"/> DirectProperty (not separate Model/Technology
/// properties, as in L1a) so the canvas can dispatch to it directly. Layout is Y-UP (see
/// <see cref="LayoutViewport"/>), unlike the schematic/symbol canvases.
/// </summary>
public sealed class LayoutCanvas : Control
{
    // ── DirectProperty: ViewModel ─────────────────────────────────────────────

    public static readonly DirectProperty<LayoutCanvas, LayoutEditorViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<LayoutCanvas, LayoutEditorViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private LayoutEditorViewModel? _viewModel;
    public LayoutEditorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnVmPropertyChanged;
                _viewModel.Model.Changed   -= OnModelChanged;
            }

            SetAndRaise(ViewModelProperty, ref _viewModel, value);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnVmPropertyChanged;
                _viewModel.Model.Changed   += OnModelChanged;   // Model itself never changes post-construction
                _needsInitialFit = true;
            }
            UpdateCursor();
            InvalidateVisual();
        }
    }

    private void OnModelChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutEditorViewModel.ActiveTool))
            UpdateCursor();
        InvalidateVisual();
    }

    // ── Viewport state — owned by the canvas, mirrored out for readouts ────────

    private double _panX;
    private double _panY;
    private double _zoom = 1.0;

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 1e-9;
    private const double MaxZoom    = 1e5;

    public double CurrentZoom => _zoom;
    public double CurrentPanX => _panX;
    public double CurrentPanY => _panY;

    public (double X, double Y) WorldToScreen(double wx, double wy) => (CurrentViewport.WorldToScreenX(wx), CurrentViewport.WorldToScreenY(wy));
    public (double X, double Y) ScreenToWorld(double sx, double sy) => (CurrentViewport.ScreenToWorldX(sx), CurrentViewport.ScreenToWorldY(sy));

    private LayoutViewport CurrentViewport => new(_panX, _panY, _zoom, Bounds.Width, Bounds.Height);

    /// <summary>Fired whenever pan/zoom changes — the view uses this to refresh rulers and the metadata bar.</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>Fired on pointer move (world coordinate) and on pointer exit (null) — drives the
    /// ruler cursor indicator and the metadata-bar X/Y readout (§1 R6).</summary>
    public event EventHandler<(double X, double Y)?>? CursorWorldChanged;

    /// <summary>
    /// Fired after each frame with any <see cref="LayerKey"/>s a resolved <see cref="Technology"/>
    /// did not define. May be empty. The view/view-model dedupes against what has already been
    /// warned about for this document and posts to Messages "once per layer per load" — the canvas
    /// itself never posts.
    /// </summary>
    public event Action<IReadOnlyList<LayerKey>>? FrameUnknownLayers;

    // ── Pan (middle-mouse always; Space-drag as an alternative) ─────────────────

    private bool   _isPanning;
    private Point  _panDragStartScreen;
    private double _panDragStartPanX;
    private double _panDragStartPanY;
    private bool   _spaceHeld;

    // ── Render state ─────────────────────────────────────────────────────────

    private ColorTheme _activeTheme     = ColorTheme.BuiltIn;
    private bool        _needsInitialFit = true;

    public LayoutCanvas()
    {
        Focusable = true;
        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerExited       += OnPointerExited;
        PointerWheelChanged += OnPointerWheel;
        KeyDown             += OnKeyDown;
        KeyUp               += OnKeyUp;
        TextInput           += OnTextInput;
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        LayoutUpdated += OnLayoutUpdated;
    }

    // Fits exactly once per bound ViewModel, as soon as Bounds becomes valid — for a layout that
    // loads with content this frames it (LayoutViewport.ZoomToFit); for a brand-new EMPTY layout it
    // still must run so the canvas starts at a physically sane, immediately-drawable default
    // (LayoutViewport.Default) instead of the raw zoom=1.0 field default (docs/sonnet-briefs/
    // brief-L1-fix-clear-and-default-zoom.md, Bug 2) — an empty layout is not exempt from needing a
    // sensible viewport, it is the case that most needed one.
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _viewModel is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            ZoomToFitInternal();
            RaiseViewportChanged();
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
        var theme = LayoutRenderTheme.FromTheme(_activeTheme, variant);
        var vp = CurrentViewport;
        var opts = new LayoutRenderOptions { Theme = theme, ShowGrid = true, Overlay = _viewModel?.Overlay };

        context.Custom(new LayoutDrawOperation(
            new Rect(Bounds.Size), _viewModel?.Model, _viewModel?.Technology, vp, opts,
            r => Dispatcher.UIThread.Post(() => FrameUnknownLayers?.Invoke(r.UnknownLayers))));
    }

    // ── Zoom commands ─────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal();
        RaiseViewportChanged();
    }

    private void ZoomToFitInternal()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;

        var bb = Bbox.Empty;
        if (_viewModel?.Model is { } model)
            foreach (var shape in model.Shapes)
                bb = bb.Union(LayoutGeometry.BboxOf(shape));

        var vp = bb.IsEmpty
            ? LayoutViewport.Default(Bounds.Width, Bounds.Height, _viewModel?.Model.SnapDbu ?? 0, _viewModel?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron)
            : LayoutViewport.ZoomToFit(bb, Bounds.Width, Bounds.Height);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
    }

    public void ZoomIn()  => ZoomAtCenter(_zoom * ZoomFactor);
    public void ZoomOut() => ZoomAtCenter(_zoom / ZoomFactor);

    /// <summary>1 device pixel per one tick of the document's display unit (e.g. 1 px = 1 mil on a
    /// PCB layout, 1 px = 1 µm on an MMIC layout) — a stable, physically-meaningful "actual size".</summary>
    public void Zoom1To1()
    {
        if (_viewModel?.Model is not { } model) return;
        long dbuPerUnit = LayoutUnits.ToDbu(1m, model.DisplayUnit, model.DbuPerMicron);
        if (dbuPerUnit <= 0) dbuPerUnit = 1;
        ZoomAtCenter(1.0 / dbuPerUnit);
    }

    private void ZoomAtCenter(double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var vp = CurrentViewport.WithZoomAnchoredAt(newZoom, Bounds.Width / 2.0, Bounds.Height / 2.0);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
        RaiseViewportChanged();
    }

    private void RaiseViewportChanged()
    {
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    private void OnPointerPressed(object? _, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceHeld))
        {
            _isPanning = true;
            _panDragStartScreen = pos;
            _panDragStartPanX = _panX;
            _panDragStartPanY = _panY;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        if (props.IsLeftButtonPressed && _viewModel is not null)
        {
            var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
            _viewModel.OnPointerPressed(wx, wy, e.KeyModifiers, e.ClickCount);
            InvalidateVisual();
        }
    }

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            var vp = CurrentViewport;
            // Screen Y maps to world via a Y-up flip, so a downward drag (increasing screen Y) must
            // move PanY the SAME direction as the drag, unlike the schematic's Y-down canvases.
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / vp.Zoom;
            _panY = _panDragStartPanY + (pos.Y - _panDragStartScreen.Y) / vp.Zoom;
            RaiseViewportChanged();
            return;   // middle-drag pan keeps working during any left-button drawing gesture
        }

        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
        CursorWorldChanged?.Invoke(this, (wx, wy));

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        _viewModel?.OnPointerMoved(wx, wy, leftDown, e.KeyModifiers);
        InvalidateVisual();
    }

    private void OnPointerExited(object? _, PointerEventArgs e) => CursorWorldChanged?.Invoke(this, null);

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            UpdateCursor();
            return;
        }

        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
        _viewModel?.OnPointerReleased(wx, wy, e.KeyModifiers);
        InvalidateVisual();
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? ZoomFactor : 1.0 / ZoomFactor;
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);

        var vp = CurrentViewport.WithZoomAnchoredAt(newZoom, pos.X, pos.Y);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
        RaiseViewportChanged();
        e.Handled = true;   // wheel zoom keeps working during any drawing gesture (never routed to the VM)
    }

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { _spaceHeld = true; UpdateCursor(); return; }
        _viewModel?.OnKeyDown(e.Key, e.KeyModifiers);
        InvalidateVisual();
    }

    private void OnKeyUp(object? _, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { _spaceHeld = false; UpdateCursor(); }
    }

    private void OnTextInput(object? _, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 }) return;
        _viewModel?.OnTextInput(e.Text);
        InvalidateVisual();
    }

    // Crosshair for every drawing tool, arrow for Select — mirrors SymbolEditorCanvas.UpdateCursor.
    private void UpdateCursor()
    {
        if (_isPanning || _spaceHeld) { Cursor = new Cursor(StandardCursorType.Hand); return; }
        bool useCross = _viewModel is { ActiveTool: not LayoutEditorViewModel.Tool.Select };
        Cursor = useCross ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class LayoutDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                _bounds;
        private readonly LayoutView?         _view;
        private readonly Technology?         _tech;
        private readonly LayoutViewport      _vp;
        private readonly LayoutRenderOptions _opts;
        private readonly Action<LayoutRenderResult> _onResult;

        public LayoutDrawOperation(Rect bounds, LayoutView? view, Technology? tech, LayoutViewport vp, LayoutRenderOptions opts, Action<LayoutRenderResult> onResult)
        {
            _bounds = bounds; _view = view; _tech = tech; _vp = vp; _opts = opts; _onResult = onResult;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            var result = LayoutRenderer.Draw(lease.SkCanvas, _view, _tech, _vp, _opts);
            _onResult(result);
        }

        public void Dispose() { }
    }
}
