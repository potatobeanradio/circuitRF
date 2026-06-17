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

    // ------------------------------------------------------------------ //
    //  Test 4: Table_TraceHeader_DoubleClick_OpensInlineEditor            //
    //  Verifies that TableRenderer.HitTest returns TraceHeader (not None) //
    //  for a column-header position, confirming that the dispatch logic   //
    //  in PlotControl.HandleDoubleTapAt can route to FocusSpecTextBox.    //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Table_TraceHeader_HitTest_ReturnsTraceHeaderKind()
    {
        var snp   = MakeSnp2Port();
        var plot  = new Plot(PlotType.Table, FreqUnit.GHz);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.BuildPath(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(trace);

        // Table layout: column-0 is the freq column, column-1 is the first trace column.
        // Both columns default to ColumnWidth=115 px at zoom=1.
        // Col-0 spans [0, 115), col-1 spans [115, 230). Resize handle zone = ±5 px from each right edge.
        // Use x=170 — comfortably inside col-1 and ≥10 px away from any right edge.
        const float headerY = 5f;
        const float traceColX = 170f;
        var canvasSize = (W: 400.0, H: 300.0);

        var hit = TableRenderer.HitTest(traceColX, headerY, plot, canvasSize, zoomLevel: 1f);

        Assert.Equal(TableHitKind.TraceHeader, hit.Kind);
        Assert.NotNull(hit.HitTrace);
        Assert.Same(trace, hit.HitTrace);
    }

    // ------------------------------------------------------------------ //
    //  Test 5: CommitSpec re-populates picker state (Fix 1)               //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task CommitSpec_SingleCubeSpec_RePopulatesPicker()
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var nodeAxis = new Axis("node", new[] { 0.0, 1.0 });
        var data = new System.Numerics.Complex[]
        {
            new(1, 0), new(2, 0),
            new(0.5, 0.5), new(1, 1),
            new(0.1, -0.1), new(0.9, 0.9),
        };
        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { freqAxis, nodeAxis }, data));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var slice = new[]
            {
                new AxisSlice("freq", AxisRole.KeepAsX,   0),
                new AxisSlice("node", AxisRole.PinToIndex, 0),
            };
            var cubeTrace = MakeCubeTrace(path, "V", slice);
            cubeTrace.Expression = cubeTrace.BuildPickerExpression(); // "V[:, 0]"

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(cubeTrace);
            var insp = new PlotInspectorViewModel(plot, () => {}, library: lib);
            insp.RebuildAndNotify();

            var row = insp.Traces.FirstOrDefault();
            Assert.NotNull(row);
            Assert.True(row.IsCubeBoundTrace);

            // CommitSpec with a mag transform on node index 1.
            row.CommitSpec("mag(V[:, 1])");

            // CubeName/Transform/Slice must be back-populated from the parsed spec.
            Assert.Equal("V", cubeTrace.CubeName);
            Assert.Equal(CubeTransform.Mag, cubeTrace.Transform);
            Assert.NotNull(cubeTrace.Slice);
            var nodeSlice = cubeTrace.Slice!.FirstOrDefault(s => s.AxisName == "node");
            Assert.Equal(AxisRole.PinToIndex, nodeSlice.Role);
            Assert.Equal(1, nodeSlice.Index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ //
    //  Tests 6–9: BuildCarriedSliceFromCube — slice carryover semantics   //
    // ------------------------------------------------------------------ //

    private static RfCore.Data.DataCube MakeCubeVhb()
    {
        // Simulates an HB V cube: axes (freq, node, harmonic)
        // node axis has labels so BuildPickerExpression emits quoted names.
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var node = new Axis("node", new[] { 0.0, 1.0, 2.0 }, "", new[] { "GND", "Vout2", "Vout3" });
        var harm = new Axis("harmonic", new[] { 1.0, 2.0, 3.0 });
        // 2 × 3 × 3 = 18 complex values (just zeros for shape testing)
        return new RfCore.Data.DataCube(
            new[] { freq, node, harm },
            new System.Numerics.Complex[18]);
    }

    private static RfCore.Data.DataCube MakeCubeIbranch()
    {
        // Simulates an HB branch-current cube: axes (freq, harmonic) — no node axis
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var harm = new Axis("harmonic", new[] { 1.0, 2.0, 3.0 });
        return new RfCore.Data.DataCube(
            new[] { freq, harm },
            new System.Numerics.Complex[6]);
    }

    [Fact]
    public void BuildCarriedSlice_SharedAxesCarriedOver()
    {
        // V slice: freq=X, node=pin@1, harmonic=pin@2
        var oldSlice = new[]
        {
            new AxisSlice("freq",     AxisRole.KeepAsX,   0),
            new AxisSlice("node",     AxisRole.PinToIndex, 1, Label: "Vout2"),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
        };
        var result = TraceRowViewModel.BuildCarriedSliceFromCube(MakeCubeIbranch(), oldSlice);

        // I:X1:g has (freq, harmonic) — node dropped, the other two carried.
        Assert.Equal(2, result.Length);
        Assert.Equal("freq",     result[0].AxisName);
        Assert.Equal(AxisRole.KeepAsX,   result[0].Role);
        Assert.Equal("harmonic", result[1].AxisName);
        Assert.Equal(AxisRole.PinToIndex, result[1].Role);
        Assert.Equal(2, result[1].Index);  // harmonic pin=2 carried
    }

    [Fact]
    public void BuildCarriedSlice_OutOfRangeIndexClamped()
    {
        // Old slice pins harmonic at index 5, but new cube only has 3 elements → clamp to 2.
        var oldSlice = new[]
        {
            new AxisSlice("freq",     AxisRole.KeepAsX,   0),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 5),
        };
        var result = TraceRowViewModel.BuildCarriedSliceFromCube(MakeCubeIbranch(), oldSlice);

        Assert.Equal(2, result.Length);
        Assert.Equal(2, result[1].Index);  // clamped from 5 → 2 (max valid index)
    }

    [Fact]
    public void BuildCarriedSlice_AbsentAxisDropped()
    {
        // Old slice has node (absent in I cube) — it must not appear in result.
        var oldSlice = new[]
        {
            new AxisSlice("freq",     AxisRole.KeepAsX,   0),
            new AxisSlice("node",     AxisRole.PinToIndex, 1, Label: "Vout2"),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
        };
        var result = TraceRowViewModel.BuildCarriedSliceFromCube(MakeCubeIbranch(), oldSlice);

        Assert.DoesNotContain(result, s => s.AxisName == "node");
    }

    [Fact]
    public void BuildCarriedSlice_LabelAxesGetQuotedLabel()
    {
        // V cube has node axis with labels. Switch from branch-current (no node) to V:
        // new axis "node" is absent from oldSlice → defaults to pin@0 with label "GND".
        var oldSlice = new[]
        {
            new AxisSlice("freq",     AxisRole.KeepAsX,   0),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 2),
        };
        var result = TraceRowViewModel.BuildCarriedSliceFromCube(MakeCubeVhb(), oldSlice);

        // node is new → pin@0, label="GND"
        var nodeSlice = result.Single(s => s.AxisName == "node");
        Assert.Equal(0, nodeSlice.Index);
        Assert.Equal("GND", nodeSlice.Label);

        // harmonic carried at 2.
        var harmSlice = result.Single(s => s.AxisName == "harmonic");
        Assert.Equal(2, harmSlice.Index);
    }
}
