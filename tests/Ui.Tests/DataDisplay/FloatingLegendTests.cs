// ================================================================
//  FloatingLegendTests.cs  —  a legend dragged out of its plot (2026-10-25)
//
//  1. A Below legend spans the plotting region, not the canvas, at any zoom; a floating one is not drawn in the plot,
//     round-trips its position through the .cdd, and is placed for an export where it was put.
//  2. Lifting a docked legend out is ONE undo step; Delete turns a selected legend — floating or docked —
//     off for its plot and leaves the plot; undo brings it back where it was.
// ================================================================

using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class FloatingLegendTests
{
    private static Trace Through(SNP snp, int row, int col)
    {
        var t = new Trace(snp, MatrixType.S, row, col, DependentVarFormat.Db);
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    private static (DataDisplayViewModel Display, PlotContainerViewModel Container) RectWithTwoTraces()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot(PlotType.Rect);
        var c   = display.Plots[0];
        var snp = new SNP([1e9, 2e9, 3e9], 2);
        c.PlotVM.Plot.Traces.Add(Through(snp, 1, 0));
        c.PlotVM.Plot.Traces.Add(Through(snp, 0, 0));
        return (display, c);
    }

    [Fact]
    public void BelowSpansThePlottingRegion_FloatingIsSavedAndPlacedButNotDrawnInThePlot()
    {
        var (display, c) = RectWithTwoTraces();
        var plot = c.PlotVM.Plot;

        plot.Legend = LegendPlacement.Below;
        var size   = (W: 600.0, H: 400.0);
        var band   = LegendRenderer.DockedBounds(size, plot, PlotRenderer.BuildTransforms(plot, size), null, false)!.Value;
        var (x0, x1) = PlotRenderer.PlotRegionX(plot, size);
        Assert.True(x0 > 20);                                          // the region is inset from the canvas edge
        Assert.True(band.Left >= x0 - 0.5 && band.Right <= x1 + 0.5);  // and the band stays within it

        // Zoomed far out, the legend is still drawn and still takes its band: it used to stop below a 4 px font.
        var tiny = (W: 120.0, H: 80.0);
        Assert.NotNull(LegendRenderer.DockedBounds(tiny, plot, PlotRenderer.BuildTransforms(plot, tiny), null, false));
        Assert.True(LegendRenderer.BelowBandPx(plot, tiny) > 0);

        plot.Legend    = LegendPlacement.Floating;
        plot.LegendPos = new PlotPoint(640, 30);
        Assert.False(LegendRenderer.Draws(plot));
        Assert.Equal(0, LegendRenderer.BelowBandPx(plot, size));

        string json = JsonSerializer.Serialize(DataDisplayViewModel.BuildPlotContainerConfig(c, "."), DataDisplayJson.Options);
        var round = JsonSerializer.Deserialize<PlotContainerConfig>(json, DataDisplayJson.Options)!;
        var loaded = PlotConfigLoader.LoadPlot(round, new NoSources());
        Assert.Equal(LegendPlacement.Floating, loaded.Legend);
        Assert.Equal(new PlotPoint(640, 30), loaded.LegendPos);

        var placed = PlotPlacement.Place(plot, c.Left, c.Top, c.Width, c.Height, FreqUnit.GHz, false, null);
        Assert.Equal((640.0, 30.0), (placed.Legend!.ViewLeft, placed.Legend.ViewTop));
        Assert.True(placed.Legend.BoxWidth > 0 && placed.Legend.BoxHeight > 0);

        // Docked, no position is written.
        plot.Legend = LegendPlacement.Inside;
        Assert.DoesNotContain("LegendLeft",
            JsonSerializer.Serialize(DataDisplayViewModel.BuildPlotContainerConfig(c, "."), DataDisplayJson.Options));
    }

    /// <summary>A plot dragged on the canvas carries its own floating legend (and marker info boxes) with it,
    /// selected or not, and undo puts both back together.</summary>
    [Fact]
    public void DraggingAPlot_CarriesItsUnselectedFloatingLegend()
    {
        var (display, c) = RectWithTwoTraces();
        c.Inspector.Legend = LegendPlacement.Inside;
        display.LiftLegend(c, 700, 50, new Avalonia.Point(0, 0));
        display.EndInfoBoxGroupDrag();
        var box = Assert.Single(display.LegendBoxes);

        display.SelectOnly(c);
        Assert.False(box.IsSelected);
        double l0 = c.Left, t0 = c.Top;

        display.BeginMoveOperation();
        c.MoveSelected(30, 20);
        display.EndMoveOperation();

        Assert.Equal((l0 + 30, t0 + 20), (c.Left, c.Top));
        Assert.Equal((730.0, 70.0), (box.LogicalLeft, box.LogicalTop));
        display.UndoRedo.Undo();
        Assert.Equal((l0, t0), (c.Left, c.Top));
        Assert.Equal((700.0, 50.0), (box.LogicalLeft, box.LogicalTop));
    }

    [Fact]
    public void LiftIsOneUndoStep_DeleteTurnsASelectedLegendOff_UndoBringsItBack()
    {
        var (display, c) = RectWithTwoTraces();
        var plot = c.PlotVM.Plot;
        c.Inspector.Legend = LegendPlacement.Inside;
        Assert.Empty(display.LegendBoxes);

        // Lift and drop: one step, back to Inside.
        display.LiftLegend(c, 700, 50, new Avalonia.Point(0, 0));
        display.UpdateInfoBoxGroupDrag(new Avalonia.Point(40, 20));
        display.EndInfoBoxGroupDrag();
        Assert.Equal(LegendPlacement.Floating, plot.Legend);
        Assert.True(c.Inspector.IsLegendOn);                           // "Legend On" stays lit through the drag
        var box = Assert.Single(display.LegendBoxes);
        Assert.Equal((740.0, 70.0), (box.LogicalLeft, box.LogicalTop));
        display.UndoRedo.Undo();
        Assert.Equal(LegendPlacement.Inside, plot.Legend);
        Assert.True(c.Inspector.IsLegendOn);
        Assert.Empty(display.LegendBoxes);
        display.UndoRedo.Redo();

        // Delete on the floating legend: off, the plot stays; undo restores the box where it was.
        display.SelectOnly(display.LegendBoxes[0]);
        Assert.True(display.HasAnySelection);
        display.DeleteSelected();
        Assert.Equal(LegendPlacement.Off, plot.Legend);
        Assert.Empty(display.LegendBoxes);
        Assert.Single(display.Plots);
        display.UndoRedo.Undo();
        Assert.Equal((740.0, 70.0), (display.LegendBoxes[0].LogicalLeft, display.LegendBoxes[0].LogicalTop));

        // "Legend On" leaves a moved legend where it is; from Below it turns the legend on in the plot's corner.
        c.Inspector.SetLegendOnCommand.Execute(null);
        Assert.Equal(LegendPlacement.Floating, plot.Legend);
        c.Inspector.Legend = LegendPlacement.Below;
        c.Inspector.SetLegendOnCommand.Execute(null);
        Assert.Equal(LegendPlacement.Inside, plot.Legend);

        // Delete on a docked legend selected in its plot.
        c.Inspector.Legend = LegendPlacement.Below;
        c.SelectDockedLegend();
        display.DeleteSelected();
        Assert.Equal(LegendPlacement.Off, plot.Legend);
        Assert.Single(display.Plots);
    }

    private sealed class NoSources : IPlotDataSources
    {
        public string?  ResolveAbs(string? sourceRef)  => sourceRef;
        public bool     Contains(string absPath)       => false;
        public SNP?     NetworkFor(string absPath)     => null;
        public RfCore.Data.DataSet? DataFor(string absPath) => null;
        public string?  AliasFor(string absPath)       => null;
        public string?  DisplayNameFor(string absPath) => absPath;
        public bool     HasMultipleSources             => false;
        public RfCore.Data.DataSet? SelectedData       => null;
    }
}
