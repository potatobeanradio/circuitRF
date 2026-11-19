using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Filters;

namespace CircuitRF.Ui.Views.Filters;

public partial class FilterDesignerWindow : Window, ICrfMenuWindow
{
    public string WindowMenuHeader => (DataContext as FilterDesignerViewModel)?.Title ?? "Filter Designer";

    public FilterDesignerWindow()
    {
        InitializeComponent();
        // The plot follows the window's light/dark variant, as the Match Designer's do.
        void Theme()
        {
            if (DataContext is not FilterDesignerViewModel vm) return;
            vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
            this.FindControl<PlotControl>("ResponsePlotControl")?.SetValue(PlotControl.PlotThemeProperty, vm.PlotHost.Theme);
        }
        DataContextChanged += (_, _) => Theme();
        ActualThemeVariantChanged += (_, _) => Theme();
        DataContextChanged += (_, _) => Watch();
        Closed += (_, _) => { Watch(null); _realiseBar?.Stop(); };
    }

    private FilterDesignerViewModel? _watched;
    private DelayedBar? _realiseBar;

    /// <summary>The bar beside "Realisers" follows the view model's work, shown only once it has run for a while.</summary>
    private void Watch() => Watch(DataContext as FilterDesignerViewModel);

    private void Watch(FilterDesignerViewModel? vm)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = vm;
        if (vm is not null) vm.PropertyChanged += OnVmPropertyChanged;
        SyncBar();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterDesignerViewModel.Working)) SyncBar();
    }

    private void SyncBar()
        => (_realiseBar ??= new DelayedBar(RealiseProgressBar, RealiseProgressBar)).Sync(_watched?.Working ?? false);

    /// <summary>The Designer over <paramref name="schematicVm"/> (Place's target; none when null), the family of
    /// <paramref name="family"/> preselected; <paramref name="technology"/> gives the substrate when the schematic's own does not.</summary>
    public static FilterDesignerWindow Show(ViewModels.SchematicViewModel? schematicVm, SymbolKind? family, Window? owner,
        Technology? technology = null)
    {
        var window = new FilterDesignerWindow { DataContext = new FilterDesignerViewModel(schematicVm, family, technology: technology) };
        if (owner is null) window.Show();
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(owner);
        }
        return window;
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => DocLauncher.Open("reference/filter-designer.html");

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
