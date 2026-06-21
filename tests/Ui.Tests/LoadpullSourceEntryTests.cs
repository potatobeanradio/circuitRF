using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 7.4g-1: KnownLoadpullProvider wires into RefreshAvailableDataSources so
/// workspace-tracked .spl/.lpcwave files appear in AvailableDataSources, and
/// SelectDataSourceAsync on their LogicalId loads them as loadpull entries.
/// </summary>
public sealed class LoadpullSourceEntryTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static string? FindTestFile(string subDir, string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", subDir, fileName);
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string SplPath =>
        FindTestFile("spl_test_data", "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl")
            ?? throw new FileNotFoundException("spl test data not found");

    private static string LpcwavePath =>
        FindTestFile("lpwave_test_data", "4x150_new_wavecal_24012020.lpcwave")
            ?? throw new FileNotFoundException("lpcwave test data not found");

    // ── T1: .spl in KnownLoadpullProvider appears in AvailableDataSources ────

    [Fact]
    public void RefreshAvailableDataSources_SplKnownFile_AppearsWithKindSpl()
    {
        var splPath = SplPath;
        var lib = new DataSourceLibraryViewModel();
        lib.KnownLoadpullProvider = () => new[] { splPath };

        lib.RefreshAvailableDataSources();

        var item = lib.AvailableDataSources.FirstOrDefault(i =>
            string.Equals(i.AbsolutePath, splPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(item);
        Assert.Equal(SourceKind.Spl, item.Kind);
        Assert.Equal(Path.GetFileName(splPath), item.DisplayName);
        Assert.Equal(splPath, item.LogicalId);
    }

    // ── T2: .lpcwave in KnownLoadpullProvider appears with KindLpcwave ────────

    [Fact]
    public void RefreshAvailableDataSources_LpcwaveKnownFile_AppearsWithKindLpcwave()
    {
        var lpcPath = LpcwavePath;
        var lib = new DataSourceLibraryViewModel();
        lib.KnownLoadpullProvider = () => new[] { lpcPath };

        lib.RefreshAvailableDataSources();

        var item = lib.AvailableDataSources.FirstOrDefault(i =>
            string.Equals(i.AbsolutePath, lpcPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(item);
        Assert.Equal(SourceKind.Lpcwave, item.Kind);
        Assert.Equal(Path.GetFileName(lpcPath), item.DisplayName);
        Assert.Equal(lpcPath, item.LogicalId);
    }

    // ── T3: Null KnownLoadpullProvider does not throw ─────────────────────────

    [Fact]
    public void RefreshAvailableDataSources_NullLoadpullProvider_DoesNotThrow()
    {
        var lib = new DataSourceLibraryViewModel();
        lib.KnownLoadpullProvider = null;

        var ex = Record.Exception(() => lib.RefreshAvailableDataSources());

        Assert.Null(ex);
    }

    // ── T4: SelectDataSourceAsync on .spl LogicalId loads the entry ──────────

    [Fact]
    public async Task SelectDataSourceAsync_SplLogicalId_LoadsEntry()
    {
        var splPath = SplPath;
        var lib = new DataSourceLibraryViewModel();
        lib.KnownLoadpullProvider = () => new[] { splPath };
        lib.RefreshAvailableDataSources();

        // LogicalId for a known-file is the absolute path (same as abs path).
        await lib.SelectDataSourceAsync(splPath);

        Assert.NotNull(lib.SelectedEntry);
        Assert.Equal(SourceKind.Spl, lib.SelectedEntry!.Kind);
        Assert.False(lib.SelectedEntry.IsBroken);
        Assert.NotNull(lib.SelectedEntry.Data);
    }

    // ── T5: SelectDataSourceAsync on .lpcwave LogicalId loads the entry ───────

    [Fact]
    public async Task SelectDataSourceAsync_LpcwaveLogicalId_LoadsEntry()
    {
        var lpcPath = LpcwavePath;
        var lib = new DataSourceLibraryViewModel();
        lib.KnownLoadpullProvider = () => new[] { lpcPath };
        lib.RefreshAvailableDataSources();

        await lib.SelectDataSourceAsync(lpcPath);

        Assert.NotNull(lib.SelectedEntry);
        Assert.Equal(SourceKind.Lpcwave, lib.SelectedEntry!.Kind);
        Assert.False(lib.SelectedEntry.IsBroken);
        Assert.NotNull(lib.SelectedEntry.Data);
    }

    // ── T6: Touchstone provider still works alongside loadpull provider ────────

    [Fact]
    public void RefreshAvailableDataSources_BothProviders_BothAppear()
    {
        var splPath = SplPath;
        var lib = new DataSourceLibraryViewModel();
        lib.KnownTouchstoneProvider = () => Array.Empty<string>();
        lib.KnownLoadpullProvider   = () => new[] { splPath };

        lib.RefreshAvailableDataSources();

        Assert.Single(lib.AvailableDataSources);
        Assert.Equal(SourceKind.Spl, lib.AvailableDataSources[0].Kind);
    }
}
