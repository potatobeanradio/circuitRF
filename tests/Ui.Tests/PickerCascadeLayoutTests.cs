// ================================================================
//  PickerCascadeLayoutTests.cs
//  Gate tests for brief-picker-cascade-layout
//
//  1. Groups_Built
//  2. GroupChange_AppliesFirstItem
//  3. NetworkGroup
//  4. Rebuild_PreservesSelection
//  5. EyeToggle
//  6. TransformOnNetwork
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

public sealed class PickerCascadeLayoutTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeRank1Cube(int len = 3)
    {
        var axis = new Axis("harmonic",
            Enumerable.Range(0, len).Select(i => (double)i * 1e9).ToArray(), "Hz");
        return new DataCube(new[] { axis }, new Complex[len]);
    }

    private static DataCube MakeIBranchCube(string[] branchLabels, int harmonics = 3)
    {
        var brVals  = Enumerable.Range(0, branchLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        var brAxis   = new Axis("branch",   brVals,  "A",  branchLabels);
        var harmAxis = new Axis("harmonic", harmVals, "Hz");
        return new DataCube(new[] { brAxis, harmAxis }, new Complex[branchLabels.Length * harmonics]);
    }

    private static DataCube MakeNodeHarmonicCube(string[] nodeLabels, int harmonics = 3)
    {
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        var nodeAxis = new Axis("node",     nodeVals, "", nodeLabels);
        var harmAxis = new Axis("harmonic", harmVals, "Hz");
        return new DataCube(new[] { nodeAxis, harmAxis }, new Complex[nodeLabels.Length * harmonics]);
    }

    private static DataCube MakeScalarCube()
        => new DataCube(Array.Empty<Axis>(), new double[] { 3.14 });

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_pcl_{Guid.NewGuid():N}.npy");
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

    // ── 1. Groups_Built ──────────────────────────────────────────────────────
    // HB run with groups "HB1" + measurements → AvailableGroups contains those
    // two; selecting HB1 populates AvailableSignals with bare-name items;
    // selecting Measurements repopulates with measurement names.

    [Fact]
    public async Task Groups_Built()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(["Vin", "Vout"]));
        ds.AddToGroup("HB1", "I", MakeIBranchCube(["Ids", "M1:d"]));
        ds.AddToGroup(DataSet.MeasurementsGroup, "PDC", MakeScalarCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Table plot so scalars are offered.
            var trvm = BuildInspector(lib, path, "HB1.V", PlotType.Table);

            Assert.Contains("HB1", trvm.AvailableGroups);
            Assert.Contains("Measurements", trvm.AvailableGroups);

            // Selecting HB1: items have bare labels (V, I)
            trvm.SelectedGroup = "HB1";
            var labels = trvm.AvailableSignals.Select(s => s.Label).ToList();
            Assert.Contains("V", labels);
            Assert.Contains("I", labels);
            Assert.DoesNotContain("PDC", labels);

            // Selecting Measurements: items have bare label (PDC)
            trvm.SelectedGroup = "Measurements";
            labels = trvm.AvailableSignals.Select(s => s.Label).ToList();
            Assert.Contains("PDC", labels);
            Assert.DoesNotContain("V", labels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2. GroupChange_AppliesFirstItem ───────────────────────────────────────
    // Setting SelectedGroup to "Measurements" selects that group's first item
    // and binds the trace with no expression error.

    [Fact]
    public async Task GroupChange_AppliesFirstItem()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(["Vin", "Vout"]));
        ds.AddToGroup(DataSet.MeasurementsGroup, "PDC", MakeScalarCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V", PlotType.Table);

            // Switch to Measurements group
            trvm.SelectedGroup = "Measurements";

            // SelectedSignal should now be the first item in Measurements
            Assert.NotNull(trvm.SelectedSignal);
            Assert.True(trvm.SelectedSignal!.IsCubeBound);
            Assert.Equal("PDC", trvm.SelectedSignal.CubeName);

            // No expression error
            Assert.Null(trvm.Trace.ExpressionError);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 3. NetworkGroup ───────────────────────────────────────────────────────
    // A single .s2p source → group named "S-Parameters";
    // item Labels omit the file prefix (bare "S(1,1)").

    [Fact]
    public async Task NetworkGroup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_pcl_{Guid.NewGuid():N}.s2p");
        try
        {
            // Write a minimal 2-port Touchstone
            File.WriteAllText(path,
                "! minimal\n" +
                "# GHz S RI R 50\n" +
                "1.0 0.1 0.0 0.2 0.0 0.2 0.0 0.1 0.0\n");

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = null;
            trace.Slice      = null;

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();
            var trvm = inspector.Traces[0];

            // Group should be "S-Parameters" (single source → no file prefix)
            Assert.Contains("S-Parameters", trvm.AvailableGroups);

            // Item labels should be bare (no file prefix)
            var labels = trvm.AvailableSignals.Select(s => s.Label).ToList();
            Assert.Contains("S(1,1)", labels);
            // Must not contain file prefix
            Assert.DoesNotContain(labels, l => l.Contains("..S("));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 4. Rebuild_PreservesSelection ─────────────────────────────────────────
    // With a cube trace bound to HB1.V, RebuildSignals re-selects group "HB1"
    // and item with CubeName "HB1.V"; no spurious trace mutation.

    [Fact]
    public async Task Rebuild_PreservesSelection()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(["Vin", "Vout"]));
        ds.AddToGroup("HB1", "I", MakeIBranchCube(["Ids"]));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            // Snapshot current selection
            string? groupBefore  = trvm.SelectedGroup;
            string? cubeBefore   = trvm.SelectedSignal?.CubeName;
            string? exprBefore   = trvm.Trace.Expression;

            // Trigger a rebuild (simulates library refresh)
            trvm.RefreshDataSources();

            // Group and item must be preserved
            Assert.Equal(groupBefore, trvm.SelectedGroup);
            Assert.Equal(cubeBefore,  trvm.SelectedSignal?.CubeName);
            Assert.Equal(exprBefore,  trvm.Trace.Expression);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. EyeToggle ─────────────────────────────────────────────────────────
    // ToggleShowAllCommand flips ShowAll; ShowAllToggleVisible gates the button
    // (same semantics as brief 4b).

    [Fact]
    public async Task EyeToggle()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(["n1", "n2"]));

        // Add __LabeledNodes so the toggle is visible and ShowAll starts false.
        var lblIdx  = new double[] { 0 };
        var lblAxis = new Axis("label", lblIdx, "", new[] { "n2" });
        var lblCube = new DataCube(new[] { lblAxis }, new double[1]);
        ds.AddToGroup("HB1", "__LabeledNodes", lblCube);

        // Unified I cube with both probe and device-port branches.
        var pbIdx  = new double[] { 0 };
        var pbAxis = new Axis("probe", pbIdx, "", new[] { "Ids" });
        var pbCube = new DataCube(new[] { pbAxis }, new double[1]);
        ds.AddToGroup("HB1", "__ProbeBranches", pbCube);
        ds.AddToGroup("HB1", "I", MakeIBranchCube(["Ids", "M1:d"]));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            // Initially ShowAll = false; toggle visible due to __LabeledNodes
            Assert.False(trvm.ShowAll);
            Assert.True(trvm.ShowAllToggleVisible);

            // Execute the command — ShowAll should flip
            trvm.ToggleShowAllCommand.Execute(null);
            Assert.True(trvm.ShowAll);

            // Execute again — flips back
            trvm.ToggleShowAllCommand.Execute(null);
            Assert.False(trvm.ShowAll);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 6. TransformOnNetwork ─────────────────────────────────────────────────
    // A network trace on Rect still exposes the transform combo via
    // TraceTransformItems (now on the spec row in AXAML, but the VM property
    // must remain populated for network traces).

    [Fact]
    public async Task TransformOnNetwork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_pcl_{Guid.NewGuid():N}.s2p");
        try
        {
            File.WriteAllText(path,
                "! minimal\n" +
                "# GHz S RI R 50\n" +
                "1.0 0.1 0.0 0.2 0.0 0.2 0.0 0.1 0.0\n");

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = null;
            trace.Slice      = null;

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            inspector.RebuildAndNotify();
            var trvm = inspector.Traces[0];

            // The trace is network-bound; IsRectOrTablePlot is true.
            Assert.False(trvm.IsCubeBoundTrace);
            Assert.True(trvm.IsRectOrTablePlot);

            // TraceTransformItems must be non-empty for network traces on Rect.
            Assert.NotEmpty(trvm.TraceTransformItems);

            // SelectedTransformItem must be set (dB20 is the default YAxis for S traces).
            Assert.NotNull(trvm.SelectedTransformItem);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
