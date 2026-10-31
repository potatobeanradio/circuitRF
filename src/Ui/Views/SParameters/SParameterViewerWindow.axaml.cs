using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.SParameters;

namespace CircuitRF.Ui.Views.SParameters;

/// <summary>
/// The S-Parameter Viewer's window. A tool window of its own, as the Match Designer and the Signal Designer
/// are — opened from Tools, from a Touchstone file in the Workspace, from a placed SnP component and from a
/// Data Display's dataset list.
/// </summary>
public partial class SParameterViewerWindow : Window, ICrfMenuWindow
{
    /// <summary>One viewer per file: a second request for a file already on show brings that window forward.</summary>
    private static readonly Dictionary<string, SParameterViewerWindow> OpenByPath = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PlotNames = ["SmithPlotControl", "PolarPlotControl", "MagnitudePlotControl", "PhasePlotControl"];

    private readonly System.Collections.ObjectModel.ObservableCollection<MarkerInfoBoxViewModel> _boxes = [];
    private (PlotControl Plot, PlotContainerViewModel Container, HoverMark Mark)[] _hosted = [];
    private string? _registeredPath;

    /// <summary>In the Window menu as in the title bar.</summary>
    public string WindowMenuHeader => Vm?.Title ?? SParameterViewerViewModel.ToolName;

    public SParameterViewerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SyncPlotTheme();
            if (Vm is { } vm)
            {
                vm.PlotsUpdated += (_, _) => RepaintPlots();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SParameterViewerViewModel.FilePath)) Register(vm.FilePath);
                    if (e.PropertyName is nameof(SParameterViewerViewModel.ShowSmith)
                                      or nameof(SParameterViewerViewModel.ShowMagnitude)
                                      or nameof(SParameterViewerViewModel.ShowPolar)
                                      or nameof(SParameterViewerViewModel.ShowPhase))
                        ApplyPlotRows();
                };
                WireMarkers(vm);
                ApplyPlotRows();
            }
        };
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
        // A press anywhere but on an info box clears the marker selection, as the Data Display's canvas does.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        Closed += (_, _) => Register(null);

        // A Touchstone file dropped anywhere on the window opens it — and, on a placed SnP's Preview, becomes
        // that component's File (SParameterViewerViewModel.DropFileAsync).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnTouchstoneDragOver);
        AddHandler(DragDrop.DropEvent, OnTouchstoneDrop);
    }

    /// <summary>Whether <paramref name="path"/> names a Touchstone file by its extension: <c>.s1p</c>…<c>.s99p</c>,
    /// <c>.snp</c> or <c>.ts</c> — the picker's own filter plus the version-2 spelling.</summary>
    internal static bool IsTouchstonePath(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".snp", StringComparison.OrdinalIgnoreCase)
            || Archive.WorkspaceArchiveScanner.IsTouchstone(ext);
    }

    /// <summary>The first existing Touchstone file a drag carries — a file from the file manager, or a path dragged as text.</summary>
    private static string? DroppedTouchstone(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            object? raw = item.TryGetRaw(DataFormat.File) ?? item.TryGetRaw(DataFormat.Text);
            IEnumerable<string?> paths = raw switch
            {
                IStorageItem single             => [single.Path?.LocalPath],
                IEnumerable<IStorageItem> files => files.Select(f => f.Path?.LocalPath),
                string text                     => [text.Trim().Trim('"')],
                _                               => [],
            };
            foreach (string? p in paths)
                if (p is not null && IsTouchstonePath(p) && File.Exists(p)) return p;
        }
        return null;
    }

    private void OnTouchstoneDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Vm is { IsLoading: false } && DroppedTouchstone(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTouchstoneDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm || DroppedTouchstone(e) is not { } path) return;
        e.Handled = true;
        await vm.DropFileAsync(path);
    }

    private SParameterViewerViewModel? Vm => DataContext as SParameterViewerViewModel;

    // ── opening ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tools ▸ S-Parameter Viewer: an empty viewer, waiting for a file. Not deduplicated — several may be
    /// open — and shown unowned, centred on the opening window's screen, and closed with that window.
    /// </summary>
    public static SParameterViewerWindow ShowStandalone(Window? owner)
    {
        var window = Create(owner);
        Present(window, owner);
        return window;
    }

    /// <summary>The viewer on <paramref name="path"/>: the window already showing it, or a new one reading it.</summary>
    /// <param name="setComponentFile">A placed SnP's Preview passes this: a Touchstone file dropped on the window then
    /// becomes that component's File (<see cref="SParameterViewerViewModel.SetComponentFile"/>). The window already
    /// showing the file takes it too, so the latest Preview is the component a drop changes.</param>
    public static SParameterViewerWindow ShowForFile(string path, Window? owner, Func<string, string?>? setComponentFile = null)
    {
        string full = Path.GetFullPath(path);
        if (OpenByPath.TryGetValue(full, out var existing))
        {
            if (setComponentFile is not null && existing.Vm is { } shown) shown.SetComponentFile = setComponentFile;
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var window = Create(owner);
        window.Vm!.SetComponentFile = setComponentFile;
        window.Register(full);
        Present(window, owner);
        _ = window.Vm!.LoadAsync(full);
        return window;
    }

    private static SParameterViewerWindow Create(Window? owner)
    {
        var vm = new SParameterViewerViewModel();
        var window = new SParameterViewerWindow { DataContext = vm };
        vm.PickFileAsync = window.PickTouchstoneAsync;
        vm.PickDisplayPathAsync = window.PickDisplayPathAsync;
        vm.ConfirmAsync = (heading, text) =>
            Dialogs.TextConfirmDialog.AskAsync(window, SParameterViewerViewModel.ToolName, heading, text, "Draw All");
        vm.OpenDataDisplay = cdd => WorkspaceLocator.For(owner)?.OpenDataDisplayDocument(cdd);
        return window;
    }

    private static void Present(SParameterViewerWindow window, Window? owner)
    {
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            return;
        }
        void CloseWithOwner(object? _, EventArgs __) => window.Close();
        owner.Closed += CloseWithOwner;
        window.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        // Centred on the screen the opening window is on — not cascaded off that window's corner, which put
        // the viewer wherever a floating panel or a small owner happened to sit.
        if (owner.Screens?.ScreenFromWindow(owner) is not { } screen)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            NewWindowFront.Keep(window, owner);
            return;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var at = CenteredIn(screen.WorkingArea, new Size(window.Width, window.Height), owner.DesktopScaling);
        window.Position = at;
        window.Show();
        window.Position = at;
        NewWindowFront.Keep(window, owner);
    }

    /// <summary>
    /// The position that centres a window of <paramref name="size"/> DIPs in <paramref name="workingArea"/>.
    /// </summary>
    /// <remarks>
    /// <b>The size converts with DESKTOP scaling, never render scaling</b> — the factor between DIPs and the
    /// units <see cref="Window.Position"/> and <see cref="Avalonia.Platform.Screen.WorkingArea"/> are in, which is
    /// what Avalonia's own CenterScreen uses. On macOS those units are points and the factor is 1; converting with
    /// render scaling there doubles the width and pins the window to the corner (MatchWindowPlacement's note). A
    /// window larger than the area keeps its top-left corner inside it.
    /// </remarks>
    internal static PixelPoint CenteredIn(PixelRect workingArea, Size size, double desktopScaling)
    {
        double k = desktopScaling > 0 ? desktopScaling : 1.0;
        int w = double.IsNaN(size.Width) ? 0 : (int)Math.Round(size.Width * k);
        int h = double.IsNaN(size.Height) ? 0 : (int)Math.Round(size.Height * k);
        return new PixelPoint(workingArea.X + Math.Max(0, (workingArea.Width - w) / 2),
                              workingArea.Y + Math.Max(0, (workingArea.Height - h) / 2));
    }

    /// <summary>Keeps <see cref="OpenByPath"/> naming the file this window shows — which changes when Open picks another.</summary>
    private void Register(string? path)
    {
        if (_registeredPath is { } old && OpenByPath.TryGetValue(old, out var w) && ReferenceEquals(w, this))
            OpenByPath.Remove(old);
        _registeredPath = path;
        if (path is not null) OpenByPath.TryAdd(path, this);
    }

    // ── pickers ──────────────────────────────────────────────────────────────

    /// <summary>The Touchstone picker's file types — the SnP component's own filter, so both offer the same files.</summary>
    internal static readonly FilePickerFileType TouchstoneFiles = new("Touchstone (*.sNp)")
    {
        Patterns = ["*.s1p", "*.s2p", "*.s3p", "*.s4p", "*.s5p", "*.s6p", "*.s7p", "*.s8p", "*.s9p", "*.s10p", "*.s11p", "*.s12p", "*.snp",
                    "*.S1P", "*.S2P", "*.S3P", "*.S4P", "*.S5P", "*.S6P", "*.S7P", "*.S8P", "*.S9P", "*.S10P", "*.S11P", "*.S12P", "*.SNP"],
    };

    private async Task<string?> PickTouchstoneAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Touchstone File",
            AllowMultiple = false,
            FileTypeFilter = [TouchstoneFiles, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickDisplayPathAsync(string suggestedName)
    {
        var start = Vm?.FilePath is { } p && Path.GetDirectoryName(p) is { } dir
            ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as Data Display",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "cdd",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Data Display (*.cdd)") { Patterns = ["*.cdd"] }],
        });
        return file?.TryGetLocalPath();
    }

    // ── the plots ────────────────────────────────────────────────────────────

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        foreach (var name in PlotNames)
            this.FindControl<PlotControl>(name)?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    /// <summary>The plots are refilled in place (same Plot, new traces), and a PlotControl repaints by itself only
    /// when its Plot is replaced or the user touches it — so every refill repaints all three here.</summary>
    private void RepaintPlots()
    {
        foreach (var name in PlotNames) this.FindControl<PlotControl>(name)?.InvalidateVisual();
    }

    /// <summary>
    /// The plots are bare PlotControls, so this window is their host, as the Signal Designer is for its own: it
    /// hands each one its container's marker providers and events, and gives the info boxes somewhere to be drawn.
    /// Without this a marker is added with no info box and cannot be selected or deleted.
    /// </summary>
    private void WireMarkers(SParameterViewerViewModel vm)
    {
        var layer = this.FindControl<ItemsControl>("InfoBoxLayer")!;
        layer.ItemsSource = _boxes;
        _hosted =
        [
            (this.FindControl<PlotControl>("SmithPlotControl")!, vm.SmithContainer, new HoverMark()),
            (this.FindControl<PlotControl>("PolarPlotControl")!, vm.PolarContainer, new HoverMark()),
            (this.FindControl<PlotControl>("MagnitudePlotControl")!, vm.MagnitudeContainer, new HoverMark()),
            (this.FindControl<PlotControl>("PhasePlotControl")!, vm.PhaseContainer, new HoverMark()),
        ];
        foreach (var (plot, container, mark) in _hosted)
        {
            plot.Overlay = mark;
            plot.AddHandler(PointerMovedEvent, (_, e) => OnPlotHover(plot, mark, e), RoutingStrategies.Bubble, handledEventsToo: true);
            plot.AddHandler(PointerExitedEvent, (_, _) => ClearHover(plot, mark), RoutingStrategies.Bubble, handledEventsToo: true);
            plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
            plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
            plot.ContainerProvider           = () => container;
            plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
            plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;
            plot.DoubleTapped += (_, args) => { plot.HandleDoubleTapAt(args.GetPosition(plot)); args.Handled = true; };
            plot.PlotChanged += container.OnPlotChanged;
            plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
            plot.MarkerAdded += container.OnMarkerAdded;
            container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();
        }
        vm.PlotHost.MarkerInfoBoxes.CollectionChanged += (_, _) => RefillBoxes();
        RefillBoxes();
        // LayoutUpdated rather than SizeChanged: a plot can move without resizing, and its boxes must follow.
        LayoutUpdated += (_, _) => SyncPlotContainers();
    }

    private void RefillBoxes()
    {
        if (Vm is null) return;
        _boxes.Clear();
        foreach (var box in Vm.PlotHost.MarkerInfoBoxes) _boxes.Add(box);
    }

    private void SyncPlotContainers()
    {
        if (this.FindControl<ItemsControl>("InfoBoxLayer") is not { } layer) return;
        foreach (var (plot, container, _) in _hosted)
        {
            if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) continue;
            if (plot.TranslatePoint(default, layer) is not { } origin) continue;
            if (Math.Abs(container.Left - origin.X) < 0.5 && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5 && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                continue;   // LayoutUpdated fires constantly; only a real move is worth notifying
            container.Left = origin.X;
            container.Top = origin.Y;
            container.Width = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
        PlaceLegends();
    }

    /// <summary>The line-width unit every plot is drawn with, whatever size its host is. The renderer's own
    /// unit is min(width, height)/200, so a plot that fills the pane would stroke twice as heavily as one
    /// sharing the pane.</summary>
    private const double StrokeUnit = 1.8;
    private const double BaseTickFont = 6;
    private const double BaseLabelFont = 8;
    private const double BaseGrid = 0.5;
    private const double BaseTick = 0.5;
    private const double BaseLine = 1;
    private const double LegendGap = 6;

    /// <summary>
    /// The trace names sit just beside the graph: the disc on a Smith chart, the plot box on a rectangular
    /// one. The rectangular box is centred, so the list on its right does not shove the graph to the left.
    /// </summary>
    private void PlaceLegends()
    {
        PlaceLegend(this.FindControl<PlotControl>("SmithPlotControl"), this.FindControl<ItemsControl>("SmithLegendList"));
        PlaceLegend(this.FindControl<PlotControl>("PolarPlotControl"), this.FindControl<ItemsControl>("PolarLegendList"));
        PlaceLegend(this.FindControl<PlotControl>("MagnitudePlotControl"), this.FindControl<ItemsControl>("MagnitudeLegendList"));
        PlaceLegend(this.FindControl<PlotControl>("PhasePlotControl"), this.FindControl<ItemsControl>("PhaseLegendList"));
    }

    private static void PlaceLegend(PlotControl? plot, ItemsControl? legend)
    {
        if (plot?.Plot is not { } model || legend is null) return;
        if (plot.Bounds.Width < 2 || plot.Bounds.Height < 2) return;
        HoldStroke(plot);

        double lw = legend.Bounds.Width, lh = legend.Bounds.Height;
        if (lw < 1 || lh < 1) return;

        if (model.PlotType == PlotType.Rect)
        {
            // The list needs a strip on the right. On a wide plot the same strip on the left keeps
            // the box centred. On a narrow one — the bottom row, or the two rectangular plots side
            // by side — mirroring it would leave the curves a thin band, so the left edge stays
            // just wide enough for the axis figures and the box takes the rest.
            double right = Math.Clamp((lw + LegendGap + 4) / plot.Bounds.Width, 0.05, 0.42);
            const double labelMin = 0.11;
            double left, box;
            if (right <= 0.16)
            {
                double margin = Math.Max(right, labelMin);
                left = margin;
                box = 1 - 2 * margin;
            }
            else
            {
                left = labelMin;
                box = Math.Max(0.5, 1 - labelMin - right);
            }
            var v = model.Axes.Viewport;
            if (Math.Abs(v.X - left) > 0.008 || Math.Abs(v.Width - box) > 0.008)
            {
                model.Axes.Viewport = new PlotRect(left, v.Y, box, v.Height);
                plot.InvalidateVisual();
            }
        }

        var chart = PlotCanvasGeometry.ChartRect(model, plot.Bounds.Width, plot.Bounds.Height);
        double x, y;
        if (model.PlotType is PlotType.Smith or PlotType.Polar)
            (x, y) = RoundLabel(chart, lw, lh, plot.Bounds.Width, plot.Bounds.Height);
        else
        {
            x = Math.Clamp(chart.Left + chart.Width + LegendGap, 0, Math.Max(0, plot.Bounds.Width - lw - 2));
            y = Math.Clamp(chart.Top + (chart.Height - lh) / 2, 0, Math.Max(0, plot.Bounds.Height - lh - 2));
        }
        if (double.IsNaN(Canvas.GetLeft(legend)) || Math.Abs(Canvas.GetLeft(legend) - x) > 0.5)
            Canvas.SetLeft(legend, x);
        if (double.IsNaN(Canvas.GetTop(legend)) || Math.Abs(Canvas.GetTop(legend) - y) > 0.5)
            Canvas.SetTop(legend, y);
    }

    /// <summary>
    /// Where the names of a round chart go: up in the corner, just outside the arc. The corner of the
    /// disc's box is empty, so the list can sit inward of the circle's widest point and still clear
    /// the chart — which is what keeps it from being cut off at the edge of the host.
    /// </summary>
    private static (double X, double Y) RoundLabel(
        (double Left, double Top, double Width, double Height) chart,
        double labelW, double labelH, double hostW, double hostH)
    {
        double cx = chart.Left + chart.Width / 2, cy = chart.Top + chart.Height / 2;
        double r = Math.Min(chart.Width, chart.Height) / 2;
        const double pad = 4;

        bool Fits(double tryY, out double xTry)
        {
            xTry = 0;
            if (tryY < 0 || tryY + labelH > hostH - 2) return false;
            double yNear = Math.Clamp(cy, tryY, tryY + labelH);
            double dy = yNear - cy;
            double half = Math.Abs(dy) >= r ? 0 : Math.Sqrt(r * r - dy * dy);
            xTry = Math.Abs(dy) >= r
                ? Math.Max(0, chart.Left + chart.Width - labelW - pad)
                : cx + half + pad;
            return xTry + labelW <= hostW - 2;
        }

        for (double tryY = Math.Max(0, chart.Top); tryY + labelH < cy && tryY + labelH < hostH; tryY += 3)
            if (Fits(tryY, out double xTry)) return (xTry, tryY);

        return (Math.Max(0, Math.Min(chart.Left + chart.Width - labelW, hostW - labelW - 2)),
                Math.Max(0, chart.Top));
    }

    /// <summary>Scales the plot's own weights so the rendered stroke is <see cref="StrokeUnit"/>, not a
    /// function of how large the host happens to be this arrangement.</summary>
    private static void HoldStroke(PlotControl plot)
    {
        if (plot.Plot is not { } model) return;
        double natural = Math.Min(plot.Bounds.Width, plot.Bounds.Height) / 200.0;
        if (natural < 0.25) return;
        double k = StrokeUnit / natural;
        var ax = model.Axes;
        bool axesHeld = Math.Abs(ax.FontSizeTicks - BaseTickFont * k) < 0.04;
        bool linesHeld = model.Traces.Count == 0
                      || Math.Abs(model.Traces[0].Properties.LineWidth - BaseLine * k) < 0.04;
        if (axesHeld && linesHeld) return;
        ax.FontSizeTicks = BaseTickFont * k;
        ax.FontSizeLabel = BaseLabelFont * k;
        ax.GridThicknessFactor = BaseGrid * k;
        ax.TickThicknessFactor = BaseTick * k;
        foreach (var t in model.Traces)
            t.Properties.LineWidth = BaseLine * k;
        plot.InvalidateVisual();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (e.Source is Visual v && v.FindAncestorOfType<DataDisplay.MarkerInfoBoxView>(includeSelf: true) is not null)
            return;
        Vm.PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null);
    }

    /// <summary>A plot that collapses under the pointer never gets PointerExited, so the hover box would
    /// stay up over whatever was turned on in its place. The layout itself follows the three flags.</summary>
    private void ApplyPlotRows()
    {
        this.FindControl<SParameterPlotLayout>("PlotGrid")?.InvalidateMeasure();
        HideReadout();
        foreach (var (plot, _, mark) in _hosted)
            if (mark.Move(null, default)) plot.InvalidateVisual();
    }

    // ── hover readout ────────────────────────────────────────────────────────

    /// <summary>How close the pointer must be, in pixels, before a point of the trace is named. A rectangular
    /// plot also accepts a point near the cursor's frequency, because the value being read is the one at that
    /// frequency and the cursor is rarely on the stroke itself.</summary>
    private const double HoverReachPx = 18;
    private const double HoverFrequencyPx = 22;
    private const double HoverValuePx = 56;

    private void OnPlotHover(PlotControl plot, HoverMark mark, PointerEventArgs e)
    {
        if (plot.Plot is null || e.GetCurrentPoint(plot).Properties.IsLeftButtonPressed)
        {
            ClearHover(plot, mark);
            return;
        }
        var pos = e.GetPosition(plot);
        var tf = PlotRenderer.BuildTransforms(plot.Plot, (plot.Bounds.Width, plot.Bounds.Height));

        foreach (var (other, _, otherMark) in _hosted)
            if (!ReferenceEquals(other, plot) && otherMark.Clear()) other.InvalidateVisual();

        if (plot.Name is "MagnitudePlotControl" or "PhasePlotControl")
        {
            if (MagnitudeCursor(plot, pos, tf, mark, out string cursorText))
            {
                if (mark.Changed) plot.InvalidateVisual();
                ShowReadout(plot, pos, cursorText);
            }
            else
            {
                if (mark.Clear()) plot.InvalidateVisual();
                HideReadout();
            }
            return;
        }

        Trace? hit = null;
        int index = -1;
        var world = default(Vector2);
        bool onStroke = false;
        double best = HoverReachPx;
        double bestAlong = HoverValuePx;
        foreach (var trace in plot.Plot.Traces)
        {
            for (int i = 0; i < trace.Points.Count; i++)
            {
                var c = tf.ToCanvas(trace.Points[i].X, trace.Points[i].Y, trace.UseSecondaryAxis);
                double dx = c.X - pos.X, dy = c.Y - pos.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best)
                {
                    best = d; onStroke = true;
                    hit = trace; index = i; world = trace.Points[i];
                }
                else if (!onStroke && plot.Plot.PlotType.IsRect()
                         && Math.Abs(dx) < HoverFrequencyPx && Math.Abs(dy) < bestAlong)
                {
                    bestAlong = Math.Abs(dy);
                    hit = trace; index = i; world = trace.Points[i];
                }
            }
        }

        if (hit is null)
        {
            if (mark.Clear()) plot.InvalidateVisual();
            HideReadout();
            return;
        }
        if (mark.Move(world, hit.Properties.LineColor)) plot.InvalidateVisual();
        ShowReadout(plot, pos, Readout(plot.Plot, hit, index));
    }

    /// <summary>
    /// A rectangular plot reads every trace at the pointer's frequency: one vertical line, a dot where
    /// it crosses each curve, and every value in the popup. False when the pointer is outside the plot
    /// box or off the frequency span.
    /// </summary>
    private static bool MagnitudeCursor(PlotControl host, Point pos, TransformSet tf, HoverMark mark, out string text)
    {
        text = "";
        var plot = host.Plot!;
        var vp = tf.Viewport;
        double left = vp.X * host.Bounds.Width, right = (vp.X + vp.Width) * host.Bounds.Width;
        double top = vp.Y * host.Bounds.Height, bottom = (vp.Y + vp.Height) * host.Bounds.Height;
        if (pos.X < left || pos.X > right || pos.Y < top || pos.Y > bottom) return false;

        double worldX = tf.PrimaryFromCanvas((float)pos.X, (float)pos.Y).Wx;
        if (!double.IsFinite(worldX)) return false;

        var hits = new List<(Vector2 At, SKColor Color)>();
        var lines = new List<string>();
        foreach (var trace in plot.Traces)
        {
            if (!Intersection(trace, worldX, out double y)) continue;
            hits.Add((new Vector2((float)worldX, (float)y), trace.Properties.LineColor));
            var marker = new Marker(trace, 0, false, false, 0);
            lines.Add(DbFloor.Label(trace.ReadoutDescription(false), trace.FormatScalarValue(y, marker)));
        }
        if (hits.Count == 0) return false;

        double hz = worldX / plot.FreqUnits.Scale();
        text = (double.IsFinite(hz) ? SParameterViewerPreset.Hz(hz) + "\n" : "") + string.Join("\n", lines);
        mark.MoveCursor(worldX, plot.Axes.Window.Top, plot.Axes.Window.Bottom, hits);
        return true;
    }

    /// <summary>The plotted value where a vertical line at <paramref name="worldX"/> crosses the trace.
    /// Between two samples it is the straight segment the plot draws, not a new sample.</summary>
    private static bool Intersection(Trace trace, double worldX, out double y)
    {
        y = double.NaN;
        var pts = trace.Points;
        if (pts.Count == 0 || worldX < pts[0].X || worldX > pts[^1].X) return false;
        if (pts.Count == 1) { y = pts[0].Y; return double.IsFinite(y); }
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double x0 = pts[i].X, x1 = pts[i + 1].X;
            if (x1 == x0) continue;
            bool spans = (x0 <= worldX && worldX <= x1) || (x1 <= worldX && worldX <= x0);
            if (!spans) continue;
            double t = (worldX - x0) / (x1 - x0);
            y = pts[i].Y + t * (pts[i + 1].Y - pts[i].Y);
            return double.IsFinite(y);
        }
        return false;
    }

    private void ClearHover(PlotControl plot, HoverMark mark)
    {
        if (mark.Clear()) plot.InvalidateVisual();
        HideReadout();
    }

    /// <summary>The marker box's own sentence for this sample, and the frequency it sits at.</summary>
    private static string Readout(Plot plot, Trace trace, int index)
    {
        double hz = trace.Data.Frequencies.Length == trace.Points.Count
            ? trace.Data.Frequencies[index]
            : plot.PlotType.IsRect() ? trace.Points[index].X / plot.FreqUnits.Scale() : double.NaN;
        var marker = new Marker(trace, double.IsFinite(hz) ? hz : 0, false, false, 0);
        if (plot.PlotType == PlotType.Smith)
        {
            string? z = null;
            if (trace.MarkerShowsImpedance(marker))
            {
                z = trace.GetMarkerImpedanceString(marker);
                if (z.Length == 0) z = null;
            }
            string term = $"{trace.MatrixType}({trace.Row + 1},{trace.Col + 1})";
            return SParameterViewerPreset.SmithHover(term, hz, trace.GetMarkerDataPoint(marker), z);
        }
        string value = trace.GetMarkerValString(marker, false);
        if (!double.IsFinite(hz)) return value;
        return plot.PlotType == PlotType.Polar
            ? SParameterViewerPreset.Hz(hz) + "\n" + value
            : value + "\n" + SParameterViewerPreset.Hz(hz);
    }

    private void ShowReadout(PlotControl plot, Point at, string text)
    {
        var layer = this.FindControl<Canvas>("HoverLayer");
        var box = this.FindControl<Border>("HoverReadout");
        var label = this.FindControl<TextBlock>("HoverReadoutText");
        if (layer is null || box is null || label is null) return;
        if (plot.TranslatePoint(at, layer) is not { } origin) return;

        label.Text = text;
        box.IsVisible = true;
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        double w = box.Bounds.Width > 1 ? box.Bounds.Width : 180;
        double h = box.Bounds.Height > 1 && box.Bounds.Height >= 14 * lines ? box.Bounds.Height : 16 * lines + 10;
        double x = origin.X + 16, y = origin.Y + 16;
        if (x + w > layer.Bounds.Width) x = origin.X - w - 10;
        if (y + h > layer.Bounds.Height) y = origin.Y - h - 10;
        Canvas.SetLeft(box, Math.Max(0, x));
        Canvas.SetTop(box, Math.Max(0, y));
    }

    private void HideReadout()
    {
        if (this.FindControl<Border>("HoverReadout") is { } box) box.IsVisible = false;
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/sparameter-viewer.html");

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Hover chrome. On the Smith chart it is a dot on the nearest sample. On the magnitude and phase
/// plots it is a vertical line at the pointer's frequency, with a dot where the line crosses each
/// trace. It draws and nothing else: a press is the plot's own, so panning and marker placement
/// are unchanged.
/// </summary>
sealed class HoverMark : IPlotOverlay
{
    private Vector2? _at;
    private SKColor _color;
    private bool _cursor;
    private double _x, _yTop, _yBot;
    private readonly List<(Vector2 At, SKColor Color)> _hits = [];

    /// <summary>True when the last <see cref="Move"/> or <see cref="MoveCursor"/> changed what is drawn.</summary>
    public bool Changed { get; private set; }

    /// <summary>Moves the dot. False when it was already there, so a pointer crossing the plot does not
    /// repaint it on every pixel.</summary>
    public bool Move(Vector2? at, SKColor color)
    {
        Changed = _cursor || _hits.Count > 0 || _at != at || _color != color;
        _cursor = false;
        _hits.Clear();
        _at = at;
        _color = color;
        return Changed;
    }

    /// <summary>The magnitude cursor: the line at <paramref name="worldX"/> and a dot on every crossing.</summary>
    public void MoveCursor(double worldX, double yTop, double yBot, List<(Vector2 At, SKColor Color)> hits)
    {
        Changed = !_cursor || _x != worldX || _hits.Count != hits.Count;
        _cursor = true;
        _at = null;
        _x = worldX;
        _yTop = yTop;
        _yBot = yBot;
        _hits.Clear();
        _hits.AddRange(hits);
    }

    /// <summary>Removes the dot and the line.</summary>
    public bool Clear() => Move(null, default);

    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        if (_cursor)
        {
            var a = tf.ToCanvas(_x, _yTop, false);
            var b = tf.ToCanvas(_x, _yBot, false);
            using var line = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f,
                Color = RenderTheme.WithOpacity(theme.TextColor, 0.55),
            };
            canvas.DrawLine(a, b, line);
            foreach (var (at, color) in _hits) DrawDot(canvas, tf.ToCanvas(at.X, at.Y, false), color, 4f);
            return;
        }
        if (_at is not { } one) return;
        DrawDot(canvas, tf.ToCanvas(one.X, one.Y, false), _color, 5f);
    }

    private static void DrawDot(SKCanvas canvas, SKPoint c, SKColor color, float radius)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = SKColors.White };
        canvas.DrawCircle(c, radius, fill);
        canvas.DrawCircle(c, radius, ring);
    }

    public object? HitTest(double canvasX, double canvasY, TransformSet tf) => null;
    public void DragBegin(object handle) { }
    public void DragTo(Complex gammaWorld) { }
    public void DragEnd(bool cancelled) { }
}
