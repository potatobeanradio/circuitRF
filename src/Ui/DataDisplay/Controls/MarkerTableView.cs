using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.Controls;

public sealed class MarkerTableView : UserControl
{
    private MarkerTableViewModel? _vm;
    private bool _scrollingHorizontal;

    public MarkerTableView()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
        DoubleTapped += OnDoubleTapped;
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (_vm is not { } vm) return;
            var boxes = new MenuItem { Header = vm.Plot.MarkerTableShowInfoBoxes ? "Hide marker boxes" : "Show marker boxes" };
            boxes.Click += (_, _) => vm.ToggleBoxes();
            var close = new MenuItem { Header = "Hide marker table" };
            close.Click += (_, _) => vm.Close();
            menu.Items.Add(boxes);
            menu.Items.Add(close);
        };
        ContextMenu = menu;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        base.OnDataContextChanged(e);
        _vm = DataContext as MarkerTableViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        SyncSize();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarkerTableViewModel.BoxWidth) or nameof(MarkerTableViewModel.BoxHeight)) SyncSize();
        if (e.PropertyName is nameof(MarkerTableViewModel.NeedsRedraw) or nameof(MarkerTableViewModel.IsSelected)) InvalidateVisual();
    }

    private void SyncSize()
    {
        if (_vm is null) return;
        Width = _vm.BoxWidth;
        Height = _vm.BoxHeight;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_vm is not { } vm) return;
        context.Custom(new DrawOperation(new Rect(Bounds.Size), vm.Rows, vm.Theme, (float)vm.ScrollOffset,
            vm.Container.GetSelectedMarkers().Select(marker => marker.Id).ToHashSet(),
            PlotAccentColor.GetTransparentAccent(RenderTheme.SelectionAlpha), vm.IsSelected, (float)vm.HorizontalScrollOffset));
    }

    private sealed class DrawOperation(Rect bounds, System.Collections.Generic.IReadOnlyList<MarkerTableRow> rows,
        RenderTheme theme, float scroll, System.Collections.Generic.IReadOnlySet<Guid> selected, SKColor selectionColor,
        bool tableSelected, float horizontalScroll) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var holder = lease.Lease();
            var canvas = holder.SkCanvas;
            canvas.Save();
            float width = MarkerTableRenderer.Measure(rows).W;
            canvas.Scale((float)(bounds.Width / width));
            MarkerTableRenderer.Draw(canvas, rows, theme, scroll, selected, selectionColor, horizontalScroll: horizontalScroll);
            if (tableSelected)
            {
                using var outline = new SKPaint { Color = selectionColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                canvas.DrawRect(1, 1, width - 2,
                    (float)(bounds.Height * width / bounds.Width) - 2, outline);
            }
            canvas.Restore();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var rows = vm.Rows;
        var size = MarkerTableRenderer.Measure(rows);
        double scale = Bounds.Width / size.W;
        var position = e.GetPosition(this);
        if (scale > 0 && MarkerTableRenderer.MaxHorizontalScroll(rows) > 0 &&
            position.Y / scale >= size.H - MarkerTableRenderer.ScrollbarHeight)
        {
            _scrollingHorizontal = true;
            vm.ScrollHorizontalTo(position.X / scale);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var row = scale > 0 ? MarkerTableRenderer.RowAt(rows, position.Y / scale, vm.ScrollOffset) : null;
        if (row is not null)
        {
            vm.SelectMarker(row);
        }
        else
        {
            vm.SelectOnly();
            var root = TopLevel.GetTopLevel(this);
            vm.StartGroupDrag(root is null ? e.GetPosition(this) : e.GetPosition(root));
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is not { } vm || e.Pointer.Captured != this) return;
        if (_scrollingHorizontal)
        {
            double scale = Bounds.Width / MarkerTableRenderer.Measure(vm.Rows).W;
            if (scale > 0) vm.ScrollHorizontalTo(e.GetPosition(this).X / scale);
            return;
        }
        var root = TopLevel.GetTopLevel(this);
        vm.UpdateGroupDrag(root is null ? e.GetPosition(this) : e.GetPosition(root));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is not { } vm || e.Pointer.Captured != this) return;
        if (_scrollingHorizontal) _scrollingHorizontal = false;
        else vm.EndGroupDrag();
        e.Pointer.Capture(null);
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is not { } vm) return;
        if ((e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0) && MarkerTableRenderer.MaxHorizontalScroll(vm.Rows) > 0)
            vm.ScrollHorizontal(-(e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * MarkerTableRenderer.ValueColumn);
        else if (MarkerTableRenderer.MaxScroll(vm.Rows) > 0)
            vm.Scroll(-e.Delta.Y * MarkerTableRenderer.LineHeight * 2);
        else return;
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not { } vm) return;
        double scale = Bounds.Width / MarkerTableRenderer.Measure(vm.Rows).W;
        var row = scale > 0 ? MarkerTableRenderer.RowAt(vm.Rows, e.GetPosition(this).Y / scale, vm.ScrollOffset) : null;
        if (row is null) return;
        new Flyout
        {
            Content = new MarkerEditorView { DataContext = new MarkerEditorViewModel(vm.MarkerEditor(row)) },
            Placement = PlacementMode.RightEdgeAlignedTop,
        }.ShowAt(this);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_vm is { } vm && e.Key is Key.Up or Key.Down or Key.Left or Key.Right &&
            vm.Container.StepSelectedMarkers(e.Key is Key.Up or Key.Right ? 1 : -1))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}