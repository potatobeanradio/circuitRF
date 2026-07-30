using System.IO;
using System.Numerics;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Pure-logic tests for RunResultsWriter — the flat, shared results/&lt;schematicKey&gt;.npy layout
/// (brief-results-storage-and-data-display.md §1). Uses temp directories and a fake IMessageSink —
/// no Avalonia runtime required.
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

    // ── WriteRun — happy path, flat naming (R-res-1) ──────────────────────────

    [Fact]
    public void WriteRun_HappyPath_FlatNpyExists()
    {
        var baseDir = MakeTempDir();
        var key     = "MyAmp";
        var sink    = new FakeSink();
        var grouped = MakeGroupedDataSet();

        RunResultsWriter.WriteRun(baseDir, key, grouped, sink);

        var dir = Path.Combine(baseDir, "results");
        var expectedNpy = Path.Combine(dir, "MyAmp.npy");
        Assert.True(File.Exists(expectedNpy));
        Assert.False(Directory.Exists(Path.Combine(dir, key)), "no per-schematic subdirectory");
        Assert.Single(sink.Successes);
        Assert.Contains("MyAmp", sink.Successes[0].Text);
        Assert.DoesNotContain("group(s)", sink.Successes[0].Text);
        // The posted path is the FILE itself, not its containing directory, so "Reveal in
        // Finder/File Explorer" selects the actual .npy.
        Assert.Equal(Path.GetFullPath(expectedNpy), sink.Successes[0].Path);
    }

    [Fact]
    public void WriteRun_TwoCellsShareASharedResultsDir_ProduceDistinctFiles()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink);
        RunResultsWriter.WriteRun(baseDir, "Amp.tb2", MakeGroupedDataSet(), sink);

        var dir = Path.Combine(baseDir, "results");
        Assert.True(File.Exists(Path.Combine(dir, "Amp.npy")));
        Assert.True(File.Exists(Path.Combine(dir, "Amp.tb2.npy")));
    }

    // ── WriteRun — scoped stale-delete (R-res-0), the regression that matters most ────

    [Fact]
    public void WriteRun_RunningOneSchematic_LeavesOtherFilesUntouched()
    {
        var baseDir = MakeTempDir();
        var dir     = Path.Combine(baseDir, "results");
        Directory.CreateDirectory(dir);

        // Three other schematics' results plus a user-named baseline, all pre-seeded.
        File.WriteAllText(Path.Combine(dir, "Amp.npy"), "amp-content");
        File.WriteAllText(Path.Combine(dir, "Mixer.npy"), "mixer-content");
        File.WriteAllText(Path.Combine(dir, "Filter.tb2.npy"), "filter-content");
        File.WriteAllText(Path.Combine(dir, "baseline_v1.npy"), "baseline-content");

        var sink = new FakeSink();
        RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink);

        // The just-written file changed (no longer the placeholder content)...
        Assert.NotEqual("amp-content", File.ReadAllText(Path.Combine(dir, "Amp.npy")));
        // ...but every OTHER file is untouched, byte-for-byte, asserted file-by-file.
        Assert.Equal("mixer-content", File.ReadAllText(Path.Combine(dir, "Mixer.npy")));
        Assert.Equal("filter-content", File.ReadAllText(Path.Combine(dir, "Filter.tb2.npy")));
        Assert.Equal("baseline-content", File.ReadAllText(Path.Combine(dir, "baseline_v1.npy")));
    }

    // ── WriteRun — R-res-0a: no orphaned .source marker is ever written ───────

    [Fact]
    public void WriteRun_NeverWritesASourceMarker()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink);

        var dir = Path.Combine(baseDir, "results");
        Assert.Empty(Directory.GetFiles(dir, ".source", SearchOption.AllDirectories));
        Assert.Empty(sink.Warnings);
    }

    // ── WriteRun — user-specified file name override (R-res-2/3) ──────────────

    [Fact]
    public void WriteRun_FileNameOverride_WritesUnderThatName()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink,
            fileNameOverride: "baseline");

        var expected = Path.GetFullPath(Path.Combine(baseDir, "results", "baseline.npy"));
        Assert.Equal(expected, Assert.Single(written));
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(Path.Combine(baseDir, "results", "Amp.npy")));
    }

    [Fact]
    public void WriteRun_FileNameOverride_NpyAppendedIfAbsent_NotDuplicated()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written1 = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink, "baseline");
        var written2 = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink, "baseline.npy");

        Assert.Equal(written1[0], written2[0]);
        Assert.EndsWith("baseline.npy", written1[0]);
        Assert.DoesNotContain(".npy.npy", written1[0]);
    }

    [Fact]
    public void WriteRun_BlankOverride_FallsBackToSchematicKey()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink, "   ");

        Assert.EndsWith("Amp.npy", written[0]);
    }

    [Fact]
    public void WriteRun_Rerun_OverwritesWithoutPrompting()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink, "baseline");
        var second = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink, "baseline");

        Assert.Single(second);   // succeeded again, same path, no error/prompt-shaped result
    }

    // ── SanitizeFileNameComponent / ResolveFileName (R-res-2) ─────────────────

    [Theory]
    [InlineData("baseline")]
    [InlineData("baseline.npy")]
    public void ResolveFileName_Override_ProducesExactlyOneNpySuffix(string typed)
    {
        Assert.Equal("baseline.npy", RunResultsWriter.ResolveFileName(typed, "Amp"));
    }

    [Fact]
    public void ResolveFileName_Blank_UsesSchematicKey()
    {
        Assert.Equal("Amp.npy", RunResultsWriter.ResolveFileName(null, "Amp"));
        Assert.Equal("Amp.npy", RunResultsWriter.ResolveFileName("", "Amp"));
        Assert.Equal("Amp.npy", RunResultsWriter.ResolveFileName("   ", "Amp"));
    }

    [Theory]
    [InlineData("../evil", ".._evil")]
    [InlineData("a/b/c", "a_b_c")]
    [InlineData(@"a\b", "a_b")]
    public void SanitizeFileNameComponent_RejectsPathSeparators(string raw, string expected)
    {
        Assert.Equal(expected, RunResultsWriter.SanitizeFileNameComponent(raw));
    }

    [Fact]
    public void WriteRun_OverrideWithPathSeparator_NeverEscapesResultsDir()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", MakeGroupedDataSet(), sink,
            fileNameOverride: "../../escape");

        var resultsDir = Path.GetFullPath(Path.Combine(baseDir, "results"));
        Assert.StartsWith(resultsDir, written[0]);
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

    // ── WriteRun — null grouped skips silently ────────────────────────────────

    [Fact]
    public void WriteRun_NullGrouped_WritesNothing()
    {
        var baseDir = MakeTempDir();
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", null, sink);

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

        var written = RunResultsWriter.WriteRun(baseDir, "Amp", empty, sink);

        Assert.Empty(written);
        Assert.False(Directory.Exists(Path.Combine(baseDir, "results")));
        Assert.Empty(sink.Successes);
        Assert.Empty(sink.Warnings);
    }

    // ── WriteRun — returns single flat path ────────────────────────────────

    [Fact]
    public void WriteRun_ReturnsFlatNpyPath()
    {
        var baseDir = MakeTempDir();
        var key     = "TestAmp";
        var sink    = new FakeSink();

        var written = RunResultsWriter.WriteRun(baseDir, key, MakeGroupedDataSet(), sink);

        Assert.Single(written);
        var expected = Path.GetFullPath(Path.Combine(baseDir, "results", "TestAmp.npy"));
        Assert.Equal(expected, written[0]);
    }

    // ── MigrateOldLayout (R-res-11) ────────────────────────────────────────────

    [Fact]
    public void MigrateOldLayout_MovesSubdirRunNpy_ToFlatFile_RemovesEmptyDir()
    {
        var baseDir = MakeTempDir();
        var results = Path.Combine(baseDir, "results");
        var oldDir  = Path.Combine(results, "Amp");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "run.npy"), "amp-content");

        var sink     = new FakeSink();
        var migrated = RunResultsWriter.MigrateOldLayout(results, sink);

        Assert.Equal("Amp", Assert.Single(migrated));
        Assert.Equal("amp-content", File.ReadAllText(Path.Combine(results, "Amp.npy")));
        Assert.False(Directory.Exists(oldDir), "the now-empty old subdirectory must be removed");
        Assert.Single(sink.Successes);
    }

    [Fact]
    public void MigrateOldLayout_RemovesOrphanedSourceMarker()
    {
        var baseDir = MakeTempDir();
        var results = Path.Combine(baseDir, "results");
        var oldDir  = Path.Combine(results, "Amp");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "run.npy"), "amp-content");
        File.WriteAllText(Path.Combine(oldDir, ".source"), "Amp");

        RunResultsWriter.MigrateOldLayout(results, new FakeSink());

        Assert.Empty(Directory.GetFiles(results, ".source", SearchOption.AllDirectories));
    }

    [Fact]
    public void MigrateOldLayout_ThreeSchematicsPlusBaseline_AllMigrateIndependently()
    {
        var baseDir = MakeTempDir();
        var results = Path.Combine(baseDir, "results");

        foreach (var key in new[] { "Amp", "Mixer", "Filter.tb2" })
        {
            var dir = Path.Combine(results, key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "run.npy"), key + "-content");
            File.WriteAllText(Path.Combine(dir, ".source"), key);
        }
        // A pre-existing flat user-named baseline must survive untouched.
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "baseline_v1.npy"), "baseline-content");

        var migrated = RunResultsWriter.MigrateOldLayout(results, new FakeSink());

        Assert.Equal(3, migrated.Count);
        Assert.Equal("Amp-content", File.ReadAllText(Path.Combine(results, "Amp.npy")));
        Assert.Equal("Mixer-content", File.ReadAllText(Path.Combine(results, "Mixer.npy")));
        Assert.Equal("Filter.tb2-content", File.ReadAllText(Path.Combine(results, "Filter.tb2.npy")));
        Assert.Equal("baseline-content", File.ReadAllText(Path.Combine(results, "baseline_v1.npy")));
        Assert.Empty(Directory.GetDirectories(results));
    }

    [Fact]
    public void MigrateOldLayout_FlatFileAlreadyExists_LeavesOldCopyInPlace_WarnsInsteadOfOverwriting()
    {
        var baseDir = MakeTempDir();
        var results = Path.Combine(baseDir, "results");
        var oldDir  = Path.Combine(results, "Amp");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "run.npy"), "old-content");
        File.WriteAllText(Path.Combine(results, "Amp.npy"), "already-flat-content");

        var sink     = new FakeSink();
        var migrated = RunResultsWriter.MigrateOldLayout(results, sink);

        Assert.Empty(migrated);
        Assert.Equal("already-flat-content", File.ReadAllText(Path.Combine(results, "Amp.npy")));
        Assert.Equal("old-content", File.ReadAllText(Path.Combine(oldDir, "run.npy")));
        Assert.Single(sink.Warnings);
    }

    [Fact]
    public void MigrateOldLayout_AlreadyFlatWorkspace_IsANoOp()
    {
        var baseDir = MakeTempDir();
        var results = Path.Combine(baseDir, "results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "Amp.npy"), "content");

        var sink     = new FakeSink();
        var migrated = RunResultsWriter.MigrateOldLayout(results, sink);

        Assert.Empty(migrated);
        Assert.Empty(sink.All);
    }

    [Fact]
    public void MigrateOldLayout_NoResultsDirectory_ReturnsEmpty_NeverThrows()
    {
        var baseDir = MakeTempDir();
        var migrated = RunResultsWriter.MigrateOldLayout(Path.Combine(baseDir, "results"), new FakeSink());
        Assert.Empty(migrated);
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
