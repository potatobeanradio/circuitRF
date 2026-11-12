// ================================================================
//  DataDisplayGraphAreaAndStepTests.cs — a rectangular plot's graph
//  area keeps its size and place however many Y-label columns it
//  carries (they widen the canvas outward; Left/Width, which the grid
//  snaps, never move), and Axes Limits' major-grid Step.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayGraphAreaAndStepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crf_garea_{Guid.NewGuid():N}");

    public DataDisplayGraphAreaAndStepTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<PlotContainerViewModel> RectPlotAsync()
    {
        string results = Path.Combine(_root, "results");
        Directory.CreateDirectory(results);
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var ds   = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", new DataCube(new[] { freq }, new[] { 10.0, 11.0 }));
        DataSetExporter.Export(ds, Path.Combine(results, "a.npy"), ExportFormat.Npy);

        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => results;
        await w.DataSourceLibrary.SelectDataSourceAsync("a.npy");
        var c = w.DataDisplay!.Plots[0];
        c.Inspector.PlotType = PlotType.Rect;
        return c;
    }

    /// <summary>The plot box in Data Display logical coordinates, from the canvas the container hands the renderer.</summary>
    private static (double Left, double Top, double Width, double Height) Box(PlotContainerViewModel c)
    {
        var r = PlotCanvasGeometry.ChartRect(c.PlotVM.Plot, c.CanvasWidthLogical, c.Height);
        return (c.CanvasLeftLogical + r.Left, c.Top + r.Top, r.Width, r.Height);
    }

    [Fact]
    public async Task AddingTracesAndARightAxis_LeavesTheGraphAreaAndTheSnappedBoxWhereTheyWere()
    {
        var c = await RectPlotAsync();
        c.Inspector.AddTraceCommand.Execute(null);
        var (left, width) = (c.Left, c.Width);
        var one = Box(c);

        c.Inspector.AddTraceCommand.Execute(null);
        c.Inspector.AddTraceCommand.Execute(null);
        c.PlotVM.Plot.Traces[2].UseSecondaryAxis = true;
        c.Inspector.OnTraceSecondaryAxisChanged();

        Assert.Equal(left,  c.Left);
        Assert.Equal(width, c.Width);
        var many = Box(c);
        Assert.Equal(one.Left,   many.Left,   9);
        Assert.Equal(one.Width,  many.Width,  9);
        Assert.Equal(one.Height, many.Height, 9);
        Assert.True(c.CanvasLeftLogical < c.Left, "the second left label column sits outside the plot's own box");
        Assert.True(c.CanvasWidthLogical > c.Width + (c.Left - c.CanvasLeftLogical), "the right axis sits outside it too");
    }

    [Fact]
    public void AStep_SetsTheMajorGridSpacing_AndOneThatWouldFloodThePlotIsIgnored()
    {
        var axes = new Axes { Window = new PlotRect(0, -7, 6, 2) };
        axes.YStep = 0.25;
        var y = axes.Ticks(minorTicks: false).MajorY.Select(p => p.Primary).Where(double.IsFinite).ToList();
        Assert.All(y.Zip(y.Skip(1)), p => Assert.Equal(0.25, p.Second - p.First, 9));

        double auto = new Axes { Window = axes.Window }.YTick;
        axes.YStep = 2.0 / (Axes.MaxUserSteps + 1) / 1.0001;   // more lines than the cap over this window
        Assert.Equal(auto, axes.YTick);
    }

    [Fact]
    public async Task AStep_IsSavedWithThePlot_AndReadBack()
    {
        var c = await RectPlotAsync();
        c.Inspector.Limits.XStepText = "0.5";

        var pc = DataDisplayViewModel.BuildPlotContainerConfig(c, "", null);
        Assert.Equal(0.5, pc.Axes!.XStep);

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        PlotConfigLoader.RestoreAxes(plot, pc.Axes);
        Assert.Equal(0.5, plot.Axes.XStep);

        c.Inspector.Limits.XStepText = "";
        Assert.Equal(0, c.PlotVM.Plot.Axes.XStep);
    }
}
