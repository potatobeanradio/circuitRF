// ================================================================
//  DataDisplayPasteSourceTests.cs — a pasted plot shows the data it
//  showed where it was copied, whatever the pasting display has
//  selected (PlotExporter.BuildContainersJson / PasteFromConfigAsync).
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayPasteSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crf_paste_{Guid.NewGuid():N}");

    public DataDisplayPasteSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteRun(string dir, string name, double gain)
    {
        Directory.CreateDirectory(dir);
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var ds   = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", new DataCube(new[] { freq }, new[] { gain, gain }));
        DataSetExporter.Export(ds, Path.Combine(dir, name), ExportFormat.Npy);
    }

    private static async Task<DisplayWindowViewModel> WindowOnAsync(string results, string selected)
    {
        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => results;
        await w.DataSourceLibrary.SelectDataSourceAsync(selected);
        return w;
    }

    [Theory]
    [InlineData(false)]   // another window of the same workspace, a different source selected
    [InlineData(true)]    // a window of another workspace
    public async Task APlotFollowingTheSelectedSource_PastesShowingTheSameFile(bool otherWorkspace)
    {
        string resultsA = Path.Combine(_root, "wsA", "results");
        string resultsB = otherWorkspace ? Path.Combine(_root, "wsB", "results") : resultsA;
        WriteRun(resultsA, "a.npy", 10);
        WriteRun(resultsB, "b.npy", 20);

        var from = await WindowOnAsync(resultsA, "a.npy");
        var src  = from.DataDisplay!.Plots[0];
        src.Inspector.PlotType = PlotType.Rect;
        src.Inspector.AddTraceCommand.Execute(null);
        Assert.Equal(DataSourceRef.Selected, src.PlotVM.Plot.Traces[0].SourceRef);

        string json = PlotExporter.BuildContainersJson(new[] { src });

        var to     = await WindowOnAsync(resultsB, "b.npy");
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(json, DataDisplayViewModel.JsonOpts)!;
        var pasted = (await to.DataDisplay!.PasteFromConfigAsync(config)).Single();

        var trace = pasted.PlotVM.Plot.Traces.Single();
        Assert.Equal(Path.GetFullPath(Path.Combine(resultsA, "a.npy")), Path.GetFullPath(trace.SourcePath!));
        Assert.True(trace.Points.Count > 0);
        Assert.Equal(10, trace.Points[0].Y, 6);
    }

    [Fact]
    public async Task PastedOntoTheDisplayShowingTheSameFile_ItFollowsTheSelectionAgain()
    {
        string results = Path.Combine(_root, "ws", "results");
        WriteRun(results, "a.npy", 10);

        var from = await WindowOnAsync(results, "a.npy");
        var src  = from.DataDisplay!.Plots[0];
        src.Inspector.PlotType = PlotType.Rect;
        src.Inspector.AddTraceCommand.Execute(null);
        string json = PlotExporter.BuildContainersJson(new[] { src });

        var to     = await WindowOnAsync(results, "a.npy");
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(json, DataDisplayViewModel.JsonOpts)!;
        var pasted = (await to.DataDisplay!.PasteFromConfigAsync(config)).Single();

        Assert.Equal(DataSourceRef.Selected, pasted.PlotVM.Plot.Traces.Single().SourceRef);
    }
}
