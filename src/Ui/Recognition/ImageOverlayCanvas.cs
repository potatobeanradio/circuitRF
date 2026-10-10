// The picture, and what was read drawn over it — brief-img-5-dialog.md R-im5-3, R-im5-4, R-im5-8.
//
// DRAWS; DECIDES NOTHING. Everything drawn over the picture is an ImageOverlayItem the reading produced (ImageOverlay);
// a click asks the overlay which item is under it, and a drag hands a rectangle to the view model as the scope. The
// measure tools hand their clicks to ImageScaleViewModel, which states them to the trace.
//
// Gestures: wheel or pinch zooms about the pointer; right- or middle-drag, or Space held with a left-drag, pans; a
// left-drag sets the scope rectangle; a left-click selects; a double-click fits the picture. With a tool armed a
// left-click is the tool's.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using PixelPoint = CircuitRF.Design.Layout.Recognition.Image.PixelPoint;
using PixelRect = CircuitRF.Design.Layout.Recognition.Image.PixelRect;

namespace CircuitRF.Ui.Recognition;

public sealed class ImageOverlayCanvas : Control
{
    private static readonly Color Amber = Color.Parse("#E0A010");
    private static readonly Color Accent = Color.Parse("#1E88E5");
    private static readonly Color PartColour = Color.Parse("#7B1FA2");
    private static readonly Color LineColour = Color.Parse("#00897B");
    private static readonly Color PortColour = Color.Parse("#C62828");
    private static readonly Color DrillColour = Color.Parse("#37474F");

    private ImageSourceViewModel? _vm;
    private RasterImage? _rasterShown;
    private WriteableBitmap? _picture;
    private ImageOverlay? _overlayShown;
    private WriteableBitmap? _hatch;
    private (ImageOverlay? Overlay, int Cluster) _flashKey = (null, -1);
    private WriteableBitmap? _flash;
    private readonly Dictionary<string, Geometry> _geometry = new(StringComparer.Ordinal);

    private double _zoom = 1;
    private Vector _pan;
    private bool _fitted;

    private Point? _pressAt;
    private Point _lastPointer;
    private bool _panning, _scoping, _spaceHeld;
    private PixelPoint? _hover;

    public ImageOverlayCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        AddHandler(PinchEvent, OnPinch);
    }

    /// <summary>Raised when the view moves — the panel re-places its inline boxes.</summary>
    public event Action? ViewChanged;

    /// <summary>A picture pixel on the control.</summary>
    public Point ToView(double x, double y) => new(x * _zoom + _pan.X, y * _zoom + _pan.Y);

    /// <summary>A control point in picture pixels.</summary>
    public PixelPoint ToPicture(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    // ── the view model ──────────────────────────────────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Scale.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Scale.PropertyChanged += OnVmChanged;
        }
        _fitted = false;
        InvalidateVisual();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageSourceViewModel.Source)) _fitted = false;
        if (e.PropertyName == nameof(ImageSourceViewModel.SelectedItem) && _vm?.SelectedItem is { } item) BringIntoView(item);
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!_fitted) Fit(size);
        return size;
    }

    /// <summary>The whole picture in the control, with a margin.</summary>
    public void Fit() => Fit(Bounds.Size);

    private void Fit(Size size)
    {
        if (_vm?.Raster is not { } r || size.Width <= 0 || size.Height <= 0) return;
        // The legend floats over the top edge and the prompt over the bottom: the picture is fitted between them.
        const double margin = 16, top = 44, bottom = 44;
        _zoom = Math.Max(1e-3, Math.Min((size.Width - 2 * margin) / r.Width, (size.Height - top - bottom) / r.Height));
        _pan = new Vector((size.Width - r.Width * _zoom) / 2, top + (size.Height - top - bottom - r.Height * _zoom) / 2);
        _fitted = true;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Pans just enough to bring an item on screen.</summary>
    private void BringIntoView(ImageOverlayItem item)
    {
        var b = item.Bounds;
        var tl = ToView(b.Left, b.Top);
        var br = ToView(b.Right, b.Bottom);
        var view = new Rect(Bounds.Size).Deflate(24);
        double dx = 0, dy = 0;
        if (br.X - tl.X > view.Width || tl.X < view.Left || br.X > view.Right) dx = view.Center.X - (tl.X + br.X) / 2;
        if (br.Y - tl.Y > view.Height || tl.Y < view.Top || br.Y > view.Bottom) dy = view.Center.Y - (tl.Y + br.Y) / 2;
        if (dx == 0 && dy == 0) return;
        _pan += new Vector(dx, dy);
        ViewChanged?.Invoke();
    }

    // ── drawing ─────────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new ImmutableSolidColorBrush(Color.Parse("#00000000")), new Rect(Bounds.Size));
        if (_vm?.Raster is not { } raster) return;
        EnsurePicture(raster);

        var pictureRect = new Rect(0, 0, raster.Width, raster.Height);
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            context.FillRectangle(Brushes.White, pictureRect);
            using (context.PushRenderOptions(new RenderOptions
                   { BitmapInterpolationMode = _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            using (context.PushOpacity(_vm.HasFailure ? 0.3 : 1.0))
                context.DrawImage(_picture!, pictureRect);

            if (!_vm.HasFailure) DrawOverlay(context, pictureRect);
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Color.Parse("#808080")), 1),
                              new Rect(ToView(0, 0), ToView(raster.Width, raster.Height)));
        DrawTools(context);
    }

    private void DrawOverlay(DrawingContext context, Rect pictureRect)
    {
        var vm = _vm!;
        var overlay = vm.Overlay;
        if (!ReferenceEquals(overlay, _overlayShown))
        {
            _overlayShown = overlay;
            _geometry.Clear();
            _hatch = BuildHatch(overlay);
        }
        if (_hatch is not null && (vm.ShowIgnored || vm.ShowUnknowns)) context.DrawImage(_hatch, pictureRect);

        if (vm.HoveredLayer is { } hovered && overlay.Clusters is { } clusters)
        {
            if (_flashKey != (overlay, hovered.Cluster))
            {
                _flashKey = (overlay, hovered.Cluster);
                _flash = BuildMask(clusters, [hovered.Cluster], Accent, 150, stripes: false);
            }
            if (_flash is not null) context.DrawImage(_flash, pictureRect);
        }

        double px = 1 / _zoom;
        foreach (var item in overlay.Items)
        {
            bool unknownShown = item.Unknown && vm.ShowUnknowns;
            if (!vm.Shows(item.Class) && !unknownShown) continue;
            bool selected = ReferenceEquals(item, vm.SelectedItem);
            var colour = item.Unknown ? Amber : ColourOf(item);
            var pen = new Pen(new ImmutableSolidColorBrush(selected ? Accent : colour), (selected ? 3 : item.Class == ImageOverlayClass.Copper ? 1.25 : 1.75) * px);

            switch (item.Class)
            {
                case ImageOverlayClass.Drill when item.Circle is { } c:
                    context.DrawEllipse(null, pen, new Point(c.X, c.Y), c.R, c.R);
                    break;
                case ImageOverlayClass.Port when item.Circle is { } c:
                    DrawPort(context, c, item.Label, selected ? Accent : PortColour, px);
                    break;
                default:
                    var g = GeometryOf(item);
                    // A halo under the outline: copper is outlined in its layer's colour, which is very often the
                    // colour the picture drew it in, and an outline the colour of what it outlines cannot be seen.
                    if (!selected && item.Class is ImageOverlayClass.Copper or ImageOverlayClass.Part)
                        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Colors.White, 0.85), pen.Thickness + 2 * px), g);
                    IBrush? fill = item.Class == ImageOverlayClass.Copper
                        ? new ImmutableSolidColorBrush(colour, 0.18)
                        : selected && item.Class == ImageOverlayClass.Part ? new ImmutableSolidColorBrush(Accent, 0.12) : null;
                    context.DrawGeometry(fill, pen, g);
                    break;
            }
            if (item.Label is { Length: > 0 } label && item.Class is ImageOverlayClass.Part or ImageOverlayClass.Line)
                DrawLabel(context, item, label, selected ? Accent : colour, px);
        }

        if (vm.ScopeRect is { } s)
            context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(Accent), 1.5 * px, new ImmutableDashStyle([4, 3], 0)),
                                  new Rect(s.Left, s.Top, s.Right - s.Left, s.Bottom - s.Top));
    }

    private static Color ColourOf(ImageOverlayItem item) => item.Class switch
    {
        ImageOverlayClass.Copper when item.Rgb is { } rgb => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb),
        ImageOverlayClass.Copper => Color.Parse("#B87333"),
        ImageOverlayClass.Drill => DrillColour,
        ImageOverlayClass.Part => PartColour,
        ImageOverlayClass.Line => LineColour,
        ImageOverlayClass.Port => PortColour,
        _ => Amber,
    };

    private Geometry GeometryOf(ImageOverlayItem item)
    {
        if (_geometry.TryGetValue(item.Id, out var g)) return g;
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            if (item.Closed) ctx.SetFillRule(FillRule.EvenOdd);
            foreach (var path in item.Paths)
            {
                if (path.Count == 0) continue;
                ctx.BeginFigure(new Point(path[0].X, path[0].Y), item.Closed);
                for (int i = 1; i < path.Count; i++) ctx.LineTo(new Point(path[i].X, path[i].Y));
                if (path.Count == 1) ctx.LineTo(new Point(path[0].X + 0.01, path[0].Y));
                ctx.EndFigure(item.Closed);
            }
        }
        _geometry[item.Id] = sg;
        return sg;
    }

    private static void DrawPort(DrawingContext context, (double X, double Y, double R) c, string? label, Color colour, double px)
    {
        // The schematic's port glyph: a circle with the port's number beside it.
        double r = 7 * px;
        var pen = new Pen(new ImmutableSolidColorBrush(colour), 1.75 * px);
        context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White, 0.85), pen, new Point(c.X, c.Y), r, r);
        context.DrawLine(pen, new Point(c.X - r * 0.6, c.Y), new Point(c.X + r * 0.6, c.Y));
        if (label is null) return;
        var text = Text(label, colour, px);
        context.DrawText(text, new Point(c.X + r + 2 * px, c.Y - text.Height / 2));
    }

    private static void DrawLabel(DrawingContext context, ImageOverlayItem item, string label, Color colour, double px)
    {
        var text = Text(label, colour, px);
        Point at = item.Class == ImageOverlayClass.Line && item.Paths.Count > 0 && item.Paths[0].Count > 0
            ? Midpoint(item.Paths[0])
            : new Point(item.Bounds.Left, item.Bounds.Top - text.Height - 1 * px);
        if (item.Class == ImageOverlayClass.Line) at = new Point(at.X - text.Width / 2, at.Y - text.Height - 3 * px);
        context.FillRectangle(new ImmutableSolidColorBrush(Colors.White, 0.8),
                              new Rect(at.X - 2 * px, at.Y, text.Width + 4 * px, text.Height));
        context.DrawText(text, at);
    }

    private static Point Midpoint(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count == 1) return new Point(path[0].X, path[0].Y);
        int i = path.Count / 2;
        return new Point((path[i - 1].X + path[i].X) / 2, (path[i - 1].Y + path[i].Y) / 2);
    }

    private static FormattedText Text(string s, Color colour, double px) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11 * px,
            new ImmutableSolidColorBrush(colour));

    private void DrawTools(DrawingContext context)
    {
        var scale = _vm!.Scale;
        var pen = new Pen(new ImmutableSolidColorBrush(Accent), 2);
        void Mark(PixelPoint p)
        {
            var v = ToView(p.X, p.Y);
            context.DrawEllipse(new ImmutableSolidColorBrush(Colors.White), pen, v, 4, 4);
        }
        if (scale.Tool == ImageMeasureTool.TwoPoints)
        {
            if (scale.PointA is { } a)
            {
                var end = scale.PointB ?? _hover;
                if (end is { } b) context.DrawLine(new Pen(pen.Brush, 1.5, new ImmutableDashStyle([5, 3], 0)), ToView(a.X, a.Y), ToView(b.X, b.Y));
                Mark(a);
            }
            if (scale.PointB is { } pb) Mark(pb);
        }
        else if (scale.Tool == ImageMeasureTool.Impedance && scale.PointA is { } p) Mark(p);

        if (_scoping && _pressAt is { } from)
            context.DrawRectangle(new ImmutableSolidColorBrush(Accent, 0.08), new Pen(pen.Brush, 1, new ImmutableDashStyle([4, 3], 0)),
                                  new Rect(from, _lastPointer).Normalize());
    }

    // ── bitmaps ─────────────────────────────────────────────────────────────────────────────────────

    private void EnsurePicture(RasterImage raster)
    {
        if (ReferenceEquals(raster, _rasterShown) && _picture is not null) return;
        _rasterShown = raster;
        _picture?.Dispose();
        _picture = FromRgba(raster.Width, raster.Height, raster.Rgba);
    }

    private static WriteableBitmap FromRgba(int w, int h, byte[] rgba)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        int row = w * 4;
        for (int y = 0; y < h; y++) Marshal.Copy(rgba, y * row, fb.Address + y * fb.RowBytes, row);
        return bmp;
    }

    /// <summary>Ignored colours hatched grey, colours mapped to nothing hatched amber.</summary>
    private static WriteableBitmap? BuildHatch(ImageOverlay overlay)
    {
        if (overlay.Clusters is not { } clusters || (overlay.IgnoredClusters.Count == 0 && overlay.UnmappedClusters.Count == 0)) return null;
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        var ignored = overlay.IgnoredClusters.ToHashSet();
        var unmapped = overlay.UnmappedClusters.ToHashSet();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (((x + y) & 7) >= 2) continue;
                int l = clusters.Label(x, y);
                Color? c = unmapped.Contains(l) ? Amber : ignored.Contains(l) ? Color.Parse("#707070") : null;
                if (c is not { } col) continue;
                int i = (y * w + x) * 4;
                px[i] = col.R; px[i + 1] = col.G; px[i + 2] = col.B; px[i + 3] = 170;
            }
        return FromRgba(w, h, px);
    }

    private static WriteableBitmap BuildMask(ColourClusterSet clusters, IReadOnlyCollection<int> which, Color colour, byte alpha, bool stripes)
    {
        int w = clusters.Width, h = clusters.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (stripes && ((x + y) & 7) >= 2) continue;
                if (!which.Contains(clusters.Label(x, y))) continue;
                int i = (y * w + x) * 4;
                px[i] = colour.R; px[i + 1] = colour.G; px[i + 2] = colour.B; px[i + 3] = alpha;
            }
        return FromRgba(w, h, px);
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAbout(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        ZoomAbout(e.ScaleOrigin, e.Scale > 0 ? Math.Pow(e.Scale, 0.2) : 1);
        e.Handled = true;
    }

    private void ZoomAbout(Point at, double factor)
    {
        double z = Math.Clamp(_zoom * factor, 0.01, 64);
        factor = z / _zoom;
        _pan = new Vector(at.X - (at.X - _pan.X) * factor, at.Y - (at.Y - _pan.Y) * factor);
        _zoom = z;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space) { _spaceHeld = true; Cursor = new Cursor(StandardCursorType.Hand); e.Handled = true; }
        else if (e.Key == Key.Escape && _vm is not null)
        {
            _vm.Scale.CancelToolCommand.Execute(null);
            _vm.CancelPick();
            _scoping = false;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) { _spaceHeld = false; Cursor = new Cursor(StandardCursorType.Cross); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed && _vm?.Scale.Tool == ImageMeasureTool.None)
        {
            Fit();
            e.Handled = true;
            return;
        }
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed
                   || (point.Properties.IsLeftButtonPressed && _spaceHeld);
        _pressAt = point.Properties.IsLeftButtonPressed && !_panning ? point.Position : null;
        _scoping = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hover = ToPicture(p);
        if (_panning)
        {
            _pan += p - _lastPointer;
            ViewChanged?.Invoke();
        }
        else if (_pressAt is { } from && _vm is { Scale.Tool: ImageMeasureTool.None, CanvasPick: false }
                 && (Math.Abs(p.X - from.X) > 4 || Math.Abs(p.Y - from.Y) > 4))
            _scoping = true;
        _lastPointer = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        e.Pointer.Capture(null);
        if (_panning) { _panning = false; return; }
        if (_pressAt is not { } from || _vm is not { } vm) return;
        _pressAt = null;

        if (_scoping)
        {
            _scoping = false;
            var a = ToPicture(from);
            var b = ToPicture(p);
            if (vm.Raster is { } r)
                vm.ScopeRect = new PixelRect(Math.Clamp(Math.Min(a.X, b.X), 0, r.Width), Math.Clamp(Math.Min(a.Y, b.Y), 0, r.Height),
                                             Math.Clamp(Math.Max(a.X, b.X), 0, r.Width), Math.Clamp(Math.Max(a.Y, b.Y), 0, r.Height));
            InvalidateVisual();
            return;
        }

        var at = ToPicture(p);
        if (vm.CanvasPick) { vm.PickAt(at); return; }
        if (vm.Scale.Click(at)) { ViewChanged?.Invoke(); InvalidateVisual(); return; }
        vm.SelectedItem = vm.Overlay.HitTest(at.X, at.Y, 4 / _zoom, vm.Shows);
    }
}
