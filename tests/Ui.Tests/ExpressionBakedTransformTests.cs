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

// A multi-cube TraceExpression already encodes its own transform (e.g. "10*log10(Pout_W*1000)").
// The transform combo must NOT double-apply on top of a REAL expression result — "render whatever the
// expression is". A bare single cube, by contrast, IS authored via the combo, so the combo still applies.
public sealed class ExpressionBakedTransformTests
{
    private static async Task<(string path, DataSourceLibraryViewModel lib)> LoadRealCube()
    {
        var ds = new DataSet();
        // Pout in watts: 0.1 W and 1.0 W.
        ds.Add("Pout_W", new DataCube(new[] { new Axis("Pin", new[] { 0.0, 5.0 }) },
                                      new double[] { 0.1, 1.0 }));

        string path = Path.Combine(Path.GetTempPath(), $"crf_baked_{System.Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static Trace MakeTrace(string path) =>
        new(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = path };

    [Fact]
    public async Task RealExpression_ComboTransformIsNoOp()
    {
        var (path, lib) = await LoadRealCube();
        try
        {
            // "10*log10(Pout_W*1000)" = Pout in dBm. 0.1 W → 20 dBm, 1.0 W → 30 dBm.
            var t = MakeTrace(path);
            t.Expression = "10*log10(Pout_W*1000)";

            t.Transform = CubeTransform.None;
            PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.True(t.TransformBaked);
            var yNone = t.Points.Select(p => (double)p.Y).ToArray();

            t.Transform = CubeTransform.dB;
            PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            var yDb = t.Points.Select(p => (double)p.Y).ToArray();

            // The dB combo must NOT change the rendered value — the expression already is the final value.
            Assert.Equal(yNone, yDb);
            Assert.Equal(20.0, yNone[0], 6);   // 10*log10(0.1*1000) = 10*log10(100) = 20
            Assert.Equal(30.0, yNone[1], 6);   // 10*log10(1.0*1000) = 10*log10(1000) = 30
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BareCube_ComboTransformStillApplies()
    {
        var (path, lib) = await LoadRealCube();
        try
        {
            // A bare single cube is authored via the picker/combo — the transform must still apply.
            var t = MakeTrace(path);
            t.CubeName = "Pout_W";
            t.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };

            t.Transform = CubeTransform.None;
            t.Expression = t.BuildPickerExpression();            // "Pout_W"
            PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            Assert.False(t.TransformBaked);                       // single-slice path → not baked
            var yNone = t.Points.Select(p => (double)p.Y).ToArray();

            t.Transform = CubeTransform.dB;
            t.Expression = t.BuildPickerExpression();            // "dB(Pout_W)"
            PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            var yDb = t.Points.Select(p => (double)p.Y).ToArray();

            Assert.NotEqual(yNone[0], yDb[0]);                   // dB combo DID transform the bare cube
            Assert.Equal(10.0 * System.Math.Log10(0.1), yDb[0], 6);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Option A: the transform combo is disabled (and pinned to None) for a real expression result, and the
    // trace's Transform is forced to None even if a stale transform was carried in.
    [Fact]
    public async Task RealExpression_ComboDisabledAndForcedNone()
    {
        var (path, lib) = await LoadRealCube();
        try
        {
            var trace = MakeTrace(path);
            trace.Expression = "10*log10(Pout_W*1000)";
            trace.Transform  = CubeTransform.dB;   // stale transform that must be cleared

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var insp = new PlotInspectorViewModel(plot, () => { }, library: lib);
            insp.RebuildAndNotify();
            var row = insp.Traces.First();

            Assert.True(trace.TransformIsInert);
            Assert.False(row.IsTransformComboEnabled);          // combo disabled
            Assert.Equal(CubeTransform.None, trace.Transform);  // forced to None
            Assert.Equal(CubeTransform.None, row.SelectedTransformItem!.Transform);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Contrast: a bare cube keeps the combo enabled (it's the authoring control).
    [Fact]
    public async Task BareCube_ComboEnabled()
    {
        var (path, lib) = await LoadRealCube();
        try
        {
            var trace = MakeTrace(path);
            trace.CubeName = "Pout_W";
            trace.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
            trace.Expression = trace.BuildPickerExpression();

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var insp = new PlotInspectorViewModel(plot, () => { }, library: lib);
            insp.RebuildAndNotify();
            var row = insp.Traces.First();

            Assert.False(trace.TransformIsInert);
            Assert.True(row.IsTransformComboEnabled);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
