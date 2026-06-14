// ================================================================
//  NpyRoundTripAllAnalysesTests.cs  —  Phase 7.0 round-trip gate
//
//  Confirms that DataSetExporter → DataSetImporter is lossless for
//  every analysis type the run service can produce:
//    ✓ HB + parametric sweep (covered by NpyRoundTripTests, Hero 2)
//    ✓ S-parameter (this file, Hero 1)
//    ✓ Loadpull    (this file, Hero 3)
//
//  Each test: run engine → export .npy → import → assert same cube
//  names, axis names/lengths/values, DataKind, and numeric values
//  within tolerance.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Export;

public class NpyRoundTripAllAnalysesTests(ITestOutputHelper output) : IDisposable
{
    // ── Testdata locators ─────────────────────────────────────────────────────

    private static string FindTestdataDir(string subdirName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", subdirName);
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"testdata/{subdirName} not found");
    }

    // ── Temp-file management ─────────────────────────────────────────────────

    private readonly List<string> _tempFiles = new();

    private string TempPath()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"crf_rt7_{Guid.NewGuid():N}.npy");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                try { File.Delete(f); } catch { }
    }

    // ── S-parameter round-trip (Hero 1) ──────────────────────────────────────

    [Fact]
    public void SParam_RoundTrip_CubeNamesAndKindMatch()
    {
        var ds   = RunHero1Sparam();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            Assert.True(imported.Contains(name),
                $"Imported S-param DataSet missing cube '{name}'.");
            Assert.Equal(ds[name].DataKind, imported[name].DataKind);
            Assert.Equal(ds[name].Rank,     imported[name].Rank);
        }

        output.WriteLine($"  S-param cube count: {imported.Cubes.Count}");
    }

    [Fact]
    public void SParam_RoundTrip_AxesMetadataExact()
    {
        var ds   = RunHero1Sparam();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            var orig = ds[name];
            var back = imported[name];

            for (int d = 0; d < orig.Rank; d++)
            {
                Assert.Equal(orig.Axes[d].Name,           back.Axes[d].Name);
                Assert.Equal(orig.Axes[d].Unit,           back.Axes[d].Unit);
                Assert.Equal(orig.Axes[d].Values.Length,  back.Axes[d].Values.Length);
                for (int i = 0; i < orig.Axes[d].Values.Length; i++)
                    Assert.Equal(orig.Axes[d].Values[i], back.Axes[d].Values[i]);
            }
        }
    }

    [Fact]
    public void SParam_RoundTrip_ComplexValuesAreBitwiseEqual()
    {
        var ds   = RunHero1Sparam();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            var orig = ds[name];
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

    // ── Loadpull round-trip (Hero 3) ──────────────────────────────────────────

    [Fact]
    public void Loadpull_RoundTrip_CubeNamesAndKindsMatch()
    {
        var ds   = RunHero3LoadpullSmall();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            Assert.True(imported.Contains(name),
                $"Imported loadpull DataSet missing cube '{name}'.");
            Assert.Equal(ds[name].DataKind, imported[name].DataKind);
            Assert.Equal(ds[name].Rank,     imported[name].Rank);
        }

        output.WriteLine($"  Loadpull cube count: {imported.Cubes.Count}");
    }

    [Fact]
    public void Loadpull_RoundTrip_AxesMetadataExact()
    {
        var ds   = RunHero3LoadpullSmall();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            var orig = ds[name];
            var back = imported[name];

            Assert.Equal(orig.Rank, back.Rank);
            for (int d = 0; d < orig.Rank; d++)
            {
                Assert.Equal(orig.Axes[d].Name,          back.Axes[d].Name);
                Assert.Equal(orig.Axes[d].Values.Length, back.Axes[d].Values.Length);
            }
        }
    }

    [Fact]
    public void Loadpull_RoundTrip_RealValuesExact()
    {
        var ds   = RunHero3LoadpullSmall();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            var orig = ds[name];
            var back = imported[name];
            if (orig.DataKind != DataKind.Real) continue;

            var origData = orig.RealValues;
            var backData = back.RealValues;
            Assert.Equal(origData.Length, backData.Length);
            for (int i = 0; i < origData.Length; i++)
                Assert.Equal(origData[i], backData[i]);
        }
    }

    [Fact]
    public void Loadpull_RoundTrip_ComplexValuesExact()
    {
        var ds   = RunHero3LoadpullSmall();
        var path = TempPath();

        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var (imported, _) = DataSetImporter.Import(path);

        foreach (var name in ds.Cubes.Keys)
        {
            var orig = ds[name];
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

    // ── Engine helpers ────────────────────────────────────────────────────────

    private DataSet RunHero1Sparam()
    {
        var dir = FindTestdataDir("Hero1");
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero1.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        // hero1.cnl has no analysis directive — provide a short sweep directly.
        var freqs = Enumerable.Range(0, 10).Select(i => (1.0 + i) * 1e9).ToArray();
        var ds    = SParameterEngine.Run(netlist, freqs);

        output.WriteLine($"  Hero1 S-param: {freqs.Length} pts, " +
                         $"{ds.Cubes.Count} cube(s)");
        return ds;
    }

    // Cached Hero 3 result (small pin sweep to keep the test fast).
    private static DataSet? _hero3Ds;
    private static readonly Lock _hero3Lock = new();

    private DataSet RunHero3LoadpullSmall()
    {
        lock (_hero3Lock)
        {
            if (_hero3Ds is not null) return _hero3Ds;

            var dir = FindTestdataDir("Hero3");
            var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero3.cnl"));
            var netlist   = new Elaborator(lib).Elaborate(tb);
            var lpa       = tb.Analyses.OfType<LoadpullAnalysis>().First();

            // Trim the pin sweep to 2 steps so the test runs fast while still
            // producing a multi-step loadpull DataSet that exercises all cube shapes.
            var pFull = LoadpullEngine.Resolve(lpa, netlist.ResolvedGlobals);
            var p     = pFull with { PinMaxDbm = pFull.PinStartDbm + pFull.PinStepDb };

            _hero3Ds = new LoadpullEngine(netlist, tb).Run(p);

            output.WriteLine($"  Hero3 loadpull (small): " +
                             $"{_hero3Ds.Cubes.Count} cube(s), " +
                             $"grid={_hero3Ds["StopCode"].Axes[0].Length} pts, " +
                             $"pin={_hero3Ds["Pout"].Axes[1].Length} steps");
            return _hero3Ds;
        }
    }
}
