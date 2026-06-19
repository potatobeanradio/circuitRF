// ================================================================
//  DataDisplayRerunRefreshTests.cs  —  brief-datadisplay-rerun-refresh
//
//  Verifies that cube-bound traces auto-refresh after a re-run:
//   T1  same-shape re-run (point count change) — preserves user's slice,
//       updates Points.
//   T2  new sweep axis added — reseeds slice (new axis pinned-at-0),
//       exactly one X axis, Points are not cleared.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayRerunRefreshTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeRealCube(string ax0Name, int ax0Len, string ax1Name, int ax1Len)
    {
        var ax0 = new Axis(ax0Name, Linspace(-4, 0, ax0Len), "V");
        var ax1 = new Axis(ax1Name, Linspace(0, ax1Len - 1, ax1Len), "");
        var data = new double[ax0Len * ax1Len];
        for (int i = 0; i < data.Length; i++) data[i] = i * 0.01;
        return new DataCube(new[] { ax0, ax1 }, data);
    }

    private static DataCube MakeRealCube3(string ax0Name, int ax0Len,
                                          string ax1Name, int ax1Len,
                                          string ax2Name, int ax2Len)
    {
        var ax0 = new Axis(ax0Name, Linspace(-4, 0,          ax0Len), "V");
        var ax1 = new Axis(ax1Name, Linspace(0,  ax1Len - 1, ax1Len), "V");
        var ax2 = new Axis(ax2Name, Linspace(0,  ax2Len - 1, ax2Len), "");
        var data = new double[ax0Len * ax1Len * ax2Len];
        for (int i = 0; i < data.Length; i++) data[i] = i * 0.001;
        return new DataCube(new[] { ax0, ax1, ax2 }, data);
    }

    private static double[] Linspace(double start, double end, int n)
    {
        if (n == 1) return [start];
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = start + i * (end - start) / (n - 1);
        return r;
    }

    private static void SaveDs(DataSet ds, string path)
    {
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
    }

    private static (Trace trace, Plot plot) MakeCubeTrace(string filePath, string cubeName,
                                                           AxisSlice[] slice)
    {
        var snp   = new SNP(new double[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = filePath;
        trace.CubeName   = cubeName;
        trace.Slice      = slice;
        trace.Expression = trace.BuildPickerExpression();

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        return (trace, plot);
    }

    // ── T1: same-shape re-run preserves slice, updates point count ────────────

    [Fact]
    public async Task SameShapeRerun_PreservesSlice_UpdatesPoints()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"crf_rrr1_{Guid.NewGuid():N}.npy");
        try
        {
            // Initial: [Vgs(41), node(3)]
            var ds1 = new DataSet();
            ds1.Add("Ids", MakeRealCube("Vgs", 41, "node", 3));
            SaveDs(ds1, tmpPath);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);
            var entry = lib.Entries.Single();

            var initialSlice = new[]
            {
                new AxisSlice("Vgs",  AxisRole.KeepAsX,    0),
                new AxisSlice("node", AxisRole.PinToIndex, 0),
            };
            var (trace, plot) = MakeCubeTrace(tmpPath, "Ids", initialSlice);
            var inspector     = new PlotInspectorViewModel(plot, () => { }, lib);

            // Populate initial points.
            PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.Equal(41, trace.Points.Count);

            // Re-run: same axes, 81 points.
            var ds2 = new DataSet();
            ds2.Add("Ids", MakeRealCube("Vgs", 81, "node", 3));
            SaveDs(ds2, tmpPath);

            await lib.ReloadChangedAsync([tmpPath]);

            // Slice must be unchanged (same axis-name set → no reseed).
            Assert.NotNull(trace.Slice);
            Assert.Equal(2, trace.Slice!.Length);
            Assert.Contains(trace.Slice, s => s.AxisName == "Vgs"  && s.Role == AxisRole.KeepAsX);
            Assert.Contains(trace.Slice, s => s.AxisName == "node" && s.Role == AxisRole.PinToIndex);

            // Points must reflect the new 81-point sampling.
            Assert.Equal(81, trace.Points.Count);
        }
        finally { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
    }

    // ── T2: new axis added → reseed + new axis pinned, still one X, no clear ──

    [Fact]
    public async Task NewAxisAdded_ReseededWithNewAxisPinned_OneXRemains()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"crf_rrr2_{Guid.NewGuid():N}.npy");
        try
        {
            // Initial: [Vgs(41), node(3)]
            var ds1 = new DataSet();
            ds1.Add("Ids", MakeRealCube("Vgs", 41, "node", 3));
            SaveDs(ds1, tmpPath);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);
            var entry = lib.Entries.Single();

            var initialSlice = new[]
            {
                new AxisSlice("Vgs",  AxisRole.KeepAsX,    0),
                new AxisSlice("node", AxisRole.PinToIndex, 0),
            };
            var (trace, plot) = MakeCubeTrace(tmpPath, "Ids", initialSlice);
            var inspector     = new PlotInspectorViewModel(plot, () => { }, lib);

            // Populate initial points.
            PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.Equal(41, trace.Points.Count);

            // Re-run with a new Vds sweep axis: [Vgs(41), Vds(7), node(3)]
            var ds3 = new DataSet();
            ds3.Add("Ids", MakeRealCube3("Vgs", 41, "Vds", 7, "node", 3));
            SaveDs(ds3, tmpPath);

            await lib.ReloadChangedAsync([tmpPath]);

            // Slice must now have 3 entries.
            Assert.NotNull(trace.Slice);
            Assert.Equal(3, trace.Slice!.Length);

            // Exactly one X axis.
            Assert.Equal(1, trace.Slice.Count(s => s.Role == AxisRole.KeepAsX));

            // Vgs remains X (it was X before the reseed).
            Assert.Contains(trace.Slice, s => s.AxisName == "Vgs" && s.Role == AxisRole.KeepAsX);

            // Vds appears (new) and is pinned-at-0.
            Assert.Contains(trace.Slice, s => s.AxisName == "Vds" && s.Role == AxisRole.PinToIndex && s.Index == 0);

            // node still pinned.
            Assert.Contains(trace.Slice, s => s.AxisName == "node" && s.Role == AxisRole.PinToIndex);

            // Points must not be cleared (TrySetCubeData extracted a 1-D Vgs curve).
            Assert.NotEmpty(trace.Points);
        }
        finally { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
    }
}
