// One finding, and the catalogue of every id there is — brief-lvs-8-findings.md §2 and §3,
// docs/design/lvs.md §6.5.
//
// ── THE ID IS THE CONTRACT; THE SENTENCE IS NOT (R-lvs8-2a) ───────────────────────────────────
//
// Every test in this series asserts ids, the CLI's `--json` projects them and the panel groups on
// them. Reword a template freely. Adding an id later is additive; CHANGING one is a breaking
// change and gets the same treatment as changing a file format (R-lvs8-3a), which is why the
// catalogue below is a closed list a source gate checks against rather than a comment.
//
// ── THERE IS EXACTLY ONE SEVERITY AND IT LIVES ON THE DIAGNOSTIC (R-lvs8-2z) ──────────────────
//
// A `Severity` field beside a `Diagnostic` that already carries one is two fields with one
// meaning, which is this repository's recurring scar — three copies of a version number
// disagreeing is what `VersionSingleSourceTests` exists for. `DrcViolation` carries its own
// because its severity comes from the RULE and not from the violation; here the diagnostic is the
// producer and owns it, so `Severity` is a derived accessor on `DisplayRefDes`' pattern.

using System.Linq;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// One line of an LVS report: what was concluded, which of the designer's own objects it is about,
/// and where to look.
/// </summary>
/// <param name="Diagnostic">The stable dotted <c>lvs.</c> id, its typed arguments and its
/// SEVERITY.</param>
/// <param name="Objects">
/// The designers' own names for what this is about, <b>un-reduced</b> (R-lvs8-2b, brief 6's
/// R-lvs6-5b). A finding that can only say "the merged group at net 14" is one a user cannot act
/// on, so a collapsed device is expanded back to every individual it stands for.
/// </param>
/// <param name="MarkerRings">
/// Where to look, as flat implicitly-closed DBU vertex lists in the layout's own world
/// coordinates — <b>the DRC marker convention</b>, so the panel and the renderer need no second
/// one. One ring per island for an open (R-lvs8-5a); the join or neck geometry for a short
/// (R-lvs8-4c), never the whole net, because a marker covering a board-wide pour points at
/// nothing.
/// </param>
/// <param name="Marker">Bounding box of <paramref name="MarkerRings"/> — what click-to-zoom
/// uses.</param>
/// <param name="Key">
/// This finding's identity as a PLACE — id, objects and the marker's exact box, on
/// <c>DrcEngine.KeyFor</c>'s terms. It is what the panel matches a selected row's marker by.
///
/// <para><b>It is NOT what a waiver names, and brief 12 does not use it</b> (R-lvs12-4b). A DRC
/// waiver keys on a box deliberately, because a DRC waiver names a place and moving the shape
/// should stop it applying; an LVS waiver names a RELATIONSHIP, which survives moving the part and
/// is invalidated by a schematic edit. <see cref="LvsWaiverKey"/> is that second key, and
/// <c>src/Design/Layout/Lvs/RESOLVED.md</c> is why there are two.</para>
/// </param>
public sealed record LvsFinding(
    Diagnostic Diagnostic,
    IReadOnlyList<string> Objects,
    IReadOnlyList<long[]> MarkerRings,
    Bbox Marker,
    string Key)
{
    /// <summary>The one severity there is — R-lvs8-2z.</summary>
    public DiagnosticSeverity Severity => Diagnostic.Severity;

    /// <summary>The id, so a caller need not reach through to the diagnostic for the contract.</summary>
    public string Id => Diagnostic.Id;

    /// <summary>True when a waiver currently suppresses this. <b>A waived finding is still
    /// REPORTED</b> (R-lvs8-1a) and merely not counted — brief 12 sets it.</summary>
    public bool Waived { get; init; }

    /// <summary>The waiver's own reason, when <see cref="Waived"/>.</summary>
    public string? WaiverReason { get; init; }

    /// <summary>
    /// A line about the RUN rather than about an object — <b>no objects, and so no marker</b>
    /// (R-lvs8-2c). This is <c>SchematicToLayoutGenerator.ReportLine</c>'s own convention, where an
    /// empty instance name means the line is about the run.
    /// </summary>
    public bool IsRunLevel => Objects.Count == 0;

    /// <summary>
    /// Whether there is somewhere to look.
    /// </summary>
    /// <remarks>
    /// <b>R-lvs8-2c had to be narrowed, and this is where.</b> Its rule is "every finding has a
    /// marker, or it is a run-level line with an empty one", and there is a third case it does not
    /// name: a finding about an object that exists only in the SCHEMATIC. A device the artwork does
    /// not have has no artwork to point at — that absence IS the finding — and a marker invented
    /// for it would put a box on the board at a place where nothing is wrong. So a schematic-only
    /// finding names its object and carries no rings, and the gate asserts that partition rather
    /// than asserting rings on everything.
    /// </remarks>
    public bool HasMarker => MarkerRings.Count > 0;

    /// <summary>The sentence, for a console or a panel row.</summary>
    public string Render() => Diagnostic.Render();
}

/// <summary>
/// <b>Every <c>lvs.</c> id there is</b> — §3's table, fixed here once so the CLI's <c>--json</c>,
/// the panel and the tests agree.
/// </summary>
/// <remarks>
/// <b>A closed list, and the gate checks both directions.</b> An id nothing produces is dead and
/// an id nothing lists breaks the <c>--json</c> contract silently, so
/// <c>tests/Ui.Tests/Lvs/FindingsTests.cs</c> asserts that every entry here is produced by some
/// fixture and that no finding carries an id that is not here.
/// </remarks>
public static class LvsFindingIds
{
    /// <summary>§3's table, in its own order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        // devices
        "lvs.device.unmatched-schematic",
        "lvs.device.unmatched-layout",
        "lvs.device.type-mismatch",
        "lvs.device.duplicate-designator",
        "lvs.device.dangling-schematic-id",
        "lvs.device.no-terminal-map",

        // terminals and pins
        "lvs.terminal.wrong-net",
        "lvs.terminal.split-across-nets",
        "lvs.pin.no-copper",

        // nets
        "lvs.net.short",
        "lvs.net.open",
        "lvs.net.label-disagrees",
        "lvs.net.contested-name",
        "lvs.net.floating-copper",

        // how the correspondence was arrived at
        "lvs.anchor.contradicted",
        "lvs.match.by-symmetry",
        "lvs.match.structural-only",
        "check.terminals.derived-by-order",
        "lvs.compare.refinement-capped",

        // ground
        "lvs.ground.reference-undrawn",
        "lvs.ground.no-reference-conductor",

        // cells
        "lvs.cell.unclassified",

        // hierarchy (brief 9)
        "lvs.hierarchy.undeclared-contact",
        "lvs.hierarchy.flattened",

        // reduction (brief 6)
        "lvs.reduce.mode",
        "lvs.reduce.parallel",
        "lvs.reduce.series",
        "lvs.reduce.jumper",

        // properties (brief 10). `lvs.reduce.multiplicity-unstated` is spelled `reduce.` and not
        // `property.` deliberately: it is what the COLLAPSE found — several devices in parallel the
        // drawing never said were several — and grouping it with the merge lines is where a reader
        // chasing a device count will look for it.
        "lvs.property.mismatch",
        "lvs.property.derived-differs",
        "lvs.property.unread-differs",
        "lvs.property.layout-silent",
        "lvs.property.missing",
        "lvs.property.multiplicity",
        "lvs.property.tolerance-unestablished",
        "lvs.reduce.multiplicity-unstated",

        // what the run could not do
        "lvs.layout.over-flatten-ceiling",
        "lvs.layout.over-device-ceiling",
        "lvs.layout.unresolved-instance",
        "lvs.layout.pending-layer-mapping",
        "lvs.schematic.elaboration-failed",
        "lvs.schematic.unreadable",
        "lvs.schematic.extraction-note",
        "lvs.scope.testbench-excluded",
        "lvs.scope.view-missing",

        // the run's own lines (§6)
        "lvs.report.summary",
        "lvs.report.capped",
    ];

    // §3's table spells the positional-terminal-map warning `lvs.terminals.derived-by-order`, and
    // that id was NOT adopted. Brief 1 already ships the diagnostic, as
    // `check.terminals.derived-by-order`, and `check` already reports it; giving the same finding a
    // second id so it reads `lvs.` in this report would be one fault with two contracts, which is
    // the thing R-lvs8-3a exists to prevent. LVS carries brief 1's own line verbatim instead —
    // TerminalDiagnostics' own rule (R-lvs1-4c), and the same choice `lvs.net.contested-name` and
    // `lvs.schematic.extraction-note` make in the other direction.

    private static readonly HashSet<string> Set = [.. All];

    /// <summary>Whether <paramref name="id"/> is in the catalogue.</summary>
    public static bool Contains(string id) => Set.Contains(id);

    /// <summary>
    /// The ids that say <b>what the run could not do</b> rather than what it found — the subset
    /// <see cref="LvsRunResult.Diagnostics"/> answers with (R-lvs8-1a).
    /// </summary>
    /// <remarks>
    /// <b>A subset rather than a second list.</b> Every one of these is also a finding, with its
    /// own marker and its own place in the report; carrying them twice would be the two-fields-one-
    /// meaning scar this file's header is about.
    /// </remarks>
    public static readonly IReadOnlySet<string> Incomplete = new HashSet<string>(
    [
        "lvs.layout.over-flatten-ceiling",
        "lvs.layout.over-device-ceiling",
        "lvs.layout.unresolved-instance",
        "lvs.layout.pending-layer-mapping",
        "lvs.schematic.elaboration-failed",
        "lvs.schematic.unreadable",
        "lvs.compare.refinement-capped",
        "lvs.device.no-terminal-map",
        "lvs.scope.view-missing",
    ]);
}

/// <summary>Turning geometry into the marker a finding carries — one convention, one place.</summary>
internal static class LvsMarker
{
    /// <summary>A box, as one implicitly-closed ring.</summary>
    public static long[] Ring(Bbox b) =>
        [b.MinX, b.MinY, b.MaxX, b.MinY, b.MaxX, b.MaxY, b.MinX, b.MaxY];

    /// <summary>
    /// A pad's own marker: the land pattern's stated width where it states one, and a
    /// <paramref name="fallback"/>-sized square where it does not.
    /// </summary>
    /// <remarks>
    /// <b>A square and not a point.</b> Every consumer of <c>MarkerRings</c> draws a ring; a
    /// degenerate one is invisible at every zoom, which is the same failure as having no marker at
    /// all.
    /// </remarks>
    public static Bbox Pad(LvsPadGeometry pad, long fallback)
    {
        long half = Math.Max(pad.WidthDbu, fallback) / 2;
        if (half <= 0) half = 1;
        return new Bbox(pad.X - half, pad.Y - half, pad.X + half, pad.Y + half);
    }

    /// <summary>The union of some boxes, or <see cref="Bbox.Empty"/>.</summary>
    public static Bbox Union(IEnumerable<Bbox> boxes)
    {
        var all = Bbox.Empty;
        foreach (var b in boxes) all = all.Union(b);
        return all;
    }

    /// <summary>
    /// The identity a waiver names — <c>DrcEngine.KeyFor</c>'s form, spelled for a finding.
    /// </summary>
    public static string Key(string id, IReadOnlyList<string> objects, Bbox marker) =>
        $"{id}|{string.Join(",", objects)}|" +
        (marker.IsEmpty ? "-" : $"{marker.MinX},{marker.MinY},{marker.MaxX},{marker.MaxY}");

    /// <summary>A finding with a marker.</summary>
    public static LvsFinding Of(Diagnostic diagnostic, IReadOnlyList<string> objects, IReadOnlyList<long[]> rings)
    {
        var box = Bbox.Empty;
        foreach (var ring in rings)
            for (int i = 0; i + 1 < ring.Length; i += 2)
                box = box.Union(new Bbox(ring[i], ring[i + 1], ring[i], ring[i + 1]));

        return new LvsFinding(diagnostic, objects, rings, box,
                              Key(diagnostic.Id, objects, box));
    }

    /// <summary>A finding whose marker is one box.</summary>
    public static LvsFinding Of(Diagnostic diagnostic, IReadOnlyList<string> objects, Bbox box)
        => box.IsEmpty
            ? new LvsFinding(diagnostic, objects, [], Bbox.Empty, Key(diagnostic.Id, objects, Bbox.Empty))
            : new LvsFinding(diagnostic, objects, [Ring(box)], box, Key(diagnostic.Id, objects, box));

    /// <summary>A line about the run — no objects, no marker, and none missing (R-lvs8-2c).</summary>
    public static LvsFinding RunLevel(Diagnostic diagnostic)
        => new(diagnostic, [], [], Bbox.Empty, Key(diagnostic.Id, [], Bbox.Empty));

    /// <summary>
    /// R-lvs8-1b's order: severity, then id, then the primary object's path — with the rendered
    /// sentence as the last tie-break so two findings naming one object still land in one order.
    /// </summary>
    public static List<LvsFinding> Ordered(IEnumerable<LvsFinding> findings) =>
        [.. findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Objects.Count > 0 ? f.Objects[0] : "", StringComparer.Ordinal)
            .ThenBy(f => f.Render(), StringComparer.Ordinal)];
}
