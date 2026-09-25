// R-em-18 — the Simulate path is RunSchematicDocAsync's five steps with a different middle.
// Background Task.Run, Messages for warnings first, then ResultsWriter.WriteRun →
// RefreshOpenDataDisplaysAsync → AutoOpenOrCreateDataDisplayAsync.
//
// **No new results plumbing and no new result type — and L8e did not add one either.** Whichever
// kernel runs, the DataSet carries S, per-port Z0, and ONE diagnostics group:
//
//   kernel A (cross-section) → "tline"   Zc, Gamma, Eeff, AttenDbPerM, Rpul, Lpul, Gpul, Cpul
//   kernel B (planar)        → "planar"  Gamma, Zc, Eeff, AttenDbPerM, Cpul, CalElectricalDeg,
//                                        DeembedResidual, DeembedRejected, DeembedErrorFloor,
//                                        CalibrationUsable, CalQuasiStatic
//
// The two groups are deliberately NOT the same name (L8e D4): a per-unit-length quantity from a 2-D
// quasi-static solve and one back-solved from a de-embedded full-wave S-matrix are different claims,
// and a plot that silently mixes them is the failure this separation exists to prevent. Everything
// after S is what makes a wrong answer diagnosable, so this file must not filter any of it out on
// the way to Data Display.
//
// Which kernel runs is EmKernelRegistry.Choose's answer, not this file's (L8e D2). This file runs
// both extractors, hands their verdicts to the registry, and reports the registry's reason.
//
// R-em-21 — no physics here. This file extracts, calls the kernel, writes files, and reports. Every
// number it touches came from the engine.

using CircuitRF.Design.Results;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;

using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Em;

/// <summary><see cref="Cancelled"/> means the user pressed Cancel: the run was abandoned at a work
/// boundary and NOTHING was written. It is deliberately distinct from <see cref="EngineError"/> —
/// a stopped run is a normal outcome and must not be reported as a failure.</summary>
public enum EmRunStatus { Ok, Refused, NoLayout, EngineError, Cancelled }

public sealed record EmRunResult(
    EmRunStatus           Status,
    DataSet?              Data,
    EmCrossSectionReadback? Readback,
    EmMeshReport?         MeshReport,
    string?               NpyPath,
    string?               SnpPath,
    string?               Error,
    /// <summary><b>Genuine warnings only</b> — something the user should act on, shown with the
    /// warning icon. Owner report, 2026-08-09: "a lot of the Messages after the EM sim have the
    /// yellow warning icon; change those to info." They were all coming out of this one list, which
    /// had become a grab-bag of the engine's own descriptive NOTES (which kernel ran and why, the
    /// mesh's own sentences, RLGC, ports, how many shapes came from instances), a couple of real
    /// warnings, and outright write FAILURES. A channel that says "warning" about everything teaches
    /// people to ignore it, which costs exactly the ones that matter. Three lists now, by what the
    /// reader is expected to DO about each.</summary>
    IReadOnlyList<string> Warnings,
    /// <summary>Which kernel actually ran (or was refused) — never
    /// <see cref="EmAnalysisKind.Auto"/>, which is a request rather than an outcome.</summary>
    EmAnalysisKind        Kind           = EmAnalysisKind.CrossSection,
    string                KernelName     = "",
    PlanarMeshReport?     PlanarMesh     = null,
    PlanarSolveResult?    PlanarSolve    = null,
    /// <summary>The engine's own descriptive output — shown with the info icon. Not a problem, and
    /// not something to act on: it is the run explaining itself.</summary>
    IReadOnlyList<string>? Notes = null,
    /// <summary>Things that genuinely failed while the run itself succeeded — a results file that
    /// could not be written. Shown with the error icon, because the user asked for a file and did
    /// not get one.</summary>
    IReadOnlyList<string>? Errors = null,
    /// <summary>D5's heat map, when a planar run produced one — port 1 at the lowest swept
    /// frequency unless the setup asked for another.</summary>
    PlanarCurrentDensityMap? CurrentDensity = null,
    /// <summary>The resolved ports, so the layout can draw the de-embedding reference planes over
    /// the location the ENGINE reports rather than one the Ui re-derives (§10.6).</summary>
    IReadOnlyList<PlanarPortResolution>? PlanarPorts = null,
    /// <summary>
    /// The coded form of <see cref="Error"/> — an id plus typed arguments plus the English template
    /// (brief-localization-groundwork.md R-loc-5). Non-null for every non-Ok status.
    ///
    /// <para><b><see cref="Error"/> is not deprecated by this and is not going away.</b> It is
    /// exactly <c>Diagnostic.Render()</c>, kept as a plain string because that is what the CLI
    /// writes to stderr — permanently, in English, regardless of any future language setting, so
    /// that nobody's grep or CI job breaks. The two are redundant on purpose: the string is the
    /// contract, the diagnostic is the structure. See docs/design/cli.md §8.</para>
    ///
    /// <para>What the id buys the Messages window is what a sentence cannot give it: grouping and
    /// filtering by kind, dedup of a refusal repeated at every sweep point, and a place to hang an
    /// action.</para>
    /// </summary>
    Diagnostic? Diagnostic = null,
    /// <summary>
    /// Every file the run wrote, when it wrote more than one Touchstone — a 3D setup run through
    /// both solvers (brief-em3d-10 R-em3d10-5): each solver's <c>.sNp</c> and <c>.npy</c>, and the
    /// comparison. Null otherwise, and then <see cref="SnpPath"/> and <see cref="NpyPath"/> are the
    /// whole list. Carried on a non-Ok status too: a result one solver produced is kept and listed
    /// when the other fails or is cancelled (R-em3d10-4).
    /// </summary>
    IReadOnlyList<EmRunOutput>? Outputs = null);

/// <summary>One file a run wrote: what it is (<c>touchstone</c>, <c>npy</c>) and where.</summary>
public sealed record EmRunOutput(string Kind, string Path);

/// <summary>
/// <b>What <see cref="EmRunService.Preflight"/> found</b> — the extract-and-mesh phase of a run,
/// with nothing solved and nothing written (EM-SEV R-emsev-5).
///
/// <para>There is no <c>Status</c> here and deliberately so: the only two outcomes this phase has
/// are "it would run, here is what it would solve" and "it was refused, here is why", and a
/// <see cref="Refusal"/> that is null or not says which. Everything else a run reports — a file that
/// could not be written, a cancelled sweep — belongs to the half this does not perform.</para>
/// </summary>
/// <param name="Kind">Which kernel the registry chose, never <see cref="EmAnalysisKind.Auto"/>.</param>
/// <param name="KernelName">Its name, for a report that has to say which one answered.</param>
/// <param name="Findings">Every sentence produced, in report order, each carrying its class.</param>
/// <param name="Refusal">Null when this setup would run; otherwise the sentence saying why not,
/// exactly as the run itself would word it.</param>
/// <param name="PlanarMesh">The mesh that was built, when the planar kernel was chosen and got that
/// far — so a caller can report the unknown count it would have solved.</param>
public sealed record EmPreflightResult(
    EmAnalysisKind           Kind,
    string                   KernelName,
    IReadOnlyList<EmFinding> Findings,
    string?                  Refusal = null,
    PlanarMeshReport?        PlanarMesh = null)
{
    public bool Ok => Refusal is null;

    /// <summary>The findings that say the answer would not be what was drawn.</summary>
    public IReadOnlyList<string> Warnings => EmFindings.WarningTexts(Findings);

    /// <summary>The findings that are the run explaining itself.</summary>
    public IReadOnlyList<string> Notes => EmFindings.NoteTexts(Findings);
}

/// <summary>
/// Headless. Everything the Simulate button does that is not dispatcher work, so it is testable
/// without a document, a canvas or a workspace — the same rule R-em-1 puts on the extractor.
/// </summary>
public static class EmRunService
{
    // ── EM-SEV R-emsev-5 — THE EXTRACT-AND-MESH PHASE, AS A THING A CALLER CAN ASK FOR ────────
    //
    // Every finding in a run's report is produced BEFORE the first frequency point is solved. On the
    // design that prompted this brief the extraction and mesh completed in a second or two and the
    // solve took eleven minutes — so "did this run keep my whole circuit?" is answerable in seconds,
    // and until now the only way to ask was to run the eleven minutes.
    //
    // It is FACTORED OUT of RunCore rather than written beside it. A second copy of this sequence
    // would be a second account of which extractor is chosen and what each one is handed, and
    // nothing would report the drift — which is the same rule `circuitrf check` itself is built on
    // (R-aut4-2: a validator that exists only in the checker is a rule the application does not
    // enforce).

    /// <summary>What <see cref="Extract"/> found: both extractors, the registry's choice, and every
    /// finding produced up to that point, in report order.</summary>
    private readonly record struct EmExtractPhase(
        EmGeometry.Result       Geometry,
        EmExtractionResult      CrossSection,
        PlanarExtractionResult  Planar,
        EmKernelChoice          Choice,
        List<EmFinding>         Findings,
        Diagnostic?             EarlyRefusal);

    /// <summary>
    /// Flatten, run BOTH extractors, and let the registry choose — the first half of every run.
    ///
    /// <para>Both extractors run on every launch because the registry needs BOTH verdicts to word
    /// either outcome (R-res-1): an explicit cross-section setup that gets refused has to be told
    /// that the planar kernel accepts the geometry, and an explicit planar one has to be told when
    /// the cheap kernel would have done. Extraction is geometry-only and costs nothing next to a
    /// solve; this is not the expensive half.</para>
    ///
    /// <para>The geometry is flattened exactly as the editor's own Refresh does — the run and the
    /// panel must never disagree about what geometry the setup is pointed at.</para>
    /// </summary>
    private static EmExtractPhase Extract(EmSetup setup, EmLayoutSource source, double fMax)
    {
        var findings = new List<EmFinding>();

        // Flattened, then clipped to the setup's solve region when it has one — the one door every
        // path to an extractor goes through (EmGeometry.ForSetup).
        var geometry = EmGeometry.ForSetup(setup, source);
        findings.AddRange(EmFindings.AsNotes(geometry.Notes));

        // A solve region that leaves a port outside it (or encloses nothing) is refused BEFORE either
        // extractor runs: the question is about what the user drew, an extractor's refusal of
        // clipped geometry would describe the symptom rather than the cause, and extracting a whole
        // board only to refuse it is the cost the region exists to avoid.
        if (geometry.RegionRefusal is { } regionRefusal)
        {
            var noCross  = EmExtractionResult.No(regionRefusal);
            var noPlanar = PlanarExtractionResult.No(regionRefusal);
            var refused  = EmKernelRegistry.Choose(setup.AnalysisKind,
                EmExtractorVerdict.No(regionRefusal), EmExtractorVerdict.No(regionRefusal));
            return new EmExtractPhase(geometry, noCross, noPlanar, refused, findings,
                                      EmDiagnostics.Forwarded("solve-region", regionRefusal));
        }

        var crossSection = CrossSectionExtractor.Extract(
            geometry.Shapes, source.Technology!, source.DbuPerMicron,
            setup.ToExtractionSettings(setup.LayoutRef));

        var planar = PlanarExtractor.Extract(
            geometry.Shapes, source.Technology!, source.DbuPerMicron, fMax,
            setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);

        var choice = EmKernelRegistry.Choose(
            setup.AnalysisKind,
            crossSection.Ok ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(crossSection.Refusal ?? ""),
            planar.Ok       ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(planar.Refusal ?? ""));

        // ── AN INTERNAL DELTA GAP IS A FULL-WAVE PORT, AND Auto WOULD SILENTLY DROP IT ───────────
        //
        // A uniform line carrying an interior gap is still a uniform CROSS-SECTION, so kernel A
        // accepts it and Auto prefers A whenever A accepts. Kernel A never meshes the plane — its two
        // ports are the ends of the extracted line by construction — so there is nowhere for the gap
        // to be and nothing that would report its absence: the run would publish a complete,
        // plausible s-matrix for the line WITHOUT the port the user asked for.
        //
        // Refused by name rather than silently re-routed to the planar kernel. Re-routing would be a
        // guess at intent that costs minutes of solve time, and the remedy is one dropdown.
        if (choice.Ok && choice.Kind == EmAnalysisKind.CrossSection
            && EmPortExtraction.AnyNonEdgePort(source.View.Shapes))
            return new EmExtractPhase(geometry, crossSection, planar, choice, findings,
                                      EmDiagnostics.InternalPortNeedsFullWave(choice.KernelName));

        findings.Add(choice.Reason);

        // The CHOSEN extractor's findings, whichever way it went and whether or not it accepted —
        // the "N shapes were ignored" lines are as useful next to a refusal as next to an answer.
        findings.AddRange(choice.Kind == EmAnalysisKind.Planar
                              ? planar.Findings
                              : EmFindings.AsNotes(crossSection.Notes));

        return new EmExtractPhase(geometry, crossSection, planar, choice, findings, null);
    }

    /// <summary>
    /// <b>The extract-and-mesh phase alone: what this setup would solve, and everything wrong with
    /// it that is knowable before a matrix is filled</b> (EM-SEV R-emsev-5).
    ///
    /// <para><b>It never solves and it writes nothing</b>, so it runs on a read-only tree, on a
    /// workspace another process has open, and in the seconds a build machine can spare. That is the
    /// whole of its value: the three findings that told the user their capacitor had been removed
    /// from the run were all produced here, minutes before the answer that hid them.</para>
    ///
    /// <para>It owns no analysis of its own — every finding comes from <see cref="EmGeometry"/>,
    /// the two extractors, <see cref="SurfaceMesher"/> and
    /// <see cref="PlanarSolve.LevelSeparationNotes"/>, which is what the GUI's own Simulate calls.
    /// </para>
    /// </summary>
    /// <param name="control">Cancellation for the mesh, which on a large board is the one part of
    /// this that takes noticeable time. Progress is reported through it as a run's would be.</param>
    public static EmPreflightResult Preflight(
        EmSetup setup, EmLayoutSource? source, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(setup);

        if (source is null)
            return new EmPreflightResult(EmAnalysisKind.CrossSection, "", [],
                                         EmDiagnostics.NoLayout(setup.LayoutRef).Render());
        if (source.Technology is null)
            return new EmPreflightResult(EmAnalysisKind.CrossSection, "", [],
                                         EmDiagnostics.NoTechnology(setup.LayoutRef).Render());
        if (setup.Is3D)
            return new EmPreflightResult(EmAnalysisKind.CrossSection, "", [],
                                         EmDiagnostics.ThreeDSolverNotBuilt(setup.Solver3D.ToString()).Render());

        var findings = new List<EmFinding>();

        // ── THE SAME PORT-TYPE MIGRATION THE RUN APPLIES, FOR THE SAME REASON ────────────────────
        //
        // In memory, writing nothing — which is exactly what RunCore does with it, so "this writes
        // nothing" is no reason to skip it. Skipping it was the drift R-aut4-2 exists to prevent: on
        // a legacy .cem the type lives in the SETUP and the labels are silent, so without this
        // `AnyNonEdgePort` below reads false, the internal-delta-gap refusal the run produces is not
        // reported, and `check` passes a setup the run refuses.
        if (EmPortKindMigration.ApplyInMemory(source.View.Shapes, setup.PortKinds) is > 0 and var moved)
            findings.Add($"{moved} port type(s) in this EM setup were carried onto the layout's own " +
                         "port labels, which is where a port's type lives. Open the layout to see " +
                         "them; saving it makes the move permanent.");

        double fMax = 0;
        double[] freqs = [];
        try { freqs = setup.Frequency.Expand(); }
        catch (Exception ex)
        {
            return new EmPreflightResult(EmAnalysisKind.CrossSection, "", [],
                                         EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render());
        }
        if (freqs.Length == 0)
            return new EmPreflightResult(EmAnalysisKind.CrossSection, "", [],
                                         EmDiagnostics.FrequencySweepEmpty().Render());
        foreach (double f in freqs) fMax = Math.Max(fMax, f);

        var pre = Extract(setup, source, fMax);
        findings.AddRange(pre.Findings);

        if (pre.EarlyRefusal is { } internalPort)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         internalPort.Render());

        if (!pre.Choice.Ok)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         pre.Choice.Refusal);

        // Kernel A has no mesh to build ahead of its solve — its two ports are the ends of the
        // extracted line by construction — so the extraction IS the whole of its preflight.
        if (pre.Choice.Kind != EmAnalysisKind.Planar || pre.Planar.Problem is not { } problem)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings, null);

        var kernel  = new PlanarKernel();
        var previewFmt = EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron);

        // The ports, then the ground paths they grow — the same order and the same arguments
        // RunPlanar uses, INCLUDING its reason for asking them before the kernel's verdict. A
        // preview of the bare extraction would be a preview of a structure the run does not solve,
        // and the port's own footprint would be missing from the mesh.
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));

        findings.AddRange(EmFindings.AsNotes(ports.Notes));
        if (!ports.Ok)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         ports.Refusal);

        var verdict = kernel.CanSolve(problem, previewFmt);
        if (!verdict.Ok)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         verdict.Reason);

        var meshed = ports.Ports.Count > 0
            ? PlanarGroundPath.Extend(problem, ports.Ports).Problem
            : problem;

        PlanarMeshReport report;
        try
        {
            report = SurfaceMesher.Mesh(
                meshed, setup.PlanarMesh, PlanarEdgeReference.LocalConductorWidth, control,
                accelerated: SurfaceMesher.UsesAcceleratedCeiling(
                    setup.AcceleratedSolve, meshed.RequiresGeneralKernel),
                lengthFormat: EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron),
                ports: ports.Ports);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         EmDiagnostics.SolveFailed(ex.Message).Render());
        }

        // MIM-13 item 3 — AllFindings, not AsNotes(Notes): the mesher classes its own sentences
        // now, and flattening them here is what put a warning at the weight of the core count.
        findings.AddRange(report.AllFindings);

        // MIM-3/MIM-8's question, which is a property of the MESH against the STACKUP and needs no
        // solve to answer — and which, past the measured bound, is EM-SEV R-emsev-4's warning.
        var lengthFmt = EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron);
        findings.AddRange(PlanarSolve.LevelSeparationNotes(meshed, report.Mesh, fMax, lengthFmt));

        // MIM-9 item 3 — and past the FULL-WAVE floor it is R-emsev-4's REFUSAL, which this has to
        // report for the same reason it reports every other one: a preflight that passed a setup the
        // run refuses is the drift R-aut4-2 exists to prevent. Asked after the notes so the scale
        // sentence is in `findings` beside it.
        if (PlanarSolve.LevelSeparationVerdict(meshed, report.Mesh, lengthFmt) is { Ok: false } thin)
            return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings,
                                         thin.Reason, report);

        return new EmPreflightResult(pre.Choice.Kind, pre.Choice.KernelName, findings, null,
                                     report);
    }

    /// <summary>
    /// <b>EM-SEV R-emsev-1 — the one place a finding's class decides which list it goes out on.</b>
    ///
    /// <para>The three lists on <see cref="EmRunResult"/> already asked the right question — what is
    /// the reader expected to DO about this? — and had no way to answer it except by where a call
    /// site chose to append. So every sentence an extractor or a sweep produced went out as a note,
    /// including the ones saying that part of the drawn structure is not in the answer. The class
    /// travels with the sentence now, and this is where it is read.</para>
    ///
    /// <para>Order within each list is preserved, which matters: the producers write in a deliberate
    /// order (the crossing note before the warning it qualifies, the mesh's own sentences in mesh
    /// order) and interleaving or sorting them would break sentences that refer to each other.</para>
    /// </summary>
    private static void AddFindings(
        IEnumerable<EmFinding> findings, List<string> notes, List<string> warnings)
    {
        foreach (var f in findings)
            (f.IsWarning ? warnings : notes).Add(f.Text);
    }

    /// <summary>
    /// R-em-19: the <c>.snp</c> lands at a PREDICTABLE path derived from the layout and setup names,
    /// mirroring <see cref="ResultsWriter"/>'s own convention, so a schematic's SnP reference is stable
    /// across runs. A run must never mint a new filename, or every re-run would orphan the reference.
    /// </summary>
    public static string ResolveSnpPath(string resultsRoot, EmSetup setup, int ports)
        => ResolveSnpBasePath(resultsRoot, setup) + $".s{ports}p";

    /// <summary>The path WITHOUT the <c>.sNp</c> suffix — what <c>TouchstoneExporter.Export</c>
    /// takes, since it appends the extension itself from the port count it finds in the cube.</summary>
    public static string ResolveSnpBasePath(string resultsRoot, EmSetup setup)
    {
        if (setup.SnpOutputPathOverride is { Length: > 0 } o)
        {
            string p = Path.IsPathRooted(o) ? o : Path.Combine(resultsRoot, o);
            string ext = Path.GetExtension(p);
            // Strip a .sNp the user typed, so the exporter's own suffix is not doubled.
            return ext.StartsWith(".s", StringComparison.OrdinalIgnoreCase) && ext.EndsWith('p')
                ? p[..^ext.Length]
                : p;
        }
        return Path.Combine(resultsRoot, ResolveResultKey(setup));
    }

    /// <summary>The <c>.snp</c>'s own stem, derived from the setup (or, failing that, the layout)
    /// name — R-em-19's predictable path, so a schematic's SnP reference survives a re-run.</summary>
    public static string ResolveResultKey(EmSetup setup)
        => ResultsWriter.SanitizeFileNameComponent(
            setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(setup.LayoutRef));

    /// <summary>
    /// The <c>.npy</c>'s stem, and the reason it is NOT <see cref="ResolveResultKey"/>.
    ///
    /// <para><b>Owner report, 2026-08-11: "running an EM sim also over-writes the .npy file — that
    /// file is for schematic simulation results, not EM results."</b> Correct, and the mechanism is a
    /// NAME COLLISION rather than anything EM-specific. <c>results/</c> is one flat, shared folder
    /// (R-res-0), a schematic writes <c>results/&lt;schematicKey&gt;.npy</c>, and an EM setup created
    /// beside a cell is named after that same cell — so cell <c>MLin</c>'s schematic and its EM setup
    /// both resolved to <c>results/MLin.npy</c> and the second run silently replaced the first.
    /// <c>RunResultsWriter</c>'s own note records that its <c>.source</c> collision
    /// marker was dropped because two SCHEMATICS can no longer collide; an EM setup is a third
    /// producer that convention never accounted for.</para>
    ///
    /// <para><b>The <c>.npy</c> is NOT dropped, because it carries results the <c>.sNp</c> cannot.</b>
    /// Touchstone holds S and nothing else; the <c>.npy</c> holds the whole <c>DataSet</c> including
    /// the diagnostics group that makes a wrong answer diagnosable — <c>tline</c>'s Zc / Gamma /
    /// Eeff / AttenDbPerM / Rpul / Lpul / Gpul / Cpul for the cross-section kernel, and
    /// <c>planar</c>'s Cpul / CalElectricalDeg / DeembedResidual / DeembedRejected /
    /// DeembedErrorFloor / CalibrationUsable and CalQuasiStatic for the full-wave one. Not writing it would lose every
    /// one of those.</para>
    ///
    /// <para>The <c>.sNp</c> keeps its own unsuffixed name deliberately: it is the artifact a
    /// schematic REFERENCES by path, and renaming it would orphan every existing reference.</para>
    /// </summary>
    public const string NpyKeySuffix = "_em";

    /// <inheritdoc cref="NpyKeySuffix"/>
    public static string ResolveNpyKey(EmSetup setup) => ResolveResultKey(setup) + NpyKeySuffix;

    /// <summary>
    /// Extract → CanSolve → Solve → write. Never throws: an engine failure is captured into
    /// <see cref="EmRunStatus.EngineError"/>, matching <c>SchematicRunService.RunNetlist</c>.
    /// </summary>
    /// <param name="control">Progress and cancellation, or null for neither. Threaded straight
    /// through to the kernel: a full-wave sweep is the longest thing this application does, so it
    /// reports the point count AND what the current point is doing (see <see cref="RunControl"/>'s
    /// own note on why one counter is not enough here).</param>
    /// <summary>
    /// The refusal a setup gets when it declares an internal port — a delta gap or a via to ground
    /// — and the chosen analysis is the uniform-line kernel. Shared by the run and by the panel's
    /// live blocking reason, so the two cannot word it differently.
    /// </summary>
    /// <summary>Kept as the name the tests and call sites already use; the sentence itself now
    /// lives once, in <see cref="EmDiagnostics"/>.</summary>
    internal static string InternalPortNeedsFullWave(string kernelName) =>
        EmDiagnostics.InternalPortNeedsFullWave(kernelName).Render();

    /// <param name="maxCores">The solver's core cap, or null for Automatic (unbounded — the
    /// behaviour every run had before the control existed). <b>An argument rather than a preference
    /// read</b> (brief-cli-em-verb.md R-emcli-3): the GUI passes
    /// <c>EmSolveCorePreference.Preferred</c>, a headless run passes nothing, and this file stays on
    /// the far side of the UI firewall. Clamped by <see cref="EmSolveCores.Sanitise"/> exactly as a
    /// stored value is, so a caller cannot ask for more cores than the machine has. It enters no
    /// provenance hash (R-emp-7), because it cannot change an answer (R-emp-8).</param>
    /// <param name="confirmMemory">brief-em3d-21 R-em3d21-2b — asked, with the warning's sentence, when
    /// a 3D Palace run's memory estimate is past 150 % of this machine's: true starts it anyway. The
    /// panel shows a dialog; the CLI answers with <c>--force</c>. Null refuses such a run. Called on the
    /// run's own thread, before Gmsh and again before Palace (once the mesh's size is known).</param>
    public static EmRunResult Run(
        EmSetup            setup,
        EmLayoutSource?    source,
        string             resultsRoot,
        CancellationToken  ct = default,
        RunControl?        control = null,
        int?               maxCores = null,
        Func<string, bool>? confirmMemory = null)
    {
        try { return RunCore(setup, source, resultsRoot, ct, control, maxCores, confirmMemory); }
        catch (OperationCanceledException)
        {
            // A stopped run is a normal outcome, not a failure — and it wrote nothing, because every
            // write in this file happens after the solve it belongs to. Reported as its own status so
            // the caller can say "stopped" rather than "the EM solve failed".
            var cancelled = EmDiagnostics.Cancelled();
            return new EmRunResult(EmRunStatus.Cancelled, null, null, null, null, null,
                cancelled.Render(), [], Diagnostic: cancelled);
        }
    }

    private static EmRunResult RunCore(
        EmSetup            setup,
        EmLayoutSource?    source,
        string             resultsRoot,
        CancellationToken  ct,
        RunControl?        control,
        int?               maxCores,
        Func<string, bool>? confirmMemory)
    {
        // One token, not two. RunControl bundles cancellation WITH progress precisely so a caller
        // wires both once; where a control is supplied its token is authoritative and the bare `ct`
        // parameter is the fallback for callers (tests, headless drivers) that pass neither.
        if (control is { Token: var t } && t.CanBeCanceled) ct = t;

        var warnings = new List<string>();
        var notes    = new List<string>();
        var errors   = new List<string>();

        if (source is null)
        {
            var d = EmDiagnostics.NoLayout(setup.LayoutRef);
            return new EmRunResult(EmRunStatus.NoLayout, null, null, null, null, null,
                d.Render(), warnings, Diagnostic: d);
        }

        if (source.Technology is null)
        {
            var d = EmDiagnostics.NoTechnology(setup.LayoutRef);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                d.Render(), warnings, Diagnostic: d);
        }

        // brief-em3d-3 — a 3D setup never falls through to a planar kernel (overview §1g).
        // brief-em3d-7 R-em3d7-1a — it is run behind this door, which stays the only one: the discovery
        // checks (brief 6) come first there, then the backend. Nothing below this line changes for a
        // planar setup.
        if (setup.Is3D)
            return CircuitRF.Design.Em3d.Em3dRunService.Run(setup, source, resultsRoot, ct, control, maxCores, confirmMemory);

        // ── A .cem WRITTEN BEFORE THE TYPE MOVED TO THE DRAWING ─────────────────────────────────
        //
        // In memory, writing nothing: a build machine handed an un-migrated pair must produce the
        // answer the application produces, and it must not modify the tree it was given to analyse.
        // The editor applies the SAME migration as a real, undoable layout edit — see
        // EmPortKindMigration for why one door rather than two readers.
        if (EmPortKindMigration.ApplyInMemory(source.View.Shapes, setup.PortKinds) is > 0 and var moved)
            notes.Add($"{moved} port type(s) in this EM setup were carried onto the layout's own port " +
                      "labels, which is where a port's type lives. Open the layout to see them; saving " +
                      "it makes the move permanent.");

        double[] freqs;
        try
        {
            freqs = setup.Frequency.Expand();
        }
        catch (Exception ex)
        {
            var d = EmDiagnostics.FrequencySweepUnresolvable(ex.Message);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                d.Render(), warnings, Diagnostic: d);
        }

        if (freqs.Length == 0)
        {
            var d = EmDiagnostics.FrequencySweepEmpty();
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                d.Render(), warnings, Diagnostic: d);
        }

        double fMax = 0;
        foreach (double f in freqs) fMax = Math.Max(fMax, f);

        // ── R-res-1: the ONE place a kernel is chosen, and its reason is in the notes ──────────
        //
        // Both extractors run on every launch, because the registry needs BOTH verdicts to word
        // either outcome — an explicit cross-section setup that gets refused has to be told that the
        // planar kernel accepts the geometry, and an explicit planar one has to be told when the
        // cheap kernel would have done. Extraction is geometry-only and costs nothing next to a
        // solve; this is not the expensive half.
        // Flattened, exactly as the editor's own Refresh does — the run and the panel must never
        // disagree about what geometry the setup is pointed at.
        var pre = Extract(setup, source, fMax);
        var (geometry, crossSection, planar, choice) =
            (pre.Geometry, pre.CrossSection, pre.Planar, pre.Choice);

        if (pre.EarlyRefusal is { } internalPort)
        {
            // The geometry notes are the only ones worth carrying to a refusal this early — the
            // choice's reason names a kernel that is not going to run.
            AddFindings(EmFindings.AsNotes(geometry.Notes), notes, warnings);
            return new EmRunResult(EmRunStatus.Refused, null, crossSection.Readback, null, null, null,
                internalPort.Render(), warnings, Notes: notes, Errors: errors,
                Kind: choice.Kind, KernelName: choice.KernelName, Diagnostic: internalPort);
        }

        // EM-SEV R-emsev-1 — the findings are SPLIT here, by the class the producer attached, into
        // the two lists this result has carried since the 2026-08-09 report that most of the EM run's
        // Messages rows were wearing the warning icon and should be information. That split was made
        // with the only tool available then — which list a call site happened to append to —
        // and every extraction sentence landed in `notes` regardless of what it said. A run that
        // deleted half the user's circuit reported it three times, correctly, at the weight of the
        // core count.
        AddFindings(pre.Findings, notes, warnings);

        if (!choice.Ok)
        {
            var d = EmDiagnostics.Forwarded("analysis-choice", choice.Refusal);
            return new EmRunResult(EmRunStatus.Refused, null, crossSection.Readback, null, null, null,
                choice.Refusal, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        // ANT-12 — the one combination the panel can express and this kernel cannot honour. The far
        // field belongs to the planar kernel; a cross-section solve returns a uniform line's RLGC and
        // has no currents on artwork to transform. Said out loud rather than silently ignored, which
        // is the same rule the resonance search follows in RunPlanar below.
        if (setup.RadiationPattern && choice.Kind == EmAnalysisKind.CrossSection)
            warnings.Add("The radiation pattern is on but this run used the cross-section " +
                         "(quasi-static) kernel, which solves a uniform transmission-line " +
                         "cross-section and has no radiating artwork to transform. No pattern was " +
                         "computed. Set Analysis to the planar (full-wave) kernel.");

        if (choice.Kind == EmAnalysisKind.Planar)
            return RunPlanar(setup, source, resultsRoot, planar, freqs, choice, warnings, notes, errors, ct, control, maxCores);

        var extraction = crossSection;
        var problem = extraction.Problem!;
        var kernel  = new QuasiStaticKernel(setup.DispersionCorrection);

        var verdict = kernel.CanSolve(problem);
        if (!verdict.Ok)
        {
            var d = EmDiagnostics.Forwarded("kernel", verdict.Reason);
            return new EmRunResult(EmRunStatus.Refused, null, extraction.Readback, null, null, null,
                verdict.Reason, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        EmSolveResult solved;
        try
        {
            control?.BeginStage("solving the cross-section");
            solved = kernel.SolveDetailed(problem, setup.Mesh, freqs, ct,
                EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron));
            control?.Tick(freqs.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var d = EmDiagnostics.SolveFailed(ex.Message);
            return new EmRunResult(EmRunStatus.EngineError, null, extraction.Readback, null, null, null,
                d.Render(), warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        // R-em-16: the engine's own report is surfaced verbatim, never re-worded.
        notes.AddRange(solved.MeshReport.Notes);
        notes.AddRange(solved.Rlgc.Notes);
        // R-gen-5: the mode-coupling residual is a per-SOLVE number — the extractor could not have
        // made it, because it does not know the frequencies.
        if (solved.SolveNotes is { } sn) notes.AddRange(sn);

        // R-em-20: compare BEFORE overwriting — the whole point is to tell the user their schematic
        // has been reading stale s-parameters, which is only knowable from the file about to be replaced.
        string? snpPath = ResolveSnpPath(resultsRoot, setup, problem.Ports.Count);
        if (EmSnpProvenance.DescribeStaleness(snpPath, problem, setup.Mesh) is { } staleWarning)
            warnings.Add(staleWarning);

        string? npyPath = null;
        try
        {
            // WriteRun appends "results" to the base dir it is given — hand it the parent, exactly
            // as the schematic run path does, so both land in the same one results/ folder.
            var written = ResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar))
                    ?? resultsRoot,
                ResolveNpyKey(setup), solved.Data);
            if (written.Error is { } writeError)
                errors.Add($"The EM result could not be written to results/: {writeError}");
            npyPath = written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception ex)
        {
            errors.Add($"The EM result could not be written to results/: {ex.Message}");
        }

        try
        {
            WriteSnp(solved.Data, ResolveSnpBasePath(resultsRoot, setup), problem, setup);
        }
        catch (Exception ex)
        {
            errors.Add($"The .snp could not be written to '{snpPath}': {ex.Message}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, solved.Data, extraction.Readback, solved.MeshReport,
                               npyPath, snpPath, null, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);
    }

    // ── The planar branch, behind the registry (R-res-1) ───────────────────────────────────────

    /// <summary>
    /// Kernel B's run, structurally identical to kernel A's: extract → ports → CanSolve → solve →
    /// staleness → <c>.npy</c> → <c>.snp</c>. <b>R-res-6: the same <c>DataSet</c> shape, the same
    /// predictable <c>.snp</c> path, the same writer.</b> Nothing new is minted here — a second
    /// naming convention would orphan every schematic reference the first one made.
    ///
    /// <para><b>PORTS ARE ASKED BEFORE <c>CanSolve</c>, AND THE ORDER IS THE POINT</b> (user report,
    /// 2026-09-18). Both are prerequisites, so whichever is asked first is the one a user with two
    /// problems ever reads — and a board with NO PORT LABELS AT ALL got the kernel's via
    /// electrical-length paragraph instead of "this layout has no port labels". The port refusal is
    /// about something the user did or did not draw; the kernel's verdict is a physics limit on a
    /// structure there is, as yet, no reason to solve. Port extraction is geometry-only and costs
    /// nothing, and by this line <c>PlanarExtractor</c> has already refused every no-metal case, so
    /// nothing is lost by asking it first. <see cref="Preview"/> and the EM panel's own
    /// <c>BlockingReason</c> order the two the same way for the same reason.</para>
    /// </summary>
    private static EmRunResult RunPlanar(
        EmSetup setup, EmLayoutSource source, string resultsRoot,
        PlanarExtractionResult extraction, double[] freqs, EmKernelChoice choice,
        List<string> warnings, List<string> notes, List<string> errors,
        CancellationToken ct, RunControl? control = null, int? maxCores = null)
    {
        var problem = extraction.Problem!;
        var kernel  = new PlanarKernel();
        var lengthFmt = EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron);

        // D3 — the ports come from the layout's own IsPort labels, and an ambiguous one is refused
        // by name rather than guessed (R-res-5).
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));

        notes.AddRange(ports.Notes);
        if (!ports.Ok)
        {
            var d = EmDiagnostics.Forwarded("ports", ports.Refusal);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ports.Refusal, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        var verdict = kernel.CanSolve(problem, lengthFmt);
        if (!verdict.Ok)
        {
            var d = EmDiagnostics.Forwarded("kernel", verdict.Reason);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                verdict.Reason, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        PlanarKernelResult solved;
        try
        {
            // Until M2 this passed `null`, so NOTHING in PlanarSolveSettings/PlanarFillSettings was
            // reachable from the EM panel — including adaptive frequency sampling, which exists
            // precisely so the default 101-point sweep is not 80 minutes to three hours (L8d/L9d
            // measured 48 s and 71.9 s per de-embedded point). It is ON by default; see
            // EmSetup.AdaptiveSampling for the accuracy measurement that makes that safe.
            // M1 (R-emp-6): the core cap is a MACHINE preference, so it is never read from the .cem.
            // It arrives as an ARGUMENT (R-emcli-3) — the GUI passes EmSolveCorePreference.Preferred,
            // a headless run passes nothing. Null (Automatic) reproduces the unbounded behaviour every
            // run had before the control existed, and it enters no provenance hash (R-emp-7) because
            // it cannot change an answer (R-emp-8).
            // M5 (2026-08-14): the accelerator is a SECOND fill-settings term, so the two are composed
            // through one base rather than each branching off `PlanarSolveSettings.Default.Fill` —
            // written as two independent ternaries, turning on the accelerator would silently discard
            // the direct vertical kernel, which is exactly the sort of quiet setting loss this panel
            // has been bitten by before.
            var fill = PlanarSolveSettings.Default.Fill ?? PlanarFillSettings.Default;
            if (setup.DirectVerticalKernel) fill = fill with { DirectVerticalKernel = true };
            if (setup.AcceleratedSolve)     fill = fill with { Aim = PlanarAimSettings.Default };

            var solveSettings = PlanarSolveSettings.Default with
            {
                Adaptive = setup.AdaptiveSampling
                    ? PlanarAdaptiveSettings.Default with
                      {
                          // ANT-9. Nested inside the adaptive settings because the search seeds
                          // itself from the interpolant refinement already built — see the combo
                          // note below for what happens when the panel asks for one without the
                          // other.
                          Search = setup.ResonanceSearch ? PlanarResonanceSettings.Default : null,
                      }
                    : null,
                MaxDegreeOfParallelism = EmSolveCores.Sanitise(maxCores),
                // ANT-12 — the far field's FIRST user-reachable switch. Null (off) is the behaviour
                // every run had before this line existed; set, the pattern rides along with the
                // solve the sweep already pays for at every point that was actually solved.
                // PlanarFarFieldSettings.Default is a 1 degree x 1 degree hemisphere and the whole
                // metric registry, which is deliberately not tunable from the panel: a pattern with
                // no numbers attached is half an answer, and the metrics cost a fraction of one
                // frequency point's own solve.
                // A pattern at EVERY frequency the sweep asked for, not at one. Left to its own
                // default the far field produces a single pattern at freqs[0] — the BOTTOM of the
                // sweep, which on an antenna is the one frequency nobody wants: measured on the
                // shipped 5.8 GHz example, 5.3 GHz reports 22.6 % radiation efficiency against 60 %
                // at resonance, and both numbers are correct about different questions. Each request
                // is mapped to the nearest point that was actually SOLVED and the set is deduplicated
                // by index, so with adaptive sampling on this is a pattern at every solved point
                // rather than one per requested point.
                //
                // AND IT IS NOT FREE — say the real number rather than the comfortable one. A
                // pattern is an exact O(N) sum per direction with no second fill and no second
                // factorisation, but a 1 degree x 1 degree hemisphere is 32,760 directions and at
                // N = 1,611 that measured ~6.4 s against ~4.6 s for the de-embedded solve it rides
                // on — MORE than the point it is attached to, not a fraction of it. End to end on
                // the shipped 5.8 GHz example: the same 21-point sweep took 1 m 37 s with one
                // pattern and 3 m 45 s with twenty-one. That is the price of the switch, and it is
                // paid only when the user asks for it.
                //
                // The reference power rides along because TRP and peak EIRP are the only ABSOLUTE
                // quantities in the metric registry — every other one is a ratio and needs no
                // excitation to be absolute against. See PlanarMetricSettings for the whole of why.
                FarField = setup.RadiationPattern
                    ? PlanarFarFieldSettings.Default with
                      {
                          FrequenciesHz = freqs,
                          Metrics = PlanarMetricSettings.Default with
                                    { ReferenceInputPowerDbm = setup.ReferenceInputPowerDbm },
                      }
                    : null,
                Fill = setup.DirectVerticalKernel || setup.AcceleratedSolve
                    ? fill
                    : PlanarSolveSettings.Default.Fill,
                // PCAL2 — `Deembed` is DELIBERATELY NOT SET from the setup any more, and the .cem
                // field that used to set it is gone. It stays at the engine default (on). For an
                // edge port the raw solve is an open circuit rather than a degraded answer, and for
                // every other port kind de-embedding is already inert, so there was no third case
                // the switch preserved — EmSetup.LegacyRawSolveRequested carries the whole finding
                // and the measurement behind it. The engine flag itself is untouched and is still
                // how the far-field and resonance paths ask for the raw current distribution.
                //
                // The one that remains is the explicit, self-declaring way past the clearance
                // refusal below — it publishes a de-embedded answer, not an uncalibrated one.
                DeembedOutsideCalibrationValidity = setup.DeembedOutsideCalibrationValidity,
            };
            // ANT-9 — the one combination the panel can express and the engine cannot honour. The
            // search bisects toward a zero crossing of Im(Z_in) that it finds in the interpolant
            // adaptive refinement builds; with adaptive sampling off there is no interpolant and
            // nothing to seed from. Left off, and SAID, rather than quietly doing nothing — which
            // is the failure mode this repository has been bitten by more than once.
            // A legacy `.cem` asked for something this no longer does. Said, rather than honoured
            // and rather than ignored: the file's stored answer was an open circuit at every edge
            // port, so running it as written is not an option — but changing what a document asks
            // for without saying so is the failure mode this repository keeps paying for.
            if (setup.LegacyRawSolveRequested)
                warnings.Add("This EM setup carries \"Deembed\": false, a setting that has been " +
                             "REMOVED, and the run below is de-embedded. That switch published the " +
                             "raw delta-gap solve, which is not the structure's response with a " +
                             "launch included — at an edge port the cut sits one cell inside the " +
                             "drawn metal, so the port drives an isolated sliver and reads as an " +
                             "OPEN. Measured on a plain 3.8 mm microstrip it gave S11 = +1 and " +
                             "S21 = -107 dB, and doubling the line's length moved S11 in the fourth " +
                             "decimal. Saving this setup drops the field.");

            if (setup.ResonanceSearch && !setup.AdaptiveSampling)
                warnings.Add("The resonance search is on but adaptive frequency sampling is off, so " +
                             "the search has been left off too: it seeds itself from the interpolant " +
                             "adaptive sampling builds, and with every point solved there is nothing " +
                             "for it to seed from. Turn adaptive sampling on to use it.");

            var lengthFormat = EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron);
            solved = kernel.Solve(problem, setup.PlanarMesh, ports.Ports, freqs, solveSettings, ct, control,
                                  lengthFormat);
        }
        catch (OperationCanceledException) { throw; }
        catch (PlanarFeedClearanceRefusedException ex)
        {
            // PCAL2/R-pcal2-1 — a REFUSAL, reported the way the mesh ceiling directly below is
            // reported and for the same reason: it reads as "this geometry cannot be de-embedded,
            // and here is the quantity that says so", not as "circuitRF broke". No .sNp is written
            // because nothing past this point runs, which is the whole point — a note does not
            // survive onto the file and the file is what the next person opens.
            var d = EmDiagnostics.Forwarded("port-clearance", ex.Message);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ex.Message, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }
        catch (PlanarPortCollisionRefusedException ex)
        {
            // Two of the user's port labels landed on one cut. The user's geometry, so a refusal
            // rather than an EngineError — the same treatment, and for the same reason, as the feed
            // clearance above. See PlanarPorts.ResolveAll.
            var d = EmDiagnostics.Forwarded("ports", ex.Message);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ex.Message, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }
        catch (PlanarMeshRefusedException ex)
        {
            // R17's ceiling is a REFUSAL, not a crash, and its diagnosis lives in the mesh report's
            // notes rather than in its one-sentence message (owner report, 2026-08-14: a user was
            // handed the ceiling and the megabytes with none of the sentences that say why the count
            // is what it is, and turned the one knob the message named — which on that geometry
            // changes nothing). Reporting it as EngineError was the second half of the same problem:
            // it reads as "circuitRF broke" when the answer is "this mesh is too big, and here is the
            // quantity that made it that big".
            notes.AddRange(ex.Report.Notes);
            var d = EmDiagnostics.Forwarded("mesh-ceiling", ex.Message);
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ex.Message, warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }
        catch (Exception ex)
        {
            var d = EmDiagnostics.SolveFailed(ex.Message);
            return new EmRunResult(EmRunStatus.EngineError, null, null, null, null, null,
                d.Render(), warnings, Notes: notes, Errors: errors, Kind: choice.Kind,
                KernelName: choice.KernelName, Diagnostic: d);
        }

        // R-em-16, unchanged for kernel B: the engine's own notes go out verbatim — and, since
        // EM-SEV, into the list its own class names. The level-separation warning rides here.
        AddFindings(solved.Findings, notes, warnings);

        // D9/R-res-9 — compare BEFORE overwriting, exactly as kernel A does.
        string? snpPath = ResolveSnpPath(resultsRoot, setup, ports.Ports.Count);
        if (EmSnpProvenance.DescribeStaleness(snpPath, problem, setup.PlanarMesh, ports.Ports)
            is { } staleWarning)
            warnings.Add(staleWarning);

        string? npyPath = null;
        try
        {
            var written = ResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot,
                ResolveNpyKey(setup), solved.Data);
            if (written.Error is { } writeError)
                errors.Add($"The EM result could not be written to results/: {writeError}");
            npyPath = written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception ex)
        {
            errors.Add($"The EM result could not be written to results/: {ex.Message}");
        }

        try
        {
            WritePlanarSnp(solved.Data, ResolveSnpBasePath(resultsRoot, setup),
                           problem, setup, ports.Ports, solved.Solve,
                           // MIM-10 — the port map goes on the FILE, built from the extraction's own
                           // index-aligned labels rather than re-derived from the numbers.
                           EmSnpProvenance.PortMap([.. problem.Layers.Select(l => l.Name)],
                                                   ports.Ports, ports.SourceLabels,
                                                   source.DbuPerMicron, source.View.DisplayUnit));
        }
        catch (Exception ex)
        {
            errors.Add($"The .snp could not be written to '{snpPath}': {ex.Message}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, solved.Data, null, null, npyPath, snpPath, null,
                               warnings, Notes: notes, Errors: errors,
                               Kind: choice.Kind, KernelName: choice.KernelName,
                               PlanarMesh: solved.MeshReport, PlanarSolve: solved.Solve,
                               CurrentDensity: solved.CurrentDensity, PlanarPorts: solved.Ports);
    }

    /// <summary>The same exporter, the same options, the planar provenance stamp (D9).</summary>
    private static void WritePlanarSnp(
        DataSet data, string snpBasePath, PlanarProblem problem, EmSetup setup,
        IReadOnlyList<PlanarPort> ports, PlanarSolveResult? solve = null,
        IReadOnlyList<string>? portMap = null)
    {
        string? group = null;
        foreach (var g in data.Groups)
            if (data.CubesIn(g).ContainsKey("S")) { group = g; break; }
        if (group is null) throw new InvalidOperationException("the solved DataSet carries no S cube");

        double z0 = ports.Count > 0 ? ports[0].Z0.Real : 50;
        var opts = new TouchstoneExportOptions(
            Z0Ohms:         z0 > 0 ? z0 : 50,
            Digits:         10,
            DigitFormat:    'g',
            MatrixFormat:   MatrixFormat.RI,
            HeaderComments: EmSnpProvenance.BuildHeader(
                problem, setup.PlanarMesh, ports,
                setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(snpBasePath),
                ProvenanceLayout(setup), DateTimeOffset.Now,
                // PCAL2/R-pcal2-2 — the caveat rides on the FILE, because the finding was that the
                // file outlives the notes. Empty on every run that did not de-embed outside the
                // calibration's validity, so an ordinary .sNp is byte-identical to one written
                // before this existed.
                EmSnpProvenance.ValidityCaveats(solve),
                portMap));

        Directory.CreateDirectory(Path.GetDirectoryName(snpBasePath)!);

        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBasePath);

        if (result.Status != TouchstoneExportStatus.Ok)
            throw new InvalidOperationException($"Touchstone export returned {result.Status}.");
    }

    /// <summary>The layout line of the provenance stamp — the reference alone, as it always was, or the
    /// reference and the solve region when one is set, so a Touchstone file read months later still
    /// says it describes part of the board. A setup with no region writes the same bytes as before.</summary>
    private static string ProvenanceLayout(EmSetup setup) =>
        setup.SolveRegion is { } r ? $"{setup.LayoutRef} (solve region {Ascii(r.Describe())})" : setup.LayoutRef;

    /// <summary>The Touchstone header is written in an encoding that loses "µ", "–" and "×" (the
    /// reason the header's own first line is ASCII), so the region is spelled in ASCII there.</summary>
    private static string Ascii(string s) => s.Replace("µm", "um").Replace("–", "-").Replace("×", "x");

    /// <summary>R-em-19: uses the existing <c>RfCore.Export.TouchstoneExporter</c>, with the
    /// provenance stamp riding on its new additive <c>HeaderComments</c> option.</summary>
    private static void WriteSnp(DataSet data, string snpBasePath, EmProblem problem, EmSetup setup)
    {
        string? group = null;
        foreach (var g in data.Groups)
            if (data.CubesIn(g).ContainsKey("S")) { group = g; break; }
        if (group is null) throw new InvalidOperationException("the solved DataSet carries no S cube");

        double z0 = problem.Ports[0].Z0.Real;
        var opts = new TouchstoneExportOptions(
            Z0Ohms:         z0 > 0 ? z0 : 50,
            Digits:         10,
            DigitFormat:    'g',
            MatrixFormat:   MatrixFormat.RI,
            HeaderComments: EmSnpProvenance.BuildHeader(
                problem, setup.Mesh,
                setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(snpBasePath),
                ProvenanceLayout(setup), DateTimeOffset.Now));

        Directory.CreateDirectory(Path.GetDirectoryName(snpBasePath)!);

        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBasePath);

        if (result.Status != TouchstoneExportStatus.Ok)
            throw new InvalidOperationException($"Touchstone export returned {result.Status}.");
    }
}
