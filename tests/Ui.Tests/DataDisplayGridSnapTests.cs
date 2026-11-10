// ================================================================
//  DataDisplayGridSnapTests.cs — with the canvas grid on, a dragged plot's
//  corner and a resized plot's far edge land on it (DataDisplayViewModel.Grid.cs).
// ================================================================

using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayGridSnapTests
{
    [Fact]
    public void ADraggedPlot_LandsOnTheGrid_AndOffItMovesFreely()
    {
        var w = new DisplayWindowViewModel();
        var d = w.DataDisplay!;
        var c = d.Plots[0];
        c.Left = 13; c.Top = 7;
        c.IsSelected = true;

        w.CycleCanvasGridCommand.Execute(null);               // coarse
        Assert.Equal(CanvasGrid.Coarse, d.CanvasGrid);
        d.BeginMoveOperation();
        c.MoveSelected(30, 50);
        Assert.Equal((40.0, 40.0), (c.Left, c.Top));          // 43,57 → nearest 40-unit corner
        c.MoveSelected(1, 1);                                 // the travel adds up: 44,58 is still nearest 40,40
        Assert.Equal((40.0, 40.0), (c.Left, c.Top));
        d.EndMoveOperation();

        w.CycleCanvasGridCommand.Execute(null);               // fine
        Assert.Equal((260.0, 160.0), c.SnapResizeTarget(263, 157));   // far edge 303,197 → 300,200, from the 40,40 corner
        w.CycleCanvasGridCommand.Execute(null);               // off
        d.BeginMoveOperation();
        c.MoveSelected(3, 3);
        Assert.Equal((43.0, 43.0), (c.Left, c.Top));
    }

    [Fact]
    public void ADraggedInfoBoxOrLegend_LandsOnTheGrid()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot(PlotType.Rect);
        var c = display.Plots[0];
        var t = new Trace(new SNP([1e9, 2e9], 2), MatrixType.S, 1, 0, DependentVarFormat.Db);
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        c.PlotVM.Plot.Traces.Add(t);
        c.Inspector.Legend = LegendPlacement.Inside;
        display.CanvasGrid = CanvasGrid.Fine;

        display.LiftLegend(c, 700, 50, new Avalonia.Point(0, 0));
        display.UpdateInfoBoxGroupDrag(new Avalonia.Point(43, 17));   // 743, 67 → the 10-unit grid
        display.EndInfoBoxGroupDrag();

        var box = Assert.Single(display.LegendBoxes);
        Assert.Equal((740.0, 70.0), (box.LogicalLeft, box.LogicalTop));
    }
}
