// EXTRACT EACH CELL ONCE, STITCH AT THE BOUNDARY — brief-lvs-9-hierarchy.md, docs/design/lvs.md
// §4.5 and §7.
//
//   for each DISTINCT resolved cell:   extract once, cache by content key
//   at each level:                     boundary pin -> parent net (transform, then point-in-piece)
//   compare:                           cell against cell, board against board
//
// ── THE PERFORMANCE IS A SIDE EFFECT; THE REASON IS CORRECTNESS ───────────────────────────────
//
// A 3,000-part board becomes 40 cell extractions and one board-level one rather than 3,000, and
// that is worth having. It is not why this exists. Flattening destroys the instance identity the
// whole comparison is built on: a report that cannot name `U3/M1` is a report about a design
// nobody drew.
//
// ── WHAT A "CELL" IS HERE, AND WHY THE TEST IS THE ONE IT IS ──────────────────────────────────
//
// A cell circuitRF can COMPARE is a cell circuitRF descends into: one whose folder resolves both a
// primary schematic and a primary layout, which is exactly `LvsRun.Run`'s own precondition. Every
// other placed cell — a land pattern, a generated part, a via fence — has a layout and no drawing
// of its own, so there is nothing to compare it against and it stays what it already was: a leaf
// device, or interconnect.
//
// That test is deliberately NOT "does it have instances of its own". A land pattern nested three
// deep is still artwork; a two-resistor module with a schematic beside it is a design. The
// difference is whether somebody drew the inside, and the schematic view IS that answer.
//
// ── THE CACHE IS PER RUN AND IN MEMORY (R-lvs9-1c) ────────────────────────────────────────────
//
// Nothing is written — LVS is read-only on `check`'s terms (R-aut4-6). A cache file would be the
// one part of this feature that could not run on a read-only tree or on a workspace another
// process has open, and it would be stale in exactly the cases nothing reports.

using System.Linq;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// What a hierarchical run actually did — <b>counters, never clocks</b> (R-lvs9-5b,
/// <c>WireSweepCounters</c>' shape for <c>WireSweepCounters</c>' reason).
/// </summary>
/// <remarks>
/// Each of these catches the regression that matters and none of them measures the machine. An
/// extraction count that tracks PLACEMENTS rather than distinct cells is the O(n) reading the
/// hierarchy exists to replace; a query count that tracks SHAPES rather than pins is brief 2's
/// index being bypassed; a union count above the extraction count is <c>LayerRegions.Build</c>
/// being called per placement (note R-lvs-48).
/// </remarks>
public sealed class LvsHierarchyCounters
{
    /// <summary>How many cell netlists were built — <b>the root counts as one</b>. R-lvs9-5a's
    /// "extractions == distinct (cell, parameter-set) pairs".</summary>
    public int Extractions { get; internal set; }

    /// <summary>How many placements took a cached extraction instead of building one. A board with
    /// 40 placements of one cell reports 39.</summary>
    public int CacheHits { get; internal set; }

    /// <summary>How many per-layer copper unions were performed for an extraction — one per
    /// <c>CopperPieces.Build</c>, which unions once per call by <c>LayerRegions.Build</c>'s own
    /// construction. R-lvs9-5a2's third counter.</summary>
    public int LayerUnions { get; internal set; }

    /// <summary>How many point-in-piece queries located a PIN — R-lvs9-5a. Every one of them is
    /// answered through brief 2's index; the partition's own build-time lookups are not counted
    /// here, because they are a property of the geometry rather than of the netlist.</summary>
    public int PinQueries { get; internal set; }

    /// <summary>How many placements were checked for undeclared contact — R-lvs9-3e's "one extra
    /// boolean per instance per layer", counted per PLACEMENT because that is the unit the check is
    /// asked for: two placements of one cell can overlap entirely different parent metal.</summary>
    public int ContactChecks { get; internal set; }

    /// <summary>The same numbers, plus <paramref name="other"/>'s — a sub-cell's run folded into
    /// its parent's, so the totals are the whole tree's.</summary>
    internal void Add(LvsHierarchyCounters other)
    {
        Extractions   += other.Extractions;
        CacheHits     += other.CacheHits;
        LayerUnions   += other.LayerUnions;
        PinQueries    += other.PinQueries;
        ContactChecks += other.ContactChecks;
    }

    /// <summary>The sentence a caller reports the cost with — the CLI verb's banner and the
    /// panel's status line (briefs 11 and 12), so the two cannot spell one run two ways.</summary>
    public string Describe() =>
        $"{Extractions} extraction(s), {CacheHits} from cache, {LayerUnions} layer union(s), "
      + $"{PinQueries} pin lookup(s)";
}

/// <summary>
/// One root placement read as a cell of its own — <b>one <see cref="LvsDevice"/> in the parent,
/// whose terminals are its boundary pins</b> (R-lvs9-2b).
/// </summary>
/// <param name="Path">The device's own path, as the report spells it: <c>U3</c>, <c>U3[0,1]</c>.</param>
/// <param name="CellDir">The resolved cell folder, absolute.</param>
/// <param name="Key">Its content key — R-lvs9-1b. Two placements with one key are one
/// extraction.</param>
/// <param name="DeviceIndex">Its position in the layout netlist, before reduction.</param>
public readonly record struct LvsModulePlacement(string Path, string CellDir, string Key, int DeviceIndex);

/// <summary>
/// One sub-cell compared on its own account — <b>cell against cell</b> (R-lvs9-6a), and the answer
/// every placement of it shares.
/// </summary>
/// <param name="CellDir">The cell folder, absolute.</param>
/// <param name="CellName">What to call it in a report.</param>
/// <param name="Placements">Every parent path that placed it, in the order they were read.</param>
/// <param name="Result">What comparing it concluded. <b>The same object for every placement</b> —
/// a cell is compared once, however many times it is placed.</param>
public sealed record LvsCellComparison(
    string CellDir, string CellName, IReadOnlyList<string> Placements, LvsRunResult Result);

/// <summary>
/// What one hierarchical run carries down the tree: what was asked for, what has already been
/// extracted, and what it cost.
/// </summary>
/// <remarks>
/// <b>Passed in and filled, rather than returned.</b> The reading is a walk over one design's
/// instances and the decisions it takes — this cell is a module, this one is flattened, this one
/// touches the parent — are needed by the caller that drives the descent, not by the netlist. A
/// netlist carrying them would be a netlist the comparator could tell apart from a schematic's,
/// which is the one thing <see cref="LvsNetlist"/> may not be.
/// </remarks>
public sealed class LvsHierarchyContext
{
    /// <summary><c>--flat</c> (R-lvs9-4a). Every cell is read as it always was: one flat graph,
    /// every sub-cell's copper in the partition, no descent.</summary>
    public bool Flat { get; init; }

    /// <summary><c>--flatten-cell &lt;name&gt;</c> (R-lvs9-3d), by cell folder name, case
    /// insensitively.</summary>
    public IReadOnlySet<string> FlattenCells { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>--recognize</c> (R-lvs14-1d) — <b>tier-3 geometric device recognition, and it is OFF
    /// unless a run asks for it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Off per RUN and off per TECHNOLOGY, and both halves are needed.</b> Turning it on for a
    /// design circuitRF authored would re-recognise devices it already knows, from geometry, less
    /// reliably than reading the instance that is right there; and a technology with no
    /// <c>DeviceRules</c> block cannot recognise anything whatever a run asks for, which is not an
    /// error.
    ///
    /// <para>It rides on the context rather than being threaded separately for
    /// <see cref="Flat"/>'s reason: one object says what the whole tree was asked for, so a
    /// sub-cell is read the same way its parent was.</para>
    /// </remarks>
    public bool Recognize { get; init; }

    /// <summary>R-lvs9-5. Shared down the whole tree, so the totals are the run's.</summary>
    public LvsHierarchyCounters Counters { get; init; } = new();

    /// <summary>
    /// R-lvs9-5d's ceiling — <b>the existing 500,000 reused rather than re-derived</b>. Above it
    /// the read refuses and says the number; a pathological design costs a message, not a hang.
    /// </summary>
    /// <remarks>
    /// It is on the CONTEXT rather than a constant so a gate can set it to three and assert the
    /// refusal without building half a million devices, which is the only way to test a ceiling
    /// whose real value is chosen to be unreachable.
    /// </remarks>
    public long MaxDevices { get; init; } = LayoutDesignFlatten.HardCeiling;

    /// <summary>Which cells have already been extracted, by content key — R-lvs9-1b.</summary>
    public Dictionary<string, LvsCellComparison> Cache { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>The cell folders currently on the descent stack. A cell that places itself is a
    /// cycle, and it is reported rather than recursed into.</summary>
    public HashSet<string> Descending { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Filled by the layout read: the placements it read as cells of their own.</summary>
    public List<LvsModulePlacement> Modules { get; } = [];

    /// <summary>What a cell folder is CALLED — its own folder name.</summary>
    public static string NameOf(string cellDir)
        => new DirectoryInfo(cellDir.TrimEnd(Path.DirectorySeparatorChar)).Name;

    /// <summary>
    /// How a cell folder is spelled in <see cref="Descending"/> — <b>one rule, both sides of the
    /// test</b>.
    /// </summary>
    /// <remarks>
    /// The stack is entered with the path the caller gave and consulted with the path the resolver
    /// produced. <c>Path.GetFullPath</c> keeps a trailing separator and the resolver never writes
    /// one, so <c>"…/Amp/"</c> and <c>"…/Amp"</c> would be two cells to the set and one to
    /// everybody else — and the cell that places itself would then recurse until the stack runs
    /// out rather than being flattened and reported.
    /// </remarks>
    public static string IdentityOf(string cellDir)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
}

/// <summary>The rules that decide what a placed cell IS — R-lvs9-2c.</summary>
public static class LvsHierarchy
{
    /// <summary>
    /// Whether <paramref name="cellDir"/> is a design of its own: a folder resolving BOTH a primary
    /// schematic and a primary layout.
    /// </summary>
    /// <remarks>
    /// <b>It is exactly <see cref="LvsRun.Run(string, LvsRunOptions, CircuitRF.Engine.RunControl)"/>'s
    /// own precondition</b>, and that is not a coincidence — descending into a cell means running
    /// the comparison on it, so a cell the comparison would decline is a cell there is no point
    /// descending into. Asking the same question in two spellings is how the two would come to
    /// disagree about which cells are compared.
    /// </remarks>
    public static bool IsComparableCell(string? cellDir)
    {
        if (cellDir is not { Length: > 0 } dir || !Directory.Exists(dir)) return false;
        try
        {
            return CellFolder.ResolvePrimary(dir, ViewType.Schematic).ResolvedName is { Length: > 0 }
                && CellFolder.ResolvePrimary(dir, ViewType.Layout).ResolvedName is { Length: > 0 };
        }
        catch (Exception) { return false; }   // an unreadable cell is reported on its own account
    }

    /// <summary>
    /// Whether the cell's own <c>.ccell</c> asks to be flattened for LVS — R-lvs9-3d's second
    /// spelling, for a cell that is permanently like this: a shield frame, a module whose ground is
    /// genuinely continuous with the board's.
    /// </summary>
    public static bool DeclaresFlattenForLvs(string? cellDir)
    {
        if (cellDir is not { Length: > 0 } dir) return false;
        try
        {
            string path = Path.Combine(dir, CellFolder.CcellFileName);
            return File.Exists(path) && CellPersistence.LoadFromFile(path).FlattenForLvs;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// <paramref name="finding"/> as the PARENT reports it: every object, and every argument that
    /// names one, prefixed with the placement that put the cell there.
    /// </summary>
    /// <remarks>
    /// <b>The arguments and the objects together, never one of them</b> — the sentence a user reads
    /// comes out of the arguments and the row they click comes out of the objects, so prefixing one
    /// alone produces a report whose two halves name different parts. A finding that names nothing
    /// (the run summary, a ground reminder) is left exactly as the cell stated it.
    ///
    /// <para>The MARKER is dropped rather than moved. It is in the sub-cell's own coordinate frame,
    /// and the honest transform is the placement's — which this function does not have, because a
    /// finding is re-reported once for a cell and a cell may be placed forty times in forty
    /// places. Brief 12 opens the cell to show it.</para>
    /// </remarks>
    public static LvsFinding Within(string placementPath, LvsFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (placementPath is not { Length: > 0 } prefix) return finding;
        if (finding.Objects.Count == 0) return finding;

        var arguments = new Dictionary<string, object?>(finding.Diagnostic.Arguments, StringComparer.Ordinal);
        foreach (string name in PathArguments)
            if (arguments.TryGetValue(name, out object? value) && value is string text && text.Length > 0)
                arguments[name] = prefix + "/" + text;

        var diagnostic = finding.Diagnostic with { Arguments = arguments };
        var objects = finding.Objects.Select(o => prefix + "/" + o).ToArray();

        return new LvsFinding(diagnostic, objects, [], Bbox.Empty,
                              LvsMarker.Key(diagnostic.Id, objects, Bbox.Empty))
        {
            Waived = finding.Waived,
            WaiverReason = finding.WaiverReason,

            // The cell's own sign-off travels with the finding, because the parent's waiver list is
            // keyed on paths that name the PLACEMENT and can never match the cell's own key.
            InheritedWaiver = finding.InheritedWaiver
                           ?? (finding.Waived ? finding.WaiverReason ?? "" : null),
        };
    }

    /// <summary>Which of a diagnostic's typed arguments name a device — the same three
    /// <c>LvsReport</c>'s own table reads.</summary>
    private static readonly string[] PathArguments = ["path", "schematicPath", "layoutPath"];

    /// <summary>
    /// Every reason a cell that COULD have been read hierarchically was not — R-lvs9-2c, said once
    /// so the info line and the decision cannot drift apart.
    /// </summary>
    internal static class FlattenReason
    {
        public const string Asked = "it was named for flattening";
        public const string Declared = "its cell declares FlattenForLvs";
        public const string NotADevice =
            "the drawing has no cell instance for it — it is a layout-only module";
        public const string Cycle = "it is placed inside itself";
        public const string Flat = "the run was asked for a flat comparison";
    }

}
