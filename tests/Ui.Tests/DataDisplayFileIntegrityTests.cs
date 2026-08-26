// ================================================================
//  DataDisplayFileIntegrityTests.cs
//
//  Owner-reported, 2026-08-26: "somehow i managed to get the data display to get
//  corrupted .. i had to make a new workspace to make a fresh one working again."
//
//  Two halves of one failure, both pinned here:
//    1. The `.cdd` was the only document type written NON-atomically, so an
//       interrupted save could leave a half-written file where a good display was.
//    2. A `.cdd` that would not parse loaded SILENTLY as a clean, materialized,
//       one-empty-plot display — and the next save wrote that blank over the file.
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

public sealed class DataDisplayFileIntegrityTests
{
    private static string MakeTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "crf_ddint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteRunNpy(string dir, string fileName)
    {
        var fAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var cube  = new DataCube(new[] { fAxis }, new Complex[] { new(0.3, -0.2), new(0.2, -0.1) });
        var ds    = new DataSet();
        ds.AddToGroup("SP1", "S", cube);
        DataSetExporter.Export(ds, Path.Combine(dir, fileName), ExportFormat.Npy);
    }

    private static async Task<DisplayWindowViewModel> MakeWindowAsync(string dir)
    {
        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => dir;
        window.GetResultsRootAction = () => dir;
        await window.DataSourceLibrary.LoadFileAsync(Path.Combine(dir, "run.npy"));
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync("run.npy");
        return window;
    }

    // ── 1. A damaged .cdd is refused, not silently opened blank ────────────────
    //
    // The whole point is the SECOND assertion: before the fix the load returned
    // quietly, the document materialized clean at that path, and saving it replaced
    // the damaged file with an empty display — the user's plots gone for good.

    [Fact]
    public async Task DamagedCdd_IsRefused_AndTheFileIsLeftUnchanged()
    {
        var dir = MakeTempDir();
        WriteRunNpy(dir, "run.npy");
        var cdd = Path.Combine(dir, "display.cdd");

        // Author a real display and save it, then truncate the file the way an
        // interrupted write would.
        var authored = await MakeWindowAsync(dir);
        authored.DataDisplay!.AddPlot(PlotType.Rect, left: 600, top: 40);
        await authored.SaveAllAsync(cdd);
        var good = File.ReadAllText(cdd);
        Assert.Equal(2, authored.DataDisplay.Plots.Count);
        Assert.Contains("\"Left\": 600", good);

        var damaged = good.Substring(0, good.Length / 2);
        File.WriteAllText(cdd, damaged);

        var window = await MakeWindowAsync(dir);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => window.LoadAllAsync(cdd));
        Assert.Contains("display.cdd", ex.Message);

        // Nothing was written back over the user's file by the failed open.
        Assert.Equal(damaged, File.ReadAllText(cdd));
    }

    // ── 2. The save is atomic: a write that fails leaves the old file intact ───
    //
    // Forced deterministically by putting a DIRECTORY where the sibling temp file
    // has to go, so the temp write throws and the rename never happens. The old
    // non-atomic path had no temp file to collide with — it truncated the target
    // and wrote straight into it, so this save would have "succeeded" and the
    // original content assertion below would fail.

    [Fact]
    public async Task SaveThatFailsPartway_LeavesThePreviousFileIntact()
    {
        var dir = MakeTempDir();
        WriteRunNpy(dir, "run.npy");
        var cdd = Path.Combine(dir, "display.cdd");

        var window = await MakeWindowAsync(dir);
        window.DataDisplay!.AddPlot(PlotType.Rect, left: 600, top: 40);
        await window.SaveAllAsync(cdd);
        var original = File.ReadAllText(cdd);
        Assert.Equal(2, window.DataDisplay.Plots.Count);

        // Block the write, then change the display and try to save over it.
        Directory.CreateDirectory(cdd + ".tmp");
        window.DataDisplay.RemovePlot(window.DataDisplay.Plots[1]);

        await Assert.ThrowsAnyAsync<Exception>(() => window.SaveAllAsync(cdd));
        Assert.Equal(original, File.ReadAllText(cdd));
    }

    // ── 3. A healthy .cdd still round-trips byte-for-byte ─────────────────────
    //     (the atomic write must not change what is written, only how)

    [Fact]
    public async Task HealthyCdd_RoundTripsByteForByte()
    {
        var dir = MakeTempDir();
        WriteRunNpy(dir, "run.npy");
        var cdd = Path.Combine(dir, "display.cdd");

        var window = await MakeWindowAsync(dir);
        window.DataDisplay!.AddPlot(PlotType.Rect, left: 600, top: 40);
        await window.SaveAllAsync(cdd);
        var first = File.ReadAllText(cdd);

        var reloaded = await MakeWindowAsync(dir);
        await reloaded.LoadAllAsync(cdd);
        var again = Path.Combine(dir, "again.cdd");
        await reloaded.SaveAllAsync(again);

        Assert.Equal(first, File.ReadAllText(again));
        Assert.False(File.Exists(cdd + ".tmp"));   // no temp left behind by a clean save
    }
}
