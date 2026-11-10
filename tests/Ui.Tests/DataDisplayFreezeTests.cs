// ================================================================
//  DataDisplayFreezeTests.cs — Freeze keeps the runs a re-run replaces
//  (up to five, each older one fainter) as ghosts of every trace, and
//  turning it off lets them go (DataSourceLibraryViewModel.Freeze.cs).
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

public sealed class DataDisplayFreezeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_freeze_{Guid.NewGuid():N}");

    public DataDisplayFreezeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteRun(double gain)
    {
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var ds   = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", new DataCube(new[] { freq }, new[] { gain, gain }));
        string path = Path.Combine(_dir, "amp.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    [Fact]
    public async Task EachReRunWhileFrozen_LeavesTheRunBeforeAsAFainterGhost_UpToFive()
    {
        string path = WriteRun(1);
        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await w.DataSourceLibrary.SelectDataSourceAsync("amp.npy");
        var c = w.DataDisplay!.Plots[0];
        c.Inspector.PlotType = PlotType.Rect;
        c.Inspector.AddTraceCommand.Execute(null);
        var plot = c.PlotVM.Plot;

        w.ToggleFreezeCommand.Execute(null);
        for (int run = 2; run <= 8; run++)
        {
            WriteRun(run);
            await w.DataSourceLibrary.ReloadChangedAsync(new[] { path });
        }

        Assert.Equal(DataSourceLibraryViewModel.MaxFrozenRuns, plot.GhostTraces.Count);
        // Oldest first and faintest: runs 3…7 under the live run 8.
        Assert.Equal(new[] { 3.0, 4, 5, 6, 7 }, plot.GhostTraces.Select(g => (double)g.Points[0].Y));
        var opacities = plot.GhostTraces.Select(g => g.Properties.LineOpacity).ToList();
        Assert.True(opacities.Zip(opacities.Skip(1)).All(p => p.First < p.Second));
        Assert.Equal(8, plot.Traces[0].Points[0].Y);
        Assert.Equal("5 earlier runs", w.FrozenRunsText);

        w.ToggleFreezeCommand.Execute(null);
        Assert.Empty(plot.GhostTraces);
    }

    [Fact]
    public async Task AReRunWhileNotFrozen_KeepsNothing()
    {
        string path = WriteRun(1);
        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await w.DataSourceLibrary.SelectDataSourceAsync("amp.npy");
        var c = w.DataDisplay!.Plots[0];
        c.Inspector.PlotType = PlotType.Rect;
        c.Inspector.AddTraceCommand.Execute(null);

        WriteRun(2);
        await w.DataSourceLibrary.ReloadChangedAsync(new[] { path });

        Assert.Empty(c.PlotVM.Plot.GhostTraces);
    }
}
