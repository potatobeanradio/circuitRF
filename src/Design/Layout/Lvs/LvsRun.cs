// The one door into LVS — brief-lvs-7-comparison.md R-lvs7-6b, overview §3.
//
//   cell folder ──► read both views ──► reduce both ──► compare ──► report ──► LvsRunResult
//
// ── WHY THERE IS EXACTLY ONE ENTRY POINT ─────────────────────────────────────────────────────
//
// The CLI verb (brief 11) calls this, the GUI panel (brief 12) calls this, and every test that is
// not a unit test calls this. That is not a convention — it is what makes "a design that passes
// headlessly passes when it is opened" TRUE rather than hoped for, and brief 11's own gate asserts
// the two results are equal object for object. A second path into the comparison is a second set
// of defaults, a second reduction mode and a second answer, and nothing would report the drift.
//
// A comment-stripped source scan over src/ holds it: nothing outside this file calls LvsCompare.
//
// ── IT READS, AND THAT IS ALL IT ADDS ────────────────────────────────────────────────────────
//
// There is no comparison logic in here. Resolving a cell folder's two primary views and its
// technology is the same walk `check`, `render` and Simulate already perform; the reading is
// briefs 3 and 4; the collapse is brief 6; the answer is brief 7. What this file owns is the
// ORDER those happen in and the single set of options both surfaces pass.

using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>What was asked for — <b>one object, passed to both sides</b> (R-lvs6-1a).</summary>
public sealed record LvsRunOptions
{
    /// <summary>Everything default: reduction on, the fixture excluded.</summary>
    public static readonly LvsRunOptions Default = new();

    /// <summary><c>--testbench</c> (R-lvs4-4c). The unit of comparison is the CELL by default,
    /// because that is what a layout draws.</summary>
    public bool IncludeFixture { get; init; }

    /// <summary>
    /// The collapse, <b>on by default</b> (R-lvs6-1f) and <b>the same object on both sides</b>.
    /// <see cref="LvsReduceOptions.NoReduce"/> is <c>--no-reduce</c>.
    /// </summary>
    public LvsReduceOptions Reduce { get; init; } = LvsReduceOptions.Default;
}

// The result model moved to LvsRunResult.cs when brief 8 landed: one comparison produces ONE
// result type, and it is the one the CLI and the panel consume. A second, thinner record here
// would be two answers to "what did the run conclude", differing in what they counted.

/// <summary>The single entry point (R-lvs7-6b).</summary>
public static class LvsRun
{
    /// <summary>
    /// Compares a cell folder's primary layout against its primary schematic.
    /// </summary>
    /// <param name="cellDir">The cell folder — the default unit of comparison (note R-lvs-29).</param>
    /// <param name="options">What was asked for; <see cref="LvsRunOptions.Default"/> when null.</param>
    /// <param name="control">Progress and cancellation. A cancelled run throws
    /// <see cref="OperationCanceledException"/>, returns nothing and writes nothing (R-lvs7-6c).</param>
    /// <remarks>
    /// <b>A cell with only one of the two views comes back EMPTY with an info line saying which
    /// is missing</b> (R-lvs11-2b) — it is the ordinary mid-design state, not a refusal, and a
    /// comparison that threw on it would be one nobody runs while a design is being drawn.
    /// </remarks>
    public static LvsRunResult Run(string cellDir, LvsRunOptions? options = null, RunControl? control = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellDir);

        string? csch = ViewPath(cellDir, ViewType.Schematic);
        string? clay = ViewPath(cellDir, ViewType.Layout);
        if (csch is null || clay is null)
            return Nothing(cellDir, csch is null ? ViewType.Schematic : ViewType.Layout);

        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        var view = LayoutPersistence.LoadFromFile(clay);

        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clay, null, new TechnologyCache());

        bool bench = IsTestBench(cellDir);
        return Run(view, clay, cellDir, resolution.Tech, model, csch, bench, options, control);
    }

    /// <summary>
    /// The same comparison over documents the caller already has in hand — <b>the one the other
    /// overload calls</b>, so there is still exactly one path through the pass.
    /// </summary>
    /// <param name="layout">The artwork.</param>
    /// <param name="clayPath">Where it was read from; a <c>CellRef</c> resolves against it.</param>
    /// <param name="cellDir">The cell folder both views belong to — what the layout's BOUNDARY
    /// pins are read against.</param>
    /// <param name="tech">The resolved technology. Null means no stackup and so no netlist.</param>
    /// <param name="schematic">The drawing.</param>
    /// <param name="cschPath">Where it was read from.</param>
    /// <param name="isTestBenchCell">Whether the owning <c>.ccell</c> says so (R-lvs4-4d).</param>
    /// <param name="options">What was asked for.</param>
    /// <param name="control">Progress and cancellation.</param>
    public static LvsRunResult Run(
        LayoutView layout, string clayPath, string cellDir, Technology? tech,
        SchematicEditModel schematic, string cschPath, bool isTestBenchCell = false,
        LvsRunOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(schematic);
        options ??= LvsRunOptions.Default;
        control?.ThrowIfCancellationRequested();

        string schematicDoc = Path.GetFileName(cschPath);
        string layoutDoc    = Path.GetFileName(clayPath);

        control?.BeginStage("Reading the schematic");
        var schematicNetlist = SchematicRead.Read(
            schematic, cschPath, options.IncludeFixture, isTestBenchCell);
        control?.ThrowIfCancellationRequested();

        // R-lvs3-3d's dangling SchematicId needs the other side's names, and NOTHING else here
        // does: supplying them cannot change a net, a terminal or a device.
        control?.BeginStage("Reading the layout");
        var layoutNetlist = LayoutRead.Read(
            layout, clayPath, cellDir, tech, out var geometry, null,
            schematicNetlist.Devices.Select(d => d.Path).ToHashSet(StringComparer.Ordinal));
        control?.ThrowIfCancellationRequested();

        control?.BeginStage("Reducing");
        var (reducedSchematic, schematicLog) = LvsReduce.Apply(schematicNetlist, options.Reduce);
        var (reducedLayout, layoutLog)       = LvsReduce.Apply(layoutNetlist, options.Reduce);
        control?.ThrowIfCancellationRequested();

        control?.BeginStage("Comparing");
        var comparison = LvsCompare.Compare(reducedSchematic, reducedLayout, control);

        // Both sides' reduction lines are printed TOGETHER (R-lvs6-5a): an asymmetry between them
        // is often the first clue to what is actually wrong.
        var notes = new List<Diagnostic>();
        notes.AddRange(schematicNetlist.Notes);
        notes.AddRange(layoutNetlist.Notes);
        notes.AddRange(schematicLog.Notes(schematicDoc));
        notes.AddRange(layoutLog.Notes(layoutDoc));

        var counts = new LvsCounts(
            new LvsSideCounts(schematicNetlist.Devices.Count, reducedSchematic.Devices.Count,
                              schematicNetlist.Nets.Count, reducedSchematic.Nets.Count),
            new LvsSideCounts(layoutNetlist.Devices.Count, reducedLayout.Devices.Count,
                              layoutNetlist.Nets.Count, reducedLayout.Nets.Count));

        var mode = options.Reduce.Enabled ? ReductionMode.On : ReductionMode.Off;

        control?.BeginStage("Reporting");
        var findings = LvsReport.Build(
            comparison, reducedSchematic, reducedLayout, geometry, notes, counts, tech?.Name, mode);

        return new LvsRunResult(
            findings, comparison, reducedSchematic, reducedLayout, geometry,
            schematicLog, layoutLog, counts, tech?.Name);
    }

    private static string? ViewPath(string cellDir, ViewType type)
        => CellFolder.ResolvePrimary(cellDir, type).ResolvedName is { Length: > 0 } name
            ? Path.Combine(CellFolder.SubFolderPath(cellDir, type), name)
            : null;

    /// <summary>The empty answer, and the one line that says why there is one.</summary>
    private static LvsRunResult Nothing(string cellDir, ViewType missing)
    {
        var note = LvsDiagnostics.ViewMissing(
            Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar)),
            CellFolder.SubFolderName(missing));
        var empty = LvsNetlist.Nothing([]);
        var log = new ReductionLog(false, 0, 0, 0, []);
        var comparison = new LvsComparison([], [], [], [], [], 0, 0, 0);

        // The summary still comes out (R-lvs8-6b): a run that said nothing at all is
        // indistinguishable from a broken command, and "no primary layout view" is the answer.
        var findings = LvsReport.Build(
            comparison, empty, empty, LvsGeometry.None, [note],
            LvsCounts.Nothing, null, ReductionMode.On);

        return new LvsRunResult(
            findings, comparison, empty, empty, LvsGeometry.None,
            log, log, LvsCounts.Nothing, null);
    }

    /// <summary>Whether the cell declares itself a bench — for R-lvs4-4d's info line, and for
    /// nothing else.</summary>
    private static bool IsTestBench(string cellDir)
    {
        string ccell = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccell)) return false;
        try { return CellPersistence.LoadFromFile(ccell).IsTestBench; }
        catch (Exception) { return false; }
    }
}
