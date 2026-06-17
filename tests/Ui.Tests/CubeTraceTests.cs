// ================================================================
//  CubeTraceTests.cs  —  Phase 7.2c-a + Part 1 slice conformance
//
//  Verifies that cube-bound traces build points correctly,
//  rank≥3 cubes are not offered in the signal picker,
//  .cdd round-trip is intact, and the new All/"a..b" slice tokens
//  are accepted by CubeTraceSpecParser (brief-trace-sweep-conformance).
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

    // ---- Test 3: Rank ≥ 3 cube IS offered (Phase 7.3a: one item per cube) --

    [Fact]
    public async Task CubeTrace_RankGE3_Offered()
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

            var snp  = new SNP(new[] { 1e9 }, 2);
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row       = inspector.Traces[0];

            // Phase 7.3a: rank-3 IS offered as a single cube-selector item.
            var item = row.AvailableSignals.FirstOrDefault(s => s.IsCubeBound && s.CubeName == "V");
            Assert.NotNull(item);

            // The axis-role rows are built from the cube's 3 axes.
            // AxisRoles is populated when the trace is cube-bound and the lib has the data.
            // (For a network-bound trace placeholder, AxisRoles is empty until cube is selected.)
            Assert.Empty(row.AxisRoles);  // no cube selected yet on this placeholder trace
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

// ================================================================
//  NodeIndexedCurrentFilterTests  —  Part 2 UI gate tests
//  (brief-trace-sweep-conformance.md §2: node-indexed current not offered)
// ================================================================

public sealed class NodeIndexedCurrentFilterTests
{
    // Build a DataSet with a node-indexed "INl" cube + a branch cube "I:M1:d".
    // The picker should offer the branch cube but NOT the node-indexed one.
    private static DataSet MakeDs()
    {
        var nodeAxis = new Axis("node",     new[] { 0.0, 1.0 }, "",      new[] { "n_gate", "n_drain" });
        var harmAxis = new Axis("harmonic", new[] { 0.0, 1e9 }, "Hz");
        var ds       = new DataSet();
        ds.Add("INl",    new DataCube(new[] { nodeAxis, harmAxis },
                             new System.Numerics.Complex[2 * 2]));
        ds.Add("I:M1:d", new DataCube(new[] { harmAxis },
                             new System.Numerics.Complex[2]));
        return ds;
    }

    [Fact]
    public async Task NodeIndexedCurrent_NotOffered_BranchCube_IsOffered()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"crf_inl_{Guid.NewGuid():N}.npy");
        try
        {
            var ds = MakeDs();
            RfCore.Export.DataSetExporter.Export(ds, tmpPath, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);

            var snp  = new SNP(new[] { 1e9 }, 2);
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row       = inspector.Traces[0];
            var signals   = row.AvailableSignals;

            // Node-indexed current must be filtered out.
            Assert.DoesNotContain(signals, s => s.IsCubeBound && s.CubeName == "INl");

            // Branch cube (no node axis) must be offered.
            Assert.Contains(signals, s => s.IsCubeBound && s.CubeName == "I:M1:d");
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }
}

// ================================================================
//  CubeSliceConformanceTests  —  Part 1 gate tests
//  (brief-trace-sweep-conformance.md §1a: All / a..b in CubeTraceSpecParser)
// ================================================================

public sealed class CubeSliceConformanceTests
{
    // "V" cube: [freq(5), node(5), pin(2)] — gives room for 2..3 / 2..4 on axis 0.
    private static DataSet MakeDs()
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9, 4e9, 5e9 }, "Hz");
        var nodeAxis = new Axis("node", new[] { 0.0, 1.0, 2.0, 3.0, 4.0 }, "");
        var pinAxis  = new Axis("pin",  new[] { -10.0, 0.0 }, "dBm");
        var data     = new System.Numerics.Complex[5 * 5 * 2];
        var ds       = new DataSet();
        ds.Add("V", new DataCube(new[] { freqAxis, nodeAxis, pinAxis }, data));
        return ds;
    }

    // Test 1: "All" parses identically to ":"
    [Fact]
    public void All_ParsesLikeColon()
    {
        var ds = MakeDs();
        bool ok1 = CubeTraceSpecParser.TryParse("V[:, 4, 1]",   ds, out _, out var s1, out _, out _);
        bool ok2 = CubeTraceSpecParser.TryParse("V[All, 4, 1]", ds, out _, out var s2, out _, out _);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Equal(s1![0].Role, s2![0].Role);
        Assert.Equal(AxisRole.KeepAsX, s2[0].Role);
        Assert.False(s2[0].IsNarrowedRange);
    }

    // Test 2: "2..3" → KeepRange with length 1 (end-exclusive)
    [Fact]
    public void Range_2_3_LengthOne()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse("V[2..3, 4, 1]", ds, out _, out var slice, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(slice);
        Assert.Equal(AxisRole.KeepAsX, slice![0].Role);
        Assert.True(slice[0].IsNarrowedRange);
        Assert.Equal(2, slice[0].RangeStart);
        Assert.Equal(3, slice[0].RangeEndExclusive);
    }

    // Test 3: "2..4" → KeepRange with length 2 (end-exclusive)
    [Fact]
    public void Range_2_4_LengthTwo()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse("V[2..4, 4, 1]", ds, out _, out var slice, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(slice);
        Assert.Equal(AxisRole.KeepAsX, slice![0].Role);
        Assert.True(slice[0].IsNarrowedRange);
        Assert.Equal(2, slice[0].RangeStart);
        Assert.Equal(4, slice[0].RangeEndExclusive);
    }

    // Test 4: case-insensitive "all" / "ALL"
    [Fact]
    public void All_CaseInsensitive()
    {
        var ds = MakeDs();
        bool ok1 = CubeTraceSpecParser.TryParse("V[all, 4, 1]", ds, out _, out var s1, out _, out _);
        bool ok2 = CubeTraceSpecParser.TryParse("V[ALL, 4, 1]", ds, out _, out var s2, out _, out _);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Equal(AxisRole.KeepAsX, s1![0].Role);
        Assert.Equal(AxisRole.KeepAsX, s2![0].Role);
        Assert.False(s1[0].IsNarrowedRange);
        Assert.False(s2[0].IsNarrowedRange);
    }

    // Test 5: "2..2" → empty range error (lo == hiEx, end-exclusive)
    [Fact]
    public void Range_EqualEndpoints_Error()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse("V[2..2, 4, 1]", ds, out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    // Test 6: "1..0" → empty range error (inverted)
    [Fact]
    public void Range_InvertedEndpoints_Error()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse("V[1..0, 4, 1]", ds, out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    // Test 7 (Part 1 #8): "V[:, All, 1]" → two X axes → error
    [Fact]
    public void TwoXAxes_Parser_Error()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse("V[:, All, 1]", ds, out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("Too many", error, StringComparison.OrdinalIgnoreCase);
    }
}
