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
// Both (brief-em3d-10) generates the problem ONCE, lowers it for both backends before either starts,
// runs Palace and then openEMS, and writes Em3dComparison's DataSet (src/Engine) beside the two
// results as `<key>.compare_em.npy`. See "The run" below for the three phases and why the refusals
// all come first.
//
// WHERE RESULTS LAND (R-em3d7-5, em-3d.md §4.5 — this brief is the first backend built, so it owns the
// rule and brief 9 reuses it): the solver is part of a 3D result's name, so running a second solver on
// one setup never overwrites the first. `<key>.palace.sNp`, the `.npy` key `<key>.palace` +
// NpyKeySuffix, and the run directory `<results>/<key>.palace/`, which is KEPT — the script, the mesh,
// the groups, the configuration and the solver's log and CSV — so a user can re-run it by hand and F2's
// viewer can read its fields. A planar result's path does not move by one byte.

using System.Globalization;
using System.Numerics;
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

    /// <summary>The <c>.npy</c>'s key: <see cref="ResultKey"/> + <see cref="EmRunService.NpyKeySuffix"/> —
    /// or, for a static setup, + <c>_es</c> / <c>_ms</c> (brief-em3d-22 R-em3d22-3c): a matrix is not S,
    /// and a setup switched between the two keeps both results.</summary>
    public static string NpyKey(EmSetup setup, Em3dSolver solver)
        => ResultKey(setup, solver) + (StaticSuffix(setup) is { Length: > 0 } st ? st : EmRunService.NpyKeySuffix);

    /// <summary>The run directory, kept after the run. <c>-o</c> does not move it (R-em3d7-5c). A static
    /// setup's is its own, <c>&lt;key&gt;.palace_es</c>, beside the driven one.</summary>
    public static string RunDirectory(string resultsRoot, EmSetup setup, Em3dSolver solver)
        => Path.Combine(resultsRoot, ResultKey(setup, solver) + StaticSuffix(setup));

    /// <summary>brief-em3d-22 — <c>_es</c>, <c>_ms</c>; brief-em3d-23 — <c>_eig</c>; empty for a driven setup.</summary>
    public static string StaticSuffix(EmSetup setup) => setup.Problem3D switch
    {
        Em3dProblemType.Electrostatic => "_es",
        Em3dProblemType.Magnetostatic => "_ms",
        Em3dProblemType.Eigenmode     => "_eig",
        _ => "",
    };

    /// <summary>
    /// brief-em3d-22 R-em3d22-1b — a static problem runs on Palace only; on openEMS or Both it is refused
    /// naming Palace, before anything is looked for or started. <c>check</c> reports the same sentence.
    /// </summary>
    public static string? StaticSolverRefusal(EmSetup setup)
        => setup.IsStatic3D && setup.Solver3D is Em3dSolver.OpenEms or Em3dSolver.Both
            ? $"This setup asks for a{(setup.Problem3D == Em3dProblemType.Electrostatic ? "n electrostatic" : " magnetostatic")} " +
              $"solve on {(setup.Solver3D == Em3dSolver.Both ? "both solvers" : "openEMS")}, and only Palace solves the static " +
              "problems: openEMS is a time-domain solver and has no static one. Set the setup's Solver3D to Palace, or run it " +
              "with `circuitrf em --solver palace`."
            : null;

    /// <summary>
    /// brief-em3d-23 R-em3d23-2e / §7 — what only Palace does: the static problems (brief 22), an eigenmode
    /// solve (FDTD has no eigensolver) and a wave port (openEMS's own waveguide and microstrip ports are a
    /// later brief). On openEMS or Both it is refused naming Palace, before anything is looked for.
    /// </summary>
    public static string? PalaceOnlyRefusal(EmSetup setup)
    {
        if (StaticSolverRefusal(setup) is { } staticOnly) return staticOnly;
        if (setup.Solver3D is not (Em3dSolver.OpenEms or Em3dSolver.Both)) return null;
        string on = setup.Solver3D == Em3dSolver.Both ? "both solvers" : "openEMS";
        const string remedy = "Set the setup's Solver3D to Palace, or run it with `circuitrf em --solver palace`.";
        if (setup.Problem3D == Em3dProblemType.Eigenmode)
            return $"This setup asks for an eigenmode solve on {on}, and only Palace finds eigenmodes: openEMS is a " +
                   "time-domain (FDTD) solver and has no eigensolver. " + remedy;
        if (setup.HasWavePorts3D && !setup.IsStatic3D)
        {
            var wave = setup.Ports3D.Where(p => p.Kind == Em3dPortKind.Wave).Select(p => p.Port).Distinct().Order().ToList();
            return $"Port{(wave.Count == 1 ? "" : "s")} {string.Join(", ", wave)} {(wave.Count == 1 ? "is a wave port" : "are wave ports")}, " +
                   $"and only Palace builds wave ports in this version (openEMS's waveguide and microstrip ports are a later " +
                   $"addition), so this setup cannot run on {on}. {remedy} Or make the port{(wave.Count == 1 ? "" : "s")} lumped.";
        }
        return null;
    }

    /// <summary>The Touchstone's path without its <c>.sNp</c> suffix: the override when the setup has
    /// one (<c>-o</c> moves the Touchstone only, as for planar), the solver-named stem otherwise.</summary>
    public static string SnpBasePath(string resultsRoot, EmSetup setup, Em3dSolver solver)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup)
            : Path.Combine(resultsRoot, ResultKey(setup, solver));

    // ── The run ──────────────────────────────────────────────────────────────────────────────
    //
    // Three phases, and only the last starts a process:
    //
    //   1. discovery (brief 6), then each backend's settings
    //   2. the problem, generated ONCE (R-em3d10-1a), then each backend's lowering — Palace's .geo
    //      and configuration, openEMS's grid and CSXCAD model
    //   3. execution: Gmsh + Palace, or openEMS once per port — or both, Palace first (R-em3d10-1c)
    //
    // Everything a backend can refuse is refused in 1 or 2, so a setup asking for both solvers learns
    // that one of them cannot run before the other has spent half an hour (R-em3d10-4b), and is told
    // the flag that runs the one that can. Phase 3 is where R-em3d10-4a applies: a solver failing
    // THERE leaves the other's result written.

    /// <summary>How many times a run in this process has generated its 3D problem — R-em3d10-1a's
    /// counter. A run through both solvers generates once and hands the one problem to both.</summary>
    public static long ProblemsGenerated => Interlocked.Read(ref _problemsGenerated);
    private static long _problemsGenerated;

    /// <summary>The token a both-run's comparison adds to the result stem (R-em3d10-2c).</summary>
    public const string CompareToken = "compare";

    /// <summary>R-em3d10-2c — the comparison's key in <c>results/</c>: <c>&lt;key&gt;.compare_em</c>.</summary>
    public static string CompareNpyKey(EmSetup setup)
        => EmRunService.ResolveResultKey(setup) + "." + CompareToken + EmRunService.NpyKeySuffix;

    /// <summary>
    /// R-em3d10-5 — one solver's Touchstone stem in a both-run. <c>-o base</c> gives
    /// <c>base.palace</c> and <c>base.openems</c>, since one path cannot hold two results; with no
    /// <c>-o</c> it is the single-solver stem, <c>results/&lt;key&gt;.&lt;solver&gt;</c>.
    /// </summary>
    public static string BothSnpBasePath(string resultsRoot, EmSetup setup, Em3dSolver solver)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup) + "." + SolverToken(solver)
            : Path.Combine(resultsRoot, ResultKey(setup, solver));

    /// <summary>R-em3d10-2c/5 — where a both-run's comparison lands: <c>base.compare_em.npy</c> with
    /// <c>-o base</c>, <c>results/&lt;key&gt;.compare_em.npy</c> beside the two results otherwise.</summary>
    public static string ComparePath(string resultsRoot, EmSetup setup)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup) + "." + CompareToken + EmRunService.NpyKeySuffix + ".npy"
            : Path.Combine(resultsRoot, CompareNpyKey(setup) + ".npy");

    /// <summary>
    /// The run. Only <see cref="EmRunService.Run"/> calls this. <paramref name="source"/> has a
    /// technology — the door checked it.
    /// </summary>
    /// <param name="confirmMemory">brief-em3d-21 R-em3d21-2b — see <see cref="EmRunService.Run"/>.</param>
    internal static EmRunResult Run(EmSetup setup, EmLayoutSource source, string resultsRoot,
                                    CancellationToken ct, RunControl? control, int? maxCores,
                                    Func<string, bool>? confirmMemory = null)
    {
        var log = new RunLog();
        var memory = new MemoryGate(confirmMemory);
        var solver = setup.Solver3D;
        bool both = solver == Em3dSolver.Both;

        if (StaticSolverRefusal(setup) is { } staticOnly)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-static", staticOnly));
        if (PalaceOnlyRefusal(setup) is { } palaceOnly)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-palace-only", palaceOnly));

        // ── brief 6: every program found, validated and probed, before anything else ─────────
        // brief-em3d-23 R-em3d23-1c — probed for what THIS setup needs, so a build without an eigensolver
        // is refused here, before Gmsh.
        var readiness = SolverDiscovery.ReadinessFor(solver, setup);
        if (readiness.FirstOrDefault(r => !r.Proceeds) is { } blocked)
        {
            var unavailable = EmDiagnostics.SolverUnavailable(blocked.Name, blocked.Refusal!, Install.SolverInstaller.OfferFor(blocked));
            if (!both) return log.Result(EmRunStatus.Refused, unavailable);
            bool palaceReady  = readiness.Where(r => r.Tool is SolverTool.Palace or SolverTool.Gmsh).All(r => r.Proceeds);
            bool openEmsReady = readiness.Where(r => r.Tool == SolverTool.OpenEms).All(r => r.Proceeds);
            return log.Result(EmRunStatus.Refused, EmDiagnostics.BothRefusedBeforeWork(
                string.Join(" ", readiness.Where(r => !r.Proceeds).Select(r => r.Refusal)),
                palaceReady ? Remedy(Em3dSolver.Palace) : openEmsReady ? Remedy(Em3dSolver.OpenEms) : ""));
        }
        bool palace  = solver is Em3dSolver.Palace or Em3dSolver.Both;
        bool openEms = solver is Em3dSolver.OpenEms or Em3dSolver.Both;
        if (!palace && !openEms) return log.Result(EmRunStatus.Refused, EmDiagnostics.ThreeDSolverNotBuilt(solver.ToString()));

        // brief-em3d-25 R-em3d25-1c — every circuitRF-installed program this run found is held until it
        // returns, here and (by a lock file in its home) in every other process, so Uninstall refuses
        // rather than deleting a solver from under the run. A program circuitRF did not install holds nothing.
        string holder = $"the 3D EM run of '{(setup.Name is { Length: > 0 } n ? n : EmRunService.ResolveResultKey(setup))}'";
        using var inUse = Install.SolverInUse.HoldPrograms(readiness.Where(r => r.Installation?.Distribution is null)
                                                                   .Select(r => r.Installation?.Path), holder);
        // brief-em3d-26 — one installed inside a Linux subsystem distribution is held by its Windows-side mirror.
        using var inUseInSubsystem = readiness.Select(r => r.Installation).OfType<SolverInstallation>()
            .Select(i => Wsl.WslSolverHomes.Hold(Install.SolverHomes.DefaultRoots, i, holder)).OfType<IDisposable>()
            .ToList() is { Count: > 0 } holds ? new Holds(holds) : null;

        // ── each backend's settings ───────────────────────────────────────────────────────────
        Stop? palaceStop = null, openEmsStop = null;
        PalaceSettings? palaceSettings = null;
        // brief-em3d-26 — where Palace runs: here, or inside a Linux subsystem distribution, with that
        // distribution's cores and memory.
        IPalaceRunner palaceRunner = NativePalaceRunner.Instance;
        Em3dMemoryScope? palaceMemory = null;
        OpenEmsGridSettings? gridSettings = null;
        OpenEmsRunSettings? runSettings = null;
        if (palace)
        {
            var p = readiness.Single(r => r.Tool == SolverTool.Palace).Installation!;
            var g = readiness.Single(r => r.Tool == SolverTool.Gmsh).Installation!;
            log.Notes.Add($"Solver: Palace {p.DescribeVersion()} at {p.Where}; mesher: Gmsh {g.DescribeVersion()} at {g.Path}.");
            palaceSettings = PalaceSettings.Resolve(setup.Palace);
            if (p.Distribution is not null)
            {
                if (SolverDiscovery.For(SolverTool.Palace).Subsystem is not { } wsl)
                    palaceStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-subsystem",
                        $"Palace was found in the Linux subsystem distribution '{p.Distribution}', which this machine cannot reach."));
                else if (Wsl.WslPalace.Runner(wsl, p, out string? subsystemRefusal) is { } runner)
                {
                    palaceRunner = runner;
                    palaceMemory = Wsl.WslPalace.MemoryScope(new Wsl.WslSession(wsl, p.Distribution));
                    log.Notes.Add($"Palace runs in the Linux subsystem distribution '{p.Distribution}', staged in its own Linux " +
                                  $"filesystem; the mesh, configuration and results stay in the run directory here. MPI: " +
                                  $"{runner.MpiLauncher ?? "none"} ({(runner.MpiLauncher is null ? "Palace runs on one process" : "inside the distribution")}).");
                }
                else palaceStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-subsystem", subsystemRefusal));
            }
            if (palaceStop is null && palaceSettings.Problems() is { Count: > 0 } bad)
                palaceStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-settings", string.Join(" ", bad)));
            // brief-em3d-21 R-em3d21-3b — an explicit core count past the physical one is refused, never
            // oversubscribed. brief-em3d-26 R-em3d26-2b — the SUBSYSTEM's cores, where Palace runs there.
            else if (palaceStop is null && RankRefusal(maxCores, palaceRunner.Cores) is { } ranks)
                palaceStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-ranks", ranks));
        }
        if (openEms)
        {
            var o = readiness.Single(r => r.Tool == SolverTool.OpenEms).Installation!;
            log.Notes.Add($"Solver: openEMS {o.DescribeVersion()} at {o.Path}.");
            gridSettings = CemOpenEms.ResolveGrid(setup.OpenEms);
            runSettings  = CemOpenEms.ResolveRun(setup.OpenEms);
            if (gridSettings.Problems().Concat(runSettings.Problems()).ToList() is { Count: > 0 } bad)
                openEmsStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-settings", string.Join(" ", bad)));
        }
        // A single solver stops at its first refusal. Both go on while either could still run, so the
        // refusal can say whether the other one would have.
        if ((!both || (palaceStop is not null && openEmsStop is not null)) && (palaceStop ?? openEmsStop) is { } early)
            return both ? BothRefused(palaceStop, openEmsStop, log) : log.Result(early.Status, early.Diagnostic);

        // ── brief 3: the problem, once ────────────────────────────────────────────────────────
        control?.BeginStage("building the 3D problem");
        Interlocked.Increment(ref _problemsGenerated);
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        log.Notes.AddRange(generated.Notes);
        log.Warnings.AddRange(generated.Warnings);
        if (!generated.Ok) return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", generated.Refusal));
        var problem = generated.Problem!;
        if (problem.Validate() is { Count: > 0 } invalid)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", string.Join(" ", invalid)));

        // ── each backend's lowering: no process yet ──────────────────────────────────────────
        PalacePlan? palacePlan = null;
        OpenEmsPlan? openEmsPlan = null;
        if (palace && palaceStop is null)
            palacePlan = PreparePalace(problem, palaceSettings!, readiness, palaceRunner, palaceMemory, log, out palaceStop);
        // brief-em3d-21 R-em3d21-2 — will it fit? A warning, and past 150 % a confirmation; still no process.
        if (palacePlan is not null && palaceStop is null &&
            memory.Admit(PalaceMemoryVerdict(problem, setup, source, palaceSettings!, palaceMemory?.Bytes ?? MachineMemory.PhysicalBytes,
                                             palaceMemory), log) is { } tooBig)
        {
            palaceStop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded(MemoryRefusalSource, tooBig));
            palacePlan = null;
        }
        if (openEms && openEmsStop is null)
            openEmsPlan = PrepareOpenEms(problem, gridSettings!, runSettings!, readiness, control, log, out openEmsStop);
        if ((palaceStop ?? openEmsStop) is { } refused)
            return both ? BothRefused(palaceStop, openEmsStop, log) : log.Result(refused.Status, refused.Diagnostic);

        // ── execution ─────────────────────────────────────────────────────────────────────────
        if (solver == Em3dSolver.Palace)
            return Single(ExecutePalace(palacePlan!, problem, setup, resultsRoot, SnpBasePath(resultsRoot, setup, Em3dSolver.Palace),
                                        ct, control, maxCores, log, memory), log);
        if (solver == Em3dSolver.OpenEms)
            return Single(ExecuteOpenEms(openEmsPlan!, problem, setup, resultsRoot, SnpBasePath(resultsRoot, setup, Em3dSolver.OpenEms),
                                         ct, control, maxCores, log), log);
        return RunBoth(palacePlan!, openEmsPlan!, problem, setup, resultsRoot, ct, control, maxCores, log, memory);
    }

    /// <summary>
    /// R-em3d10-1 — Palace, then openEMS, on the one problem; then the comparison. A failure of one
    /// keeps the other's result (R-em3d10-4a); a cancellation keeps what had already finished and
    /// writes nothing for what was running (R-em3d10-4c).
    /// </summary>
    private static EmRunResult RunBoth(PalacePlan palacePlan, OpenEmsPlan openEmsPlan, Em3dProblem problem, EmSetup setup,
                                       string resultsRoot, CancellationToken ct, RunControl? control, int? maxCores, RunLog log,
                                       MemoryGate memory)
    {
        log.Notes.Add("Both solvers run on the one 3D problem generated above, one after the other — Palace, then " +
                      "openEMS — because each already uses every core.");

        // An earlier run's comparison describes files this run is about to rewrite. Left in place past
        // a failed leg or a refused comparison, it would sit beside the fresh result disagreeing with
        // it; only this run may put one there. This file only, as ResultsWriter deletes only its own.
        try { if (File.Exists(ComparePath(resultsRoot, setup))) File.Delete(ComparePath(resultsRoot, setup)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warnings.Add($"An earlier comparison, '{ComparePath(resultsRoot, setup)}', could not be removed " +
                             $"({e.Message}); it does not describe this run.");
        }

        control?.BeginStage("Palace, the first of two solvers");
        var p = ExecutePalace(palacePlan, problem, setup, resultsRoot, BothSnpBasePath(resultsRoot, setup, Em3dSolver.Palace),
                              ct, control, maxCores, log, memory);
        if (p.Status == EmRunStatus.Cancelled) return log.Result(EmRunStatus.Cancelled, EmDiagnostics.Cancelled());

        control?.BeginStage("openEMS, the second of two solvers");
        var o = ExecuteOpenEms(openEmsPlan, problem, setup, resultsRoot, BothSnpBasePath(resultsRoot, setup, Em3dSolver.OpenEms),
                               ct, control, maxCores, log);

        var outputs = new List<EmRunOutput>();
        foreach (var leg in new[] { p, o }.Where(l => l.Ok))
        {
            if (leg.SnpPath is { } snp) outputs.Add(new("touchstone", snp));
            if (leg.NpyPath is { } npy) outputs.Add(new("npy", npy));
        }

        if (o.Status == EmRunStatus.Cancelled)
        {
            // Palace had already failed: its reason is the other half of what this run found.
            if (!p.Ok && p.Stop is { } palaceStop) log.Errors.Add(palaceStop.Render());
            return log.Result(EmRunStatus.Cancelled,
                              p.Ok ? EmDiagnostics.CancelledKeeping("openEMS", "Palace", Where(p)) : EmDiagnostics.Cancelled(),
                              outputs);
        }
        if (!p.Ok && !o.Ok)
            return log.Result(p.Status, EmDiagnostics.BothSolversFailed(p.Stop!.Render(), o.Stop!.Render()));
        if (!p.Ok || !o.Ok)
        {
            var (bad, good) = p.Ok ? (o, p) : (p, o);
            return log.Result(bad.Status, EmDiagnostics.OneSolverFailed(Name(bad.Solver), bad.Stop!.Render(), Name(good.Solver), Where(good)),
                              outputs);
        }

        // ── the comparison (R-em3d10-2, 3) ────────────────────────────────────────────────────
        var compared = Em3dComparison.Compare(p.Data!["S"], o.Data!["S"], problem, o.Facts!);
        if (compared.Data is not { } data)
            return log.Result(EmRunStatus.EngineError, EmDiagnostics.ComparisonRefused(compared.Refusal!), outputs);

        log.Warnings.AddRange(compared.Warnings);
        log.Notes.InsertRange(0, [compared.Summary!, .. compared.Notes]);     // the line a user reads first

        string path = ComparePath(resultsRoot, setup);
        string? written = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path);                       // this file only, as ResultsWriter does
            DataSetExporter.Export(data, path, ExportFormat.Npy);
            written = Path.GetFullPath(path);
            outputs.Add(new("npy", written));
        }
        catch (Exception e)
        {
            log.Errors.Add($"The comparison could not be written to '{path}': {e.Message}");
        }

        return new EmRunResult(EmRunStatus.Ok, data, null, null, written, null, null, log.Warnings,
                               KernelName: $"{p.KernelName} and {o.KernelName}", Notes: log.Notes, Errors: log.Errors,
                               Outputs: outputs);
    }

    private static string Name(Em3dSolver s) => s == Em3dSolver.Palace ? "Palace" : "openEMS";

    /// <summary>Where a leg's result is, for a sentence naming it.</summary>
    private static string Where(Leg leg)
        => string.Join(" and ", new[] { leg.SnpPath, leg.NpyPath }.OfType<string>()) is { Length: > 0 } w
            ? w : "(its files could not be written; the errors above say why)";

    /// <summary>R-em3d10-4b's pointer to the solver that would run.</summary>
    private static string Remedy(Em3dSolver ready)
        => $" {Name(ready)} is ready: run it alone with `circuitrf em --solver {SolverToken(ready)}`, or set the setup's " +
           $"Solver3D to {ready}.";

    private static EmRunResult BothRefused(Stop? palace, Stop? openEms, RunLog log)
    {
        string text = string.Join(" ", new[] { palace, openEms }.OfType<Stop>().Select(s => s.Diagnostic.Render()));
        string remedy = palace is null ? Remedy(Em3dSolver.Palace) : openEms is null ? Remedy(Em3dSolver.OpenEms) : "";
        return log.Result((palace ?? openEms)!.Status, EmDiagnostics.BothRefusedBeforeWork(text, remedy));
    }

    private static EmRunResult Single(Leg leg, RunLog log)
        => leg.Ok
            ? new EmRunResult(EmRunStatus.Ok, leg.Data, null, null, leg.NpyPath, leg.SnpPath, null, log.Warnings,
                              Notes: log.Notes, Errors: log.Errors, KernelName: leg.KernelName)
            : log.Result(leg.Status, leg.Stop!);

    /// <summary>Why a phase did not go on, and which status says so.</summary>
    private sealed record Stop(EmRunStatus Status, Diagnostic Diagnostic);

    /// <summary>One solver's execution: its status, and on success its data and files.</summary>
    private sealed record Leg(Em3dSolver Solver, EmRunStatus Status, Diagnostic? Stop, DataSet? Data,
                              string? NpyPath, string? SnpPath, string KernelName, Em3dComparisonFacts? Facts = null)
    {
        public bool Ok => Status == EmRunStatus.Ok;
        public static Leg Failed(Em3dSolver s, EmRunStatus status, Diagnostic d) => new(s, status, d, null, null, null, "");
    }

    /// <summary>The run's three lists (R-emcli-6), shared by both backends in a both-run.</summary>
    private sealed class RunLog
    {
        public List<string> Notes { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];

        public EmRunResult Result(EmRunStatus status, Diagnostic d, IReadOnlyList<EmRunOutput>? outputs = null)
            => new(status, null, null, null, null, null, d.Render(), Warnings,
                   Notes: Notes, Errors: Errors, Diagnostic: d, Outputs: outputs is { Count: > 0 } ? outputs : null);
    }

    /// <summary>Several holds released together.</summary>
    private sealed class Holds(IReadOnlyList<IDisposable> holds) : IDisposable
    {
        public void Dispose() { foreach (var h in holds) h.Dispose(); }
    }

    // ── Palace (brief-em3d-7) ─────────────────────────────────────────────────────────────────

    private sealed record PalacePlan(PalaceSettings Settings, GmshLowering Lowering, string ConfigJson,
                                     SolverInstallation Palace, SolverInstallation Gmsh, IPalaceRunner Runner,
                                     Em3dMemoryScope? Memory);

    private static PalacePlan? PreparePalace(Em3dProblem problem, PalaceSettings settings, IReadOnlyList<SolverReadiness> readiness,
                                             IPalaceRunner runner, Em3dMemoryScope? memoryScope, RunLog log, out Stop? stop)
    {
        stop = null;
        var lowering = GmshGeoWriter.Write(problem, settings);
        if (!lowering.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-lowering", lowering.Refusal)); return null; }
        var config = PalaceConfigWriter.Write(problem, lowering.Groups, settings);
        if (!config.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-lowering", config.Refusal)); return null; }

        // F0 Q6 — the void model is the flat-surface impedance: low by up to 10 % on a conductor whose
        // radius is under about ten skin depths at the bottom of the band.
        if (!problem.IsStatic && ThinRoundConductors(problem) is { Count: > 0 } thin)
            log.Notes.Add($"{string.Join(", ", thin.Select(n => $"'{n}'"))} {(thin.Count == 1 ? "is" : "are")} round and under ten " +
                          $"skin depths in radius at {Fmt(problem.Frequency.StartHz / 1e9)} GHz. Palace models a conductor's " +
                          "loss as a flat surface's, which on such a wire reads the resistance low by up to about 10 % at the " +
                          "bottom of the band (3 % at 10 GHz on a 1 mil wire); inductance is not affected.");

        return new PalacePlan(settings, lowering, config.Json!,
                              readiness.Single(r => r.Tool == SolverTool.Palace).Installation!,
                              readiness.Single(r => r.Tool == SolverTool.Gmsh).Installation!, runner, memoryScope);
    }

    private static Leg ExecutePalace(PalacePlan plan, Em3dProblem problem, EmSetup setup, string resultsRoot, string snpBase,
                                     CancellationToken ct, RunControl? control, int? maxCores, RunLog log, MemoryGate memory)
    {
        const Em3dSolver Me = Em3dSolver.Palace;
        Leg Failed(string reason) => Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(reason));
        Leg Cancelled() => Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
        var (settings, lowering, palace) = (plan.Settings, plan.Lowering, plan.Palace);
        var wall = System.Diagnostics.Stopwatch.StartNew();
        long physical = plan.Memory?.Bytes ?? MachineMemory.PhysicalBytes;
        using var runner = plan.Runner;

        string runDir = RunDirectory(resultsRoot, setup, Me);
        try { Directory.CreateDirectory(runDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"the run directory '{runDir}' could not be created ({e.Message}).");
        }

        // ── Gmsh, then the entity check ───────────────────────────────────────────────────────
        PalaceStep meshed;
        try { meshed = PalaceRun.Mesh(runDir, lowering, plan.Gmsh.Path, control, ct, physical); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"the mesh could not be staged in '{runDir}' ({e.Message}).");
        }
        if (meshed.Cancelled) return Cancelled();
        if (meshed.Refused) return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-mesh", meshed.Message));
        if (!meshed.Ok) return Failed(meshed.Message!);
        log.Notes.Add(meshed.Reused
            ? $"The geometry script is unchanged since the last run, so its mesh was reused ({runDir})."
            : $"Meshed with Gmsh in {runDir}.");

        // brief-em3d-21 R-em3d21-2 — the mesh's size is known now, and on a problem whose mesh is mostly
        // refinement (a bond wire) it is the first figure that can say the run will not fit.
        if (meshed.Tetrahedra is { } tets &&
            memory.Admit(MeshMemoryVerdict(tets, settings, physical, plan.Memory), log) is { } tooBig)
            return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(MemoryRefusalSource,
                tooBig + $" The mesh is kept in {runDir}, so a run started anyway reuses it."));

        // ── Palace ────────────────────────────────────────────────────────────────────────────
        // brief-em3d-21 R-em3d21-3a — one MPI rank per PHYSICAL core by default (Open MPI's slot count).
        var cores = runner.Cores;
        int processes = maxCores ?? cores.Count;
        if (maxCores is null && !cores.Measured) log.Notes.Add($"Palace ran on {processes} process(es): {cores.How}.");
        var tracker = new PalaceStageTracker(settings.AdaptiveMaxIterations, settings.SweepAdaptiveTol, control,
                                             electrostatic: problem.Type == Em3dProblemType.Electrostatic);
        PalaceStep solved;
        string? mpiNote;
        try { solved = runner.Solve(runDir, plan.ConfigJson, palace.Path, processes, control, ct, out mpiNote, tracker, physical); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"Palace could not be staged in '{runDir}' ({e.Message}).");
        }
        if (mpiNote is not null) log.Notes.Add(mpiNote);
        log.Notes.AddRange(tracker.Notes);
        log.Warnings.AddRange(tracker.Warnings);
        if (solved.MemoryNote is not null) log.Notes.Add(solved.MemoryNote);
        if (solved.Cancelled) return Cancelled();
        if (!solved.Ok) return Failed(solved.Message!);

        control?.BeginStage(ReadingLabel);
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        if (problem.IsStatic)
            return FinishStatic(problem, setup, resultsRoot, post, tracker, processes, log, palace, wall.Elapsed);
        if (problem.Type == Em3dProblemType.Eigenmode)
            return FinishEigen(problem, setup, resultsRoot, post, lowering, tracker, processes, log, palace, wall.Elapsed);
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var s = PalaceRun.ReadPortS(Path.Combine(post, PalaceRun.PortSFile), [.. ports.Select(p => p.Number)], out string? readError);
        if (readError is not null) return Failed(readError);
        // brief-em3d-23 R-em3d23-2d — a wave port's S is referred to its mode's own impedance: renormalised to
        // the port's stated Z0 before anything else reads it.
        Complex[][]? modeZ = null;
        if (problem.HasWavePorts)
        {
            var wave = ports.Where(p => p.Kind == Em3dPortKind.Wave).ToList();
            modeZ = PalaceRun.ReadPortZ(Path.Combine(post, PalaceRun.PortZFile), [.. wave.Select(p => p.Number)],
                                        out double[] zf, out string? zError);
            if (modeZ is null) return Failed(zError!);
            if (zf.Length != s.FrequenciesHz.Length ||
                zf.Where((f, k) => Math.Abs(f - s.FrequenciesHz[k]) > 1e-6 * Math.Abs(f)).Any())
                return Failed($"Palace's {PalaceRun.PortZFile} and {PalaceRun.PortSFile} do not list the same frequencies, so the " +
                              "wave ports' S-parameters cannot be renormalised.");
            s = RenormaliseWavePorts(s, ports, wave, modeZ);
            log.Notes.Add(WaveNote(ports, wave, modeZ, s.FrequenciesHz));
            // R-em3d23-3 — a port face that supports a second propagating mode gives an S that means something else.
            try
            {
                var second = runner.SecondModes(runDir, plan.ConfigJson, palace.Path, problem.Frequency.StopHz,
                                                   [.. wave.Select(p => p.Number)], ct, out string? secondNote);
                if (second is null) log.Notes.Add($"Whether a wave port's second mode propagates was not checked: {secondNote}.");
                else if (second.Any(m => m.Propagating))
                    foreach (var m in second.Where(m => m.Propagating))
                        log.Warnings.Add($"Port {m.Port}'s wave-port face supports a SECOND propagating mode at " +
                                     $"{Fmt(problem.Frequency.StopHz / 1e9)} GHz (Palace's kₙ = {Fmt(m.Kn.Real)}{(m.Kn.Imaginary < 0 ? "−" : "+")}" +
                                     $"{Fmt(Math.Abs(m.Kn.Imaginary))}i m⁻¹), so its S-parameters are those of the first mode alone and " +
                                     "power the second carries is not in them. Make the port region smaller (the Ports3D Width and " +
                                     "Height), or lower the sweep's top.");
                else log.Notes.Add($"No wave port's second mode propagates at {Fmt(problem.Frequency.StopHz / 1e9)} GHz, the top of " +
                                   "the sweep (Palace, asked for mode 2): " + string.Join("; ", second.Select(m =>
                                       $"port {m.Port}'s decays by 1/e in {Fmt(m.DecayLengthM * 1e3)} mm")) + ".");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Notes.Add($"Whether a wave port's second mode propagates was not checked ({e.Message}).");
            }
        }
        s = AtRequestedFrequencies(s, FrequenciesHz(problem.Frequency));
        var facts = PalaceRun.ReadFacts(post);
        log.Notes.Add($"Palace solved {s.FrequenciesHz.Length} frequencies on {Count(facts.FinalElements)} elements " +
                      $"({Count(facts.InitialElements)} initially, {facts.AdaptiveIterations ?? 0} adaptive pass(es)), " +
                      $"{Count(facts.DegreesOfFreedom)} unknowns, {processes} process(es).");

        // ── The DataSet: S and Z0 in the house convention, plus Palace's own record ─────────────
        var data = BuildDataSet(s, ports, facts);
        if (modeZ is not null)
        {
            var wave = ports.Where(p => p.Kind == Em3dPortKind.Wave).ToList();
            var values = new System.Numerics.Complex[modeZ.Length * wave.Count];
            for (int k = 0; k < modeZ.Length; k++)
                for (int i = 0; i < wave.Count; i++) values[k * wave.Count + i] = modeZ[k][i];
            data.AddToGroup(PalaceGroup, WaveModeImpedanceCube, new DataCube(
                [new Axis("f", s.FrequenciesHz, "Hz"), new Axis("Port", [.. wave.Select(p => (double)p.Number)], "")], values) { Unit = "Ohm" });
        }
        var summary = tracker.Summary;
        var quality = setup.Palace?.Quality ?? PalaceQuality.Standard;

        string? snpPath = snpBase + $".s{ports.Count}p";
        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);

        string? snpError;
        try { snpError = WriteSnp(data, snpBase, setup, problem, lowering, settings, palace, facts, summary, quality, wall.Elapsed); }
        catch (Exception e) { snpError = e.Message; }
        if (snpError is not null)
        {
            log.Errors.Add($"The .snp could not be written to '{snpPath}': {snpError}");
            snpPath = null;
        }

        // R-em3d21-5a — the one line that says what the run cost, the last thing the run says.
        log.Notes.Add(CompletionSummary(summary, quality, wall.Elapsed));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, snpPath, "Palace " + palace.DescribeVersion());
    }

    /// <summary>
    /// brief-em3d-22 R-em3d22-3c/4c — a static solve's matrices, read by column name, as a DataSet in
    /// <c>results/</c> under <c>&lt;key&gt;.palace_es</c> or <c>_ms</c>. No Touchstone: a matrix is not S.
    /// </summary>
    private static Leg FinishStatic(Em3dProblem problem, EmSetup setup, string resultsRoot, string post,
                                    PalaceStageTracker tracker, int processes, RunLog log, SolverInstallation palace, TimeSpan wall)
    {
        const Em3dSolver Me = Em3dSolver.Palace;
        bool es = problem.Type == Em3dProblemType.Electrostatic;
        int[] indices = [.. Enumerable.Range(1, problem.Terminals.Count)];
        var (file, mutualFile, symbol, mutualSymbol, unit) = es
            ? (PalaceRun.CapacitanceFile, PalaceRun.MutualCapacitanceFile, "C", "C_m", "(F)")
            : (PalaceRun.InductanceFile, PalaceRun.MutualInductanceFile, "M", "M_m", "(H)");
        var matrix = PalaceRun.ReadTerminalMatrix(Path.Combine(post, file), symbol, unit, indices, out string? error);
        if (matrix is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        var mutual = PalaceRun.ReadTerminalMatrix(Path.Combine(post, mutualFile), mutualSymbol, unit, indices, out string? mutualError);
        if (mutual is null) log.Notes.Add($"The {(es ? "mutual capacitance" : "current-difference inductance")} form was not read: {mutualError}");

        var notes = new List<string>();
        string groundText = GroundText(problem, setup);
        if (es)
        {
            notes.Add("C is the Maxwell capacitance matrix: C[i][i] is terminal i's capacitance to every other conductor " +
                      "(the charge on i with i at 1 V and all others grounded), and C[i][j] <= 0 off the diagonal. " +
                      "C_mutual is the lumped-circuit form: C_mutual[i][i] is i's capacitance to ground alone and " +
                      "C_mutual[i][j] = -C[i][j] is the capacitor between i and j. " + groundText);
            if (problem.Boundary.Faces is var f && new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax }.Contains(Em3dBoundaryKind.Absorbing))
                notes.Add("An open (Absorbing) face of the air box is a zero-charge face in an electrostatic solve: no field " +
                          "line ends on it, so every line ends on a conductor of the problem. Set a face to Pec to make it part " +
                          "of the ground instead.");
        }
        else
        {
            notes.Add("L is the inductance matrix: L[i][i] is terminal i's self-inductance with its current through its " +
                      "source port, and L[i][j] the mutual inductance between i and j. " + groundText);
            notes.Add(Em3dStaticResult.InternalInductanceNote(problem));
            if (problem.FloatingConductors() is { Count: > 0 } free)
                notes.Add($"{free.Count} conductor(s) are in no terminal ({string.Join(", ", free.Take(6).Select(n => $"'{n}'"))}" +
                          $"{(free.Count > 6 ? ", …" : "")}): they carry no source current, only the screening current a perfect " +
                          "conductor carries.");
        }
        log.Notes.AddRange(notes);

        var facts = PalaceRun.ReadFacts(post);
        var data = Em3dStaticResult.Build(problem.Type, [.. problem.Terminals.Select(t => t.Name)], matrix, mutual, notes);
        void Scalar(string name, double? v)
        {
            if (v is { } x) data.AddToGroup(PalaceGroup, name, DataCube.Scalar(x));
        }
        Scalar("MeshElementsInitial", facts.InitialElements);
        Scalar("MeshElementsFinal",   facts.FinalElements);
        Scalar("DegreesOfFreedom",    facts.DegreesOfFreedom);
        Scalar("AdaptiveIterations",  facts.AdaptiveIterations);
        log.Notes.Add($"Palace solved {problem.Terminals.Count} terminal(s) on {Count(facts.FinalElements)} elements " +
                      $"({Count(facts.InitialElements)} initially, {facts.AdaptiveIterations ?? 0} adaptive pass(es)), " +
                      $"{Count(facts.DegreesOfFreedom)} unknowns, {processes} process(es).");

        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);
        log.Notes.Add(CompletionSummary(tracker.Summary, setup.Palace?.Quality ?? PalaceQuality.Standard, wall));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "Palace " + palace.DescribeVersion());
    }

    /// <summary>The Palace group's cube holding each wave port's mode impedance Z_PV, [f, Port], ohms.</summary>
    public const string WaveModeImpedanceCube = "WavePortZmode";

    /// <summary>
    /// R-em3d23-2d — Palace's S with every wave port moved from its mode's impedance to its stated Z0: the
    /// whole matrix per frequency (RfCore's <see cref="RFNetwork.SToS"/>), never an element at a time. A lumped
    /// port is already at its Z0 (its resistance), so its reference is unchanged.
    /// </summary>
    internal static PalacePortS RenormaliseWavePorts(PalacePortS s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Em3dPort> wave,
                                                     Complex[][] modeZ)
    {
        int n = ports.Count;
        var target = ports.Select(p => p.Z0).ToArray();
        var mats = new Complex[s.S.Length][,];
        for (int k = 0; k < s.S.Length; k++)
        {
            var from = (Complex[])target.Clone();
            for (int w = 0; w < wave.Count; w++) from[ports.ToList().IndexOf(wave[w])] = modeZ[k][w];
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = s.S[k][i, j];
            var r = RFNetwork.SToS(m, from, target);
            mats[k] = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) mats[k][i, j] = r[i, j];
        }
        return s with { S = mats };
    }

    /// <summary>What each wave port's S was referred to, and what it is now.</summary>
    private static string WaveNote(IReadOnlyList<Em3dPort> ports, IReadOnlyList<Em3dPort> wave, Complex[][] modeZ, double[] f)
    {
        var parts = wave.Select((p, w) =>
        {
            var re = modeZ.Select(z => z[w].Real).ToList();
            string range = re.Min() == re.Max() ? $"{Fmt(re[0])} Ω" : $"{Fmt(re.Min())} to {Fmt(re.Max())} Ω";
            return $"port {p.Number}'s mode impedance is {range} over the sweep, renormalised to {Fmt(p.Z0.Real)} Ω";
        });
        return "Palace refers a wave port's S-parameters to the port's own mode, normalised to unit power — that is, to the " +
               "mode's impedance Z_PV (port-Z.csv), which varies with frequency. The .sNp states one real reference per port, " +
               $"so the wave ports were renormalised: {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// brief-em3d-23 R-em3d23-4c — an eigenmode solve's modes, read by column name, as a DataSet in
    /// <c>results/</c> under <c>&lt;key&gt;.palace_eig</c>. No Touchstone: a mode is not S. The run directory keeps
    /// whatever Palace wrote (R-em3d23-4d).
    /// </summary>
    private static Leg FinishEigen(Em3dProblem problem, EmSetup setup, string resultsRoot, string post, GmshLowering lowering,
                                   PalaceStageTracker tracker, int processes, RunLog log, SolverInstallation palace, TimeSpan wall)
    {
        const Em3dSolver Me = Em3dSolver.Palace;
        var modes = PalaceRun.ReadModes(Path.Combine(post, PalaceRun.EigFile), out string? error);
        if (modes is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        if (modes.Count < problem.EigenmodeCount)
            log.Warnings.Add($"Palace found {modes.Count} converged mode(s) above {Fmt(problem.EigenmodeTargetHz / 1e9)} GHz where " +
                             $"{problem.EigenmodeCount} were asked for.");
        int[] index = [.. modes.Select(m => m.Index)];

        var lumped = problem.Ports.Where(p => p.Kind == Em3dPortKind.Lumped).OrderBy(p => p.Number).Select(p => p.Number).ToList();
        double[,]? qExt = null;
        if (lumped.Count > 0)
        {
            qExt = PalaceRun.ReadModeColumns(Path.Combine(post, PalaceRun.PortQFile), [.. lumped.Select(n => $"Q_ext[{n}]")], index, out string? qError);
            if (qError is not null) log.Notes.Add($"The ports' external Q was not read: {qError}");
        }
        var volumes = lowering.Groups.Where(g => g.Dimension == 3).ToList();
        var participation = PalaceRun.ReadModeColumns(Path.Combine(post, PalaceRun.DomainEnergyFile),
                                                      [.. volumes.Select((_, k) => $"p_elec[{k + 1}]")], index, out string? pError);
        if (pError is not null) log.Notes.Add($"The energy participation was not read: {pError}");

        var notes = new List<string>
        {
            "f is each mode's resonant frequency (the real part of Palace's complex eigenfrequency). Q is Palace's Q, " +
            "Re f / 2 Im f: the LOADED Q, with every loss in the problem — finite-conductivity walls, lossy dielectrics, " +
            "open (absorbing) faces, and each lumped port's resistance, which an eigenmode solve treats as a load.",
        };
        if (qExt is not null)
            notes.Add("Q_ext is each lumped port's external Q, as Palace writes it. Q_unloaded = 1/(1/Q − Σ 1/Q_ext) is the Q " +
                      "with the ports' loading taken out, computed by circuitRF from those two figures of Palace's.");
        if (participation is not null)
            notes.Add("Participation is the fraction of each mode's electric energy in each meshed region (Palace's p_elec): " +
                      "where the mode lives.");
        log.Notes.AddRange(notes);

        var data = Em3dEigenResult.Build([.. modes.Select(m => new Em3dMode(m.Index, m.FrequencyHz, m.Q))], lumped, qExt,
                                         [.. volumes.Select(g => g.Name)], participation, notes);
        var facts = PalaceRun.ReadFacts(post);
        void Scalar(string name, double? v)
        {
            if (v is { } x) data.AddToGroup(PalaceGroup, name, DataCube.Scalar(x));
        }
        Scalar("MeshElementsInitial", facts.InitialElements);
        Scalar("MeshElementsFinal",   facts.FinalElements);
        Scalar("DegreesOfFreedom",    facts.DegreesOfFreedom);
        Scalar("AdaptiveIterations",  facts.AdaptiveIterations);
        log.Notes.Add($"Palace found {modes.Count} mode(s) above {Fmt(problem.EigenmodeTargetHz / 1e9)} GHz on {Count(facts.FinalElements)} " +
                      $"elements ({Count(facts.InitialElements)} initially, {facts.AdaptiveIterations ?? 0} adaptive pass(es)), " +
                      $"{Count(facts.DegreesOfFreedom)} unknowns, {processes} process(es).");

        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);
        log.Notes.Add(CompletionSummary(tracker.Summary, setup.Palace?.Quality ?? PalaceQuality.Standard, wall));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "Palace " + palace.DescribeVersion());
    }

    /// <summary>What the matrix is referred to, in a sentence.</summary>
    private static string GroundText(Em3dProblem problem, EmSetup setup)
    {
        bool pecFace = new[] { problem.Boundary.Faces.XMin, problem.Boundary.Faces.XMax, problem.Boundary.Faces.YMin,
                               problem.Boundary.Faces.YMax, problem.Boundary.Faces.ZMin, problem.Boundary.Faces.ZMax }
                       .Contains(Em3dBoundaryKind.Pec);
        string who = setup.Ground3D is { Length: > 0 } g ? $"net '{g}'" : "the ground-reference conductors";
        var parts = new List<string>();
        if (problem.GroundObjects.Count > 0) parts.Add($"{who} ({problem.GroundObjects.Count} conductor(s))");
        if (pecFace) parts.Add("the air box's PEC face(s)");
        return parts.Count == 0
            ? "Nothing in this problem is ground, so the matrix is referred to no conductor outside its terminals."
            : $"The reference (ground) is {string.Join(" and ", parts)}.";
    }

    /// <summary>
    /// R-em3d10-2a — Palace's <c>port-S.csv</c> prints each frequency to nine significant digits, so
    /// what is read back can sit a few hertz off the frequency Palace was asked for and solved at. When
    /// every row matches the requested sweep to that printed precision, the requested values are the
    /// frequencies; otherwise the file's own are kept, and a comparison against another solver refuses.
    /// </summary>
    internal static PalacePortS AtRequestedFrequencies(PalacePortS s, double[] requested)
    {
        const double PrintedPrecision = 1e-8;      // nine significant digits, with room for Palace's own arithmetic
        if (s.FrequenciesHz.Length != requested.Length) return s;
        for (int k = 0; k < requested.Length; k++)
            if (!(Math.Abs(s.FrequenciesHz[k] - requested[k]) <= PrintedPrecision * Math.Abs(requested[k])))
                return s;
        return s with { FrequenciesHz = (double[])requested.Clone() };
    }

    /// <summary>A solver's grouped <c>.npy</c> in <c>results/</c>; null, with an error, when it could not be written.</summary>
    private static string? WriteNpy(string resultsRoot, string key, DataSet data, RunLog log)
    {
        try
        {
            var written = ResultsWriter.WriteRun(
                Path.GetDirectoryName(resultsRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? resultsRoot, key, data);
            if (written.Error is { } writeError)
                log.Errors.Add($"The EM result could not be written to results/: {writeError}");
            return written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception e)
        {
            log.Errors.Add($"The EM result could not be written to results/: {e.Message}");
            return null;
        }
    }

    // ── openEMS (brief-em3d-9) ────────────────────────────────────────────────────────────────

    /// <summary>The diagnostics group an openEMS <c>.npy</c> carries beside S.</summary>
    public const string OpenEmsGroup = "openems";

    /// <summary>
    /// R-em3d9-3e: openEMS's own time step against brief 8's Courant estimate. Outside this band the
    /// grid or the estimate is wrong, and the run says so.
    /// </summary>
    public const double TimeStepRatioLow = 0.5, TimeStepRatioHigh = 1.0;

    private sealed record OpenEmsPlan(OpenEmsGridSettings GridSettings, OpenEmsRunSettings RunSettings, FdtdGridResult Grid,
                                      CsxcadLowering Lowering, SolverInstallation OpenEms);

    private static OpenEmsPlan? PrepareOpenEms(Em3dProblem problem, OpenEmsGridSettings gridSettings, OpenEmsRunSettings runSettings,
                                               IReadOnlyList<SolverReadiness> readiness, RunControl? control, RunLog log, out Stop? stop)
    {
        stop = null;

        // ── brief 8: the grid, then the lowering ──────────────────────────────────────────────
        control?.BeginStage("placing the FDTD grid");
        FdtdGridResult grid;
        try { grid = FdtdGrid.Build(problem, gridSettings); }
        catch (InvalidOperationException e)
        {
            stop = new(EmRunStatus.EngineError, EmDiagnostics.SolveFailed($"the FDTD grid could not be built ({e.Message})"));
            return null;
        }
        if (grid.Refusal is { } tooBig) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-grid", tooBig)); return null; }
        log.Warnings.AddRange(grid.Warnings);
        log.Notes.AddRange(grid.Merges.Select(m => m.Sentence));

        var lowering = CsxcadWriter.Write(problem, grid, gridSettings, runSettings);
        if (!lowering.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-lowering", lowering.Refusal)); return null; }
        log.Notes.AddRange(lowering.Notes);
        int n = lowering.Ports.Count;
        var s0 = grid.Smallest;
        log.Notes.Add($"openEMS grid: {grid.X.Lines.Count:N0} × {grid.Y.Lines.Count:N0} × {grid.Z.Lines.Count:N0} = {grid.Cells:N0} " +
                      $"cells; smallest cell {FdtdGrid.FormatLength(s0.SmallestCellM)} on {FdtdGrid.AxisName(s0.Axis)}, set by " +
                      $"{string.Join("; ", s0.SmallestCellFeatures.Select(f => f.Describe(s0.Axis)))}. openEMS runs once per port: " +
                      $"{n} run{(n == 1 ? "" : "s")}, each up to {lowering.MaxTimeSteps:N0} time steps.");

        return new OpenEmsPlan(gridSettings, runSettings, grid, lowering,
                               readiness.Single(r => r.Tool == SolverTool.OpenEms).Installation!);
    }

    private static Leg ExecuteOpenEms(OpenEmsPlan plan, Em3dProblem problem, EmSetup setup, string resultsRoot, string snpBase,
                                      CancellationToken ct, RunControl? control, int? maxCores, RunLog log)
    {
        const Em3dSolver Me = Em3dSolver.OpenEms;
        Leg Failed(string reason) => Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(reason));
        var (grid, lowering, runSettings, openEms) = (plan.Grid, plan.Lowering, plan.RunSettings, plan.OpenEms);
        int n = lowering.Ports.Count;

        string runDir = RunDirectory(resultsRoot, setup, Me);
        try { OpenEmsRun.Stage(runDir, lowering); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"the openEMS run could not be staged in '{runDir}' ({e.Message}).");
        }

        // The earliest the ports' decay may stop a run: the pulse, then a crossing of the STRUCTURE's
        // diagonal (conductors and ports — the air box's padding carries no signal between ports) and
        // back at the slowest wave speed in the problem. Before then a far port may not yet have seen
        // its first arrival (OpenEmsRun.Decay).
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        void Grow(double ax, double ay, double az, double bx, double by, double bz)
        {
            x0 = Math.Min(x0, ax); y0 = Math.Min(y0, ay); z0 = Math.Min(z0, az);
            x1 = Math.Max(x1, bx); y1 = Math.Max(y1, by); z1 = Math.Max(z1, bz);
        }
        foreach (var solid in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            var b = Em3dProblem.Bounds(solid.Primitive);
            Grow(b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1);
        }
        foreach (var port in problem.Ports) Grow(port.Min.X, port.Min.Y, port.Min.Z, port.Max.X, port.Max.Y, port.Max.Z);
        double diagonal = x1 >= x0 ? Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0)) : 0;
        double slowest = problem.Materials.Select(m => Math.Sqrt(Math.Max(1, m.Epsr) * Math.Max(1, m.Mur))).DefaultIfEmpty(1).Max();
        double settleS = grid.ExcitationS + 2 * diagonal * slowest / 299_792_458.0;

        // ── One run per port (R-em3d9-3a) ─────────────────────────────────────────────────────
        int? threads = maxCores is { } c ? EmSolveCores.Sanitise(c) : null;
        var runs = new List<OpenEmsPortRun>();
        for (int k = 0; k < n; k++)
        {
            OpenEmsPortRun r;
            try
            {
                r = OpenEmsRun.RunPort(runDir, lowering, k, openEms.Path, threads, runSettings.EndCriterionDb,
                                       grid.ExcitationS, control, ct, settleS);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed($"openEMS could not be run in '{runDir}' ({e.Message}).");
            }
            if (r.Cancelled) return Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
            if (!r.Ok) return Failed(r.Message!);
            runs.Add(r);

            var facts = r.Facts!;
            if (!r.Converged)
                log.Warnings.Add($"openEMS's run exciting port {r.Port} stopped at its limit of {lowering.MaxTimeSteps:N0} time steps" +
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
                    log.Warnings.Add($"openEMS chose a time step of {dt:G4} s, {ratio:G3} times circuitRF's Courant estimate of " +
                                     $"{grid.TimeStepEstimateS:G4} s — outside {TimeStepRatioLow}–{TimeStepRatioHigh}. The answer " +
                                     "is still openEMS's; the estimate circuitRF reports before a run is what disagrees.");
            }
        }

        // ── The transform (R-em3d9-4) ────────────────────────────────────────────────────────
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        double[] freqs = FrequenciesHz(problem.Frequency);
        var result = FdtdPortTransform.Solve([.. runs.Select(r => r.Probes!)], freqs, lowering.Ports,
                                             [.. ports.Select(p => p.Z0)]);
        if (result.Error is { } singular) return Failed(singular);

        var data = BuildOpenEmsDataSet(result, ports, grid, runs, runSettings);

        string? snpPath = snpBase + $".s{ports.Count}p";
        string? npyPath = WriteNpy(resultsRoot, NpyKey(setup, Me), data, log);

        string? snpError;
        try { snpError = WriteOpenEmsSnp(data, snpBase, setup, problem, grid, plan.GridSettings, runSettings, lowering, openEms, runs); }
        catch (Exception e) { snpError = e.Message; }
        if (snpError is not null)
        {
            log.Errors.Add($"The .snp could not be written to '{snpPath}': {snpError}");
            snpPath = null;
        }

        var comparisonFacts = new Em3dComparisonFacts(lowering.DielectricFitHz, lowering.PecSolids, lowering.SubCellWires,
                                                      [.. runs.Where(r => !r.Converged).Select(r => r.Port)]);
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, snpPath, "openEMS " + openEms.DescribeVersion(), comparisonFacts);
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
                                 PalaceRunFacts facts, PalaceRunSummary summary, PalaceQuality quality, TimeSpan wall)
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
                      $"to '{Ascii(p.PositiveObject)}', " + (p.Kind == Em3dPortKind.Wave
                          // R-em3d23-2d — the file states the one reference its numbers are in, and where they came from.
                          ? $"wave (mode 1), {R(p.Z0.Real)} Ohm: Palace's S is referred to the mode's own impedance Z_PV, " +
                            $"renormalised here to {R(p.Z0.Real)} Ohm; reference plane {R(p.ReferencePlane.ShiftM * 1e6)} um " +
                            "into the structure from the port face"
                          : $"lumped, {R(p.Z0.Real)} Ohm"));
        lines.Add($"circuitRF-EM 3D mesh: {Count(facts.InitialElements)} elements initially, {Count(facts.FinalElements)} " +
                  $"finally, {facts.AdaptiveIterations ?? 0} adaptive pass(es), element order {settings.ElementOrder}");
        lines.Add($"circuitRF-EM 3D operating temperature: {R(problem.OperatingTempC)} C");
        // brief-em3d-21 R-em3d21-5b — added fields, none renamed. What the solve WAS is one line; what it
        // COST (wall time, peak memory) is another, because it differs between two runs of one setup
        // exactly as the write stamp does.
        lines.Add(RunProvenancePrefix + RunProvenance(summary, quality));
        lines.Add(CostProvenancePrefix + CostProvenance(summary, wall));

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

    // ── brief-em3d-21: ranks, memory, and what the run cost ───────────────────────────────────

    /// <summary>The diagnostic source a memory refusal carries (<c>em.refused.palace-memory</c>).</summary>
    public const string MemoryRefusalSource = "palace-memory";

    /// <summary>The stage after Palace exits.</summary>
    public const string ReadingLabel = "Reading results";

    /// <summary>R-em3d21-5b — the <c>.sNp</c> line recording what the solve was.</summary>
    public const string RunProvenancePrefix = "circuitRF-EM 3D run: ";

    /// <summary>R-em3d21-5b — the <c>.sNp</c> line recording what the solve cost: it differs between two
    /// runs of one setup, as the write stamp does, so a byte comparison of two runs leaves it out.</summary>
    public const string CostProvenancePrefix = "circuitRF-EM 3D run cost: ";

    /// <summary>R-em3d21-3b — an explicit process count past the physical core count, refused naming it.</summary>
    internal static string? RankRefusal(int? asked, PhysicalCoreReading cores)
        => asked is { } n && n > cores.Count
            ? $"This run asks for {n} Palace processes (the Cores setting), and this machine has {cores.Count} physical " +
              $"core{(cores.Count == 1 ? "" : "s")} ({(cores.Measured ? "read from " + cores.How : cores.How)}). Palace " +
              "runs one MPI process per core, and MPI refuses more processes than physical cores; forced, they run " +
              $"slower, not faster, so circuitRF does not oversubscribe. Choose {cores.Count} or fewer under Cores, or " +
              "Automatic."
            : null;

    /// <summary>
    /// Palace's size for <paramref name="p"/> at <paramref name="settings"/>, ESTIMATED (brief-em3d-5
    /// R-em3d5-3c): each meshed region's volume at the Palace section's largest element for its
    /// material (<see cref="GmshGeoWriter.MaxElementSizeM"/>, the formula the script uses), and the
    /// memory that implies with refinement allowed or not. Null at an element order no measurement covers.
    /// <c>explain</c> prints this and the fit check compares it, so the two cannot disagree.
    /// </summary>
    public static Em3dPalaceEstimate? EstimatePalace(Em3dProblem p, PalaceSettings settings)
    {
        if (settings.ElementOrder is not (1 or 2)) return null;
        var byName = p.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        // The background is meshed as the problem's air, or free space with none — GmshGeoWriter's rule.
        var background = p.Solids.FirstOrDefault(s => s.Role == Em3dRole.Air) is { } air
            ? byName[air.Material] : new Em3dMaterial("(free space)", 1, null, 0, 1, 0);
        return Em3dSizeEstimate.Palace(
            p, s => GmshGeoWriter.MaxElementSizeM(byName[s.Material], GmshGeoWriter.SizingFrequencyHz(p), settings), settings.ElementOrder,
            GmshGeoWriter.MaxElementSizeM(background, GmshGeoWriter.SizingFrequencyHz(p), settings), settings.AdaptiveMaxIterations);
    }

    /// <summary>The fit check for a setup as the panel is about to run it: generates the problem and
    /// compares against this machine. The panel calls it before Simulate starts anything.</summary>
    public static Em3dMemoryVerdict PalaceMemoryVerdict(EmSetup setup, EmLayoutSource source)
    {
        if (source.Technology is null || setup.Solver3D is not (Em3dSolver.Palace or Em3dSolver.Both)) return Em3dMemoryVerdict.Unknown;
        var settings = PalaceSettings.Resolve(setup.Palace);
        if (settings.Problems().Count > 0) return Em3dMemoryVerdict.Unknown;
        var generated = Em3dGenerator.Generate(setup, source, source.Technology);
        return generated.Problem is { } p && generated.Ok
            ? PalaceMemoryVerdict(p, setup, source, settings, MachineMemory.PhysicalBytes)
            : Em3dMemoryVerdict.Unknown;
    }

    /// <summary>
    /// R-em3d21-2a/b — the estimate against <paramref name="physicalBytes"/>, with the remedies a
    /// warning names, each with the estimate it would give: the Draft preset, no refinement passes, and
    /// an air box with half the padding (regenerated, so its figure is the smaller problem's).
    /// </summary>
    public static Em3dMemoryVerdict PalaceMemoryVerdict(Em3dProblem problem, EmSetup setup, EmLayoutSource source,
                                                        PalaceSettings settings, long physicalBytes, Em3dMemoryScope? scope = null)
    {
        // brief-em3d-22 — the volume estimate prices elements per WAVELENGTH, which a static solve does
        // not have; its check is the one after meshing, on Gmsh's own count.
        if (problem.IsStatic) return Em3dMemoryVerdict.Unknown;
        long? estimate = EstimatePalace(problem, settings)?.MemoryBytes;
        if (estimate is not { } e || physicalBytes <= 0 || e <= Em3dMemoryVerdict.WarnFraction * physicalBytes)
            return Em3dMemoryVerdict.Evaluate(estimate, physicalBytes, [], scope: scope);

        var remedies = new List<Em3dMemoryRemedy>();
        var draft = PalaceSettings.Preset(PalaceQuality.Draft);
        var asDraft = settings with
        {
            ElementOrder = draft.ElementOrder, AdaptiveMaxIterations = draft.AdaptiveMaxIterations,
            AdaptiveTol = draft.AdaptiveTol, SweepAdaptiveTol = draft.SweepAdaptiveTol,
        };
        if (asDraft != settings)
            remedies.Add(new("the Draft preset (Palace.Quality: Draft)", EstimatePalace(problem, asDraft)?.MemoryBytes));
        if (settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("no refinement passes (Palace.AdaptiveMaxIterations: 0)",
                             EstimatePalace(problem, settings with { AdaptiveMaxIterations = 0 })?.MemoryBytes));
        if (source.Technology is { } tech)
        {
            double defaultPadUm = Em3dGenerator.DefaultPaddingFractionOfLongestWavelength * 299_792_458.0 /
                                  problem.Frequency.StartHz * 1e6;
            EmAirBoxFace Half(EmAirBoxFace? f) => new((f?.PaddingUm ?? defaultPadUm) / 2, f?.Boundary);
            var box = setup.AirBox ?? new EmAirBox();
            var smaller = setup.Clone();
            smaller.AirBox = new EmAirBox(Half(box.XMin), Half(box.XMax), Half(box.YMin), Half(box.YMax),
                                          Half(box.ZMin), Half(box.ZMax));
            var regenerated = Em3dGenerator.Generate(smaller, source, tech);
            if (regenerated.Ok && regenerated.Problem is { } q)
                remedies.Add(new("an air box with half the padding on every side (AirBox)", EstimatePalace(q, settings)?.MemoryBytes));
        }
        return Em3dMemoryVerdict.Evaluate(e, physicalBytes, [.. remedies.OrderBy(r => r.EstimateBytes ?? long.MaxValue)],
            basis: "Before meshing, from the 3D model's volumes:", scope: scope);
    }

    /// <summary>
    /// R-em3d21-2 — the check once Gmsh has made the mesh: <paramref name="tetrahedra"/> as Gmsh
    /// reported creating them, through the measured unknowns and bytes per unknown. The remedies are the
    /// ones a mesh this size can be priced for without meshing again.
    /// </summary>
    public static Em3dMemoryVerdict MeshMemoryVerdict(long tetrahedra, PalaceSettings settings, long physicalBytes,
                                                      Em3dMemoryScope? scope = null)
    {
        if (settings.ElementOrder is not (1 or 2)) return Em3dMemoryVerdict.Unknown;
        long e = Em3dSizeEstimate.PalaceMemoryBytesForMesh(tetrahedra, settings.ElementOrder, settings.AdaptiveMaxIterations);
        var remedies = new List<Em3dMemoryRemedy>();
        if (settings.ElementOrder != 1 || settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("the Draft preset (Palace.Quality: Draft)", Em3dSizeEstimate.PalaceMemoryBytesForMesh(tetrahedra, 1, 0)));
        if (settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("no refinement passes (Palace.AdaptiveMaxIterations: 0)",
                             Em3dSizeEstimate.PalaceMemoryBytesForMesh(tetrahedra, settings.ElementOrder, 0)));
        return Em3dMemoryVerdict.Evaluate(e, physicalBytes, [.. remedies.OrderBy(r => r.EstimateBytes)],
            basis: $"Gmsh made {tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra.", scope: scope);
    }

    /// <summary>
    /// R-em3d21-2b — one run's memory decisions: a warning is posted once at its highest level, and a
    /// run past 150 % is confirmed once (the panel's question, the CLI's --force) or refused.
    /// </summary>
    private sealed class MemoryGate(Func<string, bool>? confirm)
    {
        private Em3dMemoryLevel _posted = Em3dMemoryLevel.Fits;
        private bool _confirmed;

        /// <summary>Null to go on; otherwise the refusal.</summary>
        public string? Admit(Em3dMemoryVerdict verdict, RunLog log)
        {
            if (verdict.Level == Em3dMemoryLevel.Fits || verdict.Text is null) return null;
            if (verdict.Level == Em3dMemoryLevel.Severe && !_confirmed)
            {
                if (confirm?.Invoke(verdict.Text) != true)
                    return verdict.Text + " To start it anyway, confirm it when Simulate asks, or pass --force to `circuitrf em`.";
                _confirmed = true;
                log.Warnings.Add(verdict.Text + " It was started anyway, as confirmed.");
                _posted = verdict.Level;
                return null;
            }
            if (verdict.Level > _posted) log.Warnings.Add(verdict.Text);
            _posted = (Em3dMemoryLevel)Math.Max((int)_posted, (int)verdict.Level);
            return null;
        }
    }

    /// <summary>R-em3d21-5a — "Palace done in 2 min 27 s · peak memory 7.4 GB · …", every figure the
    /// parser read from Palace's log, and a field Palace did not print left out rather than guessed.</summary>
    public static string CompletionSummary(PalaceRunSummary s, PalaceQuality quality, TimeSpan wall)
    {
        var parts = new List<string> { $"Palace done in {Duration(wall)}" };
        if (s.PeakMemoryBytes is { } m) parts.Add($"peak memory {MachineMemory.Format(m)} (Palace's own)");
        if (s.InitialElements is { } a)
            parts.Add(s.FinalElements is { } b && b != a ? $"{a:N0} → {b:N0} tetrahedra" : $"{a:N0} tetrahedra");
        if (s.Unknowns is { } u) parts.Add($"{u:N0} unknowns");
        if (s.RefinementPasses is { } r) parts.Add($"{r} refinement pass{(r == 1 ? "" : "es")}");
        if (s.SweepSamples is { } n) parts.Add($"{n} frequency sample{(n == 1 ? "" : "s")}");
        parts.Add($"preset {quality}");
        if (!s.LogRecognised) parts.Add("Palace's log format was not recognised, so some figures are missing");
        return string.Join(" · ", parts) + ".";
    }

    private static string RunProvenance(PalaceRunSummary s, PalaceQuality quality)
        => string.Join("; ",
            $"preset {quality}",
            $"{N(s.InitialElements)} -> {N(s.FinalElements)} tetrahedra",
            $"{N(s.Unknowns)} unknowns",
            $"{N(s.RefinementPasses)} refinement pass(es)",
            $"{N(s.SweepSamples)} frequency sample(s)");

    private static string CostProvenance(PalaceRunSummary s, TimeSpan wall)
        => $"wall {wall.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s; Palace peak memory " +
           (s.PeakMemoryBytes is { } m ? Ascii(MachineMemory.Format(m)) : "unreported");

    private static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "unreported";
    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "unreported";

    /// <summary>"2 min 27 s", "48 s", "1 h 3 min".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{(int)t.TotalHours} h {t.Minutes} min";
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
