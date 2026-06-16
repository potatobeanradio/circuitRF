// ================================================================
//  CubeTraceTests.cs  —  Phase 7.2c-a gate tests
//
//  Verifies that cube-bound traces build points correctly,
//  rank≥3 cubes are not offered in the signal picker,
//  and the .cdd round-trip for cube identity fields is intact.
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

public sealed class CubeTraceTests
{
    // ---- Test 1: Real rank-1 cube builds correct Rect points ---------------

    [Fact]
    public void CubeTrace_Rect_BuildsPoints()
    {
        var snp  = new SNP(new double[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);

        trace.CubeName = "PAE";
        trace.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        trace.Transform = CubeTransform.None;

        double[] xVals = { 0, 1, 2, 3, 4 };
        double[] yVals = { 0.1, 0.2, 0.3, 0.4, 0.5 };

        trace.SetCubeData(xVals, complexValues: null, yVals,
                          "Pin", null, PlotType.Rect, FreqUnit.GHz);

        // One point per X value; X matches the axis values.
        Assert.Equal(5, trace.Points.Count);
        for (int i = 0; i < xVals.Length; i++)
            Assert.Equal((float)xVals[i], trace.Points[i].X);

        // Y with None on Real cube = the raw value.
        for (int i = 0; i < yVals.Length; i++)
            Assert.Equal((double)yVals[i], trace.Points[i].Y, 5);
    }

    // ---- Test 2: Complex rank-2 cube with dB20 transform -------------------

    [Fact]
    public void CubeTrace_Complex_dB20()
    {
        var snp   = new SNP(new double[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);

        trace.CubeName  = "V";
        trace.Slice     = new[]
        {
            new AxisSlice("node", AxisRole.PinToIndex, 0),
            new AxisSlice("Pin",  AxisRole.KeepAsX,   0),
        };
        trace.Transform = CubeTransform.dB20;

        double[] xVals  = { 0, 1, 2 };
        Complex[] cVals =
        {
            new Complex(1.0,  0.0),    // |z| = 1  → dB20 = 0
            new Complex(10.0, 0.0),    // |z| = 10 → dB20 = 20
            new Complex(0.0,  1.0),    // |z| = 1  → dB20 = 0
        };

        trace.SetCubeData(xVals, cVals, realValues: null,
                          "Pin", null, PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(3, trace.Points.Count);
        Assert.Equal(0.0,  (double)trace.Points[0].Y, 3);   // 20·log10(1) = 0
        Assert.Equal(20.0, (double)trace.Points[1].Y, 3);   // 20·log10(10) = 20
        Assert.Equal(0.0,  (double)trace.Points[2].Y, 3);   // 20·log10(1) = 0
    }

    // ---- Test 3: Rank ≥ 3 cube is not offered in the signal picker ---------

    [Fact]
    public async Task CubeTrace_RankGE3_NotOffered()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"crf_rank3_{Guid.NewGuid():N}.npy");
        try
        {
            // Build a rank-3 complex DataSet.
            var ax0  = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
            var ax1  = new Axis("node", new[] { 0.0, 1.0 }, "");
            var ax2  = new Axis("port", new[] { 0.0, 1.0 }, "");
            var data = Enumerable.Repeat(new Complex(1.0, 0.5), 8).ToArray();
            var ds   = new DataSet();
            ds.Add("V", new DataCube(new[] { ax0, ax1, ax2 }, data));
            DataSetExporter.Export(ds, tmpPath, ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);

            // One dummy network-bound trace so the inspector has a TraceRowViewModel.
            var snp  = new SNP(new[] { 1e9 }, 2);
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row       = inspector.Traces[0];

            // No cube-bound signal for the rank-3 "V" cube must be offered.
            Assert.DoesNotContain(row.AvailableSignals,
                s => s.IsCubeBound && s.CubeName == "V");
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    // ---- Test 4: .cdd round-trip preserves CubeName/Slice/Transform --------

    [Fact]
    public void CubeTrace_Roundtrips_Cdd()
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = "/some/results.npy";
        trace.CubeName   = "PAE";
        trace.Slice      = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        trace.Transform  = CubeTransform.dB20;

        // Serialize.
        var tc = DataDisplayViewModel.BuildTraceConfig(trace, configDir: "");

        Assert.Equal("PAE",              tc.CubeName);
        Assert.Equal(CubeTransform.dB20, tc.CubeTransform);
        Assert.Single(tc.CubeSlice);
        Assert.Equal("Pin",              tc.CubeSlice[0].AxisName);
        Assert.Equal(AxisRole.KeepAsX,   tc.CubeSlice[0].Role);
        Assert.Equal(0,                  tc.CubeSlice[0].Index);

        // A network-bound trace must produce null CubeName and empty CubeSlice.
        var nbSnp   = new SNP(new[] { 1e9, 2e9 }, 2);
        var nbTrace = new Trace(nbSnp, MatrixType.S, 0, 1, DependentVarFormat.Db);
        nbTrace.SourcePath = "/some/file.s2p";

        var nbConfig = DataDisplayViewModel.BuildTraceConfig(nbTrace, configDir: "");

        Assert.Null(nbConfig.CubeName);
        Assert.Empty(nbConfig.CubeSlice);
        Assert.Equal(CubeTransform.None, nbConfig.CubeTransform);
    }
}
