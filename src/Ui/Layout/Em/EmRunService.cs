// R-em-18 — the Simulate path is RunSchematicDocAsync's five steps with a different middle.
// Background Task.Run, Messages for warnings first, then RunResultsWriter.WriteRun →
// RefreshOpenDataDisplaysAsync → AutoOpenOrCreateDataDisplayAsync.
//
// **No new results plumbing and no new result type — and L8e did not add one either.** Whichever
// kernel runs, the DataSet carries S, per-port Z0, and ONE diagnostics group:
//
//   kernel A (cross-section) → "tline"   Zc, Gamma, Eeff, AttenDbPerM, Rpul, Lpul, Gpul, Cpul
//   kernel B (planar)        → "planar"  Gamma, Zc, Eeff, AttenDbPerM, Cpul, CalElectricalDeg,
//                                        DeembedResidual, DeembedRejected, CalibrationUsable
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

using RfCore;
using RfCore.Data;
using RfCore.Export;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Layout.Em;

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
    IReadOnlyList<PlanarPortResolution>? PlanarPorts = null);

/// <summary>
/// Headless. Everything the Simulate button does that is not dispatcher work, so it is testable
/// without a document, a canvas or a workspace — the same rule R-em-1 puts on the extractor.
/// </summary>
public static class EmRunService
{
    /// <summary>
    /// R-em-19: the <c>.snp</c> lands at a PREDICTABLE path derived from the layout and setup names,
    /// mirroring <c>RunResultsWriter</c>'s own convention, so a schematic's SnP reference is stable
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
        => Schematic.RunResultsWriter.SanitizeFileNameComponent(
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
    /// <see cref="Schematic.RunResultsWriter"/>'s own note records that its <c>.source</c> collision
    /// marker was dropped because two SCHEMATICS can no longer collide; an EM setup is a third
    /// producer that convention never accounted for.</para>
    ///
    /// <para><b>The <c>.npy</c> is NOT dropped, because it carries results the <c>.sNp</c> cannot.</b>
    /// Touchstone holds S and nothing else; the <c>.npy</c> holds the whole <c>DataSet</c> including
    /// the diagnostics group that makes a wrong answer diagnosable — <c>tline</c>'s Zc / Gamma /
    /// Eeff / AttenDbPerM / Rpul / Lpul / Gpul / Cpul for the cross-section kernel, and
    /// <c>planar</c>'s Cpul / CalElectricalDeg / DeembedResidual / DeembedRejected /
    /// CalibrationUsable for the full-wave one. Not writing it would lose every one of those.</para>
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
    internal static string InternalPortNeedsFullWave(string kernelName) =>
        $"This EM setup declares an internal port, and the analysis it resolved to is the " +
        $"{kernelName}. An internal delta gap is a cut across a conductor at a mesh gridline, and an " +
        "internal port is the foot of a via down to the ground plane — and the uniform-line " +
        "kernel never meshes the " +
        "plane at all: it solves a cross-section for per-unit-length RLGC and forms the network of a " +
        "length-L line in closed form, so its only ports are the two ends of that line, by " +
        "construction. There is nowhere for either to be. Running anyway would publish a complete " +
        "and plausible answer for your line WITHOUT the port you asked for, which is why this is " +
        "refused rather than reported. Set Analysis to the full-wave planar kernel, or change the " +
        "port back to an edge port.";

    public static EmRunResult Run(
        EmSetup            setup,
        EmLayoutSource?    source,
        string             resultsRoot,
        CancellationToken  ct = default,
        RunControl?        control = null)
    {
        try { return RunCore(setup, source, resultsRoot, ct, control); }
        catch (OperationCanceledException)
        {
            // A stopped run is a normal outcome, not a failure — and it wrote nothing, because every
            // write in this file happens after the solve it belongs to. Reported as its own status so
            // the caller can say "stopped" rather than "the EM solve failed".
            return new EmRunResult(EmRunStatus.Cancelled, null, null, null, null, null,
                "The EM run was stopped. Nothing was written.", []);
        }
    }

    private static EmRunResult RunCore(
        EmSetup            setup,
        EmLayoutSource?    source,
        string             resultsRoot,
        CancellationToken  ct,
        RunControl?        control)
    {
        // One token, not two. RunControl bundles cancellation WITH progress precisely so a caller
        // wires both once; where a control is supplied its token is authoritative and the bare `ct`
        // parameter is the fallback for callers (tests, headless drivers) that pass neither.
        if (control is { Token: var t } && t.CanBeCanceled) ct = t;

        var warnings = new List<string>();
        var notes    = new List<string>();
        var errors   = new List<string>();

        if (source is null)
            return new EmRunResult(EmRunStatus.NoLayout, null, null, null, null, null,
                $"The layout '{setup.LayoutRef}' could not be found, so there is no geometry to " +
                "analyse. Point this EM setup at a layout that exists.", warnings);

        if (source.Technology is null)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                $"The layout '{setup.LayoutRef}' has no technology resolved, so nothing says how " +
                "thick its metal is or where the ground plane sits.", warnings);

        double[] freqs;
        try
        {
            freqs = setup.Frequency.Expand();
        }
        catch (Exception ex)
        {
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                $"The frequency sweep could not be resolved: {ex.Message}", warnings);
        }

        if (freqs.Length == 0)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                "The frequency sweep produced no points. Check the start, stop and step or count.",
                warnings);

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
        var geometry = EmGeometry.Flatten(source.View, source.AbsolutePath);
        notes.AddRange(geometry.Notes);

        var crossSection = CrossSectionExtractor.Extract(
            geometry.Shapes, source.Technology, source.DbuPerMicron,
            setup.ToExtractionSettings(setup.LayoutRef));

        var planar = PlanarExtractor.Extract(
            geometry.Shapes, source.Technology, source.DbuPerMicron, fMax,
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
        if (choice.Ok && choice.Kind == EmAnalysisKind.CrossSection && setup.DeclaresInternalPort())
            return new EmRunResult(EmRunStatus.Refused, null, crossSection.Readback, null, null, null,
                InternalPortNeedsFullWave(choice.KernelName), warnings, Notes: notes, Errors: errors,
                Kind: choice.Kind, KernelName: choice.KernelName);

        notes.Add(choice.Reason);

        // The CHOSEN extractor's notes, whichever way it went and whether or not it accepted — the
        // "N shapes were ignored" lines are as useful next to a refusal as next to an answer.
        notes.AddRange(choice.Kind == EmAnalysisKind.Planar ? planar.Notes : crossSection.Notes);

        if (!choice.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, crossSection.Readback, null, null, null,
                choice.Refusal, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);

        if (choice.Kind == EmAnalysisKind.Planar)
            return RunPlanar(setup, source, resultsRoot, planar, freqs, choice, warnings, notes, errors, ct, control);

        var extraction = crossSection;
        var problem = extraction.Problem!;
        var kernel  = new QuasiStaticKernel(setup.DispersionCorrection);

        var verdict = kernel.CanSolve(problem);
        if (!verdict.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, extraction.Readback, null, null, null,
                verdict.Reason, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);

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
            return new EmRunResult(EmRunStatus.EngineError, null, extraction.Readback, null, null, null,
                $"The EM solve failed: {ex.Message}", warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);
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
            var written = Schematic.RunResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar))
                    ?? resultsRoot,
                ResolveNpyKey(setup), solved.Data, null);
            npyPath = written.Count > 0 ? written[0] : null;
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
    /// </summary>
    private static EmRunResult RunPlanar(
        EmSetup setup, EmLayoutSource source, string resultsRoot,
        PlanarExtractionResult extraction, double[] freqs, EmKernelChoice choice,
        List<string> warnings, List<string> notes, List<string> errors,
        CancellationToken ct, RunControl? control = null)
    {
        var problem = extraction.Problem!;
        var kernel  = new PlanarKernel();

        var verdict = kernel.CanSolve(problem);
        if (!verdict.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                verdict.Reason, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);

        // D3 — the ports come from the layout's own IsPort labels, and an ambiguous one is refused
        // by name rather than guessed (R-res-5).
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit, setup.ResolvePortKind,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));

        notes.AddRange(ports.Notes);
        if (!ports.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ports.Refusal, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);

        PlanarKernelResult solved;
        try
        {
            // Until M2 this passed `null`, so NOTHING in PlanarSolveSettings/PlanarFillSettings was
            // reachable from the EM panel — including adaptive frequency sampling, which exists
            // precisely so the default 101-point sweep is not 80 minutes to three hours (L8d/L9d
            // measured 48 s and 71.9 s per de-embedded point). It is ON by default; see
            // EmSetup.AdaptiveSampling for the accuracy measurement that makes that safe.
            // M1 (R-emp-6): the core cap is a MACHINE preference, so it is read here rather than
            // from the .cem — see EmSolveCores. Null (Automatic) reproduces the unbounded behaviour
            // every run had before the control existed, and it enters no provenance hash (R-emp-7)
            // because it cannot change an answer (R-emp-8).
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
                Adaptive = setup.AdaptiveSampling ? PlanarAdaptiveSettings.Default : null,
                MaxDegreeOfParallelism = EmSolveCores.Preferred,
                Fill = setup.DirectVerticalKernel || setup.AcceleratedSolve
                    ? fill
                    : PlanarSolveSettings.Default.Fill,
            };
            var lengthFormat = EmLengthFormat.For(source.View.DisplayUnit, source.DbuPerMicron);
            solved = kernel.Solve(problem, setup.PlanarMesh, ports.Ports, freqs, solveSettings, ct, control,
                                  lengthFormat);
        }
        catch (OperationCanceledException) { throw; }
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
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ex.Message, warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);
        }
        catch (Exception ex)
        {
            return new EmRunResult(EmRunStatus.EngineError, null, null, null, null, null,
                $"The EM solve failed: {ex.Message}", warnings, Notes: notes, Errors: errors, Kind: choice.Kind, KernelName: choice.KernelName);
        }

        // R-em-16, unchanged for kernel B: the engine's own notes go out verbatim.
        notes.AddRange(solved.Notes);

        // D9/R-res-9 — compare BEFORE overwriting, exactly as kernel A does.
        string? snpPath = ResolveSnpPath(resultsRoot, setup, ports.Ports.Count);
        if (EmSnpProvenance.DescribeStaleness(snpPath, problem, setup.PlanarMesh, ports.Ports)
            is { } staleWarning)
            warnings.Add(staleWarning);

        string? npyPath = null;
        try
        {
            var written = Schematic.RunResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot,
                ResolveNpyKey(setup), solved.Data, null);
            npyPath = written.Count > 0 ? written[0] : null;
        }
        catch (Exception ex)
        {
            errors.Add($"The EM result could not be written to results/: {ex.Message}");
        }

        try
        {
            WritePlanarSnp(solved.Data, ResolveSnpBasePath(resultsRoot, setup),
                           problem, setup, ports.Ports);
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
        IReadOnlyList<PlanarPort> ports)
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
                setup.LayoutRef, DateTimeOffset.Now));

        Directory.CreateDirectory(Path.GetDirectoryName(snpBasePath)!);

        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBasePath);

        if (result.Status != TouchstoneExportStatus.Ok)
            throw new InvalidOperationException($"Touchstone export returned {result.Status}.");
    }

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
                setup.LayoutRef, DateTimeOffset.Now));

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
