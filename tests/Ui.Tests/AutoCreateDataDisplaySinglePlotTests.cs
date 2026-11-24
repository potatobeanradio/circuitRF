// ================================================================
//  AutoCreateDataDisplaySinglePlotTests.cs
//
//  Regression for: auto-creating a .cdd after a run (R-res-8/9/10,
//  brief-results-storage-and-data-display.md) must leave EXACTLY ONE plot —
//  not the constructor's own seeded empty Smith plot PLUS a second AddPlot().
//
//  WorkspaceViewModel.AutoOpenOrCreateDataDisplayAsync is private on a class that
//  cannot be constructed headlessly, so these tests mirror its plot-seeding logic
//  exactly (the "simulate the production seam" pattern already established in
//  this codebase for WorkspaceViewModel-only logic — see HierarchySaveTests).
// ================================================================

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AutoCreateDataDisplaySinglePlotTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_autocdd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // Mirrors WorkspaceViewModel.AutoOpenOrCreateDataDisplayAsync's body exactly: reuse the
    // constructor's own already-seeded plot container (never call AddPlot), and refresh + select by
    // the RELATIVE logical id (never the absolute path) so the toolbar source-picker combo — which
    // matches AvailableDataSources by LogicalId == SelectedDataSourceRef — is never left blank.
    private static bool SeedDefaultPlot(DataDisplayDocumentViewModel newVm, string npyPath)
    {
        var lib = newVm.Window.DataSourceLibrary;
        lib.RefreshAvailableDataSources();
        lib.SelectDataSourceAsync(Path.GetFileName(npyPath)).GetAwaiter().GetResult();

        bool populated = false;
        if (lib.SelectedEntry is { } entry)
        {
            var plotType = PlotInspectorViewModel.HasPlottableData(entry, allowScalars: false)
                ? PlotType.Rect
                : PlotType.Table;

            var container = newVm.Window.DataDisplay?.Plots.FirstOrDefault();
            if (container is not null)
            {
                if (container.Inspector.PlotType != plotType)
                {
                    container.Inspector.PlotType = plotType;
                    bool square = plotType is PlotType.Smith or PlotType.Polar;
                    container.Width  = square ? 420 : 520;
                    container.Height = square ? 420 : 360;
                }
                if (container.Inspector.AddTraceCommand.CanExecute(null))
                {
                    container.Inspector.AddTraceCommand.Execute(null);
                    populated = container.Inspector.Traces.Count > 0;
                    if (populated) container.Inspector.AddSeedTransmissionTrace();
                }
            }
        }
        return populated;
    }

    private static string WriteSParamNpy(string dir, string fileName)
    {
        var freqAx = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var iAx    = new Axis("i", new[] { 0.0, 1.0 }, "", new[] { "1", "2" });
        var jAx    = new Axis("j", new[] { 0.0, 1.0 }, "", new[] { "1", "2" });

        var flat = new System.Numerics.Complex[3 * 2 * 2];
        for (int f = 0; f < 3; f++)
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            flat[f * 4 + i * 2 + j] = new System.Numerics.Complex(0.1 * (f + 1), 0);

        var sCube  = new DataCube(new[] { freqAx, iAx, jAx }, flat);
        var z0Cube = DataSetBuilder.BuildZ0Cube(new System.Numerics.Complex[] { new(50, 0), new(50, 0) });

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", sCube);
        ds.AddToGroup("SP1", "Z0", z0Cube);

        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static string WriteScalarOnlyNpy(string dir, string fileName)
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "Pdc", DataCube.Scalar(3.14));

        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    // An unswept DC run: V has one axis, but it is the node axis, which is never an X — so the
    // trace resolves to one value per node, and only a Table can show that.
    private static string WriteDcOperatingPointNpy(string dir, string fileName)
    {
        var ds   = new DataSet();
        var node = new Axis("node", new[] { 0.0, 1 }, "V", new[] { "in", "out" });
        ds.AddToGroup("DC1", "V", new DataCube(new[] { node }, new[] { 1.0, 0.508 }));
        ds.AddToGroup("DC1", "Converged", DataCube.Scalar(1.0));
        ds.AddToGroup("DC1", "Residual",  DataCube.Scalar(0.0));

        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    // A fresh DisplayWindowViewModel's initial tab already seeds one empty Smith plot
    // (DataDisplayViewModel's own "starts empty; user authors it" constructor default) — the
    // premise the auto-create bug fix depends on.
    [Fact]
    public void FreshDataDisplay_StartsWithExactlyOneSeededPlot()
    {
        var newVm = new DataDisplayDocumentViewModel();
        Assert.Single(newVm.Window.DataDisplay!.Plots);
        Assert.Equal(PlotType.Smith, newVm.Window.DataDisplay!.Plots[0].Inspector.PlotType);
    }

    [Fact]
    public void SParamRun_AutoCreate_ProducesExactlyOnePlot_RectAndPopulated()
    {
        var dir  = MakeTempDir();
        var path = WriteSParamNpy(dir, "SParamTest.npy");

        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        bool populated = SeedDefaultPlot(newVm, path);

        Assert.True(populated);
        Assert.Single(newVm.Window.DataDisplay!.Plots);   // never two plots
        Assert.Equal(PlotType.Rect, newVm.Window.DataDisplay!.Plots[0].Inspector.PlotType);
        Assert.NotEmpty(newVm.Window.DataDisplay!.Plots[0].Inspector.Traces);
    }

    // A two-port run opens on S(1,1) AND S(2,1), on the one plot: the transmission is what a filter's
    // (or any two-port's) bench is run to see.
    [Fact]
    public void SParamRun_AutoCreate_ShowsS11AndS21()
    {
        var dir  = MakeTempDir();
        var path = WriteSParamNpy(dir, "SParamTest.npy");

        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        Assert.True(SeedDefaultPlot(newVm, path));

        var plot = Assert.Single(newVm.Window.DataDisplay!.Plots);
        var traces = plot.Inspector.Traces.Select(t => t.Trace).ToList();
        Assert.Equal(2, traces.Count);
        (int I, int J) Pins(CircuitRF.Render.DataDisplay.Trace t) => (t.Slice!.Single(a => a.AxisName == "i").Index, t.Slice!.Single(a => a.AxisName == "j").Index);
        Assert.Equal((0, 0), Pins(traces[0]));
        Assert.Equal((1, 0), Pins(traces[1]));
        Assert.Equal(traces[0].Transform, traces[1].Transform);
        Assert.Equal("SP1.S", traces[1].CubeName);
        Assert.True(traces[1].Points.Count > 0);
        // A second call adds nothing: only a lone seeded S(1,1) takes it.
        Assert.False(plot.Inspector.AddSeedTransmissionTrace());
    }

    // Regression: the toolbar source-picker combo (bound TwoWay to SelectedDataSourceItem, which
    // matches AvailableDataSources by LogicalId == SelectedDataSourceRef) must show the auto-created
    // display's own source — not blank — even though traces resolve/render via SourcePath alone and
    // never depend on the combo at all.
    [Fact]
    public void SParamRun_AutoCreate_SourcePickerComboIsNotBlank()
    {
        var dir  = MakeTempDir();
        var path = WriteSParamNpy(dir, "SParamTest.npy");

        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        SeedDefaultPlot(newVm, path);

        var selected = newVm.Window.SelectedDataSourceItem;
        Assert.NotNull(selected);
        Assert.Equal("SParamTest.npy", selected!.LogicalId);
        Assert.Equal(path, selected.AbsolutePath);
    }

    [Fact]
    public void ScalarOnlyRun_AutoCreate_ProducesExactlyOnePlot_Table()
    {
        var dir  = MakeTempDir();
        var path = WriteScalarOnlyNpy(dir, "DcTest.npy");

        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        SeedDefaultPlot(newVm, path);

        Assert.Single(newVm.Window.DataDisplay!.Plots);   // never two plots
        Assert.Equal(PlotType.Table, newVm.Window.DataDisplay!.Plots[0].Inspector.PlotType);
    }

    [Fact]
    public void DcOperatingPointRun_AutoCreate_IsTable_AndTraceIsValid()
    {
        var dir  = MakeTempDir();
        var path = WriteDcOperatingPointNpy(dir, "DcOp.npy");

        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        bool populated = SeedDefaultPlot(newVm, path);

        var plot = newVm.Window.DataDisplay!.Plots[0];
        Assert.True(populated);
        Assert.Equal(PlotType.Table, plot.Inspector.PlotType);
        var trace = plot.Inspector.Traces.Single().Trace;
        Assert.False(trace.ScalarOnNonTableInvalid);
        Assert.Null(trace.InvalidSpecText);
    }
}
