// ================================================================
//  NodeBranchSelectorScalarTests.cs
//  Gate tests for brief-node-branch-selector-scalar
//
//  1. DcNoSweep_NodeIsSelector
//  2. DcNoSweep_BranchScalarOnTable
//  3. SpecParser_FullyPinned_Scalar
//  4. TrySetCubeData_NoX_Scalar
//  5. HbNoSweep_HarmonicIsX
//  6. Swept_Unchanged
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

public sealed class NodeBranchSelectorScalarTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeNodeOnlyCube(string[] nodeLabels)
    {
        var vals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        return new DataCube(new[] { new Axis("node", vals, "", nodeLabels) },
                            new double[nodeLabels.Length]);
    }

    private static DataCube MakeBranchOnlyCube(string[] branchLabels)
    {
        var vals = Enumerable.Range(0, branchLabels.Length).Select(i => (double)i * 1e-3).ToArray();
        return new DataCube(new[] { new Axis("branch", vals, "A", branchLabels) },
                            new double[branchLabels.Length]);
    }

    private static DataCube MakeNodeHarmonicCube(string[] nodeLabels, int harmonics = 3)
    {
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        return new DataCube(
            new[] { new Axis("node", nodeVals, "", nodeLabels), new Axis("harmonic", harmVals, "Hz") },
            new Complex[nodeLabels.Length * harmonics]);
    }

    private static DataCube MakePinNodeHarmonicCube(int pinCount, string[] nodeLabels, int harmonics = 3)
    {
        var pinVals  = Enumerable.Range(0, pinCount).Select(i => (double)i - 10.0).ToArray();
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        return new DataCube(
            new[]
            {
                new Axis("Pin_avail", pinVals, "dBm"),
                new Axis("node",      nodeVals, "", nodeLabels),
                new Axis("harmonic",  harmVals, "Hz"),
            },
            new Complex[pinCount * nodeLabels.Length * harmonics]);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_nbss_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static TraceRowViewModel BuildInspector(
        DataSourceLibraryViewModel lib, string sourcePath, string cubeName,
        PlotType plotType = PlotType.Rect)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = null;
        trace.Transform  = CubeTransform.None;

        var plot      = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    // ── 1. DcNoSweep_NodeIsSelector ─────────────────────────────────────────
    // DC V cube has only a "node" axis (rank-1, no swept axis).
    // Default slice must pin node (PinToIndex) and leave no KeepAsX axis.
    // The node AxisRoleRow's ShowPinPicker == true.

    [Fact]
    public async Task DcNoSweep_NodeIsSelector()
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "V", MakeNodeOnlyCube(new[] { "Vout", "Vdd", "Vgs" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "DC1.V");

            // No axis should be KeepAsX
            Assert.False(trvm.AxisRoles.Any(r => r.IsX),
                "DC node-only cube must have no KeepAsX axis in default slice.");

            // The node row must exist and be pinned (ShowPinPicker == true)
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.True(nodeRow.ShowPinPicker,
                "node axis must show the pin picker (not be treated as X).");
            Assert.False(nodeRow.IsX,
                "node axis must not be KeepAsX.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2. DcNoSweep_BranchScalarOnTable ────────────────────────────────────
    // Table trace with I/branch cube pinned to "Iout" resolves to a scalar.
    // Must not throw; trace.CubeIsScalar == true.

    [Fact]
    public async Task DcNoSweep_BranchScalarOnTable()
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "I", MakeBranchOnlyCube(new[] { "Iout", "Ids" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = "DC1.I";
            trace.Slice      = new[] { new AxisSlice("branch", AxisRole.PinToIndex, 0, Label: "Iout") };
            trace.Transform  = CubeTransform.None;

            var ex = Record.Exception(() =>
                PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Table, FreqUnit.GHz));
            Assert.Null(ex);

            Assert.True(trace.CubeIsScalar,
                "Fully-pinned branch cube on a Table must resolve to a scalar.");
            Assert.False(trace.ScalarOnNonTableInvalid,
                "Scalar on a Table must not set the invalid flag.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 3. SpecParser_FullyPinned_Scalar ─────────────────────────────────────
    // "DC1.I[\"Iout\"]" and "DC1.I[0]" both parse successfully as scalars
    // (fully-pinned slice, keptDims.Count == 0, no error).

    [Fact]
    public async Task SpecParser_FullyPinned_Scalar()
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "I", MakeBranchOnlyCube(new[] { "Iout", "Ids" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Load the DataSet from the library so CubeTraceSpecParser can resolve it
            var entry = lib.Entries.FirstOrDefault(e => e.FilePath == path);
            Assert.NotNull(entry);
            var parsedDs = entry!.Data;
            Assert.NotNull(parsedDs);

            // Label-pinned form: DC1.I["Iout"]
            bool ok1 = CubeTraceSpecParser.TryParse(
                "DC1.I[\"Iout\"]", parsedDs!,
                out string cn1, out var slice1, out _, out string err1);

            Assert.True(ok1, $"Label-pinned form failed: {err1}");
            Assert.Equal("DC1.I", cn1);
            Assert.NotNull(slice1);
            Assert.Single(slice1!);
            Assert.Equal(AxisRole.PinToIndex, slice1![0].Role);
            Assert.Empty(err1);

            // Index-pinned form: DC1.I[0]
            bool ok2 = CubeTraceSpecParser.TryParse(
                "DC1.I[0]", parsedDs!,
                out string cn2, out var slice2, out _, out string err2);

            Assert.True(ok2, $"Index-pinned form failed: {err2}");
            Assert.Equal("DC1.I", cn2);
            Assert.NotNull(slice2);
            Assert.Single(slice2!);
            Assert.Equal(AxisRole.PinToIndex, slice2![0].Role);
            Assert.Empty(err2);

            // Neither slice should contain KeepAsX (they are scalar / fully-pinned)
            Assert.False(slice1!.Any(s => s.Role == AxisRole.KeepAsX),
                "Label-pinned scalar slice must have no KeepAsX entry.");
            Assert.False(slice2!.Any(s => s.Role == AxisRole.KeepAsX),
                "Index-pinned scalar slice must have no KeepAsX entry.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 4. TrySetCubeData_NoX_Scalar ─────────────────────────────────────────
    // A rank-1 cube with its only axis fully pinned (PinToIndex, no KeepAsX)
    // → TrySetCubeData must call SetScalarCubeData (CubeIsScalar == true),
    // not force node-as-X and produce a line.

    [Fact]
    public async Task TrySetCubeData_NoX_Scalar()
    {
        var ds = new DataSet();
        ds.AddToGroup("DC1", "V", MakeNodeOnlyCube(new[] { "Vout", "Vdd" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = "DC1.V";
            // Fully pinned: only node axis, no KeepAsX
            trace.Slice     = new[] { new AxisSlice("node", AxisRole.PinToIndex, 0, Label: "Vout") };
            trace.Transform = CubeTransform.None;

            var ex = Record.Exception(() =>
                PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Table, FreqUnit.GHz));
            Assert.Null(ex);

            Assert.True(trace.CubeIsScalar,
                "Fully-pinned rank-1 cube must resolve to a scalar via SetScalarCubeData.");
            Assert.Empty(trace.Points);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. HbNoSweep_HarmonicIsX ─────────────────────────────────────────────
    // HB V[node, harmonic] (rank-2, no parametric sweep) → harmonic is KeepAsX,
    // node is PinToIndex. Regression: harmonic must NOT be pinned, and node must
    // NOT be promoted to X.

    [Fact]
    public async Task HbNoSweep_HarmonicIsX()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout", "Vdd" }, harmonics: 5));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            var harmRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "harmonic");

            Assert.NotNull(nodeRow);
            Assert.NotNull(harmRow);

            Assert.True(harmRow!.IsX,
                "harmonic axis must be KeepAsX (first non-label axis).");
            Assert.False(nodeRow!.IsX,
                "node axis must NOT be KeepAsX (it is a label axis).");
            Assert.True(nodeRow.ShowPinPicker,
                "node axis must show the pin picker.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 6. Swept_Unchanged ───────────────────────────────────────────────────
    // [Pin_avail, node, harmonic] → Pin_avail is KeepAsX (first non-label axis),
    // node and harmonic are both pinned. Regression: the parametric axis must
    // remain X even though node appears before harmonic.

    [Fact]
    public async Task Swept_Unchanged()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V",
            MakePinNodeHarmonicCube(pinCount: 7, nodeLabels: new[] { "Vout", "Vdd" }, harmonics: 3));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            var pinRow  = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "Pin_avail");
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            var harmRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "harmonic");

            Assert.NotNull(pinRow);
            Assert.NotNull(nodeRow);
            Assert.NotNull(harmRow);

            Assert.True(pinRow!.IsX,
                "Pin_avail (first non-label axis) must be KeepAsX.");
            Assert.False(nodeRow!.IsX,
                "node must be pinned when a swept axis is present.");
            Assert.False(harmRow!.IsX,
                "harmonic must be pinned when a swept axis is already X.");
            Assert.True(nodeRow.ShowPinPicker);
            Assert.True(harmRow.ShowPinPicker);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
