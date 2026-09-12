using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// The CLI's own refusals, argument errors and forwarded run notes, as coded diagnostics
/// (brief-automation-1-structured-output.md R-aut1-6/R-aut1-7).
///
/// <para><b>Every template here renders to the sentence stderr already printed, character for
/// character.</b> That is the whole constraint: R-aut0-3 says a script parsing stderr today must not
/// notice this landing, and the way to be sure of it is to make the diagnostic the SOURCE of the
/// sentence rather than a second copy of it. Where a call site wrote a prefix (<c>warning: </c>,
/// <c>note: </c>, <c>[circuitRF] </c>) the prefix stays at the call site, because it is a channel
/// convention rather than part of the message.</para>
///
/// <para><b>The ids are permanent.</b> <see cref="Diagnostic"/>'s own remarks are explicit: reword a
/// template freely, change an id and you have made a new diagnostic. <c>CliDiagnosticIdTests</c>
/// exists to turn an accidental id change into a failing test rather than a silently broken caller.
/// The first segment scopes to the producing area — <c>cli.</c> for this program's own argument and
/// file handling, <c>elab.</c> for what elaboration said, <c>lp.</c> for the loadpull verbs,
/// <c>convert.</c> for the interchange verb. <c>em.</c> is not minted here: those already exist in
/// <c>EmDiagnostics</c> and arrive on <c>EmRunResult.Diagnostic</c>.</para>
///
/// <para><b>What is deliberately NOT converted.</b> R-aut1-7 draws the line at the sites this
/// brief's verbs actually reach. Warnings authored deep in the engines still cross as finished
/// English and are wrapped here argument-free, with an honest id, rather than being re-authored or —
/// worse — having arguments invented by parsing the prose back apart. The count that remains is
/// reported in <c>src/Cli/RESOLVED.md</c> rather than left as a guess.</para>
/// </summary>
internal static class CliDiagnostics
{
    // ── arguments and files ──────────────────────────────────────────────────

    /// <summary>A verb invoked with no input file.</summary>
    public static Diagnostic InputRequired(string verb, string what) => Diagnostic.Create(
        "cli.args.input-required",
        DiagnosticSeverity.Error,
        "{verb}: input {what} file required",
        ("verb", verb), ("what", what));

    /// <summary>The input path does not exist.</summary>
    public static Diagnostic FileNotFound(string path) => Diagnostic.Create(
        "cli.input.not-found",
        DiagnosticSeverity.Error,
        "File not found: {path}",
        ("path", path));

    /// <summary><c>--set</c> without an <c>=</c>.</summary>
    /// <summary>
    /// An option the run verb does not read.
    ///
    /// <para><b>Refused rather than ignored, for the reason every other refusal in this file
    /// exists.</b> The five run verbs used to drop an unrecognised flag in silence, and the silence
    /// was not the whole cost: a verb that finds its input by "the first token that does not start
    /// with a dash" then takes the DROPPED flag's VALUE as its input path, so
    /// <c>lp x.cnl --maxmix 3</c> answered <c>File not found: 3</c> — a refusal naming neither the
    /// real problem nor the file the caller actually gave. Where the value was not swallowed, as in
    /// <c>dc x.cnl --set Vg=1</c>, nothing was reported at all and the run answered a different
    /// question than the one asked. <c>convert</c>, <c>new</c>, <c>import</c>, <c>check</c>,
    /// <c>explain</c> and <c>read</c> have refused an unknown option since they were written; this
    /// is the same rule reaching the verbs that predate it.</para>
    /// </summary>
    public static Diagnostic RunUnknownOption(string verb, string option) => Diagnostic.Create(
        "cli.args.unknown-option",
        DiagnosticSeverity.Error,
        "{verb}: unknown option '{option}'.",
        ("verb", verb), ("option", option));

    /// <summary>A second positional where the verb takes one. <c>convert</c>, <c>check</c>,
    /// <c>explain</c> and <c>read</c> have said this since they were written.</summary>
    public static Diagnostic RunMultipleInputs(string verb, string extra) => Diagnostic.Create(
        "cli.args.multiple-inputs",
        DiagnosticSeverity.Error,
        "{verb}: one input file, please — '{extra}' is a second one.",
        ("verb", verb), ("extra", extra));

    public static Diagnostic SetMalformed(string verb, string text) => Diagnostic.Create(
        "cli.args.set-malformed",
        DiagnosticSeverity.Error,
        "{verb}: --set expects name=expr, got '{text}'",
        ("verb", verb), ("text", text));

    /// <summary><c>--pin</c> that is not three parseable numbers.</summary>
    public static Diagnostic PinMalformed(string verb, string text) => Diagnostic.Create(
        "cli.args.pin-malformed",
        DiagnosticSeverity.Error,
        "{verb}: --pin expects start:step:max in dBm, got '{text}'",
        ("verb", verb), ("text", text));

    /// <summary>A Γ grid handed to a pursuit, which searches for its terminations instead of
    /// reading them. Refused rather than ignored: a grid silently not applied is a run that answers
    /// a different question and reports nothing about it.</summary>
    public static Diagnostic GridNotForPursuit() => new(
        "cli.args.grid-not-for-pursuit",
        DiagnosticSeverity.Error,
        "lpp: --grid does not apply to a pursuit — a pursuit SEARCHES for its terminations rather " +
        "than reading a grid. Use --out-grid to say where the terminations it finds are written.");

    /// <summary><c>--out-grid</c> on a loadpull, which reads a grid rather than writing one.</summary>
    public static Diagnostic OutGridNotForLoadpull() => new(
        "cli.args.out-grid-not-for-loadpull",
        DiagnosticSeverity.Error,
        "lp: --out-grid applies to lpp (the pursuit writes a grid; a loadpull reads one).");

    /// <summary>A workspace named by <c>--workspace</c> that is not there.</summary>
    public static Diagnostic WorkspaceNotFound(string path) => Diagnostic.Create(
        "cli.workspace.not-found",
        DiagnosticSeverity.Error,
        "Workspace file not found: {path}",
        ("path", path));

    /// <summary>No verb, or one this program does not have.</summary>
    public static Diagnostic UnknownVerb(string verb) => Diagnostic.Create(
        "cli.verb.unknown",
        DiagnosticSeverity.Error,
        "No such command: '{verb}'",
        ("verb", verb));

    /// <summary>Invoked with nothing at all.</summary>
    public static Diagnostic NoCommand() => new(
        "cli.verb.none",
        DiagnosticSeverity.Error,
        "No command given.");

    // ── running ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The chain selection said no — the netlist declares no analysis of the verb's kind, its chain
    /// is disabled, or the name asked for is not there. The reason is <c>SelectTop</c>'s own
    /// sentence, forwarded rather than re-authored: it names the declared analyses, which is the
    /// part that is actually actionable.
    /// </summary>
    public static Diagnostic NoAnalysis(string verb, string reason) => Diagnostic.Create(
        "cli.analysis.not-selected",
        DiagnosticSeverity.Error,
        "{verb}: {reason}",
        ("verb", verb), ("reason", reason));

    /// <summary>The run threw. The message is the exception's, which is where the only detail is.</summary>
    public static Diagnostic RunFailed(string message) => Diagnostic.Create(
        "cli.run.failed",
        DiagnosticSeverity.Error,
        "Error: {message}",
        ("message", message));

    /// <summary>A <c>.cem</c> that could not be read at all.</summary>
    public static Diagnostic SetupUnreadable(string path, string message) => Diagnostic.Create(
        "cli.em.setup-unreadable",
        DiagnosticSeverity.Error,
        "Could not read '{path}': {message}",
        ("path", path), ("message", message));

    // ── what the shape of a loadpull result says about the run (AUT-9 R-aut9-4/5/6) ──
    //
    // All three are WARNINGS and none of them changes an exit code. A loadpull grid with dead points
    // in it is an ordinary, useful result (LoadpullExitCode says so and gives the reason), and these
    // exist because `status` alone was not a diagnosis — not to turn a report into a failure.

    /// <summary>
    /// Every converged drive step delivered the engine's floor power (R-aut9-4).
    ///
    /// <para><b>This is the run that persuaded a client a working component was broken.</b> A
    /// mis-wired tuner left the bias tee delivering nothing; the run returned
    /// <c>Pout = -300.00 dBm</c> at all 56 drive points, exited <c>status: ok</c> with no
    /// diagnostic, and the client wrote up the model as defective with a minimal reproduction case.
    /// <c>-300 dBm</c> is a sentinel — what <c>10·log10(P)</c> is replaced by when P is zero — and
    /// nothing in the document said so.</para>
    /// </summary>
    public static Diagnostic LoadpullDeviceInert(int steps) => Diagnostic.Create(
        "lp.device-inert",
        DiagnosticSeverity.Warning,
        "The device delivered no power at any drive: all {steps} converged drive step(s) report the " +
        "engine's floor (-300 dBm), which is a sentinel and not a measurement. That usually means " +
        "the device is unbiased or off — check the bias network and the tuner/port wiring before " +
        "reading these figures as a measurement of the device.",
        ("steps", steps));

    /// <summary>
    /// Not one grid point converged a real drive step (R-aut9-5). The counts and the stop-code
    /// distribution are what stderr already carried and the document did not.
    /// </summary>
    public static Diagnostic LoadpullNothingConverged(int attempted, int steps, string stops) => Diagnostic.Create(
        "lp.nothing-converged",
        DiagnosticSeverity.Warning,
        "No grid point converged a drive step: {attempted} point(s) attempted, {steps} drive step(s) " +
        "walked, 0 converged. Stop codes: {stops}.",
        ("attempted", attempted), ("steps", steps), ("stops", stops));

    /// <summary>
    /// The first drive step is far above the tickle, on a run where nothing past the tickle
    /// converged (R-aut9-6). A blanket non-convergence that one parameter fixes should not look
    /// like a broken circuit.
    /// </summary>
    public static Diagnostic LoadpullTickleGap(double tickleDbm, double firstDbm) => Diagnostic.Create(
        "lp.tickle-gap",
        DiagnosticSeverity.Warning,
        "The first drive step is {gap} dB above the tickle ({tickle} dBm to {first} dBm), and " +
        "nothing past the tickle converged. A jump that large breaks the harmonic-balance warm " +
        "start; lower PinStart towards the tickle (--pin <start>:<step>:<max>) and try again before " +
        "concluding the circuit is at fault.",
        ("gap",    (firstDbm - tickleDbm).ToString("G4", System.Globalization.CultureInfo.InvariantCulture)),
        ("tickle", tickleDbm.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)),
        ("first",  firstDbm.ToString("G4",  System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A <c>.spl</c>/<c>.lpcwave</c> asked of a result that carries no Γ surface.</summary>
    public static Diagnostic NoLoadpullSurface(string extension) => Diagnostic.Create(
        "lp.export.no-surface",
        DiagnosticSeverity.Error,
        "Cannot write {extension}: this result carries no loadpull surface (no GammaLoad cube). " +
        "A pursuit that found no optimum has no follow-on grid to export — use .npy/.mat to " +
        "keep what it did produce.",
        ("extension", extension));

    /// <summary>
    /// An <c>-o</c> whose extension names no format <c>sparam</c> can write (R-aut9-1).
    ///
    /// <para>The alternative this replaces was worse than a refusal: every <c>-o</c> was written as
    /// Touchstone whatever it was called, so a caller who asked for <c>.npy</c> was handed a file
    /// beginning <c>! NOTE:</c> under that name and learned about it only from a later reader's
    /// accurate "expected magic \x93NUMPY". Writing format A to a path named B is the one option
    /// worth removing.</para>
    /// </summary>
    public static Diagnostic SparamUnsupportedExportFormat(string path, string extension) => Diagnostic.Create(
        "sparam.export.unsupported-format",
        DiagnosticSeverity.Error,
        "sparam: '{path}' — '{extension}' names no format this verb writes. Write a Touchstone " +
        "(.s1p … .s99p, and the default with no -o), or .npy, .mat, .txt for the cubes.",
        ("path", path), ("extension", extension));

    /// <summary>
    /// A Touchstone was asked for from a run that produced no S — a port-less WSProbe run
    /// (brief-wsprobe-1 R-wsp1-6, R-wsp1-12(a)). Refused naming the spellings that DO carry the
    /// result, because the run is not empty: its wsp cubes are exactly what a cube file holds.
    /// </summary>
    public static Diagnostic SparamNoSParameters(string path) => Diagnostic.Create(
        "sparam.export.no-s-parameters",
        DiagnosticSeverity.Error,
        "sparam: the run has no S-parameters (no Port/Term — only WSProbe outputs), so there is no " +
        "Touchstone to write for '{path}'. Write the cubes instead: -o out.npy, out.mat or out.txt.",
        ("path", path));

    /// <summary>
    /// An <c>--at</c> or a <c>--range</c> whose SPELLING is wrong. Refused before the run, which is
    /// the whole reason it is a separate diagnostic from the one below: the axis names cannot be
    /// checked until there is a result, but <c>--at freq</c> with no value can be, and there is no
    /// reason to make a caller wait for a solve to learn it.
    /// </summary>
    public static Diagnostic NarrowingMalformed(string option, string text) => Diagnostic.Create(
        "cli.narrow.malformed",
        DiagnosticSeverity.Error,
        "{option} '{text}' is malformed. Write --at <axis>=<value> (--at freq=2GHz), " +
        "--range <axis>=<lo>:<hi> (--range freq=1GHz:3GHz), or --format summary|full.",
        ("option", option), ("text", text));

    // A narrowing that could not be honoured against the result that came back — an axis no cube
    // has, or a value that is not one — is NOT declared here: those are
    // RfCore.Export.NarrowingDiagnostics' own (narrow.axis.unknown, narrow.value.malformed,
    // narrow.value.missing, narrow.range.empty), because the rule about which axes exist belongs
    // where the axes do. They fail the invocation: any file the run wrote is still named in
    // `outputs` and the result's SHAPE still comes back, so nothing is lost — what a caller must
    // not receive is the whole un-narrowed result under the impression that it answers the
    // question asked.

    // ── forwarded, argument-free ─────────────────────────────────────────────
    //
    // R-aut1-7's last clause: where the producing site does not have typed values to hand, wrap the
    // existing sentence with an honest id rather than inventing arguments by parsing prose. Each of
    // these is a whole English sentence authored somewhere below, and the id says only where it came
    // from — which is still enough to filter, group and deduplicate on, and is the entire point.

    /// <summary>Something elaboration reported about itself. Not a problem.</summary>
    public static Diagnostic ElaborationNote(string text) => Diagnostic.Create(
        "elab.note", DiagnosticSeverity.Info, "{text}", ("text", text));

    /// <summary>Something elaboration or an engine wants looked at.</summary>
    public static Diagnostic ElaborationWarning(string text) => Diagnostic.Create(
        "elab.warning", DiagnosticSeverity.Warning, "{text}", ("text", text));

    /// <summary>A <c>measure</c> line that would not evaluate.</summary>
    public static Diagnostic MeasurementFailed(string text) => Diagnostic.Create(
        "cli.measurement.failed", DiagnosticSeverity.Warning, "{text}", ("text", text));

    /// <summary>A warning from the EM setup resolver — a technology that would not resolve, say.</summary>
    public static Diagnostic EmSetupWarning(string text) => Diagnostic.Create(
        "em.setup.warning", DiagnosticSeverity.Warning, "{text}", ("text", text));

    /// <summary>An <see cref="Diagnostic"/>-free entry from one of <c>EmRunResult</c>'s three
    /// lists. The lists are strings — only the top-level refusal carries a coded form — so the id
    /// records which of the three it was, which is exactly the distinction the split exists for.</summary>
    public static Diagnostic EmRunNote(string text) => Diagnostic.Create(
        "em.run.note", DiagnosticSeverity.Info, "{text}", ("text", text));

    /// <inheritdoc cref="EmRunNote"/>
    public static Diagnostic EmRunWarning(string text) => Diagnostic.Create(
        "em.run.warning", DiagnosticSeverity.Warning, "{text}", ("text", text));

    /// <inheritdoc cref="EmRunNote"/>
    public static Diagnostic EmRunError(string text) => Diagnostic.Create(
        "em.run.error", DiagnosticSeverity.Error, "{text}", ("text", text));

    /// <summary>A device worker's own log, which holds facts stated nowhere else.</summary>
    public static Diagnostic WorkerOutput(string provider, string text) => Diagnostic.Create(
        "cli.worker.output",
        DiagnosticSeverity.Info,
        "{text}",
        ("provider", provider), ("text", text));

    // ── convert ──────────────────────────────────────────────────────────────
    //
    // Named one by one rather than through a generic `Convert(kind, text)` factory, because an id is
    // a permanent contract (R-aut1-8) and a factory that MINTS ids from a caller's string is a
    // contract nobody can enumerate, review or hold still.

    public static Diagnostic ConvertUnknownDxfVersion(string version) => Diagnostic.Create(
        "convert.args.dxf-version", DiagnosticSeverity.Error,
        "Unknown DXF version '{version}'. Known: AC1015, AC1018, AC1032.", ("version", version));

    public static Diagnostic ConvertUnknownDrillUnit(string unit) => Diagnostic.Create(
        "convert.args.drill-units", DiagnosticSeverity.Error,
        "Unknown drill unit '{unit}'. Known: mm, inch.", ("unit", unit));

    public static Diagnostic ConvertUnknownDrillZeros(string zeros) => Diagnostic.Create(
        "convert.args.drill-zeros", DiagnosticSeverity.Error,
        "Unknown zero suppression '{zeros}'. Known: leading, trailing.", ("zeros", zeros));

    public static Diagnostic ConvertBadDrillFormat(string text) => Diagnostic.Create(
        "convert.args.drill-format", DiagnosticSeverity.Error,
        "--drill-format wants integer:decimal digits, e.g. 2:4 — got '{text}'.", ("text", text));

    /// <summary>
    /// Distinct from <see cref="FileNotFound"/>, and not merged with it: <c>convert</c>'s input may be
    /// a FOLDER (a Gerber file set), so it has always said "Input not found" rather than "File not
    /// found". Changing the word would change stderr, which R-aut0-3 forbids — and it would be the
    /// wrong word besides.
    /// </summary>
    public static Diagnostic ConvertInputNotFound(string path) => Diagnostic.Create(
        "convert.input.not-found", DiagnosticSeverity.Error,
        "Input not found: {path}", ("path", path));

    public static Diagnostic ConvertUnknownOption(string option) => Diagnostic.Create(
        "convert.args.unknown-option", DiagnosticSeverity.Error,
        "Unknown option: {option}", ("option", option));

    public static Diagnostic ConvertMultipleInputs() => new(
        "convert.args.multiple-inputs", DiagnosticSeverity.Error,
        "convert takes one input.");

    public static Diagnostic ConvertOutputRequired() => new(
        "convert.args.output-required", DiagnosticSeverity.Error,
        "convert needs an output: -o <path>.");

    public static Diagnostic ConvertUnknownFormat(string format) => Diagnostic.Create(
        "convert.args.unknown-format", DiagnosticSeverity.Error,
        "Unknown format '{format}'. Known: clay, gdsii, dxf, gerber, board.", ("format", format));

    /// <summary>The usage text itself, recorded so a document says what stderr said. Both lines,
    /// because both were printed.</summary>
    public static Diagnostic ConvertUsage() => new(
        "convert.args.usage", DiagnosticSeverity.Error,
        "Usage: circuitrf convert <input> -o <output> [--from f] [--to f] [--cell name]\n" +
        "       formats: clay | gdsii | dxf | gerber | board\n" +
        "       --no-coalesce  keep a painted pour's individual strokes");

    public static Diagnostic ConvertListCellsNotApplicable() => new(
        "convert.args.list-cells-not-applicable", DiagnosticSeverity.Error,
        "A .clay names one cell — --list-cells applies to a file that can hold several.");

    public static Diagnostic ConvertSourceUnrecognised(string fileName) => Diagnostic.Create(
        "convert.source.unrecognised", DiagnosticSeverity.Error,
        "Could not tell what '{fileName}' is from its name or its content. " +
        "Name it with --from clay|gdsii|dxf|gerber|board.", ("fileName", fileName));

    public static Diagnostic ConvertTargetUnrecognised(string output) => Diagnostic.Create(
        "convert.target.unrecognised", DiagnosticSeverity.Error,
        "'{output}' does not name a format. Give it a known extension " +
        "(.clay, .gds, .dxf, .kicad_pcb) or say --to clay|gdsii|dxf|gerber|board.", ("output", output));

    public static Diagnostic ConvertClayToClay() => new(
        "convert.clay-to-clay", DiagnosticSeverity.Error,
        "clay to clay is a file copy, not a conversion — nothing to do.");

    /// <summary>
    /// R-aut12-4. A <c>clay</c> target is a DIRECTORY of cell folders, and asking for
    /// <c>out/board.clay</c> used to produce a directory of exactly that name with the real
    /// <c>.clay</c> two levels inside it. The result document did report the true paths, so nothing
    /// was lost — but a path that names a file and yields a directory of that name is a surprise, and
    /// the answer is to say what shape the argument has rather than to invent a collapse that would
    /// discard a hierarchy the moment an import produced one.
    /// </summary>
    public static Diagnostic ConvertClayTargetIsADirectory(string output, string suggestion)
        => Diagnostic.Create(
            "convert.target.clay-needs-directory", DiagnosticSeverity.Error,
            "An import produces one cell FOLDER per structure plus a technology beside them, so a "
          + "clay target is a directory, not a file: '{output}' would have become a directory of that "
          + "name with the .clay two levels inside it. Write `-o {suggestion} --to clay`.",
            ("output", output), ("suggestion", suggestion));

    /// <summary>R-aut12-4's other half: the named directory is an existing FILE. Nothing here
    /// overwrites, so this is a refusal rather than a delete.</summary>
    public static Diagnostic ConvertClayTargetIsAFile(string output) => Diagnostic.Create(
        "convert.target.clay-is-a-file", DiagnosticSeverity.Error,
        "'{output}' is an existing file, and a clay target is a directory of cell folders. "
      + "Name a directory.", ("output", output));

    public static Diagnostic ConvertFailed(string message) => Diagnostic.Create(
        "convert.failed", DiagnosticSeverity.Error, "{message}", ("message", message));

    public static Diagnostic ConvertTechnologyUnreadable(string path, string message) => Diagnostic.Create(
        "convert.technology.unreadable", DiagnosticSeverity.Error,
        "Could not read technology '{path}': {message}", ("path", path), ("message", message));

    public static Diagnostic ConvertLayoutUnreadable(string path, string message) => Diagnostic.Create(
        "convert.layout.unreadable", DiagnosticSeverity.Error,
        "Could not read layout '{path}': {message}", ("path", path), ("message", message));

    public static Diagnostic ConvertNoCell() => new(
        "convert.import.no-cell", DiagnosticSeverity.Error,
        "The Gerber import produced no cell.");

    public static Diagnostic ConvertNothingConverted() => new(
        "convert.import.cancelled", DiagnosticSeverity.Error,
        "Nothing was converted.");

    /// <summary>
    /// The one refusal that exists because a guess would be worse than a stop: leading vs trailing
    /// zero suppression differ by four orders of magnitude on identical text. The evidence lines stay
    /// on stderr where they were; the typed half is here.
    /// </summary>
    public static Diagnostic ConvertDrillFormatUnstated(string fileName, string inferred) => Diagnostic.Create(
        "convert.drill.format-unstated", DiagnosticSeverity.Error,
        "{fileName} does not state its coordinate format, and the inference had to guess.",
        ("fileName", fileName), ("inferred", inferred));

    /// <summary>GI4 R-gi4-10: a folder whose only artwork is inside an archive, and the flag that
    /// opens it. An offer in the GUI is a refusal naming a flag here.</summary>
    public static Diagnostic ConvertArchiveNotOpened(string archives) => Diagnostic.Create(
        "convert.gerber.archive-not-opened", DiagnosticSeverity.Error,
        "This folder holds no Gerber artwork of its own, only {archives}. Pass --open-archives to look inside.",
        ("archives", archives));

    public static Diagnostic ConvertCellHasNoLayout(string cellName) => Diagnostic.Create(
        "convert.cell.no-layout", DiagnosticSeverity.Error,
        "'{cellName}' holds no layout view to convert.", ("cellName", cellName));

    public static Diagnostic ConvertCellNotFound(string wanted, string held) => Diagnostic.Create(
        "convert.cell.not-found", DiagnosticSeverity.Error,
        "No cell named '{wanted}' in this file. It holds: {held}", ("wanted", wanted), ("held", held));

    public static Diagnostic ConvertCellAmbiguous(int count, string what) => Diagnostic.Create(
        "convert.cell.ambiguous", DiagnosticSeverity.Error,
        "This file holds {count} cells and none of them is an unambiguous top ({what} definitions " +
        "are all referenced by something else). Name one with --cell, or list them with --list-cells.",
        ("count", count), ("what", what));

    public static Diagnostic ConvertGdsiiCoordinateOverflow() => new(
        "convert.gdsii.coordinate-overflow", DiagnosticSeverity.Error,
        "coordinates overflow GDSII's 32-bit integer range — nothing written.");

    public static Diagnostic ConvertGerberDiagnostic(string text) => Diagnostic.Create(
        "convert.gerber.refused", DiagnosticSeverity.Error, "{text}", ("text", text));

    public static Diagnostic ConvertGerberHierarchyCeiling() => new(
        "convert.gerber.hierarchy-ceiling", DiagnosticSeverity.Error,
        "The design's cell hierarchy exceeds what the Gerber export can flatten.");

    public static Diagnostic ConvertGerberCrossTechnology() => new(
        "convert.gerber.cross-technology-mapping", DiagnosticSeverity.Error,
        "this design instantiates cells from another technology, and the layer mapping " +
        "has to be confirmed. Open it in circuitRF and export once, or flatten the design first.");

    public static Diagnostic ConvertBoardRefused(string reason) => Diagnostic.Create(
        "convert.board.refused", DiagnosticSeverity.Error, "{reason}", ("reason", reason));

    public static Diagnostic ConvertTargetUnsupported(string format) => Diagnostic.Create(
        "convert.target.unsupported", DiagnosticSeverity.Error,
        "Cannot write {format}.", ("format", format));

    /// <summary>
    /// One of the import's own messages — a layer that was mapped, a zone that was not imported, a
    /// stackup section the file did not carry. On stderr since the verb was written, and in the
    /// document since AUT-4: they are how a caller learns what was DROPPED, and a `--json` caller
    /// that could not read them would be reading a conversion report that omits the losses.
    ///
    /// <para>Argument-free by the same rule the forwarded block above follows: an importer hands over
    /// a finished English sentence and has no typed values to give, so the id says where it came from
    /// and nothing is invented by parsing the prose apart.</para>
    /// </summary>
    public static Diagnostic ConvertNote(string text) => Diagnostic.Create(
        "convert.note", DiagnosticSeverity.Info, "{text}", ("text", text));

    /// <summary>
    /// What <c>--list-cells</c> found. The listing is the RESULT of that invocation — the whole
    /// answer to "what does this file hold?" — so under `--json` it belongs in the document rather
    /// than only on the stdout the flag replaces (R-aut1-3).
    /// </summary>
    public static Diagnostic ConvertCellListed(string cell) => Diagnostic.Create(
        "convert.cell.listed", DiagnosticSeverity.Info, "{cell}", ("cell", cell));

    // ── new / import part (brief-automation-3-authoring-verbs.md) ─────────────
    //
    // The authoring verbs' whole contract is "it was created, or it was refused and the sentence says
    // what would fix it" (§7.5), so every refusal here NAMES the flag or the value that answers it.
    // Ids are `new.` and `import.`, and permanent like every other one.

    public static Diagnostic NewNounRequired() => new(
        "new.args.noun-required", DiagnosticSeverity.Error,
        "new: say what to create — 'new workspace' or 'new cell'.");

    public static Diagnostic NewUnknownNoun(string noun) => Diagnostic.Create(
        "new.args.unknown-noun", DiagnosticSeverity.Error,
        "new: there is nothing called '{noun}' to create. Known: workspace, cell.", ("noun", noun));

    public static Diagnostic NewUnknownOption(string option) => Diagnostic.Create(
        "new.args.unknown-option", DiagnosticSeverity.Error,
        "new: unknown option '{option}'.", ("option", option));

    public static Diagnostic NewMultipleDirectories() => new(
        "new.args.multiple-directories", DiagnosticSeverity.Error,
        "new workspace: one directory, please — use --name to give the workspace a name inside it.");

    public static Diagnostic NewWorkspaceDirRequired() => new(
        "new.args.directory-required", DiagnosticSeverity.Error,
        "new workspace: a directory is required.");

    public static Diagnostic NewCellArgsRequired() => new(
        "new.args.cell-args-required", DiagnosticSeverity.Error,
        "new cell: a workspace (or a folder inside one) and a cell name are required.");

    public static Diagnostic NewCellExtraArgument(string arg) => Diagnostic.Create(
        "new.args.cell-extra-argument", DiagnosticSeverity.Error,
        "new cell: unexpected argument '{arg}' — it takes a workspace and one cell name.", ("arg", arg));

    /// <summary>R-aut3-9: NameValidator's reason, never a second rule set — a headless caller must
    /// not be able to create a name the GUI would reject.</summary>
    public static Diagnostic NewInvalidName(string kind, string name, string reason) => Diagnostic.Create(
        "new.name.invalid", DiagnosticSeverity.Error,
        "Invalid {kind} name '{name}': {reason}", ("kind", kind), ("name", name), ("reason", reason));

    /// <summary>R-aut3-5: an unknown shipped-technology id lists the ones that exist. Not a fallback
    /// to the default — a caller that asked for a specific process and silently got another has a
    /// wrong design and no way to know.</summary>
    public static Diagnostic NewUnknownTechnology(string id, string known) => Diagnostic.Create(
        "new.tech.unknown", DiagnosticSeverity.Error,
        "No shipped technology named '{id}'. The shipped technologies are: {known}. "
        + "Use --tech none for a workspace with no technology.", ("id", id), ("known", known));

    public static Diagnostic NewParentNotFound(string path) => Diagnostic.Create(
        "new.parent.not-found", DiagnosticSeverity.Error,
        "No such directory: {path}", ("path", path));

    /// <summary>R-aut11-4: a missing parent is CREATED, and saying so is what keeps that from being
    /// a surprise — a caller that mistyped a path gets a directory rather than a refusal, and the
    /// note is where it finds out.</summary>
    public static Diagnostic NewParentCreated(string path) => Diagnostic.Create(
        "new.parent.created", DiagnosticSeverity.Info,
        "Created the parent directory {path}.", ("path", path));

    public static Diagnostic NewParentNotCreated(string path, string why) => Diagnostic.Create(
        "new.parent.not-created", DiagnosticSeverity.Error,
        "The parent directory {path} could not be created: {why}", ("path", path), ("why", why));

    /// <summary>R-aut3-6, and the read-only-parent refusal: the capability's own sentence, forwarded
    /// rather than re-worded, so the GUI and the verb refuse in the same words.</summary>
    public static Diagnostic NewRefused(string reason) => Diagnostic.Create(
        "new.refused", DiagnosticSeverity.Error, "{reason}", ("reason", reason));

    public static Diagnostic NewCellExists(string name) => Diagnostic.Create(
        "new.cell.exists", DiagnosticSeverity.Error,
        "A cell named '{name}' already exists there. Nothing was created.", ("name", name));

    public static Diagnostic NewUnknownView(string view) => Diagnostic.Create(
        "new.views.unknown", DiagnosticSeverity.Error,
        "--views: '{view}' is not a view. Known: schematic, symbol, layout.", ("view", view));

    public static Diagnostic ImportNounRequired() => new(
        "import.args.noun-required", DiagnosticSeverity.Error,
        "import: say what to import — 'import part'.");

    public static Diagnostic ImportUnknownNoun(string noun) => Diagnostic.Create(
        "import.args.unknown-noun", DiagnosticSeverity.Error,
        "import: there is nothing called '{noun}' to import. Known: part. "
        + "For layout interchange (GDSII, DXF, Gerber, boards) use `convert`.", ("noun", noun));

    public static Diagnostic ImportUnknownOption(string option) => Diagnostic.Create(
        "import.args.unknown-option", DiagnosticSeverity.Error,
        "import part: unknown option '{option}'.", ("option", option));

    public static Diagnostic ImportMultipleSources() => new(
        "import.args.multiple-sources", DiagnosticSeverity.Error,
        "import part: one file or folder, please.");

    public static Diagnostic ImportSourceRequired() => new(
        "import.args.source-required", DiagnosticSeverity.Error,
        "import part: a component file or folder is required.");

    public static Diagnostic ImportIntoRequired() => new(
        "import.args.into-required", DiagnosticSeverity.Error,
        "import part: --into <workspace-or-dir> says where the cell is created.");

    public static Diagnostic ImportSourceNotFound(string path) => Diagnostic.Create(
        "import.source.not-found", DiagnosticSeverity.Error,
        "No such file or folder: {path}", ("path", path));

    /// <summary>A reader's or a scan's own refusal, forwarded whole.</summary>
    public static Diagnostic ImportRefused(string reason) => Diagnostic.Create(
        "import.refused", DiagnosticSeverity.Error, "{reason}", ("reason", reason));

    /// <summary>R-aut3-12: one import message, on stderr and in the document. They are how a caller
    /// learns a pin was inferred, a layer was dropped or a variant was skipped.</summary>
    public static Diagnostic ImportMessage(string text) => Diagnostic.Create(
        "import.note", DiagnosticSeverity.Info, "{text}", ("text", text));

    /// <summary>R-aut3-11: the chooser dialog's question, refused rather than guessed at.</summary>
    public static Diagnostic ImportAmbiguous(int count, string listing) => Diagnostic.Create(
        "import.part.ambiguous", DiagnosticSeverity.Error,
        "{count} parts here, and nothing said which. Name one with --cell (and --variant when two "
        + "share a name), or run with --list-parts:\n{listing}", ("count", count), ("listing", listing));

    public static Diagnostic ImportPartNotFound(string wanted, string held) => Diagnostic.Create(
        "import.part.not-found", DiagnosticSeverity.Error,
        "No part named '{wanted}' here. This holds: {held}.", ("wanted", wanted), ("held", held));

    public static Diagnostic ImportVariantNotFound(string variant) => Diagnostic.Create(
        "import.variant.not-found", DiagnosticSeverity.Error,
        "No part variant '{variant}' here — --variant takes the key in --list-parts' second column.",
        ("variant", variant));

    public static Diagnostic ImportNothingCreated() => new(
        "import.nothing-created", DiagnosticSeverity.Error,
        "The part was read but no cell was created.");

    /// <summary>The layers a part needs that its technology does not define. Reported ALWAYS, and
    /// written only when --add-layers says so — the GUI's own install is session-only and says in as
    /// many words that nothing was written to disk.</summary>
    public static Diagnostic ImportLayersNotInstalled(int count, string names) => Diagnostic.Create(
        "import.layers.not-installed", DiagnosticSeverity.Warning,
        "{count} layer(s) this part uses are not in the technology: {names}. The cell was created "
        + "with them; the technology was not changed. Pass --add-layers to write them into it.",
        ("count", count), ("names", names));

    /// <summary>No destination technology resolved at all. The reconciliation has nothing to compare
    /// the part's layers against, so it reports none as new and they land with numeric keys and no
    /// names — the same state the GUI is in with a technology-less workspace, said out loud rather
    /// than left to be discovered when the layout opens grey.</summary>
    public static Diagnostic ImportNoTechnology() => new(
        "import.no-technology", DiagnosticSeverity.Warning,
        "No technology resolved for this destination, so the part's layers arrive unnamed and none "
        + "is reported as new. Name one with --tech, or set the workspace's default technology.");

    public static Diagnostic ImportNoTechnologyToAddTo(string names) => Diagnostic.Create(
        "import.layers.no-technology", DiagnosticSeverity.Error,
        "--add-layers was given but no technology file resolved, so there is nowhere to put "
        + "{names}. Name one with --tech.", ("names", names));

    // ── check (brief-automation-4-check-and-explain.md) ───────────────────────
    //
    // R-aut4-2: `check` writes no validation logic of its own. Every id below names an EXISTING
    // validator's finding, and the typed arguments are that validator's own values, not a sentence
    // parsed back apart (R-aut4-4). Where a validator hands over only prose — the `.wasm` predicate
    // parser, the technology resolver, the EM setup resolver — the sentence is forwarded whole under
    // an honest id, exactly as the forwarded block above does for the engines.
    //
    // Two of these are deliberately NOT errors. A cell sub-folder holding several views and no named
    // primary is `PrimaryState.NoPrimary`, which that enum's own remarks call "not an error"; a
    // technology that resolves to nothing is `TechResolutionSource.None`, which layout-view.md §2.4
    // calls a normal, fully-supported state. Reporting either as an error would make `check` refuse
    // designs the application opens happily, which is the one thing it must never do.

    public static Diagnostic CheckPathRequired() => new(
        "check.args.path-required", DiagnosticSeverity.Error,
        "check: a path is required — a workspace, a cell folder, or one document.");

    public static Diagnostic CheckUnknownOption(string option) => Diagnostic.Create(
        "check.args.unknown-option", DiagnosticSeverity.Error,
        "check: unknown option '{option}'.", ("option", option));

    public static Diagnostic CheckMultiplePaths() => new(
        "check.args.multiple-paths", DiagnosticSeverity.Error,
        "check: one path, please.");

    public static Diagnostic CheckUnknownSeverity(string text) => Diagnostic.Create(
        "check.args.unknown-severity", DiagnosticSeverity.Error,
        "check: --severity takes warning or error, got '{text}'.", ("text", text));

    public static Diagnostic CheckPathNotFound(string path) => Diagnostic.Create(
        "check.path.not-found", DiagnosticSeverity.Error,
        "No such file or folder: {path}", ("path", path));

    /// <summary>A path circuitRF has no reader for. Not silence: a caller that pointed `check` at
    /// the wrong file learns that here rather than from a clean exit that checked nothing.</summary>
    public static Diagnostic CheckUnknownKind(string path) => Diagnostic.Create(
        "check.path.unknown-kind", DiagnosticSeverity.Error,
        "Nothing circuitRF reads is named '{path}' — check takes a workspace, a cell folder, or a "
        + ".csch, .csym, .clay, .ctech, .cem, .cnl, .wasm or a Touchstone .sNp.", ("path", path));

    /// <summary>An interchange file. Reported rather than checked, because there is nothing to check
    /// it AGAINST: a GDSII or Gerber file is not a circuitRF document and has no primacy, no
    /// technology reference and no analysis chain. `convert` is what reads it.</summary>
    public static Diagnostic CheckInterchangeNotADocument(string path, string format) => Diagnostic.Create(
        "check.path.interchange", DiagnosticSeverity.Info,
        "'{path}' is {format} interchange, not a circuitRF document — there is nothing to validate "
        + "until it is imported. Read it with `circuitrf convert`.", ("path", path), ("format", format));

    // ── Touchstone (`.sNp`) ──────────────────────────────────────────────────
    //
    //  Every finding here carries the NUMBER it measured and the frequency it measured it at. That
    //  is the whole point of checking a data file rather than a design: "not passive" is not
    //  actionable, "σmax = 1.04 at 6.2 GHz" sends someone to a plot. The measurements themselves are
    //  `RfCore.Data.TouchstoneHealth`'s — nothing is computed in the CLI.

    /// <summary>The facts, always reported, so a clean file still tells the caller what it read.</summary>
    public static Diagnostic CheckTouchstoneSummary(
        string path, int ports, int points, string band, string z0) => Diagnostic.Create(
        "check.touchstone.summary", DiagnosticSeverity.Info,
        "{path}: {ports}-port, {points} points, {band}, reference {z0}.",
        ("path", path), ("ports", ports), ("points", points), ("band", band), ("z0", z0));

    /// <summary>The extension claims one port count and the data holds another.</summary>
    public static Diagnostic CheckTouchstonePortMismatch(string path, int declared, int actual) =>
        Diagnostic.Create(
            "check.touchstone.port-mismatch", DiagnosticSeverity.Error,
            "{path}: the extension declares {declared} ports and the data holds {actual}.",
            ("path", path), ("declared", declared), ("actual", actual));

    /// <summary>
    /// A repeated or out-of-order frequency. An unambiguous defect and an error: every interpolator
    /// in circuitRF assumes a sorted axis, and on one that is not sorted each returns a plausible
    /// wrong number rather than refusing.
    /// </summary>
    public static Diagnostic CheckTouchstoneFrequencyOrder(string path, int index, string freq) =>
        Diagnostic.Create(
            "check.touchstone.frequency-order", DiagnosticSeverity.Error,
            "{path}: the frequency axis is not strictly increasing — point {index} is {freq}.",
            ("path", path), ("index", index), ("freq", freq));

    /// <summary>Re(Z0) ≤ 0. The power-wave renormalisation divides by √Re(Z0).</summary>
    public static Diagnostic CheckTouchstoneBadZ0(string path, string z0) => Diagnostic.Create(
        "check.touchstone.bad-z0", DiagnosticSeverity.Error,
        "{path}: reference impedance {z0} has Re(Z0) ≤ 0, which no renormalisation can use.",
        ("path", path), ("z0", z0));

    /// <summary>
    /// A reference impedance other than 50 Ω. Not a defect — 75 Ω parts exist — but reported
    /// because it is the assumption most often carried silently into a comparison against a 50 Ω
    /// model, where it shifts every curve and looks like a modelling error.
    /// </summary>
    public static Diagnostic CheckTouchstoneNonStandardZ0(string path, string z0) => Diagnostic.Create(
        "check.touchstone.z0-not-50", DiagnosticSeverity.Info,
        "{path}: the reference impedance is {z0}, not 50 Ω — anything compared against this "
        + "file must be renormalised to match.", ("path", path), ("z0", z0));

    /// <summary>
    /// σ_max(S) &gt; 1: the network has gain. A WARNING and never an error, because nothing in a
    /// Touchstone file says whether the part is meant to be passive — an amplifier's file is
    /// supposed to look like this. For a capacitor or a de-embedded fixture it is a defect.
    /// </summary>
    public static Diagnostic CheckTouchstoneNotPassive(string path, string sigma, string freq) =>
        Diagnostic.Create(
            "check.touchstone.not-passive", DiagnosticSeverity.Warning,
            "{path}: σmax = {sigma} at {freq} — this network has gain. Expected for an active "
            + "device; a defect in anything that should be passive.",
            ("path", path), ("sigma", sigma), ("freq", freq));

    /// <summary>
    /// A non-reciprocal file. Same framing as passivity: a circulator or an isolator is supposed to
    /// look like this, a two-terminal part is not.
    /// </summary>
    public static Diagnostic CheckTouchstoneNotReciprocal(string path, string err, string freq) =>
        Diagnostic.Create(
            "check.touchstone.not-reciprocal", DiagnosticSeverity.Warning,
            "{path}: max |Sij − Sji| = {err} at {freq} — this network is not reciprocal. Expected "
            + "for a ferrite part or an active one; a defect in a passive two-terminal component.",
            ("path", path), ("err", err), ("freq", freq));

    /// <summary>
    /// Pre-cursor energy well above the band-truncation floor.
    ///
    /// <para><b>Reports the measurement and both of its readings, and does not pick one.</b> A file
    /// fitted without a causality constraint and a file whose uniform sweep is too coarse to
    /// resolve its own response produce the same number, and nothing available here separates them
    /// — a ferrite bead sampled on a grid too coarse for its own corner reads 37 %, on the same
    /// side of the line as a delay running backwards at 56 %. Both readings matter to anyone using the file in the time domain,
    /// so the finding is worth making; asserting the first would be a claim the number does not
    /// support.</para>
    /// </summary>
    public static Diagnostic CheckTouchstoneNotCausal(string path, string ratio) => Diagnostic.Create(
        "check.touchstone.not-causal", DiagnosticSeverity.Warning,
        "{path}: {ratio} of the impulse-response energy lands before t = 0. Either this file is not "
        + "causal — the usual cause is a model fitted without a causality constraint — or this "
        + "sweep is too coarse to resolve its own low-frequency behaviour. Either way it will "
        + "misbehave in time-domain use.", ("path", path), ("ratio", ratio));

    /// <summary>
    /// The causality test did not run, and why. Reported rather than skipped in silence: "could not
    /// be checked" and "was checked and is fine" are different answers, and the common reason — a
    /// log-spaced sweep, which is what most vendor passive files are — is not a defect.
    /// </summary>
    public static Diagnostic CheckTouchstoneCausalitySkipped(string path, string reason) =>
        Diagnostic.Create(
            "check.touchstone.causality-skipped", DiagnosticSeverity.Info,
            "{path}: causality not evaluated — {reason}.", ("path", path), ("reason", reason));

    /// <summary><c>CellViewFileValidator.DescribeDefect</c>'s own sentence, which is written to be
    /// shown verbatim.</summary>
    public static Diagnostic CheckViewDefect(string path, string view, string defect) => Diagnostic.Create(
        "check.view.defect", DiagnosticSeverity.Error,
        "{path}: {defect}", ("path", path), ("view", view), ("defect", defect));

    public static Diagnostic CheckUnreadable(string path, string message) => Diagnostic.Create(
        "check.file.unreadable", DiagnosticSeverity.Error,
        "{path}: could not be read ({message}).", ("path", path), ("message", message));

    /// <summary>R-aut4-2's <c>MissingNamedPrimary</c> — the `.ccell` names a primary that is not
    /// there. <c>PrimaryState</c>'s own remarks say do not collapse this into NoPrimary.</summary>
    public static Diagnostic CheckPrimaryMissing(string cellDir, string view, string named) => Diagnostic.Create(
        "check.cell.primary-missing", DiagnosticSeverity.Error,
        "{cellDir}: the {view} view names '{named}' as primary and that file is not there.",
        ("cellDir", cellDir), ("view", view), ("named", named));

    public static Diagnostic CheckNoPrimary(string cellDir, string view, int count) => Diagnostic.Create(
        "check.cell.no-primary", DiagnosticSeverity.Warning,
        "{cellDir}: the {view} view holds {count} files and none is primary, so nothing resolves it.",
        ("cellDir", cellDir), ("view", view), ("count", count));

    /// <summary>R-aut3-9's rule, read the other way: a name the GUI would reject must not be
    /// reported clean here either. <c>NameValidator</c>'s own reason, never a second rule set.</summary>
    public static Diagnostic CheckInvalidName(string path, string kind, string name, string reason) =>
        Diagnostic.Create(
            "check.name.invalid", DiagnosticSeverity.Error,
            "{path}: invalid {kind} name '{name}' — {reason}",
            ("path", path), ("kind", kind), ("name", name), ("reason", reason));

    /// <summary><c>TechValidation.Analyze</c>'s finding, with its <c>TechProblemArea</c> kept as an
    /// argument rather than flattened into the sentence (R-aut4-4).</summary>
    public static Diagnostic CheckTechProblem(string path, string area, string message) => Diagnostic.Create(
        "check.tech.problem", DiagnosticSeverity.Warning,
        "{path}: {message}", ("path", path), ("area", area), ("message", message));

    /// <summary>Whatever <c>TechnologyResolver</c> or <c>EmSetupResolver</c> had to say. Both return
    /// diagnostics as strings and neither has typed values to hand, so the sentence is forwarded.</summary>
    public static Diagnostic CheckResolverNote(string path, string text) => Diagnostic.Create(
        "check.resolver.note", DiagnosticSeverity.Warning,
        "{path}: {text}", ("path", path), ("text", text));

    /// <summary>A layout that resolves no technology at all. A WARNING, not an error — see this
    /// region's own header for why.</summary>
    public static Diagnostic CheckNoTechnology(string path) => Diagnostic.Create(
        "check.technology.none", DiagnosticSeverity.Warning,
        "{path}: no technology resolves for this layout — its own reference names none and the "
        + "workspace states no default, so its layers have no names and no rules to check against.",
        ("path", path));

    /// <summary>A <c>.cem</c> that resolves no layout: <c>EmSetupResolver</c> returned no source.</summary>
    public static Diagnostic CheckEmUnresolved(string path, string reason) => Diagnostic.Create(
        "check.em.unresolved", DiagnosticSeverity.Error,
        "{path}: {reason}", ("path", path), ("reason", reason));

    /// <summary>A cell reference on a schematic that resolves to nothing. The three states
    /// <c>CellSymbolResolver</c> keeps distinct stay distinct: this is <c>NotFound</c>.</summary>
    public static Diagnostic CheckRefNotFound(string path, string component, string cellRef) =>
        Diagnostic.Create(
            "check.ref.not-found", DiagnosticSeverity.Error,
            "{path}: '{component}' references '{cellRef}', which resolves to no cell folder.",
            ("path", path), ("component", component), ("cellRef", cellRef));

    /// <summary><c>CellSymbolResolver</c>'s <c>PrimaryMissing</c> — the cell is there, its symbol is
    /// not.</summary>
    public static Diagnostic CheckRefPrimaryMissing(string path, string component, string cellRef) =>
        Diagnostic.Create(
            "check.ref.primary-missing", DiagnosticSeverity.Error,
            "{path}: '{component}' references '{cellRef}', which resolves to a cell with no primary symbol.",
            ("path", path), ("component", component), ("cellRef", cellRef));

    /// <summary>
    /// A kit part reference that did not resolve, which headlessly is usually not a broken reference
    /// at all: a kit lives in a REGISTRY the GUI populates when it opens a workspace, and nothing
    /// populates it here. Reported as a warning naming that, rather than as the missing-cell-folder
    /// error a path reference gets — the two have completely different repairs, and calling every
    /// kit part in a PDK design a missing cell is exactly the noise that makes a check stop being run.
    /// </summary>
    public static Diagnostic CheckKitRefUnresolved(string path, string component, string cellRef) =>
        Diagnostic.Create(
            "check.ref.kit-not-loaded", DiagnosticSeverity.Warning,
            "{path}: '{component}' references the kit part '{cellRef}', and no kit registry is "
            + "loaded headlessly — the reference was not checked.",
            ("path", path), ("component", component), ("cellRef", cellRef));

    /// <summary>A reference that only resolved through the workspace's own move record. Not a
    /// failure — the cell was found — but the stored spelling is stale and will not survive the
    /// record being pruned.</summary>
    public static Diagnostic CheckRefRedirected(string path, string cellRef, string movedTo) =>
        Diagnostic.Create(
            "check.ref.redirected", DiagnosticSeverity.Warning,
            "{path}: '{cellRef}' resolved only through a recorded move, to '{movedTo}'. "
            + "Re-save the document to write the current path.",
            ("path", path), ("cellRef", cellRef), ("movedTo", movedTo));

    /// <summary><c>NetExtractor</c>'s own non-fatal naming conflicts.</summary>
    public static Diagnostic CheckExtractionConflict(string path, string text) => Diagnostic.Create(
        "check.schematic.conflict", DiagnosticSeverity.Warning,
        "{path}: {text}", ("path", path), ("text", text));

    /// <summary>Elaboration threw. This is the one that answers "does it resolve?" for parameters,
    /// expressions and cycles — all three arrive here as the expression engine's own message.</summary>
    public static Diagnostic CheckElaborationFailed(string path, string message) => Diagnostic.Create(
        "check.elaboration.failed", DiagnosticSeverity.Error,
        "{path}: elaboration failed — {message}", ("path", path), ("message", message));

    public static Diagnostic CheckElaborationWarning(string path, string text) => Diagnostic.Create(
        "check.elaboration.warning", DiagnosticSeverity.Warning,
        "{path}: {text}", ("path", path), ("text", text));

    public static Diagnostic CheckElaborationNote(string path, string text) => Diagnostic.Create(
        "check.elaboration.note", DiagnosticSeverity.Info,
        "{path}: {text}", ("path", path), ("text", text));

    /// <summary>
    /// A WSProbe whose two terminals are the same net (brief-wsprobe-1 R-wsp1-12(b)). In the GUI
    /// the series-probe insertion cut makes this impossible for an IProbe; a hand-written
    /// <c>.cnl</c> has no such cut, and a probe across one net measures a loop of nothing — every
    /// output it reports is about a circuit that is not the one drawn.
    /// </summary>
    public static Diagnostic CheckWsProbeShorted(string path, string probe, string net) => Diagnostic.Create(
        "wsprobe.shorted", DiagnosticSeverity.Warning,
        "{path}: WSProbe '{probe}' has both terminals on net '{net}'. A WSProbe splits a node into a " +
        "G side and an L side; with one net on both it measures nothing. Give it two nets, G first.",
        ("path", path), ("probe", probe), ("net", net));

    /// <summary>
    /// No analysis of any kind will dispatch. <c>SelectTop</c>'s own sentence per kind, forwarded.
    /// A WARNING: a cell's schematic is not supposed to declare an analysis, and a `.cnl` written to
    /// be included by another is not either.
    /// </summary>
    public static Diagnostic CheckNoRunnableAnalysis(string path, string reasons) => Diagnostic.Create(
        "check.analysis.none", DiagnosticSeverity.Warning,
        "{path}: no analysis will dispatch. {reasons}", ("path", path), ("reasons", reasons));

    /// <summary>One DRC violation, at the severity the RULE states. The rule's own name, layer and
    /// measurement travel as arguments — a caller filtering on "every clearance violation" reads
    /// them, never the sentence.</summary>
    public static Diagnostic CheckDrcViolation(
        string path, string rule, string kind, DiagnosticSeverity severity,
        string? layer, string? measured, bool waived) => Diagnostic.Create(
        "check.drc.violation", severity,
        "{path}: {rule} ({kind}){onLayer}{measurement}{waiver}",
        ("path", path), ("rule", rule), ("kind", kind),
        ("layer", layer), ("measured", measured), ("waived", waived),
        // The three rendered fragments are separate arguments from the three TYPED ones above, and
        // named differently: a consumer reads `layer`, a reader reads `onLayer`. Reusing one name
        // for both would mean the typed value could not be read back without re-parsing the prose,
        // which is the exact thing R-aut4-4 exists to prevent.
        ("onLayer",     layer    is { Length: > 0 } ? $" on {layer}" : ""),
        ("measurement", measured is { Length: > 0 } ? $" — {measured}" : ""),
        ("waiver",      waived ? " (waived)" : ""));

    /// <summary>Anything the DRC run could not do — an unresolved instance, an unmapped
    /// cross-technology sub-cell, the flatten ceiling. Stated rather than dropped.</summary>
    public static Diagnostic CheckDrcNote(string path, string text) => Diagnostic.Create(
        "check.drc.note", DiagnosticSeverity.Warning,
        "{path}: {text}", ("path", path), ("text", text));

    /// <summary>A `.cws` entry — a library, a bookmarked file, the default technology — that names
    /// nothing. The GUI shows these as warning nodes in the project tree; this is the same finding.</summary>
    public static Diagnostic CheckWorkspaceRefUnresolved(string path, string what, string reference) =>
        Diagnostic.Create(
            "check.workspace.ref-unresolved", DiagnosticSeverity.Warning,
            "{path}: its {what} '{reference}' does not resolve.",
            ("path", path), ("what", what), ("reference", reference));

    /// <summary>The `.wasm` rule file would not read, or holds a rule its own parser rejects.</summary>
    public static Diagnostic CheckAssemblyRuleInvalid(string path, string rule, string message) =>
        Diagnostic.Create(
            "check.wasm.rule-invalid", DiagnosticSeverity.Error,
            "{path}: rule '{rule}' does not parse — {message}",
            ("path", path), ("rule", rule), ("message", message));

    // ── explain ──────────────────────────────────────────────────────────────
    //
    // R-aut4-8: `explain` never guesses and never falls back silently. Where resolution failed, the
    // failure IS the answer, and it names what was looked for and where it was looked — which is the
    // whole reason a caller reaches for this verb.

    public static Diagnostic ExplainPathRequired() => new(
        "explain.args.path-required", DiagnosticSeverity.Error,
        "explain: a path is required — a document, a cell folder or a workspace.");

    public static Diagnostic ExplainUnknownOption(string option) => Diagnostic.Create(
        "explain.args.unknown-option", DiagnosticSeverity.Error,
        "explain: unknown option '{option}'.", ("option", option));

    public static Diagnostic ExplainMultiplePaths() => new(
        "explain.args.multiple-paths", DiagnosticSeverity.Error,
        "explain: one path, please.");

    /// <summary>More than one of the SIX questions this verb answers. Refused rather than ordered:
    /// each asks something different and a document answering two of them at once would have to invent
    /// a precedence nobody stated. RND-3's three join the rule rather than getting an exception from it
    /// (R-rnd3-2).</summary>
    public static Diagnostic ExplainOneQuestion() => new(
        "explain.args.one-question", DiagnosticSeverity.Error,
        "explain: --expr, --analysis, --ref, --cells, --layers and --extents ask different " +
        "questions — pass one.");

    /// <summary><c>--all</c> is <c>--cells</c>' own modifier and means nothing beside anything else.
    /// Named rather than ignored, on §3.3's terms.</summary>
    public static Diagnostic ExplainAllNeedsCells() => new(
        "explain.args.all-needs-cells", DiagnosticSeverity.Error,
        "explain: --all includes generated cells in --cells, and applies to nothing else.");

    public static Diagnostic ExplainPathNotFound(string path) => Diagnostic.Create(
        "explain.path.not-found", DiagnosticSeverity.Error,
        "No such file or folder: {path}", ("path", path));

    public static Diagnostic ExplainUnknownKind(string path) => Diagnostic.Create(
        "explain.path.unknown-kind", DiagnosticSeverity.Error,
        "Nothing circuitRF reads is named '{path}'.", ("path", path));

    public static Diagnostic ExplainUnreadable(string path, string message) => Diagnostic.Create(
        "explain.file.unreadable", DiagnosticSeverity.Error,
        "Could not read '{path}': {message}", ("path", path), ("message", message));

    /// <summary>The question needs a netlist or a schematic and this document is neither. Named
    /// rather than answered emptily.</summary>
    public static Diagnostic ExplainNotApplicable(string option, string kind) => Diagnostic.Create(
        "explain.option.not-applicable", DiagnosticSeverity.Error,
        "{option} asks about analyses and expressions, which a {kind} does not hold.",
        ("option", option), ("kind", kind));

    /// <summary>
    /// The question is about geometry, drawing layers or cell folders and the path names none.
    ///
    /// <para>Separate from <see cref="ExplainNotApplicable"/> because the two name different reasons:
    /// that one is "a technology holds no analyses", this one is "a Touchstone file has no extents".
    /// One sentence covering both would have to say neither.</para>
    /// </summary>
    public static Diagnostic ExplainOptionNotApplicable(string option, string kind, string appliesTo) =>
        Diagnostic.Create(
            "explain.option.wrong-kind", DiagnosticSeverity.Error,
            "{option} does not apply to a {kind} — it asks about {appliesTo}.",
            ("option", option), ("kind", kind), ("appliesTo", appliesTo));

    /// <summary><c>--cells</c> or <c>--extents</c> on a cell folder whose views resolve to nothing.
    /// The folder IS the answer; there is simply no document under it to measure.</summary>
    public static Diagnostic ExplainCellHasNoView(string cellDir, string view) => Diagnostic.Create(
        "explain.cell.no-view", DiagnosticSeverity.Error,
        "'{cellDir}' holds no {view} view to measure.", ("cellDir", cellDir), ("view", view));

    /// <summary>A cell folder holding more than one view, asked a question about ONE of them.
    /// R-rnd0-6's rule: the dialog's own question becomes a refusal LISTING the choices and naming the
    /// flag that answers it.</summary>
    public static Diagnostic ExplainViewRequired(string cellDir, string views) => Diagnostic.Create(
        "explain.cell.view-required", DiagnosticSeverity.Error,
        "'{cellDir}' holds more than one view ({views}) — name one with --view.",
        ("cellDir", cellDir), ("views", views));

    /// <summary>A <c>--view</c> naming a view the cell folder does not hold.</summary>
    public static Diagnostic ExplainNoSuchView(string cellDir, string view) => Diagnostic.Create(
        "explain.cell.no-such-view", DiagnosticSeverity.Error,
        "'{cellDir}' has no {view} view.", ("cellDir", cellDir), ("view", view));

    /// <summary>A <c>--view</c> that is not one of the three view types at all.</summary>
    public static Diagnostic ExplainViewUnknown(string view) => Diagnostic.Create(
        "explain.args.view-unknown", DiagnosticSeverity.Error,
        "explain: --view takes schematic, symbol or layout — not '{view}'.", ("view", view));

    /// <summary>
    /// A hierarchical shape count that stopped before it finished.
    ///
    /// <para>Reported rather than absorbed for <c>sweep-unit-scale-and-mark</c>'s reason: a number
    /// that quietly stopped counting is indistinguishable from one that finished, and a caller would
    /// read a floor as a total.</para>
    /// </summary>
    public static Diagnostic ExplainLayerCountTruncated(string path) => Diagnostic.Create(
        "explain.layers.count-truncated", DiagnosticSeverity.Warning,
        "{path}: the hierarchy is larger than this count walks — the per-layer shape counts are a " +
        "floor, not a total.", ("path", path));

    /// <summary>The expression would not evaluate. The engine's own message — an unresolved name, a
    /// cycle, a parse error — which is the whole content of the answer.</summary>
    public static Diagnostic ExplainExpressionFailed(string expression, string message) =>
        Diagnostic.Create(
            "explain.expr.failed", DiagnosticSeverity.Error,
            "'{expression}' does not evaluate here: {message}",
            ("expression", expression), ("message", message));

    /// <summary>A relative reference that resolved to nothing. Names what was looked for and the
    /// directory it was looked in (R-aut4-8).</summary>
    public static Diagnostic ExplainRefNotFound(string reference, string from) => Diagnostic.Create(
        "explain.ref.not-found", DiagnosticSeverity.Error,
        "'{reference}' resolves to no cell folder from '{from}'.",
        ("reference", reference), ("from", from));

    public static Diagnostic ExplainRefPrimaryMissing(string reference, string resolved) =>
        Diagnostic.Create(
            "explain.ref.primary-missing", DiagnosticSeverity.Error,
            "'{reference}' resolves to '{resolved}', which has no primary symbol.",
            ("reference", reference), ("resolved", resolved));

    /// <summary>The netlist declares nothing this verb would dispatch. <c>SelectTop</c>'s own
    /// sentences, one per analysis kind, forwarded rather than re-authored.</summary>
    public static Diagnostic ExplainNoRunnableAnalysis(string reasons) => Diagnostic.Create(
        "explain.analysis.none", DiagnosticSeverity.Warning,
        "No analysis will dispatch. {reasons}", ("reasons", reasons));

    /// <summary>A named analysis that is not declared at all.</summary>
    public static Diagnostic ExplainAnalysisNotFound(string name, string declared) => Diagnostic.Create(
        "explain.analysis.not-found", DiagnosticSeverity.Error,
        "No analysis named '{name}'. Declared: {declared}", ("name", name), ("declared", declared));

    // ── read (brief-automation-5-protocol-adapter.md §3) ─────────────────────

    public static Diagnostic ReadPathRequired() => new(
        "read.args.path-required", DiagnosticSeverity.Error,
        "read: a file is required. Give a result file (.npy or a Touchstone .sNp) or one of " +
        "circuitRF's own documents (.cws .csch .csym .clay .ctech .cem .cnl).");

    public static Diagnostic ReadUnknownOption(string option) => Diagnostic.Create(
        "read.args.unknown-option", DiagnosticSeverity.Error,
        "read: unknown option '{option}'", ("option", option));

    public static Diagnostic ReadMultiplePaths() => new(
        "read.args.multiple-paths", DiagnosticSeverity.Error,
        "read: one file at a time.");

    public static Diagnostic ReadPathNotFound(string path) => Diagnostic.Create(
        "read.path.not-found", DiagnosticSeverity.Error,
        "File not found: {path}", ("path", path));

    /// <summary>A directory. Refused rather than walked — what a workspace holds is what `check`
    /// and `explain` answer, and walking one here would return an unbounded document.</summary>
    public static Diagnostic ReadPathIsAFolder(string path) => Diagnostic.Create(
        "read.path.is-a-folder", DiagnosticSeverity.Error,
        "read: '{path}' is a folder. read takes one file; use check or explain for a workspace or a cell.",
        ("path", path));

    public static Diagnostic ReadFileUnreadable(string path, string message) => Diagnostic.Create(
        "read.file.unreadable", DiagnosticSeverity.Error,
        "Cannot read '{path}': {message}", ("path", path), ("message", message));

    public static Diagnostic ReadUnsupported(string path, string extension) => Diagnostic.Create(
        "read.file.unsupported", DiagnosticSeverity.Error,
        "read: nothing in circuitRF reads '{extension}' ({path}).",
        ("path", path), ("extension", extension.Length == 0 ? "(no extension)" : extension));

    /// <summary>An interchange file. Named rather than refused blankly, and pointed at the verb that
    /// does read it: half of these formats are binary, and handing back a GDSII stream as a JSON
    /// string would be an encoding decision this verb has no business making.</summary>
    public static Diagnostic ReadInterchange(string path, string format) => Diagnostic.Create(
        "read.file.interchange", DiagnosticSeverity.Error,
        "read: '{path}' is {format}, which convert reads. read takes a result file or one of " +
        "circuitRF's own documents.",
        ("path", path), ("format", format));

    /// <summary>
    /// The one document kind of circuitRF's own that is BINARY (AUT-10 R-aut10-5). A compiled
    /// assembly-rule module read as text comes back as whatever the bytes decode to and reaches a
    /// caller as a plausible-looking string, which is the failure class this surface exists to
    /// remove — so it is a refusal that names what the file is.
    /// </summary>
    public static Diagnostic ReadBinaryDocument(string path, string kind) => Diagnostic.Create(
        "read.file.binary", DiagnosticSeverity.Error,
        "read: '{path}' is a {kind} module, which is compiled binary rather than text. read returns " +
        "a document's own bytes as text, and there is no text here to return.",
        ("path", path), ("kind", kind));

    // ── reference (brief-automation-6-reference-and-components.md) ───────────

    /// <summary>An unknown topic, LISTING the real ones — <c>--tech</c>'s precedent (AUT-3): never
    /// a fallback, because a fallback answers a different question than the one asked and says
    /// nothing about it.</summary>
    public static Diagnostic ReferenceUnknownTopic(string topic, string known) => Diagnostic.Create(
        "reference.topic.unknown", DiagnosticSeverity.Error,
        "No reference topic '{topic}'. Topics: {known}", ("topic", topic), ("known", known));

    /// <summary>A primitive nobody has heard of. The list is the whole catalogue, which is exactly
    /// the thing the caller was missing.</summary>
    public static Diagnostic ReferenceUnknownComponent(string type, string known) => Diagnostic.Create(
        "reference.component.unknown", DiagnosticSeverity.Error,
        "No primitive type '{type}'. Types: {known}", ("type", type), ("known", known));

    /// <summary>An <c>analysis type=</c> token nothing understands, listing every one that IS
    /// understood — the same shape as the reader's own refusal (AUT-8 R-aut8-2), because a caller
    /// that reaches this one is about to write the line the reader will refuse.</summary>
    public static Diagnostic ReferenceUnknownAnalysis(string type, string known) => Diagnostic.Create(
        "reference.analysis.unknown", DiagnosticSeverity.Error,
        "No analysis type '{type}'. Types: {known}", ("type", type), ("known", known));

    /// <summary>A second argument on a topic that does not have items. Only <c>components</c> and
    /// <c>analyses</c> do.</summary>
    public static Diagnostic ReferenceItemNotForTopic(string topic) => Diagnostic.Create(
        "reference.args.item-not-for-topic", DiagnosticSeverity.Error,
        "reference: '{topic}' is one page and names nothing inside it. Only 'components' and " +
        "'analyses' take a name.",
        ("topic", topic));

    /// <summary>A type named with no topic. Reachable from a tool call, where the two arguments are
    /// named rather than positional — refused rather than silently promoted to a topic, which would
    /// run a different query than the one asked.</summary>
    public static Diagnostic ReferenceItemWithoutTopic() => new(
        "reference.args.item-without-topic", DiagnosticSeverity.Error,
        "reference: a type was named with no topic. Ask for 'components' and the type.");

    public static Diagnostic ReferenceTooManyArguments() => new(
        "reference.args.too-many", DiagnosticSeverity.Error,
        "reference: at most a topic and one name. Usage: circuitrf reference [topic] [type]");

    /// <summary>A <c>resources/read</c> for a URI this server does not publish. Answered with a
    /// document naming the ones it does, not a protocol error frame — an unknown resource is the same
    /// mistake as an unknown topic and gets the same answer (R-aut-7).</summary>
    public static Diagnostic ReferenceUnknownResource(string uri, string known) => Diagnostic.Create(
        "reference.resource.unknown", DiagnosticSeverity.Error,
        "No reference resource '{uri}'. Resources: {known}", ("uri", uri), ("known", known));

    public static Diagnostic ReferenceUnknownOption(string option) => Diagnostic.Create(
        "reference.args.unknown-option", DiagnosticSeverity.Error,
        "reference: unknown option '{option}'", ("option", option));

    // ── serve (brief-automation-5-protocol-adapter.md) ───────────────────────

    /// <summary>No <c>--root</c>. Required at startup rather than defaulted: the server runs with
    /// the invoking user's authority, and a default of "the current directory" would be a policy
    /// nobody stated (R-aut5-8).</summary>
    public static Diagnostic ServeRootRequired() => new(
        "serve.args.root-required", DiagnosticSeverity.Error,
        "serve: --root <dir> is required. Every path a client names resolves under it, and there " +
        "is no default.");

    /// <summary><c>--json</c> on <c>serve</c>. It would take the stdout the protocol framing needs,
    /// and every tool call already returns a document.</summary>
    public static Diagnostic ServeJsonNotApplicable() => new(
        "serve.args.json-not-applicable", DiagnosticSeverity.Error,
        "serve: --json does not apply — stdout carries the protocol, and every tool call returns a " +
        "document of its own.");

    public static Diagnostic ServeRootNotFound(string path) => Diagnostic.Create(
        "serve.root.not-found", DiagnosticSeverity.Error,
        "serve: --root '{path}' is not a directory.", ("path", path));

    public static Diagnostic ServeUnknownOption(string option) => Diagnostic.Create(
        "serve.args.unknown-option", DiagnosticSeverity.Error,
        "serve: unknown option '{option}'", ("option", option));

    /// <summary>A path that leaves the root. Names the root, because that is the fact the caller is
    /// missing — and it is a refusal rather than a silent clamp, which would run a different
    /// operation than the one asked for and say nothing about it.</summary>
    public static Diagnostic ServePathOutsideRoot(string path, string root) => Diagnostic.Create(
        "serve.path.outside-root", DiagnosticSeverity.Error,
        "'{path}' is outside the server root '{root}'.", ("path", path), ("root", root));

    public static Diagnostic ServeUnknownTool(string tool, string known) => Diagnostic.Create(
        "serve.tool.unknown", DiagnosticSeverity.Error,
        "No tool named '{tool}'. Tools: {known}", ("tool", tool), ("known", known));

    /// <summary>A required tool argument that was not given. The adapter refuses rather than
    /// choosing for the client (R-aut-1).</summary>
    public static Diagnostic ServeArgumentRequired(string tool, string argument) => Diagnostic.Create(
        "serve.args.required", DiagnosticSeverity.Error,
        "{tool}: '{argument}' is required.", ("tool", tool), ("argument", argument));

    public static Diagnostic ServeArgumentUnknownValue(string tool, string argument, string value, string allowed) =>
        Diagnostic.Create(
            "serve.args.unknown-value", DiagnosticSeverity.Error,
            "{tool}: '{argument}' does not take '{value}'. One of: {allowed}",
            ("tool", tool), ("argument", argument), ("value", value), ("allowed", allowed));

    /// <summary>An argument that belongs to another mode of the same tool. Named as such, because
    /// "unknown option grid" is a worse answer than "grid is lp's, not lpp's".</summary>
    public static Diagnostic ServeArgumentNotForMode(string tool, string argument, string mode) =>
        Diagnostic.Create(
            "serve.args.not-for-mode", DiagnosticSeverity.Error,
            "{tool}: '{argument}' does not apply to {mode}.",
            ("tool", tool), ("argument", argument), ("mode", mode));

    public static Diagnostic ServeArgumentWrongType(string tool, string argument, string expected) =>
        Diagnostic.Create(
            "serve.args.wrong-type", DiagnosticSeverity.Error,
            "{tool}: '{argument}' expects {expected}.",
            ("tool", tool), ("argument", argument), ("expected", expected));

    /// <summary>The invocation threw. A server survives it and reports it; the alternative is a
    /// client whose connection simply ends.</summary>
    public static Diagnostic ServeToolFailed(string tool, string message) => Diagnostic.Create(
        "serve.tool.failed", DiagnosticSeverity.Error,
        "{tool} failed: {message}", ("tool", tool), ("message", message));

    /// <summary>The client cancelled. Its own exit code is 130, the one `em` already uses for a run
    /// stopped at a work boundary (cli.md §7).</summary>
    public static Diagnostic ServeCancelled(string tool) => Diagnostic.Create(
        "serve.tool.cancelled", DiagnosticSeverity.Error,
        "{tool} was cancelled.", ("tool", tool));

    /// <summary>
    /// The picture was drawn and written, and it is too large to hand back inside the tool result
    /// (R-rnd5-4).
    ///
    /// <para><b>It is a refusal and never a truncation, and never a silent omission.</b> Half a PNG
    /// is not a smaller PNG, and a client that asked for a picture and got nothing back with nothing
    /// said simply asks again — so the useful answer is the size it came to, the cap it passed, and
    /// the arguments that would bring it under. The FILE is still written and still named in
    /// <c>outputs</c>; only the attachment is withheld.</para>
    ///
    /// <para>The four named arguments are the ones that actually move the number, in the order they
    /// move it by: on a real board an SVG is 23.5 MB at <c>--detail full</c> and 6.2 MB at
    /// <c>--detail screen</c>, and a PNG of the same board is 1.4 MB because a raster's size is set
    /// by its pixels rather than by the geometry behind them (<c>src/Cli/RESOLVED.md</c>, RND-2).
    /// </para>
    /// </summary>
    public static Diagnostic ServeImageTooLarge(string path, long bytes, long cap) => Diagnostic.Create(
        "serve.image.too-large", DiagnosticSeverity.Warning,
        "'{path}' is {bytes} bytes, over the {cap}-byte attachment cap, so it is not attached — the " +
        "file is written and named in outputs. Narrow it with --detail screen, --layers, --window " +
        "or a smaller --size, or ask for .png rather than a vector format.",
        ("path", path), ("bytes", bytes), ("cap", cap));

    /// <summary>The run said it wrote a file and the adapter could not read it back. Reported rather
    /// than swallowed: the alternative is a result that silently carries no picture and looks exactly
    /// like one that was never asked for an attachment.</summary>
    public static Diagnostic ServeImageUnreadable(string path, string message) => Diagnostic.Create(
        "serve.image.unreadable", DiagnosticSeverity.Warning,
        "'{path}' could not be read back to attach: {message}", ("path", path), ("message", message));

    // ── history (RC-3) ────────────────────────────────────────────────────────────────────────────

    public static Diagnostic HistoryNounRequired() => new(
        "history.args.noun-required", DiagnosticSeverity.Error,
        "history: say what to do — 'history checkpoint <workspace>'.");

    public static Diagnostic HistoryUnknownNoun(string noun) => Diagnostic.Create(
        "history.args.unknown-noun", DiagnosticSeverity.Error,
        "history: there is nothing called '{noun}'. Known: checkpoint, list, restore, commit, "
      + "versions, clone, pins, pin, unpin, fetch, send.", ("noun", noun));

    /// <summary>RC-5 R-rc5-23. <c>restore</c> needs to be told WHICH one, and the answer is a
    /// sequence out of <c>history list</c> — never an object identity, which is the vocabulary this
    /// surface does not use.</summary>
    public static Diagnostic HistoryRestoreNeedsAPoint() => new(
        "history.restore.point-required", DiagnosticSeverity.Error,
        "history restore: say which restore point, as --point <number> from 'history list'.");

    public static Diagnostic HistoryNoSuchPoint(long sequence) => Diagnostic.Create(
        "history.restore.no-such-point", DiagnosticSeverity.Error,
        "This workspace has no restore point {sequence}. 'history list' shows the ones it has.",
        ("sequence", sequence));

    /// <summary>
    /// RC-10 R-rc10-21. <c>--kinds</c> was given something that is not one of the five the panel's own
    /// filter has. <b>Named, with the list</b>, rather than silently ignored: a caller who mistyped
    /// <c>save-point</c> would otherwise get a shorter list and no reason for it, which is the same
    /// wrong answer R-rc10-10 exists to prevent one panel over.
    /// </summary>
    public static Diagnostic HistoryUnknownKind(string kind) => Diagnostic.Create(
        "history.list.unknown-kind", DiagnosticSeverity.Error,
        "history list: there is nothing to show called '{kind}'. Known: versions, save-points, "
      + "ai-batches, automatic, tidied-away.",
        ("kind", kind));

    /// <summary>
    /// RC-10 R-rc10-10. <b>A search whose answer is incomplete says so.</b> Returning fewer results
    /// than exist would be a wrong answer rather than a short one, and the caller would have no way to
    /// tell the two apart.
    /// </summary>
    public static Diagnostic HistoryThinnedAlsoMatch(int count) => Diagnostic.Create(
        "history.list.thinned-also-match", DiagnosticSeverity.Info,
        "{count} entries that were tidied away also match. Add 'tidied-away' to --kinds to see them.",
        ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The list is empty. Not a failure — it is the ordinary state of a workspace no
    /// boundary has reached yet.</summary>
    public static Diagnostic HistoryNothingKeptYet(string path) => Diagnostic.Create(
        "history.list.empty", DiagnosticSeverity.Info,
        "'{path}' has no restore points yet.", ("path", path));

    public static Diagnostic HistoryNotAWorkspace(string path) => Diagnostic.Create(
        "history.input.not-a-workspace", DiagnosticSeverity.Error,
        "'{path}' is not a workspace folder — there is no .cws in it.", ("path", path));

    /// <summary>R-rc3-3 makes absence SILENT on every automatic path. This one is not automatic: the
    /// caller typed the command, so they are told rather than left with a no-op.</summary>
    public static Diagnostic HistoryNoGit() => new(
        "history.git.unavailable", DiagnosticSeverity.Error,
        "circuitRF could not find a git to use on this machine, so it cannot keep a history. "
      + "Install one, or name it in Settings ▸ Revision Control.");

    /// <summary>
    /// RC-5 R-rc5-15. <b>The dialog's question, as a refusal naming the flags that answer it</b> —
    /// the rule <c>new</c> and <c>import</c> already follow: anything the GUI would have ASKED is a
    /// refusal here, never a guess. §9A.1 decides why it is a refusal rather than a default:
    /// including a file is irreversible and leaving it out is not, and a headless run has nobody to
    /// undo the wrong answer.
    /// </summary>
    public static Diagnostic HistoryLargeFilesNeedAnAnswer(string names, int count) => Diagnostic.Create(
        "history.large-files.unanswered", DiagnosticSeverity.Error,
        "{count} file(s) here are much larger than a design document and have never been kept: "
      + "{names}. Say what they are: --include-large to keep them (one copy now, one more whenever "
      + "they change), or --leave-out <path> for each one to leave out. Nothing was recorded.",
        ("count", (object?)count), ("names", names));

    // ── the batch, on `serve` (RC-5 §5.3a, §5.3b) ─────────────────────────────────────────────────

    /// <summary>R-rc5-6g. The state, advertised rather than inferred — an agent left to deduce it
    /// from the filesystem will deduce it wrongly.</summary>
    public static Diagnostic BatchState(string state, bool open, string? intent, string root)
        => Diagnostic.Create(
            "batch.state", DiagnosticSeverity.Info,
            "This workspace keeps a history: {state}. A change is currently open: {open}{intent}. "
          + "Its folder is {root}.",
            ("state", state), ("open", open ? "yes" : "no"),
            ("intent", open && intent is { Length: > 0 } ? $" (\"{intent}\")" : ""),
            ("root", root));

    /// <summary>R-rc5-6h. One entry per rule, so a client that reads only sentences still gets them.</summary>
    public static Diagnostic BatchRule(int number, string text) => Diagnostic.Create(
        "batch.rule", DiagnosticSeverity.Info, "Rule {n}. {text}",
        ("n", number), ("text", text));

    /// <summary>
    /// R-rc5-6i. <b>Said with every refusal</b>, because the failure this pair exists to prevent is
    /// the HELPFUL one: an agent that, finding no mechanism available, makes its own arrangements and
    /// reports success — leaving the designer told they are protected by something they cannot go back
    /// through.
    /// </summary>
    public static Diagnostic BatchImproviseNothing() => new(
        "batch.improvise-nothing", DiagnosticSeverity.Warning,
        "Stop and say this to the designer. Do not commit anything of your own, do not copy the "
      + "folder, and do not arrange any other backup: circuitRF would not know about it and the "
      + "designer could not go back through it.");

    public static Diagnostic BatchClosed(int modified) => Diagnostic.Create(
        "batch.closed", DiagnosticSeverity.Info,
        "The change is closed. {n} file(s) in the workspace differ from how it was when it opened.",
        ("n", modified));

    /// <summary>R-rc5-6d. Not an error: an agent that died and restarted must be able to say so, and
    /// the restore point it needed was taken before any of that.</summary>
    public static Diagnostic BatchWasNotOpen() => new(
        "batch.was-not-open", DiagnosticSeverity.Info,
        "No change was open. Nothing needed closing.");

    // ── the narrative half (RC-7 R-rc7-22) ────────────────────────────────────────────────────────

    /// <summary>
    /// The list is empty. <b>Not a failure</b> — a workspace with restore points and no versions is
    /// the ordinary state of one nobody has deliberately kept a version of, which is most of them.
    /// </summary>
    public static Diagnostic HistoryNoVersionsYet(string path) => Diagnostic.Create(
        "history.versions.empty", DiagnosticSeverity.Info,
        "You have not kept a version of '{path}' yet.", ("path", path));

    /// <summary>
    /// R-rc7-4. <b>This surface names a version by its identity, and only this one.</b> The
    /// restore-point nouns beside it take an ordering number instead, because those are circuitRF's
    /// own and nobody outside it has an identifier for one.
    /// </summary>
    public static Diagnostic HistoryNoSuchVersion(string wanted) => Diagnostic.Create(
        "history.versions.no-such-version", DiagnosticSeverity.Error,
        "This workspace has no version starting {wanted}. 'history versions' shows the ones it has.",
        ("wanted", wanted));

    public static Diagnostic HistoryNoRepository(string path) => Diagnostic.Create(
        "history.repository.absent", DiagnosticSeverity.Error,
        "'{path}' is not keeping a history yet. Pass --create-repository to start one.",
        ("path", path));

    // ── clone and the pin (RC-9 R-rc9-20) ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>clone</c> takes two positions and neither can be guessed. <b>Not a default destination</b>:
    /// git would derive one from the address, and a folder appearing somewhere the caller did not name
    /// is the surprise §0 forbids — the more so on a build machine, where nobody is watching.
    /// </summary>
    public static Diagnostic CloneNeedsSourceAndDestination() => new(
        "history.clone.args", DiagnosticSeverity.Error,
        "history clone: say where to copy from and where to put it — "
      + "'history clone <address> <folder>'.");

    /// <summary>
    /// R-rc9-9. The pin is on the ALIAS, so an alias is what names one. There is deliberately no way
    /// to pin a cell: one referenced workspace is one repository with one identity, and a per-cell
    /// spelling would let one design reference two mutually inconsistent versions of one library.
    /// </summary>
    public static Diagnostic PinNeedsAnAlias() => new(
        "history.pin.alias-required", DiagnosticSeverity.Error,
        "history pin: say which referenced workspace, as --alias <name>. 'history pins <workspace>' "
      + "lists them. A pin is per referenced workspace, never per cell.");

    /// <summary>This workspace references nothing, so there is nothing to pin.</summary>
    public static Diagnostic NoWorkspaceReferencesHere(string path) => Diagnostic.Create(
        "history.pins.none", DiagnosticSeverity.Info,
        "'{path}' does not reference any other workspace.", ("path", path));

    // ── render (brief-render-2-render-verb.md) ────────────────────────────────────────────────────
    //
    // R-rnd0-6, unchanged from the authoring verbs: anything the GUI would have ASKED in a dialog is a
    // refusal naming the flag that answers it, never a default chosen on the caller's behalf. Two of
    // these are worth reading for the reason rather than the sentence:
    //
    //   * `render.viewport.unit-required` — R-rnd2-4. `--window 0,0,500,300` on a layout could mean
    //     DBU, micrometres or millimetres. Those are three pictures six orders of magnitude apart and
    //     all three are plausible, so the refusal prints what it WOULD have accepted rather than
    //     guessing. This is `sweep-unit-scale-and-mark`'s failure class, and it is the same shape of
    //     argument.
    //   * `render.layers.unknown` — R-rnd2-7. A misspelled layer that was silently skipped produces a
    //     picture without that layer, which a caller cannot tell apart from a layer that is genuinely
    //     empty. The refusal names the verb that answers the question instead.
    //
    // And one that is deliberately NOT a warning: `render.tech.unresolved` is Info. An orphan `.clay`
    // handed over by a converter has no workspace above it BY CONSTRUCTION and renders on the fallback
    // palette exactly as the layout editor does — a first-class input, not a degraded one (R-rnd2-1).

    public static Diagnostic RenderPathRequired() => new(
        "render.args.path-required", DiagnosticSeverity.Error,
        "render: a path is required — a .csch, .csym, .clay or .cdd, a cell folder, or a workspace "
      + "with --cell.");

    public static Diagnostic RenderUnknownOption(string option) => Diagnostic.Create(
        "render.args.unknown-option", DiagnosticSeverity.Error,
        "render: unknown option '{option}'.", ("option", option));

    public static Diagnostic RenderMultiplePaths() => new(
        "render.args.multiple-paths", DiagnosticSeverity.Error,
        "render: one path, please.");

    public static Diagnostic RenderOutputRequired() => new(
        "render.args.output-required", DiagnosticSeverity.Error,
        "render: -o <file.svg|.pdf|.png> is required. There is no picture on stdout — stdout carries "
      + "the result document, which has to be able to co-exist with the file that was written.");

    public static Diagnostic RenderPathNotFound(string path) => Diagnostic.Create(
        "render.path.not-found", DiagnosticSeverity.Error,
        "No such file or folder: {path}", ("path", path));

    /// <summary>A path circuitRF can read but cannot DRAW — a `.ctech`, a `.cnl`, a Touchstone. Named
    /// rather than called unreadable: `check` reads all of those and this verb does not draw them.</summary>
    public static Diagnostic RenderNotDrawable(string path, string kind) => Diagnostic.Create(
        "render.path.not-drawable", DiagnosticSeverity.Error,
        "'{path}' is {kind}, and render draws a schematic, a symbol or a layout. "
      + "`circuitrf check` reads it; `circuitrf convert` reads an interchange file.",
        ("path", path), ("kind", kind));

    public static Diagnostic RenderUnknownOutputFormat(string path, string extension) => Diagnostic.Create(
        "render.output.unknown-format", DiagnosticSeverity.Error,
        "render: '{path}' has extension '{extension}', which this verb does not write — it writes "
      + ".svg, .pdf and .png. Say --format svg|pdf|png to write one of those to that path anyway.",
        ("path", path), ("extension", extension));

    public static Diagnostic RenderUnknownFormatName(string text) => Diagnostic.Create(
        "render.args.unknown-format", DiagnosticSeverity.Error,
        "render: --format takes svg, pdf or png, got '{text}'.", ("text", text));

    /// <summary>R-rnd2-1 / R-rnd0-6: a cell folder holding more than one view is the dialog's own
    /// question, so it is a refusal that LISTS them and names the flag.</summary>
    public static Diagnostic RenderViewRequired(string path, string views) => Diagnostic.Create(
        "render.cell.view-required", DiagnosticSeverity.Error,
        "'{path}' holds {views}. Say which with --view schematic|symbol|layout.",
        ("path", path), ("views", views));

    public static Diagnostic RenderNoSuchView(string path, string view) => Diagnostic.Create(
        "render.cell.no-view", DiagnosticSeverity.Error,
        "'{path}' has no {view} view.", ("path", path), ("view", view));

    /// <summary>Primacy is <c>CellFolder.ResolvePrimary</c>'s answer and no other — a view sub-folder
    /// holding several files with none named primary is the same question, asked of the `.ccell`.</summary>
    public static Diagnostic RenderNoPrimary(string path, string view, string state) => Diagnostic.Create(
        "render.cell.no-primary", DiagnosticSeverity.Error,
        "'{path}': the {view} sub-folder does not resolve to one file ({state}). Name the file "
      + "directly, or record a primary in its .ccell.",
        ("path", path), ("view", view), ("state", state));

    public static Diagnostic RenderCellRequired(string path) => Diagnostic.Create(
        "render.workspace.cell-required", DiagnosticSeverity.Error,
        "'{path}' is a workspace. Say which cell with --cell <name> — rendering \"the workspace\" is "
      + "not a picture of anything.", ("path", path));

    public static Diagnostic RenderNoSuchCell(string workspace, string cell, string known) => Diagnostic.Create(
        "render.workspace.no-such-cell", DiagnosticSeverity.Error,
        "'{workspace}' has no cell called '{cell}'. It holds: {known}",
        ("workspace", workspace), ("cell", cell), ("known", known));

    public static Diagnostic RenderAmbiguousCell(string cell, string paths) => Diagnostic.Create(
        "render.workspace.ambiguous-cell", DiagnosticSeverity.Error,
        "More than one cell is called '{cell}': {paths}. Give the cell folder's path instead.",
        ("cell", cell), ("paths", paths));

    /// <summary>R-rnd2-3: the three viewport modes are refused TOGETHER rather than ordered, because a
    /// precedence nobody stated is an invention. <c>explain</c>'s own three questions do the same.</summary>
    public static Diagnostic RenderViewportModes(string modes) => Diagnostic.Create(
        "render.viewport.multiple-modes", DiagnosticSeverity.Error,
        "render: {modes} ask for different viewports. Pass one of --fit, --window or --center/--span.",
        ("modes", modes));

    public static Diagnostic RenderWindowMalformed(string text) => Diagnostic.Create(
        "render.viewport.malformed", DiagnosticSeverity.Error,
        "render: --window expects x0,y0,x1,y1, got '{text}'.", ("text", text));

    public static Diagnostic RenderCenterMalformed(string text) => Diagnostic.Create(
        "render.viewport.center-malformed", DiagnosticSeverity.Error,
        "render: --center expects x,y, got '{text}'.", ("text", text));

    public static Diagnostic RenderSpanRequired() => new(
        "render.viewport.span-required", DiagnosticSeverity.Error,
        "render: --center needs --span <width>, and --span needs --center <x,y>. The height follows "
      + "from the output's aspect.");

    /// <summary>R-rnd2-4, and the place the unit rule bites.</summary>
    public static Diagnostic RenderCoordinateNeedsUnit(string option, string text, string examples)
        => Diagnostic.Create(
            "render.viewport.unit-required", DiagnosticSeverity.Error,
            "render: {option} '{text}' is a bare number, and a layout coordinate carries a unit — "
          + "DBU, micrometres and millimetres differ by orders of magnitude on identical text. "
          + "Write it as {examples}.",
            ("option", option), ("text", text), ("examples", examples));

    public static Diagnostic RenderCoordinateMalformed(string option, string text) => Diagnostic.Create(
        "render.viewport.coordinate-malformed", DiagnosticSeverity.Error,
        "render: {option} '{text}' is not a length — write a number with a unit (nm, um, mm, mil, in).",
        ("option", option), ("text", text));

    public static Diagnostic RenderWindowEmpty(string text) => Diagnostic.Create(
        "render.viewport.empty", DiagnosticSeverity.Error,
        "render: --window '{text}' has no area.", ("text", text));

    public static Diagnostic RenderSizeMalformed(string text) => Diagnostic.Create(
        "render.size.malformed", DiagnosticSeverity.Error,
        "render: --size expects <width>x<height> in whole units, got '{text}'.", ("text", text));

    // ── render, a `.cdd` (RND-4) ─────────────────────────────────────────────
    //
    // R-rnd4-4's rule runs through all of these: an UNRESOLVED source is a refusal naming --data,
    // never an empty plot. An empty plot is the single most dangerous output in this brief — it is a
    // valid picture, it exports cleanly, and it looks exactly like a measurement that came back
    // empty.

    public static Diagnostic RenderPlotMalformed(string text) => Diagnostic.Create(
        "render.plot.malformed", DiagnosticSeverity.Error,
        "render: --plot expects a 1-based plot number, got '{text}'.", ("text", text));

    public static Diagnostic RenderCddUnreadable(string path, string reason) => Diagnostic.Create(
        "render.cdd.unreadable", DiagnosticSeverity.Error,
        "render: '{path}' is not a readable data display — {reason}.",
        ("path", path), ("reason", reason));

    public static Diagnostic RenderCddEmpty(string path) => Diagnostic.Create(
        "render.cdd.empty", DiagnosticSeverity.Error,
        "render: '{path}' holds no plots.", ("path", path));

    /// <summary>The sentinel with nothing bound to it. Names BOTH the sentinel and the flag, because
    /// a caller reading only the flag would not know which of its sources the document wanted.</summary>
    public static Diagnostic RenderCddSelectedUnbound(string sentinel) => Diagnostic.Create(
        "render.cdd.selected-unbound", DiagnosticSeverity.Error,
        "render: this display's traces read the selected dataset ('{sentinel}'), and headlessly there "
      + "is no selection to read. Name the file with --data <path>.", ("sentinel", sentinel));

    public static Diagnostic RenderCddSourceUnresolved(string sourceRef, string tried) => Diagnostic.Create(
        "render.cdd.source-unresolved", DiagnosticSeverity.Error,
        "render: this display reads '{sourceRef}', which is not here (looked at {tried}). Name it with "
      + "--data <path>.", ("sourceRef", sourceRef), ("tried", tried));

    /// <summary>
    /// A source the DOCUMENT names was found and could not be read (AUT-9 R-aut9-7).
    ///
    /// <para><b>Three different problems, three different sentences.</b> This used to be reported
    /// through <see cref="RenderCddUnreadable"/> — "'&lt;path&gt;' is not a readable data display" —
    /// with the RESULT file's path substituted into it. A caller reasonably concluded the
    /// <c>.cdd</c> it had just written was malformed and rewrote it, when the malformed file was the
    /// <c>.npy</c> the display points at. So this one names the source, says which document
    /// referenced it, and stays distinct from a bad <c>--data</c> argument
    /// (<see cref="RenderDataUnreadable"/>) and from a bad document
    /// (<see cref="RenderCddUnreadable"/>).</para>
    /// </summary>
    public static Diagnostic RenderCddSourceUnreadable(
        string sourceRef, string sourcePath, string document, string reason) => Diagnostic.Create(
        "render.cdd.source-unreadable", DiagnosticSeverity.Error,
        "render: '{document}' reads '{sourceRef}', and that file could not be read — {reason} "
      + "({sourcePath}). The data display itself is fine; name a readable result with --data <path>.",
        ("sourceRef", sourceRef), ("sourcePath", sourcePath),
        ("document", document), ("reason", reason));

    public static Diagnostic RenderDataNotFound(string path) => Diagnostic.Create(
        "render.data.not-found", DiagnosticSeverity.Error,
        "render: --data '{path}' does not exist.", ("path", path));

    public static Diagnostic RenderDataUnreadable(string path, string reason) => Diagnostic.Create(
        "render.data.unreadable", DiagnosticSeverity.Error,
        "render: --data '{path}' could not be read — {reason}.", ("path", path), ("reason", reason));

    /// <summary>R-rnd4-4's last clause: a <c>--data</c> that binds nothing is a refusal, not a shrug.</summary>
    public static Diagnostic RenderDataBindsNothing(string path, string referenced) => Diagnostic.Create(
        "render.data.binds-nothing", DiagnosticSeverity.Error,
        "render: --data '{path}' matches nothing this display reads. It reads {referenced}.",
        ("path", path), ("referenced", referenced));

    public static Diagnostic RenderNoSuchTab(string asked, string available) => Diagnostic.Create(
        "render.tab.not-found", DiagnosticSeverity.Error,
        "render: no tab '{asked}'. This display has {available}.", ("asked", asked), ("available", available));

    public static Diagnostic RenderNoSuchPlot(int asked, int count) => Diagnostic.Create(
        "render.plot.not-found", DiagnosticSeverity.Error,
        "render: --plot {asked} — this tab has {count} plot(s).", ("asked", asked), ("count", count));

    /// <summary>R-rnd4-5: multi-page is PDF's alone. SVG and PNG are single-page formats, and
    /// inventing out-1.svg, out-2.svg from one -o is a filename this tool made up (R-rnd0-6).</summary>
    public static Diagnostic RenderAllTabsNotMultiPage(string format) => Diagnostic.Create(
        "render.all-tabs.single-page", DiagnosticSeverity.Error,
        "render: --all-tabs writes one page per tab and {format} is a single-page format. Write a "
      + ".pdf, or pick one tab with --tab.", ("format", format));

    /// <summary>Both spellings of "which tab", refused together rather than ordered — R-rnd2-3's rule
    /// for the viewport modes, applied to the one other place this verb takes two answers to one
    /// question.</summary>
    public static Diagnostic RenderTabAndAllTabs() => Diagnostic.Create(
        "render.tab.conflict", DiagnosticSeverity.Error,
        "render: --tab names one tab and --all-tabs writes every one of them. Pass one or the other.");

    /// <summary>
    /// An option that means something for a drawing and nothing for a data display. Named rather
    /// than ignored: a caller that passed --window expecting a crop would otherwise get a full
    /// picture back with no hint that its flag did nothing.
    /// </summary>
    public static Diagnostic RenderCddViewportUnsupported(string option) => Diagnostic.Create(
        "render.cdd.not-applicable", DiagnosticSeverity.Error,
        "render: {option} describes a drawing — a viewport in world coordinates, a layer, a level of "
      + "detail — and a data display has none of those; its plots carry their own axis windows and "
      + "its page is laid out to fit them. Use --tab, --plot and --size.", ("option", option));

    /// <summary>
    /// A colour theme asked of a data display, whose colours do not come from a <c>.ccolor</c>.
    ///
    /// <para>Its own refusal rather than <see cref="RenderCddViewportUnsupported"/>'s because the
    /// reason is different and the remedy is different: the flag is not describing a drawing, it is
    /// naming a palette a plot does not read. A display's two palettes are the light and dark
    /// <c>RenderTheme</c>s, chosen by <c>--variant</c>.</para>
    /// </summary>
    public static Diagnostic RenderCddThemeUnsupported(string name) => Diagnostic.Create(
        "render.cdd.theme-not-applicable", DiagnosticSeverity.Error,
        "render: a data display draws on its own plot palette, not on a .ccolor theme, so --theme "
      + "'{name}' would have been ignored. Its two palettes are chosen with --variant light or "
      + "--variant dark.", ("name", name));

    public static Diagnostic RenderMarginMalformed(string text) => Diagnostic.Create(
        "render.margin.malformed", DiagnosticSeverity.Error,
        "render: --margin expects a fraction of the extent per side, between 0 and 0.45, got '{text}'.",
        ("text", text));

    /// <summary>A raster multiplier asked of a format that has no pixels to multiply.</summary>
    public static Diagnostic RenderScaleOnVector(string option, string format) => Diagnostic.Create(
        "render.scale.vector", DiagnosticSeverity.Error,
        "render: {option} is a raster multiplier and {format} has no pixels to multiply. Use --size "
      + "to change a vector page.", ("option", option), ("format", format));

    public static Diagnostic RenderScaleAndDpi() => new(
        "render.scale.conflict", DiagnosticSeverity.Error,
        "render: --scale and --dpi are two spellings of one number (--dpi is relative to 96). Pass one.");

    public static Diagnostic RenderScaleMalformed(string option, string text) => Diagnostic.Create(
        "render.scale.malformed", DiagnosticSeverity.Error,
        "render: {option} expects a positive number, got '{text}'.", ("option", option), ("text", text));

    public static Diagnostic RenderLayersNotApplicable(string option, string kind) => Diagnostic.Create(
        "render.layers.not-applicable", DiagnosticSeverity.Error,
        "render: {option} selects drawing layers, which a {kind} does not have.",
        ("option", option), ("kind", kind));

    public static Diagnostic RenderLayersConflict() => new(
        "render.layers.conflict", DiagnosticSeverity.Error,
        "render: --layers and --hide-layers are the two directions of one choice. Pass one.");

    /// <summary>R-rnd2-7. Not a silent skip, and it names the verb that answers the question.</summary>
    public static Diagnostic RenderUnknownLayer(string name, string known) => Diagnostic.Create(
        "render.layers.unknown", DiagnosticSeverity.Error,
        "The resolved technology defines no layer called '{name}'. It defines: {known}. "
      + "`circuitrf explain --layers` lists them for a document.",
        ("name", name), ("known", known));

    public static Diagnostic RenderNoTechnologyForLayers() => new(
        "render.layers.no-technology", DiagnosticSeverity.Error,
        "No technology resolved for this layout, so there are no layer names to select by. "
      + "`circuitrf explain` reports the walk that found none.");

    // ── AUT-12: layer colour, and framing on a subset ────────────────────────

    /// <summary>R-aut12-1. The entry did not read as <c>name=colour</c> at all — a missing <c>=</c>,
    /// or nothing on one side of it.</summary>
    public static Diagnostic RenderLayerColorMalformed(string text) => Diagnostic.Create(
        "render.layer-colors.malformed", DiagnosticSeverity.Error,
        "render: --layer-colors takes name=colour entries, got '{text}'. "
      + "For example --layer-colors \"Top Copper=#e04030,Bottom Copper=#3060e0\".",
        ("text", text));

    /// <summary>R-aut12-1. The name was a layer; the value was not a colour. Refused rather than
    /// left at the layer's own colour, which is a picture that looks exactly like the override
    /// having worked on a layer that already happened to be near that shade.</summary>
    public static Diagnostic RenderLayerColorBadValue(string name, string value) => Diagnostic.Create(
        "render.layer-colors.bad-color", DiagnosticSeverity.Error,
        "render: '{value}' is not a colour for layer '{name}'. Write #rgb, #rrggbb, or #rrggbbaa — "
      + "the eight-digit form sets the layer's fill opacity, which is the alpha the renderer actually "
      + "paints through.",
        ("name", name), ("value", value));

    /// <summary>R-aut12-2. <c>--fit-layers</c> chooses what a FIT frames on, and this render was
    /// given its frame outright. Named rather than ignored: a flag that silently did nothing is the
    /// same failure as a plausible wrong picture.</summary>
    public static Diagnostic RenderFitLayersNotFitting(string mode) => Diagnostic.Create(
        "render.fit-layers.not-fitting", DiagnosticSeverity.Error,
        "render: --fit-layers chooses what a fit is framed on, and {mode} states the frame outright. "
      + "Pass one.", ("mode", mode));

    /// <summary>R-aut12-2. Framing on nothing is not a page — and an empty picture is
    /// indistinguishable from a viewport that missed.</summary>
    public static Diagnostic RenderFitLayersEmpty(string names) => Diagnostic.Create(
        "render.fit-layers.empty", DiagnosticSeverity.Error,
        "render: nothing is drawn on {names}, so there is no box for --fit-layers to frame on. "
      + "`circuitrf explain --extents` reports which layers have geometry.", ("names", names));

    public static Diagnostic RenderDetailNotApplicable(string kind) => Diagnostic.Create(
        "render.detail.not-applicable", DiagnosticSeverity.Error,
        "render: --detail governs the layout renderer's level-of-detail tiers, which a {kind} does "
      + "not use.", ("kind", kind));

    public static Diagnostic RenderDetailMalformed(string text) => Diagnostic.Create(
        "render.detail.malformed", DiagnosticSeverity.Error,
        "render: --detail takes full, screen, or a pixel budget, got '{text}'.", ("text", text));

    /// <summary>R-rnd2-9. <c>ThemeResolver.Resolve</c> cannot fail — its last step is the built-in
    /// palette — so a name that resolves to NOTHING has to be detected before it, or a misspelling
    /// silently produces a differently-coloured picture reported as a success.</summary>
    public static Diagnostic RenderThemeUnresolved(string name, string lookedIn) => Diagnostic.Create(
        "render.theme.unresolved", DiagnosticSeverity.Error,
        "No theme called '{name}'. Looked in {lookedIn}.", ("name", name), ("lookedIn", lookedIn));

    public static Diagnostic RenderThemeFileUnreadable(string path, string why) => Diagnostic.Create(
        "render.theme.unreadable", DiagnosticSeverity.Error,
        "'{path}' is not a readable .ccolor: {why}", ("path", path), ("why", why));

    public static Diagnostic RenderUnknownVariant(string text) => Diagnostic.Create(
        "render.args.unknown-variant", DiagnosticSeverity.Error,
        "render: --variant takes light or dark, got '{text}'.", ("text", text));

    public static Diagnostic RenderUnknownBackground(string text) => Diagnostic.Create(
        "render.args.unknown-background", DiagnosticSeverity.Error,
        "render: --background takes opaque or transparent, got '{text}'.", ("text", text));

    public static Diagnostic RenderDocumentUnreadable(string path, string why) => Diagnostic.Create(
        "render.document.unreadable", DiagnosticSeverity.Error,
        "'{path}' could not be read: {why}", ("path", path), ("why", why));

    /// <summary>Nothing to frame. A refusal rather than an empty page: a caller that got a blank
    /// picture cannot tell "this document is empty" from "the viewport missed everything".</summary>
    public static Diagnostic RenderNothingToDraw(string path) => Diagnostic.Create(
        "render.document.empty", DiagnosticSeverity.Error,
        "'{path}' has nothing in it to draw.", ("path", path));

    public static Diagnostic RenderWriteFailed(string path, string why) => Diagnostic.Create(
        "render.output.write-failed", DiagnosticSeverity.Error,
        "Could not write '{path}': {why}", ("path", path), ("why", why));

    /// <summary>R-rnd2-1: a NOTE. An orphan document is a first-class input.</summary>
    public static Diagnostic RenderNoTechnology(string path) => Diagnostic.Create(
        "render.tech.unresolved", DiagnosticSeverity.Info,
        "No technology resolved for '{path}' — no workspace above it defines one, so it is drawn on "
      + "the fallback palette, exactly as the layout editor draws it.", ("path", path));

    public static Diagnostic RenderResolverNote(string path, string message) => Diagnostic.Create(
        "render.tech.note", DiagnosticSeverity.Info,
        "{path}: {message}", ("path", path), ("message", message));

    /// <summary>§7's 130. A cancelled run abandons its result rather than publishing a partial one —
    /// a half-written png that a caller reads as a finished one is the failure this prevents.</summary>
    public static Diagnostic RenderCancelled() => new(
        "render.cancelled", DiagnosticSeverity.Warning,
        "Cancelled. Nothing was written.");

    // ── netlist (brief-automation-11-missing-verbs.md R-aut11-1) ─────────────

    public static Diagnostic NetlistPathRequired() => new(
        "netlist.args.path-required", DiagnosticSeverity.Error,
        "netlist: a path is required. Give a .csch, a cell folder, or a workspace with --cell.");

    public static Diagnostic NetlistUnknownOption(string option) => Diagnostic.Create(
        "netlist.args.unknown-option", DiagnosticSeverity.Error,
        "netlist: unknown option '{option}'", ("option", option));

    public static Diagnostic NetlistMultiplePaths() => new(
        "netlist.args.multiple-paths", DiagnosticSeverity.Error,
        "netlist: one path at a time.");

    public static Diagnostic NetlistPathNotFound(string path) => Diagnostic.Create(
        "netlist.path.not-found", DiagnosticSeverity.Error,
        "No such file or directory: {path}", ("path", path));

    /// <summary>The extension picks the format everywhere else on this surface; here there is one
    /// format, so an extension that is not it is a caller who meant a different verb.</summary>
    public static Diagnostic NetlistOutputNotCnl(string path, string extension) => Diagnostic.Create(
        "netlist.output.not-cnl", DiagnosticSeverity.Error,
        "netlist: '{path}' has extension '{extension}', and this verb writes a .cnl. "
      + "`circuitrf render` writes a picture.",
        ("path", path), ("extension", extension));

    public static Diagnostic NetlistNotASchematic(string path, string kind) => Diagnostic.Create(
        "netlist.path.not-a-schematic", DiagnosticSeverity.Error,
        "'{path}' is {kind}, and netlist extracts a schematic — a .csch, a cell folder, or a "
      + "workspace with --cell.",
        ("path", path), ("kind", kind));

    /// <summary>Re-emitting a `.cnl` through the reader and the writer would hand back a file that
    /// is not the one given — comments gone, directives reordered — and call it an extraction.</summary>
    public static Diagnostic NetlistAlreadyANetlist(string path) => Diagnostic.Create(
        "netlist.path.already-a-netlist", DiagnosticSeverity.Error,
        "'{path}' is already a netlist. Run it directly, or `circuitrf read` it.", ("path", path));

    public static Diagnostic NetlistCellRequired(string path) => Diagnostic.Create(
        "netlist.workspace.cell-required", DiagnosticSeverity.Error,
        "'{path}' is a workspace. Say which cell with --cell <name> — a workspace has no one "
      + "netlist.", ("path", path));

    public static Diagnostic NetlistNoSuchCell(string workspace, string cell, string known) => Diagnostic.Create(
        "netlist.workspace.no-such-cell", DiagnosticSeverity.Error,
        "'{workspace}' has no cell called '{cell}'. It holds: {known}",
        ("workspace", workspace), ("cell", cell), ("known", known));

    public static Diagnostic NetlistAmbiguousCell(string cell, string paths) => Diagnostic.Create(
        "netlist.workspace.ambiguous-cell", DiagnosticSeverity.Error,
        "More than one cell is called '{cell}': {paths}. Give the cell folder's path instead.",
        ("cell", cell), ("paths", paths));

    public static Diagnostic NetlistNoSchematicView(string path) => Diagnostic.Create(
        "netlist.cell.no-schematic", DiagnosticSeverity.Error,
        "'{path}' has no schematic view, and a netlist comes out of a schematic.", ("path", path));

    /// <summary>Primacy is <c>CellFolder.ResolvePrimary</c>'s answer and no other.</summary>
    public static Diagnostic NetlistNoPrimary(string path, string state) => Diagnostic.Create(
        "netlist.cell.no-primary", DiagnosticSeverity.Error,
        "'{path}': the schematic sub-folder does not resolve to one file ({state}). Name the file "
      + "directly, or record a primary in its .ccell.",
        ("path", path), ("state", state));

    public static Diagnostic NetlistExtractionFailed(string path, string why) => Diagnostic.Create(
        "netlist.extract.failed", DiagnosticSeverity.Error,
        "'{path}' could not be extracted: {why}", ("path", path), ("why", why));

    public static Diagnostic NetlistWriteFailed(string path, string why) => Diagnostic.Create(
        "netlist.output.write-failed", DiagnosticSeverity.Error,
        "Could not write '{path}': {why}", ("path", path), ("why", why));

    /// <summary>
    /// R-aut11-1's refusal BY KIND. A run verb takes a netlist or a schematic; anything else used to
    /// be handed to <c>CnlReader</c>, which parsed a JSON layout as netlist text and reported its
    /// first key as a missing cell name — a message that sent a caller looking for a library.
    /// </summary>
    public static Diagnostic RunWrongDocumentKind(string verb, string path, string kind) => Diagnostic.Create(
        "cli.input.wrong-kind", DiagnosticSeverity.Error,
        "{verb}: '{path}' is {kind}. {verb} takes a .cnl netlist or a .csch schematic — extract one "
      + "with `circuitrf netlist`.",
        ("verb", verb), ("path", path), ("kind", kind));

    // ── plot (brief-automation-11-missing-verbs.md R-aut11-2) ────────────────

    public static Diagnostic PlotResultRequired() => new(
        "plot.args.result-required", DiagnosticSeverity.Error,
        "plot: a result file is required — a .npy or a Touchstone .sNp.");

    public static Diagnostic PlotOutputRequired() => new(
        "plot.args.output-required", DiagnosticSeverity.Error,
        "plot: -o is required. Its extension picks the format: .svg, .pdf or .png.");

    /// <summary>R-rnd4-4's rule at the one place this verb could break it: a plot with no trace is a
    /// valid picture that exports cleanly and looks exactly like a measurement that came back
    /// empty.</summary>
    public static Diagnostic PlotTraceRequired() => new(
        "plot.args.trace-required", DiagnosticSeverity.Error,
        "plot: at least one --trace is required. A spec is comma-separated key=value — "
      + "cube=S,i=2,j=1,y=db.");

    public static Diagnostic PlotUnknownOption(string option) => Diagnostic.Create(
        "plot.args.unknown-option", DiagnosticSeverity.Error,
        "plot: unknown option '{option}'", ("option", option));

    public static Diagnostic PlotMultipleResults() => new(
        "plot.args.multiple-results", DiagnosticSeverity.Error,
        "plot: one result file at a time. Bind a second one by writing a .cdd and calling render.");

    public static Diagnostic PlotResultNotFound(string path) => Diagnostic.Create(
        "plot.result.not-found", DiagnosticSeverity.Error,
        "No such file: {path}", ("path", path));

    public static Diagnostic PlotResultUnreadable(string path, string why) => Diagnostic.Create(
        "plot.result.unreadable", DiagnosticSeverity.Error,
        "'{path}' could not be read as a result: {why}", ("path", path), ("why", why));

    public static Diagnostic PlotUnknownOutputFormat(string path, string extension) => Diagnostic.Create(
        "plot.output.unknown-format", DiagnosticSeverity.Error,
        "plot: '{path}' has extension '{extension}', which this verb does not write — it writes "
      + ".svg, .pdf and .png. Say --format to override the extension.",
        ("path", path), ("extension", extension));

    public static Diagnostic PlotUnknownFormatName(string name) => Diagnostic.Create(
        "plot.output.unknown-format-name", DiagnosticSeverity.Error,
        "plot: --format '{name}' is not one of svg, pdf, png.", ("name", name));

    public static Diagnostic PlotUnknownType(string name) => Diagnostic.Create(
        "plot.type.unknown", DiagnosticSeverity.Error,
        "plot: --type '{name}' is not one of rect, smith, polar, table, surface.", ("name", name));

    public static Diagnostic PlotUnknownFreqUnit(string name) => Diagnostic.Create(
        "plot.frequnit.unknown", DiagnosticSeverity.Error,
        "plot: --freq-unit '{name}' is not one of Hz, kHz, MHz, GHz.", ("name", name));

    // ── ANT-7 §2/§3 — the dB radial mode and the pattern spellings ──────────────────────────────

    public static Diagnostic PlotUnknownRadial(string name) => Diagnostic.Create(
        "plot.radial.unknown", DiagnosticSeverity.Error,
        "plot: --radial '{name}' is not one of linear, db.", ("name", name));

    public static Diagnostic PlotRadialNeedsPolar(string type) => Diagnostic.Create(
        "plot.radial.needs-polar", DiagnosticSeverity.Error,
        "plot: --radial db is a property of a POLAR plot's radius, and --type is '{type}'. "
      + "A pattern CUT is drawn with --type polar --radial db; the 3D surface is --type surface, "
      + "whose radius is already dB above a floor and has no mode to set; a rectangular cut needs "
      + "neither.",
        ("type", type));

    public static Diagnostic PlotWholePlaneNeedsPattern(string type) => Diagnostic.Create(
        "plot.whole-plane.needs-pattern", DiagnosticSeverity.Error,
        "plot: --whole-plane draws both halves of a pattern CUT as one curve, which needs "
      + "--type polar --radial db; --type is '{type}'. On a linear polar plot the negative half of "
      + "an angle is not a place on the disc, and on a rectangular plot the whole plane is an "
      + "x-range rather than a second slice.",
        ("type", type));

    public static Diagnostic PlotAngleLabelsNeedPolar(string type) => Diagnostic.Create(
        "plot.angle-labels.needs-polar", DiagnosticSeverity.Error,
        "plot: --angle-labels prints the bearing around the rim of a POLAR disc, and --type is "
      + "'{type}'. It applies to either radial mode — a locus has a bearing too — but only to a "
      + "polar plot; a Smith chart carries its own angle scale already, and the 3D surface states "
      + "its orientation with the drawn axes in the scene.",
        ("type", type));

    // ── ANT-10 §2/§3 — the 3D pattern surface ───────────────────────────────────────────────────

    public static Diagnostic PlotUnknownView(string name) => Diagnostic.Create(
        "plot.view.unknown", DiagnosticSeverity.Error,
        "plot: --view '{name}' is not one of iso, broadside, phi0, phi90.", ("name", name));

    public static Diagnostic PlotViewNeedsSurface(string type) => Diagnostic.Create(
        "plot.view.needs-surface", DiagnosticSeverity.Error,
        "plot: {name} frames the 3D pattern surface and --type is '{type}'. A surface is drawn with "
      + "--type surface; a principal-plane CUT is --type polar --radial db, which is the view that "
      + "tells an engineer more about an antenna.",
        ("type", type), ("name", "--view/--rotate/--zoom"));

    public static Diagnostic PlotRotateMalformed(string value) => Diagnostic.Create(
        "plot.rotate.malformed", DiagnosticSeverity.Error,
        "plot: --rotate '{value}' is not 'azimuth,elevation' in degrees, as in --rotate 35,25.",
        ("value", value));

    public static Diagnostic PlotUnknownColorMap(string name, string all) => Diagnostic.Create(
        "plot.colormap.unknown", DiagnosticSeverity.Error,
        "plot: --color-map '{name}' is not one of {all}.", ("name", name), ("all", all));

    public static Diagnostic PlotZoomMalformed(string value) => Diagnostic.Create(
        "plot.zoom.malformed", DiagnosticSeverity.Error,
        "plot: --zoom '{value}' is not a positive number.", ("value", value));

    public static Diagnostic PlotDbOptionWithoutRadial(string options) => Diagnostic.Create(
        "plot.radial.db-option-without-radial", DiagnosticSeverity.Error,
        "plot: {options} set the dB pattern scale and this plot has none, so they would do nothing. "
      + "Add --radial db (a polar cut), or use --type surface (whose radius is dB already), or drop "
      + "them.", ("options", options));

    public static Diagnostic PlotDbFloorMalformed(string value) => Diagnostic.Create(
        "plot.radial.floor-malformed", DiagnosticSeverity.Error,
        "plot: --db-floor '{value}' is not a negative number of decibels. It is measured RELATIVE "
      + "TO THE OUTER RING, so -40 puts the centre 40 dB below it; a positive value would put the "
      + "centre above the rim.", ("value", value));

    public static Diagnostic PlotDbRingMalformed(string value) => Diagnostic.Create(
        "plot.radial.ring-malformed", DiagnosticSeverity.Error,
        "plot: --db-ring '{value}' is not a positive ring spacing in decibels (10 is the default).",
        ("value", value));

    public static Diagnostic PlotDbRefMalformed(string value) => Diagnostic.Create(
        "plot.radial.ref-malformed", DiagnosticSeverity.Error,
        "plot: --db-ref '{value}' is neither 'peak' nor a number. 'peak' normalises the outer ring "
      + "to the data's own maximum; a number puts it at that absolute level.", ("value", value));

    public static Diagnostic PlotCutNeedsPatternAxes(string trace, string cube, string axes) => Diagnostic.Create(
        "plot.trace.cut-needs-pattern-axes", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', cut= is a PATTERN cut and needs a cube with both a 'theta' and "
      + "a 'phi' axis. Cube '{cube}' has: {axes}. Write the slice instead — cube={cube}[...].",
        ("trace", trace), ("cube", cube), ("axes", axes));

    public static Diagnostic PlotCutMalformed(string trace, string value) => Diagnostic.Create(
        "plot.trace.cut-malformed", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', cut='{value}' is neither a phi in degrees nor 'all'.",
        ("trace", trace), ("value", value));

    public static Diagnostic PlotTraceFreqMalformed(string trace, string value) => Diagnostic.Create(
        "plot.trace.freq-malformed", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', freq='{value}' is not a frequency (2.45G and 2.45e9 both work).",
        ("trace", trace), ("value", value));

    public static Diagnostic PlotUnknownVariant(string name) => Diagnostic.Create(
        "plot.variant.unknown", DiagnosticSeverity.Error,
        "plot: --variant '{name}' is not light or dark.", ("name", name));

    public static Diagnostic PlotUnknownBackground(string name) => Diagnostic.Create(
        "plot.background.unknown", DiagnosticSeverity.Error,
        "plot: --background '{name}' is not opaque or transparent.", ("name", name));

    public static Diagnostic PlotSizeMalformed(string text) => Diagnostic.Create(
        "plot.size.malformed", DiagnosticSeverity.Error,
        "plot: --size '{text}' is not WxH within 8..20000.", ("text", text));

    public static Diagnostic PlotScaleMalformed(string option, string text) => Diagnostic.Create(
        "plot.scale.malformed", DiagnosticSeverity.Error,
        "plot: {option} '{text}' is not a positive number in range.", ("option", option), ("text", text));

    public static Diagnostic PlotScaleAndDpi() => new(
        "plot.scale.and-dpi", DiagnosticSeverity.Error,
        "plot: --scale and --dpi are two spellings of one multiplier. Give one.");

    public static Diagnostic PlotScaleOnVector(string option, string format) => Diagnostic.Create(
        "plot.scale.on-vector", DiagnosticSeverity.Error,
        "plot: {option} multiplies raster pixels and {format} is a vector format.",
        ("option", option), ("format", format));

    public static Diagnostic PlotRangeMalformed(string option, string text) => Diagnostic.Create(
        "plot.range.malformed", DiagnosticSeverity.Error,
        "plot: {option} '{text}' is not lo:hi with hi greater than lo.",
        ("option", option), ("text", text));

    /// <summary>A Smith or Polar chart's window is the complex plane framed on the unit circle;
    /// there is no X and no Y for a range to be a range of. Refused rather than dropped, for
    /// <c>render</c>'s reason.</summary>
    public static Diagnostic PlotWindowOnComplex(string type) => Diagnostic.Create(
        "plot.window.on-complex", DiagnosticSeverity.Error,
        "plot: --x/--y/--y2 do not apply to a {type} chart, which has no x and no y axis — a "
      + "smith or polar chart's window is the complex plane, and a surface's framing is its camera "
      + "(--view/--rotate/--zoom). Use --type rect, or leave them out.", ("type", type));

    public static Diagnostic PlotWriteFailed(string path, string why) => Diagnostic.Create(
        "plot.output.write-failed", DiagnosticSeverity.Error,
        "Could not write '{path}': {why}", ("path", path), ("why", why));

    // ── plot: one trace ──────────────────────────────────────────────────────

    public static Diagnostic PlotTraceFieldMalformed(string trace, string field) => Diagnostic.Create(
        "plot.trace.field-malformed", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', '{field}' is not key=value.", ("trace", trace), ("field", field));

    public static Diagnostic PlotTraceUnknownKey(string trace, string key) => Diagnostic.Create(
        "plot.trace.unknown-key", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', '{key}' is not one of cube, i, j, y, axis, probe, with, set, "
      + "metric, z0, side, gi, ref.",
        ("trace", trace), ("key", key));

    public static Diagnostic PlotTraceRefMalformed(string trace, string value) => Diagnostic.Create(
        "plot.trace.ref-malformed", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', ref='{value}' is not a number. It is the conducted power in "
      + "dBm that a dBm LEVEL — an antenna's TrpDbm or PeakEirpDbm — is read against, and it "
      + "replaces the reference the run itself published. The correction is an exact dB shift, so "
      + "it needs no re-run.",
        ("trace", trace), ("value", value));

    public static Diagnostic PlotTraceCubeRequired(string trace) => Diagnostic.Create(
        "plot.trace.cube-required", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' names no cube. Add cube=<name>; `circuitrf read` lists what the "
      + "result holds.", ("trace", trace));

    public static Diagnostic PlotTracePortMalformed(string trace, string key, string value) => Diagnostic.Create(
        "plot.trace.port-malformed", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', {key}='{value}' is not a port number from 1.",
        ("trace", trace), ("key", key), ("value", value));

    public static Diagnostic PlotTraceAxisUnknown(string trace, string value) => Diagnostic.Create(
        "plot.trace.axis-unknown", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', axis='{value}' is not left or right.",
        ("trace", trace), ("value", value));

    /// <summary>Two ways of saying which entry, given at once. Refused rather than ordered, because
    /// the one that would have lost is silently the caller's own slice.</summary>
    public static Diagnostic PlotTracePortsWithSlice(string trace) => Diagnostic.Create(
        "plot.trace.ports-with-slice", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' gives both a bracketed slice and i=/j=. Give one — i and j are the "
      + "convenience over writing the slice.", ("trace", trace));

    // ── WSProbe traces (WSP-4 R-wsp4-12) ─────────────────────────────────────

    public static Diagnostic PlotWspMetricUnknown(string trace, string metric, string known)
        => Diagnostic.Create(
            "plot.trace.wsp-metric-unknown", DiagnosticSeverity.Error,
            "plot: in --trace '{trace}', metric='{metric}' is not a WSProbe quantity. They are: {known}",
            ("trace", trace), ("metric", metric), ("known", known));

    /// <summary>A probe field on a cube that is not a wsp matrix. Refused rather than dropped: the
    /// picture would be of the cube named, which is not what was asked for.</summary>
    public static Diagnostic PlotWspNotAWspCube(string trace, string cube) => Diagnostic.Create(
        "plot.trace.wsp-not-a-wsp-cube", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' asks for a WSProbe quantity of '{cube}', which is not a wsp matrix "
      + "({{…, freq, row, col}}, square). A run with a WSProbe in it writes one as `<analysis>.wsp`.",
        ("trace", trace), ("cube", cube));

    /// <summary>Only a metric names a probe quantity; a probe without one would draw the raw
    /// matrix entry and look like an answer.</summary>
    public static Diagnostic PlotWspMetricRequired(string trace) => Diagnostic.Create(
        "plot.trace.wsp-metric-required", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' names a probe but no metric. Add metric=<name> — for example "
      + "metric=invY0, metric=SM_Y0 or metric=LGM.", ("trace", trace));

    /// <summary>The library's own sentence, forwarded — it names the run's probes.</summary>
    public static Diagnostic PlotWspUnresolved(string trace, string why) => Diagnostic.Create(
        "plot.trace.wsp-unresolved", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' does not resolve: {why}", ("trace", trace), ("why", why));

    public static Diagnostic PlotWspSideUnknown(string trace, string value) => Diagnostic.Create(
        "plot.trace.wsp-side-unknown", DiagnosticSeverity.Error,
        "plot: in --trace '{trace}', side='{value}' is not G or L.",
        ("trace", trace), ("value", value));

    public static Diagnostic PlotNoSuchCube(string cube, string known) => Diagnostic.Create(
        "plot.trace.no-such-cube", DiagnosticSeverity.Error,
        "No cube '{cube}' in the result. It holds: {known}", ("cube", cube), ("known", known));

    public static Diagnostic PlotNoPortAxis(string cube, string axis, string axes) => Diagnostic.Create(
        "plot.trace.no-port-axis", DiagnosticSeverity.Error,
        "Cube '{cube}' has no '{axis}' axis, so {axis}= means nothing on it. Its axes are: {axes}. "
      + "Write the slice instead — cube={cube}[...].",
        ("cube", cube), ("axis", axis), ("axes", axes));

    /// <summary>The parser's OWN sentence, forwarded rather than re-worded — it is the same one the
    /// trace card shows for the same text.</summary>
    public static Diagnostic PlotTraceUnresolved(string trace, string spec, string why) => Diagnostic.Create(
        "plot.trace.unresolved", DiagnosticSeverity.Error,
        "plot: --trace '{trace}' resolved to the spec '{spec}', which does not read: {why}",
        ("trace", trace), ("spec", spec), ("why", why));

    public static Diagnostic PlotCubeIsScalar(string cube) => Diagnostic.Create(
        "plot.trace.cube-is-scalar", DiagnosticSeverity.Error,
        "Cube '{cube}' is one number, and a curve needs a swept axis. A scalar belongs on a table.",
        ("cube", cube));

    // ── find (brief-automation-11-missing-verbs.md R-aut11-3) ────────────────

    public static Diagnostic FindRootRequired() => new(
        "find.args.root-required", DiagnosticSeverity.Error,
        "find: a root directory is required.");

    public static Diagnostic FindUnknownOption(string option) => Diagnostic.Create(
        "find.args.unknown-option", DiagnosticSeverity.Error,
        "find: unknown option '{option}'", ("option", option));

    public static Diagnostic FindMultipleRoots() => new(
        "find.args.multiple-roots", DiagnosticSeverity.Error,
        "find: one root at a time.");

    public static Diagnostic FindRootNotFound(string path) => Diagnostic.Create(
        "find.root.not-found", DiagnosticSeverity.Error,
        "No such directory: {path}", ("path", path));

    public static Diagnostic FindDepthMalformed(string text, int max) => Diagnostic.Create(
        "find.depth.malformed", DiagnosticSeverity.Error,
        "find: --depth '{text}' is not a whole number from 0 to {max}.",
        ("text", text), ("max", max.ToString()));

    /// <summary>A listing that quietly stopped short is read as "it is not here". Said out loud.</summary>
    public static Diagnostic FindTruncated(int depth) => Diagnostic.Create(
        "find.walk.truncated", DiagnosticSeverity.Warning,
        "The walk stopped at --depth {depth} with directories still below it. Raise --depth to see "
      + "further.", ("depth", depth.ToString()));

    public static Diagnostic FindNothingHere(string path, int depth) => Diagnostic.Create(
        "find.nothing", DiagnosticSeverity.Info,
        "No workspace under '{path}' within {depth} level(s). A workspace is a directory holding "
      + "a .cws; `circuitrf create` makes one.", ("path", path), ("depth", depth.ToString()));

    /// <summary>A cell folder that belongs to no workspace is a real state, and an empty answer
    /// would read as "there is nothing here". It is reported rather than invented into a workspace:
    /// a workspace decides the default technology, and attributing a cell to one it is not in is
    /// worse than not listing it.</summary>
    public static Diagnostic FindRootIsACell(string path) => Diagnostic.Create(
        "find.root.is-a-cell", DiagnosticSeverity.Info,
        "'{path}' is a cell folder and no workspace is above it. `circuitrf check` and "
      + "`circuitrf explain --cells` answer what is in one.", ("path", path));

    public static Diagnostic FindCellUnreadable(string path, string why) => Diagnostic.Create(
        "find.cell.unreadable", DiagnosticSeverity.Info,
        "'{path}': its analyses could not be read ({why}), so none are listed for it. "
      + "`circuitrf check` says why.", ("path", path), ("why", why));
}
