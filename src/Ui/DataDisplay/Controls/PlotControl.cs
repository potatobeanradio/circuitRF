// ================================================================
//  PlotControl.cs  —  Avalonia custom control for Plot rendering
//
//  Interaction:
//    Left-drag         → pan Window + WindowSecondary together
//    Right-drag        → pan WindowSecondary Y only (when visible)
//    Ctrl+scroll       → zoom centred on cursor world position
//    Double-click      → opens Plot Inspector flyout (or adds marker)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;
using Material.Icons;
using Material.Icons.Avalonia;
using RfCore;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.DataDisplay.Controls
{
    public class PlotControl : Control
    {
        // ============================================================
        //  Direct Property: Plot
        // ============================================================

        public static readonly DirectProperty<PlotControl, Plot?> PlotProperty =
            AvaloniaProperty.RegisterDirect<PlotControl, Plot?>(
                nameof(Plot),
                o => o.Plot,
                (o, v) => o.Plot = v);

        private Plot? _plot;

        /// <summary>The Plot model to render.  Bind from AXAML or set in code-behind.</summary>
        public Plot? Plot
        {
            get => _plot;
            set
            {
                SetAndRaise(PlotProperty, ref _plot, value);
                InvalidateVisual();
            }
        }

        // ============================================================
        //  Direct Property: PlotTheme
        // ============================================================

        public static readonly DirectProperty<PlotControl, RenderTheme> PlotThemeProperty =
            AvaloniaProperty.RegisterDirect<PlotControl, RenderTheme>(
                nameof(PlotTheme),
                o => o.PlotTheme,
                (o, v) => o.PlotTheme = v);

        private RenderTheme _theme = RenderTheme.Light;

        /// <summary>Visual theme.  Defaults to <see cref="RenderTheme.Light"/>.</summary>
        public RenderTheme PlotTheme
        {
            get => _theme;
            set
            {
                SetAndRaise(PlotThemeProperty, ref _theme, value);
                InvalidateVisual();
            }
        }

        // ============================================================
        //  Plain CLR property: ZoomFactor
        // ============================================================

        /// <summary>
        /// Zoom factor per scroll-wheel notch.
        /// 1.15 = each notch zooms in or out by 15 %.
        /// </summary>
        public double ZoomFactor { get; set; } = 1.15;

        // ============================================================
        //  Styled Property: EnablePanning
        // ============================================================

        public static readonly StyledProperty<bool> EnablePanningProperty =
            AvaloniaProperty.Register<PlotControl, bool>(nameof(EnablePanning), defaultValue: true);

        /// <summary>
        /// When false, left-drag does not pan the axes window.
        /// Events bubble up to the parent so PlotContainerView can handle moving.
        /// </summary>
        public bool EnablePanning
        {
            get => GetValue(EnablePanningProperty);
            set => SetValue(EnablePanningProperty, value);
        }

        // ============================================================
        //  Events
        // ============================================================

        /// <summary>
        /// Raised after every pan, zoom, or reset so the host can
        /// update derived state (marker readouts, axis-limit editors, …).
        /// </summary>
        public event EventHandler? PlotChanged;

        /// <summary>
        /// Raised when the user chooses "Delete Plot" from the context menu.
        /// The host (PlotContainerView) should remove this plot from the DataDisplay.
        /// </summary>
        public event EventHandler? DeletePlotRequested;

        /// <summary>
        /// Raised immediately after a marker is added to a trace via the context menu.
        /// The host (PlotContainerView) should record an undo command.
        /// Parameters: (Marker added, Trace it was added to).
        /// </summary>
        public event Action<Marker, Trace>? MarkerAdded;

        /// <summary>
        /// Raised when a marker is moved (freq or stability position updated) or added/removed.
        /// The host should refresh marker info boxes on this event.
        /// </summary>
        public event EventHandler? MarkerMoved;

        // ============================================================
        //  Private interaction state
        // ============================================================

        private PlotDetail  _renderDetail        = PlotDetail.Full;
        private bool        _isDragging;
        private bool        _isDraggingSecondary;
        private Point       _dragStartScreen;
        private bool        _rightButtonDown;
        private bool        _rightDragOccurred;
        private ContextMenu _contextMenu = null!;
        private MaterialIcon _iconAxesLocked = new MaterialIcon();

        // Marker symbol drag state
        private Marker? _draggingMarker;
        private Trace?  _draggingTrace;

        // Right-click state
        private Point    _lastRightClickPos;
        private Marker?  _rightClickedMarker;
        private Trace?   _rightClickedTrace;
        private MenuItem? _addMarkerMenuItem;
        private MenuItem? _selectAllMarkersMenuItem;

        // Inspector flyout state
        private Flyout?                  _inspectorFlyout;
        private PlotInspectorViewModel?  _inspectorVm;
        private PlotInspectorView?       _inspectorView;

        // Table column-resize drag state
        private bool   _tableColResizeDragging;
        private int    _tableResizeColIndex;
        private double _tableResizeStartWidth;
        private Point  _tableResizeStartPt;

        // Table right-click context
        private Trace? _rightClickedDataTrace;
        private double _rightClickedDataFreq = double.NaN;

        // Table marker drag state
        private Marker? _tableDraggingMarker;
        private Trace?  _tableDraggingTrace;
        private int     _tableDraggingColIndex;

        // Axes-labels flyout state
        private Flyout?              _labelsflyout;
        private AxesLabelsViewModel? _labelsVm;

        // Axes-limits flyout state
        private Flyout?              _axesLimitsFlyout;
        private AxesLimitsViewModel? _axesLimitsVm;

        /// <summary>
        /// Returns the next globally-unique marker index (lowest unused m-number).
        /// Set by PlotContainerView.axaml.cs once the DataContext is resolved.
        /// Falls back to trace.Markers.Count+1 when null.
        /// </summary>
        public Func<int>? NextMarkerIndexProvider { get; set; }

        /// <summary>
        /// Looks up the <see cref="MarkerInfoBoxViewModel"/> for a given marker.
        /// Set by PlotContainerView.axaml.cs once the DataContext is resolved.
        /// </summary>
        public Func<Marker, MarkerInfoBoxViewModel?>? FindMarkerInfoBoxVmProvider { get; set; }

        /// <summary>
        /// Returns the <see cref="PlotContainerViewModel"/> that hosts this control.
        /// Set by PlotContainerView.axaml.cs; used by <see cref="PlotExporter"/> to
        /// retrieve label strips, marker info-box positions, and screen-space layout.
        /// </summary>
        public Func<PlotContainerViewModel?>? ContainerProvider { get; set; }

        /// <summary>
        /// Returns the set of markers that currently have selected InfoBoxes, filtered
        /// to this container.
        /// </summary>
        public Func<IEnumerable<Marker>>? SelectedMarkersProvider { get; set; }

        // ============================================================
        //  Direct Property: Library
        // ============================================================

        public static readonly DirectProperty<PlotControl, DataSourceLibraryViewModel?> LibraryProperty =
            AvaloniaProperty.RegisterDirect<PlotControl, DataSourceLibraryViewModel?>(
                nameof(Library),
                o => o.Library,
                (o, v) => o.Library = v);

        private DataSourceLibraryViewModel? _library;

        /// <summary>
        /// SNP library reference forwarded to the flyout PlotInspectorViewModel.
        /// </summary>
        public DataSourceLibraryViewModel? Library
        {
            get => _library;
            set => SetAndRaise(LibraryProperty, ref _library, value);
        }

        // ============================================================
        //  Constructor
        // ============================================================

        public PlotControl()
        {
            Focusable = true;
            PointerPressed      += OnPointerPressed;
            PointerMoved        += OnPointerMoved;
            PointerReleased     += OnPointerReleased;
            PointerWheelChanged += OnPointerWheel;

            ((Avalonia.Controls.IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        }

        // ============================================================
        //  Visual tree attach / detach — subscribe to window resize
        // ============================================================

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
                topLevel.SizeChanged += OnTopLevelSizeChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
                topLevel.SizeChanged -= OnTopLevelSizeChanged;
        }

        private void OnTopLevelSizeChanged(object? sender, SizeChangedEventArgs e) =>
            InvalidateVisual();

        // ============================================================
        //  Context menu
        // ============================================================

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var icon = new MaterialIcon { Kind = MaterialIconKind.Settings };
            var item1 = new MenuItem { Header = "Plot Properties…", Icon = icon };
            item1.Click += OnMenuPlotProperties;

            icon = new MaterialIcon { Kind = MaterialIconKind.Numeric };
            var item2 = new MenuItem { Header = "Axes Limits", Icon = icon };
            item2.Click += OnMenuActionTwo;

            icon = new MaterialIcon { Kind = MaterialIconKind.Alphabetical };
            var item3 = new MenuItem { Header = "Axes Labels…", Icon = icon };
            item3.Click += OnMenuActionThree;

            icon = new MaterialIcon { Kind = MaterialIconKind.TriangleDown };
            var item4 = new MenuItem { Header = "Add Marker", Icon = icon };
            _addMarkerMenuItem = item4;

            icon = new MaterialIcon { Kind = MaterialIconKind.SelectGroup };
            var itemSelectAll = new MenuItem
            {
                Header    = "Select All Markers",
                Icon      = icon,
                IsEnabled = _plot?.Traces.Any(t => t.Markers.Count > 0) ?? false,
            };
            itemSelectAll.Click += (_, _) => ContainerProvider?.Invoke()?.SelectAllMarkers();
            _selectAllMarkersMenuItem = itemSelectAll;

            _iconAxesLocked.Kind = _plot?.Axes.LockedPanning ?? false
                ? MaterialIconKind.CheckboxOutline
                : MaterialIconKind.CheckboxBlankOutline;
            var item5 = new MenuItem { Header = "Lock Axes Panning", Icon = _iconAxesLocked };
            item5.Click += (_, _) =>
            {
                if (_plot is null) return;
                _plot.Axes.LockedPanning ^= true;
                _iconAxesLocked.Kind = _plot.Axes.LockedPanning
                    ? MaterialIconKind.CheckboxOutline
                    : MaterialIconKind.CheckboxBlankOutline;
            };

            icon = new MaterialIcon { Kind = MaterialIconKind.FitToPageOutline };
            var item6 = new MenuItem { Header = "Autoscale", Icon = icon };
            item6.Click += OnMenuActionAutoscale;

            icon = new MaterialIcon { Kind = MaterialIconKind.ContentCopy };
            var item7 = new MenuItem { Header = "Copy", Icon = icon };
            item7.Click += async (_, _) => await OnMenuCopyPlot();

            icon = new MaterialIcon { Kind = MaterialIconKind.FileExportOutline };
            var item8 = new MenuItem { Header = "Export…", Icon = icon };
            item8.Click += OnMenuExport;

            icon = new MaterialIcon { Kind = MaterialIconKind.DeleteOutline };
            var item9 = new MenuItem { Header = "Delete Plot", Icon = icon };
            item9.Click += (_, _) => DeletePlotRequested?.Invoke(this, EventArgs.Empty);

            menu.Items.Add(item1);
            menu.Items.Add(item2);
            menu.Items.Add(item3);
            menu.Items.Add(new Separator());
            menu.Items.Add(item4);
            menu.Items.Add(itemSelectAll);
            menu.Items.Add(item5);
            menu.Items.Add(item6);
            menu.Items.Add(new Separator());
            menu.Items.Add(item7);
            menu.Items.Add(item8);
            menu.Items.Add(new Separator());
            menu.Items.Add(item9);

            menu.Opening += (_, _) =>
            {
                _iconAxesLocked.Kind = _plot?.Axes.LockedPanning ?? false
                    ? MaterialIconKind.CheckboxOutline
                    : MaterialIconKind.CheckboxBlankOutline;
            };

            return menu;
        }

        private void OnMenuPlotProperties(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ShowPlotInspector();
        }

        private void ShowPlotInspector(int scrollToTraceIndex = -1)
        {
            if (_plot is null) return;

            _inspectorFlyout?.Hide();

            // Reuse the container's single inspector so flyout edits and the Properties-pane inspector
            // stay in sync (one VM per plot). Fall back to a fresh VM only if no container is wired
            // (e.g. PlotControl used outside PlotContainerView).
            _inspectorVm = ContainerProvider?.Invoke()?.Inspector
                           ?? new PlotInspectorViewModel(_plot, () => { }, _library);

            // Point the shared inspector's Close button at this flyout while it is open.
            _inspectorVm.SetCloseAction(() => _inspectorFlyout?.Hide());
            _inspectorVm.PlotNeedsRedraw += OnInspectorPlotNeedsRedraw;

            var view = new PlotInspectorView { DataContext = _inspectorVm };
            _inspectorView = view;

            var (flyoutAnchor, hOffset, vOffset) = ComputeStableAnchor(
                new Point(Bounds.Width, 0), PlacementMode.RightEdgeAlignedTop);

            _inspectorFlyout = new Flyout
            {
                Content              = view,
                Placement            = PlacementMode.RightEdgeAlignedTop,
                HorizontalOffset     = hOffset,
                VerticalOffset       = vOffset,
                ShowMode             = FlyoutShowMode.Standard,
                OverlayInputPassThroughElement = this,
            };

            _inspectorFlyout.Closed += (_, _) =>
            {
                if (_inspectorVm is not null)
                {
                    _inspectorVm.PlotNeedsRedraw -= OnInspectorPlotNeedsRedraw;
                    // Restore a no-op so a stale flyout reference is never invoked by the Properties pane.
                    _inspectorVm.SetCloseAction(() => { });
                }
            };

            _inspectorFlyout.ShowAt(flyoutAnchor);

            if (scrollToTraceIndex >= 0)
                view.ScrollToTrace(scrollToTraceIndex);
        }

        private void OnInspectorPlotNeedsRedraw(object? sender, EventArgs e)
        {
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnMenuActionTwo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => ShowAxesLimitsFlyout();

        private void ShowAxesLimitsFlyout()
        {
            if (_plot is null) return;

            _axesLimitsFlyout?.Hide();

            _axesLimitsVm = new AxesLimitsViewModel(_plot, () => _axesLimitsFlyout?.Hide());
            _axesLimitsVm.PlotNeedsRedraw += (_, _) =>
            {
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
            };

            var view = new AxesLimitsView { DataContext = _axesLimitsVm };

            var (flyoutAnchor, hOffset, vOffset) = ComputeStableAnchor(
                new Point(0, Bounds.Height), PlacementMode.BottomEdgeAlignedLeft);

            _axesLimitsFlyout = new Flyout
            {
                Content          = view,
                Placement        = PlacementMode.BottomEdgeAlignedLeft,
                HorizontalOffset = hOffset,
                VerticalOffset   = vOffset,
                ShowMode         = FlyoutShowMode.Standard,
                OverlayInputPassThroughElement = this,
            };

            _axesLimitsFlyout.ShowAt(flyoutAnchor);
        }

        private void OnMenuActionThree(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => ShowAxesLabelsFlyout();

        private void ShowAxesLabelsFlyout()
        {
            if (_plot is null) return;

            _labelsflyout?.Hide();

            _labelsVm = new AxesLabelsViewModel(_plot, () => _labelsflyout?.Hide());
            _labelsVm.PlotNeedsRedraw += (_, _) =>
            {
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
            };

            var view = new AxesLabelsFlyout { DataContext = _labelsVm };

            var (flyoutAnchor, hOffset, vOffset) = ComputeStableAnchor(
                new Point(0, Bounds.Height), PlacementMode.BottomEdgeAlignedLeft);

            _labelsflyout = new Flyout
            {
                Content          = view,
                Placement        = PlacementMode.BottomEdgeAlignedLeft,
                HorizontalOffset = hOffset,
                VerticalOffset   = vOffset,
                ShowMode         = FlyoutShowMode.Standard,
                OverlayInputPassThroughElement = this,
            };

            _labelsflyout.ShowAt(flyoutAnchor);
        }

        private void OnMenuActionAutoscale(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _plot?.Autoscale();
        }

        private async void OnMenuExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_plot is null) return;
            bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                (_library?.Entries.Where(entry => entry.Snp is not null && !entry.Snp.IsEmpty).Count() ?? 0) > 1);
            await PlotExporter.ExportAsync(this, _plot, _theme, showFilePrefix, ContainerProvider?.Invoke());
        }

        private async Task OnMenuCopyPlot()
        {
            if (_plot is null) return;
            bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                (_library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);
            await PlotExporter.CopyPlotToClipboardAsync(
                this, _plot, _theme, showFilePrefix, ContainerProvider?.Invoke());
        }

        private void RefreshAddMarkerSubmenu()
        {
            if (_addMarkerMenuItem is null) return;

            _addMarkerMenuItem.Items.Clear();
            bool hasTraces  = _plot?.Traces.Count > 0;
            bool hasMarkers = _plot?.Traces.Any(t => t.Markers.Count > 0) ?? false;
            _addMarkerMenuItem.IsEnabled = hasTraces;
            if (_selectAllMarkersMenuItem is not null)
                _selectAllMarkersMenuItem.IsEnabled = hasMarkers;

            if (!hasTraces) return;

            foreach (var t in _plot!.Traces)
            {
                var sub      = new MenuItem { Header = t.Description };
                var captured = t;
                sub.Click += (_, _) => AddMarkerAtCanvasPoint(captured, _lastRightClickPos);
                _addMarkerMenuItem.Items.Add(sub);
            }
        }

        // ============================================================
        //  Avalonia render override
        // ============================================================

        public override void Render(DrawingContext context)
        {
            bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                (_library?.Entries.Where(e => e.Snp is not null && !e.Snp.IsEmpty).Count() ?? 0) > 1);

            var selectedMarkers = SelectedMarkersProvider?.Invoke()?.ToHashSet();
            SkiaSharp.SKColor selColor = selectedMarkers?.Count > 0
                ? RenderTheme.GetTransparentAccent(RenderTheme.SelectionAlpha)
                : default;

            float zoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);

            context.Custom(new PlotDrawOperation(
                new Rect(Bounds.Size),
                _plot,
                _theme,
                _renderDetail,
                showFilePrefix,
                selectedMarkers,
                selColor,
                zoom));
        }

        // ============================================================
        //  ICustomDrawOperation implementation (private nested class)
        // ============================================================

        private sealed class PlotDrawOperation : ICustomDrawOperation
        {
            private readonly Rect              _bounds;
            private readonly Plot?             _plot;
            private readonly RenderTheme       _theme;
            private readonly PlotDetail        _detail;
            private readonly bool              _showFilePrefix;
            private readonly HashSet<Marker>?  _selectedMarkers;
            private readonly SKColor           _selectionColor;
            private readonly float             _zoomLevel;

            public PlotDrawOperation(
                Rect             bounds,
                Plot?            plot,
                RenderTheme      theme,
                PlotDetail       detail,
                bool             showFilePrefix,
                HashSet<Marker>? selectedMarkers = null,
                SKColor          selectionColor  = default,
                float            zoomLevel       = 1f)
            {
                _bounds          = bounds;
                _plot            = plot;
                _theme           = theme;
                _detail          = detail;
                _showFilePrefix  = showFilePrefix;
                _selectedMarkers = selectedMarkers;
                _selectionColor  = selectionColor;
                _zoomLevel       = zoomLevel;
            }

            public bool Equals(ICustomDrawOperation? other) => false;

            public Rect Bounds => _bounds;

            public bool HitTest(Point p) => _bounds.Contains(p);

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature is null) return;

                using var lease = leaseFeature.Lease();
                var canvas     = lease.SkCanvas;
                var canvasSize = (W: _bounds.Width, H: _bounds.Height);

                if (_plot is null)
                {
                    canvas.Clear(SKColors.Transparent);
                    return;
                }

                PlotRenderer.Draw(canvas, canvasSize, _plot, _detail, _theme, _showFilePrefix,
                    selectedMarkers: _selectedMarkers, selectionColor: _selectionColor,
                    zoomLevel: _zoomLevel);
            }

            public void Dispose() { }
        }

        // ============================================================
        //  Pointer — press
        // ============================================================

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_plot is null) return;
            Focus();

            var props = e.GetCurrentPoint(this).Properties;
            _dragStartScreen = e.GetPosition(this);

            // ---- Table view interaction ----
            if (_plot.PlotType == PlotType.Table)
            {
                var pos  = e.GetPosition(this);
                float tableZoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                var hitResult = TableRenderer.HitTest((float)pos.X, (float)pos.Y, _plot, (Bounds.Width, Bounds.Height), tableZoom);

                if (props.IsLeftButtonPressed)
                {
                    if (hitResult.Kind == TableHitKind.ResizeHandle)
                    {
                        _tableColResizeDragging = true;
                        _tableResizeColIndex    = hitResult.ResizeColIndex;
                        _tableResizeStartPt     = pos;
                        var resCols = TableRenderer.BuildColumns(_plot);
                        _tableResizeStartWidth = hitResult.ResizeColIndex < resCols.Count
                            && resCols[hitResult.ResizeColIndex].Kind == TableColKind.TraceValue
                            ? _plot.Traces[resCols[hitResult.ResizeColIndex].FirstTraceIndex].ColumnWidth
                            : _plot.ColumnWidth;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }

                    if (hitResult.Kind == TableHitKind.MarkerGlyph && hitResult.HitMarker is not null && hitResult.HitTrace is not null)
                    {
                        _tableDraggingMarker   = hitResult.HitMarker;
                        _tableDraggingTrace    = hitResult.HitTrace;
                        _tableDraggingColIndex = hitResult.ColIndex;

                        var infoVm = FindMarkerInfoBoxVmProvider?.Invoke(hitResult.HitMarker);
                        if (infoVm is not null)
                        {
                            bool isCtrlOrMeta = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                                                e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                            if (isCtrlOrMeta) infoVm.ToggleSelect();
                            else              infoVm.SelectOnly();
                        }

                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
                else if (props.IsRightButtonPressed)
                {
                    _rightButtonDown       = true;
                    _rightDragOccurred     = false;
                    _lastRightClickPos     = pos;
                    _rightClickedDataTrace = null;
                    _rightClickedDataFreq  = double.NaN;

                    if (hitResult.Kind == TableHitKind.TraceHeader)
                    {
                        _rightClickedTrace = hitResult.HitTrace;
                    }
                    else if (hitResult.RowIndex >= 0)
                    {
                        _rightClickedDataTrace = hitResult.HitTrace;
                        var rcCols = TableRenderer.BuildColumns(_plot);
                        if (hitResult.ColIndex >= 0 && hitResult.ColIndex < rcCols.Count)
                        {
                            var rcCol = rcCols[hitResult.ColIndex];
                            if (hitResult.RowIndex >= 0 && hitResult.RowIndex < rcCol.XValues.Length)
                                _rightClickedDataFreq = rcCol.XValues[hitResult.RowIndex];
                        }
                    }
                }
                return;
            }

            if (props.IsLeftButtonPressed)
            {
                var hit = HitTestMarker(e.GetPosition(this));
                if (hit.HasValue)
                {
                    (_draggingMarker, _draggingTrace) = hit.Value;

                    var infoVm = FindMarkerInfoBoxVmProvider?.Invoke(hit.Value.Marker);
                    if (infoVm is not null)
                    {
                        bool isCtrlOrMeta = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                                            e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                        if (isCtrlOrMeta) infoVm.ToggleSelect();
                        else              infoVm.SelectOnly();
                    }

                    _renderDetail = PlotDetail.Full;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }

                if (EnablePanning)
                {
                    _isDragging                      = true;
                    _plot.Axes.WindowState           = _plot.Axes.Window;
                    _plot.Axes.WindowSecondaryState  = _plot.Axes.WindowSecondary;
                    _renderDetail                    = PlotDetail.Full;
                    e.Pointer.Capture(this);
                }
            }
            else if (props.IsRightButtonPressed)
            {
                _rightButtonDown   = true;
                _rightDragOccurred = false;
                _lastRightClickPos = _dragStartScreen;

                var markerHit = HitTestMarker(_dragStartScreen);
                if (markerHit.HasValue)
                {
                    (_rightClickedMarker, _rightClickedTrace) = markerHit.Value;
                    return;
                }

                _rightClickedMarker = null;
                _rightClickedTrace  = null;

                if (_plot.Axes.ShowSecondary)
                {
                    _isDraggingSecondary            = true;
                    _plot.Axes.WindowSecondaryState = _plot.Axes.WindowSecondary;
                    _renderDetail                   = PlotDetail.Full;
                    e.Pointer.Capture(this);
                }
            }
        }

        // ============================================================
        //  Pointer — move (pan)
        // ============================================================

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_plot is null) return;

            var current = e.GetPosition(this);

            // ---- Table column resize drag ----
            if (_tableColResizeDragging)
            {
                float resizeZoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                double dx        = (current.X - _tableResizeStartPt.X) / resizeZoom;
                double newWidth  = Math.Max(TableRenderer.MinColumnWidth, _tableResizeStartWidth + dx);
                var drCols = TableRenderer.BuildColumns(_plot);
                if (_tableResizeColIndex < drCols.Count
                    && drCols[_tableResizeColIndex].Kind == TableColKind.TraceValue)
                    _plot.Traces[drCols[_tableResizeColIndex].FirstTraceIndex].ColumnWidth = newWidth;
                else
                    _plot.ColumnWidth = newWidth;
                InvalidateVisual();
                return;
            }

            // ---- Table marker glyph drag ----
            if (_tableDraggingMarker is not null && _tableDraggingTrace is not null)
            {
                float tableZoom2 = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                var hitResult = TableRenderer.HitTest((float)current.X, (float)current.Y, _plot, (Bounds.Width, Bounds.Height), tableZoom2);
                if (hitResult.Kind == TableHitKind.DataCell || hitResult.Kind == TableHitKind.MarkerGlyph)
                {
                    bool traceChanged = false;
                    if (hitResult.ColIndex > 0 && hitResult.HitTrace is not null &&
                        hitResult.HitTrace != _tableDraggingTrace)
                    {
                        _tableDraggingTrace!.Markers.Remove(_tableDraggingMarker);
                        hitResult.HitTrace.Markers.Add(_tableDraggingMarker);
                        _tableDraggingTrace    = hitResult.HitTrace;
                        _tableDraggingColIndex = hitResult.ColIndex;
                        traceChanged           = true;
                    }

                    int mti = _plot.Traces.IndexOf(_tableDraggingTrace);
                    if (mti >= 0)
                    {
                        var mCols   = TableRenderer.BuildColumns(_plot);
                        var mValCol = mCols.FirstOrDefault(c => c.Kind == TableColKind.TraceValue && c.FirstTraceIndex == mti);
                        if (mValCol != null && hitResult.RowIndex >= 0 && hitResult.RowIndex < mValCol.XValues.Length)
                            _tableDraggingMarker.Freq = mValCol.XValues[hitResult.RowIndex];
                    }

                    if (traceChanged)
                        PlotChanged?.Invoke(this, EventArgs.Empty);
                    else
                        MarkerMoved?.Invoke(this, EventArgs.Empty);

                    InvalidateVisual();
                }
                return;
            }

            // ---- Table cursor: resize-handle hover feedback ----
            if (_plot.PlotType == PlotType.Table)
            {
                float tableZoom3 = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                var hover = TableRenderer.HitTest((float)current.X, (float)current.Y,
                    _plot, (Bounds.Width, Bounds.Height), tableZoom3);
                Cursor = hover.Kind == TableHitKind.ResizeHandle
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : Cursor.Default;
            }

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));

            // ---- Marker symbol drag ----
            if (_draggingMarker is not null && _draggingTrace is not null)
            {
                MoveMarkerToCanvasPoint(_draggingTrace, _draggingMarker, current, tf);
                InvalidateVisual();
                MarkerMoved?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_isDragging && !_isDraggingSecondary) return;

            double dxPx = current.X - _dragStartScreen.X;
            double dyPx = current.Y - _dragStartScreen.Y;

            if (_isDragging)
            {
                double dxWorld = dxPx / tf.Primary.XScale;
                double dyWorld = dyPx / tf.Primary.YScale;

                _plot.Axes.Translate(dxWorld, dyWorld);
                if (_plot.Axes.ShowSecondary)
                    _plot.Axes.TranslateSecondary(dxWorld, dyWorld);
            }
            else
            {
                _rightDragOccurred = true;
                double dyWorld = dyPx / tf.Secondary.YScale;
                _plot.Axes.TranslateSecondary(0, dyWorld);
            }

            InvalidateVisual();
        }

        // ============================================================
        //  Pointer — release
        // ============================================================

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // ---- Table column resize end ----
            if (_tableColResizeDragging)
            {
                _tableColResizeDragging = false;
                e.Pointer.Capture(null);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // ---- Table marker drag end ----
            if (_tableDraggingMarker is not null)
            {
                _tableDraggingMarker   = null;
                _tableDraggingTrace    = null;
                _tableDraggingColIndex = -1;
                e.Pointer.Capture(null);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // ---- Table right-click ----
            if (_plot?.PlotType == PlotType.Table && _rightButtonDown && !_rightDragOccurred)
            {
                if (_rightClickedTrace is not null)
                    ShowTraceHeaderContextMenu(_rightClickedTrace);
                else
                {
                    var tableMenu = BuildTableContextMenu();
                    tableMenu.Open(this);
                }
                _rightButtonDown    = false;
                _rightDragOccurred  = false;
                _rightClickedTrace  = null;
                _rightClickedMarker = null;
                return;
            }

            if (_draggingMarker is not null)
            {
                _draggingMarker = null;
                _draggingTrace  = null;
                _renderDetail   = PlotDetail.Full;
                e.Pointer.Capture(null);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_isDragging || _isDraggingSecondary)
            {
                _isDragging          = false;
                _isDraggingSecondary = false;
                _renderDetail        = PlotDetail.Full;

                e.Pointer.Capture(null);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_rightButtonDown && !_rightDragOccurred)
            {
                if (_rightClickedMarker is not null && _rightClickedTrace is not null)
                    ShowMarkerContextMenu(_rightClickedMarker, _rightClickedTrace);
                else
                {
                    _contextMenu ??= BuildContextMenu();
                    RefreshAddMarkerSubmenu();
                    _contextMenu.Open(this);
                }
            }

            _rightButtonDown    = false;
            _rightDragOccurred  = false;
            _rightClickedMarker = null;
            _rightClickedTrace  = null;
        }

        // ============================================================
        //  Scroll wheel — zoom
        // ============================================================

        private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (_plot is null) return;

            // Table plots: plain scroll moves rows; no Ctrl required.
            if (_plot.PlotType == PlotType.Table)
            {
                int delta = e.Delta.Y > 0 ? -1 : 1;
                int newScroll = Math.Max(0, _plot.TableViewScrollIndex + delta);
                _plot.TableViewScrollIndex = newScroll;
                e.Handled = true;
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Ctrl+scroll → zoom this plot's axes window.
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                        e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (!ctrl) return;

            var cursor = e.GetPosition(this);
            var tf     = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));

            var (wxP, wyP) = tf.PrimaryFromCanvas(  (float)cursor.X, (float)cursor.Y);
            var (wxS, wyS) = tf.SecondaryFromCanvas((float)cursor.X, (float)cursor.Y);

            double factor = e.Delta.Y > 0 ? 1.0 / ZoomFactor : ZoomFactor;

            _plot.Axes.Window               = ZoomedWindow(_plot.Axes.Window,          wxP, wyP, factor);
            _plot.Axes.WindowSecondary      = ZoomedWindow(_plot.Axes.WindowSecondary, wxS, wyS, factor);
            _plot.Axes.WindowState          = _plot.Axes.Window;
            _plot.Axes.WindowSecondaryState = _plot.Axes.WindowSecondary;

            e.Handled = true;
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
        }

        // ============================================================
        //  Double-click — open inspector / add marker
        // ============================================================

        /// <summary>
        /// Called by the host on DoubleTapped, with the position
        /// translated into PlotControl's local coordinate space.
        /// </summary>
        public void HandleDoubleTapAt(Point pos)
        {
            if (_plot is null) return;

            // ---- Table view double-tap ----
            if (_plot.PlotType == PlotType.Table)
            {
                float tableZoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                var hit = TableRenderer.HitTest((float)pos.X, (float)pos.Y, _plot, (Bounds.Width, Bounds.Height), tableZoom);
                if (hit.Kind == TableHitKind.ResizeHandle)
                {
                    float fitW = TableRenderer.CalcFitWidth(_plot, hit.ResizeColIndex, (Bounds.Width, Bounds.Height), tableZoom);
                    var dtCols = TableRenderer.BuildColumns(_plot);
                    if (hit.ResizeColIndex < dtCols.Count && dtCols[hit.ResizeColIndex].Kind == TableColKind.TraceValue)
                        _plot.Traces[dtCols[hit.ResizeColIndex].FirstTraceIndex].ColumnWidth = fitW;
                    else
                        _plot.ColumnWidth = fitW;
                    InvalidateVisual();
                    PlotChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (hit.Kind == TableHitKind.FreqHeader)
                {
                    _plot.TableViewAscendingSortOrder = !_plot.TableViewAscendingSortOrder;
                    InvalidateVisual();
                    PlotChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (hit.Kind == TableHitKind.TraceHeader && hit.HitTrace is not null)
                {
                    int idx = _plot!.Traces.IndexOf(hit.HitTrace);
                    ShowPlotInspector(idx);
                    _inspectorView?.FocusSpecTextBox(idx);
                }
                else
                {
                    ShowPlotInspector();
                }
                return;
            }

            // Check title / x-label / global Y-label hit targets first.
            var hitRects = AxesRenderer.ComputeLabelHitRects(
                _plot, (Bounds.Width, Bounds.Height));

            if (HitsRect(pos, hitRects.Title)   ||
                HitsRect(pos, hitRects.XLabel)  ||
                HitsRect(pos, hitRects.YLabel)  ||
                HitsRect(pos, hitRects.Y2Label))
            {
                ShowAxesLabelsFlyout();
                return;
            }

            // Per-trace Y-axis label hit check (Rect only).
            if (_plot.PlotType.IsRect())
            {
                int yLabelTrace = HitTestRectYLabel(pos);
                if (yLabelTrace >= 0) { ShowPlotInspector(yLabelTrace); return; }
            }

            // Double-tap on a marker glyph opens the editor flyout.
            var glyphHit = HitTestMarker(pos);
            if (glyphHit.HasValue)
            {
                ShowMarkerEditorFlyout(glyphHit.Value.Marker);
                return;
            }

            // If the tap lands close to a trace, add a marker there.
            if (TryAddMarkerNearPoint(pos)) return;

            // Default: show the PlotInspector.
            ShowPlotInspector();
        }

        /// <summary>
        /// Opens the Plot Inspector flyout scrolled to the given trace.
        /// Called from PlotContainerView when the user double-taps a Y-axis label strip.
        /// </summary>
        public void ShowPlotInspectorAtTrace(Trace trace)
        {
            int idx = _plot?.Traces.IndexOf(trace) ?? -1;
            ShowPlotInspector(idx);
        }

        private static bool HitsRect(Point pos, SkiaSharp.SKRect r) =>
            !r.IsEmpty && r.Contains((float)pos.X, (float)pos.Y);

        private bool TryAddMarkerNearPoint(Point canvasPt)
        {
            if (_plot is null) return false;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));

            double bestPixDist = double.PositiveInfinity;
            Trace? bestTrace   = null;
            int    bestFi      = -1;
            System.Numerics.Vector2 bestNearPt = default;

            foreach (var trace in _plot.Traces)
            {
                var (wx, wy) = trace.UseSecondaryAxis
                    ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                    : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

                var hit = trace.FindNearestTraceData(
                    new System.Numerics.Vector2((float)wx, (float)wy));
                if (!hit.HasValue) continue;

                var nearPx = tf.ToCanvas(hit.Value.NearestPoint.X, hit.Value.NearestPoint.Y,
                                         trace.UseSecondaryAxis);
                double d = Math.Sqrt(
                    Math.Pow(canvasPt.X - nearPx.X, 2) +
                    Math.Pow(canvasPt.Y - nearPx.Y, 2));

                if (d < bestPixDist)
                {
                    bestPixDist = d;
                    bestTrace   = trace;
                    bestFi      = hit.Value.FreqIndex;
                    bestNearPt  = hit.Value.NearestPoint;
                }
            }

            const double SnapPx = 20.0;
            if (bestTrace is null || bestPixDist > SnapPx) return false;

            AddMarkerAtFreqIndex(bestTrace, bestFi, bestNearPt);
            return true;
        }

        // ============================================================
        //  Private helpers
        // ============================================================

        private static Rect ZoomedWindow(Rect window, double wx, double wy, double factor)
        {
            double newW = window.Width  * factor;
            double newH = window.Height * factor;
            if (newW < 1e-12 || newH < 1e-12) return window;

            return new Rect(
                wx - (wx - window.Left) * factor,
                wy - (wy - window.Top)  * factor,
                newW, newH);
        }

        // ============================================================
        //  Marker hit-test and drag helpers
        // ============================================================

        private (Marker Marker, Trace Trace)? HitTestMarker(Point screenPt)
        {
            if (_plot is null) return null;

            var tf          = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var canvasSize  = (W: Bounds.Width, H: Bounds.Height);

            for (int ti = _plot.Traces.Count - 1; ti >= 0; ti--)
            {
                var trace = _plot.Traces[ti];
                for (int mi = trace.Markers.Count - 1; mi >= 0; mi--)
                {
                    var marker   = trace.Markers[mi];
                    var dl       = trace.GetMarkerDataLocation(marker);
                    var symbolPx = tf.ToCanvas(dl.X, dl.Y, trace.UseSecondaryAxis);
                    float radius = MarkerRenderer.SymbolHitRadius(marker, canvasSize);

                    float dx = (float)(screenPt.X - symbolPx.X);
                    float dy = (float)(screenPt.Y - symbolPx.Y);
                    if (dx * dx + dy * dy <= radius * radius)
                        return (marker, trace);
                }
            }
            return null;
        }

        private void AddMarkerAtCanvasPoint(Trace trace, Point canvasPt)
        {
            if (_plot is null) return;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var (wx, wy) = trace.UseSecondaryAxis
                ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

            var hit = trace.FindNearestTraceData(
                new System.Numerics.Vector2((float)wx, (float)wy));
            if (!hit.HasValue) return;

            AddMarkerAtFreqIndex(trace, hit.Value.FreqIndex, hit.Value.NearestPoint);
        }

        private void AddMarkerAtFreqIndex(
            Trace trace, int freqIndex,
            System.Numerics.Vector2 nearestWorldPt)
        {
            if (_plot is null) return;

            int    idx    = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
            int    fi     = Math.Clamp(freqIndex, 0, trace.Data.Frequencies.Length - 1);
            double freq   = trace.Data.Frequencies[fi];

            var marker = new Marker(trace, freq, false, false, idx, _plot.FreqUnits);

            marker.MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits;
            marker.FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat;

            if (trace.IsStabilityCircle)
                marker.PositionStatic = nearestWorldPt;

            trace.Markers.Add(marker);

            _renderDetail = PlotDetail.Full;
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
            MarkerAdded?.Invoke(marker, trace);
        }

        private void ShowMarkerEditorFlyout(Marker marker)
        {
            var infoVm = FindMarkerInfoBoxVmProvider?.Invoke(marker);
            if (infoVm is null) return;

            var ed = new MarkerEditorView
                { DataContext = new MarkerEditorViewModel(infoVm) };
            new Flyout
            {
                Content   = ed,
                Placement = PlacementMode.RightEdgeAlignedTop,
                ShowMode  = FlyoutShowMode.Standard,
            }.ShowAt(this, showAtPointer: true);
        }

        /// <summary>
        /// Shows the full marker context menu (Edit, Change to Trace, Remove) anchored
        /// to this control.  Shares its item-building logic with
        /// <see cref="MarkerInfoBoxView"/> so both surfaces are identical.
        /// </summary>
        internal void ShowMarkerContextMenu(Marker marker, Trace trace)
        {
            if (_plot is null) return;
            var infoVm = FindMarkerInfoBoxVmProvider?.Invoke(marker);

            Action? openEditor = infoVm is null ? null : () => ShowMarkerEditorFlyout(marker);

            bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                (_library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);
            var menu = new ContextMenu();
            MarkerInfoBoxView.PopulateMarkerMenu(
                menu, marker, trace, _plot.Traces,
                openEditor,
                t =>
                {
                    if (infoVm is not null)
                        infoVm.ChangeToTrace(t);
                    else
                    {
                        trace.Markers.Remove(marker);
                        PlotChanged?.Invoke(this, EventArgs.Empty);
                    }
                },
                () =>
                {
                    if (infoVm is not null)
                    {
                        infoVm.RemoveMarker();
                    }
                    else
                    {
                        var container = ContainerProvider?.Invoke();
                        if (container is not null)
                            container.RemoveMarkerWithUndo(marker, trace);
                        else
                        {
                            trace.Markers.Remove(marker);
                            InvalidateVisual();
                            PlotChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                },
                showFilePrefix);
            menu.Open(this);
        }

        private void ShowTraceHeaderContextMenu(Trace trace)
        {
            if (_plot is null) return;

            void Rebuild() {
                trace.BuildPath(_plot.PlotType, _plot.FreqUnits);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
            }

            var matrixTypeMenu = new MenuItem { Header = "Matrix Type" };
            foreach (MatrixType mt in Enum.GetValues<MatrixType>())
            {
                var item = new MenuItem
                {
                    Header  = mt.ToString(),
                    Icon    = trace.MatrixType == mt ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 12, Height = 12 } : null,
                };
                var capMt = mt;
                item.Click += (_, _) => { trace.MatrixType = capMt; Rebuild(); };
                matrixTypeMenu.Items.Add(item);
            }

            var matrixFmtMenu = new MenuItem { Header = "Number Format" };
            foreach (MatrixFormat mf in Enum.GetValues<MatrixFormat>())
            {
                string label = mf switch
                {
                    MatrixFormat.MA => "MA (Mag/Angle)",
                    MatrixFormat.RI => "RI (Real/Imag)",
                    MatrixFormat.DB => "DB (dB/Angle)",
                    _               => mf.ToString(),
                };
                var item = new MenuItem
                {
                    Header = label,
                    Icon   = trace.MatrixFormat == mf ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 12, Height = 12 } : null,
                };
                var capMf = mf;
                item.Click += (_, _) => { trace.MatrixFormat = capMf; InvalidateVisual(); PlotChanged?.Invoke(this, EventArgs.Empty); };
                matrixFmtMenu.Items.Add(item);
            }

            var yAxisMenu = new MenuItem { Header = "Y Axis" };
            foreach (DependentVarFormat yf in Enum.GetValues<DependentVarFormat>())
            {
                var item = new MenuItem
                {
                    Header = yf.Description(),
                    Icon   = trace.YAxis == yf ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 12, Height = 12 } : null,
                };
                var capYf = yf;
                item.Click += (_, _) => { trace.YAxis = capYf; Rebuild(); };
                yAxisMenu.Items.Add(item);
            }

            var menu = new ContextMenu();
            menu.Items.Add(matrixTypeMenu);
            menu.Items.Add(matrixFmtMenu);
            menu.Items.Add(yAxisMenu);
            menu.Open(this);
        }

        private ContextMenu BuildTableContextMenu()
        {
            var menu = new ContextMenu();

            bool canAddMarker = _rightClickedDataTrace is not null && !double.IsNaN(_rightClickedDataFreq);
            var icon = new MaterialIcon { Kind = MaterialIconKind.TriangleDown };
            var itemAddMarker = new MenuItem
            {
                Header    = "Add Marker",
                Icon      = icon,
                IsEnabled = canAddMarker,
            };
            if (canAddMarker)
            {
                var capTrace = _rightClickedDataTrace!;
                var capFreq  = _rightClickedDataFreq;
                itemAddMarker.Click += (_, _) => AddMarkerAtTableCell(capTrace, capFreq);
            }

            bool hasMarkers = _plot?.Traces.Any(t => t.Markers.Count > 0) ?? false;
            icon = new MaterialIcon { Kind = MaterialIconKind.SelectGroup };
            var itemSelectAll = new MenuItem
            {
                Header    = "Select All Markers",
                Icon      = icon,
                IsEnabled = hasMarkers,
            };
            itemSelectAll.Click += (_, _) => ContainerProvider?.Invoke()?.SelectAllMarkers();

            icon = new MaterialIcon { Kind = MaterialIconKind.Settings };
            var itemProps = new MenuItem { Header = "Table Properties…", Icon = icon };
            itemProps.Click += OnMenuPlotProperties;

            icon = new MaterialIcon { Kind = MaterialIconKind.Clipboard };
            var itemCopyData = new MenuItem { Header = "Copy Table Data", Icon = icon };
            itemCopyData.Click += async (_, _) => await CopyTableDataToClipboardAsync();

            icon = new MaterialIcon { Kind = MaterialIconKind.ContentCopy };
            var itemCopy = new MenuItem { Header = "Copy", Icon = icon };
            itemCopy.Click += async (_, _) => await OnMenuCopyPlot();

            icon = new MaterialIcon { Kind = MaterialIconKind.FileExportOutline };
            var itemExport = new MenuItem { Header = "Export…", Icon = icon };
            itemExport.Click += OnMenuExport;

            icon = new MaterialIcon { Kind = MaterialIconKind.DeleteOutline };
            var itemDelete = new MenuItem { Header = "Delete Table", Icon = icon };
            itemDelete.Click += (_, _) => DeletePlotRequested?.Invoke(this, EventArgs.Empty);

            menu.Items.Add(itemAddMarker);
            menu.Items.Add(itemSelectAll);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemProps);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemCopyData);
            menu.Items.Add(itemCopy);
            menu.Items.Add(itemExport);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemDelete);

            return menu;
        }

        private void AddMarkerAtTableCell(Trace trace, double freq)
        {
            if (_plot is null) return;
            int    idx    = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
            var    marker = new Marker(trace, freq, false, false, idx, _plot.FreqUnits);
            marker.MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits;
            marker.FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat;
            trace.Markers.Add(marker);
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
            MarkerAdded?.Invoke(marker, trace);
        }

        private async Task CopyTableDataToClipboardAsync()
        {
            if (_plot is null) return;
            float zoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);

            var (headers, rows) = TableRenderer.BuildCopyGrid(_plot, (Bounds.Width, Bounds.Height), zoom);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join("\t", headers));
            foreach (var row in rows)
                sb.AppendLine(string.Join("\t", row));

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(sb.ToString());
        }

        /// <summary>
        /// Scrolls the table by <paramref name="pageRows"/> full pages (positive = down, negative = up).
        /// Called from DisplayWindow for Page Up / Page Down key handling.
        /// </summary>
        public void ScrollTableRows(int pageRows)
        {
            if (_plot?.PlotType != PlotType.Table) return;
            float zoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
            float fs        = (float)(_plot.FontSize * zoom);
            float rowH      = fs * (1 + TableRenderer.RowPaddingFraction);
            float headerH   = fs * (1 + TableRenderer.RowPaddingFraction * 2);
            float availH    = (float)Bounds.Height - headerH - TableRenderer.HeaderToDataRowPadding;
            int   pageSize  = Math.Max(1, availH > 0 ? (int)(availH / rowH) : 1);

            int newScroll = _plot.TableViewScrollIndex + pageRows * pageSize;
            _plot.TableViewScrollIndex = Math.Max(0, newScroll);
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void MoveMarkerToCanvasPoint(
            Trace          trace,
            Marker         marker,
            Point          canvasPt,
            TransformSet   tf)
        {
            var clipRect = PlotRenderer.ViewportClipRect(tf.Viewport, tf.CanvasSize);

            if (trace.IsStabilityCircle)
            {
                var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
                var hit      = trace.FindNearestTraceData(
                    new System.Numerics.Vector2((float)wx, (float)wy));
                if (!hit.HasValue) return;

                var snapped = tf.ToCanvas(hit.Value.NearestPoint.X,
                                          hit.Value.NearestPoint.Y, false);
                if (!clipRect.Contains(snapped.X, snapped.Y)) return;

                marker.Freq           = trace.Data.Frequencies[hit.Value.FreqIndex];
                marker.PositionStatic = hit.Value.NearestPoint;
            }
            else
            {
                var (wx, wy) = trace.UseSecondaryAxis
                    ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                    : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

                var hit = trace.FindNearestTraceData(
                    new System.Numerics.Vector2((float)wx, (float)wy));
                if (!hit.HasValue) return;

                var snapped = tf.ToCanvas(hit.Value.NearestPoint.X,
                                          hit.Value.NearestPoint.Y,
                                          trace.UseSecondaryAxis);
                if (!clipRect.Contains(snapped.X, snapped.Y)) return;

                marker.Freq = trace.Data.Frequencies[hit.Value.FreqIndex];
            }
        }

        // ============================================================
        //  Stable flyout anchor helper
        // ============================================================

        private (Control Anchor, double HOffset, double VOffset) ComputeStableAnchor(
            Point localEdgePt, PlacementMode placement)
        {
            Control? stableAnchor = null;
            Visual?  v            = this.GetVisualParent();
            while (v is not null)
            {
                if (v is Grid g && g.Name == "ContentGrid") { stableAnchor = g; break; }
                v = v.GetVisualParent();
            }
            stableAnchor ??= TopLevel.GetTopLevel(this) as Control;

            if (stableAnchor is null)
                return (this, 0, 0);

            var ptInAnchor = this.TranslatePoint(localEdgePt, stableAnchor) ?? new Point(0, 0);

            double hOffset, vOffset;
            if (placement == PlacementMode.RightEdgeAlignedTop ||
                placement == PlacementMode.RightEdgeAlignedBottom)
            {
                hOffset = ptInAnchor.X - stableAnchor.Bounds.Width;
                vOffset = ptInAnchor.Y;
            }
            else
            {
                hOffset = ptInAnchor.X;
                vOffset = ptInAnchor.Y - stableAnchor.Bounds.Height;
            }

            return (stableAnchor, hOffset, vOffset);
        }

        // ============================================================
        //  Per-trace Y-axis label hit testing (Rect plots only)
        // ============================================================

        private int HitTestRectYLabel(Point pos)
        {
            if (_plot is null) return -1;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            float lw     = AxesRenderer.LineWidth((Bounds.Width, Bounds.Height));
            float fontSz = (float)(_plot.Axes.FontSizeTicks * 0.9f * lw);
            float sw     = fontSz * 1.5f;
            float vpLeft  = (float)(tf.Viewport.X * Bounds.Width);
            float vpRight = (float)((tf.Viewport.X + tf.Viewport.Width) * Bounds.Width);

            using var tickFont = new SKFont(SkiaSharp.SKTypeface.Default, (float)(_plot.Axes.FontSizeTicks * lw));
            float leftTickW = Math.Max(
                tickFont.MeasureText(_plot.Axes.Window.Top   .ToString($"G{_plot.Axes.NumDigitsLeftY}")),
                tickFont.MeasureText(_plot.Axes.Window.Bottom.ToString($"G{_plot.Axes.NumDigitsLeftY}")));
            float leftAnchor = vpLeft - lw * 4f - leftTickW - AxesRenderer.DescriptionStripPad * lw;

            if ((float)pos.X < vpLeft)
            {
                bool hasCustomY = !string.IsNullOrEmpty(_plot.YLabel);
                if (hasCustomY) return -1;

                var leftTraces = _plot.LeftAxisTraces;
                for (int i = 0; i < leftTraces.Count; i++)
                {
                    float cx = leftAnchor - sw * (i + 0.5f);
                    if (cx < 0f) break;
                    if (Math.Abs((float)pos.X - cx) < sw * 0.5f)
                        return _plot.Traces.IndexOf(leftTraces[i]);
                }
            }

            if ((float)pos.X > vpRight && _plot.Axes.ShowSecondary)
            {
                bool hasCustomY2 = !string.IsNullOrEmpty(_plot.Y2Label);
                if (hasCustomY2) return -1;

                float rightTickW = Math.Max(
                    tickFont.MeasureText(_plot.Axes.WindowSecondary.Top   .ToString($"G{_plot.Axes.NumDigitsRightY}")),
                    tickFont.MeasureText(_plot.Axes.WindowSecondary.Bottom.ToString($"G{_plot.Axes.NumDigitsRightY}")));
                float rightAnchor = vpRight + lw * 4f + rightTickW + AxesRenderer.DescriptionStripPad * lw;

                float canvasW = (float)Bounds.Width;
                var rightTraces = _plot.RightAxisTraces;
                for (int i = 0; i < rightTraces.Count; i++)
                {
                    float cx = rightAnchor + sw * (i + 0.5f);
                    if (cx > canvasW) break;
                    if (Math.Abs((float)pos.X - cx) < sw * 0.5f)
                        return _plot.Traces.IndexOf(rightTraces[i]);
                }
            }

            return -1;
        }
    }
}
