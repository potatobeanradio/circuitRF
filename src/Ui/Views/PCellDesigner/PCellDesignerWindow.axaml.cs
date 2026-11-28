// Owner: PCD-6 (brief-pcd-6). The window turns pointer events into the view model's gesture calls (D1); it holds no
// design state of its own but the vertex selection.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Views.PCellDesigner;

public partial class PCellDesignerWindow : Window, ICrfMenuWindow
{
    /// <summary>The windows open, one per cell folder (R-pcd6-1).</summary>
    private static readonly PCellDesignerWindows<PCellDesignerWindow> OnCell = new();

    /// <summary>The pick tolerance, in screen pixels.</summary>
    private const double PickPixels = 8;

    private readonly List<(string Instance, int Vertex)> _selected = [];
    private (string Instance, int Pin, long GrabDx, long GrabDy)? _offsetDrag;
    private (long X, long Y)? _pressAt;
    private bool _pressedVertex;
    private (string Instance, int Vertex, long X, long Y)? _vertexDrag;

    public PCellDesignerWindow()
    {
        InitializeComponent();
        void Theme()
        {
            if (Vm is not { } vm) return;
            vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
            this.FindControl<PlotControl>("ResponsePlotControl")?.SetValue(PlotControl.PlotThemeProperty, vm.PlotHost.Theme);
        }
        DataContextChanged += (_, _) => { Theme(); Watch(); WirePlot(); };
        DesignCanvas.CanvasOverlay = _dimensions;
        ActualThemeVariantChanged += (_, _) => Theme();

        DesignCanvas.AddHandler(PointerPressedEvent, OnCanvasPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DesignCanvas.AddHandler(PointerReleasedEvent, OnCanvasReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DesignCanvas.AddHandler(DragDrop.DragOverEvent, OnPartDragOver, handledEventsToo: true);
        DesignCanvas.AddHandler(DragDrop.DropEvent, OnPartDrop, handledEventsToo: true);
        DesignCanvas.LayoutUpdated += (_, _) =>
        {
            if (_fitPending && DesignCanvas.ViewModel is not null && DesignCanvas.Bounds.Width > 1) { _fitPending = false; DesignCanvas.ZoomToFit(); }
        };
    }

    private bool _fitPending;

    public string WindowMenuHeader => Vm?.Title ?? "PCell Designer";

    private PCellDesignerViewModel? Vm => DataContext as PCellDesignerViewModel;

    /// <summary>
    /// Opens the designer on <paramref name="cellDir"/>, or brings forward the one already open on it.
    /// <paramref name="openOptimizer"/> is the workspace's way to show a schematic with its Optimizer (R-pcd6-7).
    /// </summary>
    public static PCellDesignerWindow Show(string cellDir, string workspaceRoot, Window? owner, Action<string>? openOptimizer = null)
    {
        var (window, created) = OnCell.Show(cellDir,
            () => new PCellDesignerWindow { DataContext = new PCellDesignerViewModel(cellDir, workspaceRoot) { OpenOptimizer = openOptimizer } },
            w => w.Activate());
        if (!created) return window;
        window.Closed += (_, _) =>
        {
            OnCell.Closed(cellDir, window);
            window.Vm?.CancelWork();
        };
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (owner is not null)
            window.Position = MatchWindowPlacement.Cascade(owner.Position, owner.RenderScaling,
                                                          owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);
        window.Show();
        window.Vm?.Refresh();
        return window;
    }

    // ── The canvas follows the plan ───────────────────────────────────────────────────────────────────────────────

    private PCellDesignerViewModel? _watched;

    private void Watch()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnVmChanged;
        SyncCanvas();   // a view model planned before it was shown (a snapshot) is drawn at once
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PCellDesignerViewModel.Layout)) SyncCanvas();
        if (e.PropertyName == nameof(PCellDesignerViewModel.Schematic)) ShowSelection();
        if (e.PropertyName is nameof(PCellDesignerViewModel.Dimensions) or nameof(PCellDesignerViewModel.ShowDimensions)) SyncDimensions();
    }

    // ── Dimensions (brief-pcd-10 R-pcd10-2/3) and the plot's markers (R-pcd10-1) ────────────────────────────────

    private readonly DimensionOverlay _dimensions = new();

    // ── The MSHAPE draw tool (brief-pcd-11 R-pcd11-4) ────────────────────────────────────────────────────────────

    private readonly List<(long X, long Y)> _corners = [];

    private void OnDrawToggled(object? sender, RoutedEventArgs e)
    {
        _corners.Clear();
        ShowPending();
        if (Vm is { } vm && DrawToggle.IsChecked == true)
            vm.Status = "Draw: click the corners; double-click or Enter finishes, Esc cancels.";
    }

    private void FinishDrawing()
    {
        if (Vm is { } vm && vm.DrawShape([.. _corners], Tolerance)) DrawToggle.IsChecked = false;
        _corners.Clear();
        ShowPending();
    }

    private void ShowPending()
    {
        _dimensions.Pending = [.. _corners];
        DesignCanvas.InvalidateOverlay();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DrawToggle.IsChecked == true && e.Key is Key.Enter or Key.Return) { FinishDrawing(); e.Handled = true; }
        else if (DrawToggle.IsChecked == true && e.Key == Key.Escape) { DrawToggle.IsChecked = false; e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void SyncDimensions()
    {
        if (Vm is not { } vm) return;
        _dimensions.Layout = vm.Layout;
        _dimensions.Dimensions = vm.ShowDimensions ? vm.Dimensions : [];
        DesignCanvas.InvalidateOverlay();
    }

    private bool _plotWired;

    /// <summary>The response plot's marker tools, wired to its Data Display container as the LC Filter Designer wires
    /// its plots: add, drag and step a marker, with its readout box.</summary>
    private void WirePlot()
    {
        if (_plotWired || Vm is not { } vm || this.FindControl<PlotControl>("ResponsePlotControl") is not { } plot) return;
        _plotWired = true;
        var container = vm.ResponseContainer;
        plot.NextMarkerIndexProvider = container.GetNextMarkerIndex;
        plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
        plot.ContainerProvider = () => container;
        plot.SelectedMarkersProvider = container.GetSelectedMarkers;
        plot.StepSelectedMarkersHandler = container.StepSelectedMarkers;
        plot.PlotChanged += container.OnPlotChanged;
        plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
        plot.MarkerAdded += container.OnMarkerAdded;
        container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
    }

    /// <summary>The dimensions, drawn over the layout as the layout editor draws its rulers: each a ruler whose caption
    /// is its label (brief-pcd-10 D1). It takes no gesture of its own.</summary>
    private sealed class DimensionOverlay : ILayoutCanvasOverlay
    {
        public IReadOnlyList<PCellDimensionLine> Dimensions { get; set; } = [];
        public LayoutView? Layout { get; set; }

        /// <summary>The corners clicked so far with the draw tool, drawn as an open outline.</summary>
        public IReadOnlyList<(long X, long Y)> Pending { get; set; } = [];

        public void Draw(SkiaSharp.SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme theme)
        {
            if (Pending.Count > 0)
            {
                using var pen = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = theme.Selection };
                var points = Pending.Select(p => new SkiaSharp.SKPoint((float)viewport.WorldToScreenX(p.X), (float)viewport.WorldToScreenY(p.Y))).ToArray();
                canvas.DrawPoints(points.Length > 1 ? SkiaSharp.SKPointMode.Polygon : SkiaSharp.SKPointMode.Points, points, pen);
                foreach (var p in points) canvas.DrawRect(p.X - 3, p.Y - 3, 6, 6, pen);
            }
            if (Layout is null || Dimensions.Count == 0) return;
            // One view per unit, so each ruler reads its length in its label's own unit, not the layout's.
            foreach (var group in Dimensions.GroupBy(d => d.Unit))
            {
                var unit = Enum.TryParse<LayoutUnit>(group.Key, ignoreCase: true, out var u) ? u : Layout.DisplayUnit;
                var view = new LayoutView { DbuPerMicron = Layout.DbuPerMicron, DisplayUnit = unit };
                foreach (var d in group)
                    view.Rulers.Add(new RulerAnnotation { X1 = d.FromX, Y1 = d.FromY, X2 = d.ToX, Y2 = d.ToY, Caption = d.Label });
                LayoutRenderer.DrawRulersOnTop(canvas, view, viewport, new LayoutRenderOptions { Theme = theme, ShowRulers = true });
            }
        }

        public Bbox ContentBounds() => Bbox.Empty;
        public bool CompanionPressResolvedNewSelection { get; set; }
        public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount) => false;
        public bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown, KeyModifiers modifiers) => false;
        public bool OnPointerReleased(long worldX, long worldY) => false;
        public bool OnKeyDown(Key key, KeyModifiers modifiers) => false;
        public void OnKeyUp(Key key, KeyModifiers modifiers) { }
    }

    private void SyncCanvas()
    {
        if (Vm is not { } vm) return;
        var viewport = DesignCanvas.ViewModel is null ? (LayoutViewport?)null : DesignCanvas.CurrentViewport;
        DesignCanvas.ViewModel = vm.Layout is { } layout ? new LayoutEditorViewModel(layout, vm.ClayPath) { Technology = vm.Technology } : null;
        if (viewport is { } kept) DesignCanvas.SetViewport(kept);   // a re-plan keeps the view; the first one fits
        else _fitPending = true;
        SyncDimensions();
    }

    // ── Gestures (R-pcd6-2) ───────────────────────────────────────────────────────────────────────────────────────

    private (long X, long Y) World(PointerEventArgs e)
    {
        var p = e.GetPosition(DesignCanvas);
        var (x, y) = DesignCanvas.ScreenToWorld(p.X, p.Y);
        return ((long)Math.Round(x), (long)Math.Round(y));
    }

    private long Tolerance => (long)Math.Ceiling(PickPixels / Math.Max(1e-12, DesignCanvas.CurrentViewport.Zoom));

    private (int Vertex, long X, long Y, string Instance)? VertexAt(long x, long y)
    {
        if (Vm is not { } vm) return null;
        foreach (var comp in vm.Schematic.Components.Where(DrawnMetal.Is))
            foreach (var v in vm.VerticesOf(comp.InstanceName))
                if (Math.Max(Math.Abs(v.X - x), Math.Abs(v.Y - y)) <= Tolerance) return (v.Vertex, v.X, v.Y, comp.InstanceName);
        return null;
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || !e.GetCurrentPoint(DesignCanvas).Properties.IsLeftButtonPressed) return;
        var (x, y) = World(e);
        _pressAt = (x, y);
        if (DrawToggle.IsChecked == true)
        {
            // The MSHAPE draw tool (brief-pcd-11 R-pcd11-4): a click is a corner, a double-click the last one.
            if (e.ClickCount < 2 || _corners.Count == 0 || _corners[^1] != (x, y)) _corners.Add((x, y));
            if (e.ClickCount >= 2) FinishDrawing();
            else ShowPending();
            return;
        }
        _pressedVertex = false;
        if (VertexAt(x, y) is { } v)
        {
            _pressedVertex = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (!_selected.Remove((v.Instance, v.Vertex))) _selected.Add((v.Instance, v.Vertex));
                ShowSelection();
            }
            else _vertexDrag = (v.Instance, v.Vertex, v.X, v.Y);
            return;
        }
        if (vm.PinAt(x, y, Tolerance) is { } pin
            && vm.Schematic.Components.FirstOrDefault(c => c.InstanceName == pin.Instance)?.Offset is { } offset
            && offset.Pin == pin.Pin && vm.PinPoseOf(pin.Instance, pin.Pin) is { } pose)
            _offsetDrag = (pin.Instance, pin.Pin, pose.X - x, pose.Y - y);
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || DrawToggle.IsChecked == true || _pressAt is not { } at) return;
        var (x, y) = World(e);
        // A press and release in place is a click: it selects the operand under it (brief-pcd-14 R-pcd14-1).
        bool moved = Math.Max(Math.Abs(x - at.X), Math.Abs(y - at.Y)) > Tolerance;
        if (moved && _offsetDrag is { } d) vm.DragOffset(d.Instance, x + d.GrabDx, y + d.GrabDy);
        if (moved && _vertexDrag is { } v) vm.DragVertex(v.Instance, v.Vertex, v.X, v.Y, x, y);
        if (!moved && !_pressedVertex) vm.SelectAt(x, y, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        (_offsetDrag, _vertexDrag, _pressAt) = (null, null, null);
    }

    private static PaletteDragPayload? PayloadOf(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.Text) is string text && PaletteDragPayload.TryParse(text, out var payload)
                && payload is { CellDir: null, Vendor: null, PCellGeneratorId: null })
                return payload;
        return null;
    }

    private void OnPartDragOver(object? sender, DragEventArgs e)
    {
        if (PayloadOf(e) is null) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPartDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || PayloadOf(e) is not { } payload) return;
        e.Handled = true;
        var p = e.GetPosition(DesignCanvas);
        var (wx, wy) = DesignCanvas.ScreenToWorld(p.X, p.Y);
        var part = vm.PartFor(payload.Kind, payload.PortCount);
        // On a pin: joined to it (a tee when the pin is a joint); anywhere else: placed by an offset (R-pcd11-2/3).
        if (vm.PinAt((long)Math.Round(wx), (long)Math.Round(wy), Tolerance) is { } pin) vm.DropPart(part, 1, pin.Instance, pin.Pin);
        else vm.DropPartAt(part, (long)Math.Round(wx), (long)Math.Round(wy));
    }

    private void ShowSelection()
    {
        if (Vm is { } vm) _selected.RemoveAll(s => !vm.Schematic.Components.Any(c => c.InstanceName == s.Instance));
        SelectionText.Text = _selected.Count == 0
            ? "Shift-click MSHAPE vertices to select them."
            : string.Join("; ", _selected.GroupBy(s => s.Instance).Select(g => $"{g.Key}: {string.Join(", ", g.Select(s => s.Vertex))}"));
    }

    private void OnStretch(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (_selected.Select(s => s.Instance).Distinct().ToList() is not [var instance])
        {
            vm.Status = "Stretch: select vertices of one MSHAPE.";
            return;
        }
        if (StretchParameter.SelectedItem is not PCellParameterRow row)
        {
            vm.Status = "Stretch: choose the parameter.";
            return;
        }
        if (!double.TryParse(StretchAxis.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double axis))
        {
            vm.Status = "Stretch: the axis is an angle in degrees.";
            return;
        }
        if (vm.StretchByParameter(instance, _selected.Select(s => s.Vertex).Order().ToList(), row.Name, axis))
        {
            _selected.Clear();
            ShowSelection();
        }
    }

    private static PCellOperationRow? RowOf(object? sender) => (sender as Control)?.DataContext as PCellOperationRow;
    private void OnOperationApply(object? sender, RoutedEventArgs e) => Vm?.ApplyOperationRowCommand.Execute(RowOf(sender));
    private void OnOperationDelete(object? sender, RoutedEventArgs e) => Vm?.DeleteOperationRowCommand.Execute(RowOf(sender));
    private void OnOperationUp(object? sender, RoutedEventArgs e) => Vm?.MoveOperationUpCommand.Execute(RowOf(sender));
    private void OnOperationDown(object? sender, RoutedEventArgs e) => Vm?.MoveOperationDownCommand.Execute(RowOf(sender));

    private void OnAddLayer(object? sender, RoutedEventArgs e)
    {
        if (LayerPicker.SelectedItem is string layer) Vm?.AddLayerOperand(layer);
    }

    private void OnRemoveParameter(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PCellParameterRow row) Vm?.RemoveParameter(row);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PCell Package",
            SuggestedFileName = vm.CellName + PCellDesign.PackageExtension,
            DefaultExtension = PCellDesign.PackageExtension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("PCell package") { Patterns = ["*" + PCellDesign.PackageExtension] }],
        });
        if (file?.TryGetLocalPath() is { } path) vm.Export(path);
    }
}
