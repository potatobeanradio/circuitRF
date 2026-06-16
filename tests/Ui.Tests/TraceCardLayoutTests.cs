// ================================================================
//  TraceCardLayoutTests.cs  —  brief-table-cube-layout-fixes gate tests
//
//  1. ShowZ0Row_FalseForCube_TrueForSParam  — Z0 row gated on S-param only
//  2. UnifiedTransform_NetworkMap           — unified combo maps to DependentVarFormat
//  3. FreqUnitChange_RebuildsAxisRoles      — freq-unit change rebuilds pin labels
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

public sealed class TraceCardLayoutTests
{
    // ---- Helpers -----------------------------------------------------------

    private static SNP MakeSnp2Port()
    {
        var snp = new SNP(new[] { 1e9, 2e9 }, 2, MatrixType.S, MatrixFormat.MA);
        snp.FilePath = "test.s2p";
        return snp;
    }

    private static PlotInspectorViewModel MakeInspector(SNP snp, PlotType plotType = PlotType.Rect)
    {
        var plot  = new Plot(plotType, FreqUnit.GHz);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.BuildPath(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        return new PlotInspectorViewModel(plot, () => {}, library: null);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_layout_{Guid.NewGuid():N}.npy");
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

    // ------------------------------------------------------------------ //
    //  Test 1: ShowZ0Row — false for cube, true for S-param               //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ShowZ0Row_FalseForCube_TrueForSParam()
    {
        // S-param trace → ShowZ0Row must be true.
        var snp = MakeSnp2Port();
        var vm  = MakeInspector(snp);
        var row = vm.Traces.Single();

        Assert.True(row.ShowZ0Row,  "S-param trace must have ShowZ0Row=true");
        Assert.True(row.IsScatteringTrace, "S-param trace must be IsScatteringTrace");

        // Cube-bound trace → ShowZ0Row must be false.
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var ds = new DataSet();
        ds.Add("Pout", new DataCube(new[] { freqAxis }, new double[] { 1.0, 2.0 }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var slice = new[] { new AxisSlice("freq", AxisRole.KeepAsX, 0) };
            var cubeTrace = MakeCubeTrace(path, "Pout", slice);
            var cubePlot  = new Plot(PlotType.Rect, FreqUnit.GHz);
            cubePlot.Traces.Add(cubeTrace);
            var cubeInsp = new PlotInspectorViewModel(cubePlot, () => {}, library: lib);
            cubeInsp.RebuildAndNotify();

            var cubeRow = cubeInsp.Traces.FirstOrDefault();
            Assert.NotNull(cubeRow);
            Assert.True(cubeRow.IsCubeBoundTrace, "Cube trace should be IsCubeBoundTrace=true");
            Assert.False(cubeRow.ShowZ0Row, "Cube trace must have ShowZ0Row=false");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ //
    //  Test 2: UnifiedTransform_NetworkMap                                //
    // ------------------------------------------------------------------ //

    [Fact]
    public void UnifiedTransform_NetworkMap()
    {
        var snp = MakeSnp2Port();
        var vm  = MakeInspector(snp, PlotType.Rect);
        var row = vm.Traces.Single();

        // Network trace: TraceTransformItems should be AllTransformsForNetwork.
        var items = row.TraceTransformItems;
        Assert.Same(PlotInspectorViewModel.AllTransformsForNetwork, items);

        // Cube-only members are disabled.
        Assert.False(items.Single(t => t.Transform == CubeTransform.dB10).Enabled, "dB10 disabled for network");
        Assert.False(items.Single(t => t.Transform == CubeTransform.dB).Enabled,   "dB disabled for network");
        Assert.False(items.Single(t => t.Transform == CubeTransform.Conj).Enabled, "Conj disabled for network");

        // Setting unified transform to dB20 → YAxis becomes Db.
        row.SelectedTransformItem = items.Single(t => t.Transform == CubeTransform.dB20);
        Assert.Equal(DependentVarFormat.Db, row.Trace.YAxis);
        Assert.Equal(CubeTransform.dB20, row.SelectedTransformItem!.Transform);

        // Mag → Mag.
        row.SelectedTransformItem = items.Single(t => t.Transform == CubeTransform.Mag);
        Assert.Equal(DependentVarFormat.Mag, row.Trace.YAxis);

        // None → Complex.
        row.SelectedTransformItem = items.Single(t => t.Transform == CubeTransform.None);
        Assert.Equal(DependentVarFormat.Complex, row.Trace.YAxis);

        // Disabled item should be a no-op (YAxis stays Complex).
        var dB10Item = items.Single(t => t.Transform == CubeTransform.dB10);
        row.SelectedTransformItem = dB10Item;    // disabled → OnSelectedTransformItemChanged returns early
        Assert.Equal(DependentVarFormat.Complex, row.Trace.YAxis);
    }

    // ------------------------------------------------------------------ //
    //  Test 3: FreqUnitChange_RebuildsAxisRoles                           //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task FreqUnitChange_RebuildsAxisRoles()
    {
        // Cube with a single freq axis [1 GHz, 2 GHz, 3 GHz].
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var ds = new DataSet();
        ds.Add("Pout", new DataCube(new[] { freqAxis }, new double[] { 1.0, 2.0, 3.0 }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var slice = new[] { new AxisSlice("freq", AxisRole.KeepAsX, 0) };
            var cubeTrace = MakeCubeTrace(path, "Pout", slice);
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(cubeTrace);
            var insp = new PlotInspectorViewModel(plot, () => {}, library: lib);
            insp.RebuildAndNotify();

            var row = insp.Traces.FirstOrDefault();
            Assert.NotNull(row);
            Assert.True(row.IsCubeBoundTrace, "Should be cube-bound");
            Assert.NotEmpty(row.AxisRoles);

            // GHz: pin labels should include "1 GHz".
            var axRole = row.AxisRoles.First();
            Assert.Contains(axRole.PinOptions, opt => opt.Contains("GHz"));
            Assert.Contains(axRole.PinOptions, opt => opt.StartsWith("1 "));

            // Switch to MHz → labels must rebuild.
            insp.FreqUnit = FreqUnit.MHz;

            axRole = row.AxisRoles.First();
            Assert.Contains(axRole.PinOptions, opt => opt.Contains("MHz"));
            Assert.Contains(axRole.PinOptions, opt => opt.StartsWith("1000 "));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
