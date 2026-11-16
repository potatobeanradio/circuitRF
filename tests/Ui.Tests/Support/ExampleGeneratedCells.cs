using System.Collections.Concurrent;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Builds the generated cells a shipped layout places, into its workspace's <c>.generated-cells</c> folder, as opening it in
/// the application does. That folder is git-ignored, so on a fresh clone it does not exist: a test that reads such a
/// layout and found its cells on disk was reading whatever an earlier session had left there, and on a clean machine it
/// read instances that resolve to nothing. The examples' own kits are trusted here, since they ship in this repository.
/// </summary>
internal static class ExampleGeneratedCells
{
    private static readonly ConcurrentDictionary<string, Lazy<GeneratedCellsRunSummary>> Done = new(StringComparer.Ordinal);

    private static readonly ConcurrentBag<GeneratedCellsRun> Runs = [];

    internal sealed record GeneratedCellsRunSummary(int Built, IReadOnlyList<string> Unbuildable);

    /// <summary>The cells <paramref name="clayPath"/> places, built once per test run.</summary>
    public static GeneratedCellsRunSummary Ensure(string clayPath)
        => Done.GetOrAdd(Path.GetFullPath(clayPath), p => new Lazy<GeneratedCellsRunSummary>(() =>
        {
            // Kept, never disposed: a layout whose reference names an older build of a cell (the kit or circuitRF's own PCell
            // package has changed since it was saved) resolves through the run's redirect to the rebuilt one, and that redirect
            // lasts as long as the run does — as it does in the application while the document is open.
            var run = GeneratedCellsRun.Prepare(p, new GeneratedCellRunOptions
            {
                Target = GeneratedCellTarget.Disk,
                Trust  = _ => PCellTrustDecision.Allowed,
            });
            // A cell that could not be built is said here, by the cell, rather than surfacing later as a layout that draws nothing.
            Assert.True(run.Unbuildable.Count == 0, string.Join(" ", run.Unbuildable.Select(u => u.Sentence)));
            Runs.Add(run);
            return new GeneratedCellsRunSummary(run.CellsBuilt, []);
        })).Value;

    /// <summary>The cells the layout of the setup at <paramref name="cemPath"/> places; nothing when it resolves no layout.</summary>
    public static void EnsureForCem(string cemPath)
    {
        var setup = EmSetupPersistence.LoadFromFile(cemPath);
        string? cws = WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cemPath));
        if (EmSetupResolver.Resolve(cemPath, setup.LayoutRef, cws, new TechnologyCache()).Source is { } source)
            Ensure(source.AbsolutePath);
    }
}
