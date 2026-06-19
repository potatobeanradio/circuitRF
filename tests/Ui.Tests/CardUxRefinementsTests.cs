// ================================================================
//  CardUxRefinementsTests.cs
//  Gate tests for brief-card-ux-refinements (F2)
//
//  1. ViSelector_FlagOnAnalysisGroup
//  2. PickerExpr_DropsTrivialColon
//  3. PickerExpr_KeepsRange
//  4. UserTypedColon_Respected
//  5. FirstAdd_RectComplex_Mag
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

public sealed class CardUxRefinementsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataCube MakeRank1Real(string axisName, int len = 5, string unit = "")
    {
        var vals = Enumerable.Range(0, len).Select(i => (double)i).ToArray();
        return new DataCube(new[] { new Axis(axisName, vals, unit) }, new double[len]);
    }

    private static DataCube MakeRank1Complex(string axisName, int len = 5, string unit = "")
    {
        var vals = Enumerable.Range(0, len).Select(i => (double)i).ToArray();
        return new DataCube(new[] { new Axis(axisName, vals, unit) }, new Complex[len]);
    }

    private static DataCube MakeNodeHarmonicComplex(string[] nodeLabels, int harmonics = 3)
    {
        var nodeVals = Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        return new DataCube(
            new[] { new Axis("node", nodeVals, "", nodeLabels), new Axis("harmonic", harmVals, "Hz") },
            new Complex[nodeLabels.Length * harmonics]);
    }

    private static DataCube MakeBranchHarmonicComplex(string[] branchLabels, int harmonics = 3)
    {
        var branchVals = Enumerable.Range(0, branchLabels.Length).Select(i => (double)i).ToArray();
        var harmVals   = Enumerable.Range(0, harmonics).Select(k => (double)k * 1e9).ToArray();
        return new DataCube(
            new[] { new Axis("branch", branchVals, "A", branchLabels), new Axis("harmonic", harmVals, "Hz") },
            new Complex[branchLabels.Length * harmonics]);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_cux_{Guid.NewGuid():N}.npy");
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

    // ── 1. ViSelector_FlagOnAnalysisGroup ────────────────────────────────────
    // HB1 group (items V/I) → IsViSelector == true.
    // Measurements group (PDC, Gain) → IsViSelector == false.

    [Fact]
    public async Task ViSelector_FlagOnAnalysisGroup()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeNodeHarmonicComplex(new[] { "Vout", "Vdd" }));
        ds.AddToGroup("HB1", "I", MakeBranchHarmonicComplex(new[] { "Ids" }));
        ds.Add("PDC",  MakeRank1Real("Pin"));
        ds.Add("Gain", MakeRank1Real("Pin"));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");

            // On the HB1 group (V/I) → icon-select is active
            trvm.SelectedGroup = "HB1";
            Assert.True(trvm.IsViSelector,
                "HB1 group with only V and I items must set IsViSelector = true.");

            // Switch to Signals group (PDC, Gain) → icon-select is off
            trvm.SelectedGroup = "Signals";
            Assert.False(trvm.IsViSelector,
                "Signals group with PDC/Gain items must set IsViSelector = false.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 2. PickerExpr_DropsTrivialColon ──────────────────────────────────────
    // A trace whose slice is a single whole-X axis → BuildPickerExpression()
    // returns the bare cube name, not "CubeName[:]".
    // With a transform, returns "mag(CubeName)" not "mag(CubeName[:])".

    [Fact]
    public void PickerExpr_DropsTrivialColon()
    {
        var snp = new SNP(new[] { 1e9 }, 2);

        // Single whole-X axis, no transform → must return bare name
        var t1 = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        t1.CubeName  = "PDC";
        t1.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        t1.Transform = CubeTransform.None;
        Assert.Equal("PDC", t1.BuildPickerExpression());
        Assert.NotEqual("PDC[:]", t1.BuildPickerExpression());

        // Single whole-X axis, mag() transform → must return "mag(PDC)"
        var t2 = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        t2.CubeName  = "PDC";
        t2.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        t2.Transform = CubeTransform.Mag;
        Assert.Equal("mag(PDC)", t2.BuildPickerExpression());
    }

    // ── 3. PickerExpr_KeepsRange ──────────────────────────────────────────────
    // A narrowed-range X (IsNarrowedRange == true) must NOT be collapsed.
    // BuildPickerExpression must still emit the bracket form.

    [Fact]
    public void PickerExpr_KeepsRange()
    {
        var snp = new SNP(new[] { 1e9 }, 2);

        // Narrowed range: RangeStart=1, RangeEndExclusive=4 → IsNarrowedRange == true
        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.CubeName  = "PDC";
        t.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0, RangeStart: 1, RangeEndExclusive: 4) };
        t.Transform = CubeTransform.None;

        string expr = t.BuildPickerExpression();

        // Must NOT be the bare name (range must be preserved)
        Assert.NotEqual("PDC", expr);

        // Must contain a bracket form
        Assert.Contains("[", expr);
    }

    // ── 4. UserTypedColon_Respected ──────────────────────────────────────────
    // CommitSpec("PDC[:]") stores the text verbatim in Expression;
    // SpecShorthand reflects the typed text, not the collapsed form.

    [Fact]
    public async Task UserTypedColon_Respected()
    {
        var ds = new DataSet();
        ds.Add("PDC", MakeRank1Real("Pin"));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "PDC");

            // CommitSpec stores the typed text verbatim via Expression
            trvm.CommitSpec("PDC[:]");

            // SpecShorthand reads Expression when set (CubeShorthand returns Expression first)
            Assert.Equal("PDC[:]", trvm.SpecShorthand);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. FirstAdd_RectComplex_Mag ───────────────────────────────────────────
    // Seeding a complex cube on PlotType.Rect → seed Transform == Mag, Expression starts with "mag(".
    // On PlotType.Smith → Transform == None (Smith handles complex natively).

    [Fact]
    public async Task FirstAdd_RectComplex_Mag()
    {
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", MakeRank1Complex("harmonic", len: 5, unit: "Hz"));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Rect plot: AddTraceCommand should seed with mag()
            var rectPlot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var rectInspector = new PlotInspectorViewModel(rectPlot, () => { }, lib);

            rectInspector.AddTraceCommand.Execute(null);

            Assert.Single(rectPlot.Traces);
            var rectTrace = rectPlot.Traces[0];
            Assert.True(rectTrace.IsCubeBound);
            Assert.Equal(CubeTransform.Mag, rectTrace.Transform);
            Assert.StartsWith("mag(", rectTrace.Expression ?? "");

            // Smith plot: AddTraceCommand should NOT force mag() (Smith handles complex)
            var smithPlot      = new Plot(PlotType.Smith, FreqUnit.GHz);
            var smithInspector = new PlotInspectorViewModel(smithPlot, () => { }, lib);

            smithInspector.AddTraceCommand.Execute(null);

            Assert.Single(smithPlot.Traces);
            var smithTrace = smithPlot.Traces[0];
            Assert.True(smithTrace.IsCubeBound);
            Assert.Equal(CubeTransform.None, smithTrace.Transform);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
