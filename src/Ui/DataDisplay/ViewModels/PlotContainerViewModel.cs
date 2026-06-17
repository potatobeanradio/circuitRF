// ================================================================
//  PlotContainerViewModel.cs
//
//  Wraps one PlotViewModel and its dedicated PlotInspectorViewModel
//  together with canvas layout state (position, size, selection).
//
//  Owned by DataDisplayViewModel; visualised by PlotContainerView.
//  Marker info-box management lives in DataDisplayViewModel so that
//  info boxes are independent of the plot container's position.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class PlotContainerViewModel : ViewModelBase
{
    // ---- Identity ---------------------------------------------------

    public Guid Id { get; } = Guid.NewGuid();

    // ---- Owned child VMs --------------------------------------------

    public PlotViewModel           PlotVM    { get; }
    public PlotInspectorViewModel  Inspector { get; }

    // ---- Canvas layout (logical/model coordinates) ------------------
    //  Width/Height represent the GRAPH AREA (PlotControl inner size).
    //  ViewTotalWidth adds per-trace label strips so the ContentPresenter
    //  grows as traces are added while the graph region stays constant.

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _top;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;

    // ---- Per-trace Y-axis label strip collections -------------------

    public ObservableCollection<LabelStripViewModel> LeftLabelStrips  { get; } = new();
    public ObservableCollection<LabelStripViewModel> RightLabelStrips { get; } = new();

    // ---- Z-order (higher = on top; bumped on select) ----------------

    [ObservableProperty] private int _zIndex;

    // ---- Selection --------------------------------------------------

    [ObservableProperty] private bool _isSelected;

    // ---- Theme / library (propagated from DataDisplayViewModel) -----

    [ObservableProperty] private RenderTheme             _theme   = RenderTheme.Light;
    [ObservableProperty] private DataSourceLibraryViewModel?    _library;

    // ---- Derived properties -----------------------------------------

    /// <summary>True for Smith and Polar — resize maintains square aspect ratio.</summary>
    public bool IsSquareAspect =>
        PlotVM.Plot.PlotType is PlotType.Smith or PlotType.Polar;

    /// <summary>Current zoom level, read from parent DataDisplayViewModel.</summary>
    public double ZoomLevel => _parent.ZoomLevel;

    // ---- View (screen) layout — zoom+offset applied -----------------
    //  These are what the ItemContainerTheme actually binds to.
    //  Parent calls NotifyViewProperties() when zoom or offset changes.

    public double ViewLeft   => Left   * _parent.ZoomLevel + _parent.ViewOffsetX;
    public double ViewWidth  => Width  * _parent.ZoomLevel;
    public double ViewHeight =>
        (Height + TopLabelExtraLogical + BottomLabelExtraLogical) * _parent.ZoomLevel;

    /// <summary>
    /// Screen Y of the container's top edge.  Subtracts TopLabelExtraLogical
    /// so the canvas extends upward by exactly the title-clearance amount.
    /// PlotRenderer.ComputeViewport shifts the chart circle down inside the
    /// canvas by the same amount, keeping the circle stationary on screen.
    /// </summary>
    public double ViewTop =>
        (Top - TopLabelExtraLogical) * _parent.ZoomLevel + _parent.ViewOffsetY;

    /// <summary>
    /// Logical (pre-zoom) width of one label strip.
    /// Proportional to plot height so the strip and its font scale with both
    /// user resize and zoom — identical behaviour to Rect Y-axis margin labels.
    /// </summary>
    private double StripLogicalWidth => Math.Max(Height * 0.05, 10.0);

    /// <summary>
    /// Extra logical height added below the chart to accommodate overflow
    /// X-axis label rows on Smith / Polar plots with multiple traces.
    ///
    /// Mirrors the font-size and row-height formulas in
    /// AxesRenderer.DrawComplexXLabels so the canvas is always
    /// exactly tall enough for every label row without clipping.
    ///
    /// Uses PlotRenderer public margin constants so that
    /// changing a margin in one place automatically keeps this in sync.
    ///
    /// Returns 0 for Rect plots (their X labels live inside the Skia margin).
    /// </summary>
    private double BottomLabelExtraLogical
    {
        get
        {
            if (!PlotVM.Plot.PlotType.IsComplex()) return 0;
            var plot = PlotVM.Plot;

            bool hasCustomX = plot.CustomXLabelOn && !string.IsNullOrEmpty(plot.CustomXLabel);
            int  n          = hasCustomX ? 1 : Math.Max(1, plot.Traces.Count);

            // Mirror DrawComplexXLabels: lw = min(W,H)/200.  Once extra height
            // is added H > W, so effectiveH = W → lw = W/200.
            double lw         = Width / 200.0;
            // Must match DrawComplexXLabels: FontSizeLabel * lw = 8 * lw = h * 0.04
            // (mirrors AxisLabelControl's formula for the Y-axis strip labels).
            double fontSizePx = plot.Axes.FontSizeLabel * lw;
            if (fontSizePx < 4.0) return 0;   // matches DrawComplexXLabels guard

            double lineH = fontSizePx * 1.2;
            // Bottom edge of the last label row — must mirror DrawComplexXLabels exactly.
            // The 2.0 * lw term matches the downward nudge applied in the renderer;
            // change it here whenever you change the "+ 2f * lw" constant there.
            double rowsH = lineH * (n - 0.2) + fontSizePx * 0.5 + 2.0 * lw;

            // Compute the natural bottom space below the chart circle for a square canvas.
            // Mirrors ComputeViewport exactly using the public margin constants.
            // Circle is always sized with ComplexTopMarginBase regardless of title.
            double availW  = Width * (1.0 - 2.0 * PlotRenderer.ComplexSideMargin);
            double availH  = Width * (1.0 - PlotRenderer.ComplexTopMarginBase - PlotRenderer.ComplexBottomMargin);
            double side    = Math.Min(availW, availH);
            double natural = Width * (1.0 - PlotRenderer.ComplexTopMarginBase) - side;

            return Math.Max(0, rowsH - natural);
        }
    }

    /// <summary>
    /// Extra logical height added above the chart circle so the plot title
    /// is never clipped at the canvas top.
    ///
    /// When ViewHeight is grown by this amount,
    /// PlotRenderer.ComputeViewport shifts the chart circle DOWN
    /// by the same number of pixels (via the topExtra calculation), leaving the
    /// extra canvas pixels above the title text.  The chart circle is never
    /// resized — only the container height changes.
    ///
    /// Returns 0 when there is no title or the title fits in the natural
    /// PlotRenderer.ComplexTopMarginBase space.
    /// </summary>
    private double TopLabelExtraLogical
    {
        get
        {
            if (!PlotVM.Plot.PlotType.IsComplex()) return 0;
            if (string.IsNullOrEmpty(PlotVM.Plot.Title)) return 0;

            // Title is drawn at vpTop/2 + titleSz*0.35 (baseline).
            // Top of glyph: vpTop/2 − titleSz*0.65.
            // For no canvas clipping: vpTop ≥ titleSz * 1.3.
            // Natural top space = ComplexTopMarginBase * Width (same constant used
            // for circle sizing — no shrink, pure upward growth).
            double lw       = Width / 200.0;
            double titleSz  = PlotVM.Plot.Axes.FontSizeLabel * 1.4 * lw;
            double vpTopNat = PlotRenderer.ComplexTopMarginBase * Width;
            return Math.Max(0, titleSz * 1.3 - vpTopNat);
        }
    }

    /// <summary>
    /// Total screen width including per-trace Y-axis label strips on both sides.
    /// Used by the ItemContainerTheme so the ContentPresenter accommodates labels
    /// while the graph region (Width × ZoomLevel) stays constant.
    /// </summary>
    public double ViewTotalWidth =>
        (Width + (LeftLabelStrips.Count + RightLabelStrips.Count)
                 * StripLogicalWidth) * _parent.ZoomLevel;

    /// <summary>
    /// Canvas.Left position for the ContentPresenter, adjusted leftward so that
    /// the graph area (centre column) always sits at ViewLeft regardless of how
    /// many left-axis label strips are present.
    /// </summary>
    public double ViewContainerLeft =>
        ViewLeft - LeftLabelStrips.Count * StripLogicalWidth * _parent.ZoomLevel;

    /// <summary>Screen width of one label strip — used by AxisLabelControl Width binding.</summary>
    public double LabelStripViewWidth => StripLogicalWidth * _parent.ZoomLevel;

    /// <summary>
    /// Margin applied to the Y-axis label-strip ItemsControls so they span
    /// exactly the chart-circle area.  Top trims the title extra-canvas space;
    /// bottom trims the X-label extra-canvas space.  With VerticalAlignment=Stretch
    /// the resulting height is Height × ZoomLevel, which is what
    /// AxisLabelControl expects for its h × 0.04 font formula.
    /// </summary>
    public Thickness LabelStripMargin =>
        new Thickness(0, TopLabelExtraLogical    * _parent.ZoomLevel,
                      0, BottomLabelExtraLogical * _parent.ZoomLevel);

    // When model coordinates change, screen coordinates change too.
    partial void OnLeftChanged(double value)
    {
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewContainerLeft));
    }
    partial void OnTopChanged(double value)    => OnPropertyChanged(nameof(ViewTop));
    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(ViewWidth));
        OnPropertyChanged(nameof(ViewTotalWidth));
        OnPropertyChanged(nameof(ViewHeight));       // BottomLabelExtraLogical depends on Width
        OnPropertyChanged(nameof(ViewTop));          // TopLabelExtraLogical depends on Width
        OnPropertyChanged(nameof(LabelStripMargin)); // both extras depend on Width
    }
    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(ViewHeight));
        // StripLogicalWidth depends on Height, so all strip-derived properties change too.
        OnPropertyChanged(nameof(ViewTotalWidth));
        OnPropertyChanged(nameof(ViewContainerLeft));
        OnPropertyChanged(nameof(LabelStripViewWidth));
        UpdateLabelStrips(widthAndThemeOnly: true);
    }

    // Library is assigned via object-initializer AFTER the constructor body runs,
    // so subscriptions must go here, not in the constructor.
    partial void OnLibraryChanged(DataSourceLibraryViewModel? value)
    {
        if (value is null) return;
        value.LibraryChanged            += (_, _) => OnLibraryEntryCountChanged();
        value.Entries.CollectionChanged += (_, _) => OnLibraryEntryCountChanged();
    }

    // ---- PlotNeedsRedraw event (forwarded from Inspector) -----------

    public event EventHandler? PlotNeedsRedraw;

    // ---- Reference to owning DataDisplayViewModel ------------------

    private readonly DataDisplayViewModel _parent;

    // ---- Constructor ------------------------------------------------

    public PlotContainerViewModel(
        PlotViewModel          plotVM,
        PlotInspectorViewModel inspector,
        DataDisplayViewModel   parent)
    {
        PlotVM    = plotVM;
        Inspector = inspector;
        _parent   = parent;

        UpdateLabelStrips();
        SyncTableWidth();    // size the box correctly if this container starts as a Table plot

        // Forward redraw requests from the inspector to the view; also bump
        // AppearanceRevision on every strip so AxisLabelControl re-renders live
        // when a trace color or description changes (no strip rebuild needed).
        Inspector.PlotNeedsRedraw += (s, e) =>
        {
            PlotNeedsRedraw?.Invoke(this, e);
            foreach (var st in LeftLabelStrips)  st.AppearanceRevision++;
            foreach (var st in RightLabelStrips) st.AppearanceRevision++;
        };
        Inspector.PlotStructureChanged += (s, e) =>
        {
            CoerceAspectForPlotType();
            UpdateLabelStrips();
            OnPropertyChanged(nameof(IsSquareAspect));
            NotifyViewProperties();
        };

    }

    // ---- Library observer -------------------------------------------

    /// <summary>
    /// Called when an SNP is added, removed, or reloaded in the library.
    /// Rebuilds label strips so LabelStripViewModel.ShowFilePrefix
    /// reflects the new library count, and forces a plot redraw so the Rect
    /// Y-axis labels also pick up the updated showFilePrefix flag.
    /// </summary>
    private void OnLibraryEntryCountChanged()
    {
        UpdateLabelStrips();
        RequestPlotRedraw();
    }

    // ---- Methods called from PlotContainerView code-behind ----------

    /// <summary>Request exclusive selection of this container.</summary>
    public void RequestSelectOnly() => _parent.SelectOnly(this);

    /// <summary>Toggle this container's selection (Ctrl+click).</summary>
    public void RequestToggleSelect() => _parent.ToggleSelect(this);

    /// <summary>Move all currently-selected containers by (dx, dy) in logical coords.</summary>
    public void MoveSelected(double dx, double dy) => _parent.MoveSelected(dx, dy);

    /// <summary>
    /// Resize this container to an absolute target size in logical coords,
    /// respecting minimum size and aspect ratio.
    /// </summary>
    public void ResizeTo(double targetW, double targetH)
    {
        double newW = Math.Max(200, targetW);
        double newH = Math.Max(150, targetH);

        if (IsSquareAspect)
        {
            // The drag target height includes extra canvas for label overflow (title at
            // top, X-axis labels at bottom).  Strip both off to recover the square graph
            // area; ViewHeight re-adds them implicitly via the Extra properties.
            double size = Math.Max(200, Math.Max(newW,
                newH - TopLabelExtraLogical - BottomLabelExtraLogical));
            Width = Height = size;
        }
        else
        {
            Width  = newW;
            Height = newH;
        }
    }

    /// <summary>
    /// Re-shapes the container box to match the current plot type after a live plot-type switch.
    /// Smith/Polar → square (preserving the larger dimension); Table → natural column total;
    /// Rect → left as-is.
    /// </summary>
    private void CoerceAspectForPlotType()
    {
        if (IsSquareAspect)
        {
            double size = Math.Max(200, Math.Max(Width, Height));
            if (Width != size || Height != size) { Width = size; Height = size; }
        }
        else if (PlotVM.Plot.PlotType == PlotType.Table)
        {
            SyncTableWidth();
        }
    }

    /// <summary>
    /// Sets the container Width to the table's natural total column width so the table box exactly
    /// fits its columns (freq column + one per trace value column). Height is left to the user/drag.
    /// </summary>
    private void SyncTableWidth()
    {
        var plot = PlotVM.Plot;
        if (plot.PlotType != PlotType.Table) return;
        double newW = Math.Max(200, TableRenderer.TotalColumnWidth(plot));
        if (Math.Abs(Width - newW) > 0.5) Width = newW;
    }

    /// <summary>Called by parent when ZoomLevel or ViewOffset changes.</summary>
    public void NotifyViewProperties()
    {
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
        OnPropertyChanged(nameof(ViewWidth));
        OnPropertyChanged(nameof(ViewHeight));
        OnPropertyChanged(nameof(ViewTotalWidth));
        OnPropertyChanged(nameof(ViewContainerLeft));
        OnPropertyChanged(nameof(LabelStripViewWidth));
        OnPropertyChanged(nameof(LabelStripMargin));
        OnPropertyChanged(nameof(ZoomLevel));
        // Zoom changes LabelStripViewWidth, so push the new width to existing strip VMs.
        UpdateLabelStrips(widthAndThemeOnly: true);
    }

    /// <summary>
    /// Rebuilds or refreshes the per-trace label strip collections.
    /// Pass <c>widthAndThemeOnly = true</c> when only zoom or theme changed
    /// so that existing items are updated in place — no ItemsControl rebuild,
    /// no visual flicker.  Pass <c>false</c> (default) when the trace list
    /// itself has changed.
    /// </summary>
    /// <summary>
    /// Rebuilds or refreshes the per-trace label strip collections.
    ///
    /// Strip rules:
    ///   • Smith / Polar: no strips — all labels are drawn inside the Skia canvas.
    ///   • Rect + custom Y label non-empty: no strips — global label is in the margin.
    ///   • Rect + no custom Y label: first left/right trace goes into the Skia margin;
    ///     external strips start at trace index 1.
    ///
    /// Pass <c>widthAndThemeOnly = true</c> when only zoom or theme changed so that
    /// existing items are updated in place — no ItemsControl rebuild, no visual flicker.
    /// Pass <c>false</c> (default) when the trace list itself has changed.
    /// </summary>
    /// <summary>
    /// Rebuilds or refreshes the per-trace label strip collections.
    ///
    /// Strip rules (apply to all plot types including Smith / Polar):
    ///   • No custom Y label: one strip per left-axis trace, innermost = trace 0.
    ///   • Custom Y label set: strip 0 shows the custom text (trace colour overridden to
    ///     theme text colour); remaining traces still get their own strips at positions 1..N-1.
    ///   • Same logic applies independently to right-axis strips.
    ///
    /// Pass <c>widthAndThemeOnly = true</c> when only zoom or theme changed so that
    /// existing items are updated in place — no ItemsControl rebuild, no visual flicker.
    /// Pass <c>false</c> (default) when the trace list itself has changed.
    /// </summary>
    public void UpdateLabelStrips(bool widthAndThemeOnly = false)
    {
        var    plot      = PlotVM.Plot;
        double sw        = LabelStripViewWidth;   // = StripLogicalWidth * ZoomLevel
        var    th        = Theme;
        bool   isComplex = plot.PlotType.IsComplex();

        // Rect plots render Y-axis labels inside the Skia canvas margin.
        // External label strips are only used for Smith / Polar.

        if (!widthAndThemeOnly)
        {
            LeftLabelStrips.Clear();
            RightLabelStrips.Clear();

            if (isComplex)
            {
                bool hasCustomY  = plot.CustomYLabelOn  && !string.IsNullOrEmpty(plot.CustomYLabel);
                bool hasCustomY2 = plot.CustomY2LabelOn && !string.IsNullOrEmpty(plot.CustomY2Label);

                // Show filename prefix when settings force it, or when multiple SNPs are loaded.
                bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                    (Library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);

                // Compute minimal labels over all traces in the plot.
                bool alwaysSource = AppSettingsViewModel.Instance.AlwaysDisplayDataSourcePrefix;
                var  allLabels    = TraceLabeler.ComputeMinimalLabels(plot.Traces, alwaysSource);
                var  labelMap     = new Dictionary<Trace, string>();
                for (int i = 0; i < plot.Traces.Count; i++)
                    labelMap[plot.Traces[i]] = allLabels[i];

                var leftTraces  = plot.LeftAxisTraces;
                var rightTraces = plot.RightAxisTraces;

                // Custom Y label: one strip showing the custom text — no per-trace strips.
                // No custom label: one strip per trace, AutoLabel set to the computed minimal label.
                if (hasCustomY && leftTraces.Count > 0)
                {
                    LeftLabelStrips.Add(new LabelStripViewModel(leftTraces[0], false, sw, th)
                        { CustomLabel = plot.CustomYLabel, ShowFilePrefix = showFilePrefix });
                }
                else
                {
                    foreach (var t in leftTraces)
                        LeftLabelStrips.Add(new LabelStripViewModel(t, false, sw, th)
                        {
                            ShowFilePrefix = showFilePrefix,
                            AutoLabel      = labelMap.GetValueOrDefault(t)
                        });
                }

                if (hasCustomY2 && rightTraces.Count > 0)
                {
                    RightLabelStrips.Add(new LabelStripViewModel(rightTraces[0], true, sw, th)
                        { CustomLabel = plot.CustomY2Label, ShowFilePrefix = showFilePrefix });
                }
                else
                {
                    foreach (var t in rightTraces)
                        RightLabelStrips.Add(new LabelStripViewModel(t, true, sw, th)
                        {
                            ShowFilePrefix = showFilePrefix,
                            AutoLabel      = labelMap.GetValueOrDefault(t)
                        });
                }
            }
        }
        else
        {
            if (isComplex)
            {
                foreach (var s in LeftLabelStrips)  { s.StripWidth = sw; s.Theme = th; }
                foreach (var s in RightLabelStrips) { s.StripWidth = sw; s.Theme = th; }
            }
        }

        OnPropertyChanged(nameof(ViewHeight));       // BottomLabelExtraLogical depends on trace count
        OnPropertyChanged(nameof(ViewTop));          // TopLabelExtraLogical depends on title / plot type
        OnPropertyChanged(nameof(LabelStripMargin)); // both extras change with traces and title
        OnPropertyChanged(nameof(ViewTotalWidth));
        OnPropertyChanged(nameof(ViewContainerLeft));
    }

    /// <summary>
    /// Called by PlotContainerView when PlotControl.PlotChanged fires.
    /// Refreshes axis/trace bindings and asks DataDisplayViewModel to
    /// rebuild info boxes for this container's markers.
    /// </summary>
    public void OnPlotChanged(object? sender, EventArgs e)
    {
        PlotVM.OnPlotChanged(sender, e);
        SyncTableWidth();    // re-fits box after column-drag resize
        UpdateLabelStrips();
        _parent.OnContainerPlotChanged(this);
    }

    /// <summary>
    /// Called by PlotContainerView when PlotControl.MarkerMoved fires.
    /// Routes to DataDisplayViewModel so existing info boxes refresh
    /// their text without a full rebuild.
    /// </summary>
    public void OnMarkerMoved() => _parent.OnContainerMarkerMoved(this);

    /// <summary>
    /// Fires PlotNeedsRedraw so PlotContainerView calls InvalidateVisual
    /// on its PlotControl without going through the full PlotChanged pipeline.
    /// </summary>
    public void RequestPlotRedraw() => PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Returns the lowest m-number not currently used by any marker on any
    /// plot in the whole DataDisplay.
    /// </summary>
    public int GetNextMarkerIndex() => _parent.GetNextMarkerIndex();

    /// <summary>Remove this container from the DataDisplay.</summary>
    public void RequestRemoveSelf() => _parent.RemovePlot(this);

    /// <summary>
    /// Returns the logical right edge of the viewable DataDisplay area.
    /// Pass the DataDisplay control's actual screen width (Bounds.Width).
    /// </summary>
    public double GetViewableRightEdge(double dataDisplayScreenWidth)
        => _parent.GetViewableRightEdge(dataDisplayScreenWidth);


    // ---- Undo / Redo hooks ------------------------------------------

    /// <summary>Pushes an already-applied command onto the parent's undo stack.</summary>
    internal void PushUndoCommand(IUndoableCommand cmd) => _parent.UndoRedo.Push(cmd);

    /// <summary>
    /// Called at the start of a move drag (threshold first crossed).
    /// Routes to DataDisplayViewModel to snapshot positions of all selected items.
    /// </summary>
    internal void BeginMove() => _parent.BeginMoveOperation();

    /// <summary>
    /// Called when the move drag ends.
    /// Routes to DataDisplayViewModel to push a undo command with start/end positions.
    /// </summary>
    internal void EndMove() => _parent.EndMoveOperation();

    /// <summary>
    /// Called by PlotContainerView when PlotControl fires its MarkerAdded event.
    /// Routes to DataDisplayViewModel to record an undo command.
    /// </summary>
    public void OnMarkerAdded(Marker marker, Trace trace)
        => _parent.RecordMarkerAdded(marker, trace, this);

    /// <summary>
    /// Removes the given marker from its trace with undo/redo support.
    /// Called by PlotControl for marker-remove paths that bypass MarkerInfoBoxViewModel.
    /// </summary>
    public void RemoveMarkerWithUndo(Marker marker, Trace trace)
        => _parent.RemoveMarkerWithUndo(marker, trace, this);

    /// <summary>
    /// Returns the MarkerInfoBoxViewModel for the given marker,
    /// or null if no info box currently exists (e.g. before the first PlotChanged).
    /// </summary>
    public MarkerInfoBoxViewModel? FindMarkerInfoBoxVm(Marker marker) =>
        _parent.MarkerInfoBoxes.FirstOrDefault(m => m.Marker == marker);

    /// <summary>
    /// Returns all marker info-box VMs currently associated with this container.
    /// Used by PlotExporter to draw info boxes on the export canvas.
    /// </summary>
    public IReadOnlyList<MarkerInfoBoxViewModel> GetMarkerInfoBoxes() =>
        _parent.MarkerInfoBoxes.Where(m => m.Container == this).ToList();

    /// <summary>
    /// Returns the markers belonging to this container whose InfoBoxes are selected.
    /// Called by PlotControl each render frame to drive the
    /// per-glyph selection highlight.
    /// </summary>
    public IEnumerable<Marker> GetSelectedMarkers() =>
        _parent.MarkerInfoBoxes
               .Where(m => m.IsSelected && m.Container == this)
               .Select(m => m.Marker);

    /// <summary>
    /// Selects all marker InfoBoxes that belong to this container,
    /// deselecting all plots and other containers' InfoBoxes.
    /// </summary>
    public void SelectAllMarkers() => _parent.SelectAllMarkersForContainer(this);
}
