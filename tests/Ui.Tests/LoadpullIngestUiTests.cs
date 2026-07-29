using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 7.4f-3: DataSourceLibraryViewModel recognises .spl and .lpcwave extensions
/// and loads them as SourceKind.Spl / SourceKind.Lpcwave entries.
/// </summary>
public sealed class LoadpullIngestUiTests
{
    private static string TestDataDir(string subDir)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", subDir);
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"testdata/{subDir} not found");
    }

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Spl_AddsEntry_WithKindSpl()
    {
        var path = Path.Combine(TestDataDir("spl_test_data"),
                                "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var lib = new DataSourceLibraryViewModel();

        await lib.LoadFileAsync(path);

        Assert.Single(lib.Entries);
        Assert.Equal(SourceKind.Spl, lib.Entries[0].Kind);
        Assert.False(lib.Entries[0].IsBroken);
        Assert.NotNull(lib.Entries[0].Data);
    }

    [FixtureFact("testdata/lpwave_test_data", "ask the repo owner for these lab-measured .lpcwave files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Lpcwave_AddsEntry_WithKindLpcwave()
    {
        var path = Path.Combine(TestDataDir("lpwave_test_data"),
                                "4x150_new_wavecal_24012020.lpcwave");
        var lib = new DataSourceLibraryViewModel();

        await lib.LoadFileAsync(path);

        Assert.Single(lib.Entries);
        Assert.Equal(SourceKind.Lpcwave, lib.Entries[0].Kind);
        Assert.False(lib.Entries[0].IsBroken);
        Assert.NotNull(lib.Entries[0].Data);
    }

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Spl_DataSet_HasCanonicalCubes()
    {
        var path = Path.Combine(TestDataDir("spl_test_data"),
                                "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);

        var ds = lib.Entries[0].Data!;
        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "GammaLoad", "PavlDbm" })
            Assert.True(ds.Contains(name), $"Missing cube: {name}");
    }

    [FixtureFact("testdata/lpwave_test_data", "ask the repo owner for these lab-measured .lpcwave files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Lpcwave_DataSet_HasCanonicalCubes()
    {
        var path = Path.Combine(TestDataDir("lpwave_test_data"),
                                "4x150_new_wavecal_24012020.lpcwave");
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);

        var ds = lib.Entries[0].Data!;
        foreach (var name in new[] { "Pout_dBm", "GammaLoad", "PavlDbm" })
            Assert.True(ds.Contains(name), $"Missing cube: {name}");
    }

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Spl_SkipsDuplicates()
    {
        var path = Path.Combine(TestDataDir("spl_test_data"),
                                "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.LoadFileAsync(path); // second load — must dedup

        Assert.Single(lib.Entries);
    }

    [FixtureFact("testdata/lpwave_test_data", "ask the repo owner for these lab-measured .lpcwave files — not committed to the repository")]
    public async Task Library_LoadFileAsync_Lpcwave_SkipsDuplicates()
    {
        var path = Path.Combine(TestDataDir("lpwave_test_data"),
                                "4x150_new_wavecal_24012020.lpcwave");
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.LoadFileAsync(path); // second load — must dedup

        Assert.Single(lib.Entries);
    }
}
