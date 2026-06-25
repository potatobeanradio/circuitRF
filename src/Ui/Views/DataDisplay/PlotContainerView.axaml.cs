// ================================================================
//  PlotContainerView.axaml.cs  —  Interaction for PlotContainerView
//
//  Left-drag on the plot area moves the container (all selected plots).
//  Left-drag on the resize grip resizes this container.
//  Click (no drag) toggles selection (Ctrl/Cmd for multi-select).
//  Scroll wheel reaches PlotControl for zoom.
// ================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class PlotContainerView : UserControl
{
    private PlotControl _plotControl = null!;
    private Border      _resizeHandle = null!;

    // ---- Move state -------------------------------------------------
    private bool  _mayMove;         // press recorded, waiting for threshold
    private bool  _isMoving;
    private Point _lastRoot;        // last pointer position in root/window coords

    // ---- Resize state -----------------------------------------------
    private bool   _isResizing;
    private double _resizeStartW;   // width  captured at OnResizePressed (for undo)
    private double _resizeStartH;   // height captured at OnResizePressed (for undo)

    private const double DragThreshold = 4.0;

    // ---- Constructor ------------------------------------------------

    public PlotContainerView()
    {
        InitializeComponent();

        _plotControl  = this.FindControl<PlotControl>("MyPlotControl")!;
        _resizeHandle = this.FindControl<Border>("ResizeHandle")!;

        // Subscribe to plot events for state refresh
        _plotControl.PlotChanged         += OnPlotControlChanged;
        _plotControl.MarkerMoved         += OnMarkerMoved;
        _plotControl.DeletePlotRequested += OnDeletePlotRequested;
        _plotControl.MarkerAdded         += OnMarkerAdded;

        // Move handlers on the whole view (bubbled events from PlotControl)
        PointerPressed  += OnViewPointerPressed;
        PointerMoved    += OnViewPointerMoved;
        PointerReleased += OnViewPointerReleased;

        // DoubleTapped is handled here because PlotContainerView captures the
        // pointer on first press, so PlotControl never sees the second tap.
        DoubleTapped += OnViewDoubleTapped;

        // Resize handlers on the grip — these also stop the move handlers firing
        _resizeHandle.PointerPressed  += OnResizePressed;
        _resizeHandle.PointerMoved    += OnResizeMoved;
        _resizeHandle.PointerReleased += OnResizeReleased;

        // Hide the grip until the cursor hovers over it
        _resizeHandle.PointerEntered += (_, _) => _resizeHandle.Opacity = 1.0;
        _resizeHandle.PointerExited  += (_, _) => { if (!_isResizing) _resizeHandle.Opacity = 0.0; };

        // Double-click on resize grip auto-fits Table container width to its column content.
        _resizeHandle.DoubleTapped += OnResizeHandleDoubleTapped;
    }

    // ---- PlotControl events -----------------------------------------

    private void OnPlotControlChanged(object? sender, EventArgs e)
    {
        if (DataContext is PlotContainerViewModel vm)
            vm.OnPlotChanged(sender, e);
    }

    private void OnMarkerMoved(object? sender, EventArgs e)
    {
        // Forward to the container VM which routes to DataDisplayViewModel.
        if (DataContext is PlotContainerViewModel vm)
            vm.OnMarkerMoved();
    }

    private void OnViewDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Pointer capture means e.Source is always PlotContainerView after the first press,
        // so source-based dispatch is unreliable.  Use geometry instead.
        //
        // For Smith / Polar: Y-axis label strips live in the left/right column controls.
        //   Compute which strip column and which strip index was tapped, then route to
        //   the inspector for that trace.
        //
        // For Rect: no external strips — the tap always reaches HandleDoubleTapAt, which
        //   handles per-trace Y label hit-testing inside the canvas margin.

        if (DataContext is PlotContainerViewModel vm)
        {
            var    pos    = e.GetPosition(this);
            double sw     = vm.LabelStripViewWidth;
            double leftW  = vm.LeftLabelStrips.Count * sw;
            double rightX = leftW + vm.ViewWidth;

            if (pos.X < leftW && vm.LeftLabelStrips.Count > 0)
            {
                // FlowDirection = RightToLeft: strip 0 occupies [leftW-sw, leftW] (innermost).
                int i = (int)((leftW - pos.X) / sw);
                i = Math.Clamp(i, 0, vm.LeftLabelStrips.Count - 1);
                _plotControl.ShowPlotInspectorAtTrace(vm.LeftLabelStrips[i].Trace);
                e.Handled = true;
                return;
            }

            if (pos.X >= rightX && vm.RightLabelStrips.Count > 0)
            {
                // Strip 0 occupies [rightX, rightX+sw] (innermost).
                int i = (int)((pos.X - rightX) / sw);
                i = Math.Clamp(i, 0, vm.RightLabelStrips.Count - 1);
                _plotControl.ShowPlotInspectorAtTrace(vm.RightLabelStrips[i].Trace);
                e.Handled = true;
                return;
            }
        }

        _plotControl.HandleDoubleTapAt(e.GetPosition(_plotControl));
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PlotContainerViewModel vm)
        {
            vm.PlotNeedsRedraw                       += (_, _) => _plotControl?.InvalidateVisual();
            _plotControl.NextMarkerIndexProvider      = vm.GetNextMarkerIndex;
            _plotControl.FindMarkerInfoBoxVmProvider  = vm.FindMarkerInfoBoxVm;
            _plotControl.ContainerProvider            = () => DataContext as PlotContainerViewModel;
            _plotControl.SelectedMarkersProvider      = vm.GetSelectedMarkers;
            _plotControl.StepSelectedMarkersHandler   = vm.StepSelectedMarkers;
        }
    }

    // ---- Resize grip double-click: snap Rect to aspect ratio / auto-fit Table ----

    private void OnResizeHandleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PlotContainerViewModel vm) return;
        var plot = vm.PlotVM.Plot;

        // ── Rect: snap height to configured aspect ratio (width stays fixed) ──
        if (plot.PlotType == PlotType.Rect)
        {
            double ratio = AppSettingsViewModel.Instance.RectAspectRatio;
            if (ratio <= 0) return;

            double oldW = vm.Width;
            double oldH = vm.Height;

            vm.ResizeTo(oldW, oldW / ratio);  // clamps at minimum internally

            // Only push an undo entry if the size actually changed.
            if (vm.Width != oldW || vm.Height != oldH)
            {
                var cmd = new ResizePlotCommand(vm, oldW, oldH, vm.Width, vm.Height);
                vm.PushUndoCommand(cmd);
            }

            e.Handled = true;
            return;
        }

        if (plot.PlotType != PlotType.Table) return;

        // ---- Width: fit to content ----
        float totalW = TableRenderer.TotalColumnWidth(plot);

        double dataDisplayW = double.PositiveInfinity;
        Avalonia.Visual? parent = this.GetVisualParent();
        while (parent is not null)
        {
            if (parent is PlotCanvasView ddv) { dataDisplayW = ddv.Bounds.Width; break; }
            parent = parent.GetVisualParent();
        }

        double viewableRight = vm.GetViewableRightEdge(dataDisplayW);
        double maxW    = Math.Max(200, viewableRight - vm.Left);
        double targetW = Math.Min(totalW, maxW);

        // ---- Height: fit the WHOLE table (title band + header + all rows) ----
        // vm.Height is in LOGICAL (pre-zoom) units, so compute the required height in logical units too
        // by asking RequiredCanvasHeight with zoomLevel = 1 (it bakes zoom into the row/band geometry,
        // so zoom=1 yields the unscaled logical height). This is directly comparable to ResizeTo's
        // MinLogicalHeight() floor (also computed at the unscaled font), so a genuine N-row table is no
        // longer wrongly clamped to the 2-row minimum — the earlier divide-by-scale double-removed zoom
        // and produced a sub-minimum target, which the clamp then inflated, leaving extra space below
        // the last row. The renderer re-applies zoom when drawing, so the on-screen fit is exact.
        double reqLogicalH = TableRenderer.RequiredCanvasHeight(plot, 1f);
        double targetH     = reqLogicalH + 1.0;   // +1 logical px so the last row's bottom border never clips

        vm.ResizeTo(targetW, targetH);
        e.Handled = true;
    }

    private void OnDeletePlotRequested(object? sender, EventArgs e)
    {
        if (DataContext is PlotContainerViewModel vm)
            vm.RequestRemoveSelf();
    }

    // Routes the MarkerAdded event from PlotControl to the VM for undo recording.
    private void OnMarkerAdded(Marker marker, Trace trace)
    {
        if (DataContext is PlotContainerViewModel vm)
            vm.OnMarkerAdded(marker, trace);
    }

    // ---- Move: press ------------------------------------------------

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_isResizing) return;

        _mayMove  = true;
        _isMoving = false;
        _lastRoot = GetRootPoint(e);

        // Capture so moved/released events are delivered even if pointer leaves.
        e.Pointer.Capture(this);

        // Mark handled so the canvas does NOT interpret this as an empty-area click.
        e.Handled = true;
    }

    // ---- Move: move -------------------------------------------------

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_mayMove || e.Pointer.Captured != this) return;

        var current = GetRootPoint(e);
        double dx = current.X - _lastRoot.X;
        double dy = current.Y - _lastRoot.Y;

        if (!_isMoving && Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;

        if (DataContext is PlotContainerViewModel vm)
        {
            if (!_isMoving)
            {
                _isMoving = true;
                // Ensure this plot is selected before moving.
                if (!vm.IsSelected) vm.RequestSelectOnly();
                // Snapshot start positions of all selected items for undo.
                vm.BeginMove();
            }

            _lastRoot = current;

            // Convert screen-pixel delta to logical-coordinate delta.
            double zoom = vm.ZoomLevel > 0 ? vm.ZoomLevel : 1.0;
            vm.MoveSelected(dx / zoom, dy / zoom);
        }
    }

    // ---- Move: release ----------------------------------------------

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_mayMove || e.Pointer.Captured != this) return;

        e.Pointer.Capture(null);

        if (DataContext is PlotContainerViewModel vm)
        {
            if (_isMoving)
            {
                // Push an undo command capturing start → end positions.
                vm.EndMove();
            }
            else
            {
                // It was a click → update selection.
                bool isCtrlOrMeta =
                    e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                    e.KeyModifiers.HasFlag(KeyModifiers.Meta);

                if (isCtrlOrMeta) vm.RequestToggleSelect();
                else              vm.RequestSelectOnly();
            }
        }

        _mayMove  = false;
        _isMoving = false;
    }

    // ---- Resize: press ----------------------------------------------

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_resizeHandle).Properties.IsLeftButtonPressed) return;

        _isResizing = true;

        // Capture the current size before the drag starts (for undo).
        if (DataContext is PlotContainerViewModel vm)
        {
            _resizeStartW = vm.Width;
            _resizeStartH = vm.Height;
        }

        e.Pointer.Capture(_resizeHandle);
        e.Handled = true; // prevent move handler from also firing
    }

    // ---- Resize: move -----------------------------------------------
    //
    //  Use the pointer's position relative to THIS container as the
    //  target size.  This means the grip corner always tracks the mouse —
    //  no drift — because we're treating position-within-container as
    //  the desired (width, height) directly.
    //
    //  e.GetPosition(this) is in screen pixels (zoomed).  Divide by
    //  ZoomLevel to convert back to logical/model coordinates.

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizing || e.Pointer.Captured != _resizeHandle) return;
        if (DataContext is not PlotContainerViewModel vm) return;

        var    pt      = e.GetPosition(this);
        double zoom    = vm.ZoomLevel > 0 ? vm.ZoomLevel : 1.0;
        double leftPx  = vm.LeftLabelStrips.Count * vm.LabelStripViewWidth;
        double targetW = (pt.X - leftPx) / zoom;
        double targetH = pt.Y / zoom;

        // Aspect lock applies ONLY to Rect plots, and ONLY when Shift is NOT held (design feedback:
        // Rect resizes at the configured "golden" ratio by default; Shift frees it). Tables and the
        // square-aspect plots (Smith/Polar) are never ratio-locked here — a Table must resize freely in
        // both dimensions, and ResizeTo already keeps Smith/Polar square on its own.
        if (vm.PlotVM.Plot.PlotType == PlotType.Rect && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            double ratio = AppSettingsViewModel.Instance.RectAspectRatio;
            if (ratio > 0) targetH = targetW / ratio;
        }

        vm.ResizeTo(targetW, targetH);
    }

    // ---- Resize: release --------------------------------------------

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizing && DataContext is PlotContainerViewModel vm)
        {
            // Only push if the size actually changed.
            if (vm.Width != _resizeStartW || vm.Height != _resizeStartH)
            {
                var cmd = new ResizePlotCommand(vm, _resizeStartW, _resizeStartH, vm.Width, vm.Height);
                vm.PushUndoCommand(cmd);
            }
        }

        _isResizing = false;
        e.Pointer.Capture(null);
        _resizeHandle.Opacity = 0.0;  // re-hidden; PointerEntered will show it again if cursor stays
    }

    // ---- Helpers ----------------------------------------------------

    private Point GetRootPoint(PointerEventArgs e)
    {
        var root = TopLevel.GetTopLevel(this);
        return root is not null ? e.GetPosition(root) : e.GetPosition(this);
    }
}
