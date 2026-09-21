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
