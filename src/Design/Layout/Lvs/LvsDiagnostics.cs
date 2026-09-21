// The `lvs.` findings brief 3's extraction can report — brief-lvs-3-layout-netlist.md,
// docs/design/lvs.md §4.
//
// Authored HERE, below the UI firewall, for TerminalDiagnostics' reason (R-lvs1-4c): a rule that
// exists only in the CLI verb is a rule the application does not enforce. The CLI, the panel and
// every test read the same id, the same severity and the same sentence because there is one of
// each.
//
// THE IDS ARE THE DURABLE PART. Reword a template freely; changing an id is making a new
// diagnostic. Brief 8 owns the catalogue as a whole and adds the comparison's own findings to it;
// what is here is what the EXTRACTION can say on its own, before anything has been compared.

using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>The extraction's findings, as coded diagnostics.</summary>
public static class LvsDiagnostics
{
    // ── Devices (R-lvs3-3) ───────────────────────────────────────────────────

    /// <summary>
    /// R-lvs3-3e. <b>Reported before anything is matched</b>, because two placements sharing a
    /// designator makes tier 1 meaningless and every downstream finding derived from it
    /// misleading — a user chasing "R4 is on the wrong net" would be chasing whichever R4 the
    /// walk happened to reach first.
    /// </summary>
    public static Diagnostic DuplicateDesignator(string designator, int count) => Diagnostic.Create(
        "lvs.device.duplicate-designator", DiagnosticSeverity.Error,
        "{count} placements are called '{designator}'. A designator names one part, and until "
        + "these are distinct nothing that matches by name can be trusted.",
        ("designator", designator), ("count", count));

    /// <summary>R-lvs3-3d. A proposed pairing whose other half is gone.</summary>
    public static Diagnostic DanglingSchematicId(string path, string schematicId) => Diagnostic.Create(
        "lvs.device.dangling-schematic-id", DiagnosticSeverity.Warning,
        "The placement '{path}' was generated from schematic component '{schematicId}', which the "
        + "schematic no longer has. It is compared like any other unanchored device.",
        ("path", path), ("schematicId", schematicId));

    /// <summary>
    /// R-lvs3-3c. Neither a device nor obviously interconnect: no ports, no designator, and real
    /// copper. <b>Once per cell TYPE</b> — a via fence is one cell placed forty times, and forty
    /// copies of one sentence is a report nobody reads.
    /// </summary>
    public static Diagnostic UnclassifiedCell(string cellName, int placements) => Diagnostic.Create(
        "lvs.cell.unclassified", DiagnosticSeverity.Warning,
        "'{cellName}' ({placements} placement(s)) declares no ports, carries no designator and no "
        + "part kind, yet draws on conductor layers. Its copper was read as interconnect and it "
        + "contributed no device — give it a designator, a part kind or a port count if it is one.",
        ("cellName", cellName), ("placements", placements));

    // ── Terminals and pins (R-lvs3-5) ────────────────────────────────────────

    /// <summary>
    /// R-lvs3-5a. <b>The device is still emitted, with no terminals.</b> Dropping it would make
    /// the two sides disagree about how many parts there are, for a reason the report never gave.
    /// </summary>
    public static Diagnostic NoTerminalMap(string cellName, string detail) => Diagnostic.Create(
        "lvs.device.no-terminal-map", DiagnosticSeverity.Error,
        "'{cellName}' has no terminal map, so nothing says which layout pin is which schematic "
        + "port and it cannot be compared. {detail}",
        ("cellName", cellName), ("detail", detail));

    /// <summary>
    /// R-lvs3-5c. <b>The finding names the LAYER</b>, which is the whole point of it: "your
    /// technology does not call Metal3 a conductor" and "your pad is not connected" look identical
    /// from here and have entirely different fixes.
    /// </summary>
    public static Diagnostic PinOnNoCopper(string path, string pin, LayerKey layer, string layerName)
        => Diagnostic.Create(
            "lvs.pin.no-copper", DiagnosticSeverity.Error,
            "{path} pin '{pin}' sits on layer {layerName} ({layer}) and no copper is under it. "
            + "Either the pad is not connected, or the stackup does not call that layer a conductor.",
            ("path", path), ("pin", pin), ("layer", $"{layer.Layer}/{layer.Datatype}"),
            ("layerName", layerName));

    /// <summary>
    /// R-lvs3-5d. One terminal, several pads, and they are not on one net. <b>Not resolved to
    /// one</b>: a bonded ground whose two pads are on different nets is either a break in the bond
    /// or a short, and nothing here can tell which.
    /// </summary>
    public static Diagnostic TerminalSplitAcrossNets(string path, string terminal, int nets)
        => Diagnostic.Create(
            "lvs.terminal.split-across-nets", DiagnosticSeverity.Error,
            "{path} terminal '{terminal}' is several pads and they land on {nets} different nets. "
            + "One terminal is one connection, so either a pad is not bonded to the others or the "
            + "artwork shorts two nets. No net was chosen for it.",
            ("path", path), ("terminal", terminal), ("nets", nets));

    // ── Nets (R-lvs3-5e) ─────────────────────────────────────────────────────

    /// <summary>
    /// R-lvs3-5e. <b>A stamped name is read, reported and never obeyed.</b> The connectivity is
    /// what the geometry says; a label is the designer's claim about it, and where the two
    /// disagree the claim is the thing to look at.
    /// </summary>
    public static Diagnostic NetLabelDisagrees(string concluded, string stated) => Diagnostic.Create(
        "lvs.net.label-disagrees", DiagnosticSeverity.Warning,
        "Copper read as net '{concluded}' is stamped '{stated}'. The extraction follows the "
        + "geometry; the label is what to check.",
        ("concluded", concluded), ("stated", stated));

    /// <summary>The existing "one connected piece carries two different net names" refusal,
    /// carried verbatim (R-lvs3-5e) — <c>CopperPieces</c> authored the sentence and this only gives
    /// it an id.</summary>
    public static Diagnostic ContestedNetName(string detail) => Diagnostic.Create(
        "lvs.net.contested-name", DiagnosticSeverity.Error, "{detail}", ("detail", detail));

    // ── Ground (R-lvs3-6) ────────────────────────────────────────────────────

    /// <summary>
    /// R-lvs3-6d. <b>A warning and not an error</b>: a die with no backside metal, grounded only
    /// through bondwires to a package, is a real and correct design.
    /// </summary>
    public static Diagnostic NoGroundReferenceConductor() => Diagnostic.Create(
        "lvs.ground.no-reference-conductor", DiagnosticSeverity.Warning,
        "No conductor in this stackup is marked IsGroundReference, so nothing in the artwork is "
        + "net 0 unless the copper says so. Every ground terminal reads as an ordinary open.");

    /// <summary>
    /// R-lvs3-6e, and the owner asked for it by name. <b>Unconditional</b> — on every run that
    /// relies on an undrawn reference, including a perfectly clean one.
    /// </summary>
    /// <remarks>
    /// <b>Info rather than warning, because on a correct MMIC it is the normal state.</b> And
    /// unconditional because it is the one inference in the whole extraction the user cannot see
    /// on their own screen: the metal is not drawn, so there is nothing to look at and nothing to
    /// select. The first time it is wrong — a technology whose reference is mis-flagged — the
    /// design reads as perfectly connected, and this sentence is the only reason anyone would
    /// notice.
    /// </remarks>
    public static Diagnostic GroundReferenceUndrawn(string stackupEntry, int vias) => Diagnostic.Create(
        "lvs.ground.reference-undrawn", DiagnosticSeverity.Info,
        "Ground came from the stackup, not from the artwork: '{stackupEntry}' is the ground "
        + "reference and draws no layer, so {vias} via(s) were read as reaching net 0. Nothing on "
        + "your drawing shows this connection.",
        ("stackupEntry", stackupEntry), ("vias", vias));

    // ── The schematic side (brief-lvs-4-schematic-netlist.md) ────────────────

    /// <summary>
    /// R-lvs4-2c. <b>A refusal, not a partial comparison.</b> A design whose parameters do not
    /// resolve has no values to compare and its topology may depend on them — an <c>if()</c> in a
    /// sub-cell's parameter decides which of two branches gets stamped. The sentence is the
    /// elaborator's OWN, unmodified: it already names the variable, the cycle or the cell, and
    /// rewording it would mean the same failure reads two different ways depending on which door
    /// the user came in through.
    /// </summary>
    public static Diagnostic ElaborationFailed(string document, string detail) => Diagnostic.Create(
        "lvs.schematic.elaboration-failed", DiagnosticSeverity.Error,
        "'{document}' does not elaborate, so it has no resolved values to compare: {detail}",
        ("document", document), ("detail", detail));

    /// <summary>The `.cnl` round trip or the read itself failed. Its own sentence, given an id —
    /// distinct from <see cref="ElaborationFailed"/> because it happens one step earlier and the
    /// two have different fixes.</summary>
    public static Diagnostic SchematicUnreadable(string document, string detail) => Diagnostic.Create(
        "lvs.schematic.unreadable", DiagnosticSeverity.Error,
        "'{document}' could not be read as a circuit: {detail}",
        ("document", document), ("detail", detail));

    /// <summary>Everything <c>NetExtractor</c> had to say about this schematic — two labels on one
    /// physical net, a cell whose pin count disagrees with its interface, a Pin <c>Num</c> gap.
    /// Carried verbatim, because extraction authored the sentence and it is the same one the
    /// window shows.</summary>
    public static Diagnostic ExtractionNote(string detail) => Diagnostic.Create(
        "lvs.schematic.extraction-note", DiagnosticSeverity.Warning, "{detail}", ("detail", detail));

    /// <summary>
    /// R-lvs4-4d. A testbench cell compared without <c>--testbench</c>.
    /// </summary>
    /// <remarks>
    /// <b>Info, and it names the flag.</b> The default unit of comparison is the CELL, so a
    /// testbench's sources, terminations and tuners are excluded and their nets become boundary
    /// nets — which on a bench that is nothing BUT fixture leaves very little to compare.
    /// "Nothing matched" on a testbench would otherwise be a mystery with no visible cause.
    /// </remarks>
    public static Diagnostic TestBenchExcluded(string document, int excluded) => Diagnostic.Create(
        "lvs.scope.testbench-excluded", DiagnosticSeverity.Info,
        "'{document}' is a testbench and {excluded} fixture component(s) — sources, terminations, "
        + "ports and tuners — were excluded; their nets are boundary nets. Pass --testbench to "
        + "compare them as devices.",
        ("document", document), ("excluded", excluded));

    // ── Reduction (brief-lvs-6-reduction.md R-lvs6-5, note R-lvs-38/40) ─────

    /// <summary>
    /// R-lvs6-5c. <b>Emitted on every run, in both modes.</b> A result whose reduction mode is not
    /// on its face is a result two people can read differently — one counting eight fingers and one
    /// counting one FET, each certain the other is looking at a different design.
    /// </summary>
    public static Diagnostic ReductionMode(string document, bool enabled) => Diagnostic.Create(
        "lvs.reduce.mode", DiagnosticSeverity.Info,
        "'{document}' was read with series/parallel reduction {mode}.",
        ("document", document), ("mode", enabled ? "ON" : "OFF (--no-reduce)"),
        ("enabled", enabled));

    /// <summary>
    /// R-lvs6-5a. <b>Counts, by type, per document</b> — and the caller prints both sides' lines
    /// together, because an asymmetry between them is often the first clue to what is actually
    /// wrong ("Layout: 8 parallel groups (32 to 8 devices). Schematic: 0.").
    /// </summary>
    public static Diagnostic ReduceParallel(string document, int groups, int from, int to, string byType)
        => Diagnostic.Create(
            "lvs.reduce.parallel", DiagnosticSeverity.Info,
            "'{document}': {groups} parallel group(s), {from} devices read as {to} — {byType}.",
            ("document", document), ("groups", groups), ("from", from), ("to", to),
            ("byType", byType));

    /// <summary>R-lvs6-5a, the series half.</summary>
    public static Diagnostic ReduceSeries(string document, int groups, int from, int to, string byType)
        => Diagnostic.Create(
            "lvs.reduce.series", DiagnosticSeverity.Info,
            "'{document}': {groups} series group(s), {from} devices read as {to} — {byType}.",
            ("document", document), ("groups", groups), ("from", from), ("to", to),
            ("byType", byType));

    /// <summary>
    /// R-lvs6-5d. <b>Said either way</b> — collapsed or not — because a jumper present in one
    /// document and absent from the other is exactly the thing a designer wants told, and it is
    /// invisible in a count of devices that never mentioned it.
    /// </summary>
    public static Diagnostic ReduceJumper(string document, string path, bool collapsed)
        => Diagnostic.Create(
            "lvs.reduce.jumper", DiagnosticSeverity.Info,
            "'{document}': {path} declares zero ohms and is a shorting link. {outcome}",
            ("document", document), ("path", path), ("collapsed", collapsed),
            ("outcome", collapsed
                ? "Its two nets were read as one, on both sides."
                : "It was left as a device, because the other document has no jumper to collapse "
                  + "against and collapsing one side only would compare a circuit neither document draws."));

    // ── The whole run (R-lvs3-2c) ────────────────────────────────────────────

    /// <summary>
    /// R-lvs3-2c, which is <c>RailArtwork</c>'s own rule (R-ab1-5d): a design over the ceiling
    /// comes back with NO netlist and a note saying so, rather than with a confident netlist over
    /// geometry the run never saw.
    /// </summary>
    public static Diagnostic OverFlattenCeiling(string document, long ceiling) => Diagnostic.Create(
        "lvs.layout.over-flatten-ceiling", DiagnosticSeverity.Error,
        "'{document}' holds more instance geometry than the flatten ceiling of {ceiling} shapes "
        + "allows, so no netlist was extracted from it.",
        ("document", document), ("ceiling", ceiling));

    /// <summary>An instance that did not resolve. The flatten's own sentence, given an id.</summary>
    public static Diagnostic UnresolvedInstance(string detail) => Diagnostic.Create(
        "lvs.layout.unresolved-instance", DiagnosticSeverity.Warning, "{detail}", ("detail", detail));

    /// <summary>R-lvs3-2c. A layer reconciliation still pending at a technology boundary leaves
    /// that sub-cell unflattened, so its copper is not in the partition and anything on it reads
    /// as open. Reported rather than guessed at.</summary>
    public static Diagnostic PendingCrossTechMapping(string cellDir) => Diagnostic.Create(
        "lvs.layout.pending-layer-mapping", DiagnosticSeverity.Error,
        "'{cellDir}' is drawn on a different technology and its layer mapping has not been "
        + "confirmed, so none of its copper was read. Anything that connects through it is open.",
        ("cellDir", cellDir));
}
