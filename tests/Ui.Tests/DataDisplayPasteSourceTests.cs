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

    /// <summary>
    /// The clipboard path end to end, as the views wire it: a display that was already open when another display copied —
    /// and a new, unsaved display with no source selected and no results folder — has Paste enabled and pastes the plot,
    /// its data with it. Paste's enabled state used to be worked out once, at attach, before the clipboard reader was
    /// wired, and never again unless the same display copied; Ctrl/Cmd+V then did nothing in any other display.
    /// </summary>
    [Theory]
    [InlineData(false)]   // another display, open before the copy
    [InlineData(true)]    // a new, unsaved display: nothing selected, no results folder
    public async Task CopyInOneDisplay_PastesInAnother_ThroughTheClipboard(bool fresh)
    {
        string results = Path.Combine(_root, "ws", "results");
        WriteRun(results, "a.npy", 10);
        string? clipboard = null;
        void Wire(DisplayWindowViewModel w)
        {
            w.SetRichCopyAction((containers, _) => { clipboard = PlotExporter.BuildContainersJson(containers); return Task.CompletedTask; });
            w.SetGetClipboardTextAction(() => Task.FromResult(clipboard));
        }

        var from = await WindowOnAsync(results, "a.npy");
        var to   = fresh ? new DisplayWindowViewModel() : await WindowOnAsync(results, "a.npy");
        Wire(from);
        Wire(to);
        var src = from.DataDisplay!.Plots[0];
        src.Inspector.PlotType = PlotType.Rect;
        src.Inspector.AddTraceCommand.Execute(null);
        int before = to.DataDisplay!.Plots.Count;

        await from.CopyCommand.ExecuteAsync(null);
        Assert.NotNull(clipboard);
        Assert.True(to.PasteCommand.CanExecute(null));
        await to.PasteCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, to.DataDisplay.Plots.Count);
        var trace = to.DataDisplay.Plots[^1].PlotVM.Plot.Traces.Single();
        Assert.True(trace.Points.Count > 0);
        Assert.Equal(10, trace.Points[0].Y, 6);
    }

    /// <summary>
    /// The Plot inspector's Use display source: a pasted plot that kept the file it was copied with is pointed at the
    /// pasting display's own selected source, shows that source's data, and the change is one undo step.
    /// </summary>
    [Fact]
    public async Task UseDisplaySource_PointsAPastedPlotAtThisDisplaysSelection_AndUndoes()
    {
        string results = Path.Combine(_root, "ws", "results");
        WriteRun(results, "a.npy", 10);
        WriteRun(results, "b.npy", 20);

        var from = await WindowOnAsync(results, "a.npy");
        var src  = from.DataDisplay!.Plots[0];
        src.Inspector.PlotType = PlotType.Rect;
        src.Inspector.AddTraceCommand.Execute(null);
        string json = PlotExporter.BuildContainersJson(new[] { src });

        var to     = await WindowOnAsync(results, "b.npy");
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(json, DataDisplayViewModel.JsonOpts)!;
        var pasted = (await to.DataDisplay!.PasteFromConfigAsync(config)).Single();
        Assert.True(pasted.Inspector.HasPinnedSource);
        Assert.Equal(10, pasted.PlotVM.Plot.Traces.Single().Points[0].Y, 6);

        pasted.Inspector.UseDisplaySourceCommand.Execute(null);

        var trace = pasted.PlotVM.Plot.Traces.Single();
        Assert.Equal(DataSourceRef.Selected, trace.SourceRef);
        Assert.Equal(20, trace.Points[0].Y, 6);
        Assert.False(pasted.Inspector.HasPinnedSource);

        to.DataDisplay.UndoRedo.Undo();
        Assert.Equal(10, pasted.PlotVM.Plot.Traces.Single().Points[0].Y, 6);
    }
}
