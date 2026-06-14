using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Phase 7.1c-1 gate test — verifies the VM stack can be instantiated and basic
/// operations work without any view or UI thread.
/// </summary>
public sealed class DataDisplayVmSmokeTest
{
    [Fact]
    public void DisplayWindowViewModel_creates_one_tab_with_one_plot()
    {
        var vm = new DisplayWindowViewModel();

        // Should have exactly one tab created in constructor.
        Assert.Single(vm.Tabs);
        Assert.NotNull(vm.ActiveTab);

        var tab = vm.Tabs[0];
        Assert.IsType<TabViewModel>(tab);

        // The tab's DataDisplay (canvas VM) should have been created.
        var display = tab.DataDisplay;
        Assert.NotNull(display);

        // Constructor adds one empty Smith plot.
        Assert.Single(display.Plots);
        Assert.IsType<PlotContainerViewModel>(display.Plots[0]);
    }

    [Fact]
    public void AddPlot_increases_plot_count()
    {
        var vm = new DisplayWindowViewModel();
        var display = vm.ActiveTab!.DataDisplay;

        // Start with the one default plot.
        Assert.Single(display.Plots);

        display.AddPlot(PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(2, display.Plots.Count);
    }
}
