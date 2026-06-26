using System.IO;
using System.Numerics;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Pure-logic tests for RunResultsWriter (Stage 2 — single run.npy).
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

    // ── WriteRun — happy path ─────────────────────────────────────────────────

    [Fact]
    public void WriteRun_HappyPath_RunNpyExists()
    {
        var baseDir = MakeTempDir();
        var key     = "MyAmp";
        var owner   = "scratch:MyAmp";
        var sink    = new FakeSink();
        var grouped = MakeGroupedDataSet();

        RunResultsWriter.WriteRun(baseDir, key, owner, grouped, sink);

        var dir = Path.Combine(baseDir, "results", key);
        Assert.True(File.Exists(Path.Combine(dir, "run.npy")));
        Assert.Equal(owner, File.ReadAllText(Path.Combine(dir, ".source")).Trim());
        Assert.Single(sink.Successes);
        Assert.Contains("MyAmp", sink.Successes[0].Text);
        Assert.Contains("3 group(s)", sink.Successes[0].Text);
    }

    // ── WriteRun — stale-file clear ───────────────────────────────────────────

    [Fact]
    public void WriteRun_StaleNpyCleared()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var owner   = "scratch:Amp";
        var sink    = new FakeSink();

        // Pre-seed a stale .npy in the results dir
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), owner);
        File.WriteAllText(Path.Combine(dir, "stale_analysis.npy"), "dummy");

        RunResultsWriter.WriteRun(baseDir, key, owner, MakeGroupedDataSet(), sink);

        Assert.True(File.Exists(Path.Combine(dir, "run.npy")));
        Assert.False(File.Exists(Path.Combine(dir, "stale_analysis.npy")),
            "Stale .npy must be deleted on write");
    }

    // ── WriteRun — collision warning ──────────────────────────────────────────

    [Fact]
    public void WriteRun_DifferentOwner_PostsWarningWritesNothing()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var ownerA  = "/path/to/LibA/Amp";
        var ownerB  = "/path/to/LibB/Amp";
        var sink    = new FakeSink();

        // Pre-create .source with ownerA
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), ownerA);

        // Attempt to write from ownerB
        RunResultsWriter.WriteRun(baseDir, key, ownerB, MakeGroupedDataSet(), sink);

        Assert.Empty(Directory.GetFiles(dir, "*.npy"));
        Assert.Single(sink.Warnings);
        Assert.Contains("different cell", sink.Warnings[0].Text);
        Assert.Empty(sink.Successes);
    }

    // ── WriteRun — same owner proceeds without collision ──────────────────────

    [Fact]
    public void WriteRun_SameOwner_ProceedsNormally()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var owner   = "/path/to/workspace/Amp";
        var sink    = new FakeSink();

        // Pre-create .source with the same owner
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), owner);

        RunResultsWriter.WriteRun(baseDir, key, owner, MakeGroupedDataSet(), sink);

        Assert.True(File.Exists(Path.Combine(dir, "run.npy")));
        Assert.Single(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── ResolveResultsRoot — workspace vs scratch ─────────────────────────────

    [Fact]
    public void ResolveResultsRoot_Workspace_ReturnsWorkspaceResults()
    {
        var wsDir   = Path.Combine(Path.GetTempPath(), "MyProject");
        var cws     = Path.Combine(wsDir, ".cws");
        var scratch = Path.Combine(Path.GetTempPath(), "session1");

        Assert.Equal(Path.Combine(wsDir, "results"),
            RunResultsWriter.ResolveResultsRoot(cws, scratch));
    }

    [Fact]
    public void ResolveResultsRoot_NoWorkspace_ReturnsScratchResults()
    {
        // The scratch (no-workspace) case is the bug: results must resolve to the session dir so a
        // scratch sim's output is discoverable in the Data Display without saving anything.
        var scratch = Path.Combine(Path.GetTempPath(), "session1");

        Assert.Equal(Path.Combine(scratch, "results"),
            RunResultsWriter.ResolveResultsRoot(null, scratch));
    }

    // ── WriteRun — moved workspace is not a collision ─────────────────────────

    [Fact]
    public void WriteRun_WorkspaceMoved_AdoptsResultsWithoutCollision()
    {
        // baseDir simulates the workspace at its NEW location; the cell lives inside it.
        var baseDir  = MakeTempDir();
        var key      = "FET_curve_tracer";
        var newOwner = Path.Combine(baseDir, key);   // absolute, as OwnerIdentity returns for a cell
        var sink     = new FakeSink();

        // .source carries a stale ABSOLUTE path from the OLD location (it moved with the workspace).
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), "/old/location/" + key);

        RunResultsWriter.WriteRun(baseDir, key, newOwner, MakeGroupedDataSet(), sink);

        Assert.True(File.Exists(Path.Combine(dir, "run.npy")),
            "a moved workspace must still write results");
        Assert.Empty(sink.Warnings);
        Assert.Single(sink.Successes);
        // Marker rewritten to the stable workspace-relative form so future moves are seamless.
        Assert.Equal(key, File.ReadAllText(Path.Combine(dir, ".source")).Trim());
    }

    // ── WriteRun — genuine in-workspace collision still warns ──────────────────

    [Fact]
    public void WriteRun_DifferentInWorkspaceOwners_StillCollide()
    {
        var baseDir = MakeTempDir();
        var key     = "Amp";
        var sink    = new FakeSink();

        // Already owned (post-migration relative marker) by the cell folder "Amp".
        var dir = Path.Combine(baseDir, "results", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".source"), "Amp");

        // A different in-workspace owner with the same key: a loose Amp.csch (relative "Amp.csch").
        var looseOwner = Path.Combine(baseDir, "Amp.csch");
        RunResultsWriter.WriteRun(baseDir, key, looseOwner, MakeGroupedDataSet(), sink);

        Assert.Empty(Directory.GetFiles(dir, "*.npy"));
        Assert.Single(sink.Warnings);
        Assert.Contains("different cell", sink.Warnings[0].Text);
    }

    // ── WriteRun — null grouped skips silently ────────────────────────────────

    [Fact]
    public void WriteRun_NullGrouped_WritesNothing()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", "owner", null, sink);

        Assert.Empty(written);
        Assert.False(Directory.Exists(Path.Combine(baseDir, "results")));
        Assert.Empty(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── WriteRun — zero-group DataSet skips silently ──────────────────────────

    [Fact]
    public void WriteRun_ZeroGroups_WritesNothing()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();
        var empty   = new DataSet();   // no AddToGroup calls → Groups.Count == 0

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", "owner", empty, sink);

        Assert.Empty(written);
        Assert.False(Directory.Exists(Path.Combine(baseDir, "results")));
        Assert.Empty(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── WriteRun — returns single run.npy path ────────────────────────────────

    [Fact]
    public void WriteRun_ReturnsRunNpyPath()
    {
        var baseDir = MakeTempDir();
        var key     = "TestAmp";
        var owner   = "scratch:TestAmp";
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, key, owner, MakeGroupedDataSet(), sink);

        Assert.Single(written);
        var expected = Path.GetFullPath(Path.Combine(baseDir, "results", key, "run.npy"));
        Assert.Equal(expected, written[0]);

        // Collision skip returns empty list.
        var sink2 = new FakeSink();
        var collision = RunResultsWriter.WriteRun(baseDir, key, "scratch:OtherAmp",
            MakeGroupedDataSet(), sink2);
        Assert.Empty(collision);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DataSet MakeGroupedDataSet()
    {
        var axis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var data = new Complex[] { new(0.1, -0.2), new(0.2, -0.1) };
        var cube = new DataCube(new[] { axis }, data);

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", cube);
        ds.AddToGroup("HB1", "V", cube);
        ds.AddToGroup("measurements", "Gain", DataCube.Scalar(3.14));
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
