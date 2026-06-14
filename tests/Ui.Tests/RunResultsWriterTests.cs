using System.IO;
using System.Numerics;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Pure-logic tests for RunResultsWriter (Phase 7.0).
/// Uses temp directories and a fake IMessageSink — no Avalonia runtime required.
/// </summary>
public sealed class RunResultsWriterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_rwr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── SchematicKey tests ────────────────────────────────────────────────────

    [Fact]
    public void SchematicKey_CellHomedSoleView_ReturnsCellName()
    {
        // …/Amp/schematic/Amp.csch → "Amp"
        var path = Path.Combine("root", "workspace", "Amp", "schematic", "Amp.csch");
        Assert.Equal("Amp", RunResultsWriter.SchematicKey(path, "unused"));
    }

    [Fact]
    public void SchematicKey_CellHomedMultiView_ReturnsCellDotView()
    {
        // …/Amp/schematic/tb2.csch → "Amp.tb2"
        var path = Path.Combine("root", "workspace", "Amp", "schematic", "tb2.csch");
        Assert.Equal("Amp.tb2", RunResultsWriter.SchematicKey(path, "unused"));
    }

    [Fact]
    public void SchematicKey_LooseFile_ReturnsStem()
    {
        // …/foo/bar.csch → "bar"
        var path = Path.Combine("some", "folder", "bar.csch");
        Assert.Equal("bar", RunResultsWriter.SchematicKey(path, "unused"));
    }

    [Fact]
    public void SchematicKey_Scratch_ReturnsSanitizedId()
    {
        // (null, "Untitled-Schematic-1") → "Untitled-Schematic-1"
        Assert.Equal("Untitled-Schematic-1",
            RunResultsWriter.SchematicKey(null, "Untitled-Schematic-1"));
    }

    // ── WriteResults — happy path ─────────────────────────────────────────────

    [Fact]
    public void WriteResults_HappyPath_FilesExistAndSourceWritten()
    {
        var baseDir   = MakeTempDir();
        var key       = "MyAmp";
        var owner     = "scratch:MyAmp";
        var sink      = new FakeSink();
        var results   = new[]
        {
            new AnalysisResult("SP1", MakeSimpleDataSet("S")),
            new AnalysisResult("HB1", MakeSimpleDataSet("V")),
        };

        RunResultsWriter.WriteResults(baseDir, key, owner, results, sink);

        var dir = Path.Combine(baseDir, "results", key);
        Assert.True(File.Exists(Path.Combine(dir, "SP1.npy")));
        Assert.True(File.Exists(Path.Combine(dir, "HB1.npy")));
        Assert.Equal(owner, File.ReadAllText(Path.Combine(dir, ".source")).Trim());
        Assert.Single(sink.Successes);
        Assert.Contains("MyAmp", sink.Successes[0].Text);
        Assert.Contains("2 analysis file(s)", sink.Successes[0].Text);
    }

    // ── WriteResults — stale-file clear ──────────────────────────────────────

    [Fact]
    public void WriteResults_SameOwnerRerun_StaleNpyCleared()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var owner   = "scratch:Amp";
        var sink    = new FakeSink();

        // First run: 2 analyses
        RunResultsWriter.WriteResults(baseDir, key, owner,
            new[]
            {
                new AnalysisResult("SP1", MakeSimpleDataSet("S")),
                new AnalysisResult("HB1", MakeSimpleDataSet("V")),
            }, sink);

        var dir     = Path.Combine(baseDir, "results", key);
        Assert.True(File.Exists(Path.Combine(dir, "SP1.npy")));
        Assert.True(File.Exists(Path.Combine(dir, "HB1.npy")));

        // Second run: only SP1 remains
        sink.Clear();
        RunResultsWriter.WriteResults(baseDir, key, owner,
            new[] { new AnalysisResult("SP1", MakeSimpleDataSet("S")) }, sink);

        Assert.True(File.Exists(Path.Combine(dir, "SP1.npy")));
        Assert.False(File.Exists(Path.Combine(dir, "HB1.npy")), "Stale HB1.npy must be deleted");
        Assert.Single(sink.Successes);
    }

    // ── WriteResults — collision warning ──────────────────────────────────────

    [Fact]
    public void WriteResults_DifferentOwner_PostsWarningWritesNothing()
    {
        var baseDir  = MakeTempDir();
        var key      = "Amp";
        var ownerA   = "/path/to/LibA/Amp";
        var ownerB   = "/path/to/LibB/Amp";
        var sink     = new FakeSink();

        // Pre-create .source with ownerA
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), ownerA);

        // Attempt to write from ownerB
        RunResultsWriter.WriteResults(baseDir, key, ownerB,
            new[] { new AnalysisResult("SP1", MakeSimpleDataSet("S")) }, sink);

        Assert.Empty(Directory.GetFiles(dir, "*.npy"));
        Assert.Single(sink.Warnings);
        Assert.Contains("different cell", sink.Warnings[0].Text);
        Assert.Empty(sink.Successes);
    }

    // ── WriteResults — same owner proceeds without collision ──────────────────

    [Fact]
    public void WriteResults_SameOwner_ProceedsNormally()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var owner   = "/path/to/workspace/Amp";
        var sink    = new FakeSink();

        // Pre-create .source with the same owner
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), owner);

        RunResultsWriter.WriteResults(baseDir, key, owner,
            new[] { new AnalysisResult("SP1", MakeSimpleDataSet("S")) }, sink);

        Assert.True(File.Exists(Path.Combine(dir, "SP1.npy")));
        Assert.Single(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── WriteResults — empty results skips silently ───────────────────────────

    [Fact]
    public void WriteResults_EmptyResults_WritesNothing()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        RunResultsWriter.WriteResults(baseDir, "Amp", "owner", Array.Empty<AnalysisResult>(), sink);

        Assert.False(Directory.Exists(Path.Combine(baseDir, "results")));
        Assert.Empty(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DataSet MakeSimpleDataSet(string cubeName)
    {
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var data = new Complex[] { new(0.1, -0.2), new(0.2, -0.1) };
        var ds   = new DataSet();
        ds.Add(cubeName, new DataCube(new[] { axis }, data));
        return ds;
    }

    // Fake IMessageSink that records posted messages.
    private sealed class FakeSink : IMessageSink
    {
        public record Posted(MessageLevel Level, string Text, string? Path);

        private readonly List<Posted> _all = new();

        public IReadOnlyList<Posted> All       => _all;
        public IReadOnlyList<Posted> Successes => _all.Where(p => p.Level == MessageLevel.Success).ToList();
        public IReadOnlyList<Posted> Warnings  => _all.Where(p => p.Level == MessageLevel.Warning).ToList();

        public void Post(MessageLevel level, string text, string? filePath = null)
            => _all.Add(new Posted(level, text, filePath));

        public void Clear() => _all.Clear();
    }
}
