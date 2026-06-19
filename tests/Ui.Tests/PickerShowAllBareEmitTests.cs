// ================================================================
//  PickerShowAllBareEmitTests.cs
//  Gate tests for brief-picker-showall-bare-emit
//
//  1. Measurements_EmitBare
//  2. Analysis_StaysQualified
//  3. ShowAll_RevealsBranchesAndNodes
//  4. ShowAllToggleVisible_Or
//  5. PickedMeasurement_Resolves
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

public sealed class PickerShowAllBareEmitTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeRank1Cube(int len = 3)
    {
        var axis = new Axis("harmonic",
            Enumerable.Range(0, len).Select(i => (double)i * 1e9).ToArray(), "Hz");
        return new DataCube(new[] { axis }, new Complex[len]);
    }

    private static DataCube MakeNodeHarmonicCube(string[] nodeLabels, int harmonics = 3)
    {
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        var nodeAxis = new Axis("node",     nodeVals, "", nodeLabels);
        var harmAxis = new Axis("harmonic", harmVals, "Hz");
        return new DataCube(new[] { nodeAxis, harmAxis }, new Complex[nodeLabels.Length * harmonics]);
    }

    private static DataCube MakeLabeledNodesCube(string[] labeledNodes)
    {
        var idx  = Enumerable.Range(0, labeledNodes.Length).Select(i => (double)i).ToArray();
        var axis = new Axis("label", idx, "", labeledNodes);
        return new DataCube(new[] { axis }, new double[labeledNodes.Length]);
    }

    private static DataCube MakeProbeBranchesCube(string[] probeNames)
    {
        var idx  = Enumerable.Range(0, probeNames.Length).Select(i => (double)i).ToArray();
        var axis = new Axis("probe", idx, "", probeNames);
        return new DataCube(new[] { axis }, new double[probeNames.Length]);
    }

    private static DataCube MakeScalarCube()
        => new DataCube(Array.Empty<Axis>(), new double[] { 3.14 });

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_psabe_{Guid.NewGuid():N}.npy");
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

    // ── 1. Measurements_EmitBare ──────────────────────────────────────────────
    // measurements-group cubes must appear with their bare name (e.g. "PDC"),
    // not their qualified name ("measurements.PDC").

    [Fact]
    public async Task Measurements_EmitBare()
    {
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "PDC", MakeScalarCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Table plot required because rank-0 cubes are Table-only.
            var trvm = BuildInspector(lib, path, "PDC", PlotType.Table);

            var cubeItems = trvm.AvailableSignals.Where(s => s.IsCubeBound).ToList();

            // Bare name "PDC" must appear (not "measurements.PDC").
            var pdc = cubeItems.FirstOrDefault(s => s.CubeName == "PDC");
            Assert.NotNull(pdc);
            Assert.DoesNotContain("measurements", pdc.Label);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2. Analysis_StaysQualified ────────────────────────────────────────────
    // Cubes in named analysis groups (e.g. "HB1") must stay qualified ("HB1.V").

    [Fact]
    public async Task Analysis_StaysQualified()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(["Vin", "Vout"]));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            var cubeItems = trvm.AvailableSignals.Where(s => s.IsCubeBound).ToList();

            // Qualified name must be used for analysis-group cubes.
            Assert.Contains(cubeItems, s => s.CubeName == "HB1.V");

            // Bare "V" must NOT appear as a separate item.
            Assert.DoesNotContain(cubeItems, s => s.CubeName == "V");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 3. ShowAll_RevealsNodes ───────────────────────────────────────────────
    // ShowAll toggle drives the node-axis filter; the unified I cube is always visible.

    [Fact]
    public async Task ShowAll_RevealsNodes()
    {
        // V has 3 nodes; __LabeledNodes only labels "n2".
        // Unified I cube contains both probe and device-port branches (all always visible).
        string[] allNodes     = ["n1", "n2", "n3"];
        string[] labeledNodes = ["n2"];

        var ds = new DataSet();
        ds.Add("V",               MakeNodeHarmonicCube(allNodes));
        ds.Add("__LabeledNodes",  MakeLabeledNodesCube(labeledNodes));
        ds.Add("__ProbeBranches", MakeProbeBranchesCube(["Ids"]));

        var brVals   = new double[] { 0, 1 };
        var harmVals = new double[] { 0, 1e9, 2e9 };
        var brAxis   = new Axis("branch",   brVals,  "A",  new[] { "Ids", "M1:d" });
        var harmAxis = new Axis("harmonic", harmVals, "Hz");
        ds.Add("I", new DataCube(new[] { brAxis, harmAxis }, new Complex[2 * 3]));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Bind to V.  ShowAll defaults to false because __LabeledNodes is present.
            var trvm = BuildInspector(lib, path, "V");
            Assert.False(trvm.ShowAll);

            // ── ShowAll = false ──────────────────────────────────────────────

            var cubeNames = trvm.AvailableSignals
                .Where(s => s.IsCubeBound).Select(s => s.CubeName).ToList();

            // Unified I cube is always visible (no per-cube branch filter).
            Assert.Contains("I", cubeNames);

            // Node axis shows only labeled node ("n2").
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Single(nodeRow.PinOptions);
            Assert.Equal("n2", nodeRow.PinOptions[0]);

            // ── ShowAll = true ───────────────────────────────────────────────

            trvm.ShowAll = true;

            // Node axis now shows all 3 nodes.
            nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Equal(3, nodeRow.PinOptions.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 4. ShowAllToggleVisible_Or ────────────────────────────────────────────
    // Toggle is visible when a filterable label axis (node OR branch) is present.
    // Toggle is invisible when neither axis type is present.

    [Fact]
    public async Task ShowAllToggleVisible_Or()
    {
        // --- Case A: node axis filterable (ShowAllNodesToggleVisible) ---
        {
            var dsA = new DataSet();
            dsA.Add("V",              MakeNodeHarmonicCube(["n1", "n2", "n3"]));
            dsA.Add("__LabeledNodes", MakeLabeledNodesCube(["n2"]));

            var (pathA, libA) = await ExportAndLoad(dsA);
            try
            {
                var trvmA = BuildInspector(libA, pathA, "V");
                Assert.True(trvmA.ShowAllToggleVisible,
                    "Toggle must be visible when a filterable node axis is present.");
            }
            finally
            {
                if (File.Exists(pathA)) File.Delete(pathA);
            }
        }

        // --- Case B: I cube with branch axis → toggle now visible (branch filter implemented) ---
        {
            var dsB = new DataSet();
            dsB.Add("__ProbeBranches", MakeProbeBranchesCube(["Ids"]));
            var brValsB   = new double[] { 0 };
            var harmValsB = new double[] { 0, 1e9, 2e9 };
            var brAxisB   = new Axis("branch",   brValsB,  "A",  new[] { "Ids" });
            var harmAxisB = new Axis("harmonic", harmValsB, "Hz");
            dsB.Add("I", new DataCube(new[] { brAxisB, harmAxisB }, new Complex[1 * 3]));

            var (pathB, libB) = await ExportAndLoad(dsB);
            try
            {
                var trvmB = BuildInspector(libB, pathB, "I");
                Assert.True(trvmB.ShowAllToggleVisible,
                    "Toggle must be visible when a filterable branch axis is present.");
            }
            finally
            {
                if (File.Exists(pathB)) File.Delete(pathB);
            }
        }
    }

    // ── 5. PickedMeasurement_Resolves ─────────────────────────────────────────
    // Selecting the bare "PDC" item from the measurements group on a Table plot
    // binds successfully and produces no expression error.

    [Fact]
    public async Task PickedMeasurement_Resolves()
    {
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "PDC", MakeScalarCube());

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Table plot required for scalar (rank-0) cubes.
            var trvm = BuildInspector(lib, path, "PDC", PlotType.Table);

            // The picker must offer a bare "PDC" item.
            var pdcItem = trvm.AvailableSignals
                .FirstOrDefault(s => s.IsCubeBound && s.CubeName == "PDC");
            Assert.NotNull(pdcItem);

            // Selecting it must not produce an expression error.
            trvm.SelectedSignal = pdcItem;

            Assert.Null(trvm.Trace.ExpressionError);
            Assert.Equal("PDC", trvm.Trace.CubeName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
