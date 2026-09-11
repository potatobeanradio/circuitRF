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
using CircuitRF.Ui.Controls;
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

    // ---- Zoom-box state (the toolbar's magnifier) ------------------
    // The ARMED flag lives on DataDisplayViewModel (per tab, so switching tabs cannot leave a
    // crosshair over a canvas nobody armed); only the in-flight drag is held here.
    private bool  _zoomBoxDragging;
    private Point _zoomBoxOrigin;

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

        // Zoom box — the press is a TUNNEL handler, unlike everything else here, because an armed
        // magnifier has to pre-empt the CHILDREN: a bubble handler runs after the plot or InfoBox
        // under the pointer has already taken the press as a move or a marker drag, and marking it
        // handled then is too late. Move and release bubble like the pan's, so the box keeps growing
        // when the pointer crosses a plot.
        _contentGrid?.AddHandler(
            PointerPressedEvent, OnZoomBoxPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _contentGrid?.AddHandler(
            PointerMovedEvent, OnZoomBoxMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _contentGrid?.AddHandler(
            PointerReleasedEvent, OnZoomBoxReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Escape reaches the document's own KeyBinding (DeselectAllCommand, which disarms the
        // magnifier first), so it is not handled here. Arrow keys are — see OnCanvasKeyDown.
        AddHandler(KeyDownEvent, OnCanvasKeyDown, RoutingStrategies.Bubble);
    }

    // ---- Zoom box ---------------------------------------------------

    /// <summary>Smallest drag, in device pixels, that counts as a zoom window rather than a stray
    /// click. Below it the press is treated as "I did not mean to" and the view is left alone —
    /// though the tool still disarms, so it is never left on with nothing explaining it.</summary>
    private const double ZoomBoxMinDragPixels = 4.0;

    private void OnZoomBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_plotCanvas is null || Display is not { ZoomBoxArmed: true }) return;
        if (!e.GetCurrentPoint(_plotCanvas).Properties.IsLeftButtonPressed) return;

        Focus();
        _zoomBoxDragging = true;
        _zoomBoxOrigin   = e.GetPosition(_plotCanvas);
        _dragSelectOverlay?.SetSelectionRect(null);
        e.Pointer.Capture(_contentGrid);
        e.Handled = true;
    }

    private void OnZoomBoxMoved(object? sender, PointerEventArgs e)
    {
        if (!_zoomBoxDragging || _plotCanvas is null) return;
        _dragSelectOverlay?.SetSelectionRect(ZoomBoxRect(e.GetPosition(_plotCanvas)));
        e.Handled = true;
    }

    private void OnZoomBoxReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_zoomBoxDragging || _plotCanvas is null) return;

        var box = ZoomBoxRect(e.GetPosition(_plotCanvas));
        _zoomBoxDragging = false;
        _dragSelectOverlay?.SetSelectionRect(null);
        e.Pointer.Capture(null);

        // Disarm FIRST: a box too small to count, or a zoom that clamps against the display's own
        // limits, still ends the gesture. Leaving the tool armed on those paths is how a mode gets
        // stuck with nothing on screen explaining it.
        var display = Display;
        display?.DisarmZoomBox();
        if (display is not null && box.Width >= ZoomBoxMinDragPixels && box.Height >= ZoomBoxMinDragPixels)
            display.ZoomToScreenRect(box.X, box.Y, box.Width, box.Height,
                                     _plotCanvas.Bounds.Width, _plotCanvas.Bounds.Height);

        e.Handled = true;
    }

    private Rect ZoomBoxRect(Point current) => new(_zoomBoxOrigin, current);

    // ---- Arrow-key pan ----------------------------------------------

    /// <summary>
    /// Arrow keys pan the canvas when nothing is selected — see <see cref="CanvasArrowPan"/> for why
    /// the gesture exists and why the selection gates it. With something selected the arrows belong
    /// to the plot or marker that has it (<c>PlotControl</c> steps a marker along its trace with
    /// them), which is why this asks first and declines rather than handling them unconditionally.
    /// </summary>
    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Display is not { } display || display.HasAnySelection) return;
        if (CanvasArrowPan.ScreenStep(e.Key, e.KeyModifiers) is not { } step) return;

        // These offsets translate the CONTENT, so the view moves the other way: Right means "show me
        // what is further right", which slides the plots left.
        display.ViewOffsetX -= step.Dx;
        display.ViewOffsetY -= step.Dy;
        e.Handled = true;
    }

    // The display this view is currently watching for ZoomBoxArmed, so the subscription is dropped
    // when the DataContext moves rather than accumulating one per tab rebuild.
    private DataDisplayViewModel? _watchedDisplay;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Re-register the size getter if the context changes after load.
        if (DataContext is TabViewModel tabVm && _plotCanvas is not null)
            tabVm.GetCanvasSizeFunc = () =>
                (_plotCanvas.Bounds.Width, _plotCanvas.Bounds.Height);

        if (_watchedDisplay is not null) _watchedDisplay.PropertyChanged -= OnDisplayPropertyChanged;
        _watchedDisplay = Display;
        if (_watchedDisplay is not null) _watchedDisplay.PropertyChanged += OnDisplayPropertyChanged;
        SyncZoomBoxCursor();
    }

    private void OnDisplayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataDisplayViewModel.ZoomBoxArmed)) SyncZoomBoxCursor();
    }

    /// <summary>The armed magnifier reads as a crosshair, the same way the schematic, layout and
    /// wBond editors draw theirs — the arming is a mode, and a mode with no pointer change is one the
    /// user discovers by clicking.</summary>
    private void SyncZoomBoxCursor() =>
        Cursor = Display is { ZoomBoxArmed: true } ? new Cursor(StandardCursorType.Cross) : Cursor.Default;

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
