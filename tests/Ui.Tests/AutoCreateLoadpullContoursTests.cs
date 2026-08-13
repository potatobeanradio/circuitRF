// ================================================================
//  AutoCreateLoadpullContoursTests.cs
//  Gate tests for brief-dd-loadpull-contour-ux-round8 §4 — the auto-created Data Display for a
//  loadpull run gets two contour plots (Pout dBm left, Efficiency right, both at 3 dB compression)
//  instead of the single arbitrary-cube trace AutoOpenOrCreateDataDisplayAsync otherwise seeds.
//
//  WorkspaceViewModel.AutoOpenOrCreateDataDisplayAsync itself is private on a class that cannot be
//  constructed headlessly, but WorkspaceViewModel.PopulateLoadpullContourPlots — the helper this
//  brief added — is `internal static` with no instance state, so these tests call the REAL
//  production method directly (via InternalsVisibleTo) rather than mirroring its logic.
// ================================================================

using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AutoCreateLoadpullContoursTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_autolp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ---- Synthetic loadpull DataSet builder (matches LoadpullEngine.BuildLoadpullDataSet's shape,
    //      post LoadpullPostProcessor naming: Pout_dBm / Efficiency, not the raw Pout / DE). ----

    private static DataSet BuildLoadpullDataSet(
        Complex[] gammaPoints, bool includePout = true, bool includeEfficiency = true, string? group = null)
    {
        int nG = gammaPoints.Length;
        var gridAxis = new Axis("gridPoint", Enumerable.Range(0, nG).Select(i => (double)i).ToArray());
        var pinAxis  = new Axis("pinStep",   new[] { 0.0 });

        var zLoad = gammaPoints.Select(g => 50.0 * (1 + g) / (1 - g)).ToArray();
        var pout  = new double[nG];
        var eff   = new double[nG];
        for (int i = 0; i < nG; i++) { pout[i] = 10.0 + i; eff[i] = 30.0 + i; }

        var ds = new DataSet();
        void AddCube(string name, DataCube cube)
        {
            if (group is null) ds.Add(name, cube);
            else                ds.AddToGroup(group, name, cube);
        }

        AddCube("GammaLoad", new DataCube(new[] { gridAxis }, gammaPoints));
        AddCube("ZLoad",     new DataCube(new[] { gridAxis }, zLoad));
        if (includePout)       AddCube("Pout_dBm",   new DataCube(new[] { gridAxis, pinAxis }, pout));
        if (includeEfficiency) AddCube("Efficiency", new DataCube(new[] { gridAxis, pinAxis }, eff));
        return ds;
    }

    private static readonly Complex[] GammaGridHighVswr =
    {
        new(0.0, 0.0), new(0.5, 0.0), new(0.9, 0.0), new(0.0, 0.6),
    };   // max VSWR = 19 — mirrors the real .spl Γ-grid measurement (§4a)

    private static readonly Complex[] GammaGridLowVswr =
    {
        new(0.0, 0.0), new(0.026, 0.0), new(0.0, 0.026), new(-0.02, 0.015),
    };   // max VSWR ≈ 2.6 — mirrors the real RLSweep impedance-grid measurement (§4a)

    private static string WriteNpy(string dir, string fileName, DataSet ds)
    {
        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static DataDisplayDocumentViewModel SeedAndPopulate(string dir, string path, out bool populated)
    {
        var newVm = new DataDisplayDocumentViewModel();
        newVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;

        var lib = newVm.Window.DataSourceLibrary;
        lib.RefreshAvailableDataSources();
        lib.SelectDataSourceAsync(Path.GetFileName(path)).GetAwaiter().GetResult();

        var ds = lib.SelectedEntry!.Data!;
        populated = WorkspaceViewModel.PopulateLoadpullContourPlots(newVm, ds);
        return newVm;
    }

    // §4/§4a: a Γ-grid loadpull auto-creates two Smith contour plots, Pout left / Efficiency right,
    // both at 3 dB compression, non-overlapping.
    [Fact]
    public void GammaGridLoadpull_CreatesTwoSmithContourPlots()
    {
        var dir  = MakeTempDir();
        var ds   = BuildLoadpullDataSet(GammaGridHighVswr);
        var path = WriteNpy(dir, "LpGamma.npy", ds);

        var newVm = SeedAndPopulate(dir, path, out bool populated);

        Assert.True(populated);
        var plots = newVm.Window.DataDisplay!.Plots;
        Assert.Equal(2, plots.Count);
        Assert.All(plots, p => Assert.Equal(PlotType.Smith, p.Inspector.PlotType));

        var left  = plots[0];
        var right = plots[1];
        Assert.Single(left.Inspector.Traces);
        Assert.Single(right.Inspector.Traces);
        Assert.Equal("Pout_dBm",   left.Inspector.Traces[0].ContourMetricName);
        Assert.Equal("Efficiency", right.Inspector.Traces[0].ContourMetricName);
        foreach (var row in new[] { left.Inspector.Traces[0], right.Inspector.Traces[0] })
        {
            Assert.Equal(ConstraintKind.Compression, row.ContourConstraintKind);
            Assert.Equal(3.0, row.ContourConstraintValue);
        }

        // Explicit, non-overlapping positions with a real gap — never the same slot.
        Assert.True(right.Left > left.Left + left.Width, "right plot must not overlap the left plot");
        Assert.Equal(left.Top, right.Top);
    }

    // §4a: an impedance-grid loadpull produces the same two plots as Rect.
    [Fact]
    public void ImpedanceGridLoadpull_CreatesTwoRectContourPlots()
    {
        var dir  = MakeTempDir();
        var ds   = BuildLoadpullDataSet(GammaGridLowVswr);
        var path = WriteNpy(dir, "LpZ.npy", ds);

        var newVm = SeedAndPopulate(dir, path, out bool populated);

        Assert.True(populated);
        var plots = newVm.Window.DataDisplay!.Plots;
        Assert.Equal(2, plots.Count);
        Assert.All(plots, p => Assert.Equal(PlotType.Rect, p.Inspector.PlotType));
        Assert.Equal("Pout_dBm",   plots[0].Inspector.Traces[0].ContourMetricName);
        Assert.Equal("Efficiency", plots[1].Inspector.Traces[0].ContourMetricName);
    }

    // §4: a Loadpull-Pursuit-shaped run (cubes nested under an analysis group) produces the same
    // two-plot result as a flat/top-level loadpull.
    [Fact]
    public void GroupedLoadpullPursuitRun_CreatesTwoContourPlots()
    {
        var dir  = MakeTempDir();
        var ds   = BuildLoadpullDataSet(GammaGridHighVswr, group: "LPP1");
        var path = WriteNpy(dir, "LppRun.npy", ds);

        var newVm = SeedAndPopulate(dir, path, out bool populated);

        Assert.True(populated);
        Assert.Equal(2, newVm.Window.DataDisplay!.Plots.Count);
    }

    // §4: a metric whose cube is absent is skipped rather than producing an empty plot for it —
    // exactly one plot when only Pout_dBm exists.
    [Fact]
    public void MissingEfficiencyCube_ProducesOnlyOnePlot()
    {
        var dir  = MakeTempDir();
        var ds   = BuildLoadpullDataSet(GammaGridHighVswr, includeEfficiency: false);
        var path = WriteNpy(dir, "LpNoEff.npy", ds);

        var newVm = SeedAndPopulate(dir, path, out bool populated);

        Assert.True(populated);
        var plots = newVm.Window.DataDisplay!.Plots;
        Assert.Single(plots);
        Assert.Equal("Pout_dBm", plots[0].Inspector.Traces[0].ContourMetricName);
    }

    // §4: an S-parameter (non-loadpull) run is unaffected by this brief — exactly one plot, as
    // AutoCreateDataDisplaySinglePlotTests already pins for the non-loadpull path.
    [Fact]
    public void NonLoadpullRun_NeverRoutedToLoadpullPath()
    {
        var freqAx = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var iAx    = new Axis("i", new[] { 0.0 }, "", new[] { "1" });
        var jAx    = new Axis("j", new[] { 0.0 }, "", new[] { "1" });
        var sCube  = new DataCube(new[] { freqAx, iAx, jAx }, new[] { new Complex(0.1, 0), new Complex(0.2, 0) });
        var z0Cube = DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0) });

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", sCube);
        ds.AddToGroup("SP1", "Z0", z0Cube);

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    // §4: the .cdd saves and reloads to the same picture (2 plots, correct types/metrics).
    [Fact]
    public async Task GammaGridLoadpull_SaveAndReload_PreservesTwoContourPlots()
    {
        var dir  = MakeTempDir();
        var ds   = BuildLoadpullDataSet(GammaGridHighVswr);
        var path = WriteNpy(dir, "LpRoundTrip.npy", ds);

        var newVm = SeedAndPopulate(dir, path, out bool populated);
        Assert.True(populated);

        var cddPath = Path.Combine(dir, "LpRoundTrip.cdd");
        await newVm.Window.SaveAllAsync(cddPath, 0, 0, 0, 0);

        var reloadedVm = new DataDisplayDocumentViewModel();
        reloadedVm.Window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await reloadedVm.Window.LoadAllAsync(cddPath);

        var plots = reloadedVm.Window.DataDisplay!.Plots;
        Assert.Equal(2, plots.Count);
        Assert.All(plots, p => Assert.Equal(PlotType.Smith, p.Inspector.PlotType));
        Assert.Equal("Pout_dBm",   plots[0].Inspector.Traces[0].ContourMetricName);
        Assert.Equal("Efficiency", plots[1].Inspector.Traces[0].ContourMetricName);
    }
}
