using Avalonia.Controls;
using Avalonia.Interactivity;
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
    }

    /// <summary>The Designer over <paramref name="schematicVm"/> (Place's target; none when null), the family of
    /// <paramref name="family"/> preselected.</summary>
    public static FilterDesignerWindow Show(ViewModels.SchematicViewModel? schematicVm, SymbolKind? family, Window? owner)
    {
        var window = new FilterDesignerWindow { DataContext = new FilterDesignerViewModel(schematicVm, family) };
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
