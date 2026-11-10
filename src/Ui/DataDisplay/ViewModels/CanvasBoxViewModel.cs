// ================================================================
//  CanvasBoxViewModel.cs
//
//  What every box that floats on the Data Display canvas shares — a
//  marker info box and a floating legend: a position in DataDisplay
//  logical coordinates (the space PlotContainerViewModel.Left/Top are
//  in), a size in logical units, the screen position and size derived
//  from them with the zoom and pan, selection, and the drag.
//
//  The position is independent of the plot the box belongs to: moving
//  a plot does not move its boxes unless they are selected with it.
//
//  Drag uses window-relative positions so the reference frame is
//  stable regardless of the overlay element's own position.
// ================================================================

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public abstract partial class CanvasBoxViewModel : ViewModelBase
{
    public PlotContainerViewModel Container { get; }

    protected readonly DataDisplayViewModel _parent;

    // ---- Position in DataDisplay logical coordinates ----------------
    //  logical_x * ZoomLevel + ViewOffsetX  →  screen Canvas.Left
    //  logical_y * ZoomLevel + ViewOffsetY  →  screen Canvas.Top

    private double _logicalLeft;
    private double _logicalTop;

    protected CanvasBoxViewModel(PlotContainerViewModel container, DataDisplayViewModel parent,
                                 double logicalLeft, double logicalTop)
    {
        Container    = container;
        _parent      = parent;
        _logicalLeft = logicalLeft;
        _logicalTop  = logicalTop;
    }

    /// <summary>Writes a settled position back to the model the box belongs to.</summary>
    protected abstract void CommitPosition(double left, double top);

    // ---- Screen position (what Canvas.Left/Top bind to) -------------

    public double ViewLeft => _logicalLeft * _parent.ZoomLevel + _parent.ViewOffsetX;
    public double ViewTop  => _logicalTop  * _parent.ZoomLevel + _parent.ViewOffsetY;

    // ---- Logical position (DataDisplay coordinate space, zoom-independent) ----
    //  Read by PlotExporter to compute export-canvas position without needing
    //  the current zoom level or view offset.

    public double LogicalLeft => _logicalLeft;
    public double LogicalTop  => _logicalTop;

    // ---- Control dimensions — stored as logical pixels, exposed as screen pixels ------
    //  BoxWidth/BoxHeight bind to the view's Width/Height. The view lives on the
    //  DataDisplay canvas alongside plots whose sizes also scale with ZoomLevel, so
    //  the box must grow/shrink with zoom just as ViewLeft/ViewTop do. Each box derives
    //  its text size from the screen-pixel control height rather than a fixed value,
    //  so text stays proportional at any zoom level.

    private double _logicalBoxWidth  = 120;
    private double _logicalBoxHeight =  60;

    public double BoxWidth  => _logicalBoxWidth  * _parent.ZoomLevel;
    public double BoxHeight => _logicalBoxHeight * _parent.ZoomLevel;

    protected void SetLogicalSize(double width, double height)
    {
        _logicalBoxWidth  = width;
        _logicalBoxHeight = height;
        OnPropertyChanged(nameof(BoxWidth));
        OnPropertyChanged(nameof(BoxHeight));
    }

    // ---- Called by DataDisplayViewModel when zoom/pan changes -------

    public void NotifyViewProperties()
    {
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
        OnPropertyChanged(nameof(BoxWidth));
        OnPropertyChanged(nameof(BoxHeight));
    }

    // ---- Drag: window-relative positions, delta converted to logical ----

    private bool   _isDragging;
    private Point  _dragStartWindowPt;
    private double _logicalLeftAtDragStart;
    private double _logicalTopAtDragStart;

    public void StartDrag(Point windowPt)
    {
        _isDragging             = true;
        _dragStartWindowPt      = windowPt;
        _logicalLeftAtDragStart = _logicalLeft;
        _logicalTopAtDragStart  = _logicalTop;
    }

    public void UpdateDrag(Point windowPt)
    {
        if (!_isDragging) return;
        double zoom  = _parent.ZoomLevel > 0 ? _parent.ZoomLevel : 1.0;
        _logicalLeft = _logicalLeftAtDragStart + (windowPt.X - _dragStartWindowPt.X) / zoom;
        _logicalTop  = _logicalTopAtDragStart  + (windowPt.Y - _dragStartWindowPt.Y) / zoom;
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
    }

    /// <summary>True between <see cref="StartDrag"/> and <see cref="EndDrag"/>.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>Shifts the box during a drag — the grid's correction, applied after the pointer has placed it.</summary>
    internal void OffsetDrag(double dx, double dy)
    {
        if (!_isDragging) return;
        _logicalLeft += dx;
        _logicalTop  += dy;
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
    }

    public void EndDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        CommitPosition(_logicalLeft, _logicalTop);
    }

    /// <summary>
    /// Directly sets the logical position and writes it back to the model.
    /// Called by <see cref="MovePlotsCommand"/> during undo/redo of a move.
    /// </summary>
    internal void SetLogicalPosition(double left, double top)
    {
        _logicalLeft = left;
        _logicalTop  = top;
        CommitPosition(left, top);
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
    }

    // ---- Coordinated move (plot-drag path) --------------------------

    /// <summary>
    /// Moves the box by a logical-coordinate delta. Called by
    /// DataDisplayViewModel.MoveSelected when a plot drag also carries
    /// selected boxes.
    /// </summary>
    public void TranslateLogical(double dx, double dy) => SetLogicalPosition(_logicalLeft + dx, _logicalTop + dy);

    // ---- Selection --------------------------------------------------
    //  IsSelected is a true observable property so that [ObservableProperty]
    //  fires PropertyChanged automatically and the view's InvalidateVisual
    //  subscription fires on every change.

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged();

    /// <summary>What else redraws when this box's selection changes.</summary>
    protected virtual void OnSelectionChanged() { }

    /// <summary>Selects only this box (regular click — clears everything else).</summary>
    public void SelectOnly()    => _parent.SelectOnly(this);

    /// <summary>Toggles this box in the selection (Ctrl+click — leaves other items alone).</summary>
    public void ToggleSelect()  => _parent.ToggleSelect(this);

    // ---- Group drag (absolute window-position path) -----------------

    public void StartGroupDrag(Point windowPt)  => _parent.StartInfoBoxGroupDrag(windowPt, this);
    public void UpdateGroupDrag(Point windowPt)  => _parent.UpdateInfoBoxGroupDrag(windowPt);
    public void EndGroupDrag()                   => _parent.EndInfoBoxGroupDrag();
}
