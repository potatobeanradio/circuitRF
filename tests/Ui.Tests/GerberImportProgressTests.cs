// Owner report, 2026-09-04: importing a large Gerber set locks the window up. Move the work off the
// UI thread, report it on ONE row in the Messages panel with a progress bar the user can cancel from,
// and write the per-file notes AFTER the import rather than during it.
//
// What this file gates is the two halves that are testable without a window: the import's own
// progress/cancellation contract (CircuitRF.Design, no Avalonia anywhere), and the pure function that
// renders one observation onto the live row. The third half — that the .clay is read on a background
// thread before the document opens — is a threading property of WorkspaceViewModel and is covered by
// its own note in src/Ui/RESOLVED.md rather than by a wall-clock assertion here.
//
// COUNTERS ONLY, like GerberImportTests: there is no timing assertion in this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class GerberImportProgressTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-progress-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures: the same hand-authored shapes GerberImportTests uses ──────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(double xMm = 1.0, double yMm = 1.0)
        => MmHeader + "%ADD10C,0.400*%\nD10*\n" +
           $"X{(long)Math.Round(xMm * 1_000_000)}Y{(long)Math.Round(yMm * 1_000_000)}D03*\n" + "M02*\n";

    private static string Drill(double xMm = 1.0, double yMm = 1.0)
        => "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" + $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    /// <summary>A source folder of <paramref name="artworkFiles"/> artwork files plus one drill file.</summary>
    private string SourceFolder(string name, int artworkFiles)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < artworkFiles; i++)
            File.WriteAllText(Path.Combine(dir, $"layer{i}.gbr"), Artwork(1.0 + i * 0.5));
        File.WriteAllText(Path.Combine(dir, "holes.drl"), Drill());
        return dir;
    }

    private string ParentFolder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<string> FilesIn(string dir)
        => [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>Delivers each observation on the calling thread, synchronously.
    ///
    /// <para>NOT <see cref="Progress{T}"/>: with no <see cref="SynchronizationContext"/> — which is
    /// what a test thread has — it posts to the thread pool, so the observations would arrive
    /// concurrently with the import that produced them. That makes an ordering assertion meaningless
    /// and a cancel-from-inside-the-callback arrive whenever it likes. In the application there IS a
    /// context (the dispatcher's), which is the whole reason the view model uses
    /// <see cref="Progress{T}"/> there.</para></summary>
    private sealed class Inline<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    // ── The import reports progress ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheImportReportsAStageCounterThatIsMonotoneAndReachesItsOwnTotal()
    {
        string source = SourceFolder("src", artworkFiles: 4);
        string parent = ParentFolder("dest");

        var seen = new List<RunProgress>();
        var control = new RunControl
        {
            // Every observation, not a sampled one — the point here is the SEQUENCE, and the default
            // 40 ms throttle would drop most of it on a fixture this small.
            MinReportIntervalMs = 0,
            Progress            = new Inline<RunProgress>(seen.Add),
        };

        var result = GerberImport.Import(FilesIn(source), parent, "board", null, 1000, control: control);

        Assert.False(result.Cancelled);
        Assert.NotEmpty(seen);

        // 4 artwork + 1 drill + 1 write. The total is fixed once, at the top of the counted stage,
        // and every later phase renames the label THROUGH the tick — so a phase change must never
        // restate the denominator or reset the numerator. The write's unit is ticked BEFORE the write
        // rather than after it, because that same call is the last cancellation checkpoint and a
        // check landing after the cell is on disk would throw with the import already created.
        var counted = seen.Where(p => p.StageTotal > 0).ToList();
        Assert.NotEmpty(counted);
        Assert.All(counted, p => Assert.Equal(6, p.StageTotal));

        for (int i = 1; i < counted.Count; i++)
            Assert.True(counted[i].StageCompleted >= counted[i - 1].StageCompleted,
                        $"the bar went backwards: {counted[i - 1].StageCompleted} → {counted[i].StageCompleted}");

        // And it reaches its own end: a bar left short of its total reads as a run that stopped.
        Assert.Equal(6, counted[^1].StageCompleted);
    }

    [Fact]
    public void TheLabelNamesTheFileBeingRead_SoAStalledPhaseIsIdentifiable()
    {
        string source = SourceFolder("src", artworkFiles: 3);
        string parent = ParentFolder("dest");

        var stages = new List<string>();
        var control = new RunControl
        {
            MinReportIntervalMs = 0,
            Progress            = new Inline<RunProgress>(p => stages.Add(p.Stage)),
        };

        GerberImport.Import(FilesIn(source), parent, "board", null, 1000, control: control);

        Assert.Contains("reading layer0.gbr", stages);
        Assert.Contains("reading layer2.gbr", stages);
        Assert.Contains("reading holes.drl", stages);
        Assert.Contains("writing the cell", stages);
    }

    [Fact]
    public void WithNoControl_TheImportIsUnchanged_WhichIsWhatKeepsTheCliCompiling()
    {
        string source = SourceFolder("src", artworkFiles: 2);

        var withControl    = GerberImport.Import(FilesIn(source), ParentFolder("a"), "board", null, 1000,
                                                 control: new RunControl());
        var withoutControl = GerberImport.Import(FilesIn(source), ParentFolder("b"), "board", null, 1000);

        Assert.False(withControl.Cancelled);
        Assert.False(withoutControl.Cancelled);
        Assert.Equal(withoutControl.Layers.Count, withControl.Layers.Count);
        Assert.Equal(withoutControl.Messages.Count, withControl.Messages.Count);
    }

    // ── Cancellation is graceful: it creates nothing ────────────────────────────────────────────

    [Fact]
    public void ATokenAlreadyCancelled_ImportsNothing_AndLeavesTheDestinationEmpty()
    {
        string source = SourceFolder("src", artworkFiles: 3);
        string parent = ParentFolder("dest");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = GerberImport.Import(FilesIn(source), parent, "board", null, 1000,
                                         control: new RunControl { Token = cts.Token });

        Assert.True(result.Cancelled);
        Assert.Null(result.CellDir);
        Assert.Null(result.ImportDir);
        Assert.Contains(result.Messages, m => m.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        // R-L4g-14's rule, under cancellation: "nothing was created" is literally true.
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public void CancellingPartWayThrough_StopsAtTheNextFile_AndStillCreatesNothing()
    {
        string source = SourceFolder("src", artworkFiles: 6);
        string parent = ParentFolder("dest");

        using var cts = new CancellationTokenSource();
        int observed = 0;
        var control = new RunControl
        {
            MinReportIntervalMs = 0,
            Token               = cts.Token,
            // Cancel from inside the run, at the second file — the shape of a user right-clicking the
            // live row's bar while the set is being read.
            Progress = new Inline<RunProgress>(p =>
            {
                if (p.StageCompleted >= 2) cts.Cancel();
                observed = (int)p.StageCompleted;
            }),
        };

        var result = GerberImport.Import(FilesIn(source), parent, "board", null, 1000, control: control);

        Assert.True(result.Cancelled);
        Assert.Null(result.ImportDir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));

        // It stopped where it was asked to rather than running the set out — the 6 artwork files plus
        // the drill file plus the write would have been 8.
        Assert.True(observed < 8, $"the import ran past the cancel: {observed} unit(s)");
    }

    [Fact]
    public void CancellingOnTheLastUnitBeforeTheWrite_StillCreatesNothing()
    {
        // The boundary case the checkpoint's placement exists for. The tick that advances the counter
        // to its total is ALSO the last token check, and it runs BEFORE ImportFolder.Create — so a
        // cancel that arrives on the unit before it stops with the destination still empty. Ticking
        // that unit after the write instead would throw with the cell already on disk, which is
        // R-L4g-14's "nothing was created" broken by the cancellation path itself.
        string source = SourceFolder("src", artworkFiles: 3);   // 3 artwork + 1 drill + 1 write = 5
        string parent = ParentFolder("dest");

        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            MinReportIntervalMs = 0,
            Token               = cts.Token,
            Progress            = new Inline<RunProgress>(p => { if (p.StageCompleted >= 4) cts.Cancel(); }),
        };

        var result = GerberImport.Import(FilesIn(source), parent, "board", null, 1000, control: control);

        Assert.True(result.Cancelled);
        Assert.Null(result.ImportDir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public void ACancelledImportStillReportsWhatItHadWorkedOut()
    {
        // A cancelled run that says nothing at all reads as a crash. What it had already classified
        // and read is still worth printing under the cancellation line.
        string source = SourceFolder("src", artworkFiles: 2);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "not artwork");

        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            MinReportIntervalMs = 0,
            Token               = cts.Token,
            Progress            = new Inline<RunProgress>(p => { if (p.StageCompleted >= 1) cts.Cancel(); }),
        };

        var result = GerberImport.Import(FilesIn(source), ParentFolder("dest"), "board", null, 1000,
                                         control: control);

        Assert.True(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("readme.txt", StringComparison.Ordinal));
    }

    // ── The row the user actually reads ─────────────────────────────────────────────────────────

    private static readonly Action<Action> RunItHere = a => a();

    private static (LiveProgressMessage Live, MessageEntry Entry) NewLive()
    {
        var entry = new MessageEntry(MessageLevel.Info, "Import Gerber", null, DateTime.Now)
        {
            ProgressIndeterminate = true,
            ProgressPercent       = 0,
        };
        return (new LiveProgressMessage(entry, RunItHere), entry);
    }

    [Fact]
    public void ACountedObservation_LeavesTheRowTextConstant_AndPutsEverythingThatMovesAfterTheBar()
    {
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportGerberImportProgress(
            live, new RunProgress("reading GERB_01_Top_Layer.art", 0, 0, 3, 21));

        // Constant left of the bar: a file name growing in there would shove the bar sideways on
        // every tick, which is the twitching the text/counter split exists to remove.
        Assert.Equal("Import Gerber", entry.Text);
        Assert.Equal("reading GERB_01_Top_Layer.art  (3 / 21)", entry.ProgressText);
        Assert.Equal(100.0 * 3 / 21, entry.ProgressValue, 6);
        Assert.False(entry.ProgressIndeterminate);
    }

    [Fact]
    public void AnUncountedPhase_IsIndeterminate_RatherThanShowingAFakeDenominator()
    {
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportGerberImportProgress(
            live, new RunProgress("looking at what the folder holds", 0, 0));

        Assert.Equal("Import Gerber", entry.Text);
        Assert.Equal("looking at what the folder holds", entry.ProgressText);
        Assert.True(entry.ProgressIndeterminate);
    }

    [Fact]
    public void AnEmptyStage_ReadsAsStarting_RatherThanAsABlankRow()
    {
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportGerberImportProgress(live, new RunProgress("", 0, 0));

        Assert.Equal("starting", entry.ProgressText);
    }
}
