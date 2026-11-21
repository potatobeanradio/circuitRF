using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
/// The Filter Designer. Unowned, like the Match Designer, so it can sit behind the workspace.
/// The plots are the Data Display's <see cref="PlotControl"/>.
/// </summary>
public partial class FilterDesignerWindow : Window, ICrfMenuWindow
{
    private bool _wired;

    public FilterDesignerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePlots();
    }

    public string WindowMenuHeader => "Filter Designer";

    private FilterDesignerViewModel? Vm => DataContext as FilterDesignerViewModel;

    /// <summary>Tools ▸ Filter Designer, on no component.</summary>
    public static FilterDesignerWindow ShowStandalone(string? workspaceRoot, Window? owner)
    {
        var vm = new FilterDesignerViewModel { WorkspaceRoot = workspaceRoot };
        var window = new FilterDesignerWindow { DataContext = vm };
        ShowUnowned(window, owner);
        vm.Design();
        return window;
    }

    /// <summary>Opens on a placed Filter and can write the specification back onto it.</summary>
    public static FilterDesignerWindow Show(SchematicViewModel schematic, EditableComponent component, Window? owner)
    {
        var vm = new FilterDesignerViewModel
        {
            Schematic = schematic,
            Target = component,
            WorkspaceRoot = schematic.WorkspaceRoot,
        };
        vm.SeedFrom(component);
        var window = new FilterDesignerWindow { DataContext = vm };
        ShowUnowned(window, owner);
        return window;
    }

    private static void ShowUnowned(FilterDesignerWindow window, Window? owner)
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
            t => Vm.CommitPlotWindow(FilterDesignerViewModel.ParseBandPercent(t), null));
        BindNumericBox(
            "PlotPointsBox",
            () => Vm.PlotPointsText,
            t => Vm.CommitPlotWindow(null, FilterDesignerViewModel.ParsePlotPoints(t)));
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

        if (Vm.FilterAssignments() is not { } assignments)
        {
            Vm.Status = "There is no design to apply; the specification above is refused.";
            return;
        }
        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        foreach (var (name, expression) in assignments)
        {
            var p = updated.FirstOrDefault(x => x.Name == name);
            if (p is not null)
            {
                p.Expression = expression;
                // The edges are written in GHz; a parameter that showed another unit would be
                // read in it.
                if (name is "Fc" or "F1" or "F2") p.Unit = "GHz";
            }
            // A Filter placed before EllipticType existed does not carry it; add it rather than
            // leave the block on type A while the ladder beside it is b or c.
            else if (name == "EllipticType")
                updated.Add(new EditableParameter { Name = name, Expression = expression });
        }

        schematic.Execute(new SetParametersCommand(schematic.EditModel, comp, updated));
        Vm.Status = "Applied to the Filter. The ideal block keeps the impedances you typed.";
    }

    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is null || StorageProvider is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save filter netlist",
            SuggestedFileName = "filter.cnl",
            FileTypeChoices = [new FilePickerFileType("Netlist") { Patterns = ["*.cnl"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(Vm.NetlistText());
    }
}
