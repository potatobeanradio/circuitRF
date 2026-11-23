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
    [Theory]
    [InlineData(90, 50, 90, 100)]
    [InlineData(-70, 50, -70, 100)]
    [InlineData(40, -60, 90, -60)]
    [InlineData(-20, -60, -70, -60)]
    [InlineData(10, 100, 90, 100)]
    public void CtrlOval_ConstrainsBothPreviewAndReleaseToACircle(double x, double y, double expectedX, double expectedY)
    {
        var oval = new CanvasOval(10, 20, 10, 20);
        var end = new Avalonia.Point(x, y);
        var preview = Assert.IsType<CanvasOval>(CircuitRF.Ui.Views.DataDisplay.PlotCanvasView.UpdateShapeEnd(
            oval, end, Avalonia.Input.KeyModifiers.Control));
        Assert.Equal(expectedX, preview.EndX);
        Assert.Equal(expectedY, preview.EndY);
        Assert.Equal(Math.Abs(preview.EndX - preview.X), Math.Abs(preview.EndY - preview.Y));
        Assert.Equal(preview, CircuitRF.Ui.Views.DataDisplay.PlotCanvasView.UpdateShapeEnd(
            preview, end, Avalonia.Input.KeyModifiers.Control));
        Assert.Equal(oval with { EndX = x, EndY = y },
            CircuitRF.Ui.Views.DataDisplay.PlotCanvasView.UpdateShapeEnd(preview, end));
    }

    [Fact]
    public void CtrlOvalResize_KeepsTheOppositeCornerFixed_ForEveryCornerAndDirection()
    {
        foreach (bool reversed in new[] { false, true })
        foreach (bool endX in new[] { false, true })
        foreach (bool endY in new[] { false, true })
        {
            var oval = reversed ? new CanvasOval(100, 80, 10, 20) : new CanvasOval(10, 20, 100, 80);
            var resized = CircuitRF.Ui.Views.DataDisplay.PlotCanvasView.ConstrainOvalResize(oval, endX, endY);
            Assert.Equal(Math.Abs(resized.EndX - resized.X), Math.Abs(resized.EndY - resized.Y));
            Assert.Equal(endX ? oval.X : oval.EndX, endX ? resized.X : resized.EndX);
            Assert.Equal(endY ? oval.Y : oval.EndY, endY ? resized.Y : resized.EndY);
            Assert.Equal(resized, CircuitRF.Ui.Views.DataDisplay.PlotCanvasView.ConstrainOvalResize(resized, endX, endY));
            Assert.Equal(oval.Color, resized.Color);
        }
    }

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
