// ================================================================
//  NpyRoundTripTests.cs  —  Phase 5-8 export → import round-trip oracle
//
//  Tests:
//  1. Basic round-trip: export DataSet → import → every cube's DataKind,
//     shape, axis names/units/values/labels, and numeric values are
//     bitwise-equal (same IEEE doubles).
//  2. Linnet round-trip: with IncludeLinearNetwork=true, the imported
//     ImportedLinearNetwork.GRows/GCols/GData/BSrc/INl/InterfaceNodes/
//     NodeNames/BranchNames all faithfully match what the payload
//     provided to the exporter (bitwise-equal for numeric data).
//  3. format_version reject: a file written without format_version
//     (synthetic) or with a wrong version throws InvalidDataException
//     with a clear message.
//
//  Uses Hero 2 as the reference circuit (same as DataSetExportTests).
//  All file-write tests use temp files deleted on cleanup.
//
//  See docs/design/data-file-format.md for the format specification.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Export;

public class NpyRoundTripTests(ITestOutputHelper output) : IDisposable
{
    // ── Shared Hero 2 HB run (cached) ────────────────────────────────────────

    private static HbRunResult? _result;
    private static readonly Lock _lock = new();

    private static HbRunResult GetHero2Result()
    {
        lock (_lock)
        {
            if (_result is not null) return _result;
            var dir     = FindHero2Dir();
            var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
            var netlist = new Elaborator(lib).Elaborate(tb);
            var hba     = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
            _result = new HbEngine(netlist, tb).Run(p);
            return _result;
        }
    }

    private static string FindHero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    // ── Temp-file management ─────────────────────────────────────────────────

    private readonly List<string> _tempFiles = new();

    private string TempPath(string ext = "npy")
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"circuitRF_roundtrip_{Guid.NewGuid():N}.{ext}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                try { File.Delete(f); } catch { /* best-effort */ }
    }

    // ── 1. Basic round-trip (no linnet) ──────────────────────────────────────

    [Fact]
    public void BasicRoundTrip_DataKindAndShapeMatch()
    {
        var result = GetHero2Result();
        var path   = TempPath();

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);
        var (imported, linnet) = DataSetImporter.Import(path);

        Assert.Null(linnet);  // no linnet fields without IncludeLinearNetwork

        foreach (var name in result.DataSet.Cubes.Keys)
        {
            Assert.True(imported.Contains(name), $"Imported DataSet missing cube '{name}'.");
            var orig = result.DataSet[name];
            var back = imported[name];

            Assert.Equal(orig.DataKind, back.DataKind);
            Assert.Equal(orig.Rank,     back.Rank);
            for (int d = 0; d < orig.Rank; d++)
                Assert.Equal(orig.Axes[d].Length, back.Axes[d].Length);
        }

        output.WriteLine($"  Cube count: {imported.Cubes.Count}");
    }

    [Fact]
    public void BasicRoundTrip_AxesMetadataExact()
    {
        var result = GetHero2Result();
        var path   = TempPath();

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in result.DataSet.Cubes.Keys)
        {
            var orig = result.DataSet[name];
            var back = imported[name];

            for (int d = 0; d < orig.Rank; d++)
            {
                var ao = orig.Axes[d];
                var ab = back.Axes[d];

                Assert.Equal(ao.Name, ab.Name);
                Assert.Equal(ao.Unit, ab.Unit);

                Assert.Equal(ao.Values.Length, ab.Values.Length);
                for (int i = 0; i < ao.Values.Length; i++)
                    Assert.Equal(ao.Values[i], ab.Values[i]);  // bitwise: G17 → parse round-trips exactly

                Assert.Equal(ao.Labels is null, ab.Labels is null);
                if (ao.Labels is not null)
                {
                    Assert.Equal(ao.Labels.Length, ab.Labels!.Length);
                    for (int i = 0; i < ao.Labels.Length; i++)
                        Assert.Equal(ao.Labels[i], ab.Labels[i]);
                }
            }
        }
    }

    [Fact]
    public void BasicRoundTrip_ComplexValuesAreBitwiseEqual()
    {
        var result = GetHero2Result();
        var path   = TempPath();

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in result.DataSet.Cubes.Keys)
        {
            var orig = result.DataSet[name];
            var back = imported[name];
            if (orig.DataKind != DataKind.Complex) continue;

            var origData = orig.ComplexValues;
            var backData = back.ComplexValues;
            Assert.Equal(origData.Length, backData.Length);

            for (int i = 0; i < origData.Length; i++)
            {
                Assert.Equal(origData[i].Real,      backData[i].Real);
                Assert.Equal(origData[i].Imaginary, backData[i].Imaginary);
            }
        }
    }

    [Fact]
    public void BasicRoundTrip_RealValuesAreBitwiseEqual()
    {
        var result = GetHero2Result();
        var path   = TempPath();

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in result.DataSet.Cubes.Keys)
        {
            var orig = result.DataSet[name];
            var back = imported[name];
            if (orig.DataKind != DataKind.Real) continue;

            var origData = orig.RealValues;
            var backData = back.RealValues;
            Assert.Equal(origData.Length, backData.Length);

            for (int i = 0; i < origData.Length; i++)
                Assert.Equal(origData[i], backData[i]);
        }
    }

    // ── 2. Linnet round-trip ─────────────────────────────────────────────────

    [Fact]
    public void LinnetRoundTrip_FieldsPresent()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        var path = TempPath();
        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true),
            result.LinearPayload);

        var (imported, linnet) = DataSetImporter.Import(path);

        Assert.NotNull(linnet);
        Assert.NotNull(linnet.Omegas);
        Assert.NotNull(linnet.GRows);
        Assert.NotNull(linnet.GCols);
        Assert.NotNull(linnet.GData);
        Assert.NotNull(linnet.BSrc);
        Assert.NotNull(linnet.INl);
        Assert.NotNull(linnet.InterfaceNodes);
        Assert.NotNull(linnet.NodeNames);
        Assert.NotNull(linnet.BranchNames);

        output.WriteLine($"  K+1={linnet.Omegas.Length}, nnz={linnet.GRows.Length}, " +
                         $"MnaSize={linnet.MnaSize}, NonGnd={linnet.NonGroundCount}");
        output.WriteLine($"  NodeNames=[{string.Join(", ", linnet.NodeNames)}]");
        output.WriteLine($"  BranchNames=[{string.Join(", ", linnet.BranchNames)}]");
    }

    [Fact]
    public void LinnetRoundTrip_DimensionsMatch()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        Assert.Equal(p.HarmonicCount,   linnet.Omegas.Length);
        Assert.Equal(p.NonGroundCount,  (int)linnet.NonGroundCount);
        Assert.Equal(p.MnaSize,         (int)linnet.MnaSize);
        Assert.Equal(p.InterfaceCount,  linnet.InterfaceNodes.Length);
        Assert.Equal(p.NodeNames.Length, linnet.NodeNames.Length);
        Assert.Equal(p.BranchNames.Length, linnet.BranchNames.Length);

        int K1 = p.HarmonicCount;
        // nnz = union of all harmonics — G(DC) has fewer nonzeros than G(fundamental+).
        var patternSet = new HashSet<(int, int)>();
        for (int k = 0; k < K1; k++) { var (r, c, _) = p.GetSparseG(k); for (int i = 0; i < r.Length; i++) patternSet.Add((r[i], c[i])); }
        int nnz = patternSet.Count;
        Assert.Equal(nnz, linnet.GRows.Length);
        Assert.Equal(nnz, linnet.GCols.Length);
        Assert.Equal(K1,  linnet.GData.GetLength(0));
        Assert.Equal(nnz, linnet.GData.GetLength(1));
    }

    [Fact]
    public void LinnetRoundTrip_OmegasExact()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        Assert.Equal(0.0, linnet.Omegas[0]);  // DC
        for (int k = 0; k < p.HarmonicCount; k++)
            Assert.Equal(p.Omegas[k], linnet.Omegas[k]);
    }

    [Fact]
    public void LinnetRoundTrip_GTripletsExact()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        // Build canonical union pattern (same sort as the writer: by row then col).
        var canonPairs = new SortedSet<(int, int)>(
            Comparer<(int, int)>.Create((a, b) =>
                a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2)));
        for (int k = 0; k < p.HarmonicCount; k++)
        {
            var (r, c, _) = p.GetSparseG(k);
            for (int i = 0; i < r.Length; i++) canonPairs.Add((r[i], c[i]));
        }
        var canon = canonPairs.ToArray();
        int nnz   = canon.Length;

        Assert.Equal(nnz, linnet.GRows.Length);
        for (int nz = 0; nz < nnz; nz++)
        {
            Assert.Equal(canon[nz].Item1, linnet.GRows[nz]);
            Assert.Equal(canon[nz].Item2, linnet.GCols[nz]);
        }

        // GData: each harmonic verified via lookup at each canonical (row, col).
        // Positions absent from a harmonic (e.g. capacitors at DC) should be zero.
        for (int k = 0; k < p.HarmonicCount; k++)
        {
            var (rk, ck, dk) = p.GetSparseG(k);
            var lookup = new Dictionary<(int, int), Complex>(rk.Length);
            for (int i = 0; i < rk.Length; i++) lookup[(rk[i], ck[i])] = dk[i];
            for (int nz = 0; nz < nnz; nz++)
            {
                var expected = lookup.GetValueOrDefault((linnet.GRows[nz], linnet.GCols[nz]));
                Assert.Equal(expected.Real,      linnet.GData[k, nz].Real);
                Assert.Equal(expected.Imaginary, linnet.GData[k, nz].Imaginary);
            }
        }
    }

    [Fact]
    public void LinnetRoundTrip_BSrcExact()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        int S = p.SweepCount, K1 = p.HarmonicCount, M = p.MnaSize;
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int m  = 0; m  < M; m++)
        {
            var expected = p.GetBSrc(si, k, m);
            var actual   = linnet.BSrc[si, k, m];
            Assert.Equal(expected.Real,      actual.Real);
            Assert.Equal(expected.Imaginary, actual.Imaginary);
        }
    }

    [Fact]
    public void LinnetRoundTrip_INlExact()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        // Writer stores INl[si, k, n] = GetINl(si, n, k) — note parameter order swap.
        int S = p.SweepCount, K1 = p.HarmonicCount, N = p.InterfaceCount;
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K1; k++)
        for (int n  = 0; n  < N; n++)
        {
            var expected = p.GetINl(si, n, k);
            var actual   = linnet.INl[si, k, n];
            Assert.Equal(expected.Real,      actual.Real);
            Assert.Equal(expected.Imaginary, actual.Imaginary);
        }
    }

    [Fact]
    public void LinnetRoundTrip_NameMapsExact()
    {
        var result = GetHero2Result();
        var p      = result.LinearPayload!;
        var path   = TempPath();

        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true), p);

        var (_, linnet) = DataSetImporter.Import(path);
        Assert.NotNull(linnet);

        Assert.Equal(p.NodeNames,   linnet.NodeNames);
        Assert.Equal(p.BranchNames, linnet.BranchNames);
        Assert.Equal(p.InterfaceNodes, linnet.InterfaceNodes);
    }

    // ── 3. format_version checks ─────────────────────────────────────────────

    [Fact]
    public void Import_WrongFormatVersion_Throws()
    {
        // Export a valid file, then patch the __meta__ to have a wrong version.
        // Easier: just write a minimal synthetic .npy with format_version = 999.
        var result = GetHero2Result();
        var path   = TempPath();
        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);

        // Read the file, patch format_version to 0, write a new file.
        var bytes       = File.ReadAllBytes(path);
        var patched     = PatchFormatVersionTo0(bytes);
        var patchedPath = TempPath();
        File.WriteAllBytes(patchedPath, patched);

        var ex = Assert.Throws<InvalidDataException>(() => DataSetImporter.Import(patchedPath));
        output.WriteLine($"  Expected error: {ex.Message}");
        Assert.Contains("format_version", ex.Message);
        Assert.Contains("0", ex.Message);  // found version
        Assert.Contains("not backward-compatible", ex.Message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Patch the <c>"format_version":1</c> bytes in a .npy file to <c>"format_version":0</c>.
    /// Both strings are the same byte length so the |S&lt;N&gt; dtype size in the header remains valid.
    /// </summary>
    private static byte[] PatchFormatVersionTo0(byte[] original)
    {
        // The __meta__ JSON field sits inside the binary file as raw UTF-8 bytes.
        // Search for "format_version":1 and replace the trailing '1' with '0'.
        byte[] needle = System.Text.Encoding.UTF8.GetBytes("\"format_version\":2");
        for (int i = 0; i <= original.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++)
                match = original[i + j] == needle[j];
            if (!match) continue;

            var patched = (byte[])original.Clone();
            patched[i + needle.Length - 1] = (byte)'0';  // replace '1' with '0'
            return patched;
        }
        throw new InvalidOperationException("format_version pattern not found in .npy file.");
    }
}
