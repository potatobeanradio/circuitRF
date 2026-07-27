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
        // brief-datadisplay-fix-context-menu-stacking.md: one reused instance per DYNAMIC-content
        // menu (Pattern B — populate-then-open, never `new ContextMenu()` per click). _contextMenu
        // above is the existing correct Pattern A (static content, cached once).
        private ContextMenu? _markerContextMenu;
        private ContextMenu? _traceHeaderContextMenu;
        private ContextMenu? _tableContextMenu;
        private MaterialIcon _iconAxesLocked = new MaterialIcon();

        // Marker symbol drag state
        private Marker? _draggingMarker;
        private Trace?  _draggingTrace;

        // VSWR locus drag state
        private Marker? _draggingVswrMarker;
        private Trace?  _draggingVswrTrace;
        private Point   _vswrReadoutPt;
        private bool    _vswrReadoutActive;

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
        private Control?                 _inspectorFlyoutAnchor;
        // When true, a color-picker dialog is open; the flyout Closed handler re-shows instead of cleaning up.
        private bool                     _suppressInspectorDismiss;

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
        //  Arrow-key fine movement of the selected marker(s)
        // ============================================================

        /// <summary>
        /// Steps the selected marker(s) by one x-axis sample. Set by the host container to the same
        /// handler the info box uses, so arrow keys behave identically whether the canvas or an info box
        /// has focus. Returns true when a selected marker was eligible (the arrow key is then consumed).
        /// </summary>
        public Func<int, bool>? StepSelectedMarkersHandler { get; set; }

        /// <summary>
        /// On a Rect plot, Up/Right steps the selected marker(s) to the next-higher x-axis sample and
        /// Down/Left to the next-lower — fine movement that snaps to actual data points. For harmonic /
        /// mixIndex spectral axes the step follows frequency (useful for tight two-tone IMD spacings).
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_plot is not null && _plot.PlotType.IsRect() &&
                e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            {
                int direction = e.Key is Key.Up or Key.Right ? +1 : -1;
                if (StepSelectedMarkersHandler?.Invoke(direction) == true)
                {
                    e.Handled = true;
                    return;
                }
            }
            base.OnKeyDown(e);
        }

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

            // Inject owner-window resolver so color pickers use the main window, not PopupRoot.
            _inspectorVm.GetOwnerWindow = () => TopLevel.GetTopLevel(this) as Window;
            _inspectorVm.ColorPickStarted += OnInspectorColorPickStarted;
            _inspectorVm.ColorPickEnded   += OnInspectorColorPickEnded;

            var view = new PlotInspectorView { DataContext = _inspectorVm };
            _inspectorView = view;

            var (flyoutAnchor, hOffset, vOffset) = ComputeStableAnchor(
                new Point(Bounds.Width, 0), PlacementMode.RightEdgeAlignedTop);

            _inspectorFlyoutAnchor   = flyoutAnchor;
            _suppressInspectorDismiss = false;

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
                if (_suppressInspectorDismiss && _inspectorFlyoutAnchor is { } anchor)
                {
                    // Dialog stole focus and light-dismissed the flyout; re-show immediately.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _inspectorFlyout?.ShowAt(anchor));
                    return;
                }
                if (_inspectorVm is not null)
                {
                    _inspectorVm.ColorPickStarted -= OnInspectorColorPickStarted;
                    _inspectorVm.ColorPickEnded   -= OnInspectorColorPickEnded;
                    _inspectorVm.PlotNeedsRedraw  -= OnInspectorPlotNeedsRedraw;
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

        private void OnInspectorColorPickStarted(object? sender, EventArgs e)
            => _suppressInspectorDismiss = true;

        private void OnInspectorColorPickEnded(object? sender, EventArgs e)
            => _suppressInspectorDismiss = false;

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

            VswrReadout? readout = null;
            if (_vswrReadoutActive && _draggingVswrMarker is { } rMark)
                readout = new VswrReadout(
                    $"VSWR: {rMark.VswrValue:F4}",
                    new SkiaSharp.SKPoint((float)_vswrReadoutPt.X, (float)_vswrReadoutPt.Y));

            context.Custom(new PlotDrawOperation(
                new Rect(Bounds.Size),
                _plot,
                _theme,
                _renderDetail,
                showFilePrefix,
                selectedMarkers,
                selColor,
                zoom,
                readout));
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
            private readonly VswrReadout?      _vswrReadout;

            public PlotDrawOperation(
                Rect             bounds,
                Plot?            plot,
                RenderTheme      theme,
                PlotDetail       detail,
                bool             showFilePrefix,
                HashSet<Marker>? selectedMarkers = null,
                SKColor          selectionColor  = default,
                float            zoomLevel       = 1f,
                VswrReadout?     vswrReadout     = null)
            {
                _bounds          = bounds;
                _plot            = plot;
                _theme           = theme;
                _detail          = detail;
                _showFilePrefix  = showFilePrefix;
                _selectedMarkers = selectedMarkers;
                _selectionColor  = selectionColor;
                _zoomLevel       = zoomLevel;
                _vswrReadout     = vswrReadout;
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

                // §7 (corrected): clip to THIS control's region so antialiased text at the
                // edge can't bleed into the parent DataDisplay canvas during a move.
                // The leased canvas is shared across all plots and is pre-translated so the
                // control's content is positioned by the current matrix; clip in that same
                // local space via LocalClipBounds (NOT a hand-built SKRect(0,0,W,H), which
                // is the surface origin, and NOT canvas.Clear, which wipes the whole shared
                // surface and erases other plots).
                canvas.Save();
                canvas.ClipRect(canvas.LocalClipBounds);
                PlotRenderer.Draw(canvas, canvasSize, _plot, _detail, _theme, _showFilePrefix,
                    selectedMarkers: _selectedMarkers, selectionColor: _selectionColor,
                    zoomLevel: _zoomLevel, vswrReadout: _vswrReadout);
                canvas.Restore();
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
                        var resCols   = TableRenderer.BuildColumns(_plot);
                        var resizeCol = hitResult.ResizeColIndex < resCols.Count
                            ? resCols[hitResult.ResizeColIndex] : null;
                        if (resizeCol?.Kind == TableColKind.TraceValue)
                        {
                            var rt  = _plot.Traces[resizeCol.FirstTraceIndex];
                            int fci = resizeCol.FamilyCurveIndex;
                            _tableResizeStartWidth = fci >= 0 && rt.FamilyColumnWidths.TryGetValue(fci, out var fcw) ? fcw : rt.ColumnWidth;
                        }
                        else if (resizeCol?.Kind == TableColKind.XAxis)
                        {
                            var anchor = _plot.Traces[resizeCol.FirstTraceIndex];
                            _tableResizeStartWidth = anchor.XColumnWidth > 0
                                ? anchor.XColumnWidth : _plot.ColumnWidth;
                        }
                        else
                            _tableResizeStartWidth = _plot.ColumnWidth;
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
                // VSWR locus grab — checked BEFORE the glyph so a tight locus (e.g. VSWR 1.05) sitting
                // inside the marker's hit radius is still draggable; the marker would otherwise always win.
                var vswrHit = HitTestVswrLocus(e.GetPosition(this));
                if (vswrHit.HasValue)
                {
                    _draggingVswrMarker = vswrHit.Value.Marker;
                    _draggingVswrTrace  = vswrHit.Value.Trace;
                    _renderDetail = PlotDetail.Full;
                    // Show the transient VSWR readout on a plain click too (not only while dragging),
                    // so the user can click the locus to read its value without moving it.
                    _vswrReadoutPt     = e.GetPosition(this);
                    _vswrReadoutActive = true;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    InvalidateVisual();
                    return;
                }

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
                if (_tableResizeColIndex < drCols.Count)
                {
                    var drCol = drCols[_tableResizeColIndex];
                    if (drCol.Kind == TableColKind.TraceValue)
                    {
                        var dt  = _plot.Traces[drCol.FirstTraceIndex];
                        int fci = drCol.FamilyCurveIndex;
                        if (fci >= 0)
                            dt.FamilyColumnWidths[fci] = newWidth;
                        else
                            dt.ColumnWidth = newWidth;
                    }
                    else  // XAxis: write to per-anchor XColumnWidth
                        _plot.Traces[drCol.FirstTraceIndex].XColumnWidth = newWidth;
                }
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

            // ---- VSWR locus drag ----
            if (_draggingVswrMarker is not null && _draggingVswrTrace is not null)
            {
                var (plane, z0Ref) = ResolveVswrPlaneAndZ0(_draggingVswrTrace);
                var dl = _draggingVswrTrace.GetMarkerDataLocation(_draggingVswrMarker);

                double pwx, pwy;
                if (_draggingVswrTrace.UseSecondaryAxis)
                    (pwx, pwy) = tf.SecondaryFromCanvas((float)current.X, (float)current.Y);
                else
                    (pwx, pwy) = tf.PrimaryFromCanvas((float)current.X, (float)current.Y);

                double vswr;
                if (plane == RfCore.Loadpull.SurfacePlane.Gamma)
                {
                    vswr = RfCore.RfHelpers.VswrFromGamma(
                        new System.Numerics.Complex(dl.X, dl.Y),
                        new System.Numerics.Complex(pwx, pwy));
                }
                else
                {
                    var z0 = z0Ref == System.Numerics.Complex.Zero
                        ? new System.Numerics.Complex(50.0, 0.0) : z0Ref;
                    vswr = RfCore.RfHelpers.VswrFromZ(
                        new System.Numerics.Complex(dl.X, dl.Y) / z0,
                        new System.Numerics.Complex(pwx, pwy) / z0);
                }

                if (double.IsFinite(vswr))
                    _draggingVswrMarker.VswrValue = vswr;

                _vswrReadoutPt     = current;
                _vswrReadoutActive = true;
                InvalidateVisual();
                MarkerMoved?.Invoke(this, EventArgs.Empty);
                return;
            }

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
                    _tableContextMenu ??= new ContextMenu();
                    PopulateTableContextMenu(_tableContextMenu);
                    _tableContextMenu.Close(); // a second right-click while up replaces, not re-opens
                    _tableContextMenu.Open(this);
                }
                _rightButtonDown    = false;
                _rightDragOccurred  = false;
                _rightClickedTrace  = null;
                _rightClickedMarker = null;
                return;
            }

            if (_draggingVswrMarker is not null)
            {
                _draggingVswrMarker = null;
                _draggingVswrTrace  = null;
                _vswrReadoutActive  = false;
                _renderDetail       = PlotDetail.Full;
                e.Pointer.Capture(null);
                InvalidateVisual();
                PlotChanged?.Invoke(this, EventArgs.Empty);
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
            // If the table fits entirely on screen (can't scroll), let the event bubble for zoom.
            if (_plot.PlotType == PlotType.Table)
            {
                float tableZoom = (float)(ContainerProvider?.Invoke()?.ZoomLevel ?? 1.0);
                if (!TableRenderer.CanScroll(_plot, (Bounds.Width, Bounds.Height), tableZoom))
                    return;   // leave e.Handled = false so parent can zoom
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
                    if (hit.ResizeColIndex < dtCols.Count)
                    {
                        var dtCol = dtCols[hit.ResizeColIndex];
                        if (dtCol.Kind == TableColKind.TraceValue)
                        {
                            var dtt  = _plot.Traces[dtCol.FirstTraceIndex];
                            int fci  = dtCol.FamilyCurveIndex;
                            if (fci >= 0)
                                dtt.FamilyColumnWidths[fci] = fitW;
                            else
                                dtt.ColumnWidth = fitW;
                        }
                        else  // XAxis: write to per-anchor XColumnWidth
                            _plot.Traces[dtCol.FirstTraceIndex].XColumnWidth = fitW;
                    }
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
                ShowMarkerEditorFlyout(glyphHit.Value.Marker, glyphHit.Value.Trace);
                return;
            }

            // Double-tap on a VSWR locus opens its marker's editor flyout (checked after the
            // glyph so a glyph hit always wins). Without this the tap falls through to the
            // Plot Properties inspector, which is not what the user expects when aiming at the locus.
            var vswrDblHit = HitTestVswrLocus(pos);
            if (vswrDblHit.HasValue)
            {
                ShowMarkerEditorFlyout(vswrDblHit.Value.Marker, vswrDblHit.Value.Trace);
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
            if (bestTrace is null || bestPixDist > SnapPx)
            {
                // No trace sample within snap distance. Do NOT free-roam-add a contour marker
                // here: a double-click on empty plot area should fall through to the Plot
                // Properties flyout (handled by the caller). Contour markers are still added
                // explicitly via the right-click "Add Marker" menu (AddMarkerAtCanvasPoint).
                return false;
            }

            if (bestTrace.IsHarmonicStem)
                return TryAddStemMarker(bestTrace, canvasPt);

            if (bestTrace.IsCubeXMarker)
                return TryAddCubeMarker(bestTrace, canvasPt);

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

        // ============================================================
        //  VSWR locus hit-test and plane/Z0 helpers
        // ============================================================

        private (Marker Marker, Trace Trace)? HitTestVswrLocus(Point screenPt)
        {
            if (_plot is null) return null;
            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            float grabPx = (float)(Math.Min(Bounds.Width, Bounds.Height) / 200.0) * 4f;

            for (int ti = _plot.Traces.Count - 1; ti >= 0; ti--)
            {
                var trace = _plot.Traces[ti];
                for (int mi = trace.Markers.Count - 1; mi >= 0; mi--)
                {
                    var marker = trace.Markers[mi];
                    if (!marker.VswrEnabled || !PlotRenderer.VswrAvailableFor(_plot, trace, marker)) continue;

                    var (plane, z0Ref) = ResolveVswrPlaneAndZ0(trace);
                    var dl     = trace.GetMarkerDataLocation(marker);
                    var center = new System.Numerics.Complex(dl.X, dl.Y);
                    var pts    = RfCore.Loadpull.LoadpullSurface.VswrLocus(center, marker.VswrValue, plane, z0Ref);
                    if (pts is null || pts.Length < 2) continue;

                    for (int i = 0; i < pts.Length; i++)
                    {
                        var a = tf.ToCanvas(pts[i].Real, pts[i].Imaginary, trace.UseSecondaryAxis);
                        var b = tf.ToCanvas(pts[(i + 1) % pts.Length].Real, pts[(i + 1) % pts.Length].Imaginary, trace.UseSecondaryAxis);
                        if (DistPointToSegment((float)screenPt.X, (float)screenPt.Y, a.X, a.Y, b.X, b.Y) <= grabPx)
                            return (marker, trace);
                    }
                }
            }
            return null;
        }

        private (RfCore.Loadpull.SurfacePlane Plane, System.Numerics.Complex Z0Ref) ResolveVswrPlaneAndZ0(Trace trace)
        {
            var plane  = (_plot?.PlotType is PlotType.Smith or PlotType.Polar)
                ? RfCore.Loadpull.SurfacePlane.Gamma
                : RfCore.Loadpull.SurfacePlane.Z;
            var z0Ref  = trace.Z0 == System.Numerics.Complex.Zero
                ? new System.Numerics.Complex(50.0, 0.0)
                : trace.Z0;
            return (plane, z0Ref);
        }

        private static float DistPointToSegment(
            float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6f) return MathF.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0f, 1f);
            float cx = ax + t * dx, cy = ay + t * dy;
            return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        private void AddMarkerAtCanvasPoint(Trace trace, Point canvasPt)
        {
            if (_plot is null) return;

            if (TryAddContourMarker(trace, canvasPt)) return;
            if (TryAddStemMarker(trace, canvasPt)) return;
            if (TryAddCubeMarker(trace, canvasPt)) return;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var (wx, wy) = trace.UseSecondaryAxis
                ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

            var hit = trace.FindNearestTraceData(
                new System.Numerics.Vector2((float)wx, (float)wy));
            if (!hit.HasValue) return;

            AddMarkerAtFreqIndex(trace, hit.Value.FreqIndex, hit.Value.NearestPoint);
        }

        // Returns true if it added a contour marker at the cursor world point.
        private bool TryAddContourMarker(Trace trace, Point canvasPt)
        {
            if (_plot is null || !trace.IsContourTrace) return false;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
            var world = new System.Numerics.Vector2((float)wx, (float)wy);

            int idx    = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
            var marker = new Marker(trace, 0.0, false, false, idx, _plot.FreqUnits)
            {
                MarkerKind            = MarkerKind.Contour,
                MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits,
                FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat,
                // Initial complex-readout format follows the plot plane: Smith/Polar markers read
                // their coordinate as Γ (mag∠angle → MA); Rect contour markers read it as Z (R+jX → RI).
                MatrixFormat          = _plot.PlotType is PlotType.Smith or PlotType.Polar
                    ? MatrixFormat.MA
                    : MatrixFormat.RI,
            };
            marker.PositionStatic = trace.ResolveContourMarkerPosition(marker, world);

            // Choose an initial VSWR value whose locus fits inside the plot's current visible
            // window, preferring 2. The default 2 is often larger than the contour view, so the
            // circle would be clipped/invisible when the user enables VSWR; step down a ladder of
            // "nice" values and take the largest that fits. (Value only — VswrEnabled stays user-driven.)
            marker.VswrValue = ChooseFittingVswr(trace, marker);

            trace.Markers.Add(marker);
            _renderDetail = PlotDetail.Full;
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
            MarkerAdded?.Invoke(marker, trace);
            return true;
        }

        /// <summary>
        /// Picks an initial VSWR value for a freshly-added contour marker so its locus is visible
        /// inside the plot's current window. Walks a ladder of "nice" values from 2 downward and
        /// returns the largest whose locus bounding box fits entirely within the visible window;
        /// falls back to the smallest ladder value when even that does not fit. Preference to 2.
        /// </summary>
        private double ChooseFittingVswr(Trace trace, Marker marker)
        {
            // Nice ladder, largest-first; 2 is the preferred default.
            double[] ladder = { 2.0, 1.5, 1.2, 1.1, 1.05, 1.02, 1.01 };

            if (_plot is null) return ladder[0];
            var window = _plot.Axes.Window;
            if (window.Width <= 0 || window.Height <= 0) return ladder[0];

            var (plane, z0Ref) = ResolveVswrPlaneAndZ0(trace);
            var center = new System.Numerics.Complex(marker.PositionStatic.X, marker.PositionStatic.Y);

            foreach (double v in ladder)
            {
                var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(center, v, plane, z0Ref);
                if (pts is null || pts.Length < 2) continue;

                double minRe = double.MaxValue, maxRe = double.MinValue;
                double minIm = double.MaxValue, maxIm = double.MinValue;
                bool allFinite = true;
                foreach (var p in pts)
                {
                    if (!double.IsFinite(p.Real) || !double.IsFinite(p.Imaginary)) { allFinite = false; break; }
                    if (p.Real < minRe) minRe = p.Real;
                    if (p.Real > maxRe) maxRe = p.Real;
                    if (p.Imaginary < minIm) minIm = p.Imaginary;
                    if (p.Imaginary > maxIm) maxIm = p.Imaginary;
                }
                if (!allFinite) continue;

                // window.Left/Right/Top/Bottom are world-space bounds (Top may be > Bottom
                // depending on axis orientation), so compare against the min/max of each pair.
                double wMinX = Math.Min(window.Left, window.Right);
                double wMaxX = Math.Max(window.Left, window.Right);
                double wMinY = Math.Min(window.Top,  window.Bottom);
                double wMaxY = Math.Max(window.Top,  window.Bottom);

                if (minRe >= wMinX && maxRe <= wMaxX && minIm >= wMinY && maxIm <= wMaxY)
                    return v;
            }

            return ladder[^1];
        }

        private bool TryAddStemMarker(Trace trace, Point canvasPt)
        {
            if (_plot is null || !trace.IsHarmonicStem) return false;
            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var (wx, wy) = trace.UseSecondaryAxis
                ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

            var snap = trace.SnapToStem(new System.Numerics.Vector2((float)wx, (float)wy));
            if (snap is null) return false;

            int idx    = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
            var marker = new Marker(trace, 0.0, false, false, idx, _plot.FreqUnits)
            {
                MarkerKind            = MarkerKind.Spectrum,
                MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits,
                FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat,
                PositionStatic        = new System.Numerics.Vector2(snap.Value.HarmonicX, 0f),
            };

            trace.Markers.Add(marker);
            _renderDetail = PlotDetail.Full;
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
            MarkerAdded?.Invoke(marker, trace);
            return true;
        }

        private bool TryAddCubeMarker(Trace trace, Point canvasPt)
        {
            if (_plot is null || !trace.IsCubeXMarker) return false;

            var tf = PlotRenderer.BuildTransforms(_plot, (Bounds.Width, Bounds.Height));
            var (wx, wy) = trace.UseSecondaryAxis
                ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);

            var snap = trace.SnapToCubeMarker(new System.Numerics.Vector2((float)wx, (float)wy));
            if (snap is null) return false;

            // Family curves iterate the spectral axis (Spectrum kind); a single Pin-swept
            // cube curve is an ordinary Polyline. Either way the marker is keyed by cube-X
            // (PositionStatic.X) + bound curve index (PositionStatic.Y).
            // Exception: on Smith/Polar, single-curve markers store (Re, Im) so draw-time
            // resolution uses 2-D nearest instead of X-only (X-only fails on looping loci).
            int idx    = NextMarkerIndexProvider?.Invoke() ?? (trace.Markers.Count + 1);
            var posStatic = (trace.IsComplexPlanePlot && !trace.IsFamily)
                ? snap.Value.Pos
                : new System.Numerics.Vector2(snap.Value.CubeX, snap.Value.CurveIndex);
            var marker = new Marker(trace, 0.0, false, false, idx, _plot.FreqUnits)
            {
                MarkerKind            = trace.IsFamily ? MarkerKind.Spectrum : MarkerKind.Polyline,
                MaximumFractionDigits = AppSettingsViewModel.Instance.MarkerMaxFractionDigits,
                FormatString          = AppSettingsViewModel.Instance.MarkerPrecisionFormat,
                PositionStatic        = posStatic,
            };

            trace.Markers.Add(marker);
            _renderDetail = PlotDetail.Full;
            InvalidateVisual();
            PlotChanged?.Invoke(this, EventArgs.Empty);
            MarkerAdded?.Invoke(marker, trace);
            return true;
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

        private void ShowMarkerEditorFlyout(Marker marker, Trace trace)
        {
            // Prefer the live InfoBox VM; fall back to a transient one when the marker's
            // info box is hidden so the editor still opens (item: Edit Properties must work
            // even when Show Info Box is off).
            var infoVm = FindMarkerInfoBoxVmProvider?.Invoke(marker)
                         ?? ContainerProvider?.Invoke()?.GetOrCreateInfoBoxVm(marker, trace);
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

            // Always provide an editor opener — ShowMarkerEditorFlyout falls back to a transient
            // InfoBox VM when the marker's box is hidden, so "Edit Properties" is never disabled.
            Action openEditor = () => ShowMarkerEditorFlyout(marker, trace);

            bool showFilePrefix = AppSettingsViewModel.Instance.EffectiveShowFilePrefix(
                (_library?.Entries.Count(e => e.Snp is not null && !e.Snp.IsEmpty) ?? 0) > 1);
            _markerContextMenu ??= new ContextMenu();
            var menu = _markerContextMenu;
            MarkerInfoBoxView.PopulateMarkerMenu(
                menu, marker, trace, _plot.Traces, _plot,
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
                showFilePrefix,
                onContourModeToggled: () =>
                {
                    InvalidateVisual();
                    PlotChanged?.Invoke(this, EventArgs.Empty);
                    MarkerMoved?.Invoke(this, EventArgs.Empty);
                },
                onShowInfoBoxToggled: () =>
                {
                    ContainerProvider?.Invoke()?.RequestInfoBoxRebuild();
                    InvalidateVisual();
                },
                onVswrToggled: () =>
                {
                    InvalidateVisual();
                    PlotChanged?.Invoke(this, EventArgs.Empty);
                });
            menu.Close(); // a second right-click while the menu is still up replaces, not re-opens
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

            _traceHeaderContextMenu ??= new ContextMenu();
            var menu = _traceHeaderContextMenu;
            menu.Items.Clear();

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

            // Matrix Type (S/Z/Y conversion) is only meaningful for network/Touchstone traces — NOT for
            // DataCube slices from a simulation (mirrors TraceRowViewModel.ShowMatrixTypeCombo). "Y Axis" is
            // gone entirely: the dependent variable is now chosen via the trace-card expression.
            if (!trace.IsCubeBound && trace.Data is { } d && !d.IsEmpty)
                menu.Items.Add(matrixTypeMenu);
            menu.Items.Add(matrixFmtMenu);
            menu.Close(); // a second right-click while the menu is still up replaces, not re-opens
            menu.Open(this);
        }

        /// <summary>Populates (never constructs) the shared table-menu instance — its content
        /// (whether "Add Marker" is enabled, and which cell it targets) depends on
        /// <see cref="_rightClickedDataTrace"/>/<see cref="_rightClickedDataFreq"/>, which change per
        /// click, so this must be a Pattern-B populate step, not a Pattern-A build-once cache
        /// (brief-datadisplay-fix-context-menu-stacking.md §3.1 — despite taking no parameters, its
        /// content is NOT click-independent; it reads that ambient per-click state via fields).</summary>
        private void PopulateTableContextMenu(ContextMenu menu)
        {
            menu.Items.Clear();

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

            if (trace.IsContourTrace)
            {
                var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
                var snapped  = tf.ToCanvas(wx, wy, false);
                if (!clipRect.Contains(snapped.X, snapped.Y)) return;
                marker.PositionStatic = trace.ResolveContourMarkerPosition(
                    marker, new System.Numerics.Vector2((float)wx, (float)wy));
                return;
            }

            if (trace.IsHarmonicStem)
            {
                var (wx, wy) = tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
                var snap = trace.SnapToStem(new System.Numerics.Vector2((float)wx, (float)wy));
                if (snap is null) return;
                var snappedPx = tf.ToCanvas(snap.Value.Pos.X, snap.Value.Pos.Y, trace.UseSecondaryAxis);
                if (!clipRect.Contains(snappedPx.X, snappedPx.Y)) return;
                marker.PositionStatic = new System.Numerics.Vector2(snap.Value.HarmonicX, 0f);
                return;
            }

            if (trace.IsCubeXMarker)
            {
                var (wx, wy) = trace.UseSecondaryAxis
                    ? tf.SecondaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y)
                    : tf.PrimaryFromCanvas((float)canvasPt.X, (float)canvasPt.Y);
                var snap = trace.SnapToCubeMarker(new System.Numerics.Vector2((float)wx, (float)wy));
                if (snap is null) return;
                var snappedPx = tf.ToCanvas(snap.Value.Pos.X, snap.Value.Pos.Y, trace.UseSecondaryAxis);
                if (!clipRect.Contains(snappedPx.X, snappedPx.Y)) return;
                // Smith/Polar single-curve: store (Re, Im) for 2-D draw-time resolution.
                // Rect/family: store (CubeX, curveIndex) for X-only resolution.
                marker.PositionStatic = (trace.IsComplexPlanePlot && !trace.IsFamily)
                    ? snap.Value.Pos
                    : new System.Numerics.Vector2(snap.Value.CubeX, snap.Value.CurveIndex);
                return;
            }

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

                var freqs = trace.Data.Frequencies;
                if (!trace.IsCubeBound && hit.Value.FreqIndex >= 0 && hit.Value.FreqIndex < freqs.Length)
                    marker.Freq = freqs[hit.Value.FreqIndex];
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
