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

    public ChainSchematicCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
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
        var pt = e.GetCurrentPoint(this);
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
        if (hit is null) return;
        SelectedStage = d.StageOfComponent[hit.Id];
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
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
        context.Custom(new Op(new Rect(Bounds.Size), _model, _index, _panX, _panY, _zoom,
                              SchematicRenderTheme.FromTheme(theme, variant),
                              selected.Count == 0 ? null : new SchematicOverlay { SelectedComponentIds = selected }));
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
