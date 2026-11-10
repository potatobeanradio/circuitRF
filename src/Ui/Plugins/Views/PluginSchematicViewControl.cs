using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Design.Plugins;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Render;


namespace CircuitRF.Ui.Plugins.Views;

/// <summary>
/// A <c>schematic</c> view (brief-plg-3 §4): the staged <c>.cnl</c> drawn by <see cref="NetlistSchematic.FromFile"/>, as
/// <c>netlist --to-schematic</c> draws it, and painted by <see cref="SchematicRenderer"/> in the theme's schematic
/// colours. Read-only: drag to pan, the wheel to zoom about the pointer, a double-click to fit. A new reply refits
/// only when the user has not moved the view since the last one.
/// </summary>
public sealed class PluginSchematicViewControl : Control, IPluginViewControl
{
    private SchematicModel? _model;
    private SchematicSpatialIndex? _index;
    private WorldRect? _extents;
    private double _panX, _panY, _zoom = 1;
    private bool _fitted, _userMoved;
    private Point? _dragFrom;

    public PluginSchematicViewControl()
    {
        ClipToBounds = true;
        Focusable = true;
        PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2) { Fit(); _userMoved = false; return; }
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
        };
        PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            var at = e.GetPosition(this);
            _panX -= (at.X - from.X) / _zoom;
            _panY -= (at.Y - from.Y) / _zoom;
            _dragFrom = at;
            _userMoved = true;
            InvalidateVisual();
        };
        PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        PointerWheelChanged += (_, e) =>
        {
            var at = e.GetPosition(this);
            double wx = at.X / _zoom + _panX, wy = at.Y / _zoom + _panY;
            _zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), 1e-4, 1e3);
            _panX = wx - at.X / _zoom;
            _panY = wy - at.Y / _zoom;
            _userMoved = true;
            InvalidateVisual();
            e.Handled = true;
        };
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && (!_fitted || !_userMoved)) Fit(); };
    }

    public string? Show(PluginView view)
    {
        var v = (PluginSchematicView)view;
        (_model, _index) = v.Schematic.BuildRenderModel();
        _extents = DocumentExtents.SchematicBox(v.Schematic, _model);
        if (_extents is null) return $"The {v.Name} view's netlist draws nothing.";
        if (!_userMoved) Fit();
        InvalidateVisual();
        return null;
    }

    private void Fit()
    {
        if (_extents is not { } e || Bounds.Width < 1 || Bounds.Height < 1) return;
        _zoom = DocumentExtents.FitZoom(e.W, e.H, (int)Bounds.Width, (int)Bounds.Height, 0.08);
        _panX = (e.X0 + e.X1) * 0.5 - Bounds.Width / (2 * _zoom);
        _panY = (e.Y0 + e.Y1) * 0.5 - Bounds.Height / (2 * _zoom);
        _fitted = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_model is null) return;
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
        context.Custom(new Draw(new Rect(Bounds.Size), _model, _index, _panX, _panY, _zoom, theme));
    }

    private sealed class Draw(Rect bounds, SchematicModel model, SchematicSpatialIndex? index, double panX, double panY, double zoom,
                              SchematicRenderTheme theme) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => bounds.Contains(p);
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            SchematicRenderer.Draw(lease.SkCanvas, (bounds.Width, bounds.Height), model, index, panX, panY, zoom, theme);
        }
    }
}
