// ================================================================
//  NpyRoundTripGroupedTests.cs  —  Stage-1 gate: grouped DataSet round-trip
//
//  Verifies that a DataSet built via AddToGroup (three groups with
//  a deliberate cross-group name collision on "V") round-trips through
//  NpyWriter → NpyReader with group order, cube membership, axis
//  metadata, and numeric values all preserved exactly.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Engine.Tests.Export;

public class NpyRoundTripGroupedTests : IDisposable
{
    // ── Temp-file management ─────────────────────────────────────────────────

    private readonly List<string> _tempFiles = new();

    private string TempPath()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"crf_grouped_{Guid.NewGuid():N}.npy");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                try { File.Delete(f); } catch { }
    }

    // ── Build the shared test DataSet ─────────────────────────────────────────

    // HB1.V  [freq×node] Complex  — shape (4, 3)
    // HB1.I  [freq×node] Complex  — shape (4, 3)
    // SP1.V  [freq×node] Complex  — SAME name as HB1.V but different shape (5, 2)
    // SP1.S  [freq,i,j]  Complex  — shape (5, 2, 2)
    // measurements.Pout  Real scalar

    private static readonly double[] FreqVals4 =
        { 1e9, 2e9, 3e9, 4e9 };

    private static readonly double[] FreqVals5 =
        { 1e9, 2e9, 3e9, 4e9, 5e9 };

    private static readonly string[] NodeLabels3 =
        { "drain", "gate", "source" };

    private static readonly string[] NodeLabels2 =
        { "port1", "port2" };

    private static DataSet BuildGroupedDataSet()
    {
        var ds = new DataSet();

        // HB1.V — complex [4 freq × 3 node]
        var freqAxis4  = new Axis("freq", FreqVals4, "Hz");
        var nodeAxis3  = new Axis("node", new double[] { 1, 2, 3 }, "node", NodeLabels3);
        var hb1VData   = Enumerable.Range(0, 12)
            .Select(i => new Complex(i + 0.1, -(i + 0.2)))
            .ToArray();
        ds.AddToGroup("HB1", "V", new DataCube(new[] { freqAxis4, nodeAxis3 }, hb1VData));

        // HB1.I — complex [4 freq × 3 node]
        var hb1IData = Enumerable.Range(0, 12)
            .Select(i => new Complex(-(i + 1.5), i * 0.3))
            .ToArray();
        ds.AddToGroup("HB1", "I", new DataCube(new[] { freqAxis4, nodeAxis3 }, hb1IData));

        // SP1.V — complex [5 freq × 2 node]  (same cube name "V" as HB1, different shape)
        var freqAxis5  = new Axis("freq", FreqVals5, "Hz");
        var nodeAxis2  = new Axis("node", new double[] { 1, 2 }, "node", NodeLabels2);
        var sp1VData   = Enumerable.Range(0, 10)
            .Select(i => new Complex(i * 2.0, i * -0.5))
            .ToArray();
        ds.AddToGroup("SP1", "V", new DataCube(new[] { freqAxis5, nodeAxis2 }, sp1VData));

        // SP1.S — complex [5 freq × 2 i × 2 j]
        var portVals = new double[] { 1, 2 };
        var iAxis    = new Axis("i", portVals, "port");
        var jAxis    = new Axis("j", portVals, "port");
        var sp1SData = Enumerable.Range(0, 20)
            .Select(i => new Complex(Math.Cos(i * 0.3), Math.Sin(i * 0.3)))
            .ToArray();
        ds.AddToGroup("SP1", "S", new DataCube(new[] { freqAxis5, iAxis, jAxis }, sp1SData));

        // measurements.Pout — real scalar
        ds.AddToGroup("measurements", "Pout", DataCube.Scalar(23.7));

        return ds;
    }

    // ── Helper: import from freshly written path ──────────────────────────────

    private static (DataSet imported, string path) ExportAndImport(DataSet ds)
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"crf_grouped_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);
        return (imported, path);
    }

    // Override TempPath when using ExportAndImport to track for cleanup.
    private DataSet RoundTrip(DataSet ds)
    {
        var path = TempPath();
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);
        return imported;
    }

    // ── 1. Group order preserved ──────────────────────────────────────────────

    [Fact]
    public void Groups_OrderPreserved()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        Assert.Equal(new[] { "HB1", "SP1", "measurements" }, imported.Groups);
    }

    // ── 2. ContainsGroup + CubesIn key sets ───────────────────────────────────

    [Fact]
    public void ContainsGroup_AllGroupsPresent()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        Assert.True(imported.ContainsGroup("HB1"));
        Assert.True(imported.ContainsGroup("SP1"));
        Assert.True(imported.ContainsGroup("measurements"));
        Assert.False(imported.ContainsGroup("nonexistent"));
    }

    [Fact]
    public void CubesIn_KeySetsMatchPerGroup()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        Assert.Equal(new[] { "V", "I" }.OrderBy(x => x),
            imported.CubesIn("HB1").Keys.OrderBy(x => x));
        Assert.Equal(new[] { "V", "S" }.OrderBy(x => x),
            imported.CubesIn("SP1").Keys.OrderBy(x => x));
        Assert.Equal(new[] { "Pout" }.OrderBy(x => x),
            imported.CubesIn("measurements").Keys.OrderBy(x => x));
    }

    // ── 3. Per-cube DataKind, Rank, axes, and values ──────────────────────────

    [Fact]
    public void HB1_V_RoundTrip_Exact()
    {
        var orig     = BuildGroupedDataSet();
        var imported = RoundTrip(orig);

        var o = orig.CubesIn("HB1")["V"];
        var b = imported.CubesIn("HB1")["V"];

        Assert.Equal(DataKind.Complex, b.DataKind);
        Assert.Equal(2, b.Rank);
        Assert.Equal(o.Axes[0].Name,   b.Axes[0].Name);
        Assert.Equal(o.Axes[0].Unit,   b.Axes[0].Unit);
        Assert.Equal(o.Axes[1].Name,   b.Axes[1].Name);
        Assert.Equal(o.Axes[1].Unit,   b.Axes[1].Unit);
        Assert.Equal(o.Axes[1].Labels, b.Axes[1].Labels);

        var od = o.ComplexValues;
        var bd = b.ComplexValues;
        Assert.Equal(od.Length, bd.Length);
        for (int i = 0; i < od.Length; i++)
        {
            Assert.Equal(od[i].Real,      bd[i].Real);
            Assert.Equal(od[i].Imaginary, bd[i].Imaginary);
        }
    }

    [Fact]
    public void SP1_S_RoundTrip_Exact()
    {
        var orig     = BuildGroupedDataSet();
        var imported = RoundTrip(orig);

        var o = orig.CubesIn("SP1")["S"];
        var b = imported.CubesIn("SP1")["S"];

        Assert.Equal(DataKind.Complex, b.DataKind);
        Assert.Equal(3, b.Rank);
        Assert.Equal(o.Axes[0].Name, b.Axes[0].Name);
        Assert.Equal(o.Axes[1].Name, b.Axes[1].Name);
        Assert.Equal(o.Axes[2].Name, b.Axes[2].Name);

        var od = o.ComplexValues;
        var bd = b.ComplexValues;
        Assert.Equal(od.Length, bd.Length);
        for (int i = 0; i < od.Length; i++)
        {
            Assert.Equal(od[i].Real,      bd[i].Real);
            Assert.Equal(od[i].Imaginary, bd[i].Imaginary);
        }
    }

    [Fact]
    public void Measurements_Pout_RoundTrip_Exact()
    {
        var orig     = BuildGroupedDataSet();
        var imported = RoundTrip(orig);

        var o = orig.CubesIn("measurements")["Pout"];
        var b = imported.CubesIn("measurements")["Pout"];

        Assert.Equal(DataKind.Real, b.DataKind);
        Assert.Equal(0, b.Rank);
        Assert.Equal(o.RealValues[0], b.RealValues[0]);
    }

    // ── 4. Qualified resolution distinguishes same-named cubes ───────────────

    [Fact]
    public void QualifiedResolution_HB1_V_And_SP1_V_AreDistinct()
    {
        var imported = RoundTrip(BuildGroupedDataSet());

        var hb1V = imported["HB1.V"];
        var sp1V = imported["SP1.V"];

        // Different shapes (4×3 vs 5×2)
        Assert.Equal(4, hb1V.Axes[0].Length);
        Assert.Equal(3, hb1V.Axes[1].Length);
        Assert.Equal(5, sp1V.Axes[0].Length);
        Assert.Equal(2, sp1V.Axes[1].Length);

        // Values differ
        Assert.NotEqual(hb1V.ComplexValues[0].Real, sp1V.ComplexValues[0].Real);
    }

    [Fact]
    public void QualifiedResolution_SP1_S_Works()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        var s = imported["SP1.S"];
        Assert.Equal(3, s.Rank);
    }

    // ── 5. Bare resolution behaviour ──────────────────────────────────────────

    [Fact]
    public void Bare_V_IsAmbiguous_Across_Groups()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        // "V" exists in both HB1 and SP1 but not in the default group → ambiguous
        Assert.False(imported.Contains("V"));
        Assert.Throws<KeyNotFoundException>(() => _ = imported["V"]);
    }

    [Fact]
    public void Bare_S_IsAmbiguous_MultipleGroups()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        // "S" exists in SP1 only, but the default group is absent → multiple non-empty groups → ambiguous
        Assert.False(imported.Contains("S"));
        Assert.Throws<KeyNotFoundException>(() => _ = imported["S"]);
    }

    [Fact]
    public void Bare_Pout_Resolves_InMeasurementsGroup()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        // Bare "Pout" resolves to the measurements group even when other groups are present.
        Assert.True(imported.Contains("Pout"));
        var pout = imported["Pout"];
        Assert.Equal(0, pout.Rank);
        Assert.Equal(23.7, pout.RealValues[0]);
    }

    [Fact]
    public void Measurements_Pout_QualifiedPath_Works()
    {
        var imported = RoundTrip(BuildGroupedDataSet());
        // Qualified access measurements.Pout must still work alongside bare access.
        var pout = imported["measurements.Pout"];
        Assert.Equal(0, pout.Rank);
        Assert.Equal(23.7, pout.RealValues[0]);
    }

    [Fact]
    public void Bare_MeasurementCube_WhenOnlyMeasurementsGroup()
    {
        // A DataSet with only a measurements group (no default group, no other groups)
        // must bare-resolve measurement names.
        var ds = new DataSet();
        ds.AddToGroup("measurements", "Gain", DataCube.Scalar(12.5));
        var imported = RoundTrip(ds);
        Assert.True(imported.Contains("Gain"));
        Assert.Equal(12.5, imported["Gain"].RealValues[0]);
    }

    [Fact]
    public void FlatDataSet_BareContains_WorksWithSingleGroup()
    {
        // A default-group-only DataSet (Touchstone / single analysis) must still resolve bare names.
        var path = TempPath();
        var flat = new DataSet();
        var freqAxis = new Axis("freq", new double[] { 1e9, 2e9 }, "Hz");
        var portVals = new double[] { 1, 2 };
        var iAxis    = new Axis("i", portVals, "port");
        var jAxis    = new Axis("j", portVals, "port");
        flat.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis },
            new Complex[] { 1, 0, 0, 1, 1, 0, 0, 1 }));
        flat.Add("Z0", DataCube.Scalar(new Complex(50, 0)));

        DataSetExporter.Export(flat, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        // Bare lookup must work (sole group = default group "")
        Assert.True(imported.Contains("S"));
        Assert.True(imported.Contains("Z0"));

        var s = imported["S"];
        Assert.Equal(3, s.Rank);
    }
}
