using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using SkiaSharp;

namespace CircuitRF.Ui.Plugins.Views;

/// <summary>
/// A <c>layout</c> view (brief-plg-3 §3): the plugin's shapes, decoded by the PCell wire's own codec, drawn by the
/// layout editor's <see cref="LayoutCanvas"/> in <see cref="LayoutCanvas.ReadOnly"/> mode — <see cref="LayoutRenderer"/>
/// in the technology's colours, with its pan, zoom, fit (F) and zoom box — and the editor's rulers along two edges, a
/// list of the technology's layers to show or hide, and the pointer's position in the layout's unit.
///
/// <para><b>Read-only artwork, like a PCell instance.</b> Nothing selects, moves or deletes. What the plugin declared as
/// a handle is a grip: dragging it shows the grip's own ghost and the value it would set, and on release the value is
/// solved by <see cref="PCellHandleSolver"/> — the solver a placed PCell's grips use, not a copy — and sent as
/// <c>event {kind: "handle", param, value}</c>, which sets the parameter. The plugin is asked once, on release, never
/// per mouse move. Shift-drag measures.</para>
/// </summary>
public sealed class PluginLayoutViewControl : Grid, IPluginViewControl
{
    private readonly string _name;
    private readonly PluginViewContext _context;
    private readonly PluginViewGesture _gesture;
    private readonly LayoutCanvas _canvas;
    private readonly LayoutRulerControl _hRuler, _vRuler;
    private readonly StackPanel _layerList = new() { Spacing = 2 };
    private readonly TextBlock _readout;
    private readonly PluginLayoutOverlay _overlay;
    private readonly HashSet<LayerKey> _hidden = [];
    private PluginLayoutView? _view;
    private LayoutEditorViewModel? _vm;
    private bool _fitted;

    public PluginLayoutViewControl(string name, PluginViewContext context, PluginViewGesture gesture)
    {
        _name = name;
        _context = context;
        _gesture = gesture;
        ColumnDefinitions = new ColumnDefinitions("26,*,Auto");
        RowDefinitions = new RowDefinitions("26,*,Auto");

        _canvas = new LayoutCanvas { ReadOnly = true, ClipToBounds = true };
        _overlay = new PluginLayoutOverlay(this);
        _canvas.CanvasOverlay = _overlay;
        _hRuler = new LayoutRulerControl { Orientation = LayoutRulerOrientation.Horizontal, ClipToBounds = true };
        _vRuler = new LayoutRulerControl { Orientation = LayoutRulerOrientation.Vertical, ClipToBounds = true };
        _readout = new TextBlock { FontSize = 11, Opacity = 0.75, Margin = new Thickness(6, 3), TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 15 };

        Place(_hRuler, 0, 1);
        Place(_vRuler, 1, 0);
        Place(_canvas, 1, 1);
        var layers = new ScrollViewer
        {
            Content = _layerList, Width = 150, Padding = new Thickness(8, 4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Place(layers, 0, 2); SetRowSpan(layers, 2);
        Place(_readout, 2, 0); SetColumnSpan(_readout, 3);

        _canvas.ViewportChanged += (_, _) => SyncRulers();
        _canvas.LayoutUpdated += (_, _) =>
        {
            SyncRulers();
            if (!_fitted && _vm is not null && _canvas.Bounds.Width > 1) { _fitted = true; _canvas.ZoomToFit(); }
        };
        _canvas.CursorWorldChanged += (_, world) =>
        {
            _hRuler.SetCursorWorld(world?.X);
            _vRuler.SetCursorWorld(world?.Y);
            _overlay.Hover = world;
            ShowReadout();
        };
    }

    private void Place(Control c, int row, int col) { SetRow(c, row); SetColumn(c, col); Children.Add(c); }

    public LayoutCanvas Canvas => _canvas;
    public PluginLayoutView? View => _view;
    public PluginLayoutOverlay Overlay => _overlay;

    public string? Show(PluginView view)
    {
        var v = (PluginLayoutView)view;
        _view = v;
        _vm = new LayoutEditorViewModel(v.Layout) { DisplayUnitIsViewLocal = true };
        SyncTechnology();
        _canvas.ViewModel = _vm;
        _hRuler.SetUnits(v.Layout.DbuPerMicron, v.Layout.DisplayUnit);
        _vRuler.SetUnits(v.Layout.DbuPerMicron, v.Layout.DisplayUnit);
        _overlay.SetView(v);
        BuildLayerList(v);
        _canvas.InvalidateVisual();
        return null;
    }

    /// <summary>The technology as drawn: a copy with the user's hidden layers off, never the shared instance.</summary>
    private void SyncTechnology()
    {
        if (_vm is null || _view is null) return;
        var tech = _view.Technology;
        _vm.Technology = tech is null || _hidden.Count == 0 ? tech : TechnologyLayerSelection.WithVisibility(tech, l => !_hidden.Contains(l.Key));
        _canvas.InvalidateVisual();
    }

    private void BuildLayerList(PluginLayoutView v)
    {
        _layerList.Children.Clear();
        _layerList.Children.Add(new TextBlock { Text = "Layers", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 2) });
        var used = v.Layout.Shapes.Select(s => s.Layer).ToHashSet();
        var defs = v.Technology?.Layers.Where(l => used.Contains(l.Key)).ToList() ?? [];
        foreach (var def in defs)
        {
            var key = def.Key;
            var box = new CheckBox { Content = def.Name, FontSize = 11, IsChecked = !_hidden.Contains(key), MinHeight = 22 };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _hidden.Remove(key); else _hidden.Add(key);
                SyncTechnology();
            };
            _layerList.Children.Add(box);
        }
        if (defs.Count == 0)
            _layerList.Children.Add(new TextBlock { Text = "Drawn on the fallback palette.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
    }

    private void SyncRulers()
    {
        _hRuler.SetViewport(_canvas.CurrentPanX, _canvas.CurrentPanY, _canvas.CurrentZoom, _canvas.Bounds.Width, _canvas.Bounds.Height);
        _vRuler.SetViewport(_canvas.CurrentPanX, _canvas.CurrentPanY, _canvas.CurrentZoom, _canvas.Bounds.Width, _canvas.Bounds.Height);
    }

    internal string Length(double dbu) => _view is null ? "" :
        LayoutUnits.Format((long)Math.Round(dbu), _view.Layout.DisplayUnit, _view.Layout.DbuPerMicron) + " " + LayoutUnits.Suffix(_view.Layout.DisplayUnit);

    /// <summary>The line under the canvas: a drag's value, a measurement, or where the pointer is.</summary>
    internal void ShowReadout()
    {
        _readout.Text = _overlay.Readout() ?? (_overlay.Hover is { } h ? $"x {Length(h.X)}   y {Length(h.Y)}" : "");
        _canvas.InvalidateOverlay();
    }

    /// <summary>A handle released: the solved value, sent as the event and set on the parameter.</summary>
    internal void Release(PCellHandle handle, double value)
    {
        var param = _context.Manifest.Params.First(p => p.Name == handle.Parameter);
        string text = PluginViews.QuantityText(param, value, new PluginParamContext(null));
        _gesture("handle", new JsonObject { ["param"] = handle.Parameter, ["value"] = value }, handle.Parameter, text);
    }

    internal PluginParam? ParamOf(string name) => _context.Manifest.Params.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// The layout view's grips and its measure, on <see cref="ILayoutCanvasOverlay"/>'s seam. It draws transient chrome
/// only; the layout under it is never touched. Pan, zoom and the zoom box are the canvas's: a press that is not on a
/// grip and not shift-held is declined.
/// </summary>
public sealed class PluginLayoutOverlay(PluginLayoutViewControl owner) : ILayoutCanvasOverlay
{
    private PluginLayoutView? _view;
    private readonly List<(PCellHandle Handle, double PerUnit)> _grips = [];
    private int _dragging = -1;
    private (double X, double Y) _ghost;
    private (long X, long Y)? _measureFrom, _measureTo;
    private bool _measuring;

    public (double X, double Y)? Hover { get; set; }
    public bool CompanionPressResolvedNewSelection { get; set; }

    /// <summary>The grips this view can drag, and why any declared handle cannot be.</summary>
    public IReadOnlyList<PCellHandle> Grips => _grips.Select(g => g.Handle).ToList();
    public List<string> Refused { get; } = [];

    public void SetView(PluginLayoutView view)
    {
        _view = view;
        _grips.Clear();
        Refused.Clear();
        _dragging = -1;
        foreach (var h in view.Handles)
        {
            double? perUnit = PluginHandles.PerUnit(view, h, owner.ParamOf(h.Parameter));
            if (perUnit is null)
            {
                Refused.Add($"The {view.Name} view's handle on '{h.Parameter}' cannot be dragged: a linear handle drives a length in metres and an angular one an angle in degrees.");
                continue;
            }
            _grips.Add((h, perUnit.Value));
        }
    }

    /// <summary>The value the parameter takes when grip <paramref name="index"/> is let go at
    /// (<paramref name="x"/>, <paramref name="y"/>) — solved by <see cref="PCellHandleSolver"/>.</summary>
    public double? Solve(int index, double x, double y)
        => _view is null || index < 0 || index >= _grips.Count ? null : PluginHandles.Solve(_view, _grips[index].Handle, _grips[index].PerUnit, x, y);

    /// <summary>Drags grip <paramref name="index"/> to (<paramref name="x"/>, <paramref name="y"/>) and lets go, as a
    /// pointer would. False when there is no such grip or nothing could be solved.</summary>
    public bool DragTo(int index, double x, double y)
    {
        if (Solve(index, x, y) is not { } value) return false;
        owner.Release(_grips[index].Handle, value);
        return true;
    }

    public string? Readout()
    {
        if (_dragging >= 0 && _view is not null)
        {
            var (h, perUnit) = _grips[_dragging];
            double v0 = PluginViews.NumberOf(_view.Params[h.Parameter]) ?? 0;
            double proj = h.Project(_ghost.X, _ghost.Y) - h.ProjectedPosition;
            if (h.Kind == PCellHandleKind.Angular) proj = PCellHandleSolver.UnwrapNear(h.Project(_ghost.X, _ghost.Y), h.ProjectedPosition) - h.ProjectedPosition;
            double v = v0 + proj / perUnit;
            if (h.Min is { } lo) v = Math.Max(v, lo);
            if (h.Max is { } hi) v = Math.Min(v, hi);
            var p = owner.ParamOf(h.Parameter);
            return $"{h.DisplayLabel}: {PluginValueFormat.Number(v, p?.Unit)} — release to apply";
        }
        if (_measureFrom is { } a && _measureTo is { } b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return $"Δx {owner.Length(dx)}   Δy {owner.Length(dy)}   distance {owner.Length(Math.Sqrt(dx * dx + dy * dy))}";
        }
        return null;
    }

    public void Draw(SKCanvas canvas, LayoutViewport vp, LayoutRenderTheme theme)
    {
        using var grip = new SKPaint { IsAntialias = true, Color = new SKColor(0x2E, 0x8B, 0xFF), Style = SKPaintStyle.Fill };
        using var ring = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var stem = new SKPaint { IsAntialias = true, Color = new SKColor(0x2E, 0x8B, 0xFF, 0xA0), Style = SKPaintStyle.Stroke, StrokeWidth = 1,
                                       PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
        for (int i = 0; i < _grips.Count; i++)
        {
            var h = _grips[i].Handle;
            var anchor = new SKPoint((float)vp.WorldToScreenX(h.AnchorX), (float)vp.WorldToScreenY(h.AnchorY));
            var at = new SKPoint((float)vp.WorldToScreenX(h.X), (float)vp.WorldToScreenY(h.Y));
            canvas.DrawLine(anchor, at, stem);
            canvas.DrawCircle(at, 5, grip);
            canvas.DrawCircle(at, 5, ring);
            if (i == _dragging)
            {
                var ghost = new SKPoint((float)vp.WorldToScreenX(_ghost.X), (float)vp.WorldToScreenY(_ghost.Y));
                using var ghostPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0x2E, 0x8B, 0xFF, 0x80), Style = SKPaintStyle.Fill };
                canvas.DrawLine(at, ghost, stem);
                canvas.DrawCircle(ghost, 6, ghostPaint);
                canvas.DrawCircle(ghost, 6, ring);
            }
        }
        if (_measureFrom is { } a && _measureTo is { } b)
        {
            using var line = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xA0, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            var p0 = new SKPoint((float)vp.WorldToScreenX(a.X), (float)vp.WorldToScreenY(a.Y));
            var p1 = new SKPoint((float)vp.WorldToScreenX(b.X), (float)vp.WorldToScreenY(b.Y));
            canvas.DrawLine(p0, p1, line);
            canvas.DrawCircle(p0, 3, line);
            canvas.DrawCircle(p1, 3, line);
        }
    }

    public Bbox ContentBounds() => Bbox.Empty;

    private int HitGrip(long x, long y, long tol)
    {
        long r = Math.Max(tol, 1) * 2;
        for (int i = 0; i < _grips.Count; i++)
        {
            var h = _grips[i].Handle;
            if (Math.Abs(h.X - x) <= r && Math.Abs(h.Y - y) <= r) return i;
        }
        return -1;
    }

    public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount)
    {
        int hit = HitGrip(worldX, worldY, tolDbu);
        if (hit >= 0)
        {
            _dragging = hit;
            _ghost = (_grips[hit].Handle.X, _grips[hit].Handle.Y);
            _measureFrom = _measureTo = null;
            owner.ShowReadout();
            return true;
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            _measuring = true;
            _measureFrom = _measureTo = (worldX, worldY);
            owner.ShowReadout();
            return true;
        }
        if (_measureFrom is not null) { _measureFrom = _measureTo = null; owner.ShowReadout(); }
        return false;
    }

    public bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown, KeyModifiers modifiers)
    {
        if (_dragging >= 0 && leftButtonDown)
        {
            var (h, _) = _grips[_dragging];
            if (h.Kind == PCellHandleKind.Angular)
            {
                double radius = Math.Sqrt(Math.Pow(h.X - h.AnchorX, 2) + Math.Pow(h.Y - h.AnchorY, 2));
                double a = Math.Atan2(worldY - h.AnchorY, worldX - h.AnchorX);
                _ghost = (h.AnchorX + radius * Math.Cos(a), h.AnchorY + radius * Math.Sin(a));
            }
            else
            {
                // The ghost slides along the grip's own axis: the only direction the parameter moves it.
                double r = h.AxisDeg * Math.PI / 180, along = h.Project(worldX, worldY) - h.ProjectedPosition;
                _ghost = (h.X + along * Math.Cos(r), h.Y + along * Math.Sin(r));
            }
            owner.ShowReadout();
            return true;
        }
        if (_measuring && leftButtonDown)
        {
            _measureTo = (worldX, worldY);
            owner.ShowReadout();
            return true;
        }
        return false;
    }

    public bool OnPointerReleased(long worldX, long worldY)
    {
        if (_dragging >= 0)
        {
            int index = _dragging;
            _dragging = -1;
            var (h, _) = _grips[index];
            bool moved = Math.Abs(_ghost.X - h.X) > 0.5 || Math.Abs(_ghost.Y - h.Y) > 0.5;
            if (moved && Solve(index, _ghost.X, _ghost.Y) is { } value) owner.Release(h, value);
            owner.ShowReadout();
            return true;
        }
        if (_measuring) { _measuring = false; return true; }
        return false;
    }

    public bool OnKeyDown(Key key, KeyModifiers modifiers)
    {
        if (key != Key.Escape || (_dragging < 0 && _measureFrom is null)) return false;
        _dragging = -1;
        _measureFrom = _measureTo = null;
        owner.ShowReadout();
        return true;
    }

    public void OnKeyUp(Key key, KeyModifiers modifiers) { }
}

/// <summary>
/// A plugin layout's grip, solved (brief-plg-3 §3). The plugin is not asked while a grip is dragged, so the geometry the
/// solver measures is the grip's own: it moves one for one with its parameter — a length in metres along its axis, an
/// angle in degrees about its anchor. <see cref="PCellHandleSolver"/> does the measuring and the solving, exactly as it
/// does for a placed PCell's grips; nothing here is a second solver.
/// </summary>
public static class PluginHandles
{
    /// <summary>DBU (or degrees) the grip moves per unit of its parameter, or null when the parameter is not a length
    /// in metres (linear) or an angle in degrees (angular) — such a grip is not draggable.</summary>
    public static double? PerUnit(PluginLayoutView view, PCellHandle h, PluginParam? p)
    {
        if (p is null || p.Kind != PluginParamKind.Quantity) return null;
        if (h.Kind == PCellHandleKind.Angular) return p.Unit == "deg" ? 1.0 : null;
        return p.Unit == "metre" ? view.Layout.DbuPerMicron * 1e6 : null;
    }

    /// <summary>The parameter value that puts grip <paramref name="h"/> at (<paramref name="x"/>, <paramref name="y"/>),
    /// from the value the view was drawn at; null when it cannot be solved.</summary>
    public static double? Solve(PluginLayoutView view, PCellHandle h, double perUnit, double x, double y)
    {
        double v0 = PluginViews.NumberOf(view.Params[h.Parameter]) ?? 0;
        var baseParams = new Dictionary<string, PCellValue>(StringComparer.Ordinal) { [h.Parameter] = PCellValue.Real(v0) };

        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> trial)
        {
            double dv = trial[h.Parameter].AsReal() - v0;
            PCellHandle moved;
            if (h.Kind == PCellHandleKind.Angular)
            {
                double a = dv * Math.PI / 180, dx = h.X - h.AnchorX, dy = h.Y - h.AnchorY;
                moved = h with { X = h.AnchorX + (long)Math.Round(dx * Math.Cos(a) - dy * Math.Sin(a)),
                                 Y = h.AnchorY + (long)Math.Round(dx * Math.Sin(a) + dy * Math.Cos(a)) };
            }
            else
            {
                double r = h.AxisDeg * Math.PI / 180, d = dv * perUnit;
                moved = h with { X = h.X + (long)Math.Round(d * Math.Cos(r)), Y = h.Y + (long)Math.Round(d * Math.Sin(r)) };
            }
            return new PCellResult([], [], Handles: [moved]);
        }

        if (!PCellHandleSolver.MeasureSensitivity(Generate, baseParams, h, 0, out double slope, out _)) return null;
        var solved = PCellHandleSolver.Solve(Generate, baseParams, h, 0, h.Project(x, y), slope);
        return solved.Ok ? solved.Value.AsReal() : null;
    }
}
