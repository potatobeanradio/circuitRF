using Avalonia.Controls;
using CircuitRF.Core.Matching;
using Avalonia.Platform.Storage;
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
        if (Vm is null) return;
        var magnitude = this.FindControl<PlotControl>("MagnitudePlotControl");
        var phase = this.FindControl<PlotControl>("PhasePlotControl");
        Bind(magnitude, Vm.MagnitudeContainer);
        Bind(phase, Vm.PhaseContainer);
        SyncTheme();
        ActualThemeVariantChanged += (_, _) => SyncTheme();

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

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        void Set(string name, string expression)
        {
            var p = updated.FirstOrDefault(x => x.Name == name);
            if (p is null) return;
            p.Expression = expression;
        }

        // The boxes are in the selected unit. Write them as GHz so the unit already on the
        // parameter stays the one the editor shows.
        double ToGHz(string text)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double v))
                return 0;
            double hz = Vm.Unit switch { "kHz" => v * 1e3, "MHz" => v * 1e6, "GHz" => v * 1e9, _ => v };
            return hz / 1e9;
        }

        Set("Response", Vm.Response.ToString());
        Set("Form", Vm.Form.ToString());
        Set("Order", Vm.OrderText.Trim());
        Set("Ripple", Vm.ApText.Trim());
        Set("Astop", Vm.AsText.Trim());
        Set("Zin", Vm.SourceText.Trim());
        Set("Zout", Vm.LoadText.Trim());
        string g(string t) => ToGHz(t).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        if (Vm.Form is NetworkForm.Lowpass)
            Set("Fc", g(Vm.FaText));
        else if (Vm.Form is NetworkForm.Highpass)
            Set("Fc", g(Vm.FbText));
        else if (Vm.Form is NetworkForm.Bandstop)
        {
            Set("F1", g(Vm.FbText));
            Set("F2", g(Vm.FcText));
        }
        else
        {
            Set("F1", g(Vm.FbText));
            Set("F2", g(Vm.FcText));
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
