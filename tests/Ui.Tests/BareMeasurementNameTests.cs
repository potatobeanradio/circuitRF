// ================================================================
//  BareMeasurementNameTests.cs  —  Gate tests for bare measurement name
//  resolution in CubeTraceSpecParser (brief-bare-measurement-name)
//
//  1. BareScalar_Resolves          — bare "PDC" → true, empty slice
//  2. BareScalar_WithTransform     — "dB10 PDC" → true, empty slice, dB10
//  3. BareRank1_Resolves           — bare "Gain" (rank-1) → true, one KeepAsX slice
//  4. EmptyBracketScalar           — "PDC[]" → true, empty slice (bonus form)
//  5. CommitSpec_BareScalar_NoExprError — CommitSpec sets CubeName, not ExpressionError
// ================================================================

using System;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class BareMeasurementNameTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeScalar(double value = 1.0) =>
        new DataCube(Array.Empty<Axis>(), new[] { value });

    private static DataCube MakeRank1(string axisName = "freq", int len = 3)
    {
        var axis = new Axis(axisName, Enumerable.Range(0, len).Select(i => (double)i).ToArray(), "Hz");
        var data = Enumerable.Range(0, len).Select(i => (double)i).ToArray();
        return new DataCube(new[] { axis }, data);
    }

    private static DataSet MakeDsWithMeasScalar()
    {
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "PDC", MakeScalar(0.042));
        return ds;
    }

    // ── 1. BareScalar_Resolves ────────────────────────────────────────────────

    [Fact]
    public void BareScalar_Resolves()
    {
        var ds = MakeDsWithMeasScalar();

        bool ok = CubeTraceSpecParser.TryParse("PDC", ds,
            out var cubeName, out var slice, out var transform, out var error);

        Assert.True(ok, $"Expected true but got error: {error}");
        Assert.Equal("PDC", cubeName);
        Assert.NotNull(slice);
        Assert.Empty(slice!);
        Assert.Equal(CubeTransform.None, transform);
    }

    // ── 2. BareScalar_WithTransform ──────────────────────────────────────────

    [Fact]
    public void BareScalar_WithTransform()
    {
        var ds = MakeDsWithMeasScalar();

        bool ok = CubeTraceSpecParser.TryParse("dB10 PDC", ds,
            out var cubeName, out var slice, out var transform, out var error);

        Assert.True(ok, $"Expected true but got error: {error}");
        Assert.Equal("PDC", cubeName);
        Assert.NotNull(slice);
        Assert.Empty(slice!);
        Assert.Equal(CubeTransform.dB10, transform);
    }

    // ── 3. BareRank1_Resolves ─────────────────────────────────────────────────

    [Fact]
    public void BareRank1_Resolves()
    {
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", MakeRank1("freq", 5));

        bool ok = CubeTraceSpecParser.TryParse("Gain", ds,
            out var cubeName, out var slice, out var transform, out var error);

        Assert.True(ok, $"Expected true but got error: {error}");
        Assert.Equal("Gain", cubeName);
        Assert.NotNull(slice);
        Assert.Single(slice!);
        Assert.Equal(AxisRole.KeepAsX, slice![0].Role);
    }

    // ── 4. EmptyBracketScalar ─────────────────────────────────────────────────

    [Fact]
    public void EmptyBracketScalar()
    {
        var ds = MakeDsWithMeasScalar();

        bool ok = CubeTraceSpecParser.TryParse("PDC[]", ds,
            out var cubeName, out var slice, out var transform, out var error);

        Assert.True(ok, $"Expected true but got error: {error}");
        Assert.Equal("PDC", cubeName);
        Assert.NotNull(slice);
        Assert.Empty(slice!);
    }

    // ── 5. CommitSpec_BareScalar_NoExprError ─────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task CommitSpec_BareScalar_NoExprError()
    {
        // Build a minimal Data Display loaded with a dataset containing PDC scalar.
        var ds = MakeDsWithMeasScalar();

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_bmn_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(ds, path, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = "PDC";
            trace.Slice      = Array.Empty<AxisSlice>();
            trace.Transform  = CubeTransform.None;

            var plot = new Plot(PlotType.Table, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();

            var rowVm = inspector.Traces[0];

            // CommitSpec with bare "PDC" must succeed: CubeName is set, no expression error.
            rowVm.CommitSpec("PDC");

            Assert.Null(trace.ExpressionError);
            Assert.Equal("PDC", trace.CubeName);
            Assert.NotNull(trace.Slice);
            Assert.Empty(trace.Slice!);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
