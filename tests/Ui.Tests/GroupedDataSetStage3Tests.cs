// ================================================================
//  GroupedDataSetStage3Tests.cs  —  Gate tests for grouped DataSet stage 3
//  (brief-grouped-dataset-stage3.md)
//
//  1. Parser_Grouped_QualifiedName       — CubeTraceSpecParser resolves "HB1.V[:, 0]" in a grouped set
//  2. Parser_Grouped_BareNameFails       — bare "V[:, 0]" fails when cube is only in group "HB1"
//  3. Parser_Grouped_AvailableListQualified — error message lists qualified names
//  4. Expr_Grouped_QualifiedRef          — TraceExpression resolves "mag(HB1.V[:, 0])"
//  5. Expr_Grouped_CrossGroup            — cross-group expression with same-shape slices
//  6. TraceRow_Grouped_SignalsQualified  — AvailableSignals carries qualified CubeName items
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class GroupedDataSetStage3Tests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 2-group DataSet:
    ///   "HB1" → V [freq(3), node(2)] complex
    ///   "SP1" → S [freq(3), i(2), j(2)] complex
    ///   "measurements" → Gain [freq(3)] real
    /// </summary>
    private static DataSet MakeGroupedDs()
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var nodeAxis = new Axis("node", new[] { 0.0, 1.0 });
        var iAxis    = new Axis("i",    new[] { 0.0, 1.0 });
        var jAxis    = new Axis("j",    new[] { 0.0, 1.0 });

        var vData = new Complex[]
        {
            new(1.0, 0.0), new(2.0, 0.0),
            new(0.5, 0.5), new(1.0, 1.0),
            new(0.1,-0.1), new(0.9, 0.9),
        };
        var sData = new Complex[3 * 2 * 2];
        for (int k = 0; k < sData.Length; k++) sData[k] = new Complex(0.5, 0.0);

        var gainData = new double[] { -3.0, -3.1, -3.2 };

        var ds = new DataSet();
        ds.AddToGroup("HB1",         "V",    new DataCube(new[] { freqAxis, nodeAxis }, vData));
        ds.AddToGroup("SP1",         "S",    new DataCube(new[] { freqAxis, iAxis, jAxis }, sData));
        ds.AddToGroup("measurements","Gain", new DataCube(new[] { freqAxis }, gainData));
        return ds;
    }

    // ── Test 1: Parser_Grouped_QualifiedName ─────────────────────────────────

    [Fact]
    public void Parser_Grouped_QualifiedName()
    {
        var ds = MakeGroupedDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "HB1.V[:, 0]", ds,
            out string cubeName, out var slice, out var transform, out string error);

        Assert.True(ok, error);
        Assert.Equal("HB1.V", cubeName);
        Assert.NotNull(slice);
        Assert.Equal(2, slice!.Length);
        Assert.Equal(AxisRole.KeepAsX,    slice[0].Role);
        Assert.Equal(AxisRole.PinToIndex, slice[1].Role);
        Assert.Equal(0,                   slice[1].Index);
        Assert.Equal(CubeTransform.None,  transform);
    }

    // ── Test 2: Parser_Grouped_BareNameFails ─────────────────────────────────

    [Fact]
    public void Parser_Grouped_BareNameFails()
    {
        // "V" is only in group "HB1"; bare "V" is absent from the default group.
        var ds = MakeGroupedDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "V[:, 0]", ds,
            out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("HB1.V", error); // the available list should list the qualified name
    }

    // ── Test 3: Parser_Grouped_AvailableListQualified ────────────────────────

    [Fact]
    public void Parser_Grouped_AvailableListQualified()
    {
        var ds = MakeGroupedDs();
        CubeTraceSpecParser.TryParse("NoSuchCube[:, 0]", ds, out _, out _, out _, out string error);

        // Parser lists every cube with its qualified name, regardless of plotability.
        Assert.Contains("HB1.V",             error);
        Assert.Contains("SP1.S",             error);  // S is listed as "SP1.S", not bare "S"
        Assert.Contains("measurements.Gain", error);
        // Bare names must NOT appear as standalone entries.
        Assert.DoesNotContain("Available: V,",  error);
        Assert.DoesNotContain("Available: S,",  error);
        Assert.DoesNotContain(", V,",           error);  // bare V in middle of list
    }

    // ── Test 4: Expr_Grouped_QualifiedRef ────────────────────────────────────

    [Fact]
    public void Expr_Grouped_QualifiedRef()
    {
        var ds = MakeGroupedDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(HB1.V[:, 0])", ds, PlotType.Rect,
            out var xVals, out var cz, out var rz,
            out _, out _, out string error);

        Assert.True(ok, error);
        Assert.Null(cz);
        Assert.NotNull(rz);
        Assert.Equal(3, rz!.Length);
        // node-0 magnitudes: |1+0j|=1, |0.5+0.5j|=√0.5, |0.1-0.1j|=√0.02
        Assert.Equal(1.0,             rz[0], 6);
        Assert.Equal(Math.Sqrt(0.5),  rz[1], 6);
        Assert.Equal(Math.Sqrt(0.02), rz[2], 6);
    }

    // ── Test 5: Expr_Grouped_CrossGroup ─────────────────────────────────────

    [Fact]
    public void Expr_Grouped_CrossGroup()
    {
        // Build a set with two groups, each having a real 1-D cube with same freq axis.
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var ds = new DataSet();
        ds.AddToGroup("SP1", "Pin", new DataCube(new[] { freqAxis }, new double[] { 1.0, 2.0, 3.0 }));
        ds.AddToGroup("HB1", "Pout",new DataCube(new[] { freqAxis }, new double[] { 4.0, 5.0, 6.0 }));

        bool ok = TraceExpression.TryEvaluate(
            "HB1.Pout[:] - SP1.Pin[:]", ds, PlotType.Rect,
            out _, out _, out var rz,
            out _, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(rz);
        Assert.Equal(3, rz!.Length);
        Assert.Equal(3.0, rz[0], 9);
        Assert.Equal(3.0, rz[1], 9);
        Assert.Equal(3.0, rz[2], 9);
    }

    // ── Test 6: TraceRow_Grouped_SignalsQualified ────────────────────────────

    [Fact]
    public async Task TraceRow_Grouped_SignalsQualified()
    {
        var ds = MakeGroupedDs();

        // Export and reload via DataSourceLibraryViewModel.
        string path = Path.Combine(Path.GetTempPath(), $"crf_gds3_{Guid.NewGuid():N}.npy");
        try
        {
            DataSetExporter.Export(ds, path, ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);

            // Seed a cube-bound trace pointing at "HB1.V" so the inspector has a valid context.
            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = "HB1.V";
            trace.Slice      = new[] { new AxisSlice("freq", AxisRole.KeepAsX, 0),
                                       new AxisSlice("node", AxisRole.PinToIndex, 0) };

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();

            var trvm = inspector.Traces[0];

            // Analysis cubes are qualified; measurements-group cubes emit with bare name.
            // With the cascade picker, AvailableSignals is per-group — enumerate all groups.
            Assert.Contains("HB1",          trvm.AvailableGroups);
            Assert.Contains("Measurements", trvm.AvailableGroups);

            // HB1 group must contain qualified "HB1.V".
            trvm.SelectedGroup = "HB1";
            var hb1Items = trvm.AvailableSignals.Where(s => s.IsCubeBound).Select(s => s.CubeName).ToList();
            Assert.Contains("HB1.V", hb1Items);

            // Measurements group must contain bare "Gain".
            trvm.SelectedGroup = "Measurements";
            var measItems = trvm.AvailableSignals.Where(s => s.IsCubeBound).Select(s => s.CubeName).ToList();
            Assert.Contains("Gain", measItems);   // measurements group → bare name

            // SP1.S must not appear in any group (S is filtered from cube picker).
            foreach (var grp in trvm.AvailableGroups.ToList())
            {
                trvm.SelectedGroup = grp;
                Assert.DoesNotContain("SP1.S",
                    trvm.AvailableSignals.Where(s => s.IsCubeBound).Select(s => s.CubeName));
            }

            // Selecting HB1.V must set the trace's CubeName to the qualified string.
            trvm.SelectedGroup = "HB1";
            var hb1V = trvm.AvailableSignals.First(s => s.IsCubeBound && s.CubeName == "HB1.V");
            trvm.SelectedSignal = hb1V;
            Assert.Equal("HB1.V", trace.CubeName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
