using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Design.Chain;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Chain;

/// <summary>
/// The RF Chain Budget's block diagram: the chain as a <b>read-only circuitRF schematic</b> (<see cref="ChainBenches.Diagram"/>,
/// the drawing <c>netlist --to-schematic</c> makes), drawn by <see cref="SchematicRenderer"/> itself, as the Match
/// Designer's network pane is. Scroll-wheel zoom and middle-drag pan; a click on a part selects its stage, and the
/// selected stage's parts are drawn selected, through the renderer's own selection overlay.
/// </summary>
public sealed class ChainSchematicCanvas : Control
{
    public static readonly StyledProperty<ChainDiagram?> DiagramProperty =
        AvaloniaProperty.Register<ChainSchematicCanvas, ChainDiagram?>(nameof(Diagram));

    public static readonly StyledProperty<int> SelectedStageProperty =
        AvaloniaProperty.Register<ChainSchematicCanvas, int>(nameof(SelectedStage), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public ChainDiagram? Diagram { get => GetValue(DiagramProperty); set => SetValue(DiagramProperty, value); }

    /// <summary>The stage drawn selected, and the one a click selects; −1 for none.</summary>
    public int SelectedStage { get => GetValue(SelectedStageProperty); set => SetValue(SelectedStageProperty, value); }

    private const double MinZoom = 0.005, MaxZoom = 4.0, ZoomStep = 1.15;

    private SchematicModel? _model;
    private SchematicSpatialIndex? _index;
    private double _panX, _panY, _zoom = 1.0;
    private bool _fitted;
    private Point? _dragFrom;

    // Z arms a zoom box, F fits, Escape disarms: the keys a schematic canvas with no editor answers
    // (SchematicCanvas.OnViewerKeyDown), with its rules — a click without a drag zooms nothing, and the box disarms
    // either way. The box is drawn as the schematic's own rubber band.
    private bool _zoomBoxArmed;
    private (double X, double Y)? _zoomBoxStart;
    private (double X, double Y, double W, double H)? _zoomBoxRect;

    public ChainSchematicCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>Fit the whole chain in the view: the F key and the tab's Fit button.</summary>
    public void ZoomToFit()
    {
        _fitted = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Copies the whole drawing to the clipboard as the schematic editor's Copy does (SchematicClipboard.CopyAsync):
    /// PDF, SVG and a bitmap for a slide or a document, and circuitRF's own schematic data, so it also pastes into a
    /// schematic as parts and wires.
    /// </summary>
    public async System.Threading.Tasks.Task CopyToClipboardAsync()
    {
        if (Diagram is not { } d || TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard } top) return;
        var m = d.Model;
        IntPtr hwnd = top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await CircuitRF.Ui.Clipboard.SchematicClipboard.CopyAsync(clipboard, [.. m.Components], [.. m.Wires], [.. m.CanvasObjects],
                                                                m.GridSize, [.. m.NetLabels], m.SchematicDirectory, hwnd);
    }

    private void SetZoomBoxArmed(bool armed)
    {
        _zoomBoxArmed = armed;
        Cursor = new Cursor(armed ? StandardCursorType.Cross : StandardCursorType.Arrow);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return;
        switch (e.Key)
        {
            case Key.Z: SetZoomBoxArmed(true); e.Handled = true; break;
            case Key.F: ZoomToFit(); e.Handled = true; break;
            case Key.Escape when _zoomBoxArmed:
                _zoomBoxStart = null;
                _zoomBoxRect = null;
                SetZoomBoxArmed(false);
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    private void ZoomToRect(double x0, double y0, double x1, double y1)
    {
        double worldW = Math.Abs(x1 - x0), worldH = Math.Abs(y1 - y0);
        if (worldW < 1 || worldH < 1) return;
        const double pad = 0.05;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2.0 * pad), MinZoom, MaxZoom);
        _panX = (x0 + x1) / 2.0 - Bounds.Width / (2.0 * _zoom);
        _panY = (y0 + y1) / 2.0 - Bounds.Height / (2.0 * _zoom);
        _fitted = true;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiagramProperty)
        {
            var old = _model;
            (_model, _index) = Diagram is { } d ? d.Model.BuildRenderModel() : (null, null);
            // A value edit keeps the user's zoom; a drawing of another extent (a stage added) re-frames it.
            if (old is null || _model is null || Math.Abs(old.BbMaxX - _model.BbMaxX) > 0.5 || Math.Abs(old.BbMaxY - _model.BbMaxY) > 0.5)
                _fitted = false;
            InvalidateVisual();
        }
        else if (change.Property == SelectedStageProperty) InvalidateVisual();
    }

    private bool Fit()
    {
        if (_model is null || Bounds.Width < 1 || Bounds.Height < 1) return false;
        double worldW = Math.Max(_model.BbMaxX - _model.BbMinX, 1), worldH = Math.Max(_model.BbMaxY - _model.BbMinY, 1);
        const double pad = 0.03;
        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2 * pad), MinZoom, MaxZoom);
        _panX = _model.BbMinX - (Bounds.Width - worldW * _zoom) / (2 * _zoom);
        _panY = _model.BbMinY - (Bounds.Height - worldH * _zoom) / (2 * _zoom);
        _fitted = true;
        return true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        var p = e.GetPosition(this);
        double wx = p.X / _zoom + _panX, wy = p.Y / _zoom + _panY;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep), MinZoom, MaxZoom);
        _panX = wx - p.X / _zoom;
        _panY = wy - p.Y / _zoom;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (_zoomBoxArmed && pt.Properties.IsLeftButtonPressed)
        {
            var q = e.GetPosition(this);
            _zoomBoxStart = (q.X / _zoom + _panX, q.Y / _zoom + _panY);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (pt.Properties.IsMiddleButtonPressed)
        {
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed || _model is null || Diagram is not { } d) return;
        var p = e.GetPosition(this);
        double wx = p.X / _zoom + _panX, wy = p.Y / _zoom + _panY, tol = 6 / _zoom;
        var hit = _model.Components.FirstOrDefault(c => wx >= c.GlyphBbMinX - tol && wx <= c.GlyphBbMaxX + tol
                                                      && wy >= c.GlyphBbMinY - tol && wy <= c.GlyphBbMaxY + tol
                                                      && d.StageOfComponent.ContainsKey(c.Id));
        // A click off every part clears the selection, as a click on the Blocks view's empty space does.
        SelectedStage = hit is null ? -1 : d.StageOfComponent[hit.Id];
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_zoomBoxStart is { } start)
        {
            var q = e.GetPosition(this);
            double wx = q.X / _zoom + _panX, wy = q.Y / _zoom + _panY;
            _zoomBoxRect = (Math.Min(start.X, wx), Math.Min(start.Y, wy), Math.Abs(wx - start.X), Math.Abs(wy - start.Y));
            InvalidateVisual();
            return;
        }
        if (_dragFrom is not { } from) return;
        var to = e.GetPosition(this);
        _panX -= (to.X - from.X) / _zoom;
        _panY -= (to.Y - from.Y) / _zoom;
        _dragFrom = to;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_zoomBoxStart is { } start)
        {
            var q = e.GetPosition(this);
            double wx = q.X / _zoom + _panX, wy = q.Y / _zoom + _panY;
            _zoomBoxStart = null;
            _zoomBoxRect = null;
            e.Pointer.Capture(null);
            if (Math.Abs(wx - start.X) > 1 && Math.Abs(wy - start.Y) > 1)
                ZoomToRect(Math.Min(start.X, wx), Math.Min(start.Y, wy), Math.Max(start.X, wx), Math.Max(start.Y, wy));
            SetZoomBoxArmed(false);
            InvalidateVisual();
            return;
        }
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        _fitted = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 4 || Bounds.Height < 4 || _model is null) return;
        if (!_fitted) Fit();
        var theme = ThemeService.Active;
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var selected = Diagram is { } d
            ? d.StageOfComponent.Where(kv => kv.Value == SelectedStage).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal)
            : [];
        var overlay = selected.Count == 0 && _zoomBoxRect is null ? null
            : new SchematicOverlay { SelectedComponentIds = selected, RubberBand = _zoomBoxRect, RubberBandCrossing = false };
        context.Custom(new Op(new Rect(Bounds.Size), _model, _index, _panX, _panY, _zoom,
                              SchematicRenderTheme.FromTheme(theme, variant), overlay));
    }

    private sealed class Op(Rect bounds, SchematicModel model, SchematicSpatialIndex? index, double panX, double panY,
                            double zoom, SchematicRenderTheme theme, SchematicOverlay? overlay) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } lease) return;
            using var l = lease.Lease();
            SchematicRenderer.Draw(l.SkCanvas, (bounds.Width, bounds.Height), model, index, panX, panY, zoom, theme, overlay);
        }
    }
}
