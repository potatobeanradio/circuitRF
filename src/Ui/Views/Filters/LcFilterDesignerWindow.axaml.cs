using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Views.Match;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Filters;

/// <summary>
/// The LC Filter Designer. Unowned, like the Match Designer, so it can sit behind the workspace.
/// The plots are the Data Display's <see cref="PlotControl"/>.
/// </summary>
public partial class LcFilterDesignerWindow : Window, ICrfMenuWindow
{
    private bool _wired;

    public LcFilterDesignerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePlots();
        RealOnly("SourceBox");
        RealOnly("LoadBox");
        // A schematic may have been opened or closed while this window was behind.
        Activated += (_, _) => Vm?.RefreshPlacement();
    }

    public string WindowMenuHeader => "LC Filter Designer";

    private LcFilterDesignerViewModel? Vm => DataContext as LcFilterDesignerViewModel;

    /// <summary>The windows open on placed Filters, one each.</summary>
    private static readonly LcFilterDesignerWindows<LcFilterDesignerWindow> OnFilter = new();

    /// <summary>
    /// Tools ▸ LC Filter Designer, on no component. <paramref name="schematic"/> is where Place filter
    /// and Place component go, read when the button is pressed.
    /// </summary>
    public static LcFilterDesignerWindow ShowStandalone(string? workspaceRoot, Window? owner,
                                                      Func<SchematicViewModel?>? schematic = null)
    {
        var vm = new LcFilterDesignerViewModel { WorkspaceRoot = workspaceRoot, SchematicProvider = schematic };
        var window = new LcFilterDesignerWindow { DataContext = vm };
        ShowUnowned(window, owner);
        vm.Design();
        return window;
    }

    /// <summary>
    /// Opens on a placed Filter and can write the specification back onto it — or, when a window
    /// is already open on that Filter, brings it forward instead of opening a second one.
    /// </summary>
    public static LcFilterDesignerWindow Show(SchematicViewModel schematic, EditableComponent component, Window? owner)
    {
        var (window, created) = OnFilter.Show(component, () =>
        {
            var vm = new LcFilterDesignerViewModel
            {
                Schematic = schematic,
                Target = component,
                WorkspaceRoot = schematic.WorkspaceRoot,
            };
            vm.SeedFrom(component);
            return new LcFilterDesignerWindow { DataContext = vm };
        }, w => w.Activate());
        if (created)
        {
            window.Closed += (_, _) => OnFilter.Closed(component, window);
            ShowUnowned(window, owner);
        }
        return window;
    }

    private static void ShowUnowned(LcFilterDesignerWindow window, Window? owner)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (owner is not null)
            window.Position = MatchWindowPlacement.Cascade(
                owner.Position, owner.RenderScaling,
                owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);
        window.Show();
    }

    private void WirePlots()
    {
        if (Vm is null || _wired) return;
        _wired = true;
        var magnitude = this.FindControl<PlotControl>("MagnitudePlotControl");
        var phase = this.FindControl<PlotControl>("PhasePlotControl");
        Bind(magnitude, Vm.MagnitudeContainer);
        Bind(phase, Vm.PhaseContainer);
        if (magnitude is not null)
            magnitude.Overlay = Vm.MagnitudeMask;
        SyncTheme();
        ActualThemeVariantChanged += (_, _) => SyncTheme();

        BindNumericBox(
            "BandFractionBox",
            () =>
            {
                string t = Vm.PlotBandPercentText.Trim().TrimEnd('%').Trim();
                return t + "%";
            },
            t => Vm.CommitPlotWindow(LcFilterDesignerViewModel.ParseBandPercent(t), null));
        BindNumericBox(
            "PlotPointsBox",
            () => Vm.PlotPointsText,
            t => Vm.CommitPlotWindow(null, LcFilterDesignerViewModel.ParsePlotPoints(t)));
        BindNumericBox(
            "PlotFminBox",
            () => Vm.PlotFminText,
            t => { Vm.PlotFminText = t.Trim(); Vm.UpdatePlots(); });
        BindNumericBox(
            "PlotFmaxBox",
            () => Vm.PlotFmaxText,
            t => { Vm.PlotFmaxText = t.Trim(); Vm.UpdatePlots(); });

        void Bind(PlotControl? plot, PlotContainerViewModel container)
        {
            if (plot is null) return;
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
    }

    /// <summary>
    /// Match Designer's commit-on-Return-and-focus-loss for the ± band / points boxes.
    /// </summary>
    private void BindNumericBox(string name, Func<string> read, Action<string> write)
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        box.Text = read();
        box.LostFocus += (_, _) =>
        {
            write(box.Text ?? "");
            box.Text = read();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Return && e.Key != Key.Enter) return;
            write(box.Text ?? "");
            box.Text = read();
            e.Handled = true;
        };
    }

    private void SyncTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;
        this.FindControl<PlotControl>("MagnitudePlotControl")
            ?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        this.FindControl<PlotControl>("PhasePlotControl")
            ?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    private void OnZoomFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        this.FindControl<MatchSchematicCanvas>("NetworkSchematic")?.ZoomToFit();

    private void OnApply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm?.Target is not { } comp || Vm.Schematic is not { } schematic) return;
        if (comp.Symbol != SymbolKind.Filter) return;

        // The same function Place component writes with, so the two cannot disagree. A Filter
        // placed before a Designer setting existed gains it here.
        if (Vm.AppliedParameters(comp.Parameters) is not { } updated)
        {
            Vm.Status = "There is no design to apply; the specification above is refused.";
            return;
        }

        schematic.Execute(new SetParametersCommand(schematic.EditModel, comp, updated));
        Vm.Status = "Applied to the Filter. The ideal block keeps the impedances you typed.";
    }

    private void OnPlaceFilter(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Status = Vm.PlaceFilter();
    }

    private void OnPlaceComponent(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Status = Vm.PlaceComponent();
    }

    private void OnSendMask(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Status = Vm.SendMaskToOptimizer();
    }

    /// <summary>
    /// The Source and Load boxes take a real number only: a keystroke that cannot be part of one is not
    /// typed, and a paste or edit that leaves the box holding anything else is undone. The view model refuses
    /// the same text, but a TextBox does not read back a value its own binding was just refused.
    /// </summary>
    private void RealOnly(string name)
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        string good = box.Text ?? "";
        box.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!LcFilterDesignerViewModel.IsRealNumberText(e.Text)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        box.TextChanged += (_, _) =>
        {
            string now = box.Text ?? "";
            if (LcFilterDesignerViewModel.IsRealNumberText(now)) { good = now; return; }
            int caret = box.CaretIndex;
            box.Text = good;
            box.CaretIndex = Math.Min(Math.Max(0, caret - (now.Length - good.Length)), good.Length);
        };
    }
}
