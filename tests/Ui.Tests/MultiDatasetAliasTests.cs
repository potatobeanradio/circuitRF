// ================================================================
//  MultiDatasetAliasTests.cs  —  brief-results-storage-and-data-display.md §3/§4 gates
//
//  R-res-4: a .cdd references a SET of .npy files, each with a short display alias, stored
//  in the .cdd (not re-derived at load time), unique within the display, and used to qualify
//  trace labels ("baseline:S21" vs "tuned:S21").
// ================================================================

using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MultiDatasetAliasTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_alias_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string WriteNpy(string dir, string fileName)
    {
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var data = new System.Numerics.Complex[] { new(0.1, -0.2), new(0.2, -0.1) };
        var cube = new DataCube(new[] { axis }, data);
        var ds   = new DataSet();
        ds.AddToGroup("SP1", "S", cube);
        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    // ── Default alias + uniqueness (R-res-4) ──────────────────────────────────

    [Fact]
    public async Task NewEntry_DefaultsAliasToFileStem()
    {
        var dir  = MakeTempDir();
        var path = WriteNpy(dir, "Amp.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);

        var entry = Assert.Single(lib.Entries);
        Assert.Equal("Amp", entry.Alias);
    }

    [Fact]
    public async Task TrySetAlias_DuplicateAcrossEntries_IsRefused()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "Amp.npy");
        var pathB = WriteNpy(dir, "Baseline.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(pathA);
        await lib.LoadFileAsync(pathB);

        var a = lib.Entries[0];
        var b = lib.Entries[1];

        Assert.True(lib.TrySetAlias(a, "tuned"));
        Assert.Equal("tuned", a.Alias);

        // b cannot also become "tuned" — refused, b's own alias is untouched.
        Assert.False(lib.TrySetAlias(b, "tuned"));
        Assert.Equal("Baseline", b.Alias);
    }

    [Fact]
    public async Task TrySetAlias_BlankInput_FallsBackToFileStem()
    {
        var dir  = MakeTempDir();
        var path = WriteNpy(dir, "Amp.npy");

        var lib   = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        var entry = Assert.Single(lib.Entries);

        Assert.True(lib.TrySetAlias(entry, "baseline"));
        Assert.True(lib.TrySetAlias(entry, "   "));
        Assert.Equal("Amp", entry.Alias);
    }

    // ── Minimal-label policy uses the alias, not the raw file name (R-res-4) ──

    [Fact]
    public async Task ComputeMinimalLabels_QualifiesBySourceAlias()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(pathA);
        await lib.LoadFileAsync(pathB);
        var entryA = lib.Entries[0];
        var entryB = lib.Entries[1];
        Assert.True(lib.TrySetAlias(entryA, "baseline"));
        Assert.True(lib.TrySetAlias(entryB, "tuned"));

        var snp = new SNP(new[] { 1e9 }, 2);
        var t1  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = pathA };
        var t2  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = pathB };

        var labels = TraceLabeler.ComputeMinimalLabels(new[] { t1, t2 }, aliasFor: t => lib.AliasFor(t.SourcePath));

        Assert.Equal("baseline·S(1,1) dB20", labels[0]);
        Assert.Equal("tuned·S(1,1) dB20", labels[1]);
    }

    [Fact]
    public void ComputeMinimalLabels_NoAliasResolver_FallsBackToFileStem()
    {
        var snp = new SNP(new[] { 1e9 }, 2);
        var t1  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = "/results/SP1.npy" };
        var t2  = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db) { SourcePath = "/results/SP1.npy" };

        // Same source, no resolver supplied at all — must behave exactly as before aliasing existed.
        var labels = TraceLabeler.ComputeMinimalLabels(new[] { t1, t2 });
        Assert.All(labels, l => Assert.DoesNotContain("SP1", l));
    }

    // ── .cdd round-trip: aliases persist, survive a missing dataset (R-res-4/5) ───

    [Fact]
    public async Task SaveThenLoad_RoundTripsAliasesForEveryDeclaredSource()
    {
        var dir   = MakeTempDir();
        var pathA = WriteNpy(dir, "run_v1.npy");
        var pathB = WriteNpy(dir, "run_v2.npy");
        var cddPath = Path.Combine(dir, "display.cdd");

        var window = new DisplayWindowViewModel();
        // R-dd-6: SaveAllAsync only persists a SourceAliases key that resolves to a bare,
        // portable filename — matching the real app, which always anchors ComputeSourceKey at a
        // results root, this must be configured here too (dir doubles as "results/" for this test).
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[0], "baseline"));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[1], "tuned"));

        await window.SaveAllAsync(cddPath);

        var reloaded = new DisplayWindowViewModel();
        reloaded.DataSourceLibrary.ResultsRootProvider = () => dir;
        await reloaded.LoadAllAsync(cddPath);

        var reloadedA = reloaded.DataSourceLibrary.Entries.Single(e =>
            string.Equals(e.FilePath, pathA, StringComparison.OrdinalIgnoreCase));
        var reloadedB = reloaded.DataSourceLibrary.Entries.Single(e =>
            string.Equals(e.FilePath, pathB, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("baseline", reloadedA.Alias);
        Assert.Equal("tuned", reloadedB.Alias);
    }

    [Fact]
    public async Task Load_MissingDeclaredSource_ReportsAsBroken_PreservesAlias_OtherSourceStaysLive()
    {
        var dir     = MakeTempDir();
        var pathA   = WriteNpy(dir, "run_v1.npy");
        var pathB   = WriteNpy(dir, "run_v2.npy");
        var cddPath = Path.Combine(dir, "display.cdd");

        var window = new DisplayWindowViewModel();
        // R-dd-6: see the note in SaveThenLoad_RoundTripsAliasesForEveryDeclaredSource above.
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(pathA);
        await window.DataSourceLibrary.LoadFileAsync(pathB);
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[0], "baseline"));
        Assert.True(window.DataSourceLibrary.TrySetAlias(window.DataSourceLibrary.Entries[1], "tuned"));
        await window.SaveAllAsync(cddPath);

        // Delete pathB by hand — this happens routinely per the brief's own stated workflow.
        File.Delete(pathB);

        var reloaded = new DisplayWindowViewModel();
        reloaded.DataSourceLibrary.ResultsRootProvider = () => dir;
        await reloaded.LoadAllAsync(cddPath);

        var stillLive = reloaded.DataSourceLibrary.Entries.Single(e =>
            string.Equals(e.FilePath, pathA, StringComparison.OrdinalIgnoreCase));
        Assert.False(stillLive.IsBroken);
        Assert.Equal("baseline", stillLive.Alias);

        var broken = reloaded.DataSourceLibrary.Entries.Single(e =>
            string.Equals(e.FilePath, pathB, StringComparison.OrdinalIgnoreCase));
        Assert.True(broken.IsBroken);
        Assert.Equal("tuned", broken.Alias);   // alias survives even though the file is gone
    }
}
