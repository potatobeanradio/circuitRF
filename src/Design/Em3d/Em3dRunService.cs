// brief-em3d-7 R-em3d7-1a — a 3D setup's run, behind EmRunService.Run (overview §5).
//
// EmRunService is the ONE door: the `em` verb, the .cem panel's Simulate and EmCliVerbTests all reach
// this file through it, and nothing else may call it (gate 12's source scan). It returns the same
// EmRunResult with the same statuses as a planar run, so no caller branches.
//
// brief-emi-1 — the run is solver-neutral: it asks the setup's Em3dSolverChoice which solvers run, and
// each one's descriptor (Em3dSolvers) for a LEG that configures, lowers and executes it. A solver's own
// part of this class lives in its folder, Solvers/<Name>/Em3dRunService.<Name>.cs:
//
//   Palace:  discovery (brief 6) -> Em3dGenerator (brief 3) -> GmshGeoWriter -> gmsh -> entity check
//                                -> PalaceConfigWriter -> palace -> port-S.csv -> .sNp + .npy
//   openEMS: discovery -> Em3dGenerator -> FdtdGrid (brief 8) -> CsxcadWriter -> openEMS once per port
//                      -> probe files -> FdtdPortTransform (src/Engine) -> .sNp + .npy
//
// The version and capability checks run FIRST, so a missing or unvalidated program costs seconds and
// starts no mesher (R-em3d7-4c, gate 11).
//
// A comparison (brief-em3d-10; the format's Both, and brief-emi-6's Compare of any set) generates the problem ONCE, lowers it for every solver
// before any starts, runs them in order, and writes Em3dComparison's DataSet (src/Engine) beside the
// results as `<key>.compare_em.npy`. See "The run" below for the three phases and why the refusals all
// come first.
//
// WHERE RESULTS LAND (R-em3d7-5, em-3d.md §4.5): the solver is part of a 3D result's name, so running a
// second solver on one setup never overwrites the first. `<key>.<token>.sNp`, the `.npy` key
// `<key>.<token>` + NpyKeySuffix, and the run directory `<results>/<key>.<token>/`, which is KEPT — the
// script, the mesh, the configuration and the solver's log and output — so a user can re-run it by hand
// and F2's viewer can read its fields. A planar result's path does not move by one byte.
//
// brief-cosim-pattern-1 — the setup's Output name, when it has one, decides every result file (EmResultPaths); the run directory is a
// working folder and does not follow it. Every driven leg publishes through PublishDriven: staged, then renamed into place.

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Results;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Em3d;

public static partial class Em3dRunService
{
    /// <summary>R-em3d7-5a — a 3D result's stem: the planar stem and the solver's result token.</summary>
    public static string ResultKey(EmSetup setup, Em3dSolverId solver)
        => EmRunService.ResolveResultKey(setup) + "." + Em3dSolvers.Get(solver).ResultToken;

    /// <summary>The <c>.npy</c>'s key: <see cref="ResultKey"/> + <see cref="EmRunService.NpyKeySuffix"/> —
    /// or, for a static setup, + <c>_es</c> / <c>_ms</c> (brief-em3d-22 R-em3d22-3c): a matrix is not S,
    /// and a setup switched between the two keeps both results.</summary>
    public static string NpyKey(EmSetup setup, Em3dSolverId solver)
        => ResultKey(setup, solver) + (StaticSuffix(setup) is { Length: > 0 } st ? st : EmRunService.NpyKeySuffix);

    /// <summary>The run directory, kept after the run. <c>-o</c> does not move it (R-em3d7-5c). A static
    /// setup's is its own, <c>&lt;key&gt;.palace_es</c>, beside the driven one.</summary>
    public static string RunDirectory(string resultsRoot, EmSetup setup, Em3dSolverId solver)
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
    /// brief-em3d-22 R-em3d22-1b — a static problem runs only on a solver that solves it; on another it is refused
    /// naming the one that does, before anything is looked for or started. <c>check</c> reports the same sentence.
    /// </summary>
    public static string? StaticSolverRefusal(EmSetup setup) => setup.IsStatic3D ? SolverRefusal(setup) : null;

    /// <summary>brief-em3d-114 — the diagnostic source of a terminal wave port's refusal.</summary>
    public const string TerminalPortsSource = "em3d-terminal-ports";

    /// <summary>
    /// brief-em3d-23 R-em3d23-2e / §7 / brief-emi-1 R-emi1-2c — what a chosen solver refuses of the setup's problem
    /// type (the static problems, an eigenmode solve), in its own words, before anything is looked for. Each chosen
    /// solver's descriptor is asked in order (<see cref="Em3dSolverDescriptor.Refusal"/>). brief-em3d-116 lifted the wave
    /// port from this list: openEMS feeds one as a probed line, and refuses only a hollow waveguide, which it learns from
    /// the problem (FdtdWavePorts.HollowRefusal, in the lowering — still before any process starts).
    /// </summary>
    public static string? SolverRefusal(EmSetup setup)
        => Em3dSolvers.Of(setup.Solver3D)
            .Select(d => setup.IsScattering3D && !d.Capabilities.PlaneWave ? Em3dPlaneWaves.SolverRefusal(d.DisplayName) : d.Refusal(setup))
            .FirstOrDefault(r => r is not null);

    /// <summary>
    /// brief-em3d-114 R-em3d114-2e — what the one solver <paramref name="solver"/> names refuses of a problem's
    /// multi-terminal wave ports (<see cref="Em3dSolverDescriptor.PortRefusal"/>): <paramref name="ports"/> is each such
    /// port's label and terminal count; <paramref name="shifted"/> a terminal port whose reference plane is moved, and how
    /// far. Null when it runs them all, and for a comparison, which goes on without a solver that refuses them and says so.
    /// </summary>
    public static string? TerminalPortRefusal(Em3dSolverChoice solver, IReadOnlyList<(string Label, int Terminals)> ports,
                                              bool lumpedPorts = false, (string Label, double ShiftM)? shifted = null)
        => solver.IsCompare ? null
         : Em3dSolvers.Of(solver).Select(d => d.PortRefusal(new(ports, lumpedPorts, shifted))?.Alone).FirstOrDefault(r => r is not null);

    /// <summary>The facts a solver's port refusal reads, from a built problem.</summary>
    public static Em3dTerminalFacts TerminalFacts(Em3dProblem problem)
    {
        var groups = TerminalPorts(problem.Ports);
        bool lumped = groups.Count > 0 && problem.Ports.Any(p => p.Kind == Em3dPortKind.Lumped);
        var moved = problem.Ports.FirstOrDefault(p => p.FaceGroup is not null && p.ReferencePlane.ShiftM != 0);
        string fill = problem.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(problem.Solids, problem.Materials);
        var oneSpeed = problem.Ports.Where(p => p.FaceGroup is not null).GroupBy(p => p.FaceGroup!.Value)
                              .Where(g => FdtdWavePorts.RegionMaterials(problem, g.First(), fill).Count == 1)
                              .Select(g => g.First().FaceGroupLabel ?? $"port {g.Key}").ToList();
        return new(groups, lumped, moved is null ? null : (moved.FaceGroupLabel ?? moved.Name, moved.ReferencePlane.ShiftM)) { OneSpeed = oneSpeed };
    }

    /// <summary>The problem's multi-terminal ports, as <see cref="TerminalPortRefusal"/> takes them: one entry per face group.</summary>
    public static IReadOnlyList<(string Label, int Terminals)> TerminalPorts(IReadOnlyList<Em3dPort> ports)
        => [.. ports.Where(p => p.FaceGroup is not null).GroupBy(p => p.FaceGroup!.Value).OrderBy(g => g.Key)
                    .Select(g => (g.First().FaceGroupLabel ?? $"port {g.Key}", g.Count()))];

    /// <summary>
    /// brief-em3d-31 R-em3d31-4 — why a 3D setup's radiation pattern is not computed, or null when it is (or
    /// was not asked for): a static or eigenmode problem drives no port at a frequency, so nothing radiates in
    /// the sense a pattern describes. The run carries it as a note and the panel disables the checkbox with it.
    /// </summary>
    public static string? RadiationPatternIgnored(EmSetup setup)
        => setup.RadiationPattern && setup.Is3D && setup.Problem3D != Em3dProblemType.Driven
            ? $"No radiation pattern: this setup is {(setup.Problem3D == Em3dProblemType.Eigenmode ? "an eigenmode" : "a static")} " +
              "problem, which drives no port at a frequency, so nothing radiates in the sense a pattern describes. It is " +
              "computed from a Driven 3D run (the setup's Problem3D)."
            : null;

    /// <summary>brief-em3d-31 — the frequencies a driven 3D run's pattern is produced at (every sweep point, as
    /// the planar path does), or null when no pattern was asked for or the problem is not driven.</summary>
    internal static double[]? FarFieldFrequencies(EmSetup setup, Em3dProblem problem)
        => (setup.RadiationPattern || problem.PlaneWave is not null) && problem.Type == Em3dProblemType.Driven
            ? FrequenciesHz(problem.Frequency) : null;   // brief-hzn-12 — a scattering run's far field is its result

    /// <summary>The result's name as a path: the setup's Output name when it has one, the solver-named stem otherwise. The
    /// Touchstone lands here unless the run was given a separate destination (<see cref="EmRunTarget.TouchstoneDestination"/>).</summary>
    public static string SnpBasePath(string resultsRoot, EmSetup setup, Em3dSolverId solver)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup)
            : Path.Combine(resultsRoot, ResultKey(setup, solver));

    // ── The run ──────────────────────────────────────────────────────────────────────────────
    //
    // Three phases, and only the last starts a process:
    //
    //   1. discovery (brief 6), then each solver's settings (SolverLeg.Configure)
    //   2. the problem, generated ONCE (R-em3d10-1a), then each solver's lowering (SolverLeg.Prepare) —
    //      Palace's .geo and configuration, openEMS's grid and CSXCAD model
    //   3. execution (SolverLeg.Execute): one solver, or each in turn (R-em3d10-1c)
    //
    // Everything a solver can refuse is refused in 1 or 2, so a setup asking for several solvers learns
    // that one of them cannot run before another has spent half an hour (R-em3d10-4b), and is told the
    // flag that runs the one that can. Phase 3 is where R-em3d10-4a applies: a solver failing THERE
    // leaves the others' results written.

    /// <summary>How many times a run in this process has generated its 3D problem — R-em3d10-1a's
    /// counter. A run through several solvers generates once and hands the one problem to each.</summary>
    public static long ProblemsGenerated => Interlocked.Read(ref _problemsGenerated);
    private static long _problemsGenerated;

    /// <summary>brief-cosim-pattern-1 — where one leg's result files land. <paramref name="compare"/>: one name cannot hold two
    /// results, so each solver's has its token after the name.</summary>
    internal static EmResultPaths ResultPaths(string resultsRoot, EmSetup setup, Em3dSolverId solver, bool compare, string? touchstone)
    {
        string token = Em3dSolvers.Get(solver).ResultToken;
        bool named = setup.SnpOutputPathOverride is { Length: > 0 };
        string b = compare ? BothSnpBasePath(resultsRoot, setup, solver) : SnpBasePath(resultsRoot, setup, solver);
        string suffix = StaticSuffix(setup) is { Length: > 0 } st ? st : EmRunService.NpyKeySuffix;
        string npy = named ? b + suffix + ".npy" : Path.Combine(EmResultNames.ResultsDirectory(resultsRoot), NpyKey(setup, solver) + ".npy");
        string snp = touchstone is { Length: > 0 } t ? EmResultNames.Clean(t) + (compare ? "." + token : "") : b;
        return new EmResultPaths(b, npy, snp);
    }

    /// <summary>brief-cosim-pattern-1 — test seam: a stand-in for a solver's leg in a run under the given results root, or null. Discovery
    /// is not asked, so the run's own path from lowering to publishing is held with no solver installed.</summary>
    internal static Func<string, Em3dSolverDescriptor, SolverLeg?>? LegStandIn;

    /// <summary>brief-emi-10 — test seam: called first thing in every run with its results root and the memory limit it was
    /// handed, so a test can see a flag reach the run (and cancel it by throwing) without a solver installed.</summary>
    internal static Action<string, long?>? RunStarting;

    /// <summary>The token a comparison adds to the result stem (R-em3d10-2c).</summary>
    public const string CompareToken = "compare";

    /// <summary>R-em3d10-2c — the comparison's key in <c>results/</c>: <c>&lt;key&gt;.compare_em</c>.</summary>
    public static string CompareNpyKey(EmSetup setup)
        => EmRunService.ResolveResultKey(setup) + "." + CompareToken + EmRunService.NpyKeySuffix;

    /// <summary>
    /// R-em3d10-5 — one solver's Touchstone stem in a comparison. <c>-o base</c> gives
    /// <c>base.palace</c> and <c>base.openems</c>, since one path cannot hold two results; with no
    /// <c>-o</c> it is the single-solver stem, <c>results/&lt;key&gt;.&lt;solver&gt;</c>.
    /// </summary>
    public static string BothSnpBasePath(string resultsRoot, EmSetup setup, Em3dSolverId solver)
        => setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup) + "." + Em3dSolvers.Get(solver).ResultToken
            : Path.Combine(resultsRoot, ResultKey(setup, solver));

    /// <summary>R-em3d10-2c/5 — where a comparison lands: <c>base.compare_em.npy</c> with
    /// <c>-o base</c> or an Output name, <c>results/&lt;key&gt;.compare_em.npy</c> beside the results otherwise.</summary>
    public static string ComparePath(string resultsRoot, EmSetup setup, string? touchstone = null)
        => touchstone is { Length: > 0 } t
            ? EmResultNames.Clean(t) + "." + CompareToken + EmRunService.NpyKeySuffix + ".npy"
            : setup.SnpOutputPathOverride is { Length: > 0 }
            ? EmRunService.ResolveSnpBasePath(resultsRoot, setup) + "." + CompareToken + EmRunService.NpyKeySuffix + ".npy"
            : Path.Combine(resultsRoot, CompareNpyKey(setup) + ".npy");

    /// <summary>
    /// The run. Only <see cref="EmRunService.Run"/> calls this. <paramref name="source"/> has a
    /// technology — the door checked it.
    /// </summary>
    /// <param name="confirmMemory">brief-em3d-21 R-em3d21-2b — see <see cref="EmRunService.Run"/>.</param>
    internal static EmRunResult Run(EmSetup setup, EmLayoutSource source, string resultsRoot,
                                    CancellationToken ct, RunControl? control, int? maxCores,
                                    Func<Em3dMemoryVerdict, bool>? confirmMemory = null,
                                    PalaceDevice device = PalaceDevice.Cpu, long? memoryCeilingBytes = null,
                                    EmergeDevice emergeDevice = EmergeDevice.Auto, EmRunTarget? target = null,
                                    Action<Em3dGenerationResult>? assembled = null)
        => Run(setup, s =>
               {
                   // brief-lcp-7 — the problem the run solved, last: its component ports are what the co-simulation netlist is written from.
                   var g = CircuitRF.Design.ThreeD.Em3dSetupProblem.Of(s, source, source.Technology!);
                   assembled?.Invoke(g);
                   return g;
               }, resultsRoot, ct, control, maxCores,
               confirmMemory, device: device, memoryCeilingBytes: memoryCeilingBytes, emergeDevice: emergeDevice, target: target);

    /// <summary>
    /// brief-em3d-42 — the run, from whatever builds the problem: a <c>.cem</c>'s layout through the generator,
    /// or a <c>.c3d</c> through its elaboration and problem assembly. <paramref name="build"/> is called with
    /// the setup the run uses (and again, with a smaller air box, by the memory check's remedy), so both
    /// geometry routes get every step below — discovery, lowering, memory, execution — from ONE body.
    /// </summary>
    /// <param name="keepRecord">brief-em3d-98 R-em3d98-2 — called with a leg's solver and run directory when that leg completed
    /// (or did not converge), before its status says so: a 3D view's run keeps its inputs record there (EmRunService.RunThreeDView).
    /// Per LEG, so a comparison whose second leg is cancelled still keeps the first's.</param>
    /// <param name="device">brief-em3d-136 — where Palace solves: a machine setting, passed in like
    /// <paramref name="maxCores"/> and hashed nowhere. The Palace leg only; openEMS is unaffected.</param>
    /// <param name="target">brief-cosim-pattern-1 — what this run is asked beyond the setup.</param>
    /// <param name="threeDViewText">brief-cosim-pattern-1 R-cp1-1 — the 3D view's document as text, for the record.</param>
    internal static EmRunResult Run(EmSetup setup, Func<EmSetup, Em3dGenerationResult> build, string resultsRoot,
                                    CancellationToken ct, RunControl? control, int? maxCores,
                                    Func<Em3dMemoryVerdict, bool>? confirmMemory = null,
                                    Action<Em3dSolverId, string>? keepRecord = null,
                                    PalaceDevice device = PalaceDevice.Cpu, long? memoryCeilingBytes = null,
                                    EmergeDevice emergeDevice = EmergeDevice.Auto, EmRunTarget? target = null, string? threeDViewText = null)
    {
        RunStarting?.Invoke(resultsRoot, memoryCeilingBytes);
        var log = new RunLog(control);
        var memory = new MemoryGate(confirmMemory);
        var choice = setup.Solver3D;
        bool compare = choice.IsCompare;

        if (StaticSolverRefusal(setup) is { } staticOnly)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-static", staticOnly));
        if (SolverRefusal(setup) is { } solverOnly)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-palace-only", solverOnly));

        // ── brief 6: every program found, validated and probed, before anything else ─────────
        // brief-em3d-23 R-em3d23-1c — probed for what THIS setup needs, so a build without an eigensolver
        // is refused here, before Gmsh.
        // brief-em3d-136 R-em3d136-3d — and, for a GPU run, whether this Palace solves on a GPU and how many it sees.
        bool standIn = Em3dSolvers.Of(choice).Any(d => LegStandIn?.Invoke(resultsRoot, d) is not null);
        var readiness = standIn ? [] : SolverDiscovery.ReadinessFor(choice, setup, device);
        if (readiness.FirstOrDefault(r => !r.Proceeds) is { } blocked)
        {
            var unavailable = EmDiagnostics.SolverUnavailable(blocked.Name, blocked.Refusal!, Install.SolverInstaller.OfferFor(blocked));
            if (!compare) return log.Result(EmRunStatus.Refused, unavailable);
            var ready = Em3dSolvers.Of(choice).Where(d => readiness.Where(r => d.Tools.Contains(r.Tool)).All(r => r.Proceeds)).ToList();
            return log.Result(EmRunStatus.Refused, RefusedBeforeWork(choice.Solvers.Count,
                string.Join(" ", readiness.Where(r => !r.Proceeds).Select(r => r.Refusal)), Remedy(ready)));
        }
        var legs = Em3dSolvers.Of(choice).Select(d => LegStandIn?.Invoke(resultsRoot, d) ?? d.NewLeg()).ToList();
        if (legs.Count == 0) return log.Result(EmRunStatus.Refused, EmDiagnostics.ThreeDSolverNotBuilt(choice.ToString()));
        // brief-dfr35-1 R-dfr35-1-5 — the mesh Gmsh makes for Palace, whichever else a comparison runs: the other solvers mesh
        // inside their own drivers, or not at all.
        bool meshOnly = control?.MeshOnly == true;
        if (meshOnly)
        {
            if (legs.FirstOrDefault(l => l.Solver.Id == PalaceSolver.Id) is not { } meshing)
                return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded(MeshOnlySource, MeshOnlyRefusal(Em3dSolvers.Names(Em3dSolvers.Of(choice)))));
            legs = [meshing];
            compare = false;
        }

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

        // ── each solver's settings ────────────────────────────────────────────────────────────
        var run = new RunContext(setup, build, resultsRoot, ct, control, maxCores, device, readiness, log, memory, memoryCeilingBytes,
                                 emergeDevice) { Target = target, ThreeDViewText = threeDViewText };
        foreach (var leg in legs) leg.Configure(run);
        using var legHolds = new Holds([.. legs.Select(l => l.Hold(holder)).OfType<IDisposable>()]);
        // One solver stops at its first refusal. A comparison goes on while any could still run, so the
        // refusal can say whether another one would have.
        if ((!compare || legs.All(l => l.Stop is not null)) && legs.FirstOrDefault(l => l.Stop is not null)?.Stop is { } early)
            return compare ? CompareRefused(legs, log) : log.Result(early.Status, early.Diagnostic);

        // ── brief 3: the problem, once ────────────────────────────────────────────────────────
        control?.BeginStage("building the 3D problem");
        Interlocked.Increment(ref _problemsGenerated);
        var generated = build(setup);
        foreach (var (sentence, fix) in generated.ViaNetFixes) log.Offers[sentence] = fix;
        log.Notes.AddRange(generated.Notes);
        log.Warnings.AddRange(generated.Warnings);
        if (!generated.Ok) return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", generated.Refusal));
        var problem = generated.Problem!;
        run.Generated = generated;
        if (problem.Validate() is { Count: > 0 } invalid)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", string.Join(" ", invalid)));
        // brief-hzn-11 R-hzn11-2 — a hollow guide below its cutoff, refused before anything is meshed
        if (Em3dModePorts.BelowCutoffRefusal(problem, generated.WantsDc) is { } belowCutoff)
            return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", belowCutoff));
        // brief-hzn-11 R-hzn11-3 — a solver that cannot run a hollow guide or report S normalised to the mode: refused alone,
        // left out of a comparison with a note
        foreach (var leg in legs.ToList())
        {
            if (Em3dModePorts.SolverRefusal(problem, leg.Solver.Id) is not { } modeRefusal) continue;
            if (!compare) return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded("em3d-problem", modeRefusal));
            log.Notes.Add(modeRefusal + $" This comparison ran without {leg.Solver.DisplayName}.");
            legs.Remove(leg);
        }
        if (compare && legs.Count == 1)
        {
            compare = false;
            if (legs[0].Stop is { } remaining) return log.Result(remaining.Status, remaining.Diagnostic);
        }
        // brief-em3d-114 R-em3d114-2e — whatever built the problem; a .c3d's run has already refused before discovery.
        // brief-em3d-116 R-em3d116-3 / brief-em3d-115 R-em3d115-2d — in a comparison, a solver refusing the terminal ports is
        // skipped and the run says so. A two-terminal face runs on both.
        var terminalFacts = TerminalFacts(problem);
        foreach (var leg in legs.ToList())
        {
            if (leg.Solver.PortRefusal(terminalFacts) is not { } refusesPorts) continue;
            if (!compare) return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded(TerminalPortsSource, refusesPorts.Alone));
            log.Notes.Add(refusesPorts.InCompare);
            legs.Remove(leg);
        }
        if (compare && legs.Count == 1)
        {
            compare = false;
            if (legs[0].Stop is { } remaining) return log.Result(remaining.Status, remaining.Diagnostic);
        }

        // ── brief-sar-6 R-sar6-1 — the Sar group, resolved against the problem by the solver that writes its dump ─────────
        if (setup.RequestsSar)
        {
            if (!legs.Any(l => l.Solver.Capabilities.Sar))
            {
                string sarSolvers = Em3dSolvers.Names(Em3dSolvers.With(c => c.Sar));
                return log.Result(EmRunStatus.Refused, EmDiagnostics.Forwarded(SarSource,
                    $"This setup asks for SAR, and only {sarSolvers} writes the raw SAR dump it is computed on: set its 3D solver to " +
                    $"{sarSolvers}, or a comparison that includes it."));
            }
            foreach (var leg in legs.Where(l => l.Solver.Capabilities.Sar))
                if (leg.AdmitSar(problem, generated, run) is { } sarStop) return log.Result(sarStop.Status, sarStop.Diagnostic);
            if (compare)
                log.Notes.Add($"SAR and B1 come with {Em3dSolvers.Names(legs.Where(l => l.Solver.Capabilities.Sar).Select(l => l.Solver))}'s " +
                              $"result only; {Em3dSolvers.Names(legs.Where(l => !l.Solver.Capabilities.Sar).Select(l => l.Solver))}'s carries none.");
        }

        // ── each solver's lowering: no process yet ────────────────────────────────────────────
        foreach (var leg in legs) leg.Prepare(problem, run);
        if (RadiationPatternIgnored(setup) is { } noPattern) log.Notes.Add(noPattern);
        if (legs.FirstOrDefault(l => l.Stop is not null)?.Stop is { } refused)
            return compare ? CompareRefused(legs, log) : log.Result(refused.Status, refused.Diagnostic);

        // ── brief-emdc-2 R-emdc2-2 — the 0 Hz row: ONE conduction solve, before any leg executes, which every leg splices ───
        if (generated.WantsDc && !meshOnly) run.Dc = SolveDc(problem, setup, resultsRoot, run);

        // ── execution ─────────────────────────────────────────────────────────────────────────
        // brief-dfr35-1 R-dfr35-1-5 — no status.json: a mesh is not a run's result, and beginning one would drop the record of
        // the result already in that folder.
        run.Compare = compare;
        if (meshOnly) return Single(legs[0].Execute(problem, run.Paths(legs[0].Solver.Id).SnpBase, run), log);
        // R-cp1-3 — what a killed run of this setup left is gone before this one spends anything.
        foreach (var leg in legs) EmResultStaging.RemoveLeftovers(run.Paths(leg.Solver.Id));
        if (!compare)
        {
            var only = legs[0];
            return Single(Recorded(setup, resultsRoot, only.Solver.Id, keepRecord, () =>
                only.Execute(problem, run.Paths(only.Solver.Id).SnpBase, run)), log);
        }
        return RunCompare(legs, choice.Reference, problem, setup, resultsRoot, run, keepRecord);
    }

    /// <summary>brief-emdc-2 — test seam: discovery's answer for the DC row's Gmsh, in place of the real one.</summary>
    internal static Func<SolverReadiness>? DcGmsh;

    /// <summary>How many DC conduction solves runs in this process have made: one per run, however many solvers it runs.</summary>
    public static long DcSolves => Interlocked.Read(ref _dcSolves);
    private static long _dcSolves;

    /// <summary>The DC row's mesh and its script, beside the solvers' run directories: one per setup, shared by every leg.</summary>
    public static string DcDirectory(string resultsRoot, EmSetup setup)
        => Path.Combine(resultsRoot, EmRunService.ResolveResultKey(setup) + ".dc");

    /// <summary>
    /// R-emdc2-2 — the run's one DC conduction solve (Em3dDcSolve), as its own stage. Its notes join the run's; a refusal does
    /// not stop the run (the RF answer is still right) and becomes a warning saying what it costs, with the install offer a
    /// solver's refusal carries when the cause is Gmsh missing.
    /// </summary>
    private static Em3dDcResult SolveDc(Em3dProblem problem, EmSetup setup, string resultsRoot, RunContext run)
    {
        run.Control?.BeginStage("DC conduction solve");
        Interlocked.Increment(ref _dcSolves);
        var gmsh = DcGmsh?.Invoke() ?? SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh));
        var dc = Em3dDcSolve.Solve(problem, DcDirectory(resultsRoot, setup), run.Ct, gmsh);
        run.Log.Notes.AddRange(dc.Notes);
        if (dc.Refusal is { } refusal)
        {
            string warning = Em3dDcRow.RefusedWarning(refusal);
            // Before the sentence, so a run posting it as it happens offers the install on its row (brief-dfr27-5).
            if (!gmsh.Proceeds && Install.SolverInstaller.OfferFor(gmsh) is { } tool) run.Log.Installs[warning] = tool;
            run.Log.Warnings.Add(warning);
        }
        return dc;
    }

    /// <summary>
    /// R-emdc2-3 — a leg's DataSet with the run's 0 Hz row at the front of its S group, before either of its files is written;
    /// and the header lines its <c>.sNp</c> says it with (R-emdc2-4). Nothing, and no line, when the sweep asked for no 0 Hz.
    /// </summary>
    internal static IReadOnlyList<string> SpliceDc(DataSet data, Em3dProblem problem, Em3dDcResult? dc)
    {
        if (dc is null) return [];
        if (dc.S is not null && data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is { } group)
            Em3dDcRow.Prepend(data, group, dc);
        return Em3dDcRow.HeaderLines(dc, problem);
    }

    /// <summary>
    /// brief-em3d-98 R-em3d98-2 — one leg, with its <c>status.json</c>: <c>running</c> (and the previous run's record removed)
    /// before the solver touches the directory, how it ended on every exit path — an exception and a cancellation included —
    /// and, when it completed or did not converge, its inputs record kept first (<paramref name="keep"/>).
    /// </summary>
    internal static Leg Recorded(EmSetup setup, string resultsRoot, Em3dSolverId solver, Action<Em3dSolverId, string>? keep, Func<Leg> execute)
    {
        string dir = RunDirectory(resultsRoot, setup, solver);
        var started = C3dRunStatus.Begin(dir);
        Leg leg;
        try { leg = execute(); }
        catch (OperationCanceledException) { C3dRunStatus.Finish(dir, started, C3dRunState.Cancelled); throw; }
        catch (Exception e) { C3dRunStatus.Finish(dir, started, C3dRunState.Failed, FirstSentence(e.Message)); throw; }
        var state = leg.Status switch
        {
            EmRunStatus.Ok        => leg.NotConverged is null ? C3dRunState.Complete : C3dRunState.NotConverged,
            EmRunStatus.Cancelled => C3dRunState.Cancelled,
            _                     => C3dRunState.Failed,
        };
        if (state is C3dRunState.Complete or C3dRunState.NotConverged) keep?.Invoke(solver, dir);
        C3dRunStatus.Finish(dir, started, state, state switch
        {
            C3dRunState.NotConverged => leg.NotConverged,
            C3dRunState.Failed       => leg.Stop is { } d ? FirstSentence(d.Render()) : null,
            _                        => null,
        });
        return leg;
    }

    /// <summary>A tooltip's worth of <paramref name="text"/>: its first sentence, at most 240 characters.</summary>
    private static string FirstSentence(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0) text = text[..(end + 1)];
        return text.Length <= 240 ? text : text[..239] + "…";
    }

    /// <summary>
    /// R-em3d10-1 / brief-emi-6 R-emi6-3 — each solver in turn on the one problem, in registry order; then every other solver
    /// compared against <paramref name="reference"/>. A failure of one keeps the others' results (R-em3d10-4a); a cancellation
    /// keeps what had already finished and writes nothing for what was running (R-em3d10-4c). Two solvers say it as Both always
    /// has.
    /// </summary>
    private static EmRunResult RunCompare(IReadOnlyList<SolverLeg> legs, Em3dSolverId reference, Em3dProblem problem, EmSetup setup,
                                          string resultsRoot, RunContext run, Action<Em3dSolverId, string>? keepRecord)
    {
        var (log, control) = (run.Log, run.Control);
        bool two = legs.Count == 2;
        log.Notes.Add($"{(two ? "Both solvers run" : $"All {SolverCount(legs.Count)} solvers run")} on the one 3D problem generated above, one after the other — " +
                      $"{string.Join(", then ", legs.Select(l => l.Solver.DisplayName))} — because each already uses every core.");

        string comparePath = ComparePath(resultsRoot, setup, run.Target?.TouchstoneDestination);

        // An earlier run's comparison describes files this run rewrites. Left in place past a failed
        // leg or a refused comparison, it would sit beside the fresh result disagreeing with it; only
        // this run may put one there. Once a leg has published (R-cp1-3: until then, nothing changed).
        // This file only, as ResultsWriter deletes only its own.
        bool cleared = false;
        void ClearComparison()
        {
            if (cleared) return;
            cleared = true;
            try { if (File.Exists(comparePath)) File.Delete(comparePath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warnings.Add($"An earlier comparison, '{comparePath}', could not be removed " +
                                 $"({e.Message}); it does not describe this run.");
            }
        }

        var done = new List<Leg>();
        for (int k = 0; k < legs.Count; k++)
        {
            var leg = legs[k];
            control?.BeginStage($"{leg.Solver.DisplayName}, the {Ordinal(k)} of {SolverCount(legs.Count)} solvers");
            var ran = Recorded(setup, resultsRoot, leg.Solver.Id, keepRecord, () =>
                leg.Execute(problem, run.Paths(leg.Solver.Id).SnpBase, run));
            if (ran.SnpPath is not null || ran.NpyPath is not null) ClearComparison();
            if (ran.Status != EmRunStatus.Cancelled) { done.Add(ran); continue; }

            // What had already failed is the other half of what this run found.
            foreach (var failed in done.Where(l => !l.Ok)) log.Errors.Add(failed.Stop!.Render());
            var kept = done.Where(l => l.Ok).ToList();
            return log.Result(EmRunStatus.Cancelled,
                              kept.Count > 0 ? EmDiagnostics.CancelledKeeping(Name(ran.Solver), Names(kept), string.Join("; ", kept.Select(Where)))
                                             : EmDiagnostics.Cancelled(),
                              Outputs(kept));
        }

        var ok = done.Where(l => l.Ok).ToList();
        var outputs = Outputs(ok);
        var bad = done.Where(l => !l.Ok).ToList();
        if (ok.Count == 0)
            return log.Result(bad[0].Status, two && legs[0].Solver.Id == PalaceSolver.Id && legs[1].Solver.Id == OpenEmsSolver.Id
                ? EmDiagnostics.BothSolversFailed(bad[0].Stop!.Render(), bad[1].Stop!.Render())
                : EmDiagnostics.NoSolverSucceeded(string.Join(" ", bad.Select(l => $"{Name(l.Solver)}: {l.Stop!.Render()}"))));
        if (two && bad.Count == 1)
            return log.Result(bad[0].Status, EmDiagnostics.OneSolverFailed(Name(bad[0].Solver), bad[0].Stop!.Render(), Name(ok[0].Solver),
                                                                          Where(ok[0])), outputs);

        // ── the comparison (R-em3d10-2, 3) — of what completed, when the reference did and another did too ──────────────
        var against = ok.FirstOrDefault(l => l.Solver == reference);
        var others = ok.Where(l => l.Solver != reference).ToList();
        if (bad.Count > 0 && (against is null || others.Count == 0))
            return log.Result(bad[0].Status, EmDiagnostics.SomeSolversFailed(Failures(bad), Names(ok), string.Join("; ", ok.Select(Where)),
                against is null ? $"No comparison was written, because it is against {Name(reference)}."
                                : "No comparison was written, because it needs another result to compare."), outputs);

        // An eigenmode comparison has no S to difference: each solver's modes are in its own result.
        if (problem.Type != Em3dProblemType.Driven)
        {
            log.Notes.Add("Each solver's result is its own file; a comparison file is written for a driven problem's S-parameters only.");
            return bad.Count > 0
                ? log.Result(bad[0].Status, EmDiagnostics.SomeSolversFailed(Failures(bad), Names(ok), string.Join("; ", ok.Select(Where)), ""), outputs)
                : new EmRunResult(EmRunStatus.Ok, against!.Data, null, null, null, null, null, log.Warnings,
                                  KernelName: string.Join(" and ", ok.Select(l => l.KernelName)), Notes: log.Notes, Errors: log.Errors,
                                  Outputs: outputs, MessageFiles: log.FilesOrNull, MessageInstalls: log.InstallsOrNull, MessageOffers: log.OffersOrNull);
        }

        var compared = Em3dComparison.Compare(ComparisonLeg(against!), [.. others.Select(ComparisonLeg)], problem);
        if (compared.Data is not { } data)
            return log.Result(EmRunStatus.EngineError, done.Count == 2 ? EmDiagnostics.ComparisonRefused(compared.Refusal!)
                                                                      : EmDiagnostics.ComparisonRefusedMany(compared.Refusal!), outputs);

        log.Warnings.AddRange(compared.Warnings);
        log.Notes.InsertRange(0, [compared.Summary!, .. compared.Notes]);     // the line a user reads first

        string path = comparePath;
        var (written, compareError) = EmResultStaging.PublishNpy(path, data);
        if (written is not null) outputs.Add(new("npy", written));
        if (compareError is not null) log.Errors.Add($"The comparison could not be written to '{path}': {compareError}");

        if (bad.Count > 0)
            return log.Result(bad[0].Status, EmDiagnostics.SomeSolversFailed(Failures(bad), Names(ok), string.Join("; ", ok.Select(Where)),
                written is null ? "The comparison of what completed could not be written."
                                : $"{Names(ok)} were compared: {written}."), outputs);
        return new EmRunResult(EmRunStatus.Ok, data, null, null, written, null, null, log.Warnings,
                               KernelName: string.Join(" and ", ok.Select(l => l.KernelName)), Notes: log.Notes, Errors: log.Errors,
                               Outputs: outputs, MessageFiles: log.FilesOrNull, MessageInstalls: log.InstallsOrNull, MessageOffers: log.OffersOrNull);

        static string Failures(IEnumerable<Leg> failed) => string.Join("; ", failed.Select(l => $"{Name(l.Solver)} failed: {l.Stop!.Render()}"));
    }

    /// <summary>A completed leg as the comparison takes it: its descriptor's name, token and method, its S and its facts.</summary>
    private static Em3dComparisonLeg ComparisonLeg(Leg leg)
    {
        var d = Em3dSolvers.Get(leg.Solver);
        return new Em3dComparisonLeg(d.DisplayName, d.ResultToken, d.Method, leg.Data!["S"], leg.Facts);
    }

    /// <summary>Each completed leg's Touchstone, in run order, then each one's <c>.npy</c> (brief-emb-3 R-emb3-4: the
    /// Touchstone is what a caller wants first, and it should never have to put the path together itself).</summary>
    private static List<EmRunOutput> Outputs(IEnumerable<Leg> completed)
    {
        var legs = completed.ToList();
        var outputs = new List<EmRunOutput>();
        foreach (var leg in legs) if (leg.SnpPath is { } snp) outputs.Add(new("touchstone", snp));
        foreach (var leg in legs) if (leg.NpyPath is { } npy) outputs.Add(new("npy", npy));
        foreach (var leg in legs) if (leg.RecordPath is { } record) outputs.Add(new("record", record));
        return outputs;
    }

    private static string Ordinal(int k) => k switch { 0 => "first", 1 => "second", 2 => "third", 3 => "fourth", _ => $"{k + 1}th" };

    private static string SolverCount(int n) => n switch { 2 => "two", 3 => "three", 4 => "four", _ => n.ToString(CultureInfo.InvariantCulture) };

    private static string Name(Em3dSolverId s) => Em3dSolvers.Get(s).DisplayName;

    private static string Names(IEnumerable<Leg> legs) => Em3dSolvers.Names(legs.Select(l => Em3dSolvers.Get(l.Solver)));

    /// <summary>Where a leg's result is, for a sentence naming it.</summary>
    private static string Where(Leg leg)
        => string.Join(" and ", new[] { leg.SnpPath, leg.NpyPath }.OfType<string>()) is { Length: > 0 } w
            ? w : "(its files could not be written; the errors above say why)";

    /// <summary>R-em3d10-4b's pointer to what would run: the one ready solver alone, or the ready ones compared.</summary>
    private static string Remedy(IReadOnlyList<Em3dSolverDescriptor> ready)
        => ready.Count switch
        {
            0 => "",
            1 => $" {ready[0].DisplayName} is ready: run it alone with `circuitrf em --solver {ready[0].ResultToken}`, or set the setup's " +
                 $"Solver3D to {ready[0].Spelling}.",
            _ => $" {Em3dSolvers.Names(ready)} are ready: compare them with `circuitrf em --solver " +
                 $"{string.Join(",", ready.Select(d => d.ResultToken))}`.",
        };

    /// <summary>A comparison refused before any work: every leg's refusal, and what would run.</summary>
    private static EmRunResult CompareRefused(IReadOnlyList<SolverLeg> legs, RunLog log)
    {
        string text = string.Join(" ", legs.Select(l => l.Stop).OfType<Stop>().Select(s => s.Diagnostic.Render()));
        return log.Result(legs.First(l => l.Stop is not null).Stop!.Status,
                          RefusedBeforeWork(legs.Count, text, Remedy([.. legs.Where(l => l.Stop is null).Select(l => l.Solver)])));
    }

    private static Diagnostic RefusedBeforeWork(int solvers, string refusal, string remedy)
        => solvers == 2 ? EmDiagnostics.BothRefusedBeforeWork(refusal, remedy) : EmDiagnostics.CompareRefusedBeforeWork(refusal, remedy);

    private static EmRunResult Single(Leg leg, RunLog log)
        => leg.Ok
            ? new EmRunResult(EmRunStatus.Ok, leg.Data, null, null, leg.NpyPath, leg.SnpPath, null, log.Warnings,
                              Notes: log.Notes, Errors: log.Errors, KernelName: leg.KernelName, MessageFiles: log.FilesOrNull,
                              MessageInstalls: log.InstallsOrNull, MessageOffers: log.OffersOrNull, MeshOnly: leg.MeshOnly,
                              RecordPath: leg.RecordPath)
            : log.Result(leg.Status, leg.Stop!);

    /// <summary>Why a phase did not go on, and which status says so.</summary>
    internal sealed record Stop(EmRunStatus Status, Diagnostic Diagnostic);

    /// <summary>One solver's execution: its status, and on success its data and files.</summary>
    /// <param name="Facts">What makes a difference from the reference expected (Em3dComparison) — the solver's comparison
    /// facts, set by a solver whose results are compared against another's (openEMS today).</param>
    /// <param name="NotConverged">brief-em3d-98 — a successful leg's solver said it did not converge (its own signal, never a
    /// criterion of ours): the sentence its <c>status.json</c> carries. Null when it converged.</param>
    internal sealed record Leg(Em3dSolverId Solver, EmRunStatus Status, Diagnostic? Stop, DataSet? Data,
                              string? NpyPath, string? SnpPath, string KernelName, Em3dComparisonFacts? Facts = null,
                              string? NotConverged = null, Em3dMeshOnlyReport? MeshOnly = null, string? RecordPath = null)
    {
        public bool Ok => Status == EmRunStatus.Ok;
        public static Leg Failed(Em3dSolverId s, EmRunStatus status, Diagnostic d) => new(s, status, d, null, null, null, "");
    }

    /// <summary>
    /// brief-emi-1 R-emi1-1b — one solver's part of one run. A descriptor makes a fresh one per run
    /// (<see cref="Em3dSolverDescriptor.NewLeg"/>); the run calls it in three phases and only the last starts a process.
    /// A phase that cannot go on sets <see cref="Stop"/>; whether that ends the run is the run's decision (a comparison
    /// goes on while another solver could still run).
    /// </summary>
    internal abstract class SolverLeg(Em3dSolverDescriptor solver)
    {
        public Em3dSolverDescriptor Solver { get; } = solver;

        /// <summary>Why this leg cannot run, once a phase has found it.</summary>
        public Stop? Stop { get; protected set; }

        /// <summary>Phase 1 — its settings, from what discovery found. Its notes go to the run's log in order.</summary>
        public abstract void Configure(RunContext run);

        /// <summary>Something held until the run returns (a Linux subsystem distribution the run uses), or null.</summary>
        public virtual IDisposable? Hold(string holder) => null;

        /// <summary>Phase 2, for a solver with <see cref="Em3dSolverCapabilities.Sar"/> — the setup's Sar group resolved
        /// against the problem. A refusal here ends the run.</summary>
        public virtual Stop? AdmitSar(Em3dProblem problem, Em3dGenerationResult generated, RunContext run) => null;

        /// <summary>Phase 2 — its lowering of the one problem; no process.</summary>
        public abstract void Prepare(Em3dProblem problem, RunContext run);

        /// <summary>Phase 3 — the solve, its results written to <paramref name="snpBase"/> and <c>results/</c>.</summary>
        public abstract Leg Execute(Em3dProblem problem, string snpBase, RunContext run);
    }

    /// <summary>What every leg of one run shares: the setup, the inputs the caller passed, discovery's answer and the log.</summary>
    /// <param name="MemoryCeilingBytes">brief-emi-10 R-emi10-4 — the user's memory limit for a solver (Settings ▸ Solvers,
    /// <c>--memory-limit</c>), or null for this machine's memory. An ARGUMENT, as the core cap is.</param>
    /// <param name="EmergeDevice">brief-emi-9 R-emi9-3 — where an EMerge leg factorises (Settings ▸ Solvers, <c>em --device</c>).
    /// An ARGUMENT, as <paramref name="Device"/> is for Palace, hashed nowhere.</param>
    internal sealed record RunContext(EmSetup Setup, Func<EmSetup, Em3dGenerationResult> Build, string ResultsRoot,
                                      CancellationToken Ct, RunControl? Control, int? MaxCores, PalaceDevice Device,
                                      IReadOnlyList<SolverReadiness> Readiness, RunLog Log, MemoryGate Memory,
                                      long? MemoryCeilingBytes = null, EmergeDevice EmergeDevice = EmergeDevice.Auto)
    {
        /// <summary>brief-emdc-2 R-emdc2-2 — the run's one DC conduction solve, which every leg splices; null when the sweep
        /// asked for no 0 Hz point. A refused one has no S, and its leg's file says so.</summary>
        public Em3dDcResult? Dc { get; set; }

        /// <summary>brief-cosim-pattern-1 — what the caller asked of this run beyond the setup.</summary>
        public EmRunTarget? Target { get; init; }

        /// <summary>brief-cosim-pattern-1 R-cp1-1 — the 3D view's document as text, when the run came from one.</summary>
        public string? ThreeDViewText { get; init; }

        /// <summary>What the run's problem was built from, once it has been: the component ports the record names.</summary>
        public Em3dGenerationResult? Generated { get; set; }

        /// <summary>This run compares solvers, so each one's result has its token after the name.</summary>
        public bool Compare { get; set; }

        /// <summary>Where <paramref name="solver"/>'s result files land.</summary>
        public EmResultPaths Paths(Em3dSolverId solver)
            => ResultPaths(ResultsRoot, Setup, solver, Compare, Target?.TouchstoneDestination);

        /// <summary>brief-emi-10 R-emi10-2 — what this run's processes are held to: the ceiling (this machine's memory, or the
        /// user's lower limit), lifted once the user forces the run past its size check.</summary>
        public ProcessLimits Limits(long physicalBytes)
            => ProcessLimits.For(physicalBytes, MemoryCeilingBytes) with { Lifted = () => Memory.Forced };
    }

    /// <summary>brief-emi-10 — a process stopped by the memory ceiling or the disk floor, as the leg's refusal: the run's own,
    /// not the solver's failure (the CLI exits 1, not 2).</summary>
    internal static Leg StoppedLeg(Em3dSolverId solver, string name, ProcessStop stop, ProcessLimits limits,
                                   IReadOnlyList<Em3dMemoryRemedy> remedies, string? logPath, string? before = null)
        => Leg.Failed(solver, EmRunStatus.Refused, EmDiagnostics.Forwarded(
               stop.Reason == ProcessStopReason.Memory ? MemoryCeilingSource : DiskFloorSource,
               before + stop.Sentence(name, limits.CeilingOwner, remedies, logPath)));

    /// <summary>The diagnostic source of <c>--mesh-only</c>'s refusal of a setup Gmsh does not mesh for.</summary>
    public const string MeshOnlySource = "mesh-only";

    /// <summary>brief-dfr35-1 R-dfr35-1-5 — why a setup with no Palace leg has nothing for <c>--mesh-only</c> to make.</summary>
    /// <param name="asked">What was asked for, as the caller spells it: the option, or the 3D view's button.</param>
    public static string MeshOnlyRefusal(string solver, string asked = "--mesh-only")
        => $"{asked} makes the mesh Gmsh writes for Palace, and this setup runs on {solver}, which meshes in its own run or not at all.";

    /// <summary>brief-dfr36-1 R-dfr36-1-1 — why a 3D view's Mesh cannot run <paramref name="setup"/>, or null: a thermal setup is
    /// meshed by Gmsh too, and an EM setup needs a solver Gmsh meshes for.</summary>
    public static string? MeshButtonRefusal(EmSetup setup)
    {
        if (setup.IsThermal) return null;
        if (!setup.Is3D) return $"This setup is a planar analysis, and {C3dSetups.PlanarRefusal}.";
        return Em3dSolvers.FirstWith(setup.Solver3D, c => c.GmshMesh) is not null ? null
            : MeshOnlyRefusal(Em3dSolvers.Names(Em3dSolvers.Of(setup.Solver3D)), "Mesh");
    }

    /// <summary>brief-dfr36-1 R-dfr36-1-3 — whether a run of <paramref name="setup"/> on <paramref name="problem"/> would reuse the mesh
    /// in its folder: the script it would write against the one that mesh was made from.</summary>
    public static Em3dMeshCurrency MeshCurrency(Em3dProblem problem, EmSetup setup, string resultsRoot)
    {
        var settings = PalaceSettings.Resolve(setup.Palace);
        var lowering = GmshGeoWriter.Write(problem, settings);
        if (!lowering.Ok) return Em3dMeshCurrency.Stale;
        return PalaceRun.HasMeshOf(RunDirectory(resultsRoot, setup, PalaceSolver.Id), lowering,
                                   () => GmshGeoWriter.Write(problem, settings with { StraightSided = true }), out _)
            ? Em3dMeshCurrency.Current : Em3dMeshCurrency.Stale;
    }

    /// <summary>The diagnostic source of a run stopped at its memory ceiling.</summary>
    public const string MemoryCeilingSource = "memory-ceiling";

    /// <summary>The diagnostic source of a run stopped, or not started, below the free-disk floor.</summary>
    public const string DiskFloorSource = "disk-floor";

    /// <summary>The run's three lists (R-emcli-6), shared by every leg of a comparison. brief-dfr27-5 R-dfr27-5-3 — a note or
    /// a warning goes to <paramref name="control"/>'s <see cref="RunControl.Notes"/> the moment it is added, with the file or
    /// install it carries; set those in <see cref="Files"/>/<see cref="Installs"/> BEFORE adding the sentence.</summary>
    internal sealed class RunLog
    {
        public RunLog(RunControl? control = null)
        {
            Action<RunNote>? sink = control?.Notes;
            Notes    = new(sink is null ? null : t => sink(Posted(false, t)));
            Warnings = new(sink is null ? null : t => sink(Posted(true, t)));
        }

        public RunLines Notes { get; }
        public RunLines Warnings { get; }
        public List<string> Errors { get; } = [];

        private RunNote Posted(bool warning, string text)
            => new(warning, text, Files.GetValueOrDefault(text), Installs.GetValueOrDefault(text), Offers.GetValueOrDefault(text));

        /// <summary>One of the log's lists: what a <see cref="List{T}"/> was, posting each sentence as it is added.</summary>
        internal sealed class RunLines(Action<string>? post) : IReadOnlyList<string>, ICollection<string>
        {
            private readonly List<string> _items = [];

            public int Count => _items.Count;
            public bool IsReadOnly => false;
            public bool Contains(string item) => _items.Contains(item);
            public void CopyTo(string[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
            void ICollection<string>.Clear() => throw new NotSupportedException();
            bool ICollection<string>.Remove(string item) => throw new NotSupportedException();
            public string this[int index] => _items[index];
            public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public void Add(string text)
            {
                _items.Add(text);
                post?.Invoke(text);
            }

            public void AddRange(IEnumerable<string> texts)
            {
                foreach (string t in texts) Add(t);
            }

            /// <summary>At <paramref name="index"/> in the result's order; posted now, as everything else is.</summary>
            public void InsertRange(int index, IEnumerable<string> texts)
            {
                var list = texts.ToList();
                _items.InsertRange(index, list);
                foreach (string t in list) post?.Invoke(t);
            }
        }

        /// <summary>The file a sentence in the three lists names, keyed by the sentence (see
        /// <see cref="EmRunResult.MessageFiles"/>).</summary>
        public Dictionary<string, string> Files { get; } = [];

        public IReadOnlyDictionary<string, string>? FilesOrNull => Files.Count > 0 ? Files : null;

        /// <summary>brief-emdc-2 — the tool a sentence in the three lists offers to install, keyed by the sentence (see
        /// <see cref="EmRunResult.MessageInstalls"/>).</summary>
        public Dictionary<string, string> Installs { get; } = [];

        public IReadOnlyDictionary<string, string>? InstallsOrNull => Installs.Count > 0 ? Installs : null;

        /// <summary>brief-dfr34-4 — what a sentence offers beside it, keyed by the sentence (see
        /// <see cref="EmRunResult.MessageOffers"/>).</summary>
        public Dictionary<string, object> Offers { get; } = [];

        public IReadOnlyDictionary<string, object>? OffersOrNull => Offers.Count > 0 ? Offers : null;

        public EmRunResult Result(EmRunStatus status, Diagnostic d, IReadOnlyList<EmRunOutput>? outputs = null)
            => new(status, null, null, null, null, null, d.Render(), Warnings,
                   Notes: Notes, Errors: Errors, Diagnostic: d, Outputs: outputs is { Count: > 0 } ? outputs : null,
                   MessageFiles: FilesOrNull, MessageInstalls: InstallsOrNull, MessageOffers: OffersOrNull);
    }

    /// <summary>A run's sentences, added to whichever list a writer was handed — a <see cref="RunLog"/>'s, which posts each
    /// as it is added (brief-dfr27-5), or a plain one.</summary>
    internal static void AddRange(this ICollection<string> to, IEnumerable<string> sentences)
    {
        foreach (string s in sentences) to.Add(s);
    }

    /// <summary>Several holds released together.</summary>
    private sealed class Holds(IReadOnlyList<IDisposable> holds) : IDisposable
    {
        public void Dispose() { foreach (var h in holds) h.Dispose(); }
    }

    /// <summary>A static or eigenmode problem's grouped <c>.npy</c>; null, with an error, when it could not be written.</summary>
    private static string? WriteNpy(string npyPath, DataSet data, RunLog log)
    {
        var (written, error) = EmResultStaging.PublishNpy(npyPath, data);
        if (error is not null) log.Errors.Add($"The EM result could not be written to results/: {error}");
        return written;
    }

    /// <summary>
    /// brief-cosim-pattern-1 — a finished driven leg's whole result, through the one publisher: staged, then renamed into place, with
    /// its record. <paramref name="writeSnp"/> takes the staged base and the stamp; null, or why not.
    /// </summary>
    internal static EmPublished PublishDriven(RunContext run, Em3dSolverId solver, Em3dProblem problem, DataSet data, string version, string? device,
                                              EmResultMesh? mesh, Func<string, string, string?> writeSnp,
                                              EmResultStaging? staging = null, PortFieldManifest? fields = null)
        => EmResultStaging.PublishRun(new EmPublishRequest(
                run.Paths(solver), problem.Ports.Count, data, run.Setup, run.Target, Em3dSolvers.Get(solver).DisplayName, version, device, mesh,
                RecordPorts(problem, run.Generated), writeSnp)
            {
                Fields = fields, Staging = staging, ThreeDViewText = run.ThreeDViewText,
                PartPorts = EmPartsOpen.PositionsOf(RecordPorts(problem, run.Generated), p => p.Part is not null && p.Kind == EmResultPort.Gap),
            }, run.Log.Errors);

    /// <summary>R-cp1-1 — the problem's ports as the record states them: a part's gap port, a port with a real return, or a wave port.</summary>
    internal static IReadOnlyList<EmResultPort> RecordPorts(Em3dProblem problem, Em3dGenerationResult? generated)
        => [.. problem.Ports.OrderBy(p => p.Number).Select(p =>
           {
               int source = p.SourceNumber ?? p.Number;
               var part = generated?.ComponentPorts?.Ports.FirstOrDefault(c => c.Port?.Number == source);
               return new EmResultPort
               {
                   Number = p.Number,
                   Name = part?.Port?.Name ?? p.Name,
                   Z0Re = p.Z0.Real,
                   Z0Im = p.Z0.Imaginary,
                   Kind = p.Kind == Em3dPortKind.Wave ? EmResultPort.Wave
                        : part is { Connector: false } ? EmResultPort.Gap : EmResultPort.Plane,
                   Part = part?.Refdes,
                   Pins = part is null ? null : [.. new[] { part.Pin, part.ReferencePin }.OfType<string>()],
                   ModeNormalised = p.ModeNormalised,
               };
           })];

    /// <summary>The sweep's frequencies, as Palace's <c>NSample</c> spaces them: both ends included.</summary>
    public static double[] FrequenciesHz(Em3dFrequency f)
    {
        if (f.ListHz is { Count: > 0 } list) return [.. list];
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

    /// <summary>
    /// R-em3d21-2b — one run's memory decisions: a warning is posted once at its highest level, and a
    /// run past this machine's memory is confirmed once where it can be (the panel's question, the CLI's --force), and one past
    /// 150 % is refused unless it is.
    /// </summary>
    internal sealed class MemoryGate(Func<Em3dMemoryVerdict, bool>? confirm)
    {
        private Em3dMemoryLevel _posted = Em3dMemoryLevel.Fits;
        private bool _confirmed;

        /// <summary>brief-emi-10 — the user forced the run past its size check: its processes keep no memory ceiling.</summary>
        public bool Forced => _confirmed;

        /// <summary>The last verdict the gate was given: its remedies are what a run stopped at its ceiling names.</summary>
        public Em3dMemoryVerdict? Last { get; private set; }

        /// <summary>brief-dfr27-5 R-dfr27-5-5 — the MPI processes the question's answer chose for this run, or null.</summary>
        public int? ProcessCap { get; private set; }

        /// <summary>The question's answer chose Palace's iterative linear solver over the setup's direct one, for this run.</summary>
        public bool UseIterative { get; private set; }

        /// <summary>
        /// brief-dfr27-5 R-dfr27-5-2 — openEMS's time estimate, through the same confirmation: a long run is asked about (the
        /// panel's question); with no one to ask (the CLI) or once answered yes, the estimate is a warning and the run goes on.
        /// A short run's estimate is a note. Null to go on; otherwise the refusal.
        /// </summary>
        public string? AdmitTime(Em3dMemoryVerdict verdict, RunLog log)
        {
            if (verdict.Text is not { } text) return null;
            if (verdict.Level != Em3dMemoryLevel.Severe) { log.Notes.Add(text); return null; }
            if (confirm is null) { log.Warnings.Add(text); return null; }
            if (!confirm(verdict)) return text + " It was not started.";
            log.Warnings.Add(text + " It was started anyway, as confirmed.");
            return null;
        }

        /// <summary>Null to go on; otherwise the refusal.</summary>
        public string? Admit(Em3dMemoryVerdict verdict, RunLog log)
        {
            Last = verdict;
            if (verdict.Level == Em3dMemoryLevel.Fits || verdict.Text is null) return null;
            // A run that will not fit is asked about below 150 % too, where someone can be asked, and --force counts as the answer:
            // otherwise it starts with its ceiling and is stopped at it, with no way to choose to let it swap. With neither, a warning.
            if ((verdict.Level == Em3dMemoryLevel.Severe || verdict.PastMemory && confirm is not null) && !_confirmed)
            {
                if (confirm?.Invoke(verdict) != true)
                    return verdict.Text + " To start it anyway, confirm it when Simulate asks, or pass --force to `circuitrf em`.";
                // brief-dfr27-5 R-dfr27-5-5 — a remedy the run takes itself: it fits, so it keeps its memory ceiling.
                if (verdict.Choice.Remedy is { Iterative: true } iterative)
                {
                    UseIterative = true;
                    Warn(verdict.Text + " It was started with the iterative linear solver, as chosen (about " +
                         $"{MachineMemory.Format(iterative.EstimateBytes ?? 0)}).");
                    _posted = Em3dMemoryLevel.Warning;
                    return null;
                }
                if (verdict.Choice.Remedy is { Processes: { } n } chosen)
                {
                    ProcessCap = n;
                    Warn(verdict.Text + $" It was started on {n} MPI process{(n == 1 ? "" : "es")}, as chosen (about " +
                         $"{MachineMemory.Format(chosen.EstimateBytes ?? 0)}).");
                    _posted = Em3dMemoryLevel.Warning;
                    return null;
                }
                _confirmed = true;
                Warn(verdict.Text + " It was started anyway, as confirmed.");
                _posted = verdict.Level;
                return null;
            }
            if (verdict.Level > _posted) Warn(verdict.Text);
            _posted = (Em3dMemoryLevel)Math.Max((int)_posted, (int)verdict.Level);
            return null;

            void Warn(string text)
            {
                if (verdict.File is { } file) log.Files[text] = file;
                log.Warnings.Add(text);
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Count(long? v) => v is { } x ? x.ToString("N0", CultureInfo.InvariantCulture) : "an unreported number of";

    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    private static string Ascii(string s)
        => new(s.Replace("µm", "um").Replace("–", "-").Replace("×", "x").Select(ch => ch < 128 ? ch : '?').ToArray());
}
