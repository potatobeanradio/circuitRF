// ================================================================
//  MarkerInfoBoxViewModel.cs
//
//  ViewModel for a single draggable MarkerInfoBox overlay.
//  One instance per (Marker, Trace) pair, owned by DataDisplayViewModel.
//
//  Position is stored in DataDisplay logical coordinates — the same
//  coordinate system as PlotContainerViewModel.Left/Top.  This means
//  the info box is independent of which plot it was created from and
//  does not move when a plot container is repositioned.
//
//  ViewLeft / ViewTop (screen pixels in the DataDisplay canvas) are
//  computed on demand: logicalPos * ZoomLevel + ViewOffset.
//  DataDisplayViewModel calls NotifyViewProperties() whenever zoom
//  or pan changes.
//
//  Drag uses window-relative positions so the reference frame is
//  stable regardless of the overlay element's own position.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class MarkerInfoBoxViewModel : ViewModelBase
{
    // ---- Model references -------------------------------------------

    public Marker                  Marker    { get; }
    public Trace                   Trace     { get; }
    public PlotContainerViewModel  Container { get; }

    private readonly Func<FreqUnit>       _getFreqUnit;
    private readonly Func<RenderTheme>    _getTheme;
    private readonly DataDisplayViewModel _parent;

    // ---- Position in DataDisplay logical coordinates ----------------
    //  logical_x * ZoomLevel + ViewOffsetX  →  screen Canvas.Left
    //  logical_y * ZoomLevel + ViewOffsetY  →  screen Canvas.Top

    private double _logicalLeft;
    private double _logicalTop;

    // ---- Screen position (what Canvas.Left/Top bind to) -------------

    public double ViewLeft => _logicalLeft * _parent.ZoomLevel + _parent.ViewOffsetX;
    public double ViewTop  => _logicalTop  * _parent.ZoomLevel + _parent.ViewOffsetY;

    // ---- Logical position (DataDisplay coordinate space, zoom-independent) ----
    //  Read by PlotExporter to compute export-canvas position without needing
    //  the current zoom level or view offset.

    public double LogicalLeft => _logicalLeft;
    public double LogicalTop  => _logicalTop;

    // ---- Control dimensions — stored as logical pixels, exposed as screen pixels ------
    //  BoxWidth/BoxHeight bind to the view's Width/Height.  The view lives on the
    //  DataDisplay canvas alongside plots whose sizes also scale with ZoomLevel, so
    //  the info box must grow/shrink with zoom just as ViewLeft/ViewTop do.
    //  DrawInfoBox derives its font size from the screen-pixel control height rather
    //  than a fixed value, so text stays proportional at any zoom level.

    private double _logicalBoxWidth  = 120;
    private double _logicalBoxHeight =  60;

    public double BoxWidth  => _logicalBoxWidth  * _parent.ZoomLevel;
    public double BoxHeight => _logicalBoxHeight * _parent.ZoomLevel;

    // ---- Drag state -------------------------------------------------

    private bool   _isDragging;
    private Point  _dragStartWindowPt;
    private double _logicalLeftAtDragStart;
    private double _logicalTopAtDragStart;

    // ---- Constructor ------------------------------------------------

    public MarkerInfoBoxViewModel(
        Marker                 marker,
        Trace                  trace,
        PlotContainerViewModel container,
        Func<FreqUnit>         getFreqUnit,
        Func<RenderTheme>      getTheme,
        DataDisplayViewModel   parent,
        double                 logicalLeft,
        double                 logicalTop)
    {
        Marker       = marker;
        Trace        = trace;
        Container    = container;
        _getFreqUnit = getFreqUnit;
        _getTheme    = getTheme;
        _parent      = parent;
        _logicalLeft = logicalLeft;
        _logicalTop  = logicalTop;

        RefreshSize();
    }

    // ---- Called when marker data changes (symbol dragged) -----------

    public void OnMarkerMoved()
    {
        RefreshSize();
        OnPropertyChanged(nameof(NeedsRedraw));
        // Redraw the PlotControl so the marker glyph moves to the new frequency position.
        Container.RequestPlotRedraw();
    }

    // Triggers a Skia repaint without a size refresh (e.g., theme change).
    public void RequestRedraw() => OnPropertyChanged(nameof(NeedsRedraw));

    // Called by DataDisplayViewModel when the library entry count changes so that
    // ShowFilePrefix is re-evaluated and the box text/size updates accordingly.
    public void OnLibraryEntryCountChanged()
    {
        RefreshSize();
        RequestRedraw();
    }

    // Dummy property — PropertyChanged on this triggers InvalidateVisual in the view.
    public int NeedsRedraw { get; private set; }

    // ---- Marker removal -------------------------------------------------

    public void RemoveMarker()
    {
        // Route through the parent so the removal is recorded on the undo stack.
        _parent.RemoveMarkerWithUndo(Marker, Trace, Container);
    }

    // ---- Change to a different trace ------------------------------------

    public void ChangeToTrace(Trace newTrace)
    {
        Trace.Markers.Remove(Marker);
        var moved = new Marker(newTrace, Marker.Freq, Marker.IsMulti, Marker.IsDelta,
                               Marker.Index, Marker.FreqUnits)
        {
            Name                   = Marker.Name,
            Style                  = Marker.Style,
            MatrixFormat           = Marker.MatrixFormat,
            MaximumFractionDigits  = Marker.MaximumFractionDigits,
            UseNormalizedImpedance = Marker.UseNormalizedImpedance,
            FormatString           = Marker.FormatString,
            InfoBoxPos             = Marker.InfoBoxPos,
        };
        // Keep the marker's x-position as close as possible to where it was on the old trace,
        // so the user can track where it landed. For network (freq-swept) traces x == frequency,
        // so snap to the new trace's nearest available frequency to the old marker's Freq.
        if (!newTrace.IsCubeBound && newTrace.Data?.Frequencies is { Length: > 0 } newFreqs)
        {
            double best = newFreqs[0], bestDiff = Math.Abs(Marker.Freq - newFreqs[0]);
            for (int i = 1; i < newFreqs.Length; i++)
            {
                double d = Math.Abs(Marker.Freq - newFreqs[i]);
                if (d < bestDiff) { bestDiff = d; best = newFreqs[i]; }
            }
            moved.Freq = best;
        }
        newTrace.Markers.Add(moved);
        if (newTrace.IsStabilityCircle)
        {
            // temporarily set the current PositionStatic to center so SnapMarkerToStabilityCircle will
            // put the marker glyph on the circle's point closest to center of Smith Chart
            int fi = Array.FindIndex(newTrace.Data!.Frequencies, f => f >= moved.Freq - 1e-6);
            if (fi < 0) fi = newTrace.Data!.Frequencies.Length - 1;
            moved.PositionStatic = new System.Numerics.Vector2(0,0);
            newTrace.SnapMarkerToStabilityCircle(moved, fi);
        }
        Container.RequestPlotRedraw();
        _parent.OnContainerPlotChanged(Container);
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

    public void EndDrag()
    {
        if (!_isDragging) return;
        _isDragging        = false;
        Marker.InfoBoxPos  = new Point(_logicalLeft, _logicalTop);
    }

    /// <summary>
    /// Directly sets the logical position and syncs Marker.InfoBoxPos.
    /// Called by <see cref="MovePlotsCommand"/> during undo/redo of a move.
    /// </summary>
    internal void SetLogicalPosition(double left, double top)
    {
        _logicalLeft      = left;
        _logicalTop       = top;
        Marker.InfoBoxPos = new Point(left, top);
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
    }

    // ---- Helpers ----------------------------------------------------

    // True when the file prefix should appear in the marker info box and context menu.
    // Always true when AlwaysDisplayDataSourcePrefix is set in Settings; otherwise
    // mirrors the library-count heuristic (multiple loaded SNPs).
    public bool ShowFilePrefix =>
        AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
            (_parent.Library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);

    /// <summary>All traces in the same plot except the one that owns this marker.</summary>
    public IReadOnlyList<Trace> OtherTraces =>
        Container.PlotVM.Plot.Traces.Where(t => t != Trace).ToList();

    private void RefreshSize()
    {
        var otherTraces = Marker.IsMulti ? OtherTraces : null;
        var (w, h) = MarkerRenderer.MeasureInfoBox(Marker, Trace, _getFreqUnit(), ShowFilePrefix, otherTraces);
        _logicalBoxWidth  = w;
        _logicalBoxHeight = h;
        OnPropertyChanged(nameof(BoxWidth));
        OnPropertyChanged(nameof(BoxHeight));
    }

    public FreqUnit    FreqUnit => _getFreqUnit();
    public RenderTheme Theme    => _getTheme();
    public PlotType    PlotType => Container.PlotVM.Plot.PlotType;

    // ---- Selection --------------------------------------------------
    //  IsSelected is a true observable property so that [ObservableProperty]
    //  fires PropertyChanged automatically and the view's InvalidateVisual
    //  subscription fires on every change.

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;

    // When selection state changes, the marker symbol in the PlotControl must also redraw.
    partial void OnIsSelectedChanged(bool value) => Container.RequestPlotRedraw();

    /// <summary>Selects only this InfoBox (regular click — clears everything else).</summary>
    public void SelectOnly()    => _parent.SelectOnly(this);

    /// <summary>Toggles this InfoBox in the selection (Ctrl+click — leaves other items alone).</summary>
    public void ToggleSelect()  => _parent.ToggleSelect(this);

    // ---- Coordinated move (plot-drag path) --------------------------

    /// <summary>
    /// Moves the info box by a logical-coordinate delta.  Called by
    /// DataDisplayViewModel.MoveSelected when a plot drag also carries
    /// selected InfoBoxes.
    /// </summary>
    public void TranslateLogical(double dx, double dy)
    {
        _logicalLeft += dx;
        _logicalTop  += dy;
        Marker.InfoBoxPos = new Point(_logicalLeft, _logicalTop);
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
    }

    // ---- InfoBox group drag (absolute window-position path) ---------

    public void StartGroupDrag(Point windowPt)  => _parent.StartInfoBoxGroupDrag(windowPt);
    public void UpdateGroupDrag(Point windowPt)  => _parent.UpdateInfoBoxGroupDrag(windowPt);
    public void EndGroupDrag()                   => _parent.EndInfoBoxGroupDrag();
}
