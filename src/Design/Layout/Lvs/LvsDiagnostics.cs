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

    /// <summary>
    /// R-lvs11-2b. A cell with only one of the two views. <b>Info, and not a refusal</b>: it is the
    /// ordinary mid-design state, and a comparison that failed on it would be one nobody runs while
    /// the design is being drawn.
    /// </summary>
    public static Diagnostic ViewMissing(string cell, string view) => Diagnostic.Create(
        "lvs.scope.view-missing", DiagnosticSeverity.Info,
        "'{cell}' has no primary {view} view, so there was nothing to compare it against.",
        ("cell", cell), ("view", view));

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

    // ── The comparison (brief-lvs-7-comparison.md) ───────────────────────────
    //
    // Brief 8 owns the catalogue as a whole and adds the shorts' PATHS and the opens' ISLANDS to
    // what is here. These are the ids brief 7 can produce on its own — what the refinement
    // concluded, before anything has been given a marker.

    /// <summary>
    /// R-lvs7-5a, and R-lvs7-4d's counts. A schematic device the layout has no counterpart for.
    /// </summary>
    /// <remarks>
    /// <b>The class sizes are on the finding.</b> "Three parallel caps here and four there" is a
    /// different fault from "this one part is missing", and a reader who is given only the missing
    /// part cannot tell which they are looking at.
    /// </remarks>
    public static Diagnostic UnmatchedSchematic(string path, int schematicCount, int layoutCount)
        => Diagnostic.Create(
            "lvs.device.unmatched-schematic", DiagnosticSeverity.Error,
            "The schematic's '{path}' has no counterpart in the layout ({schematicCount} "
            + "indistinguishable device(s) in the schematic against {layoutCount} in the layout).",
            ("path", path), ("schematicCount", schematicCount), ("layoutCount", layoutCount));

    /// <summary>R-lvs7-5a, the other side.</summary>
    public static Diagnostic UnmatchedLayout(string path, int schematicCount, int layoutCount)
        => Diagnostic.Create(
            "lvs.device.unmatched-layout", DiagnosticSeverity.Error,
            "The layout's '{path}' has no counterpart in the schematic ({schematicCount} "
            + "indistinguishable device(s) in the schematic against {layoutCount} in the layout).",
            ("path", path), ("schematicCount", schematicCount), ("layoutCount", layoutCount));

    /// <summary>
    /// R-lvs7-5d. <b>One line, not two unmatched-device lines.</b> "R1 is a resistor in the
    /// schematic and a capacitor in the layout" is a sentence a user can act on; two anonymous
    /// "unmatched" lines are a puzzle whose answer is this sentence.
    /// </summary>
    public static Diagnostic TypeMismatch(
        string schematicPath, string layoutPath, string schematicType, string layoutType)
        => Diagnostic.Create(
            "lvs.device.type-mismatch", DiagnosticSeverity.Error,
            "'{schematicPath}' is a {schematicType} in the schematic and '{layoutPath}' is a "
            + "{layoutType} in the layout. They name each other and cannot be the same part.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath),
            ("schematicType", schematicType), ("layoutType", layoutType));

    /// <summary>
    /// Brief LVS 16 R-lvs16-2a. <b>A warning, one per placed part, and never an error</b>: a
    /// resistor, a capacitor or an inductor placed end for end is the same circuit, so it must not
    /// fail <c>--severity error</c>. But the placement states the wrong pin order and everything that
    /// reads pin 1 literally reads it backwards — which is how a whole board once became one net in
    /// railRF — and LVS is the one place a designer is guaranteed to look.
    /// </summary>
    /// <remarks>
    /// The lands are ARGUMENTS, not a lookup: the marker is the part's own two pads, and after
    /// reduction a merged parallel group no longer has a device of this path to look them up on.
    /// </remarks>
    /// <param name="refdes">The part.</param>
    /// <param name="path">Its placement path, for the report's objects.</param>
    /// <param name="document">The <c>.clay</c> that places it — where the turn has to be made, which
    /// for a part inside a placed cell is that cell's layout (R-lvs16-3d).</param>
    public static Diagnostic DeviceTurned(
        string refdes, string path, string document, (long X, long Y) land1, (long X, long Y) land2)
        => Diagnostic.Create(
            "lvs.device.turned", DiagnosticSeverity.Warning,
            "'{refdes}' is placed end for end: its pin 1 sits on the copper the schematic gives its "
            + "pin 2. The circuit is the same either way round, but anything that reads the "
            + "placement's pin order — railRF, the placement table, cross-probing — reads it "
            + "backwards. Turn it in the layout ('{document}').",
            ("refdes", refdes), ("path", path), ("document", document),
            ("x1", land1.X), ("y1", land1.Y), ("x2", land2.X), ("y2", land2.Y));

    /// <summary>
    /// Brief LVS 16 R-lvs16-2b. A two-terminal part that is NOT symmetric — a diode, an LED, a
    /// polarised capacitor, a two-terminal cell — whose terminals are all wrong as placed and all
    /// right the other way round. <b>One error in place of three findings</b> (a contradicted
    /// anchor and two unmatched devices) that described it only indirectly: the board assembles it
    /// backwards.
    /// </summary>
    /// <remarks>
    /// It names BOTH nets and does not say which half is wrong: either the part or the copper may
    /// be, and only the designer knows.
    /// </remarks>
    public static Diagnostic DeviceReversed(
        string schematicPath, string layoutPath, string kind, string net1, string net2)
        => Diagnostic.Create(
            "lvs.device.reversed", DiagnosticSeverity.Error,
            "'{layoutPath}' is placed end for end: the schematic puts its terminal 1 on '{net1}' and "
            + "its terminal 2 on '{net2}', and the layout has them the other way round. A {kind} is "
            + "not the same part reversed, so this board assembles it backwards. Turn the part, or "
            + "correct the copper — whichever is the half that is wrong.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("kind", kind),
            ("net1", net1), ("net2", net2));

    /// <summary>
    /// R-lvs7-5a. A matched device whose terminal reaches copper belonging to a DIFFERENT
    /// schematic net — the mis-wiring finding, and the one that names both nets.
    /// </summary>
    /// <remarks>
    /// <b>Only where the copper it reached belongs to another net.</b> A terminal that landed on
    /// an island of its own net is an open (<see cref="NetOpen"/>) and reporting it here as well
    /// would turn one missing via into a finding per pin.
    /// </remarks>
    /// <param name="reaches">What the sentence calls the layout net — <c>'VDD'</c>, or the other
    /// pins on it where nobody named it. Empty falls back to <paramref name="found"/>, which stays
    /// the typed name.</param>
    public static Diagnostic TerminalWrongNet(
        string path, int port, string terminal, string expected, string found, string reaches = "")
        => Diagnostic.Create(
            "lvs.terminal.wrong-net", DiagnosticSeverity.Error,
            "{path} terminal {port} ('{terminal}') is on '{expected}' in the schematic and reaches "
            + "{reaches} in the layout.",
            ("path", path), ("port", port), ("terminal", terminal),
            ("expected", expected), ("found", found),
            ("reaches", reaches.Length > 0 ? reaches : $"'{found}'"));

    /// <summary>
    /// Two or more schematic nets are one piece of copper — <b>with the PATH</b> (R-lvs8-4b),
    /// which is the half a designer can act on.
    /// </summary>
    /// <remarks>
    /// <b>One factory, one template, and the path is an ARGUMENT.</b> The comparison concludes the
    /// short before anything has been located, and the report adds the route once it has the
    /// artwork's geometry; two factories would be two sentences for one id, which is the drift
    /// R-lvs8-2a is about. <paramref name="through"/> is empty where nothing located it — and it is
    /// empty on the comparison's own pass, always.
    /// </remarks>
    /// <param name="nets">The two schematic nets, as the designer spells them.</param>
    /// <param name="count">How many nets this line is about — two, for a pair (R-lvs8-4d).</param>
    /// <param name="layoutNet">What the copper they share is called.</param>
    /// <param name="through">" They are joined through …", or empty.</param>
    /// <param name="widthDbu">The narrowest metal on the route, DBU — <b>typed, so a test asserts
    /// the number rather than the sentence</b>. Zero where nothing was located.</param>
    /// <param name="x">Where, DBU.</param>
    /// <param name="y">DBU.</param>
    /// <param name="pair">The two nets as the sentence says them — <c>'IN' and 'OUT'</c>. Empty
    /// falls back to <paramref name="nets"/>, which stays the WAIVER identity and so is never
    /// reworded (<c>LvsWaiverKey</c>).</param>
    /// <param name="carries">" It carries …": which of each net's pins the copper reaches, or
    /// empty. Without it a user is told two nets touch and not where to start looking.</param>
    public static Diagnostic NetShort(
        string nets, int count, string layoutNet,
        string through = "", long widthDbu = 0, long x = 0, long y = 0,
        string pair = "", string carries = "") => Diagnostic.Create(
        "lvs.net.short", DiagnosticSeverity.Error,
        "Short: schematic nets {pair} are joined by one piece of copper in the layout.{carries}{through}",
        ("nets", nets), ("count", count), ("layoutNet", layoutNet), ("through", through),
        ("widthDbu", widthDbu), ("x", x), ("y", y),
        ("pair", pair.Length > 0 ? pair : nets), ("carries", carries));

    /// <summary>
    /// One schematic net is several pieces of copper — <b>with the ISLANDS</b> (R-lvs8-5a) and a
    /// marker on each.
    /// </summary>
    /// <remarks>
    /// <b>Each island names the net's OWN pins first</b>, and the rest of that copper after them.
    /// The first wording listed every pin on every island, and a designer reading "net '6' is
    /// L1.2, ML7.1, ML7.2; L2.2, ML5.1, ML5.2" could not tell which of those six the net was about
    /// (field report, 2026-09-24).
    /// </remarks>
    /// <param name="net">The net's name — the WAIVER identity, so never reworded.</param>
    /// <param name="subject">"Schematic net 'IN'", or "An unnamed schematic net". Empty falls back
    /// to <paramref name="net"/>.</param>
    /// <param name="members">The net's own pins, as the designer names them.</param>
    public static Diagnostic NetOpen(
        string net, int islands, string pins, string subject = "", string members = "")
        => Diagnostic.Create(
            "lvs.net.open", DiagnosticSeverity.Error,
            "Open: {subject} should connect {members}, but in the layout they are on {islands} pieces "
            + "of copper that do not touch: {pins}.",
            ("net", net), ("islands", islands), ("pins", pins),
            ("subject", subject.Length > 0 ? subject : $"schematic net '{net}'"),
            ("members", members.Length > 0 ? members : "its pins"));

    /// <summary>
    /// R-lvs8-5c. An island with no pin on it at all. <b>A warning, and NOT an open</b>:
    /// unconnected copper on a net is a pour somebody forgot to stitch, which is worth saying and
    /// is not the same defect — an open is a net the schematic says is one and the artwork makes
    /// several, and copper nothing lands on is not part of that story.
    /// </summary>
    public static Diagnostic NetFloatingCopper(string named, int pieces, string where)
        => Diagnostic.Create(
            "lvs.net.floating-copper", DiagnosticSeverity.Warning,
            "{pieces} piece(s) of copper{named} carry no pin at all, at {where}. Nothing connects "
            + "to them, so they are in no net — a pour that was never stitched reads exactly like "
            + "this.",
            ("named", named.Length > 0 ? $" stamped '{named}'" : ""), ("pieces", pieces),
            ("where", where), ("net", named));

    // ── Properties (brief-lvs-10-properties.md) ──────────────────────────────
    //
    // EVERY ONE OF THESE PRINTS BOTH VALUES AND THE TOLERANCE THAT WAS APPLIED (R-lvs10-3c).
    // Never the word "mismatch" alone: the defaults are measured off one design and are declared
    // provisional, so a wrong default has to be VISIBLE on the line it produced rather than latent
    // in a table nobody opens. "R3: schematic 294 Ω, layout 150 Ω, tolerance 1 %" is actionable and
    // is also self-auditing.

    /// <summary>
    /// R-lvs10-3c. Two values that disagree by more than the dimension's tolerance.
    /// </summary>
    public static Diagnostic PropertyMismatch(
        string schematicPath, string layoutPath, string name,
        string schematic, string layout, string tolerance)
        => Diagnostic.Create(
            "lvs.property.mismatch", DiagnosticSeverity.Error,
            "{schematicPath} {name}: schematic {schematic}, layout {layout}, tolerance {tolerance}.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("name", name),
            ("schematic", schematic), ("layout", layout), ("tolerance", tolerance));

    /// <summary>
    /// R-lvs10-4b. The layout's generator DERIVED this value from the artwork it drew, so it
    /// cannot disagree with the artwork — only with what the schematic asked for.
    /// </summary>
    /// <remarks>
    /// <b>A different sentence because it sends the designer somewhere different.</b> "C2 value
    /// mismatch" leaves them to work out whether the drawing or the artwork is wrong; "the layout's
    /// geometry gives 1.82 pF; the schematic asks for 2.0 pF" says the geometry is self-consistent
    /// and one of the two numbers is the one to change.
    /// </remarks>
    public static Diagnostic PropertyDerivedDiffers(
        string schematicPath, string layoutPath, string name,
        string schematic, string layout, string tolerance)
        => Diagnostic.Create(
            "lvs.property.derived-differs", DiagnosticSeverity.Error,
            "{schematicPath} {name}: the layout's geometry gives {layout}; the schematic asks for "
            + "{schematic}. Tolerance {tolerance}. The generator derived this from what it drew, so "
            + "change the artwork's dimensions or change the drawing's request.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("name", name),
            ("schematic", schematic), ("layout", layout), ("tolerance", tolerance));

    /// <summary>
    /// R-lvs10-4c. <b>Info, and the artwork is not wrong.</b> The generator never read this
    /// parameter, so nothing about the geometry depends on it — a model name or a multiplier is
    /// still the user's to set and a difference there is information rather than a fault.
    /// </summary>
    public static Diagnostic PropertyUnreadDiffers(
        string schematicPath, string layoutPath, string name,
        string schematic, string layout, string tolerance)
        => Diagnostic.Create(
            "lvs.property.unread-differs", DiagnosticSeverity.Info,
            "{schematicPath} {name}: schematic {schematic}, layout {layout}, tolerance {tolerance}. "
            + "The generator never read this parameter, so no geometry depends on it.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("name", name),
            ("schematic", schematic), ("layout", layout), ("tolerance", tolerance));

    /// <summary>
    /// R-lvs10-2b. <b>Once per device TYPE, at info, and it is not a finding about a device.</b>
    /// Four hundred 0402s sharing one land pattern must not produce four hundred lines saying the
    /// obvious — the land pattern is shared by every 0402 on the board, so a value stored on it
    /// would be wrong for all but one, and claiming nothing is the correct thing for it to do.
    /// </summary>
    public static Diagnostic PropertyLayoutSilent(string cellName, int devices, string parameters)
        => Diagnostic.Create(
            "lvs.property.layout-silent", DiagnosticSeverity.Info,
            "'{cellName}' ({devices} device(s)) states no values of its own, so the schematic's "
            + "{parameters} had nothing to be compared against.",
            ("cellName", cellName), ("devices", devices), ("parameters", parameters));

    /// <summary>
    /// A part drawn by its own component's generator carries parameters that generator never takes
    /// — a line's substrate (Er, H, T, …) is the circuit's, not the artwork's. <b>Once per
    /// generator, at info</b>, on <see cref="PropertyLayoutSilent"/>'s terms: said, not compared,
    /// and not a warning about every line on the board (field report, 2026-09-24: twenty of them
    /// on a five-line filter, each calling the two sides "not the same generator").
    /// </summary>
    public static Diagnostic PropertyNotDrawn(string generator, int devices, string stated, string parameters)
        => Diagnostic.Create(
            "lvs.property.not-drawn", DiagnosticSeverity.Info,
            "The {generator} generator draws from {stated} only, so the schematic's {parameters} "
            + "({devices} device(s)) have nothing in the artwork to be compared against.",
            ("generator", generator.ToUpperInvariant()), ("devices", devices), ("stated", stated),
            ("parameters", parameters));

    /// <summary>
    /// R-lvs10-2d. <b>Not the same thing as <see cref="PropertyLayoutSilent"/>.</b> The artwork
    /// states values — it is a generator with a parameter list — and this one is not on it, which
    /// means the two sides are not the same generator.
    /// </summary>
    public static Diagnostic PropertyMissing(
        string schematicPath, string layoutPath, string name, string schematic, string stated)
        => Diagnostic.Create(
            "lvs.property.missing", DiagnosticSeverity.Warning,
            "{schematicPath} asks for {name} = {schematic} and the artwork's '{layoutPath}' carries "
            + "no {name} at all, though it states {stated}. The two are not the same generator.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("name", name),
            ("schematic", schematic), ("stated", stated));

    /// <summary>
    /// R-lvs10-5a. <b>A property finding and not a topology one</b>: the merge is what made the
    /// comparison possible, and judging it is here.
    /// </summary>
    public static Diagnostic PropertyMultiplicity(
        string schematicPath, string layoutPath, string name, int schematic, int layout)
        => Diagnostic.Create(
            "lvs.property.multiplicity", DiagnosticSeverity.Error,
            "{schematicPath} declares {name} = {schematic} and the artwork has {layout} in "
            + "parallel. Multiplicity is an integer and is compared exactly.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath), ("name", name),
            ("schematic", schematic), ("layout", layout));

    /// <summary>
    /// R-lvs10-5b. Several devices in parallel in the artwork against a schematic device that
    /// declares no multiplicity parameter at all — <b>a real and common under-specification</b>,
    /// and the designer should know rather than have it silently accepted.
    /// </summary>
    public static Diagnostic MultiplicityUnstated(string schematicPath, string layoutPath, int devices, string group)
        => Diagnostic.Create(
            "lvs.reduce.multiplicity-unstated", DiagnosticSeverity.Warning,
            "The artwork has {devices} devices in parallel here ({group}) and '{schematicPath}' "
            + "declares no multiplicity parameter. They were compared as one; say Nf or M on the "
            + "schematic if that is what is meant.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath),
            ("devices", devices), ("group", group));

    /// <summary>
    /// R-lvs10-6e. <b>No tolerance is invented before it is measured.</b> A unit dimension with no
    /// representative in the fixture the table was measured off is compared EXACTLY, and this line
    /// is what makes that gap visible enough to close instead of leaving a number nobody can
    /// defend.
    /// </summary>
    /// <remarks>
    /// <b>Once per dimension, and only for a dimension something actually compared.</b> A run-level
    /// line about the table rather than about a device: it has nowhere on the board to point at.
    /// </remarks>
    public static Diagnostic ToleranceUnestablished(string dimension, int compared)
        => Diagnostic.Create(
            "lvs.property.tolerance-unestablished", DiagnosticSeverity.Info,
            "No property tolerance has been measured for {dimension}, so its {compared} "
            + "comparison(s) were exact. The shipped table was measured off a correct design and "
            + "that dimension had no representative in it.",
            ("dimension", dimension), ("compared", compared));

    // ── The run's own lines (brief-lvs-8-findings.md §6) ────────────────────────────────────

    /// <summary>
    /// R-lvs8-6b. <b>Emitted on every run, including one that found nothing.</b>
    /// </summary>
    /// <remarks>
    /// "Nothing changed, say nothing" is a rule about per-object noise. A run that deliberately
    /// concluded "these match" and then said nothing at all is indistinguishable from a broken
    /// command, which is the one outcome a gate cannot catch and a user cannot diagnose.
    /// </remarks>
    public static Diagnostic RunSummary(string counts, string technology, ReductionMode reduction)
        => Diagnostic.Create(
            "lvs.report.summary", DiagnosticSeverity.Info,
            "Compared {counts}, against {technology}, with reduction {reduction}.",
            ("counts", counts), ("technology", technology),
            ("reduction", reduction == Lvs.ReductionMode.On ? "ON" : "OFF (--no-reduce)"));

    /// <summary>
    /// R-lvs8-6a. The trailing count when a cap bit — <c>SchematicToLayoutGenerator.ReportLine</c>'s
    /// own convention, where a line about the run carries no object.
    /// </summary>
    /// <remarks>
    /// <b>A pour accidentally joined to forty nets must not emit a finding per pair without saying
    /// so.</b> Capping silently is worse than not capping: the report looks complete and is not.
    /// </remarks>
    public static Diagnostic Capped(string id, int shown, int total) => Diagnostic.Create(
        "lvs.report.capped", DiagnosticSeverity.Info,
        "{shown} of {total} '{finding}' finding(s) are listed; the rest were the same fault again.",
        ("finding", id), ("shown", shown), ("total", total));

    /// <summary>
    /// R-lvs7-2b, and <b>usually the most useful line in the whole report</b>: a name-based pairing
    /// every terminal of which the structure refutes. It names a mis-wired part by the designer's
    /// own name for it rather than reporting two anonymous unmatched objects.
    /// </summary>
    /// <remarks>
    /// <b>Warning, not error, and the comparison continues without it</b> (R-lvs7-2c). Keeping a
    /// contradicted anchor would propagate one wrong pairing through every neighbour's colour and
    /// turn one fault into a cascade, which is how a report goes from six findings to four hundred.
    /// </remarks>
    public static Diagnostic AnchorContradicted(string schematicPath, string layoutPath)
        => Diagnostic.Create(
            "lvs.anchor.contradicted", DiagnosticSeverity.Warning,
            "'{layoutPath}' in the layout names the schematic's '{schematicPath}', but none of its "
            + "terminals reaches the copper that pairing requires. The name was dropped and the "
            + "two were compared on their structure.",
            ("schematicPath", schematicPath), ("layoutPath", layoutPath));

    /// <summary>
    /// R-lvs7-4c. <b>Not a nicety.</b> The pairing inside an automorphism group is arbitrary, so a
    /// later finding naming "C7" may mean the part the designer calls C9 — and a user who does not
    /// know that will chase the wrong part.
    /// </summary>
    public static Diagnostic MatchBySymmetry(int count, string pairs) => Diagnostic.Create(
        "lvs.match.by-symmetry", DiagnosticSeverity.Info,
        "{count} device(s) are genuinely interchangeable and were paired arbitrarily: {pairs}. A "
        + "finding naming one of them may mean any other.",
        ("count", count), ("pairs", pairs));

    /// <summary>
    /// R-lvs7-2e. No designator, no <c>SchematicId</c>, no net name the two sides share — the
    /// comparison ran on structure alone. <b>The case that proves the algorithm rather than the
    /// naming</b>, and the one where every pairing is the algorithm's own choice.
    /// </summary>
    public static Diagnostic MatchStructuralOnly(int devices) => Diagnostic.Create(
        "lvs.match.structural-only", DiagnosticSeverity.Info,
        "Nothing named a part on both sides, so all {devices} device(s) were paired on structure "
        + "alone. Every name in this report is the layout's or the schematic's own, never a "
        + "correspondence either document stated.",
        ("devices", devices));

    /// <summary>
    /// R-lvs7-3d. <b>It should never bind</b>: the partition strictly refines on every iteration
    /// that changes anything, so it cannot change more times than there are objects. If this ever
    /// fires the signatures are not stable and the answer is not to be trusted.
    /// </summary>
    public static Diagnostic RefinementCapped(int cap) => Diagnostic.Create(
        "lvs.compare.refinement-capped", DiagnosticSeverity.Error,
        "Colour refinement was still changing after {cap} iteration(s) and was stopped. The "
        + "comparison below is incomplete.",
        ("cap", cap));

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

    // ── Hierarchy (brief 9) ──────────────────────────────────────────────────

    /// <summary>
    /// R-lvs9-3b. A placed cell reaches the design around it through metal it does not declare a
    /// pin for.
    /// </summary>
    /// <remarks>
    /// <b>An ERROR, and never absorbed</b> (R-lvs9-3c). Reading it as an ordinary connection would
    /// mean the hierarchy says one thing and the copper another — which is the whole class of
    /// defect this tool exists to find, reintroduced by the tool itself. The two honest answers are
    /// to declare a pin there or to flatten this one cell, and the sentence names both.
    /// </remarks>
    public static Diagnostic UndeclaredContact(
        string path, string cellName, LayerKey layer, string layerName, string where,
        long widthDbu, long x, long y)
        => Diagnostic.Create(
            "lvs.hierarchy.undeclared-contact", DiagnosticSeverity.Error,
            "{path} places '{cellName}', whose copper meets this design's own on {layerName} "
            + "({layer}) at {where}, away from every pin it declares. A cell joined to its parent "
            + "by undeclared metal has no hierarchical reading: declare a pin there, or flatten "
            + "'{cellName}' for LVS.",
            ("path", path), ("cellName", cellName), ("layerName", layerName),
            ("layer", $"{layer.Layer}/{layer.Datatype}"), ("where", where),
            ("widthDbu", widthDbu), ("x", x), ("y", y));

    /// <summary>
    /// R-lvs9-3d and R-lvs9-6b. A cell that could have been compared on its own account was read
    /// flat instead, and WHY.
    /// </summary>
    /// <remarks>
    /// <b>Info, and unconditional</b> — using the escape hatch is reported so a design that quietly
    /// flattens everything is visible. Once per cell TYPE, for <c>UnclassifiedCell</c>'s reason: a
    /// module placed forty times is one decision, not forty.
    /// </remarks>
    public static Diagnostic CellFlattened(string cellName, int placements, string reason)
        => Diagnostic.Create(
            "lvs.hierarchy.flattened", DiagnosticSeverity.Info,
            "'{cellName}' ({placements} placement(s)) was read flat rather than compared as a cell "
            + "of its own, because {reason}. Its copper joined this design's partition and its "
            + "contents were not compared.",
            ("cellName", cellName), ("placements", placements), ("reason", reason));

    /// <summary>
    /// R-lvs9-5d, which is <c>DrcEngine</c>'s bargain reused: a pathological design costs a
    /// message, not a hang — and the message says what it would have needed.
    /// </summary>
    public static Diagnostic OverDeviceCeiling(string document, int devices, long ceiling)
        => Diagnostic.Create(
            "lvs.layout.over-device-ceiling", DiagnosticSeverity.Error,
            "'{document}' reads as {devices} device(s), above the {ceiling} this comparison will "
            + "attempt, so nothing was compared. Compare a cell of it rather than the whole "
            + "design, or raise the ceiling if the machine can carry it.",
            ("document", document), ("devices", devices), ("ceiling", ceiling));

    // ── Geometric device recognition (brief-lvs-14-recognition.md) ───────────
    //
    // TIER 3 IS A FALLBACK AND EVERY SENTENCE HERE SAYS SO. R-lvs14-5a: recognition answers "what
    // does this copper look like", never "is this the device the process actually makes", and a
    // clean report over a recognised design must not read as the stronger claim. That is why the
    // in-use line is unconditional wherever recognition contributed, and why nothing it rejected
    // is ever dropped in silence (R-lvs14-4c) — a pass that quietly discarded half the devices
    // would make a design read as clean.

    /// <summary>
    /// R-lvs14-5a. <b>Unconditional wherever recognition contributed</b>, and it names the deck and
    /// the count.
    /// </summary>
    /// <remarks>
    /// Info, and the same argument as <see cref="GroundReferenceUndrawn"/>: it is an inference the
    /// user cannot see on their own screen. There is no instance to select, no placement to click,
    /// nothing in the drawing that says "this rectangle was read as a resistor" — so being told is
    /// the only way anyone knows the comparison rested on a recognition at all.
    /// </remarks>
    public static Diagnostic RecognizeInUse(string technology, int devices, int rules)
        => Diagnostic.Create(
            "lvs.recognize.in-use", DiagnosticSeverity.Info,
            "{devices} device(s) were RECOGNISED from geometry by {rules} rule(s) of '{technology}', "
            + "not read from placed instances. Recognition says what this copper looks like; it "
            + "cannot say whether it is the device the process actually makes.",
            ("technology", technology), ("devices", devices), ("rules", rules));

    /// <summary>
    /// R-lvs14-3b. A candidate whose terminal count is not the two a two-terminal rule needs.
    /// </summary>
    /// <remarks>
    /// <b>A warning, and NO device is emitted.</b> A body with one terminal is half a device and a
    /// body with five is a rule matching something it was not written for; either way there is no
    /// honest netlist entry to make. It is reported rather than skipped because R-lvs14-4c is that
    /// every candidate the extraction rejected says why.
    /// </remarks>
    public static Diagnostic RecognizeTerminalCount(
        string rule, string where, int found, int expected) => Diagnostic.Create(
        "lvs.recognize.terminal-count", DiagnosticSeverity.Warning,
        "Device rule '{rule}' matched a body at {where} with {found} terminal(s) where {expected} "
        + "are needed, so no device was made from it.",
        ("rule", rule), ("where", where), ("found", found), ("expected", expected));

    /// <summary>
    /// R-lvs14-3c. A body within a few percent of square, so which way is "along" is not decidable
    /// from the shape.
    /// </summary>
    /// <remarks>
    /// <b>The device IS emitted; only the values that depend on the axis are withheld.</b> The
    /// topology is real and comparable — R-lvs3-5a's rule, that a device the comparison cannot
    /// fully handle must still appear in the count — and a resistor read the wrong way round is
    /// off by (L/W)² with nothing saying so, which is the one outcome that must not happen.
    /// </remarks>
    public static Diagnostic RecognizeAmbiguousAxis(
        string rule, string path, string where, string parameters) => Diagnostic.Create(
        "lvs.recognize.ambiguous-axis", DiagnosticSeverity.Warning,
        "'{path}' ({rule}) is within a few percent of square at {where}, so nothing says which way "
        + "is its length. {parameters} depend on that and were left unclaimed rather than guessed "
        + "at — read the wrong way round a length-over-width value is out by the square of the "
        + "ratio.",
        ("rule", rule), ("path", path), ("where", where), ("parameters", parameters));

    /// <summary>
    /// R-lvs14-2c, at run time. <b>The deck should have been refused by <c>check</c> first</b> —
    /// <c>TechValidation</c> parses every rule and evaluates every constant — so this fires only
    /// where somebody ran without checking, and it names the rule rather than failing the run.
    /// </summary>
    public static Diagnostic RecognizeRuleInvalid(string rule, string detail) => Diagnostic.Create(
        "lvs.recognize.rule-invalid", DiagnosticSeverity.Error,
        "Device rule '{rule}' cannot be read and recognised nothing: {detail}",
        ("rule", rule), ("detail", detail));

    /// <summary>
    /// R-lvs14-4b. A formula that did not evaluate on one candidate's own measurements.
    /// </summary>
    /// <remarks>
    /// <b>The device keeps its terminals and claims nothing about that parameter.</b> Inventing a
    /// value the rule did not produce is the one thing recognition may never do, and brief 10's
    /// compare-only-where-both-claim already answers a device that claims nothing.
    /// </remarks>
    public static Diagnostic RecognizeParameterFailed(
        string path, string rule, string parameter, string detail) => Diagnostic.Create(
        "lvs.recognize.parameter-failed", DiagnosticSeverity.Warning,
        "'{path}' ({rule}): {parameter} did not evaluate on this body's own geometry, so it claims "
        + "no value. {detail}",
        ("path", path), ("rule", rule), ("parameter", parameter), ("detail", detail));

    // ── The assembly's bond wires (brief-lvs-13-assemblies.md) ───────────────

    /// <summary>
    /// R-lvs13-5b. <b>Which wires were read</b> — the one the ENGINE would simulate, said out loud.
    /// </summary>
    /// <remarks>
    /// Info, and unconditional wherever a wBond is compared. Verifying wires the engine will not
    /// simulate verifies a design nobody runs, and the only way a reader can tell which of the two
    /// sources answered is to be told: a Carried instance and a Linked one look identical in every
    /// other line of the report.
    /// </remarks>
    public static Diagnostic WBondWiresRead(string path, string source, string from, int arrays, int wires)
        => Diagnostic.Create(
            "lvs.wbond.wires-read", DiagnosticSeverity.Info,
            "wBond '{path}' was compared against its {source} wires ({from}): {arrays} array(s), "
            + "{wires} wire(s). These are the wires the next Run simulates.",
            ("path", path), ("source", source), ("from", from),
            ("arrays", arrays), ("wires", wires));

    /// <summary>
    /// R-lvs13-3c. A wire foot on no copper at all.
    /// </summary>
    /// <remarks>
    /// <b>The ARRAY, the WIRE and the COORDINATE</b>, because a wBond has no designator to name and
    /// no pad to point at: "a foot is unbonded" with nothing else attached is a sentence a user
    /// cannot act on. The coordinate carries the layout's own unit for
    /// <see cref="LvsGeometryNaming"/>'s reason.
    /// </remarks>
    public static Diagnostic WBondFootOnNothing(
        string path, string array, int wire, string end, string where, long x, long y)
        => Diagnostic.Create(
            "lvs.wbond.foot-on-nothing", DiagnosticSeverity.Error,
            "wBond '{path}' array '{array}' wire {wire}: its {end} foot lands at {where}, where "
            + "there is no copper. A bond wire reaches a net through its foot, so this one reaches "
            + "nothing.",
            ("path", path), ("array", array), ("wire", wire), ("end", end),
            ("where", where), ("x", x), ("y", y));

    /// <summary>
    /// R-lvs13-4c. The array list moved under a placed instance, so its pins did.
    /// </summary>
    /// <remarks>
    /// <b>Reported BEFORE any net is compared, and the instance is then left out of the
    /// comparison.</b> A wBond's pin order IS its array order, so a reorder genuinely re-points
    /// every pin while the schematic's wiring stays where it was drawn — comparing nets against
    /// that produces 2M findings that are all real and all about the wrong thing. One line naming
    /// the drift is the finding; the cascade would bury it.
    ///
    /// <para>The drift itself is <c>WBondPlacement.DriftBetween</c>'s answer, consumed rather than
    /// re-derived (R-lvs13-4b): that function exists for exactly this failure and a second
    /// implementation here would be a second opinion about it.</para>
    /// </remarks>
    public static Diagnostic WBondArrayDrift(string path, string recorded, string current)
        => Diagnostic.Create(
            "lvs.wbond.array-drift", DiagnosticSeverity.Error,
            "wBond '{path}' was wired against arrays {recorded} and now declares {current}. Every "
            + "pin keeps its position while its name moves, so the schematic's wires connect to "
            + "different arrays than they were drawn for. It was left out of the comparison until "
            + "that is settled — re-import the wires, or re-wire the symbol.",
            ("path", path), ("recorded", recorded), ("current", current));

    /// <summary>
    /// R-lvs13-5c. A Carried instance whose payload no longer matches the cell's own <c>.wBond</c>.
    /// </summary>
    /// <remarks>
    /// <b>A warning, not an error.</b> wbond.md §9.6 calls this state normal and recoverable — it is
    /// what drawing in the layout and not yet pushing to the schematic looks like. The rule is that
    /// it must never be QUIET, not that it must be prevented, so the sentence names the command
    /// that settles it.
    /// </remarks>
    public static Diagnostic WBondPayloadDrift(string path, string file)
        => Diagnostic.Create(
            "lvs.wbond.payload-drift", DiagnosticSeverity.Warning,
            "wBond '{path}' carries its own wires, and they differ from '{file}' beside the "
            + "artwork. The carried copy is what runs and what was compared. Use "
            + "\"Update Schematic from wBond Layout\" to bring the two back together.",
            ("path", path), ("file", file));
}
