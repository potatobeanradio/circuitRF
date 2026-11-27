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
        DataContextChanged += (_, _) => { Theme(); Watch(); };
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
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PCellDesignerViewModel.Layout) && Vm is { } vm)
        {
            var viewport = DesignCanvas.ViewModel is null ? (LayoutViewport?)null : DesignCanvas.CurrentViewport;
            DesignCanvas.ViewModel = vm.Layout is { } layout ? new LayoutEditorViewModel(layout, vm.ClayPath) { Technology = vm.Technology } : null;
            if (viewport is { } kept) DesignCanvas.SetViewport(kept);   // a re-plan keeps the view; the first one fits
            else _fitPending = true;
        }
        if (e.PropertyName == nameof(PCellDesignerViewModel.Schematic)) ShowSelection();
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
        if (VertexAt(x, y) is { } v)
        {
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
        if (Vm is not { } vm) return;
        var (x, y) = World(e);
        if (_offsetDrag is { } d) vm.DragOffset(d.Instance, x + d.GrabDx, y + d.GrabDy);
        if (_vertexDrag is { } v && (v.X, v.Y) != (x, y)) vm.DragVertex(v.Instance, v.Vertex, v.X, v.Y, x, y);
        (_offsetDrag, _vertexDrag) = (null, null);
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
        if (vm.PinAt((long)Math.Round(wx), (long)Math.Round(wy), Tolerance) is not { } pin)
        {
            vm.Status = "Drop: drop the part on a pin of the layout.";
            return;
        }
        vm.DropPart(vm.PartFor(payload.Kind, payload.PortCount), 1, pin.Instance, pin.Pin);
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
