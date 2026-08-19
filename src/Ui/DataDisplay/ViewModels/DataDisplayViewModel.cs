// ================================================================
//  DataDisplayViewModel.cs
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using RfCore;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataDisplayViewModel : ViewModelBase
{
    // ---- Undo / Redo ------------------------------------------------
    // One manager per tab — each tab has its own independent undo history.

    public UndoRedoManager UndoRedo { get; } = new();

    // ---- ContentChanged — fired when the saved config may have changed ----
    // Consumers (DisplayWindowViewModel) recompute HasUnsavedChanges() on this.
    // Two channels drive it: undo edits (via UndoRedo.StateChanged) and
    // inspector/redraw edits (via container PlotNeedsRedraw).

    public event EventHandler? ContentChanged;

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
    private void OnContainerRedraw(object? s, EventArgs e) => RaiseContentChanged();

    private void OnPlotsCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (PlotContainerViewModel c in e.NewItems) c.PlotNeedsRedraw += OnContainerRedraw;
        if (e.OldItems is not null)
            foreach (PlotContainerViewModel c in e.OldItems) c.PlotNeedsRedraw -= OnContainerRedraw;
        RaiseContentChanged();
    }

    // ---- Collections ------------------------------------------------

    private readonly ObservableCollection<PlotContainerViewModel> _plots = new();
    public  ObservableCollection<PlotContainerViewModel> Plots => _plots;

    private readonly ObservableCollection<MarkerInfoBoxViewModel> _markerInfoBoxes = new();
    public  ObservableCollection<MarkerInfoBoxViewModel> MarkerInfoBoxes => _markerInfoBoxes;

    // ---- Selection --------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveInspector))]
    [NotifyPropertyChangedFor(nameof(HasSingleSelection))]
    private PlotContainerViewModel? _singleSelectedPlot;

    public PlotInspectorViewModel? ActiveInspector    => SingleSelectedPlot?.Inspector;
    public bool                    HasSingleSelection => SingleSelectedPlot is not null;

    /// <summary>
    /// True when any plot container OR any marker info box is currently selected.
    /// Used to gate the Delete command and similar multi-select operations.
    /// </summary>
    public bool HasAnySelection =>
        _plots.Any(p => p.IsSelected) || _markerInfoBoxes.Any(m => m.IsSelected);

    /// <summary>True when at least one marker InfoBox is selected.</summary>
    public bool HasSelectedInfoBoxes => _markerInfoBoxes.Any(m => m.IsSelected);


    // ---- Zoom / view offset -----------------------------------------

    private double _zoomLevel   = 1.0;
    private double _viewOffsetX = 0.0;
    private double _viewOffsetY = 0.0;

    public double ZoomLevel
    {
        get => _zoomLevel;
        set { if (SetProperty(ref _zoomLevel, value)) PropagateViewProperties(); }
    }

    public double ViewOffsetX
    {
        get => _viewOffsetX;
        set { if (SetProperty(ref _viewOffsetX, value)) PropagateViewProperties(); }
    }

    public double ViewOffsetY
    {
        get => _viewOffsetY;
        set { if (SetProperty(ref _viewOffsetY, value)) PropagateViewProperties(); }
    }

    private void PropagateViewProperties()
    {
        foreach (var p in _plots)           p.NotifyViewProperties();
        foreach (var m in _markerInfoBoxes) m.NotifyViewProperties();
    }

    /// <summary>
    /// Returns the DataDisplay canvas size in screen pixels (W, H). Injected by
    /// DisplayWindowViewModel (which owns the view's GetCanvasSize action) so new-plot
    /// placement can reason about the visible viewport. Null when not wired (tests / headless);
    /// placement then falls back to a simple cascade.
    /// </summary>
    public Func<(double W, double H)>? CanvasSizeProvider { get; set; }

    // ---- Marker info-box management ---------------------------------

    /// <summary>
    /// Called by PlotContainerViewModel when its PlotControl fires PlotChanged
    /// (marker added/removed, trace changed, etc.).  Rebuilds the overlay info
    /// boxes that belong to <paramref name="container"/>.
    /// </summary>
    public void OnContainerPlotChanged(PlotContainerViewModel container)
        => RebuildMarkerInfoBoxesForContainer(container);

    /// <summary>
    /// Targeted show/hide of a single marker's InfoBox VM, without tearing down and
    /// recreating every InfoBox in the container (which a full rebuild does). Used by the
    /// ShowInfoBox toggle so an open MarkerEditor flyout — bound to a specific
    /// MarkerInfoBoxViewModel — is not orphaned/dismissed when the toggle fires.
    /// Adds the box when newly shown, removes it when hidden; the marker glyph itself is
    /// always drawn by PlotRenderer regardless.
    /// </summary>
    public void SetMarkerInfoBoxVisibility(Marker marker, Trace trace, PlotContainerViewModel container)
    {
        var existing = _markerInfoBoxes.FirstOrDefault(
            m => m.Container == container && ReferenceEquals(m.Marker, marker));

        if (marker.ShowInfoBox)
        {
            if (existing is not null) return;   // already shown
            var plot = container.PlotVM.Plot;
            if (double.IsNaN(marker.InfoBoxPos.X))
                PlaceInfoBoxInLogicalCoords(marker, trace, plot, container);
            _markerInfoBoxes.Add(new MarkerInfoBoxViewModel(
                marker, trace, container,
                () => plot.FreqUnits,
                () => Theme,
                this,
                marker.InfoBoxPos.X,
                marker.InfoBoxPos.Y));
        }
        else if (existing is not null)
        {
            _markerInfoBoxes.Remove(existing);
            RefreshSelection();
        }
    }

    /// <summary>
    /// Called by PlotContainerViewModel when PlotControl.MarkerMoved fires
    /// (user dragged a marker symbol to a new frequency).  Refreshes the text
    /// in existing info boxes without a full rebuild.
    /// </summary>
    public void OnContainerMarkerMoved(PlotContainerViewModel container)
    {
        foreach (var vm in _markerInfoBoxes)
            if (vm.Container == container)
                vm.OnMarkerMoved();
    }

    /// <summary>
    /// Returns the live InfoBox VM for <paramref name="marker"/> if one exists (ShowInfoBox=true),
    /// otherwise builds a transient VM that is NOT added to the MarkerInfoBoxes collection.
    /// Used by the marker context menu's "Edit Properties" action so the editor flyout can open
    /// even when the marker's info box is hidden (the editor only needs the VM as a model wrapper).
    /// </summary>
    public MarkerInfoBoxViewModel GetOrCreateInfoBoxVm(
        Marker marker, Trace trace, PlotContainerViewModel container)
    {
        var existing = _markerInfoBoxes.FirstOrDefault(
            m => m.Container == container && ReferenceEquals(m.Marker, marker));
        if (existing is not null) return existing;

        var plot = container.PlotVM.Plot;
        double left = double.IsNaN(marker.InfoBoxPos.X) ? container.Left : marker.InfoBoxPos.X;
        double top  = double.IsNaN(marker.InfoBoxPos.Y) ? container.Top  : marker.InfoBoxPos.Y;
        return new MarkerInfoBoxViewModel(
            marker, trace, container,
            () => plot.FreqUnits,
            () => Theme,
            this,
            left, top);
    }

    private void RebuildMarkerInfoBoxesForContainer(PlotContainerViewModel container)
    {
        // Capture which markers were selected before the rebuild so we can restore
        // selection on the new VMs.  This keeps the selection intact when, e.g.,
        // a marker drag ends and PlotChanged triggers a rebuild.
        var previouslySelected = _markerInfoBoxes
            .Where(m => m.Container == container && m.IsSelected)
            .Select(m => m.Marker)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        // Remove existing boxes for this container (iterate backwards for safe removal).
        for (int i = _markerInfoBoxes.Count - 1; i >= 0; i--)
            if (_markerInfoBoxes[i].Container == container)
                _markerInfoBoxes.RemoveAt(i);

        var plot = container.PlotVM.Plot;

        // Pass 1: ensure EVERY shown marker has a finite InfoBoxPos before any VM is
        // created. Restoring selection below sets IsSelected=true, whose setter fires a
        // RequestPlotRedraw → dirty-check → JSON serialization of ALL markers. If any
        // not-yet-processed marker still held its placeholder NaN InfoBoxPos, that
        // mid-rebuild serialization would throw (System.Text.Json rejects NaN/∞). Placing
        // all positions first guarantees the graph is finite whenever the dirty-check runs.
        foreach (var trace in plot.Traces)
            foreach (var marker in trace.Markers)
                if (marker.ShowInfoBox && double.IsNaN(marker.InfoBoxPos.X))
                    PlaceInfoBoxInLogicalCoords(marker, trace, plot, container);

        // Pass 2: build the VMs and restore selection (now safe to fire the dirty-check).
        foreach (var trace in plot.Traces)
        {
            foreach (var marker in trace.Markers)
            {
                if (!marker.ShowInfoBox) continue;   // glyph still drawn by PlotRenderer

                var vm = new MarkerInfoBoxViewModel(
                    marker, trace, container,
                    () => plot.FreqUnits,
                    () => Theme,
                    this,
                    marker.InfoBoxPos.X,
                    marker.InfoBoxPos.Y);

                // Restore selection for markers that were selected before the rebuild.
                // IsSelected setter triggers OnIsSelectedChanged → RequestPlotRedraw,
                // which is harmless here since the plot is already being invalidated.
                if (previouslySelected.Contains(marker))
                    vm.IsSelected = true;

                _markerInfoBoxes.Add(vm);
            }
        }
    }

    /// <summary>
    /// Auto-places a marker info box near the marker symbol, converting
    /// from PlotControl screen pixels to DataDisplay logical coordinates.
    /// </summary>
    private void PlaceInfoBoxInLogicalCoords(
        Marker marker, Trace trace, Plot plot, PlotContainerViewModel container)
    {
        // PlotControl fills the container, so its size in screen pixels is ViewWidth × ViewHeight.
        double cW = container.ViewWidth  > 0 ? container.ViewWidth  : container.Width;
        double cH = container.ViewHeight > 0 ? container.ViewHeight : container.Height;

        var tf = PlotRenderer.BuildTransforms(plot, (cW, cH));
        var dl = trace.GetMarkerDataLocation(marker);
        var px = tf.ToCanvas(dl.X, dl.Y, trace.UseSecondaryAxis);

        // Offset 15/10 px from symbol, clamped inside the container bounds.
        double sxPx = Math.Clamp((double)px.X + 15, 0, Math.Max(0, cW - 80));
        double syPx = Math.Clamp((double)px.Y + 10, 0, Math.Max(0, cH - 50));

        // Convert PlotControl-local screen pixels → DataDisplay logical coordinates:
        //   DataDisplay screen X = container.ViewLeft + sxPx
        //                        = container.Left * zoom + offsetX + sxPx
        //   DataDisplay logical X = (screen X − offsetX) / zoom
        //                        = container.Left + sxPx / zoom
        double zoom = _zoomLevel > 0 ? _zoomLevel : 1.0;
        marker.InfoBoxPos = new Point(
            container.Left + sxPx / zoom,
            container.Top  + syPx / zoom);
    }

    private void RemoveMarkerInfoBoxesForContainer(PlotContainerViewModel container)
    {
        for (int i = _markerInfoBoxes.Count - 1; i >= 0; i--)
            if (_markerInfoBoxes[i].Container == container)
                _markerInfoBoxes.RemoveAt(i);
    }

    /// <summary>
    /// Returns the lowest positive m-number not already in use by any marker
    /// on any trace across the entire data display.
    /// </summary>
    public int GetNextMarkerIndex()
    {
        var used = new System.Collections.Generic.HashSet<int>();
        foreach (var c in _plots)
            foreach (var t in c.PlotVM.Plot.Traces)
                foreach (var m in t.Markers)
                    if (m.Name.StartsWith("m", StringComparison.Ordinal)
                        && int.TryParse(m.Name.AsSpan(1), out int n))
                        used.Add(n);

        int idx = 1;
        while (used.Contains(idx)) idx++;
        return idx;
    }

    // ---- Shared state propagated to all containers ------------------

    private RenderTheme          _theme   = RenderTheme.Light;
    private DataSourceLibraryViewModel? _library;

    public RenderTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                foreach (var p in _plots)           p.Theme = value;
                foreach (var m in _markerInfoBoxes) m.RequestRedraw();
            }
        }
    }

    public DataSourceLibraryViewModel? Library
    {
        get => _library;
        set
        {
            if (SetProperty(ref _library, value))
                foreach (var p in _plots) p.Library = value;
        }
    }

    // ---- Z-order counter --------------------------------------------

    private int _nextZIndex = 1;

    // ---- Internal helpers called by UndoCommands --------------------
    // These keep undo/redo logic centralised and avoid duplicating the
    // side-effect chain (info-box rebuild, selection refresh, etc.).

    /// <summary>Adds a container to the display and rebuilds its info boxes.</summary>
    internal void InternalAddContainer(PlotContainerViewModel container, bool selectIt)
    {
        _plots.Add(container);
        RebuildMarkerInfoBoxesForContainer(container);
        if (selectIt) SelectOnly(container);
        else          RefreshSelection();
    }

    /// <summary>Removes a container and its info boxes from the display.</summary>
    internal void InternalRemoveContainer(PlotContainerViewModel container)
    {
        RemoveMarkerInfoBoxesForContainer(container);
        _plots.Remove(container);
        RefreshSelection();
    }

    /// <summary>Adds a marker to its trace and rebuilds the container's info boxes.</summary>
    internal void InternalAddMarker(Marker marker, Trace trace, PlotContainerViewModel container)
    {
        trace.Markers.Add(marker);
        container.OnPlotChanged(null, EventArgs.Empty);
        container.RequestPlotRedraw();
    }

    /// <summary>Removes a marker from its trace and rebuilds the container's info boxes.</summary>
    internal void InternalRemoveMarker(Marker marker, Trace trace, PlotContainerViewModel container)
    {
        trace.Markers.Remove(marker);
        container.OnPlotChanged(null, EventArgs.Empty);
        container.RequestPlotRedraw();
    }

    // ---- Move-operation state (for undo of plot/InfoBox drags) ------
    // BeginMoveOperation captures the pre-drag positions of all selected items.
    // EndMoveOperation creates and pushes a MovePlotsCommand with start+end pairs.

    private List<(PlotContainerViewModel Vm, double Left, double Top)>?   _moveStartPlots;
    private List<(MarkerInfoBoxViewModel  Vm, double Left, double Top)>?   _moveStartInfoBoxes;

    /// <summary>
    /// Called at the start of a move gesture (threshold first crossed).
    /// Snapshots the current logical positions of every selected item.
    /// </summary>
    internal void BeginMoveOperation()
    {
        _moveStartPlots    = _plots.Where(p => p.IsSelected)
                                   .Select(p => (p, p.Left, p.Top)).ToList();
        _moveStartInfoBoxes = _markerInfoBoxes.Where(m => m.IsSelected)
                                               .Select(m => (m, m.LogicalLeft, m.LogicalTop)).ToList();
    }

    /// <summary>
    /// Called when the move gesture ends.  Creates a MovePlotsCommand
    /// from the snapshots captured in BeginMoveOperation and the
    /// current (end) positions, then pushes it onto the undo stack.
    /// </summary>
    internal void EndMoveOperation()
    {
        if (_moveStartPlots is null && _moveStartInfoBoxes is null) return;

        var startPlots    = _moveStartPlots    ?? new List<(PlotContainerViewModel, double, double)>();
        var startInfoBoxes = _moveStartInfoBoxes ?? new List<(MarkerInfoBoxViewModel, double, double)>();

        var plotSnaps = startPlots.Select(s => new PlotMoveSnapshot(
            s.Vm, s.Left, s.Top, s.Vm.Left, s.Vm.Top)).ToList();

        var ibSnaps = startInfoBoxes.Select(s => new InfoBoxMoveSnapshot(
            s.Vm, s.Left, s.Top, s.Vm.LogicalLeft, s.Vm.LogicalTop)).ToList();

        bool anyMoved = plotSnaps.Any(s => s.StartLeft != s.EndLeft || s.StartTop != s.EndTop)
                     || ibSnaps.Any(s => s.StartLogLeft != s.EndLogLeft || s.StartLogTop != s.EndLogTop);

        if (anyMoved)
            UndoRedo.Push(new MovePlotsCommand(plotSnaps, ibSnaps));

        _moveStartPlots     = null;
        _moveStartInfoBoxes = null;
    }

    // ---- Marker add recorded from PlotControl -----------------------

    /// <summary>
    /// Called by PlotContainerViewModel when PlotControl fires its MarkerAdded event.
    /// Pushes an undo command for the marker that was just added to the trace.
    /// </summary>
    internal void RecordMarkerAdded(Marker marker, Trace trace, PlotContainerViewModel container)
        => UndoRedo.Push(new AddMarkerCommand(marker, trace, container, this));

    /// <summary>
    /// Removes a marker with undo/redo support.  Called by MarkerInfoBoxViewModel
    /// and PlotContainerViewModel so marker removal is always reversible.
    /// </summary>
    internal void RemoveMarkerWithUndo(Marker marker, Trace trace, PlotContainerViewModel container)
        => UndoRedo.Do(new RemoveMarkerCommand(marker, trace, container, this));

    // ---- Constructor ------------------------------------------------

    public DataDisplayViewModel(DataSourceLibraryViewModel library, bool addEmptyPlot = true, bool selectEmptyPlot = true)
    {
        _library = library;
        library.LibraryChanged            += (_, _) => OnLibraryEntryCountChanged();
        library.Entries.CollectionChanged += (_, _) => OnLibraryEntryCountChanged();
        library.SelectedDataSourceChanged += OnSelectedDataSourceChanged;

        // Respond immediately to settings changes that require a visual refresh.
        AppSettingsViewModel.Instance.PropertyChanged += OnSettingsPropertyChanged;

        _plots.CollectionChanged += OnPlotsCollectionChanged;
        UndoRedo.StateChanged    += (_, _) => RaiseContentChanged();

        if (addEmptyPlot)
            AddPlot(PlotType.Smith, FreqUnit.GHz);
            if (!selectEmptyPlot)
                SelectOnly((PlotContainerViewModel?) null);
    }

    private void OnLibraryEntryCountChanged()
    {
        foreach (var m in _markerInfoBoxes)
            m.OnLibraryEntryCountChanged();
    }

    private async void OnSelectedDataSourceChanged(object? s, EventArgs e)
    {
        if (_library is null) return;
        // Re-point all sentinel traces to the newly-selected datasource, then rebuild via the same
        // path a trace-expression commit uses (Inspector.RebuildAndNotify). Cube traces go through
        // TrySetCubeData ONLY — never BuildPath, which would rebuild Points from the trace's stale
        // cube buffer and resurrect the previous source's curve (the reported bug). Network traces
        // get their SNP re-pointed to the new source (or a broken SNP) so they redraw the new data
        // or fall blank. RebuildAndNotify also autoscales, refreshes trace cards, and redraws.
        foreach (var c in _plots)
        {
            foreach (var t in c.PlotVM.Plot.Traces)
            {
                if (string.IsNullOrEmpty(t.SourceRef) || t.SourceRef == DataSourceRef.Selected)
                {
                    t.SourcePath = _library.ResolveAbs(t.SourceRef);
                    if (!t.IsCubeBound)
                        t.Data = _library.SelectedEntry?.Snp ?? SNP.CreateBroken(t.SourcePath ?? "");
                }
            }
            c.Inspector.RebuildAndNotify();
            c.UpdateLabelStrips();
        }
        RaiseContentChanged();
        await Task.CompletedTask;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettingsViewModel.AlwaysDisplayDataSourcePrefix):
                // Rebuild label strips so the prefix toggle takes effect on Y-axis labels.
                // Also request a plot redraw so Rect Y-axis labels (inside the canvas) update.
                foreach (var c in _plots)
                {
                    c.UpdateLabelStrips();
                    c.RequestPlotRedraw();
                }
                foreach (var m in _markerInfoBoxes)
                    m.OnLibraryEntryCountChanged();
                break;

            case nameof(AppSettingsViewModel.MarkerBoxTransparentBackground):
                // InfoBoxDrawOperation reads the setting fresh each Render(), so just
                // invalidate all boxes so they pick up the new value on the next frame.
                foreach (var m in _markerInfoBoxes)
                    m.RequestRedraw();
                break;
        }
    }

    // ---- Plot management --------------------------------------------

    /// <summary>
    /// Creates the PlotContainerViewModel, pushes an AddPlotCommand (which
    /// executes immediately, adding the container and selecting it), then
    /// returns the new container.
    /// </summary>
    public PlotContainerViewModel AddPlot(
        PlotType plotType  = PlotType.Smith,
        FreqUnit freqUnit  = FreqUnit.GHz,
        double   left      = -1,
        double   top       = -1,
        double   width     = -1,
        double   height    = -1)
    {
        bool square = plotType is PlotType.Smith or PlotType.Polar;

        double w = width > 0 ? width : (square ? 420 : 520);

        // A NEW Rect plot must open at the configured aspect ratio (golden by default) — the same
        // `height = width / RectAspectRatio` rule PlotContainerView enforces on every resize and on
        // the settings-change snap. The old fixed 520x360 default was 1.444, so an auto-created
        // display (and every Add Plot) opened off-ratio until the user happened to resize it.
        double h;
        if (height > 0)                    h = height;      // explicit (e.g. restoring a .cdd)
        else if (plotType == PlotType.Rect)
        {
            double ratio = AppSettingsViewModel.Instance.RectAspectRatio;
            h = ratio > 0 ? w / ratio : 360;
        }
        else                               h = square ? 420 : 360;

        // Auto-place when the caller did not specify a position. ComputeNewPlotPosition
        // centers the first in-view plot and otherwise grows a square grid (see its docs).
        double l, t;
        if (left >= 0 && top >= 0)
        {
            l = left;
            t = top;
        }
        else
        {
            var (px, py) = ComputeNewPlotPosition(w, h);
            l = left >= 0 ? left : px;
            t = top  >= 0 ? top  : py;
        }

        var plot      = new Plot(plotType, freqUnit);
        var plotVm    = new PlotViewModel(plot);
        var inspector = new PlotInspectorViewModel(plot, () => { }, Library);

        var container = new PlotContainerViewModel(plotVm, inspector, this)
        {
            Left    = l,
            Top     = t,
            Width   = w,
            Height  = h,
            Theme   = Theme,
            Library = Library,
        };

        // Do() executes the command immediately (adds container + selects it).
        UndoRedo.Do(new AddPlotCommand(container, this));
        return container;
    }

    // ---- New-plot auto-placement ------------------------------------
    //
    //  Goal (per spec): drop a newly-added plot somewhere convenient.
    //   1. If no existing plot is visible in the current viewport, center the new
    //      plot in the viewport.
    //   2. Otherwise infer an approximate grid (cell size + margins) from the
    //      in-view plots and place the new plot in the next grid slot, growing the
    //      grid as a square. With no user moves the fill order is:
    //        (1,1)(1,2)(2,1)(2,2)(1,3)(2,3)(3,3)(1,4)(2,4)(3,4)(4,4)(1,5)... (row,col)
    //      i.e. each new ring fills the new rightmost column top-to-bottom, then the
    //      new bottom row left-to-right. The next slot is chosen from the COUNT of
    //      in-view plots, so the canonical cascade reproduces that order exactly.

    private const double PlacementMargin = 24.0;   // logical px gap between grid cells
    private const double PlacementCluster = 0.40;   // cluster tolerance as a fraction of cell size

    /// <summary>
    /// Computes the logical (Left, Top) for a new plot of size (w, h). See the section
    /// comment above for the algorithm. Falls back to a simple cascade when the viewport
    /// size is unavailable (no CanvasSizeProvider wired).
    /// </summary>
    private (double Left, double Top) ComputeNewPlotPosition(double w, double h)
    {
        // Visible logical viewport. Without a canvas size we cannot reason about "in view",
        // so fall back to the historical cascade.
        if (CanvasSizeProvider is null)
            return (30 + _plots.Count * 30, 30 + _plots.Count * 30);

        var (canvasW, canvasH) = CanvasSizeProvider();
        if (canvasW <= 0 || canvasH <= 0)
            return (30 + _plots.Count * 30, 30 + _plots.Count * 30);

        double zoom = _zoomLevel > 0 ? _zoomLevel : 1.0;
        double vx0  = -_viewOffsetX / zoom;
        double vy0  = -_viewOffsetY / zoom;
        double vx1  = (canvasW - _viewOffsetX) / zoom;
        double vy1  = (canvasH - _viewOffsetY) / zoom;

        // Plots whose logical rect intersects the visible viewport.
        var inView = _plots.Where(p =>
            p.Left < vx1 && p.Left + p.Width  > vx0 &&
            p.Top  < vy1 && p.Top  + p.Height > vy0).ToList();

        // Case 1: nothing in view -> center the new plot in the viewport.
        if (inView.Count == 0)
        {
            double cx = (vx0 + vx1) / 2.0;
            double cy = (vy0 + vy1) / 2.0;
            return (cx - w / 2.0, cy - h / 2.0);
        }

        // Case 2: infer an approximate grid from the in-view plots.
        // Cell size = median in-view plot size (robust to one odd-sized plot).
        double cellW = Median(inView.Select(p => p.Width))  is { } mw && mw > 0 ? mw : w;
        double cellH = Median(inView.Select(p => p.Height)) is { } mh && mh > 0 ? mh : h;

        // Cluster the in-view origins into columns (by Left) and rows (by Top). The cluster
        // tolerance scales with cell size so small drags don't fragment the grid.
        var colXs = ClusterSorted(inView.Select(p => p.Left), cellW * PlacementCluster);
        var rowYs = ClusterSorted(inView.Select(p => p.Top),  cellH * PlacementCluster);

        // Grid origin = top-left of the inferred grid; step = cell size + margin (use the
        // observed column/row spacing when available, else cell size + default margin).
        double originX = colXs[0];
        double originY = rowYs[0];
        double stepX   = colXs.Count > 1 ? colXs[1] - colXs[0] : cellW + PlacementMargin;
        double stepY   = rowYs.Count > 1 ? rowYs[1] - rowYs[0] : cellH + PlacementMargin;
        if (stepX <= 0) stepX = cellW + PlacementMargin;
        if (stepY <= 0) stepY = cellH + PlacementMargin;

        // Next slot in the canonical fill order, from the in-view count (1-based index).
        var (row, col) = GridSlotForIndex(inView.Count + 1);

        // If the user moved plots so the canonical slot is already occupied, scan forward
        // in fill order for the first free (row, col) so the new plot doesn't land on one.
        var occupied = new HashSet<(int, int)>();
        foreach (var p in inView)
        {
            int c = NearestIndex(colXs, p.Left, stepX);
            int r = NearestIndex(rowYs, p.Top,  stepY);
            occupied.Add((r, c));
        }
        for (int probe = inView.Count + 1; occupied.Contains((row, col)); probe++)
            (row, col) = GridSlotForIndex(probe + 1);

        double left = originX + (col - 1) * stepX;
        double top  = originY + (row - 1) * stepY;

        // Visibility guarantee (per spec): the user must always SEE the newly-added plot. If the grid
        // slot would fall outside the viewport — e.g. the in-view plots already fill the visible area,
        // so the grid grows off-screen — place the new plot INSIDE the viewport instead. Otherwise the
        // user presses "Add Plot" and nothing appears to happen.
        if (left < vx0 || top < vy0 || left + w > vx1 || top + h > vy1)
            (left, top) = PlaceInsideViewport(w, h, vx0, vy0, vx1, vy1, inView.Count);

        return (left, top);
    }

    /// <summary>
    /// Places a new plot of size (w, h) inside the visible viewport: cascaded from the top-left by the
    /// in-view plot count so consecutive adds don't perfectly stack, and clamped so the plot stays fully
    /// visible (or, when the plot is larger than the viewport, pinned to the top-left so its title shows).
    /// </summary>
    private static (double Left, double Top) PlaceInsideViewport(
        double w, double h, double vx0, double vy0, double vx1, double vy1, int inViewCount)
    {
        const double margin = 24.0, cascade = 28.0;
        double left    = vx0 + margin + inViewCount * cascade;
        double top     = vy0 + margin + inViewCount * cascade;
        double maxLeft = Math.Max(vx0, vx1 - w);   // collapses to vx0 when the plot is wider than the view
        double maxTop  = Math.Max(vy0, vy1 - h);
        return (Math.Clamp(left, vx0, maxLeft), Math.Clamp(top, vy0, maxTop));
    }

    /// <summary>
    /// Maps a 1-based add index to a (row, col) grid slot (both 1-based). The grid grows as a
    /// square: ring n (indices (n-1)^2+1..n^2) fills the new rightmost column n top-to-bottom
    /// (rows 1..n), then the new bottom row n left-to-right (cols 1..n-1). Reproduces the spec
    /// sequence (1,1)(1,2)(2,1)(2,2)(1,3)(2,3)(3,3)(1,4)...
    /// </summary>
    private static (int Row, int Col) GridSlotForIndex(int k)
    {
        if (k < 1) k = 1;
        int n = (int)Math.Ceiling(Math.Sqrt(k));
        int p = k - (n - 1) * (n - 1);   // 1-based position within ring n (1..2n-1)
        return p <= n ? (p, n) : (n, p - n);
    }

    /// <summary>Median of a sequence, or null when empty.</summary>
    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Clusters near-equal values (within <paramref name="tol"/>) and returns the sorted list of
    /// cluster centers (means). Used to recover the distinct column-X or row-Y lines of an
    /// approximate grid from in-view plot origins.
    /// </summary>
    private static List<double> ClusterSorted(IEnumerable<double> values, double tol)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var centers = new List<double>();
        if (sorted.Count == 0) return centers;
        if (tol <= 0) tol = 1.0;

        double sum = sorted[0];
        int    cnt = 1;
        double anchor = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - anchor <= tol)
            {
                sum += sorted[i];
                cnt++;
            }
            else
            {
                centers.Add(sum / cnt);
                sum = sorted[i];
                cnt = 1;
                anchor = sorted[i];
            }
        }
        centers.Add(sum / cnt);
        return centers;
    }

    /// <summary>
    /// Returns the 1-based index of the cluster center nearest <paramref name="value"/>.
    /// <paramref name="step"/> is unused for the lookup but documents the expected spacing.
    /// </summary>
    private static int NearestIndex(List<double> centers, double value, double step)
    {
        _ = step;
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            double d = Math.Abs(centers[i] - value);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best + 1;   // 1-based
    }

    public void RemoveSelected()
    {
        var toRemove = _plots.Where(p => p.IsSelected).ToList();
        if (toRemove.Count == 0) return;
        // Do() executes immediately (removes containers) and records for undo.
        UndoRedo.Do(new RemovePlotsCommand(toRemove, this));
    }

    /// <summary>
    /// Deletes all selected items as a single undoable compound action.
    /// Selected InfoBox markers are removed from their traces; selected plot
    /// containers are removed from the display.
    ///
    /// Composite undo order (reverse): containers restored first, then markers
    /// — markers must have their containers present when they are re-added.
    /// </summary>
    public void DeleteSelected()
    {
        var cmds = new List<IUndoableCommand>();

        // Collect marker-remove commands (one per selected marker).
        foreach (var box in _markerInfoBoxes.Where(m => m.IsSelected))
            cmds.Add(new RemoveMarkerCommand(box.Marker, box.Trace, box.Container, this));

        // Collect plot-remove commands (one per selected container).
        var toRemove = _plots.Where(p => p.IsSelected).ToList();
        if (toRemove.Count > 0)
            cmds.Add(new RemovePlotsCommand(toRemove, this));

        if (cmds.Count == 0) return;

        // Execute all removals as one reversible compound command.
        UndoRedo.Do(new CompositeCommand(cmds));
    }

    // ---- Marker frequency step (arrow keys) -------------------------

    public void IncrementSelectedMarkers() => StepSelectedMarkers(+1);
    public void DecrementSelectedMarkers() => StepSelectedMarkers(-1);

    private void StepSelectedMarkers(int delta)
    {
        foreach (var box in _markerInfoBoxes.Where(m => m.IsSelected))
        {
            var freqs = box.Trace.Data?.Frequencies;
            if (freqs is null || freqs.Length == 0) continue;

            // Find the data index nearest to the marker's current frequency.
            int fi   = 0;
            double best = double.MaxValue;
            for (int i = 0; i < freqs.Length; i++)
            {
                double d = Math.Abs(freqs[i] - box.Marker.Freq);
                if (d < best) { best = d; fi = i; }
            }

            fi = Math.Clamp(fi + delta, 0, freqs.Length - 1);
            box.Marker.Freq = freqs[fi];

            // Stability circle markers use PositionStatic (world coords) for glyph
            // placement, not Freq.  Reset to origin first so the snap always lands
            // on the point of the new circle closest to the Smith-chart centre (0,0).
            if (box.Trace.IsStabilityCircle)
            {
                box.Marker.PositionStatic = System.Numerics.Vector2.Zero;
                box.Trace.SnapMarkerToStabilityCircle(box.Marker, fi);
            }

            box.OnMarkerMoved();
            box.Container.RequestPlotRedraw();
        }
    }

    public void RemovePlot(PlotContainerViewModel container)
    {
        UndoRedo.Do(new RemovePlotsCommand(new[] { container }, this));
    }

    // ---- Plot selection ---------------------------------------------

    /// <summary>
    /// Selects only <paramref name="vm"/> (or nothing if null) and deselects
    /// all plots and all InfoBoxes — the "regular click" path.
    /// </summary>
    public void SelectOnly(PlotContainerViewModel? vm)
    {
        foreach (var p in _plots)           p.IsSelected = false;
        foreach (var m in _markerInfoBoxes) m.IsSelected = false;
        if (vm is not null)
        {
            vm.IsSelected = true;
            vm.ZIndex     = _nextZIndex++;
        }
        RefreshSelection();
    }

    /// <summary>
    /// Deselects everything selectable in this display — <b>Escape</b> (owner, 2026-08-18:
    /// <i>"Pressing &lt;esc&gt; key with a Data Display in focus should cause anything selected to be
    /// unselected. This includes markers, bitmaps, plots, etc."</i>).
    ///
    /// <para><b>The exact inverse of <see cref="SelectAll"/>, and deliberately written as its mirror
    /// rather than as <c>SelectOnly(null)</c></b> — the two must stay in step, and a reader comparing
    /// them should be able to see that they cover the same collections. Everything a Data Display can
    /// select lives in one of those two: a plot container, or a marker info box (which is how a
    /// selected MARKER is represented — the glyph and its box are one selectable thing).</para>
    /// </summary>
    public void DeselectAll()
    {
        foreach (var p in _plots)           p.IsSelected = false;
        foreach (var m in _markerInfoBoxes) m.IsSelected = false;
        RefreshSelection();
    }

    /// <summary>Selects everything selectable in this display (Ctrl/Cmd+A): every plot AND every marker
    /// info box.</summary>
    public void SelectAll()
    {
        foreach (var p in _plots)           p.IsSelected = true;
        foreach (var m in _markerInfoBoxes) m.IsSelected = true;
        RefreshSelection();
    }

    /// <summary>
    /// Toggles <paramref name="vm"/> selection without disturbing any other
    /// selected items — the Ctrl+click path.
    /// </summary>
    public void ToggleSelect(PlotContainerViewModel vm)
    {
        vm.IsSelected = !vm.IsSelected;
        if (vm.IsSelected) vm.ZIndex = _nextZIndex++;
        RefreshSelection();
    }

    // ---- InfoBox selection ------------------------------------------

    /// <summary>
    /// Selects only <paramref name="vm"/> (or nothing if null) and deselects
    /// all plots and all InfoBoxes — the "regular click" path.
    /// </summary>
    public void SelectOnly(MarkerInfoBoxViewModel? vm)
    {
        foreach (var p in _plots)           p.IsSelected = false;
        foreach (var m in _markerInfoBoxes) m.IsSelected = false;
        if (vm is not null) vm.IsSelected = true;
        RefreshSelection();
    }

    /// <summary>
    /// Toggles <paramref name="vm"/> selection without disturbing any other
    /// selected items — the Ctrl+click path.
    /// </summary>
    public void ToggleSelect(MarkerInfoBoxViewModel vm)
    {
        vm.IsSelected = !vm.IsSelected;
        OnPropertyChanged(nameof(HasAnySelection));
    }

    /// <summary>
    /// Selects every InfoBox that belongs to <paramref name="container"/>,
    /// clearing all plot and other-container InfoBox selections.
    /// This is the "Select All Markers" context-menu action.
    /// </summary>
    public void SelectAllMarkersForContainer(PlotContainerViewModel container)
    {
        foreach (var p in _plots)           p.IsSelected = false;
        foreach (var m in _markerInfoBoxes) m.IsSelected = m.Container == container;
        RefreshSelection();
    }

    /// <summary>
    /// Selects every plot container and marker InfoBox whose screen-space bounds
    /// intersect <paramref name="screenRect"/> (in <c>_plotCanvas</c>-relative pixels).
    /// When <paramref name="addToSelection"/> is true the existing selection is
    /// preserved and only newly intersecting items are added; otherwise only
    /// intersecting items end up selected.
    ///
    /// Intentionally only mutates <see cref="PlotContainerViewModel.IsSelected"/>
    /// and <see cref="MarkerInfoBoxViewModel.IsSelected"/> when the value would
    /// actually change, to avoid triggering redundant <c>RequestPlotRedraw</c>
    /// calls on every mouse-move pixel during a drag.
    /// </summary>
    public void SelectItemsInRect(Rect screenRect, bool addToSelection)
    {
        foreach (var p in _plots)
        {
            var itemRect     = new Rect(p.ViewContainerLeft, p.ViewTop, p.ViewTotalWidth, p.ViewHeight);
            bool shouldSelect = screenRect.Intersects(itemRect);
            bool newSelected  = addToSelection ? (p.IsSelected || shouldSelect) : shouldSelect;
            if (p.IsSelected != newSelected)
            {
                p.IsSelected = newSelected;
                if (newSelected) p.ZIndex = _nextZIndex++;
            }
        }

        foreach (var m in _markerInfoBoxes)
        {
            var itemRect      = new Rect(m.ViewLeft, m.ViewTop, m.BoxWidth, m.BoxHeight);
            bool shouldSelect = screenRect.Intersects(itemRect);
            bool newSelected  = addToSelection ? (m.IsSelected || shouldSelect) : shouldSelect;
            if (m.IsSelected != newSelected)
                m.IsSelected = newSelected;
        }

        RefreshSelection();
    }

    internal void RefreshSelection()
    {
        var selected = _plots.Where(p => p.IsSelected).ToList();
        SingleSelectedPlot = selected.Count == 1 ? selected[0] : null;
        OnPropertyChanged(nameof(HasAnySelection));
    }

    // ---- Coordinated move -------------------------------------------

    /// <summary>
    /// Moves all currently-selected plots and InfoBoxes by (dx, dy) in
    /// logical (pre-zoom) coordinates.  Called during plot-container drag.
    /// </summary>
    public void MoveSelected(double dx, double dy)
    {
        foreach (var p in _plots.Where(p => p.IsSelected))
        {
            p.Left += dx;
            p.Top  += dy;
        }
        foreach (var m in _markerInfoBoxes.Where(m => m.IsSelected))
            m.TranslateLogical(dx, dy);
    }

    // ---- InfoBox group drag (absolute window positions) -------------
    //  Called from MarkerInfoBoxView when the user drags a selected InfoBox.
    //  All currently-selected InfoBoxes are dragged together using the same
    //  reference point so their relative layout is preserved.

    internal void StartInfoBoxGroupDrag(Point windowPt)
    {
        // Capture pre-drag logical positions for undo before the drag starts.
        _moveStartInfoBoxes = _markerInfoBoxes
            .Where(m => m.IsSelected)
            .Select(m => (m, m.LogicalLeft, m.LogicalTop))
            .ToList();

        foreach (var m in _markerInfoBoxes)
            if (m.IsSelected) m.StartDrag(windowPt);
    }

    internal void UpdateInfoBoxGroupDrag(Point windowPt)
    {
        foreach (var m in _markerInfoBoxes)
            if (m.IsSelected) m.UpdateDrag(windowPt);
    }

    internal void EndInfoBoxGroupDrag()
    {
        foreach (var m in _markerInfoBoxes)
            if (m.IsSelected) m.EndDrag();

        // Push an undo command if anything actually moved.
        if (_moveStartInfoBoxes is { Count: > 0 })
        {
            var ibSnaps = _moveStartInfoBoxes.Select(s => new InfoBoxMoveSnapshot(
                s.Vm, s.Left, s.Top, s.Vm.LogicalLeft, s.Vm.LogicalTop)).ToList();

            bool anyMoved = ibSnaps.Any(s => s.StartLogLeft != s.EndLogLeft || s.StartLogTop != s.EndLogTop);
            if (anyMoved)
                UndoRedo.Push(new MovePlotsCommand(
                    System.Array.Empty<PlotMoveSnapshot>(), ibSnaps));
        }
        _moveStartInfoBoxes = null;
    }

    // ---- Table scroll (Page Up / Page Down) -------------------------

    /// <summary>
    /// Scrolls the single selected Table plot by one full page in the given direction
    /// (positive = down / later frequencies, negative = up / earlier frequencies).
    /// Does nothing when the selection is not exactly one Table plot.
    /// </summary>
    public void ScrollSelectedTable(int pageDirection)
    {
        var tableContainers = _plots
            .Where(p => p.IsSelected && p.PlotVM.Plot.PlotType == PlotType.Table)
            .ToList();
        if (tableContainers.Count != 1) return;

        var container = tableContainers[0];
        var plot      = container.PlotVM.Plot;

        float zoom     = (float)(container.ZoomLevel > 0 ? container.ZoomLevel : 1.0);
        float fs       = (float)(plot.FontSize * zoom);
        float rowH     = fs * (1 + TableRenderer.RowPaddingFraction);
        float headerH  = fs * (1 + TableRenderer.RowPaddingFraction * 2);
        float canvasH  = (float)container.ViewHeight;   // screen pixels
        float availH   = canvasH - headerH - TableRenderer.HeaderToDataRowPadding;
        int   pageSize = Math.Max(1, availH > 0 ? (int)(availH / rowH) : 1);

        plot.TableViewScrollIndex = Math.Max(0, plot.TableViewScrollIndex + pageDirection * pageSize);
        container.RequestPlotRedraw();
    }

    /// <summary>
    /// Returns the logical X coordinate of the right edge of the viewable DataDisplay area.
    /// Pass the DataDisplay control's actual screen width.
    /// </summary>
    public double GetViewableRightEdge(double dataDisplayScreenWidth)
    {
        double zoom = _zoomLevel > 0 ? _zoomLevel : 1.0;
        return (dataDisplayScreenWidth - _viewOffsetX) / zoom;
    }

    // ---- Zoom -------------------------------------------------------

    private const double ZoomStep = 1.25;
    private const double ZoomMin  = 0.1;
    private const double ZoomMax  = 8.0;

    public void ZoomIn()  => ZoomLevel = Math.Min(ZoomMax, ZoomLevel * ZoomStep);
    public void ZoomOut() => ZoomLevel = Math.Max(ZoomMin, ZoomLevel / ZoomStep);
    public void ActualSize() => ZoomLevel = 1.0;

    public void ZoomAtPoint(double screenX, double screenY, double factor)
    {
        double newZoom = Math.Clamp(_zoomLevel * factor, ZoomMin, ZoomMax);
        double scale   = newZoom / _zoomLevel;

        _viewOffsetX = screenX - (screenX - _viewOffsetX) * scale;
        _viewOffsetY = screenY - (screenY - _viewOffsetY) * scale;
        _zoomLevel   = newZoom;

        OnPropertyChanged(nameof(ZoomLevel));
        OnPropertyChanged(nameof(ViewOffsetX));
        OnPropertyChanged(nameof(ViewOffsetY));
        PropagateViewProperties();
    }

    public void FitAll(double canvasW, double canvasH)
    {
        if (_plots.Count == 0 || canvasW <= 0 || canvasH <= 0)
        {
            ZoomLevel   = 1.0;
            ViewOffsetX = 0.0;
            ViewOffsetY = 0.0;
            return;
        }

        double minL = _plots.Min(p => p.Left);
        double minT = _plots.Min(p => p.Top);
        double maxR = _plots.Max(p => p.Left + p.Width);
        double maxB = _plots.Max(p => p.Top  + p.Height);

        double contentW = maxR - minL;
        double contentH = maxB - minT;
        if (contentW <= 0 || contentH <= 0) return;

        const double padding = 0.9;
        double zoom = Math.Clamp(
            Math.Min(canvasW / contentW, canvasH / contentH) * padding,
            ZoomMin, ZoomMax);

        double offsetX = (canvasW - contentW * zoom) / 2.0 - minL * zoom;
        double offsetY = (canvasH - contentH * zoom) / 2.0 - minT * zoom;

        _zoomLevel   = zoom;
        _viewOffsetX = offsetX;
        _viewOffsetY = offsetY;
        OnPropertyChanged(nameof(ZoomLevel));
        OnPropertyChanged(nameof(ViewOffsetX));
        OnPropertyChanged(nameof(ViewOffsetY));
        PropagateViewProperties();
    }

    // ---- Save / Load helpers ----------------------------------------

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Builds a <see cref="TabConfig"/> snapshot of this DataDisplay —
    /// zoom, offset, and all plot containers.
    /// Called by DisplayWindowViewModel.SaveAllAsync for each tab.
    /// </summary>
    internal TabConfig BuildTabConfig(string name, string configDir)
    {
        var tc = new TabConfig
        {
            Name        = name,
            ZoomLevel   = _zoomLevel,
            ViewOffsetX = _viewOffsetX,
            ViewOffsetY = _viewOffsetY,
        };
        foreach (var c in _plots)
            tc.Plots.Add(BuildPlotContainerConfig(c, configDir, Library));
        return tc;
    }

    /// <summary>
    /// Clears and restores the display from a <see cref="TabConfig"/>.
    /// Called by DisplayWindowViewModel.LoadAllAsync for each tab.
    /// </summary>
    internal async Task LoadFromTabConfigAsync(TabConfig tabConfig, string configDir)
    {
        foreach (var c in _plots) c.PlotNeedsRedraw -= OnContainerRedraw;
        _plots.Clear();
        _markerInfoBoxes.Clear();
        RefreshSelection();

        foreach (var pc in tabConfig.Plots)
            await LoadPlotContainerConfigAsync(pc, configDir);

        _zoomLevel   = tabConfig.ZoomLevel   > 0 ? tabConfig.ZoomLevel : 1.0;
        _viewOffsetX = tabConfig.ViewOffsetX;
        _viewOffsetY = tabConfig.ViewOffsetY;
        OnPropertyChanged(nameof(ZoomLevel));
        OnPropertyChanged(nameof(ViewOffsetX));
        OnPropertyChanged(nameof(ViewOffsetY));
        PropagateViewProperties();

        // The initial AddPlot in the constructor leaves a stale undo entry;
        // loading a config replaces the entire display state so the stack is reset.
        UndoRedo.Clear();
    }

    // Loads a single PlotContainerConfig and appends the resulting container
    // to _plots, including SNP-library lookup, broken-entry fallback, and
    // marker restoration.  Returns the added container so callers such as
    // PasteFromConfigAsync can collect pasted containers.
    private async Task<PlotContainerViewModel> LoadPlotContainerConfigAsync(PlotContainerConfig pc, string configDir)
    {
        var plot = new Plot(pc.PlotType, pc.FreqUnit);

        plot.CustomTitle     = pc.CustomTitle;
        plot.CustomTitleOn   = pc.CustomTitleOn;
        plot.CustomXLabel    = pc.CustomXLabel;
        plot.CustomXLabelOn  = pc.CustomXLabelOn;
        plot.CustomYLabel    = pc.CustomYLabel;
        plot.CustomYLabelOn  = pc.CustomYLabelOn;
        plot.CustomY2Label   = pc.CustomY2Label;
        plot.CustomY2LabelOn = pc.CustomY2LabelOn;
        plot.TableViewAscendingSortOrder = pc.TableViewAscendingSortOrder;
        plot.TableViewScrollIndex        = pc.TableViewScrollIndex;
        plot.FontSize                    = pc.FontSize > 0 ? pc.FontSize : 12;
        plot.ColumnWidth                 = pc.FreqColumnWidth > 0 ? pc.FreqColumnWidth : 115;
        plot.TableOptimum                = pc.TableOptimum;
        plot.TableReadMode               = pc.TableReadMode;
        plot.TableCompression            = pc.TableCompression > 0 ? pc.TableCompression : 3.0;
        plot.SummaryLoadpullGroup        = pc.SummaryLoadpullGroup;

        foreach (var traceConfig in pc.Traces)
        {
            if (traceConfig.SourcePath is null) continue;

            // traceConfig.SourcePath now stores the logical SourceRef (not an absolute/relative path).
            string? sref        = traceConfig.SourcePath;
            string? resolvedPath = Library?.ResolveAbs(sref);

            // For cross-schematic refs or abs Touchstone: resolve and lazy-load that specific file.
            if (resolvedPath is not null &&
                !string.IsNullOrEmpty(sref) && sref != DataSourceRef.Selected &&
                Library is not null)
            {
                await Library.LoadFileAsync(resolvedPath);
            }

            // Look up the library entry; also grab the SNP for network-bound traces.
            DataSourceEntryViewModel? libEntry = null;
            SNP? snp = null;

            if (Library is not null && resolvedPath is not null)
            {
                libEntry = Library.Entries
                    .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                         StringComparison.OrdinalIgnoreCase));

                if (libEntry is null && File.Exists(resolvedPath))
                {
                    await Library.LoadFileAsync(resolvedPath);
                    libEntry = Library.Entries
                        .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                             StringComparison.OrdinalIgnoreCase));
                }
                else if (libEntry is null)
                {
                    Library.AddBrokenEntry(resolvedPath);
                    libEntry = Library.Entries
                        .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                             StringComparison.OrdinalIgnoreCase));
                }

                snp = libEntry?.Snp;
            }

            bool isCubeBound    = (traceConfig.CubeName is not null && traceConfig.CubeSlice.Count > 0)
                               || traceConfig.Expression is not null;
            bool isContourTrace = traceConfig.ContourTrace is not null;
            bool isSummaryTrace = traceConfig.SummaryColumn is not null;

            // Network-bound: must have a valid SNP. Cube-bound/contour/summary: must have a library entry.
            if (!isCubeBound && !isContourTrace && !isSummaryTrace && snp is null) continue;
            if (isCubeBound  && libEntry is null) continue;
            if (isContourTrace && libEntry is null) continue;
            if (isSummaryTrace && libEntry is null) continue;

            void RestoreMarkers(Trace tr, TraceConfig tcfg)
            {
                foreach (var mc in tcfg.Markers)
                {
                    var marker = new Marker(tr, mc.Freq, mc.IsMulti, mc.IsDelta, mc.Index, mc.FreqUnits)
                    {
                        Name                   = mc.Name,
                        MatrixFormat           = mc.MatrixFormat,
                        Style                  = mc.Style,
                        UseNormalizedImpedance = mc.UseNormalizedImpedance,
                        MaximumFractionDigits  = mc.MaximumFractionDigits,
                        InfoBoxPos             = new Avalonia.Point(mc.InfoBoxX, mc.InfoBoxY),
                        PositionStatic         = new System.Numerics.Vector2(mc.PositionStaticX, mc.PositionStaticY),
                        MarkerKind             = mc.MarkerKind,
                        ShowInfoBox            = mc.ShowInfoBox,
                        ContourSnapped         = mc.ContourSnapped,
                        VswrEnabled            = mc.VswrEnabled,
                        VswrValue              = mc.VswrValue,
                    };
                    tr.Markers.Add(marker);
                }
            }

            Trace trace;
            if (isSummaryTrace)
            {
                var sc = traceConfig.SummaryColumn!;
                var placeholder = new SNP(new double[] { 1e9 }, 1);
                trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
                trace.SummaryColumn = new SummaryColumnData
                {
                    Kind           = sc.Kind,
                    MetricName     = sc.MetricName,
                    Header         = sc.Header,
                    FractionDigits = sc.FractionDigits,
                    ColumnWidth    = sc.ColumnWidth,
                };
                ApplyProperties(traceConfig.Properties, trace.Properties);
                trace.SourceRef  = sref;
                trace.SourcePath = resolvedPath;
                RestoreMarkers(trace, traceConfig);
                plot.Traces.Add(trace);
                continue;
            }
            else if (isContourTrace)
            {
                var ct = traceConfig.ContourTrace!;
                var placeholder = new SNP(new double[] { 1e9 }, 1);
                trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
                trace.ContourData = new ContourData
                {
                    MetricName            = ct.MetricName,
                    ContourConstraintKind = ct.ConstraintKind,
                    ConstraintMetricName  = ct.ConstraintMetricName,
                    ConstraintValue       = ct.ConstraintValue,
                    FreqIndex             = ct.FreqIndex,
                    LoadpullGroup         = ct.LoadpullGroup,
                    LevelMode             = ct.LevelMode,
                    LevelStart            = ct.LevelStart,
                    LevelStep             = ct.LevelStep,
                    LevelStop             = ct.LevelStop,
                    LevelCount            = ct.LevelCount,
                    ShowIsoLines          = ct.ShowIsoLines,
                    ShowFill              = ct.ShowFill,
                    DrawLabels            = ct.DrawLabels,
                    SelectedFillKind      = ct.SelectedFillKind,
                    ColorMap              = ct.ColorMap,
                    LabelSpacing          = ct.LabelSpacing,
                    DisplayMxp            = ct.DisplayMxp,
                    DisplayMxe            = ct.DisplayMxe,
                    DisplayGridPoints     = ct.DisplayGridPoints,
                    GridPointColor        = new SKColor(ct.GridPointColor),
                    LabelForeground       = new SKColor(ct.LabelForeground),
                    LineColor             = new SKColor(ct.LineColor),
                    StrokeWidth           = ct.StrokeWidth,
                    LineColorOverridden   = ct.LineColorOverridden,
                    LabelBackground       = new SKColor(ct.LabelBackground),
                    GridPointSize         = ct.GridPointSize,
                    LevelFontSize         = ct.LevelFontSize,
                    FadeLineOpacity       = ct.FadeLineOpacity,
                    InterpKernel          = ct.InterpKernel,
                    Smoothing             = ct.Smoothing,
                    Epsilon               = ct.Epsilon,
                };
                ApplyProperties(traceConfig.Properties, trace.Properties);
                trace.SourceRef  = sref;
                trace.SourcePath = resolvedPath;
                RestoreMarkers(trace, traceConfig);
                plot.Traces.Add(trace);
                continue;
            }
            else if (isCubeBound)
            {
                // Use a placeholder SNP (or the actual SNP if the file has one).
                var placeholderSnp = snp ?? new SNP(new double[] { 1e9 }, 2);
                trace = new Trace(placeholderSnp, MatrixType.S, 0, 0,
                                  DependentVarFormat.Db, traceConfig.UseSecondaryAxis);
                trace.CubeName   = traceConfig.CubeName;
                trace.Transform  = traceConfig.CubeTransform;
                trace.Slice      = traceConfig.CubeSlice.Count > 0
                    ? traceConfig.CubeSlice.Select(s => new AxisSlice(s.AxisName, s.Role, s.Index)).ToArray()
                    : null;
                trace.Expression = traceConfig.Expression;
            }
            else
            {
                trace = new Trace(snp!, traceConfig.MatrixType, traceConfig.Row,
                                  traceConfig.Col, traceConfig.YAxis, traceConfig.UseSecondaryAxis);
                trace.Derived = traceConfig.Derived;
            }

            // Ordered port selection for network metrics (R-stb-3a) — restored for every trace
            // kind, since a .cdd may carry a derived trace on either source path.
            trace.InputPort             = traceConfig.InputPort;
            trace.OutputPort            = traceConfig.OutputPort;
            trace.PassivityWholeNetwork = traceConfig.PassivityWholeNetwork;

            trace.SourceRef             = sref;
            trace.SourcePath            = resolvedPath;
            trace.MatrixFormat          = traceConfig.MatrixFormat;
            trace.ColumnWidth           = traceConfig.ColumnWidth > 0 ? traceConfig.ColumnWidth : 115;
            trace.XColumnWidth          = traceConfig.XColumnWidth;
            foreach (var kvp in traceConfig.FamilyColumnWidths)
                trace.FamilyColumnWidths[kvp.Key] = kvp.Value;
            trace.FormatString          = traceConfig.FormatString;
            trace.MaximumFractionDigits = traceConfig.MaximumFractionDigits;

            if (ComplexStringHelper.TryParse(traceConfig.Z0, out System.Numerics.Complex z0))
                trace.Z0 = z0;
            trace.Z0OverrideEnabled = traceConfig.Z0Override;

            ApplyProperties(traceConfig.Properties, trace.Properties);

            if (isCubeBound)
                PlotInspectorViewModel.TrySetCubeData(trace, Library, pc.PlotType, pc.FreqUnit);
            else if (snp is not null && !snp.IsEmpty)
                trace.BuildPath(pc.PlotType, pc.FreqUnit);

            if (isCubeBound) { RestoreMarkers(trace, traceConfig); plot.Traces.Add(trace); continue; }

            RestoreMarkers(trace, traceConfig);
            plot.Traces.Add(trace);
        }

        if (pc.Axes is { } savedAxes)
            plot.RestoreAxesFromConfig(
                savedAxes.AutoscaleX, savedAxes.AutoscaleY,
                savedAxes.AutoscaleRightY, savedAxes.AutoscaleMag,
                new Rect(savedAxes.WindowX, savedAxes.WindowY,
                         savedAxes.WindowWidth, savedAxes.WindowHeight),
                new Rect(savedAxes.WindowSecondaryX, savedAxes.WindowSecondaryY,
                         savedAxes.WindowSecondaryWidth, savedAxes.WindowSecondaryHeight));
        else
            plot.Autoscale();  // no axes config — old file, default to full autoscale

        var plotVm    = new PlotViewModel(plot);
        var inspector = new PlotInspectorViewModel(plot, () => { }, Library);

        var container = new PlotContainerViewModel(plotVm, inspector, this)
        {
            Left    = pc.Left,
            Top     = pc.Top,
            Width   = pc.Width,
            Height  = pc.Height,
            Theme   = Theme,
            Library = Library,
        };
        _plots.Add(container);
        RebuildMarkerInfoBoxesForContainer(container);
        return container;
    }

    /// <summary>
    /// Builds a <see cref="PlotContainerConfig"/> from a live container.
    /// Single authoritative location — both Save and Copy use this so new
    /// Plot properties only need to be added here.
    /// </summary>
    /// <param name="c">The container to snapshot.</param>
    /// <param name="configDir">
    /// Directory used to make trace source paths relative.
    /// Pass <see cref="string.Empty"/> when building an in-memory config
    /// (e.g. clipboard copy) where absolute paths are acceptable.
    /// </param>
    internal static PlotContainerConfig BuildPlotContainerConfig(
        PlotContainerViewModel c, string configDir,
        DataSourceLibraryViewModel? library = null)
    {
        var plot = c.PlotVM.Plot;
        var pc   = new PlotContainerConfig
        {
            Left     = c.Left,
            Top      = c.Top,
            Width    = c.Width,
            Height   = c.Height,
            PlotType = plot.PlotType,
            FreqUnit = plot.FreqUnits,
            CustomTitle     = plot.CustomTitle,
            CustomTitleOn   = plot.CustomTitleOn,
            CustomXLabel    = plot.CustomXLabel,
            CustomXLabelOn  = plot.CustomXLabelOn,
            CustomYLabel    = plot.CustomYLabel,
            CustomYLabelOn  = plot.CustomYLabelOn,
            CustomY2Label   = plot.CustomY2Label,
            CustomY2LabelOn = plot.CustomY2LabelOn,
            TableViewAscendingSortOrder = plot.TableViewAscendingSortOrder,
            TableViewScrollIndex        = plot.TableViewScrollIndex,
            FontSize                    = plot.FontSize,
            FreqColumnWidth             = plot.ColumnWidth,
            TableOptimum                = plot.TableOptimum,
            TableReadMode               = plot.TableReadMode,
            TableCompression            = plot.TableCompression,
            SummaryLoadpullGroup        = plot.SummaryLoadpullGroup,
            Axes = new AxesConfig
            {
                AutoscaleX      = plot.AutoscaleX,
                AutoscaleY      = plot.AutoscaleY,
                AutoscaleRightY = plot.AutoscaleRightY,
                AutoscaleMag    = plot.AutoscaleMag,
                WindowX             = plot.Axes.Window.X,
                WindowY             = plot.Axes.Window.Y,
                WindowWidth         = plot.Axes.Window.Width,
                WindowHeight        = plot.Axes.Window.Height,
                WindowSecondaryX        = plot.Axes.WindowSecondary.X,
                WindowSecondaryY        = plot.Axes.WindowSecondary.Y,
                WindowSecondaryWidth    = plot.Axes.WindowSecondary.Width,
                WindowSecondaryHeight   = plot.Axes.WindowSecondary.Height,
            },
        };
        foreach (var t in plot.Traces)
            pc.Traces.Add(BuildTraceConfig(t, configDir, library));
        return pc;
    }

    /// <summary>
    /// Computes the persistence key for a data source's alias (R-res-4) — the same relative-to-
    /// results-root form a trace's own SourceRef uses when the file lives under the results root
    /// (portable across a moved workspace), else the absolute path. Deliberately does NOT resolve
    /// the "Selected" sentinel: an alias belongs to one concrete file, never to "whichever source is
    /// currently selected."
    /// </summary>
    internal static string ComputeSourceKey(string absPath, DataSourceLibraryViewModel? library)
    {
        var root = library?.ResultsRootProvider?.Invoke();
        if (root is not null && absPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(root, absPath).Replace('\\', '/');
        return absPath;
    }

    /// <summary>Derives the logical SourceRef from a trace's absolute SourcePath as a fallback
    /// for traces created before SourceRef was stamped.</summary>
    private static string? DeriveRef(Trace t, DataSourceLibraryViewModel? library)
    {
        string? abs = t.IsCubeBound ? t.SourcePath : (t.Data?.FilePath ?? t.SourcePath);
        if (abs is null || library is null) return abs;

        // Match against the currently-selected datasource → sentinel.
        if (library.SelectedDataSourceAbs is not null &&
            string.Equals(abs, library.SelectedDataSourceAbs, StringComparison.OrdinalIgnoreCase))
            return DataSourceRef.Selected;

        // Under the results root → "<name>.npy" relative id (flat results/, R-res-1).
        var root = library.ResultsRootProvider?.Invoke();
        if (root is not null && abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(root, abs).Replace('\\', '/');

        return abs;  // rooted Touchstone or unknown
    }

    internal static TraceConfig BuildTraceConfig(Trace t, string configDir,
        DataSourceLibraryViewModel? library = null)
    {
        // Emit the logical SourceRef (persisted) instead of an absolute/relative path.
        // DeriveRef is the fallback for traces created before SourceRef was populated.
        string? sourceRef = t.SourceRef ?? DeriveRef(t, library);

        var tc = new TraceConfig
        {
            SourcePath            = sourceRef,
            Row                   = t.Row,
            Col                   = t.Col,
            MatrixType            = t.MatrixType,
            Derived               = t.Derived,
            InputPort             = t.InputPort,
            OutputPort            = t.OutputPort,
            PassivityWholeNetwork = t.PassivityWholeNetwork,
            YAxis                 = t.YAxis,
            UseSecondaryAxis      = t.UseSecondaryAxis,
            Z0                    = ComplexStringHelper.Format(t.Z0),
            Z0Override            = t.Z0OverrideEnabled,
            MatrixFormat          = t.MatrixFormat,
            ColumnWidth           = t.ColumnWidth,
            XColumnWidth          = t.XColumnWidth,
            FamilyColumnWidths    = new Dictionary<int, double>(t.FamilyColumnWidths),
            FormatString          = t.FormatString,
            MaximumFractionDigits = t.MaximumFractionDigits,
            // Cube-bound identity fields (Phase 7.2c-a). Null = network-bound.
            CubeName      = t.CubeName,
            CubeTransform = t.Transform,
            CubeSlice     = t.Slice is null
                ? new()
                : t.Slice.Select(s => new AxisSliceConfig
                  {
                      AxisName = s.AxisName,
                      Role     = s.Role,
                      Index    = s.Index,
                  }).ToList(),
            Expression    = t.Expression,
            Properties       = new TracePropertiesConfig
            {
                LineEnabled      = t.Properties.LineEnabled,
                LineWidth        = t.Properties.LineWidth,
                LineColorIndex   = t.Properties.LineColorIndex,
                LineType         = t.Properties.LineType,
                MarkerEnabled    = t.Properties.MarkerEnabled,
                MarkerSize       = t.Properties.MarkerSize,
                MarkerColorIndex = t.Properties.MarkerColorIndex,
                MarkerType       = t.Properties.MarkerType,
            },
        };

        // Contour trace authoring state (7.4e).
        if (t.ContourData is { } cd)
        {
            tc.ContourTrace = new ContourTraceConfig
            {
                MetricName           = cd.MetricName,
                ConstraintKind       = cd.ContourConstraintKind,
                ConstraintMetricName = cd.ConstraintMetricName,
                ConstraintValue      = cd.ConstraintValue,
                FreqIndex            = cd.FreqIndex,
                LoadpullGroup        = cd.LoadpullGroup,
                LevelMode            = cd.LevelMode,
                LevelStart           = cd.LevelStart,
                LevelStep            = cd.LevelStep,
                LevelStop            = cd.LevelStop,
                LevelCount           = cd.LevelCount,
                ShowIsoLines         = cd.ShowIsoLines,
                ShowFill             = cd.ShowFill,
                DrawLabels           = cd.DrawLabels,
                SelectedFillKind     = cd.SelectedFillKind,
                ColorMap             = cd.ColorMap,
                LabelSpacing         = cd.LabelSpacing,
                DisplayMxp           = cd.DisplayMxp,
                DisplayMxe           = cd.DisplayMxe,
                DisplayGridPoints    = cd.DisplayGridPoints,
                GridPointColor       = (uint)cd.GridPointColor,
                LabelForeground      = (uint)cd.LabelForeground,
                LineColor             = (uint)cd.LineColor,
                StrokeWidth           = cd.StrokeWidth,
                LineColorOverridden   = cd.LineColorOverridden,
                LabelBackground       = (uint)cd.LabelBackground,
                GridPointSize         = cd.GridPointSize,
                LevelFontSize         = cd.LevelFontSize,
                FadeLineOpacity       = cd.FadeLineOpacity,
                InterpKernel         = cd.InterpKernel,
                Smoothing            = cd.Smoothing,
                Epsilon              = cd.Epsilon,
            };
        }

        // Summary column authoring state (7.5).
        if (t.SummaryColumn is { } sc)
        {
            tc.SummaryColumn = new SummaryColumnConfig
            {
                Kind           = sc.Kind,
                MetricName     = sc.MetricName,
                Header         = sc.Header,
                FractionDigits = sc.FractionDigits,
                ColumnWidth    = sc.ColumnWidth,
            };
        }

        foreach (var m in t.Markers)
        {
            tc.Markers.Add(new MarkerConfig
            {
                Name                  = m.Name,
                Index                 = m.Index,
                Freq                  = Finite(m.Freq),
                FreqUnits             = m.FreqUnits,
                MatrixFormat          = m.MatrixFormat,
                Style                 = m.Style,
                UseNormalizedImpedance= m.UseNormalizedImpedance,
                MaximumFractionDigits = m.MaximumFractionDigits,
                IsMulti               = m.IsMulti,
                IsDelta               = m.IsDelta,
                // Guard every persisted floating-point field to a finite value. System.Text.Json
                // throws on NaN/±∞ (JsonOpts sets no NumberHandling), and the dirty-check serializes
                // on every redraw — so a single non-finite marker number would crash the app. The
                // primary cause (a placeholder-NaN InfoBoxPos serialized mid-rebuild) is fixed in
                // RebuildMarkerInfoBoxesForContainer; this is defense-in-depth for any other path.
                InfoBoxX              = Finite(m.InfoBoxPos.X),
                InfoBoxY              = Finite(m.InfoBoxPos.Y),
                PositionStaticX       = Finite(m.PositionStatic.X),
                PositionStaticY       = Finite(m.PositionStatic.Y),
                MarkerKind            = m.MarkerKind,
                ShowInfoBox           = m.ShowInfoBox,
                ContourSnapped        = m.ContourSnapped,
                VswrEnabled           = m.VswrEnabled,
                VswrValue             = Finite(m.VswrValue, fallback: 2.0),
            });
        }

        return tc;
    }

    /// <summary>Returns <paramref name="v"/> when finite, else <paramref name="fallback"/> (default 0).
    /// Used to keep non-finite marker coordinates out of the JSON serializer, which rejects NaN/±∞.</summary>
    private static double Finite(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;

    /// <summary>float overload of <see cref="Finite(double,double)"/>.</summary>
    private static float Finite(float v, float fallback = 0f) => float.IsFinite(v) ? v : fallback;

    // ---- Paste ----------------------------------------------------------

    /// <summary>
    /// Adds plots from <paramref name="config"/> to the current display without clearing
    /// existing plots.  Each pasted plot is offset by <see cref="PasteOffset"/> logical
    /// pixels so it does not land exactly on top of existing content.
    /// Source files that are not already in the library are loaded (or added as broken
    /// entries when missing) so the DataSourceLibraryView shows every required path.
    /// Returns the list of newly-added containers (used to record a PasteCommand for undo).
    /// </summary>
    public async Task<IReadOnlyList<PlotContainerViewModel>> PasteFromConfigAsync(DataDisplayConfig config)
    {
        const double PasteOffset = 20.0;

        // Seed with every marker name already in the display; updated as new names are assigned
        // so intra-paste collisions (two source plots both having "m1") are also resolved.
        var usedMarkerNames = new HashSet<string>(
            _plots.SelectMany(p => p.PlotVM.Plot.Traces)
                  .SelectMany(t => t.Markers)
                  .Select(m => m.Name),
            StringComparer.Ordinal);

        var pastedContainers = new List<PlotContainerViewModel>();

        foreach (var pc in config.Plots)
        {
            // Offset position so pasted content doesn't land exactly on top of the source.
            pc.Left += PasteOffset;
            pc.Top  += PasteOffset;
            foreach (var tc in pc.Traces)
                foreach (var mc in tc.Markers)
                {
                    mc.InfoBoxX += PasteOffset;
                    mc.InfoBoxY += PasteOffset;
                }

            var container = await LoadPlotContainerConfigAsync(pc, configDir: "");

            // Dedup marker names against pre-paste set (incl. names already assigned this paste).
            foreach (var trace in container.PlotVM.Plot.Traces)
            {
                foreach (var marker in trace.Markers)
                {
                    string name = marker.Name;
                    if (!usedMarkerNames.Add(name))
                    {
                        for (int n = 2; ; n++)
                        {
                            string candidate = $"{name}_{n}";
                            if (usedMarkerNames.Add(candidate)) { marker.Name = candidate; break; }
                        }
                    }
                }
            }

            pastedContainers.Add(container);
        }

        // Select only the newly pasted containers and their InfoBoxes;
        // deselect everything else first.
        if (pastedContainers.Count > 0)
        {
            foreach (var p in _plots)           p.IsSelected = false;
            foreach (var m in _markerInfoBoxes) m.IsSelected = false;

            foreach (var c in pastedContainers)
            {
                c.IsSelected = true;
                c.ZIndex     = _nextZIndex++;
            }

            var pastedSet = pastedContainers.ToHashSet();
            foreach (var m in _markerInfoBoxes)
                if (pastedSet.Contains(m.Container))
                    m.IsSelected = true;

            RefreshSelection();
        }

        return pastedContainers;
    }

    private static void ApplyProperties(TracePropertiesConfig src, TraceProperties dst)
    {
        dst.LineEnabled      = src.LineEnabled;
        dst.LineWidth        = src.LineWidth;
        dst.LineColorIndex   = src.LineColorIndex;
        dst.LineType         = src.LineType;
        dst.MarkerEnabled    = src.MarkerEnabled;
        dst.MarkerSize       = src.MarkerSize;
        dst.MarkerColorIndex = src.MarkerColorIndex;
        dst.MarkerType       = src.MarkerType;
    }
}
