// ================================================================
//  Z0IndicatorTests.cs  —  Phase 7.2e gate tests
//
//  1. Entry_ClassifiesUnusualZ0   — DataSourceEntryViewModel classifies Z0 from DataSet
//  2. Badge_OnlyOnScatteringTrace — ShowZ0Badge only on S-kind traces from unusual-Z0 sources
//  3. Warning_FiresOncePerSource  — UnusualZ0Detected fires exactly once per source path
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class Z0IndicatorTests
{
    // ---- Helpers ------------------------------------------------------------

    private static DataSet MakeUniformRealDataSet(double z0 = 50)
    {
        // 1-port S DataSet with uniform-real Z0.
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0 }, "port");
        var sData    = new Complex[] { new(0.1, 0.0), new(0.2, 0.0) };
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, sData));
        // Build a uniform-real Z0 cube (1 port, value = z0).
        var z0Cube = DataSetBuilder.BuildZ0Cube(new[] { new Complex(z0, 0) });
        ds.Add("Z0", z0Cube);
        return ds;
    }

    private static DataSet MakeNonUniformZ0DataSet()
    {
        // 2-port S DataSet with non-uniform Z0 (port1=50Ω, port2=75Ω).
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0, 2.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0, 2.0 }, "port");
        var sData    = new Complex[8];
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, sData));
        var z0Cube = DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0), new Complex(75, 0) });
        ds.Add("Z0", z0Cube);
        return ds;
    }

    private static DataSet MakeComplexZ0DataSet()
    {
        // 1-port S DataSet with complex Z0.
        var freqAxis = new Axis("freq", new[] { 1e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0 }, "port");
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, new Complex[] { new(0.1, 0) }));
        var z0Cube = DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, -10) });
        ds.Add("Z0", z0Cube);
        return ds;
    }

    // ---- Test 1 -------------------------------------------------------------

    [Fact]
    public void Entry_ClassifiesUnusualZ0()
    {
        var lib = new DataSourceLibraryViewModel();

        // Non-uniform Z0 → HasUnusualZ0 true, Z0PerPort contains both ports.
        var nonUniformDs = MakeNonUniformZ0DataSet();
        var snpNu = DataSetBuilder.ToSnp(nonUniformDs);
        snpNu.FilePath = "/tmp/nonuniform.npy";
        var entryNu = new DataSourceEntryViewModel("/tmp/nonuniform.npy", nonUniformDs, snpNu, lib);

        Assert.True(entryNu.HasUnusualZ0,    "non-uniform Z0 must set HasUnusualZ0");
        Assert.Equal(Z0Kind.NonUniform, entryNu.Z0Kind);
        Assert.Equal(2, entryNu.Z0PerPort.Count);
        Assert.Equal(new Complex(50, 0), entryNu.Z0PerPort[0]);
        Assert.Equal(new Complex(75, 0), entryNu.Z0PerPort[1]);

        // Uniform-real Z0 → HasUnusualZ0 false.
        var uniformDs = MakeUniformRealDataSet(50);
        var snpUr = DataSetBuilder.ToSnp(uniformDs);
        snpUr.FilePath = "/tmp/uniform.npy";
        var entryUr = new DataSourceEntryViewModel("/tmp/uniform.npy", uniformDs, snpUr, lib);

        Assert.False(entryUr.HasUnusualZ0, "uniform-real Z0 must NOT set HasUnusualZ0");
        Assert.Equal(Z0Kind.UniformReal, entryUr.Z0Kind);
    }

    // ---- Test 2 -------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Badge_OnlyOnScatteringTrace()
    {
        var tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0badge_{Guid.NewGuid():N}.npy");
        try
        {
            // Write a non-uniform Z0 .npy file to disk so LoadFileAsync can read it.
            var ds = MakeNonUniformZ0DataSet();
            RfCore.Export.DataSetExporter.Export(ds, tmpPath, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);
            await lib.SelectDataSourceAsync(tmpPath);
            var entry = lib.Entries.Single();

            Assert.True(entry.HasUnusualZ0, "pre-condition: entry must have unusual Z0");

            var snp  = entry.Snp!;
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            // S-parameter trace bound to this SNP.
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row       = inspector.Traces[0];

            // S-trace from unusual-Z0 source → badge shown.
            Assert.True(row.ShowZ0Badge, "S-trace from unusual-Z0 source must show Z0 badge");
            Assert.Contains("non-uniform", row.Z0BadgeTooltip,
                StringComparison.OrdinalIgnoreCase);

            // Switch to Y-parameter view → badge hidden (not an S trace).
            row.MatrixType = MatrixType.Y;
            Assert.False(row.ShowZ0Badge, "Y-trace must NOT show Z0 badge");
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
        }
    }

    // ---- Test 3 -------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task Warning_FiresOncePerSource()
    {
        var tmpA = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0warn_a_{Guid.NewGuid():N}.npy");
        var tmpB = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0warn_b_{Guid.NewGuid():N}.npy");
        try
        {
            // Source A: non-uniform Z0.
            var dsA = MakeNonUniformZ0DataSet();
            RfCore.Export.DataSetExporter.Export(dsA, tmpA, RfCore.Export.ExportFormat.Npy);

            // Source B: complex Z0.
            var dsB = MakeComplexZ0DataSet();
            RfCore.Export.DataSetExporter.Export(dsB, tmpB, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            int warnCount = 0;
            lib.UnusualZ0Detected += (_, _, _) => warnCount++;

            // Load source A once — event fires once.
            await lib.LoadFileAsync(tmpA);
            await lib.SelectDataSourceAsync(tmpA);
            Assert.Equal(1, warnCount);

            // Reload source A (simulate auto-refresh) — event must NOT fire again.
            await lib.ReloadAsync(lib.Entries.Single(e =>
                string.Equals(e.FilePath, tmpA, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, warnCount);

            // Remove A and re-add — cleared path so must fire again.
            var entryA = lib.Entries.Single(e =>
                string.Equals(e.FilePath, tmpA, StringComparison.OrdinalIgnoreCase));
            lib.Remove(entryA);
            await lib.LoadFileAsync(tmpA);
            await lib.SelectDataSourceAsync(tmpA);
            Assert.Equal(2, warnCount);

            // Load source B — distinct path, fires once more.
            await lib.LoadFileAsync(tmpB);
            await lib.SelectDataSourceAsync(tmpB);
            Assert.Equal(3, warnCount);
        }
        finally
        {
            if (System.IO.File.Exists(tmpA)) System.IO.File.Delete(tmpA);
            if (System.IO.File.Exists(tmpB)) System.IO.File.Delete(tmpB);
        }
    }
}
