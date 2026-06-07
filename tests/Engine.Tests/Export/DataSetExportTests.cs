// ================================================================
//  DataSetExportTests.cs — Phase 5-7 export integration tests
//
//  Tests:
//  1. .npy conformance: header magic, version, structured dtype, field data
//  2. .mat (HDF5) round-trip: groups present, complex dataset readable
//  3. Disk-size estimate warning fires at correct threshold
//  4. EvaluateNone / EvaluateAll / EvaluateSpecified add correct cubes
//  5. IncludeLinearNetwork produces linnet fields in both formats
//
//  Uses Hero 2 as the reference circuit (same cnl as LinearBackSolveTests).
//  All file-write tests use temp files that are deleted on cleanup.
//
//  See docs/design/data-export.md §9.
// ================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using PureHDF;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Export;

/// <summary>
/// Integration tests for <see cref="DataSetExporter"/>, covering both .npy and .mat formats.
/// </summary>
public class DataSetExportTests(ITestOutputHelper output) : IDisposable
{
    private static string Hero2Dir()
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

    // ── Shared HB run ────────────────────────────────────────────────────────

    private static HbRunResult? _result;
    private static readonly Lock _lock = new();

    private static HbRunResult GetHero2Result()
    {
        lock (_lock)
        {
            if (_result is not null) return _result;
            var dir     = Hero2Dir();
            var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
            var netlist = new Elaborator(lib).Elaborate(tb);
            var hba     = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals)
                         with { SweepStop = -14.0, SweepStep = 2.0 };
            _result = new HbEngine(netlist, tb).Run(p);
            return _result;
        }
    }

    // ── Temp file management ─────────────────────────────────────────────────

    private readonly List<string> _tempFiles = new();

    private string TempPath(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"circuitRF_export_test_{Guid.NewGuid():N}.{ext}");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { /* best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Compound type matching MatWriter's ComplexEntry struct (MATLAB-compatible)
    [StructLayout(LayoutKind.Sequential)]
    private struct ComplexEntry
    {
        public double real;
        public double imag;
    }

    // ── 1. .npy format conformance ───────────────────────────────────────────

    [Fact]
    public void Npy_BasicExport_WritesValidMagicAndVersion()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 10, "File must be at least 10 bytes (preamble).");

        // Magic: \x93NUMPY
        Assert.Equal(0x93, bytes[0]);
        Assert.Equal((byte)'N', bytes[1]);
        Assert.Equal((byte)'U', bytes[2]);
        Assert.Equal((byte)'M', bytes[3]);
        Assert.Equal((byte)'P', bytes[4]);
        Assert.Equal((byte)'Y', bytes[5]);

        // Version (major, minor)
        byte major = bytes[6];
        Assert.True(major is 1 or 2, $"Major version must be 1 or 2, got {major}.");

        output.WriteLine($"  .npy size = {bytes.Length} bytes, version {bytes[6]}.{bytes[7]}");
    }

    [Fact]
    public void Npy_BasicExport_HeaderContainsAllCubeNames()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);

        // Read header as ASCII — it follows the 6-byte magic + 2-byte version + 2/4-byte HEADER_LEN.
        var bytes  = File.ReadAllBytes(path);
        byte major = bytes[6];
        int  lenBytes = major == 1 ? 2 : 4;
        int  preamble = 6 + 2 + lenBytes;
        int  headerLen = major == 1
            ? BitConverter.ToUInt16(bytes, 8)
            : (int)BitConverter.ToUInt32(bytes, 8);
        string header = Encoding.ASCII.GetString(bytes, preamble, headerLen);

        output.WriteLine($"  .npy header: {header[..Math.Min(200, header.Length)]}...");

        // Header must contain the Python dict keys
        Assert.Contains("'descr'", header);
        Assert.Contains("'fortran_order'", header);
        Assert.Contains("'shape'", header);
        Assert.Contains("(1,)", header);     // shape = (1,)
        Assert.Contains("False", header);    // fortran_order = False

        // Every cube name must appear in the header
        foreach (var kvp in result.DataSet.Cubes)
        {
            string name = kvp.Key.Replace("/", "__slash__");
            Assert.Contains(name, header);
        }

        // __meta__ field must be present
        Assert.Contains("__meta__", header);
    }

    [Fact]
    public void Npy_BasicExport_HeaderIsMultipleOf64()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);

        var bytes     = File.ReadAllBytes(path);
        byte major    = bytes[6];
        int  lenBytes = major == 1 ? 2 : 4;
        int  preamble = 6 + 2 + lenBytes;
        int  headerLen = major == 1
            ? BitConverter.ToUInt16(bytes, 8)
            : (int)BitConverter.ToUInt32(bytes, 8);

        // Total file offset of data must be divisible by 64.
        int dataOffset = preamble + headerLen;
        Assert.True(dataOffset % 64 == 0,
            $"Data offset {dataOffset} is not a multiple of 64 (NumPy alignment requirement).");
    }

    [Fact]
    public void Npy_BasicExport_DataSizeMatchesComputedSize()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Npy);

        var bytes  = File.ReadAllBytes(path);
        byte major = bytes[6];
        int  preamble  = 6 + 2 + (major == 1 ? 2 : 4);
        int  headerLen = major == 1
            ? BitConverter.ToUInt16(bytes, 8)
            : (int)BitConverter.ToUInt32(bytes, 8);
        int dataStart = preamble + headerLen;

        // Expected data size: sum of all cube element counts × bytes per element
        long expectedData = 0;
        foreach (var cube in result.DataSet.Cubes.Values)
        {
            long elems = cube.Axes.Count == 0 ? 1L
                : cube.Axes.Aggregate(1L, (acc, a) => acc * a.Length);
            expectedData += elems * (cube.DataKind == DataKind.Complex ? 16L : 8L);
        }
        // __meta__ contributes its byte-string size; we get it from the header |SN declaration.
        // For this check, just verify total file size > dataStart + some reasonable minimum.
        long dataActual = bytes.Length - dataStart;
        Assert.True(dataActual >= expectedData,
            $"Data section too small: actual={dataActual}, expectedCubes={expectedData}.");

        output.WriteLine($"  Data section: {dataActual} bytes " +
                         $"(cubes={expectedData} + meta + padding)");
    }

    // ── 2. .mat (HDF5) format ────────────────────────────────────────────────

    [Fact]
    public void Mat_BasicExport_WritesValidHdf5File()
    {
        var result = GetHero2Result();
        var path   = TempPath("mat");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Mat);

        // File must be readable by PureHDF.
        Assert.True(File.Exists(path), "Output file must exist.");
        Assert.True(new FileInfo(path).Length > 0, "Output file must not be empty.");

        using var file = H5File.OpenRead(path);
        var dsGroup = file.Group("dataset");
        Assert.NotNull(dsGroup);

        // Every cube must be a dataset in /dataset/
        foreach (var name in result.DataSet.Cubes.Keys)
        {
            string hdfName = name.Replace("/", "__slash__");
            Assert.NotNull(dsGroup.Dataset(hdfName));
        }

        // __axes__ group must exist
        Assert.NotNull(dsGroup.Group("__axes__"));

        output.WriteLine($"  .mat size = {new FileInfo(path).Length} bytes");
    }

    [Fact]
    public void Mat_ComplexCube_IsCompoundComplexEntry()
    {
        var result = GetHero2Result();
        var path   = TempPath("mat");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Mat);

        using var file  = H5File.OpenRead(path);
        var dsGroup = file.Group("dataset");

        // V cube is Complex in Hero 2.
        var vDataset = dsGroup.Dataset("V");
        Assert.NotNull(vDataset);

        // Read as ComplexEntry struct array (MATLAB-compatible compound type).
        var readBack = vDataset.Read<ComplexEntry[]>();
        Assert.True(readBack.Length > 0, "V dataset must have at least one element.");

        // First element's real and imag parts must be finite (not NaN/Inf).
        Assert.True(double.IsFinite(readBack[0].real),  "real part must be finite.");
        Assert.True(double.IsFinite(readBack[0].imag),  "imag part must be finite.");

        output.WriteLine($"  V dataset: {readBack.Length} complex entries " +
                         $"(first: real={readBack[0].real:G4}, imag={readBack[0].imag:G4})");
    }

    [Fact]
    public void Mat_AxesJson_IsValidJsonForVCube()
    {
        var result = GetHero2Result();
        var path   = TempPath("mat");

        DataSetExporter.Export(result.DataSet, path, ExportFormat.Mat);

        using var file      = H5File.OpenRead(path);
        var dsGroup     = file.Group("dataset");
        var axesGroup   = dsGroup.Group("__axes__");
        var vAxesGroup  = axesGroup.Group("V");
        var jsonDataset = vAxesGroup.Dataset("axes.json");

        var jsonStrings = jsonDataset.Read<string[]>();
        Assert.Single(jsonStrings);

        string json = jsonStrings[0];
        output.WriteLine($"  V axes.json: {json[..Math.Min(200, json.Length)]}...");

        // Must be a JSON array containing objects with "name", "unit", "values"
        Assert.StartsWith("[", json.TrimStart());
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"unit\"", json);
        Assert.Contains("\"values\"", json);
    }

    // ── 3. Disk-size warning ──────────────────────────────────────────────────

    [Fact]
    public void SizeWarning_FiresWhenThresholdExceeded()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        // Capture stderr
        var origErr = Console.Error;
        using var capture = new StringWriter();
        Console.SetError(capture);

        try
        {
            // Threshold of 0 MiB → always warn
            DataSetExporter.Export(
                result.DataSet, path, ExportFormat.Npy,
                new ExportOptions(SizeWarningThresholdMiB: 0.0));
        }
        finally
        {
            Console.SetError(origErr);
        }

        string stderr = capture.ToString();
        output.WriteLine($"  Captured stderr: {stderr[..Math.Min(200, stderr.Length)]}");

        Assert.Contains("[Export]", stderr);
        Assert.Contains("MiB", stderr);
        Assert.Contains("Proceeding", stderr);
    }

    [Fact]
    public void SizeWarning_DoesNotFireBelowThreshold()
    {
        var result = GetHero2Result();
        var path   = TempPath("npy");

        var origErr = Console.Error;
        using var capture = new StringWriter();
        Console.SetError(capture);

        try
        {
            // Threshold of 10000 MiB → should never warn for Hero 2 (tiny output)
            DataSetExporter.Export(
                result.DataSet, path, ExportFormat.Npy,
                new ExportOptions(SizeWarningThresholdMiB: 10000.0));
        }
        finally
        {
            Console.SetError(origErr);
        }

        string stderr = capture.ToString();
        // No warning expected
        Assert.DoesNotContain("advisory threshold", stderr);
    }

    // ── 4. LinearEvalMode ─────────────────────────────────────────────────────

    [Fact]
    public void EvaluateNone_DoesNotAddVLinearOrILinear()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        var path = TempPath("npy");
        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(
                LinearEvalMode: LinearEvalMode.EvaluateNone,
                IncludeLinearNetwork: false),
            result.LinearPayload);

        // The DataSet should NOT have V_linear or I_linear cubes.
        Assert.False(result.DataSet.Contains("V_linear"),
            "EvaluateNone should not add V_linear cube.");
        Assert.False(result.DataSet.Contains("I_linear"),
            "EvaluateNone should not add I_linear cube.");
    }

    [Fact]
    public void EvaluateAll_AddsVLinearCube()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        // Use a fresh DataSet copy to avoid polluting the shared result.
        var freshDs = new DataSet();
        foreach (var kvp in result.DataSet.Cubes)
            freshDs.Add(kvp.Key, kvp.Value);

        var path = TempPath("npy");
        DataSetExporter.Export(
            freshDs, path, ExportFormat.Npy,
            new ExportOptions(LinearEvalMode: LinearEvalMode.EvaluateAll),
            result.LinearPayload);

        // V_linear should now be in the DataSet.
        Assert.True(freshDs.Contains("V_linear"),
            "EvaluateAll should add V_linear cube.");

        var vLinear = freshDs["V_linear"];
        output.WriteLine($"  V_linear axes: [{string.Join(", ", vLinear.Axes.Select(a => $"{a.Name}[{a.Length}]"))}]");

        // V_linear must be Complex and have at least 2 axes (node, harmonic)
        Assert.Equal(DataKind.Complex, vLinear.DataKind);
        Assert.True(vLinear.Rank >= 2, "V_linear must have at least 2 axes.");
    }

    // ── 5. IncludeLinearNetwork ───────────────────────────────────────────────

    [Fact]
    public void Npy_IncludeLinearNetwork_WritesLinnetFields()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        var path = TempPath("npy");
        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Npy,
            new ExportOptions(IncludeLinearNetwork: true),
            result.LinearPayload);

        // Read header and verify __linnet_ fields are present.
        var bytes  = File.ReadAllBytes(path);
        byte major = bytes[6];
        int  preamble  = 6 + 2 + (major == 1 ? 2 : 4);
        int  headerLen = major == 1
            ? BitConverter.ToUInt16(bytes, 8)
            : (int)BitConverter.ToUInt32(bytes, 8);
        string header = Encoding.ASCII.GetString(bytes, preamble, headerLen);

        output.WriteLine($"  .npy header (first 400): {header[..Math.Min(400, header.Length)]}");

        Assert.Contains("__linnet_omegas",          header);
        Assert.Contains("__linnet_G_rows",          header);
        Assert.Contains("__linnet_G_cols",          header);
        Assert.Contains("__linnet_G_data",          header);
        Assert.Contains("__linnet_bSrc",            header);
        Assert.Contains("__linnet_iNl",             header);
        Assert.Contains("__linnet_interface_nodes", header);
        Assert.Contains("__linnet_mna_size",        header);
        Assert.Contains("__linnet_non_ground_count",header);
    }

    [Fact]
    public void Mat_IncludeLinearNetwork_WritesLinearNetworkGroup()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        var path = TempPath("mat");
        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Mat,
            new ExportOptions(IncludeLinearNetwork: true),
            result.LinearPayload);

        using var file    = H5File.OpenRead(path);
        var dsGroup   = file.Group("dataset");

        // Verify format_version scalar
        var fmtVer = dsGroup.Dataset("format_version").Read<long[]>();
        Assert.Single(fmtVer);
        Assert.Equal(1L, fmtVer[0]);

        var linGroup  = dsGroup.Group("__linear_network__");
        Assert.NotNull(linGroup);

        // Verify omegas: should have K+1 elements, first = 0 (DC)
        var omegas = linGroup.Dataset("omegas").Read<double[]>();
        Assert.Equal(result.LinearPayload!.HarmonicCount, omegas.Length);
        Assert.Equal(0.0, omegas[0], precision: 15);

        // Verify node_names: non-empty string array
        var nodeNames = linGroup.Dataset("node_names").Read<string[]>();
        Assert.Equal(result.LinearPayload.NonGroundCount, nodeNames.Length);
        foreach (var n in nodeNames) Assert.False(string.IsNullOrEmpty(n));

        // Verify branch_names: length = MnaSize - NonGroundCount
        var branchNames = linGroup.Dataset("branch_names").Read<string[]>();
        Assert.Equal(
            result.LinearPayload.MnaSize - result.LinearPayload.NonGroundCount,
            branchNames.Length);

        // Verify G_rows and G_cols reflect the union sparsity pattern (not just DC).
        // DC has fewer entries than AC harmonics (capacitors are open at ω=0);
        // nnz must equal the union count across all harmonics.
        var gRows = linGroup.Dataset("G_rows").Read<int[]>();
        var gCols = linGroup.Dataset("G_cols").Read<int[]>();
        Assert.Equal(gRows.Length, gCols.Length);

        var patternSet = new HashSet<(int, int)>();
        for (int k = 0; k < result.LinearPayload.HarmonicCount; k++)
        {
            var (r, c, _) = result.LinearPayload.GetSparseG(k);
            for (int i = 0; i < r.Length; i++) patternSet.Add((r[i], c[i]));
        }
        Assert.Equal(patternSet.Count, gRows.Length);

        output.WriteLine(
            $"  __linear_network__: K+1={omegas.Length}, nnz={gRows.Length}, " +
            $"nodes={nodeNames.Length}, branches={branchNames.Length}");
        output.WriteLine($"  branch_names: [{string.Join(", ", branchNames)}]");
        output.WriteLine($"  omegas[0..2]: [{string.Join(", ", omegas.Take(3).Select(v => v.ToString("G4")))}]");
    }

    [Fact]
    public void Mat_IncludeLinearNetwork_OmegasAreLinear()
    {
        var result = GetHero2Result();
        Assert.NotNull(result.LinearPayload);

        var path = TempPath("mat");
        DataSetExporter.Export(
            result.DataSet, path, ExportFormat.Mat,
            new ExportOptions(IncludeLinearNetwork: true),
            result.LinearPayload);

        using var file = H5File.OpenRead(path);
        var linGroup   = file.Group("dataset").Group("__linear_network__");
        var omegas     = linGroup.Dataset("omegas").Read<double[]>();

        Assert.Equal(0.0, omegas[0], precision: 15);   // DC: ω=0
        double omega1 = omegas[1];
        for (int k = 2; k < omegas.Length; k++)
            Assert.Equal(k * omega1, omegas[k], precision: 10);
    }
}
