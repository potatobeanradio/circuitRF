// ================================================================
//  DataDisplaySelectAllTests.cs — Ctrl/Cmd+A selects everything
//
//  DataDisplayViewModel.SelectAll is the logic the data-display key bindings
//  (Ctrl+A / Meta+A → SelectAllCommand) route to: all plots + all markers.
// ================================================================

using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplaySelectAllTests
{
    [Fact]
    public void SelectAll_SelectsEveryPlot()
    {
        var ddvm = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        var p1   = ddvm.AddPlot(PlotType.Rect);
        var p2   = ddvm.AddPlot(PlotType.Smith);
        p1.IsSelected = false;
        p2.IsSelected = false;

        ddvm.SelectAll();

        Assert.True(p1.IsSelected);
        Assert.True(p2.IsSelected);
        Assert.True(ddvm.Plots.All(p => p.IsSelected));
    }
}
