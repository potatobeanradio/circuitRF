// A Transient preset added to a display reads the display's SELECTED source, as every trace Add Trace makes does: switching
// the source moves its waveforms to the other run. Pinned to the file it was added on, a display saved under another
// schematic's name (or copied with its cell) went on drawing the old run whatever the combo said.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.Transient;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TransientPresetFollowsSourceTests : IDisposable
{
    private readonly string _results = Directory.CreateTempSubdirectory("tran-follow-").FullName;
    public void Dispose() { try { Directory.Delete(_results, recursive: true); } catch { } }

    /// <summary>A 50 Hz sine of <paramref name="amplitude"/> on net "Vin" over two periods, shaped as a transient run writes it.</summary>
    private void WriteRun(string name, double amplitude)
    {
        const int n = 401;
        var t = Enumerable.Range(0, n).Select(i => i * 0.04 / (n - 1)).ToArray();
        var v = t.Select(x => amplitude * Math.Sin(2 * Math.PI * 50 * x)).ToArray();
        var ds = new DataSet();
        ds.AddToGroup("TRAN1", "V", new DataCube([new Axis("time", t, "s"), new Axis("node", [0], "", ["Vin"])], v));
        ds.AddToGroup("TRAN1", "Tran.AcceptedSteps", new DataCube([], [n]));
        ds.AddToGroup("TRAN1", "__TranFundamental_Hz", new DataCube([], [50.0]));
        DataSetExporter.Export(ds, Path.Combine(_results, name), ExportFormat.Npy);
    }

    [Fact]
    public async Task TransientPreset_FollowsTheSelectedSource()
    {
        WriteRun("rectifier.npy", 1.0);
        WriteRun("rectifier2.npy", 2.0);

        var window = new DisplayWindowViewModel();
        var lib = window.DataSourceLibrary;
        lib.ResultsRootProvider = () => _results;
        lib.RefreshAvailableDataSources();
        await lib.SelectDataSourceAsync("rectifier.npy");
        var display = window.DataDisplay!;
        Assert.NotEmpty(await display.AddTransientAsync());

        var wave = display.Plots.Single(p => p.Inspector.Traces.Any(r => r.Trace.Label == "Vin")).PlotVM.Plot.Traces.Single();
        Assert.Equal(1.0, wave.Points.Max(p => p.Y), 2);

        await lib.SelectDataSourceAsync("rectifier2.npy");
        Assert.Equal(2.0, wave.Points.Max(p => p.Y), 2);
    }
}
