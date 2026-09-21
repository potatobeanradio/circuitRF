// What one LVS run concluded — brief-lvs-8-findings.md R-lvs8-1, on `DrcRunResult`'s terms.
//
// ── EVERY FIELD OF DrcRunResult THAT EARNED ITS PLACE EARNS IT HERE (R-lvs8-1a) ───────────────
//
// Waived findings are STILL REPORTED and merely not counted; the TECHNOLOGY IS NAMED, because a
// workspace holding two processes has a default that may not be the one the designer has in mind;
// and everything the run could not do is STATED rather than dropped.
//
// ── ONE STORE PER FACT ────────────────────────────────────────────────────────────────────────
//
// `Correspondence`, `Diagnostics`, the counts and every tally below are DERIVED. The brief writes
// them as fields and they are not: a second list holding the same pairs the comparison already
// holds is the scar `VersionSingleSourceTests` exists for, and this record would be the third
// place a device pairing lives. What is stored is what nothing else can produce — the findings,
// the comparison, the two netlists as compared, and the two reduction logs.

using System.Linq;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Whether the series/parallel collapse ran — <b>on the face of the result, always</b>
/// (R-lvs8-1d, brief 6's R-lvs6-5c).</summary>
/// <remarks>
/// A result whose reduction mode is not on its face is a result two people can read differently —
/// one counting eight fingers and one counting one FET, each certain the other is looking at a
/// different design.
/// </remarks>
public enum ReductionMode
{
    /// <summary>The default: series, parallel and jumper collapses applied to both sides.</summary>
    On,

    /// <summary><c>--no-reduce</c>.</summary>
    Off,
}

/// <summary>One side's tally, <b>before and after the collapse</b> (R-lvs8-1c).</summary>
/// <remarks>
/// Both numbers, because a user reading <i>"32 devices"</i> against <i>"8 devices"</i> needs to see
/// the merge that explains it.
/// </remarks>
/// <param name="DevicesBefore">As read.</param>
/// <param name="DevicesAfter">As compared.</param>
/// <param name="NetsBefore">As read.</param>
/// <param name="NetsAfter">As compared — a jumper collapse makes two nets one.</param>
public readonly record struct LvsSideCounts(
    int DevicesBefore, int DevicesAfter, int NetsBefore, int NetsAfter);

/// <summary>What was on each side — R-lvs8-1c.</summary>
public readonly record struct LvsCounts(LvsSideCounts Schematic, LvsSideCounts Layout)
{
    /// <summary>Nothing was read.</summary>
    public static readonly LvsCounts Nothing = new(default, default);

    /// <summary>The sentence the run summary carries.</summary>
    public string Describe() =>
        $"schematic {Say(Schematic.DevicesBefore, Schematic.DevicesAfter)} device(s) and "
      + $"{Say(Schematic.NetsBefore, Schematic.NetsAfter)} net(s); "
      + $"layout {Say(Layout.DevicesBefore, Layout.DevicesAfter)} device(s) and "
      + $"{Say(Layout.NetsBefore, Layout.NetsAfter)} net(s)";

    private static string Say(int before, int after) =>
        before == after ? after.ToString() : $"{before} read as {after}";
}

/// <summary>
/// One comparison, end to end: what was read, what it reduced to, what corresponds, and every
/// line the run produced.
/// </summary>
/// <param name="Findings">
/// <b>The report</b> (R-lvs8-2) — every <c>lvs.</c> line, each with the objects it is about, a
/// marker and a waiver key, in R-lvs8-1b's order. Waived ones are included and marked.
/// </param>
/// <param name="Comparison">The correspondence and the divergences, as brief 7 concluded them.</param>
/// <param name="Schematic">The schematic netlist as compared — <b>reduced</b>.</param>
/// <param name="Layout">The layout netlist as compared — <b>reduced</b>.</param>
/// <param name="Geometry">Where the layout's objects are. <see cref="LvsGeometry.None"/> where
/// nothing was drawn.</param>
/// <param name="SchematicReduction">What the collapse did to the schematic.</param>
/// <param name="LayoutReduction">What it did to the layout.</param>
/// <param name="Counts">Both sides, before and after the collapse (R-lvs8-1c).</param>
/// <param name="TechnologyName">Which process the layout was read against (R-lvs8-1a).</param>
public sealed record LvsRunResult(
    IReadOnlyList<LvsFinding> Findings,
    LvsComparison Comparison,
    LvsNetlist Schematic,
    LvsNetlist Layout,
    LvsGeometry Geometry,
    ReductionLog SchematicReduction,
    ReductionLog LayoutReduction,
    LvsCounts Counts,
    string? TechnologyName)
{
    /// <summary>
    /// Every sub-cell this design places, compared on its own account — <b>one entry per distinct
    /// cell, not per placement</b> (<c>brief-lvs-9-hierarchy.md</c> R-lvs9-1a).
    /// </summary>
    /// <remarks>
    /// Empty for a flat run and for a design that places no cell with a drawing of its own, which
    /// is most of them. Each entry's own <see cref="LvsCellComparison.Result"/> is a full result in
    /// its turn, so the tree is as deep as the design is — and each level's findings are also
    /// re-reported in <see cref="Findings"/> under the placement that put the cell there, so a
    /// caller that only reads the report still sees everything.
    /// </remarks>
    public IReadOnlyList<LvsCellComparison> Cells { get; init; } = [];

    /// <summary>What the hierarchical reading cost — <b>counters, never clocks</b> (R-lvs9-5).</summary>
    public LvsHierarchyCounters Hierarchy { get; init; } = new();

    /// <summary>
    /// Every correspondence the run established, devices then nets — what brief 12 cross-probes
    /// with, and the answer to "what DID match".
    /// </summary>
    public IReadOnlyList<LvsPair> Correspondence => [.. Comparison.Devices, .. Comparison.Nets];

    /// <summary>
    /// <b>What the run could NOT do</b>, stated rather than dropped (R-lvs8-1a) — the refusals and
    /// the incomplete readings, which are the reason a comparison can look clean and not be.
    /// </summary>
    public IReadOnlyList<Diagnostic> Incomplete =>
        [.. Findings.Where(f => LvsFindingIds.Incomplete.Contains(f.Id)).Select(f => f.Diagnostic)];

    /// <summary>Every finding as a plain diagnostic, in the report's own order.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => [.. Findings.Select(f => f.Diagnostic)];

    /// <summary>R-lvs8-1d, and it is on the face of the result whichever way it went.</summary>
    public ReductionMode Reduction =>
        SchematicReduction.Enabled || LayoutReduction.Enabled ? ReductionMode.On : ReductionMode.Off;

    /// <summary>How many findings are errors. <b>Waived ones do not count and are still
    /// reported.</b></summary>
    public int ErrorCount => Findings.Count(f => !f.Waived && f.Severity == DiagnosticSeverity.Error);

    /// <summary>How many are warnings, on the same terms.</summary>
    public int WarningCount => Findings.Count(f => !f.Waived && f.Severity == DiagnosticSeverity.Warning);

    /// <summary>How many a waiver currently suppresses.</summary>
    public int WaivedCount => Findings.Count(f => f.Waived);

    /// <summary>Nothing outstanding above <see cref="DiagnosticSeverity.Info"/> — <b>the answer to
    /// "does the artwork implement the drawing"</b>.</summary>
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;
}
