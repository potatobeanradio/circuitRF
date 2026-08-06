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
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Layout.Em;

public enum EmRunStatus { Ok, Refused, NoLayout, EngineError }

public sealed record EmRunResult(
    EmRunStatus           Status,
    DataSet?              Data,
    EmCrossSectionReadback? Readback,
    EmMeshReport?         MeshReport,
    string?               NpyPath,
    string?               SnpPath,
    string?               Error,
    IReadOnlyList<string> Warnings,
    /// <summary>Which kernel actually ran (or was refused) — never
    /// <see cref="EmAnalysisKind.Auto"/>, which is a request rather than an outcome.</summary>
    EmAnalysisKind        Kind           = EmAnalysisKind.CrossSection,
    string                KernelName     = "",
    PlanarMeshReport?     PlanarMesh     = null,
    PlanarSolveResult?    PlanarSolve    = null,
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

    /// <summary>The results-file stem the <c>.npy</c> uses — same key as the <c>.snp</c>, so a run's
    /// two artifacts always name each other.</summary>
    public static string ResolveResultKey(EmSetup setup)
        => Schematic.RunResultsWriter.SanitizeFileNameComponent(
            setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(setup.LayoutRef));

    /// <summary>
    /// Extract → CanSolve → Solve → write. Never throws: an engine failure is captured into
    /// <see cref="EmRunStatus.EngineError"/>, matching <c>SchematicRunService.RunNetlist</c>.
    /// </summary>
    public static EmRunResult Run(
        EmSetup            setup,
        EmLayoutSource?    source,
        string             resultsRoot,
        CancellationToken  ct = default)
    {
        var warnings = new List<string>();

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
        var crossSection = CrossSectionExtractor.Extract(
            source.View.Shapes, source.Technology, source.DbuPerMicron,
            setup.ToExtractionSettings(setup.LayoutRef));

        var planar = PlanarExtractor.Extract(
            source.View.Shapes, source.Technology, source.DbuPerMicron, fMax,
            setup.ToExtractionSettings(setup.LayoutRef));

        var choice = EmKernelRegistry.Choose(
            setup.AnalysisKind,
            crossSection.Ok ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(crossSection.Refusal ?? ""),
            planar.Ok       ? EmExtractorVerdict.Yes : EmExtractorVerdict.No(planar.Refusal ?? ""));

        warnings.Add(choice.Reason);

        // The CHOSEN extractor's notes, whichever way it went and whether or not it accepted — the
        // "N shapes were ignored" lines are as useful next to a refusal as next to an answer.
        warnings.AddRange(choice.Kind == EmAnalysisKind.Planar ? planar.Notes : crossSection.Notes);

        if (!choice.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, crossSection.Readback, null, null, null,
                choice.Refusal, warnings, choice.Kind, choice.KernelName);

        if (choice.Kind == EmAnalysisKind.Planar)
            return RunPlanar(setup, source, resultsRoot, planar, freqs, choice, warnings, ct);

        var extraction = crossSection;
        var problem = extraction.Problem!;
        var kernel  = new QuasiStaticKernel(setup.DispersionCorrection);

        var verdict = kernel.CanSolve(problem);
        if (!verdict.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, extraction.Readback, null, null, null,
                verdict.Reason, warnings, choice.Kind, choice.KernelName);

        EmSolveResult solved;
        try
        {
            solved = kernel.SolveDetailed(problem, setup.Mesh, freqs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EmRunResult(EmRunStatus.EngineError, null, extraction.Readback, null, null, null,
                $"The EM solve failed: {ex.Message}", warnings, choice.Kind, choice.KernelName);
        }

        // R-em-16: the engine's own report is surfaced verbatim, never re-worded.
        warnings.AddRange(solved.MeshReport.Notes);
        warnings.AddRange(solved.Rlgc.Notes);
        // R-gen-5: the mode-coupling residual is a per-SOLVE number — the extractor could not have
        // made it, because it does not know the frequencies.
        if (solved.SolveNotes is { } sn) warnings.AddRange(sn);

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
                ResolveResultKey(setup), solved.Data, null);
            npyPath = written.Count > 0 ? written[0] : null;
        }
        catch (Exception ex)
        {
            warnings.Add($"The EM result could not be written to results/: {ex.Message}");
        }

        try
        {
            WriteSnp(solved.Data, ResolveSnpBasePath(resultsRoot, setup), problem, setup);
        }
        catch (Exception ex)
        {
            warnings.Add($"The .snp could not be written to '{snpPath}': {ex.Message}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, solved.Data, extraction.Readback, solved.MeshReport,
                               npyPath, snpPath, null, warnings, choice.Kind, choice.KernelName);
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
        List<string> warnings, CancellationToken ct)
    {
        var problem = extraction.Problem!;
        var kernel  = new PlanarKernel();

        var verdict = kernel.CanSolve(problem);
        if (!verdict.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                verdict.Reason, warnings, choice.Kind, choice.KernelName);

        // D3 — the ports come from the layout's own IsPort labels, and an ambiguous one is refused
        // by name rather than guessed (R-res-5).
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0);

        warnings.AddRange(ports.Notes);
        if (!ports.Ok)
            return new EmRunResult(EmRunStatus.Refused, null, null, null, null, null,
                ports.Refusal, warnings, choice.Kind, choice.KernelName);

        PlanarKernelResult solved;
        try
        {
            solved = kernel.Solve(problem, setup.PlanarMesh, ports.Ports, freqs, null, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new EmRunResult(EmRunStatus.EngineError, null, null, null, null, null,
                $"The EM solve failed: {ex.Message}", warnings, choice.Kind, choice.KernelName);
        }

        // R-em-16, unchanged for kernel B: the engine's own notes go out verbatim.
        warnings.AddRange(solved.Notes);

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
                ResolveResultKey(setup), solved.Data, null);
            npyPath = written.Count > 0 ? written[0] : null;
        }
        catch (Exception ex)
        {
            warnings.Add($"The EM result could not be written to results/: {ex.Message}");
        }

        try
        {
            WritePlanarSnp(solved.Data, ResolveSnpBasePath(resultsRoot, setup),
                           problem, setup, ports.Ports);
        }
        catch (Exception ex)
        {
            warnings.Add($"The .snp could not be written to '{snpPath}': {ex.Message}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, solved.Data, null, null, npyPath, snpPath, null,
                               warnings, choice.Kind, choice.KernelName,
                               solved.MeshReport, solved.Solve,
                               solved.CurrentDensity, solved.Ports);
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
