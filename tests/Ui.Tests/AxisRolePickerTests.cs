// ================================================================
//  AxisRolePickerTests.cs  —  Phase 7.3a gate tests
//
//  Verifies the axis-role assignment picker:
//  rank ≥3 cubes offered (one item per cube), name-matched N-D
//  slice resolution, auto-flip invariant, .cdd round-trip,
//  and the Real-cube-disabled-on-Smith gate.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AxisRolePickerTests
{
    // ---- Helpers -----------------------------------------------------------

    /// <summary>
    /// Rank-3 Real cube "V" with axes [node(nodeCount), harmonic(harmCount), Pin(pinCount)].
    /// Value at (n, h, p) = n*100 + h*10 + p.
    /// </summary>
    private static DataSet BuildRank3RealCube(
        int nodeCount = 2, int harmonicCount = 3, int pinCount = 5)
    {
        var axNode = new Axis("node",
            Enumerable.Range(0, nodeCount).Select(i => (double)i).ToArray(), "");
        var axHarm = new Axis("harmonic",
            Enumerable.Range(0, harmonicCount).Select(i => (double)i).ToArray(), "");
        var axPin  = new Axis("Pin",
            Enumerable.Range(0, pinCount).Select(i => (double)i).ToArray(), "");

        int total = nodeCount * harmonicCount * pinCount;
        var data  = new double[total];
        for (int n = 0; n < nodeCount; n++)
        for (int h = 0; h < harmonicCount; h++)
        for (int p = 0; p < pinCount; p++)
            data[n * harmonicCount * pinCount + h * pinCount + p] = n * 100 + h * 10 + p;

        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { axNode, axHarm, axPin }, data));
        return ds;
    }

    private static async System.Threading.Tasks.Task<(string path, DataSourceLibraryViewModel lib)>
        ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_7.3a_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        return (path, lib);
    }

    private static Trace MakeCubeTrace(string sourcePath, string cubeName, AxisSlice[] slice)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = slice;
        trace.Transform  = CubeTransform.None;
        return trace;
    }

    // ---- Test 1: Rank3_PinTwo_KeepOne --------------------------------------
    // Rank-3 cube: pin node + harmonic, keep Pin as X.
    // Resolver must yield a rank-1 cube over Pin; values match cube[1,2,..].

    [Fact]
    public async System.Threading.Tasks.Task Rank3_PinTwo_KeepOne()
    {
        var ds = BuildRank3RealCube();
        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var slice = new[]
            {
                new AxisSlice("node",     AxisRole.PinToIndex, 1),
                new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
                new AxisSlice("Pin",      AxisRole.KeepAsX,   0),
            };
            var trace = MakeCubeTrace(path, "V", slice);
            var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();

            // Pin axis has 5 elements.
            Assert.Equal(5, trace.Points.Count);

            // Y[i] = node*100 + harm*10 + pin = 1*100 + 2*10 + i = 120+i
            for (int i = 0; i < 5; i++)
                Assert.Equal(120.0 + i, (double)trace.Points[i].Y, 5);

            // X[i] = Pin axis values = 0,1,2,3,4
            for (int i = 0; i < 5; i++)
                Assert.Equal((float)i, trace.Points[i].X);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Test 2: SwitchX_Recomputes ----------------------------------------
    // Flip X from Pin to harmonic via AxisRoles — point count follows new X axis.

    [Fact]
    public async System.Threading.Tasks.Task SwitchX_Recomputes()
    {
        var ds = BuildRank3RealCube();
        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Start: Pin=X (5 points), node=pinned(1), harmonic=pinned(2).
            var slice = new[]
            {
                new AxisSlice("node",     AxisRole.PinToIndex, 1),
                new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
                new AxisSlice("Pin",      AxisRole.KeepAsX,   0),
            };
            var trace = MakeCubeTrace(path, "V", slice);
            var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();
            Assert.Equal(5, trace.Points.Count);

            // AxisRoles built from cube.Axes order: [node, harmonic, Pin]
            var row = inspector.Traces[0];
            Assert.Equal(3, row.AxisRoles.Count);

            // Set harmonic (index 1) as X → auto-flips Pin back to Pinned.
            row.AxisRoles[1].IsX = true;  // triggers FlushSliceAndRebuild → RebuildAndNotify

            // Now harmonic is X (3 elements), node=pinned(1), Pin=pinned(0).
            Assert.Equal(3, trace.Points.Count);

            // Y[h] = node=1, harm=h, pin=0 → 1*100 + h*10 + 0 = 100 + h*10
            for (int h = 0; h < 3; h++)
                Assert.Equal(100.0 + h * 10.0, (double)trace.Points[h].Y, 5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Test 3: AxisMatchByName -------------------------------------------
    // Slice entries in different order than cube.Axes → name-keyed resolution.

    [Fact]
    public async System.Threading.Tasks.Task AxisMatchByName()
    {
        var ds = BuildRank3RealCube();
        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Reversed order vs cube.Axes ([node, harmonic, Pin]).
            var slice = new[]
            {
                new AxisSlice("Pin",      AxisRole.KeepAsX,   0),   // last in cube, first here
                new AxisSlice("harmonic", AxisRole.PinToIndex, 2),  // second in cube, second here
                new AxisSlice("node",     AxisRole.PinToIndex, 1),  // first in cube, last here
            };
            var trace = MakeCubeTrace(path, "V", slice);
            var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();

            // Same result as Test 1: Pin is X → 5 points, Y = 120+i.
            Assert.Equal(5, trace.Points.Count);
            for (int i = 0; i < 5; i++)
                Assert.Equal(120.0 + i, (double)trace.Points[i].Y, 5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Test 4: Rank3_Roundtrips_Cdd -------------------------------------
    // Serialize + restore a rank-3 Slice via TraceConfig; restored curve matches.

    [Fact]
    public async System.Threading.Tasks.Task Rank3_Roundtrips_Cdd()
    {
        var ds = BuildRank3RealCube();
        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var slice = new[]
            {
                new AxisSlice("node",     AxisRole.PinToIndex, 1),
                new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
                new AxisSlice("Pin",      AxisRole.KeepAsX,   0),
            };
            var trace = MakeCubeTrace(path, "V", slice);

            // ---- Serialize ----
            var tc = DataDisplayViewModel.BuildTraceConfig(trace, configDir: "");

            Assert.Equal("V", tc.CubeName);
            Assert.Equal(3,   tc.CubeSlice.Count);

            var nodeEntry = tc.CubeSlice.First(s => s.AxisName == "node");
            Assert.Equal(AxisRole.PinToIndex, nodeEntry.Role);
            Assert.Equal(1,                   nodeEntry.Index);

            var harmEntry = tc.CubeSlice.First(s => s.AxisName == "harmonic");
            Assert.Equal(AxisRole.PinToIndex, harmEntry.Role);
            Assert.Equal(2,                   harmEntry.Index);

            var pinEntry = tc.CubeSlice.First(s => s.AxisName == "Pin");
            Assert.Equal(AxisRole.KeepAsX, pinEntry.Role);
            Assert.Equal(0,                pinEntry.Index);

            // ---- Restore ----
            var restoredSlice = tc.CubeSlice
                .Select(s => new AxisSlice(s.AxisName, s.Role, s.Index))
                .ToArray();
            var restored  = MakeCubeTrace(path, tc.CubeName!, restoredSlice);
            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(restored);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();

            Assert.Equal(5, restored.Points.Count);
            for (int i = 0; i < 5; i++)
                Assert.Equal(120.0 + i, (double)restored.Points[i].Y, 5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Test 5: RealCube_SmithDisabled ------------------------------------
    // Real rank-3 cube is offered but disabled (not selectable) on a Smith plot.

    [Fact]
    public async System.Threading.Tasks.Task RealCube_SmithDisabled()
    {
        var ds = BuildRank3RealCube();   // Real DataCube
        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var snp  = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);

            var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row       = inspector.Traces[0];

            // Phase 7.3a: rank-3 IS offered (one item per cube).
            var item = row.AvailableSignals.FirstOrDefault(s => s.IsCubeBound && s.CubeName == "V");
            Assert.NotNull(item);

            // Real cube must be disabled on a Smith (complex) plot.
            Assert.False(item!.IsEnabled,
                "Real cube should be disabled on a Smith plot");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
