// ================================================================
//  AutoOpenAuthoredDataDisplayTests.cs
//
//  Owner-reported, 2026-09-17: simulating a bench that already SHIPS a Data
//  Display beside it did not open that display — it auto-created a second,
//  empty one under results/. The lookup searched results/ alone, which was the
//  same question as "is there a display for this bench?" only for as long as
//  the only displays that existed were ones the application had written itself.
//
//  The order is a path convention, so it lives on RunResultsWriter beside the
//  other results-path conventions and is exercised directly here — the view
//  model's own method is private on a class that cannot be constructed
//  headlessly, and a test that MIRRORED its body could not have caught this.
// ================================================================

using System.IO;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AutoOpenAuthoredDataDisplayTests : IDisposable
{
    private readonly string _ws;

    public AutoOpenAuthoredDataDisplayTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), $"crf_authored_cdd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_ws, "results"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    /// <summary>
    /// The reported bug, as the run sees it: a display authored beside the bench is the first
    /// candidate that EXISTS, so the run opens it instead of creating one under results/.
    /// </summary>
    [Fact]
    public void AnAuthoredDisplayBesideTheBenchIsFoundBeforeTheResultsFolder()
    {
        var authored = Path.Combine(_ws, "TxFetFinal.cdd");
        File.WriteAllText(authored, "{}");

        var candidates = RunResultsWriter.AutoDisplayCandidates(
            _ws, Path.Combine(_ws, "results"), "TxFetFinal");

        var found = candidates.FirstOrDefault(File.Exists);
        Assert.Equal(Path.GetFullPath(authored), found);
    }

    /// <summary>
    /// And the behaviour that was already right stays right: with nothing beside the bench, the
    /// auto-created display under results/ is still what a re-run picks up.
    /// </summary>
    [Fact]
    public void WithNothingBesideTheBenchTheResultsFolderDisplayIsStillFound()
    {
        var created = Path.Combine(_ws, "results", "TxFetFinal.cdd");
        File.WriteAllText(created, "{}");

        var candidates = RunResultsWriter.AutoDisplayCandidates(
            _ws, Path.Combine(_ws, "results"), "TxFetFinal");

        var found = candidates.FirstOrDefault(File.Exists);
        Assert.Equal(Path.GetFullPath(created), found);
    }

    /// <summary>
    /// The LAST candidate is where a new display gets created, so it must always be the results/
    /// spelling — including when a caller passes results/ as its own base (the scratch shape),
    /// where the two candidates collapse to one rather than becoming a duplicate pair.
    /// </summary>
    [Fact]
    public void TheCreationPathIsAlwaysTheResultsSpellingAndDuplicatesCollapse()
    {
        var results = Path.Combine(_ws, "results");

        var pair = RunResultsWriter.AutoDisplayCandidates(_ws, results, "Bench");
        Assert.Equal(2, pair.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(results, "Bench.cdd")), pair[^1]);

        var collapsed = RunResultsWriter.AutoDisplayCandidates(results, results, "Bench");
        Assert.Single(collapsed);
        Assert.Equal(Path.GetFullPath(Path.Combine(results, "Bench.cdd")), collapsed[^1]);
    }
}
