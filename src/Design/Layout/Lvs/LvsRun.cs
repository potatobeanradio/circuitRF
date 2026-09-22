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

/// <summary>
/// One global variable, replaced before the schematic is elaborated — <c>--set var=expr</c>
/// (R-lvs11-2d).
/// </summary>
/// <param name="Name">The variable, as the design spells it.</param>
/// <param name="Expression">What it becomes, as an EXPRESSION rather than a number: it is parsed
/// by the one expression engine in the design's own scope, exactly as the run verbs' own
/// <c>--set</c> is (<c>cli.md</c> §5).</param>
public readonly record struct LvsGlobalOverride(string Name, string Expression);

/// <summary>What was asked for — <b>one object, passed to both sides</b> (R-lvs6-1a).</summary>
public sealed record LvsRunOptions
{
    /// <summary>
    /// Named before <see cref="Default"/> on purpose: a static field initializer runs in TEXTUAL
    /// order, so a <c>new()</c> above this line would read it as null and every default-options run
    /// would fault on the first cell it classified.
    /// </summary>
    private static readonly IReadOnlySet<string> NoCells =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// <c>--flat</c> (R-lvs9-4a): every placed cell is a leaf, its copper joins one partition, and
    /// nothing is descended into.
    /// </summary>
    /// <remarks>
    /// <b>Always available and always correct</b>, which is what makes it the reference the
    /// hierarchical reading is gated against (R-lvs9-4b): on a design whose cells keep to their
    /// declared pins, the two runs report the same findings, and where they do not the copper is
    /// joined somewhere nobody declared.
    /// </remarks>
    public bool Flat { get; init; }

    /// <summary>
    /// <c>--flatten-cell &lt;name&gt;</c> (R-lvs9-3d) — by cell folder name, case insensitively.
    /// </summary>
    /// <remarks>
    /// <b>The escape hatch is explicit and per cell</b>, and using it is reported at info so a
    /// design that quietly flattens everything is visible. The second spelling is the cell's own
    /// <c>FlattenForLvs</c>, for a cell that is permanently like this.
    /// </remarks>
    public IReadOnlySet<string> FlattenCells { get; init; } = NoCells;

    /// <summary>
    /// <c>--recognize</c> (R-lvs14-1d) — read devices out of COPPER as well as out of instances.
    /// </summary>
    /// <remarks>
    /// <b>Default off, and it stays off for a design circuitRF authored.</b> circuitRF's layout is
    /// instance-bearing, so the primary reading is correspondence rather than recognition; tier 3
    /// is for artwork that carries no instances at all — a hand-drawn MMIC device, a GDSII import
    /// whose hierarchy was flattened away, a Gerber board read back as polygons.
    ///
    /// <para>A technology with no <c>DeviceRules</c> block recognises nothing however this is set,
    /// and saying so is not an error.</para>
    /// </remarks>
    public bool Recognize { get; init; }

    /// <summary>R-lvs9-5d's ceiling. See <see cref="LvsHierarchyContext.MaxDevices"/>.</summary>
    public long MaxDevices { get; init; } = LayoutDesignFlatten.HardCeiling;

    /// <summary>
    /// <c>--set var=expr</c> (R-lvs11-2d) — globals REPLACED in the schematic's own scope before it
    /// is elaborated, in the order given.
    /// </summary>
    /// <remarks>
    /// <b>Why LVS takes an override at all.</b> A design whose component values depend on a swept
    /// or configured global has more than one correct layout, and a caller comparing the artwork of
    /// one corner against a drawing that defaults to another must be able to say which. The override
    /// lands in the same place every run verb's does — the <c>TestBench</c>'s own globals, before
    /// elaboration — so everything derived from it re-derives.
    ///
    /// <para><b>It reaches the SCHEMATIC side only</b>, and it reaches the resolved parameter VALUES
    /// brief 10 compares rather than the topology: LVS reads its topology from the drawing's own
    /// instances and nets, never from the elaborated netlist (R-lvs4-2a). Nothing here is applied to
    /// the artwork, which is already drawn.</para>
    /// </remarks>
    public IReadOnlyList<LvsGlobalOverride> Set { get; init; } = [];
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
        => Run(cellDir, options, control, null);

    /// <summary>
    /// The same comparison, on a hierarchy already in progress — <b>how a sub-cell is compared</b>
    /// (R-lvs9-6a). Internal because a caller outside this file cannot have a context to pass.
    /// </summary>
    internal static LvsRunResult Run(
        string cellDir, LvsRunOptions? options, RunControl? control, LvsHierarchyContext? carried)
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
        return Run(view, clay, cellDir, resolution.Tech, model, csch, bench, options, control, carried);
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
        LvsRunOptions? options = null, RunControl? control = null,
        LvsHierarchyContext? carried = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(schematic);
        options ??= LvsRunOptions.Default;
        control?.ThrowIfCancellationRequested();

        // ── The hierarchy this level belongs to (brief 9) ──────────────────────────────────────
        //
        // ONE context for the whole tree: one cache, one set of counters, one descent stack. A
        // cell placed inside two different sub-cells is extracted once because both descents share
        // this object, which is the whole of R-lvs9-1 in one line.
        var hierarchy = carried ?? new LvsHierarchyContext
        {
            Flat = options.Flat,
            FlattenCells = options.FlattenCells,
            MaxDevices = options.MaxDevices,
            Recognize = options.Recognize,
        };

        // This cell is now ON the descent stack, so a cell that places itself is flattened and
        // reported rather than recursed into until the stack runs out.
        string identity = Path.GetFullPath(cellDir);
        bool entered = hierarchy.Descending.Add(identity);
        try
        {
        hierarchy.Counters.Extractions++;

        string schematicDoc = Path.GetFileName(cschPath);
        string layoutDoc    = Path.GetFileName(clayPath);

        // ── The assembly's bond wires, BEFORE either side is read (brief 13) ──────────────────
        //
        // R-lvs13-4c: an array list that has moved under a placed instance is reported and that
        // instance is then left out of BOTH netlists, so the finding is one line rather than 2M
        // findings that are all real and all about the wrong thing. That decision has to be taken
        // before either read, which is why it is here and not inside one of them.
        var (wbonds, wbondNotes) = AssemblyRead.Resolve(schematic, cschPath, clayPath);
        var excluded = AssemblyRead.Excluded(wbonds);

        control?.BeginStage("Reading the schematic");
        var schematicNetlist = SchematicRead.Read(
            schematic, cschPath, options.IncludeFixture, isTestBenchCell, options.Set, excluded);
        control?.ThrowIfCancellationRequested();

        // R-lvs3-3d's dangling SchematicId needs the other side's names, and NOTHING else here
        // does: supplying them cannot change a net, a terminal or a device.
        //
        // The MODULES it reads are filled into `hierarchy` as it walks, which is what the descent
        // below consumes — a netlist carrying them would be a netlist the comparator could tell
        // apart from a schematic's, which is the one thing LvsNetlist may not be.
        control?.BeginStage("Reading the layout");
        var level = new LvsHierarchyContext
        {
            Flat = hierarchy.Flat,
            FlattenCells = hierarchy.FlattenCells,
            MaxDevices = hierarchy.MaxDevices,
            Recognize = hierarchy.Recognize,
            Counters = hierarchy.Counters,
            Cache = hierarchy.Cache,
            Descending = hierarchy.Descending,
        };
        // R-lvs13-2c/2d: how a DIE's own technology resolves. Supplied rather than null — without
        // it the flatten never asks, so two technologies' layer numbers are taken at face value and
        // an MMIC's layer 1 becomes the board's by coincidence of integers. With it, the flatten
        // reconciles what it confidently can and leaves the rest PENDING, which `LayoutRead` already
        // reports as a refusal for that sub-cell (`lvs.layout.pending-layer-mapping`).
        var techCache = new TechnologyCache();
        var layoutNetlist = LayoutRead.Read(
            layout, clayPath, cellDir, tech, out var geometry,
            (techRef, subLayoutDir) => TechnologyResolver.ResolveForDocument(
                techRef, Path.Combine(subLayoutDir, "x.clay"), null, techCache).Resolution,
            schematicNetlist.Devices.Select(d => d.Path).ToHashSet(StringComparer.Ordinal),
            level, wbonds);
        control?.ThrowIfCancellationRequested();

        control?.BeginStage("Reducing");
        var (reducedSchematic, schematicLog) = LvsReduce.Apply(schematicNetlist, options.Reduce);
        var (reducedLayout, layoutLog)       = LvsReduce.Apply(layoutNetlist, options.Reduce);
        control?.ThrowIfCancellationRequested();

        control?.BeginStage("Comparing");
        var comparison = LvsCompare.Compare(reducedSchematic, reducedLayout, control);

        // ── R-lvs10-1a: the property pass, AFTER the topology and on the pairs it produced ──────
        //
        // Not inside `LvsCompare`, and not a second comparison either. Two identical netlists have
        // to compare identically whether or not one of them was drawn somewhere (R-lvs3-1a), and a
        // geometric tolerance is one DBU of the DOCUMENT's own database — so the pass takes the
        // resolution and the technology's overrides from here, where both are known, and hands back
        // divergences that join the ones brief 7 concluded. They are the same kind of thing and
        // belong in one ordered list; a second one would be a second answer to "what did the
        // comparison find", differing in what it counted.
        var properties = LvsProperties.Compare(
            reducedSchematic, reducedLayout, comparison,
            LvsPropertyTolerances.For(tech, layout.DbuPerMicron));

        if (properties.Count > 0)
            comparison = comparison with
            {
                Findings = LvsCompare.Ordered([.. comparison.Findings, .. properties]),
            };

        // Both sides' reduction lines are printed TOGETHER (R-lvs6-5a): an asymmetry between them
        // is often the first clue to what is actually wrong.
        var notes = new List<Diagnostic>();
        notes.AddRange(wbondNotes);
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

        // ── R-lvs9-1 and R-lvs9-6a: each DISTINCT cell compared once, cell against cell ────────
        var cells = Descend(level, options, control);
        if (cells.Count > 0)
            findings = LvsMarker.Ordered(
                findings.Concat(cells.SelectMany(
                    c => c.Result.Findings
                          .Where(f => !f.IsRunLevel)
                          .Select(f => LvsHierarchy.Within(c.Placements[0], f)))));

        // ── R-lvs12-4f/4g: the document's own waivers, HONOURED and never written ──────────────
        //
        // Here rather than in either surface, so the CLI and the panel cannot disagree about what a
        // waiver suppresses — the same argument that put the whole comparison behind one entry
        // point. A sub-cell's findings arrive already marked against ITS `.clay`, which is the
        // right answer: a waiver is a statement about the drawing it is stored on.
        //
        // The run writes nothing. This reads `layout.LvsWaivers` and hands back findings with
        // `Waived` set; the list on the document is untouched, which is `check`'s own rule
        // (R-aut4-6) and is what keeps a run usable on a read-only tree.
        findings = LvsWaivers.Apply(findings, layout.LvsWaivers);

        return new LvsRunResult(
            findings, comparison, reducedSchematic, reducedLayout, geometry,
            schematicLog, layoutLog, counts, tech?.Name)
        {
            Cells = cells,
            Hierarchy = level.Counters,
        };
        }
        finally { if (entered) hierarchy.Descending.Remove(identity); }
    }

    /// <summary>
    /// Every sub-cell this level places, compared — <b>once per distinct (cell, parameter-set)
    /// pair</b> (R-lvs9-1a/1d), however many times it is placed.
    /// </summary>
    /// <remarks>
    /// <b>The cache is the feature.</b> A board with forty placements of one module is one
    /// comparison and thirty-nine lookups, and that is what makes 3,000 parts of 40 types tractable
    /// (note R-lvs-46). The key is the cell's own content (<see cref="LvsCellKey"/>), so two
    /// placements of one PCell with different parameters are correctly two comparisons.
    ///
    /// <para><b>A cell's findings are re-reported under its FIRST placement.</b> A defect inside a
    /// cell is one defect in one drawing — fixing the cell fixes every placement of it — so
    /// reporting it forty times would be forty rows about one thing. Which placements they are is
    /// on <see cref="LvsCellComparison.Placements"/>, all of them, and brief 12 opens the cell.</para>
    /// </remarks>
    private static IReadOnlyList<LvsCellComparison> Descend(
        LvsHierarchyContext level, LvsRunOptions options, RunControl? control)
    {
        if (level.Modules.Count == 0) return [];

        var cells = new List<LvsCellComparison>();
        foreach (var group in level.Modules.GroupBy(m => m.Key, StringComparer.Ordinal))
        {
            control?.ThrowIfCancellationRequested();

            var placements = group.Select(m => m.Path).ToArray();
            var first = group.First();

            if (level.Cache.TryGetValue(first.Key, out var cached))
            {
                level.Counters.CacheHits += placements.Length;
                cells.Add(cached with { Placements = placements });
                continue;
            }

            control?.BeginStage($"Comparing '{LvsHierarchyContext.NameOf(first.CellDir)}'");
            var result = Run(first.CellDir, options, control, level);

            level.Counters.CacheHits += placements.Length - 1;
            var comparison = new LvsCellComparison(
                first.CellDir, LvsHierarchyContext.NameOf(first.CellDir), placements, result);

            level.Cache[first.Key] = comparison;
            cells.Add(comparison);
        }

        return cells;
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
