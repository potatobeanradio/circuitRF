using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>Issue 66 §4: a CLI run verb's <c>-o</c> writes one analysis's cubes ungrouped, Simulate under the analysis's
/// name. A display reading a flat file binds <c>ENV1.Pavg</c> to its bare <c>Pavg</c>; a file that has analysis groups of
/// its own never does.</summary>
public sealed class FlatCliResultBindingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-flatcli-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static DataCube Scalar(double v) => DataCube.Scalar(v);

    [Fact]
    public async Task AFlatCliResult_AnswersAnAnalysisQualifiedSpec_InTheLibraryAndThePlotLoader()
    {
        string flat = Path.Combine(_dir, "cli.npy");
        var ds = new DataSet();
        ds.Add("Pavg", Scalar(1.92));
        DataSetExporter.Export(ds, flat, ExportFormat.Npy);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(flat);
        var data = Assert.Single(lib.Entries).Data!;
        Assert.Equal(1.92, data["ENV1.Pavg"].RealValues[0]);
        Assert.Equal(1.92, data["Pavg"].RealValues[0]);

        var (loaded, _, error, _) = PlotSourceFile.LoadWithNotes(flat);
        Assert.Null(error);
        Assert.Equal(1.92, loaded!["ENV1.Pavg"].RealValues[0]);
    }

    [Fact]
    public async Task AGroupedResult_NeverBindsAnotherAnalysissName()
    {
        string grouped = Path.Combine(_dir, "simulate.npy");
        var ds = new DataSet();
        ds.AddToGroup("ENV1", "Pavg", Scalar(1.92));
        DataSetExporter.Export(ds, grouped, ExportFormat.Npy);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(grouped);
        var data = Assert.Single(lib.Entries).Data!;
        Assert.Equal(1.92, data["ENV1.Pavg"].RealValues[0]);
        Assert.False(data.Contains("ENV2.Pavg"));
    }

    [Fact]
    public void ADataSetNoDisplayLoaded_KeepsTheStrictRule()
        => Assert.False(new DataSet { }.With("Pavg", Scalar(1)).Contains("ENV1.Pavg"));
}

file static class DataSetTestExtensions
{
    public static DataSet With(this DataSet ds, string name, DataCube c) { ds.Add(name, c); return ds; }
}
