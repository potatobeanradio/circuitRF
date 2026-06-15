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

    // ---- Marker info-box management ---------------------------------

    /// <summary>
    /// Called by PlotContainerViewModel when its PlotControl fires PlotChanged
    /// (marker added/removed, trace changed, etc.).  Rebuilds the overlay info
    /// boxes that belong to <paramref name="container"/>.
    /// </summary>
    public void OnContainerPlotChanged(PlotContainerViewModel container)
        => RebuildMarkerInfoBoxesForContainer(container);

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

        foreach (var trace in plot.Traces)
        {
            foreach (var marker in trace.Markers)
            {
                if (double.IsNaN(marker.InfoBoxPos.X))
                    PlaceInfoBoxInLogicalCoords(marker, trace, plot, container);

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
    private SnpLibraryViewModel? _library;

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

    public SnpLibraryViewModel? Library
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

    public DataDisplayViewModel(SnpLibraryViewModel library, bool addEmptyPlot = true, bool selectEmptyPlot = true)
    {
        _library = library;
        library.LibraryChanged            += (_, _) => OnLibraryEntryCountChanged();
        library.Entries.CollectionChanged += (_, _) => OnLibraryEntryCountChanged();

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

        double w = width  > 0 ? width  : (square ? 420 : 520);
        double h = height > 0 ? height : (square ? 420 : 360);

        double l = left  >= 0 ? left  : 30 + _plots.Count * 30;
        double t = top   >= 0 ? top   : 30 + _plots.Count * 30;

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
            tc.Plots.Add(BuildPlotContainerConfig(c, configDir));
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
    // marker restoration.
    private async Task LoadPlotContainerConfigAsync(PlotContainerConfig pc, string configDir)
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

        foreach (var traceConfig in pc.Traces)
        {
            if (traceConfig.SourcePath is null) continue;

            string resolvedPath = Path.IsPathRooted(traceConfig.SourcePath)
                ? traceConfig.SourcePath
                : Path.GetFullPath(Path.Combine(configDir, traceConfig.SourcePath));

            SNP? snp = null;

            if (Library is not null)
            {
                snp = Library.Entries
                    .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                         StringComparison.OrdinalIgnoreCase))?.Snp;

                if (snp is null)
                {
                    if (File.Exists(resolvedPath))
                    {
                        await Library.LoadFileAsync(resolvedPath);
                        snp = Library.Entries
                            .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                                 StringComparison.OrdinalIgnoreCase))?.Snp;
                    }
                    else
                    {
                        Library.AddBrokenEntry(resolvedPath);
                        snp = Library.Entries
                            .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                                 StringComparison.OrdinalIgnoreCase))?.Snp;
                    }
                }
            }

            if (snp is null) continue;

            var trace = new Trace(snp, traceConfig.MatrixType, traceConfig.Row,
                                  traceConfig.Col, traceConfig.YAxis, traceConfig.UseSecondaryAxis);
            trace.Derived               = traceConfig.Derived;
            trace.SourcePath            = resolvedPath;
            trace.MatrixFormat          = traceConfig.MatrixFormat;
            trace.ColumnWidth           = traceConfig.ColumnWidth > 0 ? traceConfig.ColumnWidth : 115;
            trace.FormatString          = traceConfig.FormatString;
            trace.MaximumFractionDigits = traceConfig.MaximumFractionDigits;

            if (ComplexStringHelper.TryParse(traceConfig.Z0, out System.Numerics.Complex z0))
                trace.Z0 = z0;

            ApplyProperties(traceConfig.Properties, trace.Properties);

            if (!snp.IsEmpty)
                trace.BuildPath(pc.PlotType, pc.FreqUnit);

            foreach (var mc in traceConfig.Markers)
            {
                var marker = new Marker(trace, mc.Freq, mc.IsMulti, mc.IsDelta, mc.Index, mc.FreqUnits)
                {
                    Name                  = mc.Name,
                    MatrixFormat          = mc.MatrixFormat,
                    Style                 = mc.Style,
                    UseNormalizedImpedance= mc.UseNormalizedImpedance,
                    MaximumFractionDigits = mc.MaximumFractionDigits,
                    InfoBoxPos            = new Avalonia.Point(mc.InfoBoxX, mc.InfoBoxY),
                    PositionStatic        = new System.Numerics.Vector2(mc.PositionStaticX, mc.PositionStaticY),
                };
                trace.Markers.Add(marker);
            }

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
        PlotContainerViewModel c, string configDir)
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
            pc.Traces.Add(BuildTraceConfig(t, configDir));
        return pc;
    }

    internal static TraceConfig BuildTraceConfig(Trace t, string configDir)
    {
        // Use the SNP's authoritative FilePath; write relative when same directory.
        string? sourcePath = t.Data?.FilePath ?? t.SourcePath;
        if (sourcePath != null && !string.IsNullOrEmpty(configDir))
        {
            string? srcDir = Path.GetDirectoryName(sourcePath);
            if (string.Equals(configDir, srcDir, StringComparison.OrdinalIgnoreCase))
                sourcePath = Path.GetFileName(sourcePath);
        }

        var tc = new TraceConfig
        {
            SourcePath            = sourcePath,
            Row                   = t.Row,
            Col                   = t.Col,
            MatrixType            = t.MatrixType,
            Derived               = t.Derived,
            YAxis                 = t.YAxis,
            UseSecondaryAxis      = t.UseSecondaryAxis,
            Z0                    = ComplexStringHelper.Format(t.Z0),
            MatrixFormat          = t.MatrixFormat,
            ColumnWidth           = t.ColumnWidth,
            FormatString          = t.FormatString,
            MaximumFractionDigits = t.MaximumFractionDigits,
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

        foreach (var m in t.Markers)
        {
            tc.Markers.Add(new MarkerConfig
            {
                Name                  = m.Name,
                Index                 = m.Index,
                Freq                  = m.Freq,
                FreqUnits             = m.FreqUnits,
                MatrixFormat          = m.MatrixFormat,
                Style                 = m.Style,
                UseNormalizedImpedance= m.UseNormalizedImpedance,
                MaximumFractionDigits = m.MaximumFractionDigits,
                IsMulti               = m.IsMulti,
                IsDelta               = m.IsDelta,
                InfoBoxX              = m.InfoBoxPos.X,
                InfoBoxY              = m.InfoBoxPos.Y,
                PositionStaticX       = m.PositionStatic.X,
                PositionStaticY       = m.PositionStatic.Y,
            });
        }

        return tc;
    }

    // ---- Paste ----------------------------------------------------------

    /// <summary>
    /// Adds plots from <paramref name="config"/> to the current display without clearing
    /// existing plots.  Each pasted plot is offset by <see cref="PasteOffset"/> logical
    /// pixels so it does not land exactly on top of existing content.
    /// Source files that are not already in the library are loaded (or added as broken
    /// entries when missing) so the SnpLibraryView shows every required path.
    /// Returns the list of newly-added containers (used to record a PasteCommand for undo).
    /// </summary>
    public async Task<IReadOnlyList<PlotContainerViewModel>> PasteFromConfigAsync(DataDisplayConfig config)
    {
        const double PasteOffset = 20.0;

        var pastedContainers = new List<PlotContainerViewModel>();

        // Seed with every marker name already in the display; updated as new names are assigned
        // so intra-paste collisions (two source plots both having "m1") are also resolved.
        var usedMarkerNames = new HashSet<string>(
            _plots.SelectMany(p => p.PlotVM.Plot.Traces)
                  .SelectMany(t => t.Markers)
                  .Select(m => m.Name),
            StringComparer.Ordinal);

        foreach (var pc in config.Plots)
        {
            var plot = new Plot(pc.PlotType, pc.FreqUnit);

            // Restore custom axis labels (absent in old files → defaults leave auto-labels intact).
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

            foreach (var tc in pc.Traces)
            {
                if (tc.SourcePath is null) continue;

                string resolvedPath = Path.IsPathRooted(tc.SourcePath)
                    ? tc.SourcePath
                    : Path.GetFullPath(tc.SourcePath);

                SNP? snp = null;
                if (Library is not null)
                {
                    snp = Library.Entries
                        .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                             StringComparison.OrdinalIgnoreCase))?.Snp;

                    if (snp is null)
                    {
                        if (File.Exists(resolvedPath))
                        {
                            await Library.LoadFileAsync(resolvedPath);
                            snp = Library.Entries
                                .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                                     StringComparison.OrdinalIgnoreCase))?.Snp;
                        }
                        else
                        {
                            Library.AddBrokenEntry(resolvedPath);
                            snp = Library.Entries
                                .FirstOrDefault(e => string.Equals(e.FilePath, resolvedPath,
                                                     StringComparison.OrdinalIgnoreCase))?.Snp;
                        }
                    }
                }

                if (snp is null) continue;

                var trace = new Trace(snp, tc.MatrixType, tc.Row, tc.Col, tc.YAxis,
                                      tc.UseSecondaryAxis);
                trace.Derived               = tc.Derived;
                trace.SourcePath            = resolvedPath;
                trace.MatrixFormat          = tc.MatrixFormat;
                trace.ColumnWidth           = tc.ColumnWidth > 0 ? tc.ColumnWidth : 115;
                trace.FormatString          = tc.FormatString;
                trace.MaximumFractionDigits = tc.MaximumFractionDigits;

                if (ComplexStringHelper.TryParse(tc.Z0, out System.Numerics.Complex z0))
                    trace.Z0 = z0;

                ApplyProperties(tc.Properties, trace.Properties);

                if (!snp.IsEmpty)
                    trace.BuildPath(pc.PlotType, pc.FreqUnit);

                foreach (var mc in tc.Markers)
                {
                    // Resolve name collisions: if the name is already taken, append _2, _3, …
                    string markerName = mc.Name;
                    if (!usedMarkerNames.Add(markerName))
                    {
                        for (int n = 2; ; n++)
                        {
                            string candidate = $"{markerName}_{n}";
                            if (usedMarkerNames.Add(candidate)) { markerName = candidate; break; }
                        }
                    }

                    var marker = new Marker(trace, mc.Freq, mc.IsMulti, mc.IsDelta, mc.Index, mc.FreqUnits)
                    {
                        Name                   = markerName,
                        MatrixFormat           = mc.MatrixFormat,
                        Style                  = mc.Style,
                        UseNormalizedImpedance = mc.UseNormalizedImpedance,
                        MaximumFractionDigits  = mc.MaximumFractionDigits,
                        InfoBoxPos             = new Avalonia.Point(mc.InfoBoxX + PasteOffset, mc.InfoBoxY + PasteOffset),
                        PositionStatic         = new System.Numerics.Vector2(mc.PositionStaticX, mc.PositionStaticY),
                    };
                    trace.Markers.Add(marker);
                }

                plot.Traces.Add(trace);
            }

            if (pc.Axes is { } pastedAxes)
                plot.RestoreAxesFromConfig(
                    pastedAxes.AutoscaleX, pastedAxes.AutoscaleY,
                    pastedAxes.AutoscaleRightY, pastedAxes.AutoscaleMag,
                    new Rect(pastedAxes.WindowX, pastedAxes.WindowY,
                             pastedAxes.WindowWidth, pastedAxes.WindowHeight),
                    new Rect(pastedAxes.WindowSecondaryX, pastedAxes.WindowSecondaryY,
                             pastedAxes.WindowSecondaryWidth, pastedAxes.WindowSecondaryHeight));
            else
                plot.Autoscale();

            var plotVm    = new PlotViewModel(plot);
            var inspector = new PlotInspectorViewModel(plot, () => { }, Library);

            var container = new PlotContainerViewModel(plotVm, inspector, this)
            {
                Left    = pc.Left   + PasteOffset,
                Top     = pc.Top    + PasteOffset,
                Width   = pc.Width,
                Height  = pc.Height,
                Theme   = Theme,
                Library = Library,
            };
            _plots.Add(container);
            RebuildMarkerInfoBoxesForContainer(container);
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
