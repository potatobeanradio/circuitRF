// brief-em3d-7 R-em3d7-1a — a 3D setup's run, behind EmRunService.Run (overview §5).
//
// EmRunService is the ONE door: the `em` verb, the .cem panel's Simulate and EmCliVerbTests all reach
// this file through it, and nothing else may call it (gate 12's source scan). It returns the same
// EmRunResult with the same statuses as a planar run, so no caller branches.
//
//   discovery (brief 6)  -> Em3dGenerator (brief 3) -> GmshGeoWriter -> gmsh -> entity check
//                        -> PalaceConfigWriter -> palace -> port-S.csv -> .sNp + .npy
//
// The version and capability checks run FIRST, so a missing or unvalidated program costs seconds and
// starts no mesher (R-em3d7-4c, gate 11).
//
// openEMS (brief-em3d-9) is the second backend behind the same door:
//
//   discovery -> Em3dGenerator -> FdtdGrid (brief 8) -> CsxcadWriter -> openEMS once per port
//             -> probe files -> FdtdPortTransform (src/Engine) -> .sNp + .npy
//
// and lands by the rule below with the token `openems`.
//
// WHERE RESULTS LAND (R-em3d7-5, em-3d.md §4.5 — this brief is the first backend built, so it owns the
// rule and brief 9 reuses it): the solver is part of a 3D result's name, so running a second solver on
// one setup never overwrites the first. `<key>.palace.sNp`, the `.npy` key `<key>.palace` +
// NpyKeySuffix, and the run directory `<results>/<key>.palace/`, which is KEPT — the script, the mesh,
// the groups, the configuration and the solver's log and CSV — so a user can re-run it by hand and F2's
// viewer can read its fields. A planar result's path does not move by one byte.

using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Results;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Em3d;

public static class Em3dRunService
{
    /// <summary>The diagnostics group a Palace <c>.npy</c> carries beside S.</summary>
    public const string PalaceGroup = "palace";

    /// <summary>The token a solver adds to a result's name — <c>palace</c>, <c>openems</c>.</summary>
    public static string SolverToken(Em3dSolver solver) => solver switch
    {
        Em3dSolver.Palace  => "palace",
        Em3dSolver.OpenEms => "openems",
        _                  => solver.ToString().ToLowerInvariant(),
    };

    /// <summary>R-em3d7-5a — a 3D result's stem: the planar stem and the solver.</summary>
    public static string ResultKey(EmSetup setup, Em3dSolver solver)
        => EmRunService.ResolveResultKey(setup) + "." + SolverToken(solver);

    /// <summary>The <c>.npy</c>'s key: <see cref="ResultKey"/> + <see cref="EmRunService.NpyKeySuffix"/>.</summary>
    public static string NpyKey(EmSetup setup, Em3dSolver solver) => ResultKey(setup, solver) + EmRunService.NpyKeySuffix;

    /// <summary>The run directory, kept after the run. <c>-o</c> does not move it (R-em3d7-5c).</summary>
    public static string RunDirectory(string resultsRoot, EmSetup setup, Em3dSolver solver)
        => Path.Combine(resultsRoot, ResultKey(setup, solver));

    /// <summary>The Touchstone's path without its <c>.sNp</c> suffix: the override when the setup has
    /// one (<c>-o</c> moves the Touchstone only, as for planar), the solver-named stem otherwise.</summary>
    public static string SnpBasePath(string resultsRoot, EmSetup setup, Em3dSolver solver)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup)
            : Path.Combine(resultsRoot, ResultKey(setup, solver));

    /// <summary>
    /// The run. Only <see cref="EmRunService.Run"/> calls this. <paramref name="source"/> has a
    /// technology — the door checked it.
    /// </summary>
    internal static EmRunResult Run(EmSetup setup, EmLayoutSource source, string resultsRoot,
                                    CancellationToken ct, RunControl? control, int? maxCores)
    {
        var warnings = new List<string>();
        var notes    = new List<string>();
        var errors   = new List<string>();

        EmRunResult Refused(Diagnostic d) =>
            new(EmRunStatus.Refused, null, null, null, null, null, d.Render(), warnings,
                Notes: notes, Errors: errors, Diagnostic: d);
        EmRunResult Failed(Diagnostic d) =>
            new(EmRunStatus.EngineError, null, null, null, null, null, d.Render(), warnings,
                Notes: notes, Errors: errors, Diagnostic: d);
        EmRunResult Cancelled()
        {
            var c = EmDiagnostics.Cancelled();
            return new(EmRunStatus.Cancelled, null, null, null, null, null, c.Render(), warnings,
                       Notes: notes, Errors: errors, Diagnostic: c);
        }

        // ── brief 6: every program found, validated and probed, before anything else ─────────
        var readiness = SolverDiscovery.ReadinessFor(setup.Solver3D);
        if (readiness.FirstOrDefault(r => !r.Proceeds) is { } blocked)
            return Refused(EmDiagnostics.SolverUnavailable(blocked.Name, blocked.Refusal!));
        if (setup.Solver3D == Em3dSolver.OpenEms)
            return RunOpenEms(setup, source, resultsRoot, ct, control, maxCores, readiness);
        if (setup.Solver3D != Em3dSolver.Palace)
            return Refused(EmDiagnostics.ThreeDSolverNotBuilt(setup.Solver3D.ToString()));

        var palace = readiness.Single(r => r.Tool == SolverTool.Palace).Installation!;
        var gmsh   = readiness.Single(r => r.Tool == SolverTool.Gmsh).Installation!;
        notes.Add($"Solver: Palace {palace.DescribeVersion()} at {palace.Path}; mesher: Gmsh " +
                  $"{gmsh.DescribeVersion()} at {gmsh.Path}.");

        var settings = PalaceSettings.Resolve(setup.Palace);
        if (settings.Problems() is { Count: > 0 } bad)
            return Refused(EmDiagnostics.Forwarded("palace-settings", string.Join(" ", bad)));

        // ── brief 3: the problem ──────────────────────────────────────────────────────────────
        control?.BeginStage("building the 3D problem");
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        notes.AddRange(generated.Notes);
        warnings.AddRange(generated.Warnings);
        if (!generated.Ok) return Refused(EmDiagnostics.Forwarded("em3d-problem", generated.Refusal));
        var problem = generated.Problem!;
        if (problem.Validate() is { Count: > 0 } invalid)
            return Refused(EmDiagnostics.Forwarded("em3d-problem", string.Join(" ", invalid)));

        // ── the lowering ──────────────────────────────────────────────────────────────────────
        var lowering = GmshGeoWriter.Write(problem, settings);
        if (!lowering.Ok) return Refused(EmDiagnostics.Forwarded("palace-lowering", lowering.Refusal));
        var config = PalaceConfigWriter.Write(problem, lowering.Groups, settings);
        if (!config.Ok) return Refused(EmDiagnostics.Forwarded("palace-lowering", config.Refusal));

        // F0 Q6 — the void model is the flat-surface impedance: low by up to 10 % on a conductor whose
        // radius is under about ten skin depths at the bottom of the band.
        if (ThinRoundConductors(problem) is { Count: > 0 } thin)
            notes.Add($"{string.Join(", ", thin.Select(n => $"'{n}'"))} {(thin.Count == 1 ? "is" : "are")} round and under ten " +
                      $"skin depths in radius at {Fmt(problem.Frequency.StartHz / 1e9)} GHz. Palace models a conductor's " +
                      "loss as a flat surface's, which on such a wire reads the resistance low by up to about 10 % at the " +
                      "bottom of the band (3 % at 10 GHz on a 1 mil wire); inductance is not affected.");

        string runDir = RunDirectory(resultsRoot, setup, Em3dSolver.Palace);
        try { Directory.CreateDirectory(runDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(EmDiagnostics.SolveFailed($"the run directory '{runDir}' could not be created ({e.Message})."));
        }

        // ── Gmsh, then the entity check ───────────────────────────────────────────────────────
        PalaceStep meshed;
        try { meshed = PalaceRun.Mesh(runDir, lowering, gmsh.Path, control, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(EmDiagnostics.SolveFailed($"the mesh could not be staged in '{runDir}' ({e.Message})."));
        }
        if (meshed.Cancelled) return Cancelled();
        if (meshed.Refused) return Refused(EmDiagnostics.Forwarded("palace-mesh", meshed.Message));
        if (!meshed.Ok) return Failed(EmDiagnostics.SolveFailed(meshed.Message!));
        notes.Add(meshed.Reused
            ? $"The geometry script is unchanged since the last run, so its mesh was reused ({runDir})."
            : $"Meshed with Gmsh in {runDir}.");

        // ── Palace ────────────────────────────────────────────────────────────────────────────
        int processes = maxCores ?? EmSolveCores.ProcessorCount;
        PalaceStep solved;
        string? mpiNote;
        try { solved = PalaceRun.Solve(runDir, config.Json!, palace.Path, processes, control, ct, out mpiNote); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(EmDiagnostics.SolveFailed($"Palace could not be staged in '{runDir}' ({e.Message})."));
        }
        if (mpiNote is not null) notes.Add(mpiNote);
        if (solved.Cancelled) return Cancelled();
        if (!solved.Ok) return Failed(EmDiagnostics.SolveFailed(solved.Message!));

        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        var s = PalaceRun.ReadPortS(Path.Combine(post, PalaceRun.PortSFile), [.. ports.Select(p => p.Number)], out string? readError);
        if (readError is not null) return Failed(EmDiagnostics.SolveFailed(readError));
        var facts = PalaceRun.ReadFacts(post);
        notes.Add($"Palace solved {s.FrequenciesHz.Length} frequencies on {Count(facts.FinalElements)} elements " +
                  $"({Count(facts.InitialElements)} initially, {facts.AdaptiveIterations ?? 0} adaptive pass(es)), " +
                  $"{Count(facts.DegreesOfFreedom)} unknowns, {processes} process(es).");

        // ── The DataSet: S and Z0 in the house convention, plus Palace's own record ─────────────
        var data = BuildDataSet(s, ports, facts);

        string snpBase = SnpBasePath(resultsRoot, setup, Em3dSolver.Palace);
        string? snpPath = snpBase + $".s{ports.Count}p";
        string? npyPath = null;
        try
        {
            var written = ResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot,
                NpyKey(setup, Em3dSolver.Palace), data);
            if (written.Error is { } writeError)
                errors.Add($"The EM result could not be written to results/: {writeError}");
            npyPath = written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception e)
        {
            errors.Add($"The EM result could not be written to results/: {e.Message}");
        }

        string? snpError;
        try { snpError = WriteSnp(data, snpBase, setup, problem, lowering, settings, palace, facts); }
        catch (Exception e) { snpError = e.Message; }
        if (snpError is not null)
        {
            errors.Add($"The .snp could not be written to '{snpPath}': {snpError}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, data, null, null, npyPath, snpPath, null, warnings,
                               Notes: notes, Errors: errors, KernelName: "Palace " + palace.DescribeVersion());
    }

    // ── openEMS (brief-em3d-9) ────────────────────────────────────────────────────────────────

    /// <summary>The diagnostics group an openEMS <c>.npy</c> carries beside S.</summary>
    public const string OpenEmsGroup = "openems";

    /// <summary>
    /// R-em3d9-3e: openEMS's own time step against brief 8's Courant estimate. Outside this band the
    /// grid or the estimate is wrong, and the run says so.
    /// </summary>
    public const double TimeStepRatioLow = 0.5, TimeStepRatioHigh = 1.0;

    private static EmRunResult RunOpenEms(EmSetup setup, EmLayoutSource source, string resultsRoot,
                                          CancellationToken ct, RunControl? control, int? maxCores,
                                          IReadOnlyList<SolverReadiness> readiness)
    {
        var warnings = new List<string>();
        var notes    = new List<string>();
        var errors   = new List<string>();
        EmRunResult Refused(Diagnostic d) =>
            new(EmRunStatus.Refused, null, null, null, null, null, d.Render(), warnings,
                Notes: notes, Errors: errors, Diagnostic: d);
        EmRunResult Failed(Diagnostic d) =>
            new(EmRunStatus.EngineError, null, null, null, null, null, d.Render(), warnings,
                Notes: notes, Errors: errors, Diagnostic: d);
        EmRunResult Cancelled()
        {
            var c = EmDiagnostics.Cancelled();
            return new(EmRunStatus.Cancelled, null, null, null, null, null, c.Render(), warnings,
                       Notes: notes, Errors: errors, Diagnostic: c);
        }

        var openEms = readiness.Single(r => r.Tool == SolverTool.OpenEms).Installation!;
        notes.Add($"Solver: openEMS {openEms.DescribeVersion()} at {openEms.Path}.");

        var gridSettings = CemOpenEms.ResolveGrid(setup.OpenEms);
        var runSettings  = CemOpenEms.ResolveRun(setup.OpenEms);
        if (gridSettings.Problems().Concat(runSettings.Problems()).ToList() is { Count: > 0 } bad)
            return Refused(EmDiagnostics.Forwarded("openems-settings", string.Join(" ", bad)));

        // ── brief 3: the problem ──────────────────────────────────────────────────────────────
        control?.BeginStage("building the 3D problem");
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        notes.AddRange(generated.Notes);
        warnings.AddRange(generated.Warnings);
        if (!generated.Ok) return Refused(EmDiagnostics.Forwarded("em3d-problem", generated.Refusal));
        var problem = generated.Problem!;
        if (problem.Validate() is { Count: > 0 } invalid)
            return Refused(EmDiagnostics.Forwarded("em3d-problem", string.Join(" ", invalid)));

        // ── brief 8: the grid, then the lowering ──────────────────────────────────────────────
        control?.BeginStage("placing the FDTD grid");
        FdtdGridResult grid;
        try { grid = FdtdGrid.Build(problem, gridSettings); }
        catch (InvalidOperationException e) { return Failed(EmDiagnostics.SolveFailed($"the FDTD grid could not be built ({e.Message})")); }
        if (grid.Refusal is { } tooBig) return Refused(EmDiagnostics.Forwarded("openems-grid", tooBig));
        warnings.AddRange(grid.Warnings);
        notes.AddRange(grid.Merges.Select(m => m.Sentence));

        var lowering = CsxcadWriter.Write(problem, grid, gridSettings, runSettings);
        if (!lowering.Ok) return Refused(EmDiagnostics.Forwarded("openems-lowering", lowering.Refusal));
        notes.AddRange(lowering.Notes);
        int n = lowering.Ports.Count;
        var s0 = grid.Smallest;
        notes.Add($"openEMS grid: {grid.X.Lines.Count:N0} × {grid.Y.Lines.Count:N0} × {grid.Z.Lines.Count:N0} = {grid.Cells:N0} " +
                  $"cells; smallest cell {FdtdGrid.FormatLength(s0.SmallestCellM)} on {FdtdGrid.AxisName(s0.Axis)}, set by " +
                  $"{string.Join("; ", s0.SmallestCellFeatures.Select(f => f.Describe(s0.Axis)))}. openEMS runs once per port: " +
                  $"{n} run{(n == 1 ? "" : "s")}, each up to {lowering.MaxTimeSteps:N0} time steps.");

        string runDir = RunDirectory(resultsRoot, setup, Em3dSolver.OpenEms);
        try { OpenEmsRun.Stage(runDir, lowering); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(EmDiagnostics.SolveFailed($"the openEMS run could not be staged in '{runDir}' ({e.Message})."));
        }

        // ── One run per port (R-em3d9-3a) ─────────────────────────────────────────────────────
        int? threads = maxCores is { } c ? EmSolveCores.Sanitise(c) : null;
        var runs = new List<OpenEmsPortRun>();
        for (int k = 0; k < n; k++)
        {
            OpenEmsPortRun r;
            try
            {
                r = OpenEmsRun.RunPort(runDir, lowering, k, openEms.Path, threads, runSettings.EndCriterionDb,
                                       grid.ExcitationS, control, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(EmDiagnostics.SolveFailed($"openEMS could not be run in '{runDir}' ({e.Message})."));
            }
            if (r.Cancelled) return Cancelled();
            if (!r.Ok) return Failed(EmDiagnostics.SolveFailed(r.Message!));
            runs.Add(r);

            var facts = r.Facts!;
            if (!r.Converged)
                warnings.Add($"openEMS's run exciting port {r.Port} stopped at its limit of {lowering.MaxTimeSteps:N0} time steps" +
                             (double.IsNaN(r.DecayDb) ? ", before the excitation pulse and one pulse length after it had passed"
                                                      : $" with the port signals {Db(r.DecayDb)} below their peak") +
                             (facts.EnergyDb is { } e0 ? $" and the field energy {Db(e0)} below its own" : "") +
                             $", against an end criterion of {Db(runSettings.EndCriterionDb)}. It has NOT converged: the " +
                             "result is written, and the energy still ringing in the structure is missing from it. Raise the " +
                             "openEMS section's MaxTimeSteps.");
            if (facts.TimeStepS is { } dt && grid.TimeStepEstimateS > 0)
            {
                double ratio = dt / grid.TimeStepEstimateS;
                if (ratio < TimeStepRatioLow || ratio > TimeStepRatioHigh)
                    warnings.Add($"openEMS chose a time step of {dt:G4} s, {ratio:G3} times circuitRF's Courant estimate of " +
                                 $"{grid.TimeStepEstimateS:G4} s — outside {TimeStepRatioLow}–{TimeStepRatioHigh}. The answer " +
                                 "is still openEMS's; the estimate circuitRF reports before a run is what disagrees.");
            }
        }

        // ── The transform (R-em3d9-4) ────────────────────────────────────────────────────────
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        double[] freqs = FrequenciesHz(problem.Frequency);
        var result = FdtdPortTransform.Solve([.. runs.Select(r => r.Probes!)], freqs, lowering.Ports,
                                             [.. ports.Select(p => p.Z0)]);
        if (result.Error is { } singular) return Failed(EmDiagnostics.SolveFailed(singular));

        var data = BuildOpenEmsDataSet(result, ports, grid, runs, runSettings);

        string snpBase = SnpBasePath(resultsRoot, setup, Em3dSolver.OpenEms);
        string? snpPath = snpBase + $".s{ports.Count}p";
        string? npyPath = null;
        try
        {
            var written = ResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot,
                NpyKey(setup, Em3dSolver.OpenEms), data);
            if (written.Error is { } writeError)
                errors.Add($"The EM result could not be written to results/: {writeError}");
            npyPath = written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception e)
        {
            errors.Add($"The EM result could not be written to results/: {e.Message}");
        }

        string? snpError;
        try { snpError = WriteOpenEmsSnp(data, snpBase, setup, problem, grid, gridSettings, runSettings, lowering, openEms, runs); }
        catch (Exception e) { snpError = e.Message; }
        if (snpError is not null)
        {
            errors.Add($"The .snp could not be written to '{snpPath}': {snpError}");
            snpPath = null;
        }

        return new EmRunResult(EmRunStatus.Ok, data, null, null, npyPath, snpPath, null, warnings,
                               Notes: notes, Errors: errors, KernelName: "openEMS " + openEms.DescribeVersion());
    }

    /// <summary>The sweep's frequencies, as Palace's <c>NSample</c> spaces them: both ends included.</summary>
    public static double[] FrequenciesHz(Em3dFrequency f)
    {
        if (f.Points <= 1 || f.StopHz == f.StartHz) return [f.StartHz];
        var v = new double[f.Points];
        for (int k = 0; k < f.Points; k++)
        {
            double a = (double)k / (f.Points - 1);
            v[k] = f.Kind == Em3dSweepKind.Log
                ? f.StartHz * Math.Pow(f.StopHz / f.StartHz, a)
                : f.StartHz + (f.StopHz - f.StartHz) * a;
        }
        v[^1] = f.StopHz;
        return v;
    }

    private static DataSet BuildOpenEmsDataSet(FdtdPortResult r, IReadOnlyList<Em3dPort> ports, FdtdGridResult grid,
                                               IReadOnlyList<OpenEmsPortRun> runs, OpenEmsRunSettings settings)
    {
        var z0 = ports.Select(p => p.Z0).ToArray();
        var snp = new SNP(r.FrequenciesHz, r.S, MatrixType.S, MatrixFormat.RI, z0[0]);
        var ds = DataSetBuilder.FromSnp(snp);
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0));

        void Scalar(string name, double? v)
        {
            if (v is { } x && double.IsFinite(x)) ds.AddToGroup(OpenEmsGroup, name, DataCube.Scalar(x));
        }
        Scalar("GridCells", grid.Cells);
        Scalar("GridLinesX", grid.X.Lines.Count);
        Scalar("GridLinesY", grid.Y.Lines.Count);
        Scalar("GridLinesZ", grid.Z.Lines.Count);
        Scalar("SmallestCell", grid.Smallest.SmallestCellM);
        Scalar("TimeStepEstimate", grid.TimeStepEstimateS);
        Scalar("EndCriterionDb", settings.EndCriterionDb);
        foreach (var run in runs)
        {
            Scalar($"TimeStep_p{run.Port}", run.Facts?.TimeStepS);
            Scalar($"StepsRun_p{run.Port}", run.Facts?.StepsRun);
            Scalar($"PortDecayDb_p{run.Port}", run.DecayDb);
            Scalar($"EnergyDb_p{run.Port}", run.Facts?.EnergyDb);
            Scalar($"Converged_p{run.Port}", run.Converged ? 1 : 0);
        }
        return ds;
    }

    /// <summary>R-em3d9-5b — the planar exporter with the 3D stamp and openEMS's provenance lines.</summary>
    private static string? WriteOpenEmsSnp(DataSet data, string snpBase, EmSetup setup, Em3dProblem problem, FdtdGridResult grid,
                                           OpenEmsGridSettings gridSettings, OpenEmsRunSettings runSettings, CsxcadLowering lowering,
                                           SolverInstallation openEms, IReadOnlyList<OpenEmsPortRun> runs)
    {
        string? group = data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S"));
        if (group is null) return "the solved DataSet carries no S cube";

        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var portText = new StringBuilder();
        foreach (var p in problem.Ports)
            portText.Append($"{p.Number}:{p.PositiveObject}:{p.NegativeObject}:{R(p.Min.X)},{R(p.Min.Y)},{R(p.Min.Z)}:" +
                            $"{R(p.Max.X)},{R(p.Max.Y)},{R(p.Max.Z)}:{R(p.Z0.Real)},{R(p.Z0.Imaginary)}|");
        string settingsText = string.Join("|", R(gridSettings.CellsPerWavelength), R(gridSettings.GradingRatio),
            gridSettings.ThirdsRule, gridSettings.MinCellM is { } mc ? R(mc) : "auto", gridSettings.PmlCells,
            R(runSettings.EndCriterionDb), lowering.MaxTimeSteps);

        var s0 = grid.Smallest;
        var lines = new List<string>();
        foreach (var p in problem.Ports.OrderBy(p => p.Number))
            lines.Add($"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{Ascii(p.Name)}' from '{Ascii(p.NegativeObject)}' " +
                      $"to '{Ascii(p.PositiveObject)}', lumped, {R(p.Z0.Real)} Ohm");
        lines.Add($"circuitRF-EM 3D grid: {grid.X.Lines.Count} x {grid.Y.Lines.Count} x {grid.Z.Lines.Count} = {grid.Cells} cells; " +
                  $"smallest cell {Ascii(FdtdGrid.FormatLength(s0.SmallestCellM))} on {FdtdGrid.AxisName(s0.Axis)}, set by " +
                  Ascii(string.Join("; ", s0.SmallestCellFeatures.Select(f => f.Describe(s0.Axis)))));
        foreach (var run in runs)
        {
            var f = run.Facts!;
            lines.Add($"circuitRF-EM 3D openEMS run port {run.Port}: dt {(f.TimeStepS is { } dt ? dt.ToString("G6", CultureInfo.InvariantCulture) : "unreported")} s " +
                      $"(estimate {grid.TimeStepEstimateS.ToString("G6", CultureInfo.InvariantCulture)} s), " +
                      $"{(f.StepsRun is { } st ? st.ToString(CultureInfo.InvariantCulture) : "an unreported number of")} steps, " +
                      $"port signals {(double.IsNaN(run.DecayDb) ? "unmeasured (stopped within the pulse)" : Db(run.DecayDb))} and energy {(f.EnergyDb is { } e ? Db(e) : "unreported")} against {Db(runSettings.EndCriterionDb)}, " +
                      (f.Aborted ? "stopped by circuitRF on the ports' decay, converged"
                       : f.EnergyCriterionMet ? "stopped by openEMS's energy criterion, converged"
                       : run.Converged ? "converged" : "stopped at the step limit, NOT converged"));
        }
        lines.Add($"circuitRF-EM 3D dielectric loss fitted at: {R(lowering.DielectricFitHz / 1e9)} GHz");
        lines.Add("circuitRF-EM 3D perfect conductors: " + (lowering.PecSolids.Count == 0 ? "none" : string.Join(", ", lowering.PecSolids.Select(Ascii))));
        if (lowering.SubCellWires.Count > 0)
            lines.Add("circuitRF-EM 3D sub-cell wires: " + string.Join(", ", lowering.SubCellWires.Select(Ascii)));
        lines.Add($"circuitRF-EM 3D operating temperature: {R(problem.OperatingTempC)} C");

        double z0 = problem.Ports.OrderBy(p => p.Number).First().Z0.Real;
        var opts = new TouchstoneExportOptions(
            Z0Ohms:         z0 > 0 ? z0 : 50,
            Digits:         10,
            DigitFormat:    'g',
            MatrixFormat:   MatrixFormat.RI,
            HeaderComments: EmSnpProvenance.BuildHeader3D(
                "openEMS",
                EmSnpProvenance.HashText(lowering.Model!),
                EmSnpProvenance.HashText(settingsText),
                EmSnpProvenance.HashText(portText.ToString()),
                "openEMS " + openEms.DescribeVersion(),
                lines,
                setup.Name is { Length: > 0 } nm ? nm : Path.GetFileNameWithoutExtension(snpBase),
                setup.SolveRegion is { } reg ? $"{setup.LayoutRef} (solve region {Ascii(reg.Describe())})" : setup.LayoutRef,
                DateTimeOffset.Now));

        Directory.CreateDirectory(Path.GetDirectoryName(snpBase)!);
        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBase);
        return result.Status == TouchstoneExportStatus.Ok ? null : $"Touchstone export returned {result.Status}.";
    }

    private static string Db(double db) => double.IsNegativeInfinity(db) ? "over 300 dB"
                                           : $"{Math.Abs(db).ToString("0.#", CultureInfo.InvariantCulture)} dB";

    // ── Results ──────────────────────────────────────────────────────────────────────────────

    private static DataSet BuildDataSet(PalacePortS s, IReadOnlyList<Em3dPort> ports, PalaceRunFacts facts)
    {
        int nf = s.FrequenciesHz.Length, np = ports.Count;
        var mats = new Mat<System.Numerics.Complex>[nf];
        for (int k = 0; k < nf; k++)
        {
            var m = new Mat<System.Numerics.Complex>(np, np);
            for (int i = 0; i < np; i++)
                for (int j = 0; j < np; j++)
                    m[i, j] = s.S[k][i, j];
            mats[k] = m;
        }
        var z0 = ports.Select(p => p.Z0).ToArray();
        var snp = new SNP(s.FrequenciesHz, mats, MatrixType.S, MatrixFormat.RI, z0[0]);
        var ds = DataSetBuilder.FromSnp(snp);
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0));

        // What the solver itself reported — the part that makes a surprising answer diagnosable.
        void Scalar(string name, double? v)
        {
            if (v is { } x) ds.AddToGroup(PalaceGroup, name, DataCube.Scalar(x));
        }
        Scalar("MeshElementsInitial", facts.InitialElements);
        Scalar("MeshElementsFinal",   facts.FinalElements);
        Scalar("DegreesOfFreedom",    facts.DegreesOfFreedom);
        Scalar("AdaptiveIterations",  facts.AdaptiveIterations);
        return ds;
    }

    /// <summary>The planar exporter and options, with the planar stamp extended (R-em3d7-5d). Null
    /// when written; otherwise why not.</summary>
    private static string? WriteSnp(DataSet data, string snpBase, EmSetup setup, Em3dProblem problem,
                                 GmshLowering lowering, PalaceSettings settings, SolverInstallation palace,
                                 PalaceRunFacts facts)
    {
        string? group = data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S"));
        if (group is null) return "the solved DataSet carries no S cube";

        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var portText = new StringBuilder();
        foreach (var p in problem.Ports)
            portText.Append($"{p.Number}:{p.PositiveObject}:{p.NegativeObject}:{R(p.Min.X)},{R(p.Min.Y)},{R(p.Min.Z)}:" +
                            $"{R(p.Max.X)},{R(p.Max.Y)},{R(p.Max.Z)}:{R(p.Z0.Real)},{R(p.Z0.Imaginary)}|");
        string settingsText = string.Join("|", R(settings.MaxElementWavelengths), R(settings.EdgeRefinement),
            R(settings.Grading), settings.ElementOrder, R(settings.AdaptiveTol), settings.AdaptiveMaxIterations,
            R(settings.SweepAdaptiveTol));
        string materialsText = string.Join("|", problem.Materials.Select(m =>
            $"{m.Name}:{R(m.Epsr)}:{R(m.TanD)}:{R(m.Mur)}:{R(m.SigmaSm)}"));

        var lines = new List<string>();
        foreach (var p in problem.Ports.OrderBy(p => p.Number))
            lines.Add($"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{Ascii(p.Name)}' from '{Ascii(p.NegativeObject)}' " +
                      $"to '{Ascii(p.PositiveObject)}', lumped, {R(p.Z0.Real)} Ohm");
        lines.Add($"circuitRF-EM 3D mesh: {Count(facts.InitialElements)} elements initially, {Count(facts.FinalElements)} " +
                  $"finally, {facts.AdaptiveIterations ?? 0} adaptive pass(es), element order {settings.ElementOrder}");
        lines.Add($"circuitRF-EM 3D operating temperature: {R(problem.OperatingTempC)} C");

        double z0 = problem.Ports.OrderBy(p => p.Number).First().Z0.Real;
        var opts = new TouchstoneExportOptions(
            Z0Ohms:         z0 > 0 ? z0 : 50,
            Digits:         10,
            DigitFormat:    'g',
            MatrixFormat:   MatrixFormat.RI,
            HeaderComments: EmSnpProvenance.BuildHeader3D(
                "Palace",
                EmSnpProvenance.HashText(lowering.Geo! + "\n" + materialsText),
                EmSnpProvenance.HashText(settingsText),
                EmSnpProvenance.HashText(portText.ToString()),
                "Palace " + palace.DescribeVersion(),
                lines,
                setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(snpBase),
                setup.SolveRegion is { } r ? $"{setup.LayoutRef} (solve region {Ascii(r.Describe())})" : setup.LayoutRef,
                DateTimeOffset.Now));

        Directory.CreateDirectory(Path.GetDirectoryName(snpBase)!);
        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBase);
        return result.Status == TouchstoneExportStatus.Ok ? null : $"Touchstone export returned {result.Status}.";
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Round conductors — vias and round wires — whose radius is under ten skin depths at the
    /// bottom of the band (F0 Q6's threshold for the void model's curvature error).</summary>
    private static List<string> ThinRoundConductors(Em3dProblem problem)
    {
        const double Mu0 = 4e-7 * Math.PI;
        var sigma = problem.Materials.ToDictionary(m => m.Name, m => (m.SigmaSm, m.Mur), StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var solid in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            double? radius = solid.Primitive switch
            {
                Em3dSweep { Section: Em3dSection.Circle } w => w.Diameter / 2,
                Em3dCylinder c => c.Radius,
                _ => null,
            };
            if (radius is not { } r || !sigma.TryGetValue(solid.Material, out var m) || !(m.SigmaSm > 0)) continue;
            double delta = 1 / Math.Sqrt(Math.PI * problem.Frequency.StartHz * Mu0 * m.Mur * m.SigmaSm);
            if (r < 10 * delta) names.Add(solid.Name);
        }
        return names;
    }

    private static string Count(long? v) => v is { } x ? x.ToString("N0", CultureInfo.InvariantCulture) : "an unreported number of";

    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    private static string Ascii(string s)
        => new(s.Replace("µm", "um").Replace("–", "-").Replace("×", "x").Select(ch => ch < 128 ? ch : '?').ToArray());
}
