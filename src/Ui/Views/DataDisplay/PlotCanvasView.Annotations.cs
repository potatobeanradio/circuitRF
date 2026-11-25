using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using CircuitRF.Ui.Controls;
using Avalonia.Skia;
using SkiaSharp;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class PlotCanvasView
{
    private AnnotationLayer? _annotationLayer;
    private SchematicInlineEditBox? _annotationEditor;
    private Point _textAnchor;
    private IPointer? _linePointer;
    private Point _linePress;
    private CanvasAnnotation? _shapePreview;
    private int _moveIndex = -1;
    private CanvasAnnotation? _moveOriginal;
    private CanvasAnnotation? _movePreview;
    private enum LineHandle { None, Start, End }
    private LineHandle _lineHandle;
    private enum BoxCorner { StartStart, EndStart, StartEnd, EndEnd }
    private static readonly BoxCorner[] BoxCorners = Enum.GetValues<BoxCorner>();
    private BoxCorner? _boxCorner;

    private void InitializeAnnotations()
    {
        if (_contentGrid is not { } grid) return;
        _annotationLayer = new(this) { IsHitTestVisible = false };
        _annotationLayer.Bind(ForegroundProperty, this.GetObservable(ForegroundProperty));
        grid.Children.Insert(grid.Children.Count - 1, _annotationLayer);
        grid.AddHandler(PointerPressedEvent, OnAnnotationPressed, RoutingStrategies.Tunnel);
        grid.AddHandler(PointerMovedEvent, OnAnnotationMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        grid.AddHandler(PointerReleasedEvent, OnAnnotationReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        grid.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(e.Pointer, _linePointer))
            {
                CancelAnnotationDraft();
                Display?.EndCanvasGesture();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelAnnotationDraft();
            Display?.EndCanvasGesture();
        };
    }

    private void SwitchAnnotationDisplay(DataDisplayViewModel? display)
    {
        CancelAnnotationDraft();
        if (_watchedDisplay is { } old)
            ((INotifyCollectionChanged)old.Annotations).CollectionChanged -= OnAnnotationsChanged;
        if (display is not null)
            ((INotifyCollectionChanged)display.Annotations).CollectionChanged += OnAnnotationsChanged;
        _annotationLayer?.InvalidateVisual();
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _annotationLayer?.InvalidateVisual();

    private void UpdateAnnotationView(string? property)
    {
        if (property == nameof(DataDisplayViewModel.DrawingTool)) CancelAnnotationDraft();
        if (property == nameof(DataDisplayViewModel.SelectedAnnotation)) _annotationLayer?.InvalidateVisual();
        if (property is nameof(DataDisplayViewModel.ZoomLevel) or nameof(DataDisplayViewModel.ViewOffsetX)
            or nameof(DataDisplayViewModel.ViewOffsetY))
        {
            PositionAnnotationEditor();
            _annotationLayer?.InvalidateVisual();
        }
    }

    private static Point ViewPoint(DataDisplayViewModel display, double x, double y)
        => new(x * display.ZoomLevel + display.ViewOffsetX, y * display.ZoomLevel + display.ViewOffsetY);

    private static Point SnappedPoint(DataDisplayViewModel display, Point point)
        => new(display.SnapToGrid((point.X - display.ViewOffsetX) / display.ZoomLevel),
               display.SnapToGrid((point.Y - display.ViewOffsetY) / display.ZoomLevel));

    private void OnAnnotationPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_contentGrid is not { } grid || Display is not { } display || display.ZoomBoxArmed
            || e.GetCurrentPoint(grid).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (_annotationEditor is not null) return;

        var point = e.GetPosition(grid);
        var anchor = SnappedPoint(display, point);
        if (display.DrawingTool == AnnotationTool.None)
        {
            _lineHandle = display.SelectedAnnotation is CanvasLine selectedLine
                ? HitLineHandle(display, selectedLine, point) : LineHandle.None;
            _boxCorner = display.SelectedAnnotation switch
            {
                CanvasBox box => HitCorner(point, corner => BoxCornerPoint(display, box, corner)),
                CanvasOval oval => HitCorner(point, corner => BoxCornerPoint(display,
                    new CanvasBox(oval.X, oval.Y, oval.EndX, oval.EndY), corner)),
                CanvasText text when !string.IsNullOrEmpty(text.Text) => AnnotationLayer.HitTextCorner(display, text, point),
                _ => null,
            };
            int hit = _lineHandle != LineHandle.None || _boxCorner is not null ? display.SelectedAnnotationIndex
                : _annotationLayer?.HitTest(display, point) ?? -1;
            if (hit >= 0 || display.SelectedAnnotation is not null) Focus();
            if (hit < 0)
            {
                display.SelectAnnotation(-1);
                return;
            }
            display.SelectAnnotation(hit);
            _moveIndex = hit;
            _moveOriginal = display.Annotations[hit];
            _linePress = point;
            _linePointer = e.Pointer;
            e.Pointer.Capture(grid);
        }
        else if (display.DrawingTool == AnnotationTool.Text)
        {
            OpenAnnotationEditor(anchor);
        }
        else
        {
            Focus();
            _linePress = point;
            _linePointer = e.Pointer;
            _shapePreview = display.DrawingTool switch
            {
                AnnotationTool.Box => new CanvasBox(anchor.X, anchor.Y, anchor.X, anchor.Y),
                AnnotationTool.Oval => new CanvasOval(anchor.X, anchor.Y, anchor.X, anchor.Y),
                _ => new CanvasLine(anchor.X, anchor.Y, anchor.X, anchor.Y),
            };
            e.Pointer.Capture(grid);
        }
        e.Handled = true;
    }

    private void OnAnnotationMoved(object? sender, PointerEventArgs e)
    {
        if (_linePointer != e.Pointer || Display is not { } display) return;
        var point = e.GetPosition(_contentGrid);
        if (_moveOriginal is { } original && MovedEnough(point))
            _movePreview = MoveAnnotation(display, original, point, e.KeyModifiers);
        else if (_moveOriginal is not null)
            _movePreview = null;
        else
            _shapePreview = UpdateShapeEnd(_shapePreview, SnappedPoint(display, point), e.KeyModifiers);
        _annotationLayer?.InvalidateVisual();
        e.Handled = true;
    }

    private void OnAnnotationReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_linePointer != e.Pointer || Display is not { } display
            || e.InitialPressMouseButton != MouseButton.Left) return;
        var point = e.GetPosition(_contentGrid);
        var end = SnappedPoint(display, point);
        var shape = UpdateShapeEnd(_shapePreview, end, e.KeyModifiers);
        var original = _moveOriginal;
        var index = _moveIndex;
        var moved = original is not null && MovedEnough(point) ? MoveAnnotation(display, original, point, e.KeyModifiers) : null;
        CancelAnnotationDraft();
        if (moved is not null && (moved is not (CanvasBox or CanvasOval) || IsNonempty(moved)) && index == display.SelectedAnnotationIndex
            && ReferenceEquals(original, display.SelectedAnnotation))
            display.EditAnnotation(index, moved);
        else if (MovedEnough(point) && IsNonempty(shape))
        {
            display.AddAnnotation(shape!);
            display.DrawingTool = AnnotationTool.None;
            display.SelectAnnotation(display.Annotations.Count - 1);
        }
        e.Handled = true;
    }

    private bool MovedEnough(Point point) => double.Hypot(point.X - _linePress.X, point.Y - _linePress.Y) >= 4;

    internal static CanvasAnnotation? UpdateShapeEnd(CanvasAnnotation? shape, Point end,
        KeyModifiers modifiers = KeyModifiers.None) => shape switch
    {
        CanvasLine line => line with { EndX = end.X, EndY = end.Y },
        CanvasBox box => box with { EndX = end.X, EndY = end.Y },
        CanvasOval oval when modifiers.HasFlag(KeyModifiers.Control) => ConstrainCircle(oval, end),
        CanvasOval oval => oval with { EndX = end.X, EndY = end.Y },
        _ => null,
    };

    private static CanvasOval ConstrainCircle(CanvasOval oval, Point end)
    {
        double diameter = Math.Max(Math.Abs(end.X - oval.X), Math.Abs(end.Y - oval.Y));
        return oval with
        {
            EndX = oval.X + Math.CopySign(diameter, end.X - oval.X),
            EndY = oval.Y + Math.CopySign(diameter, end.Y - oval.Y),
        };
    }

    private static bool IsNonempty(CanvasAnnotation? shape) => shape switch
    {
        CanvasLine line => line.X != line.EndX || line.Y != line.EndY,
        CanvasBox box => box.X != box.EndX && box.Y != box.EndY,
        CanvasOval oval => oval.X != oval.EndX && oval.Y != oval.EndY,
        _ => false,
    };

    private static LineHandle HitLineHandle(DataDisplayViewModel display, CanvasLine line, Point point)
    {
        var start = ViewPoint(display, line.X, line.Y);
        var end = ViewPoint(display, line.EndX, line.EndY);
        var startDistance = double.Hypot(point.X - start.X, point.Y - start.Y);
        var endDistance = double.Hypot(point.X - end.X, point.Y - end.Y);
        return Math.Min(startDistance, endDistance) > 6 ? LineHandle.None
            : startDistance < endDistance ? LineHandle.Start : LineHandle.End;
    }

    private static bool UsesEndX(BoxCorner corner) => corner is BoxCorner.EndStart or BoxCorner.EndEnd;
    private static bool UsesEndY(BoxCorner corner) => corner is BoxCorner.StartEnd or BoxCorner.EndEnd;

    private static Point BoxCornerPoint(DataDisplayViewModel display, CanvasBox box, BoxCorner corner)
        => ViewPoint(display, UsesEndX(corner) ? box.EndX : box.X, UsesEndY(corner) ? box.EndY : box.Y);

    private static BoxCorner? HitCorner(Point point, Func<BoxCorner, Point> cornerPoint)
    {
        BoxCorner? hit = null;
        double nearest = 6;
        foreach (var corner in BoxCorners)
        {
            var handle = cornerPoint(corner);
            var distance = double.Hypot(point.X - handle.X, point.Y - handle.Y);
            if (distance > nearest) continue;
            nearest = distance;
            hit = corner;
        }
        return hit;
    }

    private CanvasAnnotation MoveAnnotation(DataDisplayViewModel display, CanvasAnnotation annotation, Point point,
        KeyModifiers modifiers)
    {
        var deltaX = (point.X - _linePress.X) / display.ZoomLevel;
        var deltaY = (point.Y - _linePress.Y) / display.ZoomLevel;
        return annotation switch
        {
            CanvasText text when _boxCorner is { } corner =>
                AnnotationLayer.ResizeText(display, text, corner, _linePress, point),
            CanvasText text => text with { X = display.SnapToGrid(text.X + deltaX), Y = display.SnapToGrid(text.Y + deltaY) },
            CanvasLine line => MoveLine(line),
            CanvasBox box => MoveBox(box),
            CanvasOval oval => MoveOval(oval),
            _ => annotation,
        };

        CanvasLine MoveLine(CanvasLine line)
        {
            if (_lineHandle == LineHandle.End)
                return line with { EndX = display.SnapToGrid(line.EndX + deltaX), EndY = display.SnapToGrid(line.EndY + deltaY) };
            var x = display.SnapToGrid(line.X + deltaX);
            var y = display.SnapToGrid(line.Y + deltaY);
            if (_lineHandle == LineHandle.Start) return line with { X = x, Y = y };
            return line with { X = x, Y = y, EndX = line.EndX + (x - line.X), EndY = line.EndY + (y - line.Y) };
        }

        CanvasBox MoveBox(CanvasBox box)
        {
            if (_boxCorner is { } corner)
            {
                box = UsesEndX(corner) ? box with { EndX = display.SnapToGrid(box.EndX + deltaX) }
                                      : box with { X = display.SnapToGrid(box.X + deltaX) };
                return UsesEndY(corner) ? box with { EndY = display.SnapToGrid(box.EndY + deltaY) }
                                       : box with { Y = display.SnapToGrid(box.Y + deltaY) };
            }
            var x = display.SnapToGrid(box.X + deltaX);
            var y = display.SnapToGrid(box.Y + deltaY);
            return box with { X = x, Y = y, EndX = box.EndX + (x - box.X), EndY = box.EndY + (y - box.Y) };
        }

        CanvasOval MoveOval(CanvasOval oval)
        {
            var moved = MoveBox(new CanvasBox(oval.X, oval.Y, oval.EndX, oval.EndY));
            var resized = oval with { X = moved.X, Y = moved.Y, EndX = moved.EndX, EndY = moved.EndY };
            return _boxCorner is { } corner && modifiers.HasFlag(KeyModifiers.Control)
                ? ConstrainOvalResize(resized, UsesEndX(corner), UsesEndY(corner)) : resized;
        }
    }

    internal static CanvasOval ConstrainOvalResize(CanvasOval resized, bool movingEndX, bool movingEndY)
    {
        var anchorX = movingEndX ? resized.X : resized.EndX;
        var anchorY = movingEndY ? resized.Y : resized.EndY;
        var moving = new Point(movingEndX ? resized.EndX : resized.X, movingEndY ? resized.EndY : resized.Y);
        var circle = ConstrainCircle(resized with { X = anchorX, Y = anchorY }, moving);
        return resized with
        {
            X = movingEndX ? anchorX : circle.EndX,
            Y = movingEndY ? anchorY : circle.EndY,
            EndX = movingEndX ? circle.EndX : anchorX,
            EndY = movingEndY ? circle.EndY : anchorY,
        };
    }

    private void OpenAnnotationEditor(Point anchor)
    {
        if (_contentGrid is not { } grid) return;
        _textAnchor = anchor;
        // The application's inline editor, in the face and size the text will be drawn in, its first line on the baseline
        // the annotation will have — so what is typed is where it lands (PositionAnnotationEditor).
        var editor = new SchematicInlineEditBox
        {
            Name = "AnnotationTextInput", AcceptsReturn = true, MinWidth = 160, TextWrapping = TextWrapping.NoWrap
        };
        editor.AddHandler(KeyDownEvent, OnAnnotationEditorKey, RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) =>
        {
            if (ReferenceEquals(_annotationEditor, editor)) CancelAnnotationDraft();
        };
        _annotationEditor = editor;
        grid.Children.Add(new Canvas { Children = { editor } });
        PositionAnnotationEditor();
        editor.Focus();
    }

    private void PositionAnnotationEditor()
    {
        if (_annotationEditor is not { } editor || Display is not { } display) return;
        var point = ViewPoint(display, _textAnchor.X, _textAnchor.Y);
        double zoom = display.ZoomLevel > 0 ? display.ZoomLevel : 1.0;
        var probe = new CanvasText(0, 0, "Ag");
        double size = Math.Max(1, probe.FontSize * zoom);
        double baseline = AnnotationRenderer.Layout(probe).Baseline * zoom;
        string text = editor.Text ?? "";
        if (editor.IsVisible) editor.MoveTo(point.X, point.Y + baseline, size);
        else editor.Open(text, point.X, point.Y + baseline, size);
        editor.Width = double.NaN;   // grows with what is typed, from MinWidth
    }

    private void OnAnnotationEditorKey(object? sender, KeyEventArgs e)
    {
        if (_annotationEditor is not { } editor || Display is not { } display) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (!string.IsNullOrWhiteSpace(editor.Text))
            {
                display.AddAnnotation(new CanvasText(_textAnchor.X, _textAnchor.Y, editor.Text));
                display.DrawingTool = AnnotationTool.None;
                display.SelectAnnotation(display.Annotations.Count - 1);
            }
        }
        else if (e.Key != Key.Escape) return;
        CancelAnnotationDraft();
        Focus();
        e.Handled = true;
    }

    private void CancelAnnotationDraft()
    {
        var pointer = _linePointer;
        _linePointer = null;
        _shapePreview = null;
        _moveIndex = -1;
        _moveOriginal = null;
        _movePreview = null;
        _lineHandle = LineHandle.None;
        _boxCorner = null;
        if (pointer is not null && ReferenceEquals(pointer.Captured, _contentGrid))
        {
            pointer.Capture(null);
            Display?.EndCanvasGesture();
        }
        if (_annotationEditor is { } editor)
        {
            _annotationEditor = null;
            if (editor.Parent is Canvas canvas) _contentGrid?.Children.Remove(canvas);
        }
        _annotationLayer?.InvalidateVisual();
    }

    private sealed class AnnotationLayer(PlotCanvasView owner) : UserControl
    {
        private static readonly DashStyle LineDashes = new([3, 2], 0);

        private static AnnotationRenderer.TextLayout Format(CanvasText text, DataDisplayViewModel display, IBrush brush)
        {
            var layout = AnnotationRenderer.Layout(text);
            return layout with { Width = layout.Width * (float)display.ZoomLevel, Height = layout.Height * (float)display.ZoomLevel };
        }

        private static Matrix TextTransform(CanvasText text, DataDisplayViewModel display)
        {
            var anchor = ViewPoint(display, text.X, text.Y);
            return Matrix.CreateRotation((int)text.Rotation * Math.PI / 180)
                * Matrix.CreateTranslation(anchor.X, anchor.Y);
        }

        private static double TextWidth(AnnotationRenderer.TextLayout text) => text.Width;

        private static Rect TextBounds(AnnotationRenderer.TextLayout text)
            => new Rect(new Point(), new Size(TextWidth(text), text.Height));

        private static Point TextCornerPoint(Rect bounds, BoxCorner corner)
            => new(UsesEndX(corner) ? bounds.Right : bounds.Left, UsesEndY(corner) ? bounds.Bottom : bounds.Top);

        public static BoxCorner? HitTextCorner(DataDisplayViewModel display, CanvasText text, Point point)
        {
            var bounds = TextBounds(Format(text, display, Brushes.Black));
            var localPoint = TextTransform(text, display).Invert().Transform(point);
            return HitCorner(localPoint, corner => TextCornerPoint(bounds, corner));
        }

        public static CanvasText ResizeText(DataDisplayViewModel display, CanvasText text, BoxCorner corner,
                                            Point press, Point current)
        {
            var transform = TextTransform(text, display);
            var inverse = transform.Invert();
            var delta = (inverse.Transform(current).X - inverse.Transform(press).X) / display.ZoomLevel;
            if (Math.Abs(delta) < 1 / display.ZoomLevel) return text;

            var width = TextWidth(Format(text, display, Brushes.Black)) / display.ZoomLevel;
            var trailing = UsesEndX(corner);
            var resizedWidth = Math.Max(1, width + (trailing ? delta : -delta));
            if (trailing) return text with { WrapWidth = resizedWidth };

            var anchor = transform.Transform(new Point((width - resizedWidth) * display.ZoomLevel, 0));
            return text with
            {
                X = (anchor.X - display.ViewOffsetX) / display.ZoomLevel,
                Y = (anchor.Y - display.ViewOffsetY) / display.ZoomLevel,
                WrapWidth = resizedWidth,
            };
        }

        private static Rect BoxBounds(DataDisplayViewModel display, CanvasBox box)
        {
            var start = ViewPoint(display, box.X, box.Y);
            var end = ViewPoint(display, box.EndX, box.EndY);
            return new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        }

        public int HitTest(DataDisplayViewModel display, Point point)
        {
            for (int index = display.Annotations.Count - 1; index >= 0; index--)
            {
                switch (display.Annotations[index])
                {
                    case CanvasOval oval:
                        var ovalBounds = BoxBounds(display, new CanvasBox(oval.X, oval.Y, oval.EndX, oval.EndY));
                        var rx = ovalBounds.Width / 2;
                        var ry = ovalBounds.Height / 2;
                        if (rx <= 0 || ry <= 0) break;
                        double norm = double.Hypot((point.X - ovalBounds.Center.X) / rx, (point.Y - ovalBounds.Center.Y) / ry);
                        if (oval.FillColor is not null && norm <= 1 || Math.Abs(norm - 1) * Math.Min(rx, ry)
                            <= Math.Max(5, oval.LineWidth * display.ZoomLevel / 2)) return index;
                        break;
                    case CanvasBox box:
                        var bounds = BoxBounds(display, box);
                        var tolerance = Math.Max(5, display.ZoomLevel);
                        if (box.FillColor is not null && bounds.Contains(point)
                            || bounds.Inflate(tolerance).Contains(point)
                            && (Math.Abs(point.X - bounds.Left) <= tolerance || Math.Abs(point.X - bounds.Right) <= tolerance
                                || Math.Abs(point.Y - bounds.Top) <= tolerance || Math.Abs(point.Y - bounds.Bottom) <= tolerance))
                            return index;
                        break;
                    case CanvasText note when !string.IsNullOrEmpty(note.Text):
                        var formatted = Format(note, display, Brushes.Black);
                        if (TextBounds(formatted).Inflate(3).Contains(TextTransform(note, display).Invert().Transform(point)))
                            return index;
                        break;
                    case CanvasLine line:
                        var start = ViewPoint(display, line.X, line.Y);
                        var end = ViewPoint(display, line.EndX, line.EndY);
                        var dx = end.X - start.X;
                        var dy = end.Y - start.Y;
                        var lengthSquared = dx * dx + dy * dy;
                        var fraction = lengthSquared == 0 ? 0 : Math.Clamp(
                            ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
                        if (double.Hypot(point.X - (start.X + fraction * dx), point.Y - (start.Y + fraction * dy))
                            <= Math.Max(5, line.LineWidth * display.ZoomLevel / 2)) return index;
                        break;
                }
            }
            return -1;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (owner.Display is not { } display || Foreground is not { } brush) return;
            var annotations = display.Annotations.Select((annotation, index) =>
                index == owner._moveIndex && owner._movePreview is { } moving ? moving : annotation).ToList();
            if (owner._shapePreview is { } draft) annotations.Add(draft);
            context.Custom(new AnnotationDrawOperation(new Rect(Bounds.Size), annotations.ToArray(), display.ZoomLevel,
                display.ViewOffsetX, display.ViewOffsetY, display.Theme.TextColor));
            for (int index = 0; index < display.Annotations.Count; index++)
                Draw(index == owner._moveIndex && owner._movePreview is { } moving ? moving : display.Annotations[index],
                     index == display.SelectedAnnotationIndex || display.AllAnnotationsSelected);
            if (owner._shapePreview is { } preview) Draw(preview, false);

            void Draw(CanvasAnnotation annotation, bool selected)
            {
                if (!selected) return;
                var selection = selected ? new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash) : null;
                switch (annotation)
                {
                    case CanvasOval oval:
                        Draw(new CanvasBox(oval.X, oval.Y, oval.EndX, oval.EndY), selected);
                        break;
                    case CanvasBox box:
                        var bounds = BoxBounds(display, box);
                        if (selection is not null)
                        {
                            context.DrawRectangle(null, selection, bounds.Inflate(3));
                            foreach (var corner in BoxCorners)
                            {
                                var handle = BoxCornerPoint(display, box, corner);
                                context.DrawRectangle(Brushes.DodgerBlue, null, new Rect(handle, new Size()).Inflate(3));
                            }
                        }
                        break;
                    case CanvasLine line:
                        var start = ViewPoint(display, line.X, line.Y);
                        var end = ViewPoint(display, line.EndX, line.EndY);
                        if (selection is not null)
                        {
                            context.DrawRectangle(null, selection, new Rect(start, new Size()).Inflate(3));
                            context.DrawRectangle(null, selection, new Rect(end, new Size()).Inflate(3));
                        }
                        break;
                    case CanvasText note when !string.IsNullOrEmpty(note.Text):
                        var formatted = Format(note, display, brush);
                        using (context.PushTransform(TextTransform(note, display)))
                        {
                            if (selection is not null)
                            {
                                var textBounds = TextBounds(formatted);
                                context.DrawRectangle(null, selection, textBounds.Inflate(3));
                                foreach (var corner in BoxCorners)
                                    context.DrawRectangle(Brushes.DodgerBlue, null,
                                        new Rect(TextCornerPoint(textBounds, corner), new Size()).Inflate(3));
                            }
                        }
                        break;
                }

            }
        }
    }

    private sealed class AnnotationDrawOperation(Rect bounds, CanvasAnnotation[] annotations,
        double zoom, double offsetX, double offsetY, SKColor color) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
            canvas.Translate((float)offsetX, (float)offsetY);
            canvas.Scale((float)zoom);
            foreach (var annotation in annotations) AnnotationRenderer.Draw(canvas, annotation, color);
            canvas.Restore();
        }
    }
}