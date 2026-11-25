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
using Avalonia.Threading;
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

    private readonly CardLayer _cardLayer = null!;
    private readonly CardLayer _selectionLayer = null!;

    /// <summary>
    /// The plot's card under the plot and its label strips, or the selection outline over them: both span
    /// <see cref="CircuitRF.Render.DataDisplay.PlotCanvasGeometry.CardSpan"/> — the export's card, measured on the plot
    /// canvas as it is laid out now. Redrawn after every frame of the plot, so it follows its labels.
    /// </summary>
    private sealed class CardLayer(PlotContainerView owner, bool selection) : Control
    {
        public override void Render(Avalonia.Media.DrawingContext context)
        {
            if (owner.DataContext is not PlotContainerViewModel vm) return;
            var plot = vm.PlotVM.Plot;
            if (selection ? !vm.IsSelected : !plot.Card) return;
            var pc = owner._plotControl;
            if (pc.Bounds.Width <= 2 || pc.Bounds.Height <= 2 || pc.TranslatePoint(default, this) is not { } at) return;
            var (cl, cr) = owner.CardSpan(vm, pc.Bounds.Size);
            var rect = new Rect(at.X + cl, at.Y, cr - cl, pc.Bounds.Height);
            double radius = plot.Card ? CircuitRF.Render.DataDisplay.PlotCanvasGeometry.CardRadius(pc.Bounds.Width, pc.Bounds.Height) : 3;
            if (selection)
            {
                var accent = owner.TryFindResource("SystemAccentColor", owner.ActualThemeVariant, out var c) && c is Avalonia.Media.Color col
                    ? col : Avalonia.Media.Colors.DodgerBlue;
                context.DrawRectangle(null, new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(accent), 2),
                                      rect.Deflate(1), radius, radius);
                return;
            }
            var fill = CircuitRF.Render.DataDisplay.CardStyle.Fill(vm.Theme);
            var edge = CircuitRF.Render.DataDisplay.CardStyle.Border(vm.Theme);
            context.DrawRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(fill.Alpha, fill.Red, fill.Green, fill.Blue)),
                                  new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(
                                      Avalonia.Media.Color.FromArgb(edge.Alpha, edge.Red, edge.Green, edge.Blue)), 1),
                                  rect.Deflate(0.5), radius, radius);
        }
    }

    /// <summary>The card's left and right edges on the plot canvas for a canvas of <paramref name="size"/>: what the
    /// card and the selection outline span, what the plot takes the pointer over and where the resize grip sits.</summary>
    private (double Left, double Right) CardSpan(PlotContainerViewModel vm, Size size)
        => CircuitRF.Render.DataDisplay.PlotCanvasGeometry.CardSpan(
            vm.PlotVM.Plot, size.Width, size.Height, vm.LeftLabelStrips.Count, vm.RightLabelStrips.Count,
            vm.LabelStripViewWidth);

    /// <summary>The grip at the card's lower-right corner rather than the canvas's, which on a Rect plot is wider.</summary>
    private void PlaceResizeHandle()
    {
        if (DataContext is not PlotContainerViewModel vm || _plotControl.Bounds.Width <= 2) return;
        var (_, right) = CardSpan(vm, _plotControl.Bounds.Size);
        double inset = Math.Max(0, _plotControl.Bounds.Width - right) + 2;
        if (Math.Abs(_resizeHandle.Margin.Right - inset) > 0.5) _resizeHandle.Margin = new Thickness(0, 0, inset, 2);
    }

    // ---- Constructor ------------------------------------------------

    public PlotContainerView()
    {
        InitializeComponent();

        _plotControl  = this.FindControl<PlotControl>("MyPlotControl")!;
        _resizeHandle = this.FindControl<Border>("ResizeHandle")!;
        _plotControl.ShowPositionLock = true;   // a Data Display plot sits on a canvas it can be moved on
        // The card is drawn here, wide enough for the label strips beside the plot, and the selection outline with it.
        _plotControl.DrawsCard = false;
        // The plot takes the pointer over its card only: a Rect plot's canvas runs past its labels on both sides.
        _plotControl.HitArea = size =>
        {
            if (DataContext is not PlotContainerViewModel vm) return new Rect(size);
            var (l, r) = CardSpan(vm, size);
            l = Math.Clamp(l, 0, size.Width);
            r = Math.Clamp(r, l, size.Width);
            return new Rect(l, 0, r - l, size.Height);
        };
        var grid = this.FindControl<Grid>("RootGrid")!;
        _cardLayer = new CardLayer(this, selection: false);
        _selectionLayer = new CardLayer(this, selection: true);
        Grid.SetColumnSpan(_cardLayer, 3);
        Grid.SetColumnSpan(_selectionLayer, 3);
        grid.Children.Insert(0, _cardLayer);
        grid.Children.Add(_selectionLayer);
        // After the frame, never inside it: Avalonia refuses a visual invalidated during the render pass.
        _plotControl.Rendered += () => Dispatcher.UIThread.Post(() =>
        {
            _cardLayer.InvalidateVisual();
            _selectionLayer.InvalidateVisual();
            PlaceResizeHandle();
        }, DispatcherPriority.Render);

        // Subscribe to plot events for state refresh
        _plotControl.PlotChanged         += OnPlotControlChanged;
        _plotControl.MarkerMoved         += OnMarkerMoved;
        _plotControl.DeletePlotRequested += OnDeletePlotRequested;
        _plotControl.MarkerAdded         += OnMarkerAdded;

        // Move handlers on the whole view (bubbled events from PlotControl)
        PointerPressed  += OnViewPointerPressed;
        PointerMoved    += OnViewPointerMoved;
        PointerReleased += OnViewPointerReleased;
        // A drag whose release never arrives still ends: its undo step is pushed and the selection published.
        PointerCaptureLost += (_, _) =>
        {
            if (!_mayMove) return;
            _mayMove = false;
            if (_isMoving && DataContext is PlotContainerViewModel vm) vm.EndMove();
            _isMoving = false;
        };

        // DoubleTapped is handled here because PlotContainerView captures the
        // pointer on first press, so PlotControl never sees the second tap.
        DoubleTapped += OnViewDoubleTapped;

        // Resize handlers on the grip — these also stop the move handlers firing
        _resizeHandle.PointerPressed  += OnResizePressed;
        _resizeHandle.PointerMoved    += OnResizeMoved;
        _resizeHandle.PointerReleased += OnResizeReleased;

        // Hide the grip until the cursor hovers over it
        _resizeHandle.PointerEntered += (_, _) => { if (!IsPositionLocked) _resizeHandle.Opacity = 1.0; };
        _resizeHandle.PointerExited  += (_, _) => { if (!_isResizing) _resizeHandle.Opacity = 0.0; };

        // Double-click on resize grip auto-fits Table container width to its column content.
        _resizeHandle.DoubleTapped += OnResizeHandleDoubleTapped;

        // R-dd-3 — dragging an .npy from the project tree onto this plot adds it as a dataset
        // and opens the Add Trace picker for it, in one motion.
        Avalonia.Input.DragDrop.SetAllowDrop(this, true);
        AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnDataFileDragOver);
        AddHandler(Avalonia.Input.DragDrop.DropEvent,     OnDataFileDrop);
    }

    private static bool TryGetNpyPayload(DragEventArgs e, out CircuitRF.Ui.DataDisplay.NpyFileDragPayload payload)
    {
        payload = default!;
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(Avalonia.Input.DataFormat.Text) is string text
                && CircuitRF.Ui.DataDisplay.NpyFileDragPayload.TryParse(text, out payload))
                return true;
        return false;
    }

    private void OnDataFileDragOver(object? sender, DragEventArgs e)
    {
        if (TryGetNpyPayload(e, out _))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled     = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDataFileDrop(object? sender, DragEventArgs e)
    {
        if (!TryGetNpyPayload(e, out var payload)) return;
        if (DataContext is not PlotContainerViewModel vm) return;
        e.Handled = true;
        await vm.Inspector.AddDatasetFromDropAsync(payload.AbsolutePath);
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

    // ---- The armed tool's crosshair, over the plot too ----

    private DataDisplayViewModel? _watchedDisplay;

    private void WatchDisplay(DataDisplayViewModel? display)
    {
        if (_watchedDisplay is not null) _watchedDisplay.PropertyChanged -= OnDisplayPropertyChanged;
        _watchedDisplay = display;
        if (_watchedDisplay is not null) _watchedDisplay.PropertyChanged += OnDisplayPropertyChanged;
        _plotControl.ToolArmed = _watchedDisplay?.AnyToolArmed == true;
    }

    private void OnDisplayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DataDisplayViewModel.ZoomBoxArmed) or nameof(DataDisplayViewModel.DrawingTool)
            or nameof(DataDisplayViewModel.MarkerToolArmed))
            _plotControl.ToolArmed = _watchedDisplay?.AnyToolArmed == true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchDisplay((DataContext as PlotContainerViewModel)?.Display);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        WatchDisplay(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) WatchDisplay((DataContext as PlotContainerViewModel)?.Display);
        if (DataContext is PlotContainerViewModel vm)
        {
            vm.PlotNeedsRedraw                       += (_, _) => _plotControl?.InvalidateVisual();
            _plotControl.NextMarkerIndexProvider      = vm.GetNextMarkerIndex;
            _plotControl.FindMarkerInfoBoxVmProvider  = vm.FindMarkerInfoBoxVm;
            _plotControl.ContainerProvider            = () => DataContext as PlotContainerViewModel;
            _plotControl.SelectedMarkersProvider      = vm.GetSelectedMarkers;
            _plotControl.StepSelectedMarkersHandler   = vm.StepSelectedMarkers;
            vm.PropertyChanged += (_, a) =>
            {
                if (a.PropertyName == nameof(PlotContainerViewModel.IsSelected)) _selectionLayer.InvalidateVisual();
            };
        }
    }

    // ---- Resize grip double-click: snap Rect to aspect ratio / auto-fit Table ----

    /// <summary>Lock Position is on: the grip neither shows nor resizes.</summary>
    private bool IsPositionLocked => DataContext is PlotContainerViewModel { IsPositionLocked: true };

    private void OnResizeHandleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PlotContainerViewModel vm || vm.IsPositionLocked) return;
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

    // Routes the MarkerAdded event from PlotControl to the VM for undo recording (which also selects
    // it), then gives the plot the keyboard once the menu that added it has closed, so the arrows
    // step the new marker without a click on it first (R-dfr26-6-4).
    private void OnMarkerAdded(Marker marker, Trace trace)
    {
        if (DataContext is PlotContainerViewModel vm)
            vm.OnMarkerAdded(marker, trace);
        Dispatcher.UIThread.Post(() => _plotControl.Focus(), DispatcherPriority.Background);
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
                // Selected first when it is not (the Properties panel follows when the drag ends), then the start
                // positions of everything selected are taken for undo.
                vm.BeginMoveSelecting();
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

        _mayMove = false;   // first, so the capture-lost below is this release, not a lost drag
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
        if (IsPositionLocked) { e.Handled = true; return; }

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
        // The grip is at the CANVAS's right edge; a rectangular plot's canvas carries its extra Y-label
        // columns outside its own width, in proportion to it (Plot.LabelColumnsOutside).
        var    plotM   = vm.PlotVM.Plot;
        double targetW = (pt.X - leftPx) / zoom
                       / (1.0 + plotM.LeftLabelExtraFraction + plotM.RightLabelExtraFraction);
        double targetH = pt.Y / zoom;

        // Aspect lock applies ONLY to Rect plots, and ONLY when Shift is NOT held (design feedback:
        // Rect resizes at the configured "golden" ratio by default; Shift frees it). Tables and the
        // square-aspect plots (Smith/Polar) are never ratio-locked here — a Table must resize freely in
        // both dimensions, and ResizeTo already keeps Smith/Polar square on its own.
        // The grid, when it is on, takes the far edges (the width first, so a ratio-locked Rect keeps its ratio).
        (targetW, targetH) = vm.SnapResizeTarget(targetW, targetH);

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
