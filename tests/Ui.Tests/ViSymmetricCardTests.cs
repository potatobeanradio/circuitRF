// ================================================================
//  ViSymmetricCardTests.cs
//  Gate tests for brief-vi-symmetric-card
//
//  1. BranchPins_FilteredByProbeBranches
//  2. EyeRow_OnBranchAxis
//  3. SpecEdit_ResyncsCombos
//  4. SpecEdit_Invalid_BestEffort
//  5. AnalysisGroup_OffersBothVI
//  6. V_NodeFilter_Unchanged
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

public sealed class ViSymmetricCardTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeBranchHarmonicCube(string[] branchLabels, int harmonics = 3)
    {
        var branchVals = Enumerable.Range(0, branchLabels.Length).Select(i => (double)i).ToArray();
        var harmVals   = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        var branchAxis = new Axis("branch",   branchVals, "", branchLabels);
        var harmAxis   = new Axis("harmonic", harmVals,   "Hz");
        return new DataCube(new[] { branchAxis, harmAxis }, new Complex[branchLabels.Length * harmonics]);
    }

    private static DataCube MakeNodeHarmonicCube(string[] nodeLabels, int harmonics = 3)
    {
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        var nodeAxis = new Axis("node",     nodeVals, "", nodeLabels);
        var harmAxis = new Axis("harmonic", harmVals, "Hz");
        return new DataCube(new[] { nodeAxis, harmAxis }, new Complex[nodeLabels.Length * harmonics]);
    }

    private static DataCube MakeLabeledNodesCube(string[] labels)
    {
        var vals = Enumerable.Range(0, labels.Length).Select(i => (double)i).ToArray();
        return new DataCube(new[] { new Axis("label", vals, "", labels) }, new double[labels.Length]);
    }

    private static DataCube MakeProbeBranchesCube(string[] probes)
    {
        var vals = Enumerable.Range(0, probes.Length).Select(i => (double)i).ToArray();
        return new DataCube(new[] { new Axis("probe", vals, "", probes) }, new double[probes.Length]);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_vicard_{Guid.NewGuid():N}.npy");
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

    // ── 1. BranchPins_FilteredByProbeBranches ────────────────────────────────
    // I cube with branch labels ["Ids","M1:d"] + __ProbeBranches=["Ids"].
    // ShowAll=false → branch row PinOptions only contains "Ids".
    // ShowAll=true  → PinOptions includes "M1:d".

    [Fact]
    public async Task BranchPins_FilteredByProbeBranches()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "I", MakeBranchHarmonicCube(new[] { "Ids", "M1:d" }));
        ds.AddToGroup("HB1", "__ProbeBranches", MakeProbeBranchesCube(new[] { "Ids" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.I");

            // Filter ON by default (provenance present → ShowAll starts false).
            Assert.False(trvm.ShowAll);
            Assert.True(trvm.ShowAllToggleVisible);

            var branchRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "branch");
            Assert.NotNull(branchRow);
            Assert.True(branchRow.IsFilterableLabelAxis);

            // Filtered: only the IProbe branch is shown.
            Assert.Single(branchRow.PinOptions);
            Assert.Equal("Ids", branchRow.PinOptions[0]);

            // ShowAll=true reveals all branches.
            trvm.ShowAll = true;
            branchRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "branch");
            Assert.NotNull(branchRow);
            Assert.Equal(2, branchRow.PinOptions.Count);
            Assert.Contains("M1:d", branchRow.PinOptions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2. EyeRow_OnBranchAxis ───────────────────────────────────────────────
    // Branch axis row has IsFilterableLabelAxis=true; harmonic row has it false.
    // Same rule applies to node axis.

    [Fact]
    public async Task EyeRow_OnBranchAxis()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "I", MakeBranchHarmonicCube(new[] { "Ids", "M1:d" }));
        ds.AddToGroup("HB1", "__ProbeBranches", MakeProbeBranchesCube(new[] { "Ids" }));
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout", "Vdd" }));
        ds.AddToGroup("HB1", "__LabeledNodes", MakeLabeledNodesCube(new[] { "Vout" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Branch cube: branch row = filterable, harmonic row = not filterable.
            var iTravm    = BuildInspector(lib, path, "HB1.I");
            var iBranchRow = iTravm.AxisRoles.FirstOrDefault(r => r.AxisName == "branch");
            var iHarmRow   = iTravm.AxisRoles.FirstOrDefault(r => r.AxisName == "harmonic");
            Assert.NotNull(iBranchRow);
            Assert.NotNull(iHarmRow);
            Assert.True(iBranchRow.IsFilterableLabelAxis);
            Assert.False(iHarmRow.IsFilterableLabelAxis);

            // Node cube: node row = filterable, harmonic row = not filterable.
            var vTravm   = BuildInspector(lib, path, "HB1.V");
            var vNodeRow = vTravm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            var vHarmRow = vTravm.AxisRoles.FirstOrDefault(r => r.AxisName == "harmonic");
            Assert.NotNull(vNodeRow);
            Assert.NotNull(vHarmRow);
            Assert.True(vNodeRow.IsFilterableLabelAxis);
            Assert.False(vHarmRow.IsFilterableLabelAxis);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 3. SpecEdit_ResyncsCombos ─────────────────────────────────────────────
    // Trace on HB1.V; CommitSpec("HB1.I[:, 1]") → SelectedGroup stays HB1,
    // SelectedSignal.Label == "I", and axis rows are rebuilt for the I cube.

    [Fact]
    public async Task SpecEdit_ResyncsCombos()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout" }));
        ds.AddToGroup("HB1", "I", MakeBranchHarmonicCube(new[] { "Ids", "M1:d" }));
        ds.AddToGroup("HB1", "__LabeledNodes", MakeLabeledNodesCube(new[] { "Vout" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");
            Assert.Equal("HB1", trvm.SelectedGroup);
            Assert.Equal("V",   trvm.SelectedSignal?.Label);

            // Edit spec to the I cube (harmonic axis pinned at index 1).
            trvm.CommitSpec("HB1.I[:, 1]");

            Assert.Equal("HB1", trvm.SelectedGroup);
            Assert.Equal("I",   trvm.SelectedSignal?.Label);
            Assert.False(trvm.SelectedSignal?.IsAbsent);
            Assert.False(trvm.ShowEmptyQuantity);

            // Axis rows rebuilt for I cube (branch + harmonic).
            Assert.NotNull(trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "branch"));
            Assert.NotNull(trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "harmonic"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 4. SpecEdit_Invalid_BestEffort ───────────────────────────────────────
    // CommitSpec on an invalid expression sets SpecError but leaves combos alone.

    [Fact]
    public async Task SpecEdit_Invalid_BestEffort()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");
            string  groupBefore = trvm.SelectedGroup ?? "";
            string? labelBefore = trvm.SelectedSignal?.Label;

            // Invalid expression must not throw.
            var ex = Record.Exception(() => trvm.CommitSpec("mag(HB1.V) + bogus("));
            Assert.Null(ex);

            // Error is set.
            Assert.True(trvm.HasSpecError);

            // Group and item combos are unchanged.
            Assert.Equal(groupBefore, trvm.SelectedGroup);
            Assert.Equal(labelBefore, trvm.SelectedSignal?.Label);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. AnalysisGroup_OffersBothVI ────────────────────────────────────────
    // HB group with V only → picker still lists an absent I placeholder.
    // Selecting the absent I: ShowEmptyQuantity=true, correct message, no throw.

    [Fact]
    public async Task AnalysisGroup_OffersBothVI()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout" }));
        // No I cube in HB1.

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            // Both V and an absent I should appear (I is the synthesized absent placeholder).
            var vItem = trvm.AvailableSignals.FirstOrDefault(s => s.Label == "V");
            var iItem = trvm.AvailableSignals.FirstOrDefault(s => s.Label == "I");
            Assert.NotNull(vItem);
            Assert.NotNull(iItem);
            Assert.False(vItem.IsAbsent);
            Assert.True(iItem.IsAbsent);

            // Selecting the absent I must not throw.
            var ex = Record.Exception(() => { trvm.SelectedSignal = iItem; });
            Assert.Null(ex);

            Assert.True(trvm.ShowEmptyQuantity);
            Assert.Equal("No branch currents", trvm.EmptyQuantityMessage);
            Assert.False(trvm.ShowAxisRoles);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 6. V_NodeFilter_Unchanged ─────────────────────────────────────────────
    // Regression: node-axis filter behavior is unchanged after branch-filter generalization.

    [Fact]
    public async Task V_NodeFilter_Unchanged()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicCube(new[] { "Vout", "__internal" }));
        ds.AddToGroup("HB1", "__LabeledNodes", MakeLabeledNodesCube(new[] { "Vout" }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            // Filter ON: only labeled "Vout" appears in node pin options.
            Assert.False(trvm.ShowAll);
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.True(nodeRow.IsFilterableLabelAxis);
            Assert.Single(nodeRow.PinOptions);
            Assert.Equal("Vout", nodeRow.PinOptions[0]);

            // ShowAll=true reveals all nodes.
            trvm.ShowAll = true;
            nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Equal(2, nodeRow.PinOptions.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
