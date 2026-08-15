// ================================================================
//  PlotCanvasView.axaml.cs  —  Canvas interaction for one tab.
//
//  Handles middle-button panning, drag-select rubber-band, scroll-wheel
//  canvas zoom, and canvas-background deselect.  The DataContext is
//  TabViewModel; all state changes go through TabViewModel.DataDisplay.
//
//  GetCanvasSizeFunc is registered on OnDataContextChanged so the window
//  ViewModel can query the current canvas pixel size (needed for FitAll).
// ================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class PlotCanvasView : UserControl
{
    private ItemsControl?       _plotCanvas;
    private Grid?               _contentGrid;
    private DragSelectOverlay?  _dragSelectOverlay;

    // ---- Middle-button canvas pan state ----------------------------
    private bool  _canvasPanning;
    private Point _canvasPanLast;

    // ---- Drag-select rubber-band state -----------------------------
    private bool  _maybeDragSelecting;
    private bool  _isDragSelecting;
    private Point _dragSelectOrigin;
    private bool  _dragSelectAdditive;

    private const double CanvasZoomStep      = 1.15;
    private const double DragSelectThreshold = 4.0;

    public PlotCanvasView()
    {
        Focusable = true;
        InitializeComponent();

        // FindControl is valid immediately after InitializeComponent() —
        // the AXAML visual tree is fully built before the constructor returns.
        _plotCanvas        = this.FindControl<ItemsControl>("PlotCanvas");
        _contentGrid       = this.FindControl<Grid>("ContentGrid");
        _dragSelectOverlay = this.FindControl<DragSelectOverlay>("DragSelectOverlay");

        // Register canvas-background handlers on PlotCanvas.
        // Handlers are registered once in the constructor so they never accumulate
        // even if the control is shown / hidden multiple times by the TabControl.
        _plotCanvas?.AddHandler(
            PointerPressedEvent,
            OnCanvasPointerPressed,
            RoutingStrategies.Bubble);

        // Middle-button pan — also on ContentGrid so pan works when the
        // cursor is over the InfoBox overlay (a sibling of PlotCanvas).
        _plotCanvas?.AddHandler(
            PointerPressedEvent, OnCanvasPanPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _plotCanvas?.AddHandler(
            PointerMovedEvent, OnCanvasPanMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _plotCanvas?.AddHandler(
            PointerReleasedEvent, OnCanvasPanReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // ContentGrid is the common ancestor of PlotCanvas AND the InfoBox overlay.
        // Registering here catches events that originate in MarkerInfoBoxView.
        _contentGrid?.AddHandler(
            PointerWheelChangedEvent, OnContentGridWheel,
            RoutingStrategies.Bubble);
        _contentGrid?.AddHandler(
            PointerPressedEvent, OnCanvasPanPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _contentGrid?.AddHandler(
            PointerMovedEvent, OnCanvasPanMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _contentGrid?.AddHandler(
            PointerReleasedEvent, OnCanvasPanReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag-select move and release at ContentGrid level so the rect keeps
        // updating even when the cursor is over an InfoBox or plot.
        _contentGrid?.AddHandler(
            PointerMovedEvent, OnDragSelectMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _contentGrid?.AddHandler(
            PointerReleasedEvent, OnDragSelectReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Re-register the size getter if the context changes after load.
        if (DataContext is TabViewModel tabVm && _plotCanvas is not null)
            tabVm.GetCanvasSizeFunc = () =>
                (_plotCanvas.Bounds.Width, _plotCanvas.Bounds.Height);
    }

    // Helper to reach the active DataDisplayViewModel.
    private DataDisplayViewModel? Display => (DataContext as TabViewModel)?.DataDisplay;

    // ---- Canvas background click → deselect + begin drag-select ----

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_plotCanvas).Properties.IsLeftButtonPressed) return;
        Focus();

        bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                        e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (!additive)
            Display?.SelectOnly((PlotContainerViewModel?)null);

        _dragSelectOrigin   = e.GetPosition(_plotCanvas);
        _dragSelectAdditive = additive;
        _maybeDragSelecting = true;
        _isDragSelecting    = false;

        // Capture at ContentGrid level so move/released events arrive even when
        // the cursor leaves PlotCanvas or enters the InfoBox overlay.
        e.Pointer.Capture(_contentGrid);
    }

    // ---- Drag-select rubber-band ------------------------------------

    private void OnDragSelectMoved(object? sender, PointerEventArgs e)
    {
        if (!_maybeDragSelecting || e.Pointer.Captured != _contentGrid) return;

        var pos = e.GetPosition(_plotCanvas);
        double dx = pos.X - _dragSelectOrigin.X;
        double dy = pos.Y - _dragSelectOrigin.Y;

        if (!_isDragSelecting && Math.Sqrt(dx * dx + dy * dy) < DragSelectThreshold) return;

        _isDragSelecting = true;

        var selRect = new Rect(
            Math.Min(_dragSelectOrigin.X, pos.X),
            Math.Min(_dragSelectOrigin.Y, pos.Y),
            Math.Abs(dx),
            Math.Abs(dy));

        _dragSelectOverlay?.SetSelectionRect(selRect);
        Display?.SelectItemsInRect(selRect, _dragSelectAdditive);
    }

    private void OnDragSelectReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_maybeDragSelecting || e.Pointer.Captured != _contentGrid) return;

        e.Pointer.Capture(null);
        _maybeDragSelecting = false;
        _isDragSelecting    = false;
        _dragSelectOverlay?.SetSelectionRect(null);
    }

    // ---- Middle-button pan -----------------------------------------

    private void OnCanvasPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_plotCanvas).Properties.IsMiddleButtonPressed) return;
        if (_canvasPanning) return;   // prevent double-fire from ContentGrid registration
        _canvasPanning = true;
        _canvasPanLast = e.GetPosition(_plotCanvas);
        e.Pointer.Capture(_plotCanvas);
    }

    private void OnCanvasPanMoved(object? sender, PointerEventArgs e)
    {
        if (!_canvasPanning || e.Pointer.Captured != _plotCanvas) return;
        var display = Display;
        if (display is null) return;

        var current = e.GetPosition(_plotCanvas);
        display.ViewOffsetX += current.X - _canvasPanLast.X;
        display.ViewOffsetY += current.Y - _canvasPanLast.Y;
        _canvasPanLast = current;
    }

    private void OnCanvasPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_canvasPanning) return;
        _canvasPanning = false;
        if (e.Pointer.Captured == _plotCanvas)
            e.Pointer.Capture(null);
    }

    // ---- Overlay pass-through wheel (catches scroll from MarkerInfoBoxView) ----

    private void OnContentGridWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;

        // Ctrl+scroll is plot-axis zoom — leave it for the PlotControl to handle.
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                    e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl) return;

        if (_plotCanvas is null) return;
        var pos = e.GetPosition(_plotCanvas);
        if (!new Rect(_plotCanvas.Bounds.Size).Contains(pos)) return;

        OnCanvasWheel(sender, e);
    }

    // ---- Scroll-wheel canvas zoom ----------------------------------

    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        var display = Display;
        if (display is null) return;

        // DataDisplayViewModel.ZoomAtPoint multiplies (factor > 1 zooms in), unlike the other
        // canvases' divide-by-factor convention — so this ternary must be the OPPOSITE of theirs to
        // land on the same on-screen direction. Every other document (schematic, layout, symbol
        // editor, wBond profile) zooms IN on Delta.Y > 0; this canvas used to zoom OUT instead.
        double factor = e.Delta.Y > 0 ? CanvasZoomStep : 1.0 / CanvasZoomStep;
        var    cursor = e.GetPosition(_plotCanvas);
        display.ZoomAtPoint(cursor.X, cursor.Y, factor);

        e.Handled = true;
    }
}
