// ================================================================
//  DataExporterTests.cs  —  Gate tests for Part A of the Data Exporter
//
//  Tests:
//    1. MatWriter grouped writes format_version=2 + group structure
//    2. TsvWriter rank-3 complex: header + row count + spot-checked row
//    3. DataSetSubset.SelectGroups keeps only named groups
//    4. TouchstoneExporter: uniform-real Z0, single slice, round-trips
//    5. TouchstoneExporter: non-uniform Z0 → Renormalized==true
//    6. TouchstoneExporter: all-sweep 2-pt → 2 files written
//    7. TouchstoneExporter: name collision → NameCollision, no files written
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using PureHDF;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace RfCore.Tests.Export;

// ── Helpers ───────────────────────────────────────────────────────────────────

file sealed class TempFile(string ext) : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dex_{Guid.NewGuid():N}{ext}");

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

file sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dex_{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

// ── MatWriter grouped ────────────────────────────────────────────────────────

public sealed class MatWriterGroupedTests
{
    [Fact]
    public void MatWriter_Grouped_WritesFormatVersion2AndGroupSubkeys()
    {
        // Arrange — two-group DataSet: "HB1" + "measurements"
        var ds = new DataSet();

        var freqAxis   = new Axis("freq",  new double[] { 1e9, 2e9 }, "Hz");
        var nodeAxis   = new Axis("node",  new double[] { 1, 2 }, "");
        var cubePower  = new DataCube(new[] { freqAxis }, new Complex[] { new(1, 0), new(2, 0) });
        var cubeGain   = new DataCube(new[] { nodeAxis }, new double[] { 3.0, 4.0 });
        var cubeScalar = new DataCube(Array.Empty<Axis>(), new double[] { 5.0 });

        ds.AddToGroup("HB1", "V", cubePower);
        ds.AddToGroup("measurements", "Gain", cubeGain);
        ds.AddToGroup("measurements", "PAE",  cubeScalar);

        using var tmp = new TempFile(".mat");

        // Act
        DataSetExporter.Export(ds, tmp.Path, ExportFormat.Mat);

        // Assert — read back with PureHDF
        using var hf = H5File.OpenRead(tmp.Path);
        var dataset = hf.Group("dataset");

        // format_version should be 2
        var fv = dataset.Dataset("format_version").Read<long[]>();
        Assert.Equal(2L, fv[0]);

        // /dataset/HB1/V must exist
        var hb1 = dataset.Group("HB1");
        Assert.NotNull(hb1.Dataset("V"));

        // /dataset/measurements/Gain and PAE must exist
        var meas = dataset.Group("measurements");
        Assert.NotNull(meas.Dataset("Gain"));
        Assert.NotNull(meas.Dataset("PAE"));

        // /dataset/groups must list group names
        var groups = dataset.Dataset("groups").Read<string[]>();
        Assert.Contains("HB1", groups);
        Assert.Contains("measurements", groups);
    }
}

// ── TsvWriter ────────────────────────────────────────────────────────────────

public sealed class TsvWriterTests
{
    [Fact]
    public void TsvWriter_Rank3Complex_HeaderRowCountAndSpotCheck()
    {
        // Arrange — rank-3 complex cube [2, 3, 4]
        var a0 = new Axis("freq", new double[] { 1e9, 2e9 },       "Hz");
        var a1 = new Axis("i",    new double[] { 1, 2, 3 },         "port");
        var a2 = new Axis("j",    new double[] { 1, 2, 3, 4 },      "port");
        int total = 2 * 3 * 4;
        var data = new Complex[total];
        for (int k = 0; k < total; k++) data[k] = new Complex(k + 1, -(k + 1));
        var cube = new DataCube(new[] { a0, a1, a2 }, data);

        var ds = new DataSet();
        ds.Add("S", cube);

        using var tmp = new TempFile(".txt");

        // Act
        DataSetExporter.Export(ds, tmp.Path, ExportFormat.Tsv);

        // Assert
        string[] lines = File.ReadAllLines(tmp.Path)
            .Where(l => l.Length > 0)
            .ToArray();

        // First line = section header
        Assert.Equal("# S", lines[0]);

        // Second line = column headers: freq[Hz] \t i[port] \t j[port] \t re \t im
        Assert.Equal("freq[Hz]\ti[port]\tj[port]\tre\tim", lines[1]);

        // Should have 1 header + 1 section line + total data rows
        // (section + col-header + 24 data rows = 26 lines with blank stripped)
        Assert.Equal(1 + 1 + total, lines.Length);

        // Spot-check first data row: freq=1e9, i=1, j=1, re=1, im=-1
        string[] firstRow = lines[2].Split('\t');
        Assert.Equal(5, firstRow.Length);
        Assert.Equal((1e9).ToString("G17", CultureInfo.InvariantCulture), firstRow[0]);
        Assert.Equal((1.0).ToString("G17", CultureInfo.InvariantCulture), firstRow[1]);
        Assert.Equal((1.0).ToString("G17", CultureInfo.InvariantCulture), firstRow[2]);
        Assert.Equal((1.0).ToString("G17", CultureInfo.InvariantCulture), firstRow[3]);
        Assert.Equal((-1.0).ToString("G17", CultureInfo.InvariantCulture), firstRow[4]);
    }
}

// ── DataSetSubset ─────────────────────────────────────────────────────────────

public sealed class DataSetSubsetTests
{
    [Fact]
    public void SelectGroups_KeepsOnlyNamedGroups()
    {
        var ds = new DataSet();
        var cubeA = new DataCube(new[] { new Axis("x", new double[] { 1 }) }, new double[] { 1 });
        var cubeB = new DataCube(new[] { new Axis("x", new double[] { 1 }) }, new double[] { 2 });
        var cubeC = new DataCube(new[] { new Axis("x", new double[] { 1 }) }, new double[] { 3 });

        ds.AddToGroup("HB1",         "V",    cubeA);
        ds.AddToGroup("measurements","Gain", cubeB);
        ds.AddToGroup("SP1",         "S",    cubeC);

        // Only keep HB1 + measurements
        var subset = DataSetSubset.SelectGroups(ds, new[] { "HB1", "measurements" });

        Assert.True(subset.ContainsGroup("HB1"));
        Assert.True(subset.ContainsGroup("measurements"));
        Assert.False(subset.ContainsGroup("SP1"));

        Assert.Single(subset.CubesIn("HB1"));
        Assert.Single(subset.CubesIn("measurements"));
    }

    [Fact]
    public void SelectGroups_UnknownGroupsSkipped()
    {
        var ds = new DataSet();
        ds.AddToGroup("A", "x", new DataCube(new[] { new Axis("a", new double[] { 1 }) }, new double[] { 0 }));

        var subset = DataSetSubset.SelectGroups(ds, new[] { "A", "DoesNotExist" });

        Assert.True(subset.ContainsGroup("A"));
        Assert.False(subset.ContainsGroup("DoesNotExist"));
    }
}

// ── TouchstoneExporter ────────────────────────────────────────────────────────

public sealed class TouchstoneExporterTests
{
    // Helpers
    private static DataSet MakeSDataSet(int nPorts, int nFreq, Complex z0, double[]? sweepVals = null)
    {
        var ds = new DataSet();
        var portVals = Enumerable.Range(1, nPorts).Select(p => (double)p).ToArray();
        var freqVals = Enumerable.Range(1, nFreq).Select(k => k * 1e9).ToArray();

        Axis[] axes;
        Complex[] data;

        if (sweepVals == null)
        {
            axes = new[]
            {
                new Axis("freq", freqVals, "Hz"),
                new Axis("i", portVals, "port"),
                new Axis("j", portVals, "port"),
            };
            int total = nFreq * nPorts * nPorts;
            data = new Complex[total];
            for (int fi = 0; fi < nFreq; fi++)
            for (int i  = 0; i  < nPorts; i++)
            for (int j  = 0; j  < nPorts; j++)
                data[fi * nPorts * nPorts + i * nPorts + j] =
                    (i == j) ? new Complex(0.1 + fi * 0.01, 0.05) : new Complex(0.5, -0.1);
        }
        else
        {
            axes = new[]
            {
                new Axis("sweep", sweepVals, ""),
                new Axis("freq",  freqVals, "Hz"),
                new Axis("i",     portVals, "port"),
                new Axis("j",     portVals, "port"),
            };
            int nSwp  = sweepVals.Length;
            int total = nSwp * nFreq * nPorts * nPorts;
            data = new Complex[total];
            for (int si = 0; si < nSwp; si++)
            for (int fi = 0; fi < nFreq; fi++)
            for (int i  = 0; i  < nPorts; i++)
            for (int j  = 0; j  < nPorts; j++)
                data[((si * nFreq + fi) * nPorts + i) * nPorts + j] =
                    (i == j) ? new Complex(0.1 + si * 0.1 + fi * 0.01, 0.05) : new Complex(0.5, -0.1);
        }

        var sCube = new DataCube(axes, data);
        ds.AddToGroup("SP1", "S", sCube);

        var z0Vals = Enumerable.Repeat(z0, nPorts).ToArray();
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(z0Vals));

        return ds;
    }

    // ── Test 1: uniform-real Z0, single slice, round-trips ──────────────────

    [Fact]
    public void TouchstoneExporter_UniformRealZ0_SingleSlice_RoundTrips()
    {
        var ds   = MakeSDataSet(nPorts: 2, nFreq: 3, z0: new Complex(50, 0));
        var opts = new TouchstoneExportOptions(50.0, 12, 'f', MatrixFormat.MA);

        using var dir = new TempDir();
        string basePath = System.IO.Path.Combine(dir.Path, "out");

        var result = TouchstoneExporter.Export(
            ds, "SP1", opts,
            new Dictionary<string, int>(),
            allSweepFiles: false,
            basePath);

        Assert.Equal(TouchstoneExportStatus.Ok, result.Status);
        Assert.Single(result.WrittenPaths);
        Assert.False(result.Renormalized);

        // Read back and compare S[0,0] at each frequency
        string snpPath = result.WrittenPaths[0];
        Assert.True(File.Exists(snpPath));
        var snp = TouchstoneIO.ReadFile(snpPath);

        Assert.Equal(3, snp.FrequencyCount);
        Assert.Equal(2, snp.Ports);

        // Check round-trip: S[0,0] at freq 0 should match original
        var sCubes = ds.CubesIn("SP1");
        Assert.True(sCubes.TryGetValue("S", out var sCube));
        var original = sCube!.ComplexValues;
        double tol = 1e-4;
        Assert.True(Math.Abs(snp.Matrices[0][0, 0].Real - original[0].Real) < tol);
    }

    // ── Test 2: non-uniform Z0 → Renormalized == true ───────────────────────

    [Fact]
    public void TouchstoneExporter_NonUniformZ0_MarksRenormalized()
    {
        // Build dataset manually with non-uniform Z0 (different ports)
        var ds  = new DataSet();

        var portVals = new double[] { 1, 2 };
        var freqVals = new double[] { 1e9, 2e9 };
        var sCube = new DataCube(
            new[] { new Axis("freq", freqVals, "Hz"), new Axis("i", portVals, "port"), new Axis("j", portVals, "port") },
            new Complex[] { new(0.1, 0), new(0.5, 0), new(0.5, 0), new(0.1, 0),
                            new(0.2, 0), new(0.4, 0), new(0.4, 0), new(0.2, 0) });
        // Non-uniform Z0: port 1 = 50 Ω, port 2 = 75 Ω
        var z0Cube = DataSetBuilder.BuildZ0Cube(new Complex[] { new(50, 0), new(75, 0) });
        ds.AddToGroup("SP1", "S",  sCube);
        ds.AddToGroup("SP1", "Z0", z0Cube);

        var opts = new TouchstoneExportOptions(50.0, 10, 'f', MatrixFormat.MA);
        using var dir = new TempDir();
        string basePath = System.IO.Path.Combine(dir.Path, "out");

        var result = TouchstoneExporter.Export(
            ds, "SP1", opts,
            new Dictionary<string, int>(),
            allSweepFiles: false,
            basePath);

        Assert.Equal(TouchstoneExportStatus.Ok, result.Status);
        Assert.Equal(Z0Kind.NonUniform, result.SourceZ0Kind);
        Assert.True(result.Renormalized);
    }

    // ── Test 3: all-sweep 2-pt sweep → 2 files ──────────────────────────────

    [Fact]
    public void TouchstoneExporter_AllSweepFiles_TwoPointSweep_WritesTwoFiles()
    {
        var sweepVals = new double[] { 1.0, 2.0 };
        var ds   = MakeSDataSet(nPorts: 2, nFreq: 2, z0: new Complex(50, 0), sweepVals: sweepVals);
        var opts = new TouchstoneExportOptions(50.0, 10, 'f', MatrixFormat.MA);

        using var dir = new TempDir();
        string basePath = System.IO.Path.Combine(dir.Path, "sweep");

        var result = TouchstoneExporter.Export(
            ds, "SP1", opts,
            new Dictionary<string, int>(),
            allSweepFiles: true,
            basePath);

        Assert.Equal(TouchstoneExportStatus.Ok, result.Status);
        Assert.Equal(2, result.WrittenPaths.Count);
        foreach (var p in result.WrittenPaths)
            Assert.True(File.Exists(p));
    }

    // ── Test 4: name collision → NameCollision, no files written ─────────────

    [Fact]
    public void TouchstoneExporter_NameCollision_WritesNothing()
    {
        // Build S cube with sweep axis whose values sanitize to the same string
        // Labels ["a/b", "a\\b"] both sanitize to "ab" → collision
        var ds  = new DataSet();
        int nP  = 1;
        int nFr = 2;

        var freqVals   = new double[] { 1e9, 2e9 };
        var sweepVals  = new double[] { 1.0, 2.0 };
        var sweepLabels = new string[] { "a/b", "a\\b" };
        var portVals   = new double[] { 1 };

        var sweepAxis = new Axis("param", sweepVals, "", labels: sweepLabels);
        var freqAxis  = new Axis("freq",  freqVals, "Hz");
        var iAxis     = new Axis("i",     portVals, "port");
        var jAxis     = new Axis("j",     portVals, "port");

        int total = 2 * nFr * nP * nP;
        var data  = Enumerable.Repeat(new Complex(0.5, 0), total).ToArray();
        var sCube = new DataCube(new[] { sweepAxis, freqAxis, iAxis, jAxis }, data);
        var z0Cube = DataSetBuilder.BuildZ0Cube(new Complex[] { new(50, 0) });

        ds.AddToGroup("SP1", "S",  sCube);
        ds.AddToGroup("SP1", "Z0", z0Cube);

        var opts = new TouchstoneExportOptions(50.0, 10, 'f', MatrixFormat.MA);
        using var dir = new TempDir();
        string basePath = System.IO.Path.Combine(dir.Path, "out");

        var result = TouchstoneExporter.Export(
            ds, "SP1", opts,
            new Dictionary<string, int>(),
            allSweepFiles: true,
            basePath);

        Assert.Equal(TouchstoneExportStatus.NameCollision, result.Status);
        Assert.Empty(result.WrittenPaths);
        Assert.NotEmpty(result.CollidingNames);
        // Verify nothing was written
        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}
