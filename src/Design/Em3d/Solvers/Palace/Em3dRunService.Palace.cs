// brief-emi-1 — Palace's part of Em3dRunService: its leg of a run (settings, lowering, execution) and everything only a
// Palace run does. Moved here unchanged from Em3dRunService.cs; the descriptor that names it is PalaceSolver.cs beside it.
//
// Palace's writers and readers (GmshGeoWriter, PalaceConfigWriter, PalaceRun, PalaceLogProgress, PalaceTerminalS …)
// predate the solver folders and stay in src/Design/Em3d/.

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
    /// <summary>The diagnostics group a Palace <c>.npy</c> carries beside S.</summary>
    public const string PalaceGroup = "palace";

    /// <summary>brief-dfr31-1 R-dfr31-1-6 — the Maxwell matrix with every floating body still a terminal, before elimination.</summary>
    public const string FullCapacitanceCube = "C_full";

    /// <summary>brief-em3d-116 R-em3d116-3 — the note a Both run carries when Palace refuses its terminal ports and it runs on
    /// openEMS alone. Null when Palace runs them.</summary>
    public static string? TerminalPortsSkipPalace(IReadOnlyList<(string Label, int Terminals)> ports, bool lumpedPorts = false,
                                                  (string Label, double ShiftM)? shifted = null)
        => PalacePortRefusal(new(ports, lumpedPorts, shifted))?.InCompare;

    /// <summary>
    /// brief-em3d-114 R-em3d114-2e / brief-em3d-115 R-em3d115-2d — what Palace refuses of a problem's multi-terminal wave ports
    /// (D-115a/b), naming openEMS, which builds them: three or more terminals on one face (the Gram fit of a degenerate face is
    /// exactly determined at three and under-determined at four, brief 124), a reference plane moved off the port face (the
    /// terminal voltages Palace writes ignore the Offset, brief 113), and a lumped port beside a terminal group (the transform
    /// is stated for wave-port entries only). Null when Palace runs them all. In a comparison the run goes ahead without
    /// Palace and says Palace was skipped.
    /// </summary>
    internal static Em3dLegRefusal? PalacePortRefusal(Em3dTerminalFacts facts)
        => PalaceTerminalReason(facts.Ports, facts.LumpedPorts, facts.Shifted) is { } why
            ? new(why + ". Set the setup's solver to openEMS.", why + ", so this setup ran on openEMS and Palace was skipped for it.")
            : null;

    private static string? PalaceTerminalReason(IReadOnlyList<(string Label, int Terminals)> ports, bool lumpedPorts,
                                                (string Label, double ShiftM)? shifted)
    {
        if (ports.FirstOrDefault(p => p.Terminals > 2) is { Label: not null } many)
            return TerminalHead(many) + "Palace runs terminal ports with two terminals per face in this version";
        if (ports.Count == 0) return null;
        if (shifted is { } s)
            return $"Port '{s.Label}' has its reference plane moved {Fmt(Math.Abs(s.ShiftM) * 1e3)} mm off its face; Palace runs terminal " +
                   "ports with the reference plane on the port face in this version (the terminal voltages it writes are measured there)";
        if (lumpedPorts)
            return TerminalHead(ports[0]) + "Palace runs terminal ports only beside other wave ports in this version, and this problem " +
                   "also has a lumped port";
        return null;
    }

    private static string TerminalHead((string Label, int Terminals) port)
    {
        var (label, n) = port;
        string count = n switch { 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
                                  _ => n.ToString(CultureInfo.InvariantCulture) };
        return $"Port '{label}' has {count} terminals; ";
    }

    /// <summary>brief-emi-1 — Palace's leg of one run: Gmsh and Palace found, the Palace section resolved (and where Palace runs:
    /// here or in a Linux subsystem distribution), the .geo and configuration written, the memory checked, then the solve.</summary>
    internal sealed class PalaceLeg : SolverLeg
    {
        private PalaceSettings? _settings;
        // brief-em3d-26 — where Palace runs: here, or inside a Linux subsystem distribution, with that
        // distribution's cores and memory.
        private IPalaceRunner _runner = NativePalaceRunner.Instance;
        private Em3dMemoryScope? _memory;
        private int? _gpus;
        private PalacePlan? _plan;

        public PalaceLeg() : base(PalaceSolver.Descriptor) { }

        public override void Configure(RunContext run)
        {
            var (readiness, log, setup, device) = (run.Readiness, run.Log, run.Setup, run.Device);
            // brief-em3d-136 R-em3d136-2a — the GPUs the probe counted (a GPU run only; it was refused above if none).
            _gpus = device == PalaceDevice.Gpu
                ? readiness.FirstOrDefault(r => r.Tool == SolverTool.Palace)?.Capabilities
                           .FirstOrDefault(c => c.Capability == SolverCapability.Gpu)?.Devices
                : null;
            var p = readiness.Single(r => r.Tool == SolverTool.Palace).Installation!;
            var g = readiness.Single(r => r.Tool == SolverTool.Gmsh).Installation!;
            log.Notes.Add($"Solver: Palace {p.DescribeVersion()} at {SpackInstalls.FoldPadding(p.Where)}; mesher: Gmsh {g.DescribeVersion()} at {g.Path}.");
            // brief-em3d-135 R-em3d135-1e — an untested Gmsh runs, and the run says so among its Warnings.
            if (readiness.Single(r => r.Tool == SolverTool.Gmsh).Warning is { } untested) log.Warnings.Add(untested);
            var settings = _settings = PalaceSettings.Resolve(setup.Palace);
            if (p.Distribution is not null)
            {
                if (SolverDiscovery.For(SolverTool.Palace).Subsystem is not { } wsl)
                    Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-subsystem",
                        $"Palace was found in the Linux subsystem distribution '{p.Distribution}', which this machine cannot reach."));
                else if (Wsl.WslPalace.Runner(wsl, p, PalaceConfigWriter.FieldDirectories(settings), out string? subsystemRefusal) is { } runner)
                {
                    _runner = runner;
                    _memory = Wsl.WslPalace.MemoryScope(new Wsl.WslSession(wsl, p.Distribution));
                    log.Notes.Add($"Palace runs in the Linux subsystem distribution '{p.Distribution}', staged in its own Linux " +
                                  $"filesystem; the mesh, configuration and results stay in the run directory here. MPI: " +
                                  $"{(runner.MpiLauncher is { } mpi ? SpackInstalls.FoldPadding(mpi) : "none")} ({(runner.MpiLauncher is null ? "Palace runs on one process" : "inside the distribution")}).");
                }
                else Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-subsystem", subsystemRefusal));
            }
            if (Stop is null && settings.Problems() is { Count: > 0 } bad)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-settings", string.Join(" ", bad)));
            // brief-em3d-21 R-em3d21-3b — an explicit core count past the physical one is refused, never
            // oversubscribed. brief-em3d-26 R-em3d26-2b — the SUBSYSTEM's cores, where Palace runs there.
            else if (Stop is null && PalaceRankRefusal(device, run.MaxCores, _runner.Cores, _gpus) is { } ranks)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-ranks", ranks));
        }

        // brief-em3d-97 R-em3d97-4 — a run in the subsystem holds it, so the memory dialog will not restart it from under the run.
        public override IDisposable? Hold(string holder)
            => _runner is Wsl.WslPalaceRunner wslRunner ? Wsl.WslInUse.Hold(wslRunner.Distribution, holder) : null;

        public override void Prepare(Em3dProblem problem, RunContext run)
        {
            if (Stop is not null) return;
            _plan = PreparePalace(problem, _settings!, run.Readiness, _runner, _memory, run.Log, out var stop,
                                  run.Setup.RadiationPattern, run.Device, _gpus);
            Stop = stop;
            // brief-em3d-21 R-em3d21-2 — will it fit? A warning, and past 150 % a confirmation; still no process.
            // brief-dfr27-5 R-dfr27-5-5 — priced on the processes the run will use, so fewer can be offered.
            int? processes = run.Device == PalaceDevice.Cpu && _runner is not null
                ? PalaceProcessCount(run.Device, run.MaxCores, _runner.Cores, _gpus).Processes : null;
            // brief-dfr35-1 R-dfr35-1-5 — meshing is what is asked: the size a solve would need is said, and refuses nothing.
            if (run.Control?.MeshOnly == true)
            {
                if (_plan is not null && Stop is null &&
                    PalaceMemoryVerdict(problem, run.Setup, run.Build, _settings!, _memory?.Bytes ?? MachineMemory.PhysicalBytes, _memory, processes) is { Text: { } size })
                    run.Log.Notes.Add(size);
            }
            else if (_plan is not null && Stop is null &&
                run.Memory.Admit(PalaceMemoryVerdict(problem, run.Setup, run.Build, _settings!, _memory?.Bytes ?? MachineMemory.PhysicalBytes,
                                                     _memory, processes), run.Log) is { } tooBig)
            {
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded(MemoryRefusalSource, tooBig));
                _plan = null;
            }
        }

        public override Leg Execute(Em3dProblem problem, string snpBase, RunContext run)
            => ExecutePalace(_plan!, problem, run.Setup, run.ResultsRoot, snpBase, run.Ct, run.Control, run.MaxCores, run.Log, run.Memory,
                             run.Limits(_memory?.Bytes ?? MachineMemory.PhysicalBytes), run, run.Dc);
    }

    // ── Palace (brief-em3d-7) ─────────────────────────────────────────────────────────────────

    private sealed record PalacePlan(PalaceSettings Settings, GmshLowering Lowering, string ConfigJson,
                                     SolverInstallation Palace, SolverInstallation Gmsh, IPalaceRunner Runner,
                                     Em3dMemoryScope? Memory, bool FarField = false,
                                     PalaceDevice Device = PalaceDevice.Cpu, int? Gpus = null);

    private static PalacePlan? PreparePalace(Em3dProblem problem, PalaceSettings settings, IReadOnlyList<SolverReadiness> readiness,
                                             IPalaceRunner runner, Em3dMemoryScope? memoryScope, RunLog log, out Stop? stop,
                                             bool radiationPattern, PalaceDevice device, int? gpus)
    {
        stop = null;
        var lowering = GmshGeoWriter.Write(problem, settings);
        if (!lowering.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-lowering", lowering.Refusal)); return null; }
        // brief-em3d-31 R-em3d31-3 — the far field when asked for and Palace can give one; otherwise the run says
        // why and goes on (a floor or a wave port changes nothing about S).
        bool farField = radiationPattern && problem.Type == Em3dProblemType.Driven;
        if (farField && PalaceConfigWriter.FarFieldRefusal(problem) is { } noFarField)
        {
            log.Notes.Add(noFarField);
            farField = false;
        }
        var config = PalaceConfigWriter.Write(problem, lowering.Groups, settings, farField, device);
        if (!config.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-lowering", config.Refusal)); return null; }

        // F0 Q6 — the void model is the flat-surface impedance: low by up to 10 % on a conductor whose
        // radius is under about ten skin depths at the bottom of the band.
        if (!problem.IsStatic && ThinRoundConductors(problem) is { Count: > 0 } thin)
            log.Notes.Add($"{string.Join(", ", thin.Select(n => $"'{n}'"))} {(thin.Count == 1 ? "is" : "are")} round and under ten " +
                          $"skin depths in radius at {Fmt(problem.Frequency.StartHz / 1e9)} GHz. Palace models a conductor's " +
                          "loss as a flat surface's, which on such a wire reads the resistance low by up to about 10 % at the " +
                          "bottom of the band (3 % at 10 GHz on a 1 mil wire); inductance is not affected.");
        // brief-em3d-65 R-em3d65-4b/4c — a kernel solid's curved faces: the loss model at a small radius (a warning), and a
        // radius small enough to set the mesh (a note). Neither blocks the run.
        foreach (var row in Em3dFidelity.For(problem, Em3dFidelitySolver.Palace, null, GmshGeoWriter.SmallestRequestedSizeM(problem, settings)))
            (row.Severity == Em3dFidelitySeverity.Warning ? log.Warnings : log.Notes).Add(row.Sentence);

        return new PalacePlan(settings, lowering, config.Json!,
                              readiness.Single(r => r.Tool == SolverTool.Palace).Installation!,
                              readiness.Single(r => r.Tool == SolverTool.Gmsh).Installation!, runner, memoryScope, farField,
                              device, gpus);
    }

    private static Leg ExecutePalace(PalacePlan plan, Em3dProblem problem, EmSetup setup, string resultsRoot, string snpBase,
                                     CancellationToken ct, RunControl? control, int? maxCores, RunLog log, MemoryGate memory,
                                     ProcessLimits limits, RunContext run, Em3dDcResult? dc = null)
    {
        const Em3dSolverId Me = PalaceSolver.Id;
        Leg Failed(string reason) => Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(reason));
        Leg Cancelled() => Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
        var (settings, lowering, palace) = (plan.Settings, plan.Lowering, plan.Palace);
        string configJson = plan.ConfigJson;
        // The memory question's answer can trade the setup's direct solver for the iterative one, for this run only: the
        // mesh is the same, so only the configuration is written again.
        void TakeIterativeIfChosen()
        {
            if (!memory.UseIterative || settings.LinearSolver != PalaceLinearSolver.Direct) return;
            settings = settings with { LinearSolver = PalaceLinearSolver.Iterative };
            configJson = PalaceConfigWriter.Write(problem, lowering.Groups, settings, plan.FarField, plan.Device).Json ?? configJson;
        }
        TakeIterativeIfChosen();
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
        // brief-emb-2 R-emb2-1 — a script that curves its elements gets a straight-sided twin, made only if the cap is reached.
        GmshLowering Straight() => GmshGeoWriter.Write(problem, settings with { StraightSided = true });
        try
        {
            meshed = PalaceRun.Mesh(runDir, lowering, plan.Gmsh.Path, control, ct, limits, physical,
                                    GmshGeoWriter.Curves(lowering) ? Straight : null, settings.MeshCurvingMinutes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"the mesh could not be staged in '{runDir}' ({e.Message}).");
        }
        if (meshed.Cancelled) return Cancelled();
        // brief-emi-10 — Gmsh at the memory ceiling or the disk floor: a runaway, most likely, and the run's refusal.
        if (meshed.Stopped is { } meshStop)
            return StoppedLeg(Me, "Gmsh", meshStop, limits, [], Path.Combine(runDir, PalaceRun.GmshLogFile));
        if (meshed.Refused) return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded("palace-mesh", meshed.Message));
        if (!meshed.Ok)
        {
            var failedMesh = Failed(meshed.Message!);
            // brief-dfr35-1 R-dfr35-1-8 — the place travels with the refusal, for the row that shows it in the layout.
            if (meshed.Place is { } place) log.Offers[failedMesh.Stop!.Render()] = place;
            return failedMesh;
        }
        log.Notes.Add(meshed.Reused
            ? $"The geometry script is unchanged since the last run, so its mesh was reused ({runDir})."
            : $"Meshed with Gmsh in {runDir}.");
        if (meshed.Note is { } curvingNote) log.Notes.Add(curvingNote);

        // brief-emb-2 R-emb2-4 — the mesh itself, before Palace: a zero-volume or inverted element, or a surface triangle that is
        // no element's face, is refused naming where it is, instead of a solver abort that names nothing.
        if (Em3dMeshCheck.Check(Path.Combine(runDir, GmshGeoWriter.MeshFile), lowering.Groups, robustMeshing: setup.RobustMeshing) is { Refusal: { } badMesh })
            return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(Em3dMeshCheck.Source,
                badMesh + $" The mesh is {Path.Combine(runDir, GmshGeoWriter.MeshFile)}."));

        // brief-dfr35-1 R-dfr35-1-5 — stopped here, after everything a run checks of its mesh and before any solver.
        if (control?.MeshOnly == true)
        {
            string meshLog = Path.Combine(runDir, PalaceRun.GmshLogFile);
            var checkedMesh = Em3dMeshCheck.Check(Path.Combine(runDir, GmshGeoWriter.MeshFile), lowering.Groups);
            if (meshed.Tetrahedra is { } madeHere && MeshedAgainstEstimate(madeHere, EstimatePalace(problem, settings), settings, problem.IsStatic,
                                                                           plan.Device == PalaceDevice.Cpu ? PalaceProcessCount(plan.Device, maxCores, runner.Cores, plan.Gpus).Processes : null) is { } sized)
                log.Notes.Add(sized);
            return new Leg(Me, EmRunStatus.Ok, null, null, null, null, $"Gmsh {plan.Gmsh.DescribeVersion()}")
            {
                MeshOnly = new Em3dMeshOnlyReport((long?)checkedMesh?.Tetrahedra ?? meshed.Tetrahedra, EstimatePalace(problem, settings)?.Tetrahedra,
                                                  meshed.Reused, runDir, PalaceRun.GmshWarningLines(meshLog)),
            };
        }

        // brief-em3d-21 R-em3d21-2 — the mesh's size is known now, and on a problem whose mesh is mostly
        // refinement (a bond wire) it is the first figure that can say the run will not fit.
        // ── Palace ────────────────────────────────────────────────────────────────────────────
        // brief-em3d-21 R-em3d21-3a — one MPI rank per PHYSICAL core by default (Open MPI's slot count).
        // brief-em3d-136 R-em3d136-2a — one per GPU on a GPU run, capped by Cores.
        // brief-dfr27-5 R-dfr27-5-5 — and no more than the question before the run chose.
        var (processes, processNote) = PalaceProcessCount(plan.Device, maxCores, runner.Cores, plan.Gpus);
        bool cpu = plan.Device == PalaceDevice.Cpu;
        if (cpu && memory.ProcessCap is { } cap && cap < processes) (processes, processNote) = (cap, null);

        if (meshed.Tetrahedra is { } tets &&
            memory.Admit(MeshMemoryVerdict(tets, settings, physical, plan.Memory, problem.IsStatic, cpu ? processes : null), log) is { } tooBig)
            return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(MemoryRefusalSource,
                tooBig + $" The mesh is kept in {runDir}, so a run started anyway reuses it."));
        if (cpu && memory.ProcessCap is { } chosen && chosen < processes) (processes, processNote) = (chosen, null);
        // brief-dfr32-3 R-dfr32-3-2 (owner decision D1 b) — what the saved fields will weigh, beside the memory figure, before Palace.
        if (meshed.Tetrahedra is { } meshTets && PalaceFieldFiles.EstimateNote(problem, settings, meshTets, runDir) is { } fieldsNote)
            log.Notes.Add(fieldsNote);
        TakeIterativeIfChosen();
        // EMB-4 R-emb4-1 — Gmsh's count against the estimate made before meshing, and the memory at Gmsh's count for the
        // linear solver this run actually uses (after the question's answer, which can swap direct for iterative).
        if (meshed.Tetrahedra is { } made && MeshedAgainstEstimate(made, EstimatePalace(problem, settings), settings,
                                                                   problem.IsStatic, cpu ? processes : null) is { } counted)
            log.Notes.Add(counted);
        if (processNote is not null) log.Notes.Add(processNote);
        var tracker = new PalaceStageTracker(settings.AdaptiveMaxIterations, settings.SweepAdaptiveTol, control,
                                             electrostatic: problem.Type == Em3dProblemType.Electrostatic);
        PalaceStep solved;
        string? mpiNote;
        // brief-em3d-115 — a terminal face whose two modes travel at one speed (a stripline, any homogeneous fill) is refused:
        // Palace returns such a pair in an arbitrary mixture, re-drawn every time it solves the face (per excitation and
        // frequency in a sweep) and on some meshes as nearly the same field even within one solve, so no conversion can
        // recover two terminals from it (src/Design/RESOLVED.md § brief-em3d-115). Found before the 3D solve, by the face's
        // own 2D mode solve at the sweep's centre (seconds).
        if (problem.Type == Em3dProblemType.Driven && !problem.IsStatic && Em3dPortMap.HasTerminals(problem.Ports))
        {
            var ordered = problem.Ports.OrderBy(p => p.Number).ToList();
            var requested = FrequenciesHz(problem.Frequency);
            double centre = requested[requested.Length / 2];
            foreach (var face in PalaceTerminalS.Faces(ordered).Where(f => f.Count > 1))
            {
                int attribute = lowering.Groups.Single(g => g.Kind == Em3dGroupKind.Port && g.PortNumber == ordered[face[0]].Number).Attribute;
                var kn = runner.FaceModes(runDir, configJson, palace.Path, attribute, face.Count, centre, ct, out string? why);
                if (ct.IsCancellationRequested) return Cancelled();
                if (kn is null) return Failed($"The terminal faces' modes could not be found before the solve: {why}.");
                if (ModalTerminalTransform.IsDegenerate(kn))
                    return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(TerminalPortsSource,
                        PalaceTerminalS.DegenerateRefusal(ordered[face[0]].FaceGroupLabel ?? ordered[face[0]].Name, kn, centre)));
            }
        }
        try { solved = runner.Solve(runDir, configJson, palace.Path, processes, control, ct, out mpiNote, tracker, physical, limits); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"Palace could not be staged in '{runDir}' ({e.Message}).");
        }
        if (mpiNote is not null) log.Notes.Add(mpiNote);
        log.Notes.AddRange(tracker.Notes);
        log.Warnings.AddRange(tracker.Warnings);
        if (solved.MemoryNote is not null) log.Warnings.Add(solved.MemoryNote);
        if (solved.Cancelled) return Cancelled();
        if (solved.Stopped is { } solveStop)
            return StoppedLeg(Me, "Palace", solveStop, limits, memory.Last?.Remedies ?? [], Path.Combine(runDir, PalaceRun.PalaceLogFile));
        if (!solved.Ok) return Failed(solved.Message!);
        // brief-em3d-136 R-em3d136-5a — what Palace actually ran on. The probe should have refused a build that would revert
        // to the CPU; this catches a binary changed since the probe, or an environment that differs between the two.
        if (plan.Device == PalaceDevice.Gpu && PalaceRun.RanOnCpu(Path.Combine(runDir, PalaceRun.PalaceLogFile)))
            log.Warnings.Add(GpuRevertedWarning(runDir));

        control?.BeginStage(ReadingLabel);
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        PalaceRun.PruneIterationFields(post);
        if (problem.IsStatic)
            return FinishStatic(problem, setup, run.Paths(Me).NpyPath, post, tracker, processes, log, palace, wall.Elapsed);
        if (problem.Type == Em3dProblemType.Eigenmode)
            return FinishEigen(problem, setup, run.Paths(Me).NpyPath, post, lowering, tracker, processes, log, palace, wall.Elapsed);
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var s = PalaceRun.ReadPortS(Path.Combine(post, PalaceRun.PortSFile), [.. ports.Select(p => p.Number)], out string? readError);
        if (readError is not null) return Failed(readError);
        var requestedHz = FrequenciesHz(problem.Frequency);
        s = WithoutSaveOnlyRows(s, requestedHz);
        // brief-em3d-23 R-em3d23-2d — a wave port's S is referred to its mode's own impedance: renormalised to
        // the port's stated Z0 before anything else reads it.
        Complex[][]? modeZ = null;
        // brief-em3d-115 R-em3d115-4 — with a terminal group, Palace's S is MODAL on that face: every port goes through the
        // transform instead (a one-entry face reduces to the renormalisation below), from S, V_wp, Z_PV and the log's kₙ.
        (WavePortModalSamples Samples, WavePortTerminalOutcome Outcome)? terminal = null;
        if (Em3dPortMap.HasTerminals(ports))
        {
            var modes = PalaceRun.ReadWaveModes(File.ReadLines(Path.Combine(runDir, PalaceRun.PalaceLogFile))).ToList();
            var faces = PalaceTerminalS.Faces(ports).Where(f => f.Count > 1).ToList();
            var needed = faces.SelectMany(f => f.Select(i => ports[i].Number)).ToHashSet();
            // R-em3d115-3 — an adaptive sweep logs no kₙ: each terminal face's modes are solved on their own (Palace's 2D mode
            // solve of that face, seconds) at a few of the sweep's frequencies, and interpolated between.
            if (requestedHz.Any(f => !modes.Any(m => needed.All(m.Modes.ContainsKey) && Math.Abs(m.FrequencyHz - f) <= 5e-4 * f)))
            {
                control?.BeginStage("solving the terminal faces' modes (Palace)");
                var knots = PalaceTerminalS.KnotFrequenciesHz(requestedHz);
                foreach (double f in knots)
                {
                    var block = new Dictionary<int, PalaceWaveMode>();
                    foreach (var face in faces)
                    {
                        int attribute = lowering.Groups.Single(g => g.Kind == Em3dGroupKind.Port && g.PortNumber == ports[face[0]].Number).Attribute;
                        var kn = runner.FaceModes(runDir, configJson, palace.Path, attribute, face.Count, f, ct, out string? why);
                        if (ct.IsCancellationRequested) return Cancelled();
                        if (kn is null) return Failed($"The terminal faces' wavenumbers could not be found: {why}.");
                        for (int k = 0; k < face.Count; k++) block[ports[face[k]].Number] = new PalaceWaveMode(ports[face[k]].Number, k + 1, kn[k]);
                    }
                    modes.Add(new PalaceWaveModeSample(f, block));
                }
                log.Notes.Add($"Palace's adaptive sweep writes no wavenumber kₙ, which the terminal S needs: each terminal face's modes were " +
                              $"solved on their own (Palace's 2D mode solve of the face) at {string.Join(", ", knots.Select(f => Fmt(f / 1e9)))} GHz " +
                              "and interpolated between, on the effective permittivity.");
            }
            var samples = PalaceTerminalS.Read(post, modes, [.. ports.Select(p => p.Number)], out string? terminalError, needed);
            if (samples is null) return Failed(terminalError!);
            if (SweepRows(samples.FrequenciesHz, requestedHz) is { } rows && samples.FrequenciesHz.Length > requestedHz.Length)
                samples = new WavePortModalSamples([.. rows.Select(k => samples.FrequenciesHz[k])], [.. rows.Select(k => samples.Samples[k])]);
            var outcome = PalaceTerminalS.Convert(samples, ports, out terminalError);
            if (outcome is null) return Failed(terminalError!);
            terminal = (samples, outcome);
            s = outcome.S;
            modeZ = [.. samples.Samples.Select(x => x.ZPv.Select(z => new Complex(z, 0)).ToArray())];
            log.Notes.AddRange(PalaceTerminalS.RouteNotes(ports, outcome));
            var (checkNotes, checkWarnings) = PalaceTerminalS.Checks(ports, outcome, PalaceTerminalS.Lossless(problem));
            log.Notes.AddRange(checkNotes);
            log.Warnings.AddRange(checkWarnings);
        }
        if (problem.HasWavePorts)
        {
            var wave = ports.Where(p => p.Kind == Em3dPortKind.Wave).ToList();
            if (terminal is null)
            {
                modeZ = PalaceRun.ReadPortZ(Path.Combine(post, PalaceRun.PortZFile), [.. wave.Select(p => p.Number)],
                                            out double[] zf, out string? zError);
                if (modeZ is null) return Failed(zError!);
                if (zf.Length > s.FrequenciesHz.Length && SweepRows(zf, requestedHz) is { } zRows)
                {
                    modeZ = [.. zRows.Select(k => modeZ[k])];
                    zf = [.. zRows.Select(k => zf[k])];
                }
                if (zf.Length != s.FrequenciesHz.Length ||
                    zf.Where((f, k) => Math.Abs(f - s.FrequenciesHz[k]) > 1e-6 * Math.Abs(f)).Any())
                    return Failed($"Palace's {PalaceRun.PortZFile} and {PalaceRun.PortSFile} do not list the same frequencies, so the " +
                                  "wave ports' S-parameters cannot be renormalised.");
                s = WavePortReferences.Renormalise(s, ports, wave, modeZ);
                log.Notes.Add(WavePortReferences.Note("Palace", "port-Z.csv", wave, modeZ));
            }
            // R-em3d23-3 — a port face that supports a mode past its own gives an S that means something else. brief-em3d-115
            // R-em3d115-2c: on a terminal face its own are the terminals' modes, so the face is asked about mode N + 1.
            try
            {
                var asked = wave.Where(p => !GmshGeoWriter.SharesAnotherTerminalsFace(problem, p)).ToList();
                var second = runner.SecondModes(runDir, configJson, palace.Path, problem.Frequency.StopHz,
                                                   [.. asked.Select(p => p.Number)], ct, out string? secondNote);
                string? Group(PalaceWaveMode m) => ports.First(p => p.Number == m.Port).FaceGroupLabel;
                int Terminals(PalaceWaveMode m) => wave.Count(q => q.FaceGroup is not null && q.FaceGroup == ports.First(p => p.Number == m.Port).FaceGroup);
                if (second is null)
                    log.Notes.Add(terminal is null
                        ? $"Whether a wave port's second mode propagates was not checked: {secondNote}."
                        : $"Whether a wave port face supports a mode past its own was not checked: {secondNote}.");
                else if (second.Any(m => m.Propagating))
                    foreach (var m in second.Where(m => m.Propagating))
                        log.Warnings.Add((Group(m) is { } label
                                             ? $"Port '{label}''s face supports a propagating mode past its {Terminals(m)} terminals' own (mode {m.Mode}) at "
                                             : $"Port {m.Port}'s wave-port face supports a SECOND propagating mode at ") +
                                     $"{Fmt(problem.Frequency.StopHz / 1e9)} GHz (Palace's kₙ = {Fmt(m.Kn.Real)}{(m.Kn.Imaginary < 0 ? "−" : "+")}" +
                                     $"{Fmt(Math.Abs(m.Kn.Imaginary))}i m⁻¹), so its S-parameters are those of " +
                                     (Group(m) is null ? "the first mode alone and power the second carries is not in them. "
                                                       : "its terminals' modes alone and power the extra mode carries is not in them. ") +
                                     "Make the port region smaller (the Ports3D Width and Height), or lower the sweep's top.");
                else if (terminal is null)
                    log.Notes.Add($"No wave port's second mode propagates at {Fmt(problem.Frequency.StopHz / 1e9)} GHz, the top of " +
                                  "the sweep (Palace, asked for mode 2): " + string.Join("; ", second.Select(m =>
                                      $"port {m.Port}'s decays by 1/e in {Fmt(m.DecayLengthM * 1e3)} mm")) + ".");
                else
                    log.Notes.Add($"No wave port face supports a mode past its own at {Fmt(problem.Frequency.StopHz / 1e9)} GHz, the top of " +
                                  "the sweep (Palace, asked for the next mode of each face): " + string.Join("; ", second.Select(m =>
                                      $"{(Group(m) is { } l ? $"port '{l}''s mode {m.Mode}" : $"port {m.Port}'s mode 2")} decays by 1/e in " +
                                      $"{Fmt(m.DecayLengthM * 1e3)} mm")) + ".");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Notes.Add($"Whether a wave port's second mode propagates was not checked ({e.Message}).");
            }
        }
        s = AtRequestedFrequencies(s, requestedHz);
        var facts = PalaceRun.ReadFacts(post);
        log.Notes.Add($"Palace solved {s.FrequenciesHz.Length} frequencies on {Count(facts.FinalElements)} elements " +
                      $"({Count(facts.InitialElements)} initially, {facts.AdaptiveIterations ?? 0} adaptive pass(es)), " +
                      $"{Count(facts.DegreesOfFreedom)} unknowns, {processes} process(es).");
        // brief-dfr32-4 R-dfr32-4-2 — each driven port's lost power at the band's ends, outliers flagged.
        if (Em3dPortLoss.Line(s.FrequenciesHz, s.S, [.. ports.Select(p => p.Number)]) is { } lossLine) log.Notes.Add(lossLine);

        // ── The DataSet: S and Z0 in the house convention, plus Palace's own record ─────────────
        var data = BuildDataSet(s, ports, facts);
        if (terminal is { } t) PalaceTerminalS.AddDiagnostics(data, PalaceGroup, t.Samples, t.Outcome, ports);
        if (plan.FarField && PalacePattern(post, s, ports, setup, data, log.Notes) is { } patternError) return Failed(patternError);
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

        // brief-emdc-2 R-emdc2-3 — the run's 0 Hz row, before either file is written
        var dcLines = SpliceDc(data, problem, dc);
        var published = PublishDriven(run, Me, problem, data, palace.DescribeVersion(), plan.Device == PalaceDevice.Gpu ? "gpu" : "cpu",
                                      new EmResultMesh(facts.FinalElements, "elements", facts.DegreesOfFreedom),
                                      (stagedBase, stamp) => WriteSnp(data, stagedBase, setup, problem, lowering, settings, palace, facts, summary,
                                                                      quality, wall.Elapsed, dcLines, stamp));

        // R-em3d21-5a — the one line that says what the run cost, the last thing the run says.
        log.Notes.Add(CompletionSummary(summary, quality, wall.Elapsed, FieldFilesBytes(post), Path.Combine(post, PalaceConfigWriter.FieldDirectory)));
        return new Leg(Me, EmRunStatus.Ok, null, data, published.Npy, published.Snp, "Palace " + palace.DescribeVersion(),
                       NotConverged: PalaceNotConverged(tracker), RecordPath: published.Record);
    }

    /// <summary>brief-em3d-98 — Palace's own not-converged signal: its log's "Linear solver did not converge", which the stage
    /// tracker already turns into the run's warning. Null when it said no such thing.</summary>
    private static string? PalaceNotConverged(PalaceStageTracker tracker)
        => tracker.Warnings.FirstOrDefault() is { } w ? FirstSentence(w) : null;

    /// <summary>
    /// brief-em3d-22 R-em3d22-3c/4c — a static solve's matrices, read by column name, as a DataSet in
    /// <c>results/</c> under <c>&lt;key&gt;.palace_es</c> or <c>_ms</c>. No Touchstone: a matrix is not S.
    /// </summary>
    private static Leg FinishStatic(Em3dProblem problem, EmSetup setup, string npyTarget, string post,
                                    PalaceStageTracker tracker, int processes, RunLog log, SolverInstallation palace, TimeSpan wall)
    {
        const Em3dSolverId Me = PalaceSolver.Id;
        bool es = problem.Type == Em3dProblemType.Electrostatic;
        int[] indices = [.. Enumerable.Range(1, problem.Terminals.Count)];
        var (file, mutualFile, symbol, mutualSymbol, unit) = es
            ? (PalaceRun.CapacitanceFile, PalaceRun.MutualCapacitanceFile, "C", "C_m", "(F)")
            : (PalaceRun.InductanceFile, PalaceRun.MutualInductanceFile, "M", "M_m", "(H)");
        var matrix = PalaceRun.ReadTerminalMatrix(Path.Combine(post, file), symbol, unit, indices, out string? error);
        if (matrix is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        var mutual = PalaceRun.ReadTerminalMatrix(Path.Combine(post, mutualFile), mutualSymbol, unit, indices, out string? mutualError);
        if (mutual is null)
            log.Notes.Add($"The {(es ? "mutual capacitance" : "current-difference inductance")} form was not read: {mutualError}" +
                          // brief-dfr34-4 R-dfr34-4-5 — C_mutual is exact arithmetic on C, so a missing file costs nothing.
                          (es ? " C_mutual is derived from C instead (the same conversion: −C[i][j] between, the row sum to ground)." : ""));
        if (es && mutual is null) mutual = Em3dStaticResult.MutualFromMaxwell(matrix);
        // brief-dfr31-1 R-dfr31-1-6 — floating bodies were solved as terminals; eliminated here, exactly. The full matrix is
        // kept in the diagnostics group, and the mutual form is the reduced matrix's (Palace's is the full one's).
        double[,]? full = null;
        var floating = problem.Terminals.Select(t => t.Floating).ToList();
        if (es && floating.Contains(true))
        {
            full = matrix;
            matrix = Em3dStaticResult.EliminateFloating(full, floating);
            mutual = Em3dStaticResult.MutualFromMaxwell(matrix);
        }

        var notes = new List<string>();
        string groundText = GroundText(problem, setup);
        if (es)
        {
            notes.Add("C is the Maxwell capacitance matrix: C[i][i] is terminal i's capacitance to every other conductor " +
                      "(the charge on i with i at 1 V and all others grounded), and C[i][j] <= 0 off the diagonal. " +
                      "C_mutual is the lumped-circuit form: C_mutual[i][i] is i's capacitance to ground alone and " +
                      "C_mutual[i][j] = -C[i][j] is the capacitor between i and j. C_ground is each terminal's capacitance to " +
                      "ground alone, the row sum of C. " + groundText);
            // brief-dfr34-4 R-dfr34-4-5 — the row links to the app note's section on the two matrices.
            log.Offers[notes[^1]] = new EmDocLink(Em3dStaticResult.MaxwellDocPage, Em3dStaticResult.MaxwellDocAnchor,
                                                  "Why C's off-diagonals are negative");
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
        if (full is not null)
            notes.Add($"{floating.Count(f => f)} floating piece(s) of metal were solved as terminals and eliminated: C is the matrix " +
                      "of the named terminals with that metal carrying no net charge. The matrix before elimination is " +
                      $"{PalaceGroup}.{FullCapacitanceCube}.");
        var data = Em3dStaticResult.Build(problem.Type, [.. problem.Terminals.Where(t => !t.Floating).Select(t => t.Name)], matrix, mutual, notes);
        if (full is not null)
            data.AddToGroup(PalaceGroup, FullCapacitanceCube, Em3dStaticResult.Matrix(full, [.. problem.Terminals.Select(t => t.Name)], "F"));
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

        string? npyPath = WriteNpy(npyTarget, data, log);
        log.Notes.Add(CompletionSummary(tracker.Summary, setup.Palace?.Quality ?? PalaceQuality.Standard, wall, FieldFilesBytes(post), Path.Combine(post, PalaceConfigWriter.FieldDirectory)));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "Palace " + palace.DescribeVersion(),
                       NotConverged: PalaceNotConverged(tracker));
    }

    /// <summary>
    /// brief-em3d-31 R-em3d31-3 — Palace's far field through the stage. Each excitation's r·E (Cartesian, poles
    /// written once) is divided by the driven port's own TOTAL voltage, so the pattern is a 1 V port voltage's;
    /// the accepted power at that voltage is (|V_inc|² − |V − V_inc|²)/(2·R·|V|²), the incident-minus-reflected
    /// power of port-V.csv's own waves; and the mismatch factor reads the published S_kk. Only the sweep's own
    /// rows are read, so a save-only Point sample (brief 29) adds no pattern. Null on success.
    /// </summary>
    internal static string? PalacePattern(string post, PalacePortS s, IReadOnlyList<Em3dPort> ports, EmSetup setup, DataSet data,
                                         ICollection<string> notes)
    {
        var numbers = ports.Select(p => p.Number).ToList();
        var ff = PalaceRun.ReadFarField(Path.Combine(post, PalaceRun.FarFieldFile), out string? error);
        if (ff is null) return error;
        var pv = PalaceRun.ReadPortV(Path.Combine(post, PalaceRun.PortVFile), numbers, out error);
        if (pv is null) return error;
        var (vf, vinc, vtot) = pv.Value;

        var grid = FarFieldStage.Sphere();
        int n = ports.Count, nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        var points = new Em3dPatternPoint[s.FrequenciesHz.Length * n];
        for (int i = 0; i < s.FrequenciesHz.Length; i++)
        {
            double fHz = s.FrequenciesHz[i];
            int row = Array.FindIndex(vf, g => Math.Abs(g * 1e9 - fHz) <= 1e-8 * fHz);
            var key = ff.Keys.Where(k => Math.Abs(k.FGHz * 1e9 - fHz) <= 1e-8 * fHz).Select(k => k.FGHz).DefaultIfEmpty(double.NaN).First();
            if (row < 0 || double.IsNaN(key))
                return $"Palace wrote no far field or port voltage at {Fmt(fHz / 1e9)} GHz, a frequency of the sweep.";
            for (int k = 0; k < n; k++)
            {
                if (!ff.TryGetValue((key, numbers[k]), out var dirs))
                    return $"Palace's {PalaceRun.FarFieldFile} has no rows for port {numbers[k]}'s excitation at {Fmt(fHz / 1e9)} GHz.";
                Complex v = vtot[row][k];
                double vi = vinc[row][k], r = ports[k].Z0.Real;
                if (!(v.Magnitude > 0)) return $"Port {numbers[k]}'s own voltage is zero at {Fmt(fHz / 1e9)} GHz in Palace's run.";
                var eth = new Complex[nt * np];
                var eph = new Complex[nt * np];
                for (int it = 0; it < nt; it++)
                    for (int ip = 0; ip < np; ip++)
                    {
                        int th = (int)Math.Round(grid.ThetaDeg[it] * 1000), phi = th is 0 or 180000 ? 0 : (int)Math.Round(grid.PhiDeg[ip] * 1000);
                        if (!dirs.TryGetValue((th, phi), out var e))
                            return $"Palace's {PalaceRun.FarFieldFile} has no row at θ = {grid.ThetaDeg[it]}°, φ = {grid.PhiDeg[ip]}° for port " +
                                   $"{numbers[k]} at {Fmt(fHz / 1e9)} GHz.";
                        (eth[grid.IndexOf(it, ip)], eph[grid.IndexOf(it, ip)]) =
                            NearToFarField.ToSpherical(e.X / v, e.Y / v, e.Z / v, grid.ThetaDeg[it], grid.PhiDeg[ip]);
                    }
                double reflected = (v - vi).Magnitude;
                double accepted = (vi * vi - reflected * reflected) / (2 * r * v.Magnitude * v.Magnitude);
                points[i * n + k] = new Em3dPatternPoint(FarFieldStage.Pattern(grid, eth, eph, numbers[k], fHz), accepted, s.S[i][k, k]);
            }
        }
        notes.AddRange(Em3dRadiation.Publish(data, "Palace (FEM)", grid, numbers, s.FrequenciesHz, points,
                                             setup.ReferenceInputPowerDbm, hemisphere: false));
        notes.Add("Palace's far field is its own Stratton–Chu integral over the air box's absorbing faces, which are first order: " +
                  "the pattern is only as good as the field those faces leave unreflected, so a box close to the radiator reads " +
                  $"its nulls and its back radiation high. {PalaceRun.FarFieldFile} is about 16 MB per frequency per port at the 1° sphere.");
        return null;
    }

    /// <summary>The Palace group's cube holding each wave port's mode impedance Z_PV, [f, Port], ohms.</summary>
    public const string WaveModeImpedanceCube = "WavePortZmode";

    /// <summary>
    /// brief-em3d-23 R-em3d23-4c — an eigenmode solve's modes, read by column name, as a DataSet in
    /// <c>results/</c> under <c>&lt;key&gt;.palace_eig</c>. No Touchstone: a mode is not S. The run directory keeps
    /// whatever Palace wrote (R-em3d23-4d).
    /// </summary>
    private static Leg FinishEigen(Em3dProblem problem, EmSetup setup, string npyTarget, string post, GmshLowering lowering,
                                   PalaceStageTracker tracker, int processes, RunLog log, SolverInstallation palace, TimeSpan wall)
    {
        const Em3dSolverId Me = PalaceSolver.Id;
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

        string? npyPath = WriteNpy(npyTarget, data, log);
        log.Notes.Add(CompletionSummary(tracker.Summary, setup.Palace?.Quality ?? PalaceQuality.Standard, wall, FieldFilesBytes(post), Path.Combine(post, PalaceConfigWriter.FieldDirectory)));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "Palace " + palace.DescribeVersion(),
                       NotConverged: PalaceNotConverged(tracker));
    }

    /// <summary>What the matrix is referred to, in a sentence.</summary>
    private static string GroundText(Em3dProblem problem, EmSetup setup)
    {
        bool pecFace = new[] { problem.Boundary.Faces.XMin, problem.Boundary.Faces.XMax, problem.Boundary.Faces.YMin,
                               problem.Boundary.Faces.YMax, problem.Boundary.Faces.ZMin, problem.Boundary.Faces.ZMax }
                       .Contains(Em3dBoundaryKind.Pec);
        string who = Em3dGroundList.Describe(setup.Ground3D);
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

    /// <summary>
    /// brief-em3d-29 R-em3d29-1b — a field saved at a frequency that is not a sweep point is a Point sample
    /// Palace evaluates like any other, so its row is in <c>port-S.csv</c> too. Those rows are dropped here:
    /// the <c>.sNp</c> holds the sweep the setup asked for, and nothing else.
    /// </summary>
    internal static PalacePortS WithoutSaveOnlyRows(PalacePortS s, double[] requested)
    {
        if (s.FrequenciesHz.Length <= requested.Length || SweepRows(s.FrequenciesHz, requested) is not { } rows) return s;
        return s with { FrequenciesHz = [.. rows.Select(k => s.FrequenciesHz[k])], S = [.. rows.Select(k => s.S[k])] };
    }

    /// <summary>The row matching each requested frequency to the printed precision, in order; null when one
    /// has no row.</summary>
    internal static int[]? SweepRows(double[] have, double[] requested)
    {
        const double PrintedPrecision = 1e-8;
        var rows = new int[requested.Length];
        int from = 0;
        for (int k = 0; k < requested.Length; k++)
        {
            int at = -1;
            for (int j = from; j < have.Length; j++)
                if (Math.Abs(have[j] - requested[k]) <= PrintedPrecision * Math.Abs(requested[k])) { at = j; break; }
            if (at < 0) return null;
            rows[k] = at;
            from = at + 1;
        }
        return rows;
    }


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
        Em3dPortMap.AddTo(ds, PalaceGroup, ports);          // brief-em3d-93

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
                                 PalaceRunFacts facts, PalaceRunSummary summary, PalaceQuality quality, TimeSpan wall,
                                 IReadOnlyList<string> dcLines, string? stamp = null)
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
        if (problem.MeshFrequencyHz is { } sizedAt) settingsText += "|mesh frequency " + R(sizedAt);
        string materialsText = string.Join("|", problem.Materials.Select(m =>
            $"{m.Name}:{R(m.Epsr)}:{R(m.TanD)}:{R(m.Mur)}:{R(m.SigmaSm)}"));

        // Which numbered port is which named port, first and in the spelling other tools read (Port[N] = name).
        var lines = new List<string>(Em3dPortMap.NameLines(problem.Ports));
        foreach (var p in problem.Ports.OrderBy(p => p.Number))
            lines.Add($"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{Ascii(p.Name)}' from '{Ascii(p.NegativeObject)}' " +
                      $"to '{Ascii(p.PositiveObject)}', " + (p.Kind == Em3dPortKind.Wave
                          // R-em3d23-2d — the file states the one reference its numbers are in, and where they came from.
                          ? $"wave (mode 1), {R(p.Z0.Real)} Ohm: Palace's S is referred to the mode's own impedance Z_PV, " +
                            $"renormalised here to {R(p.Z0.Real)} Ohm; reference plane {R(p.ReferencePlane.ShiftM * 1e6)} um " +
                            "into the structure from the port face"
                          : $"lumped, {R(p.Z0.Real)} Ohm"));
        lines.AddRange(Em3dPortMap.Lines(problem.Ports));     // brief-em3d-93 — which document port each is, when one was off
        lines.Add($"circuitRF-EM 3D mesh: {Count(facts.InitialElements)} elements initially, {Count(facts.FinalElements)} " +
                  $"finally, {facts.AdaptiveIterations ?? 0} adaptive pass(es), element order {settings.ElementOrder}" +
                  (problem.MeshFrequencyHz is { } meshedAt ? $", sized at {R(meshedAt / 1e9)} GHz" : ""));
        lines.Add($"circuitRF-EM 3D operating temperature: {R(problem.OperatingTempC)} C");
        // brief-em3d-21 R-em3d21-5b — added fields, none renamed. What the solve WAS is one line; what it
        // COST (wall time, peak memory) is another, because it differs between two runs of one setup
        // exactly as the write stamp does.
        lines.Add(RunProvenancePrefix + RunProvenance(summary, quality));
        lines.Add(CostProvenancePrefix + CostProvenance(summary, wall));
        lines.AddRange(dcLines);                              // brief-emdc-2 R-emdc2-4

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
                DateTimeOffset.Now, stamp));

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

    /// <summary>
    /// brief-em3d-136 R-em3d136-2a — how many Palace processes, and the note that says so. On the CPU: one per physical
    /// core, or the Cores setting (brief-em3d-21). On a GPU: one per GPU the probe counted — Palace assigns ranks to GPUs
    /// round-robin, so a second rank per GPU would share it — capped by the Cores setting when that is lower.
    /// </summary>
    internal static (int Processes, string? Note) PalaceProcessCount(PalaceDevice device, int? maxCores, PhysicalCoreReading cores,
                                                                      int? gpus)
    {
        if (device == PalaceDevice.Cpu)
        {
            int processes = maxCores ?? cores.Count;
            return (processes, maxCores is null && !cores.Measured ? $"Palace ran on {processes} process(es): {cores.How}." : null);
        }
        int n = Math.Max(1, gpus ?? 1);
        return maxCores is { } cap && cap < n
            ? (cap, $"Palace ran on {cap} of {n} GPUs, one process each: the Cores setting allows {cap}.")
            : (n, $"Palace ran on {n} GPU(s), one process each.");
    }

    /// <summary>
    /// brief-em3d-136 R-em3d136-2b — <see cref="RankRefusal"/> on the count a run would use. On a GPU run it applies only
    /// when the Cores setting is what sets the count (it is lower than the GPU count): a count derived from GPUs is
    /// not a core count, and two GPUs on a one-core machine are no reason to refuse.
    /// </summary>
    internal static string? PalaceRankRefusal(PalaceDevice device, int? maxCores, PhysicalCoreReading cores, int? gpus)
        => device == PalaceDevice.Cpu || maxCores is { } cap && cap < Math.Max(1, gpus ?? 1)
            ? RankRefusal(maxCores, cores)
            : null;

    /// <summary>brief-em3d-136 R-em3d136-5a — the run asked for a GPU and Palace's log says it solved on the CPU.</summary>
    internal static string GpuRevertedWarning(string runDir)
        => $"Palace was asked to use a GPU but ran on the CPU; see its log in {runDir}.";

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
        // The background is meshed as GmshGeoWriter meshes it: the box's own fill (3D editor round 3), else the problem's
        // air, else free space.
        string fill = p.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(p.Solids, p.Materials);
        var background = byName.TryGetValue(fill, out var bg) ? bg : new Em3dMaterial("(free space)", 1, null, 0, 1, 0);
        // brief-dfr26-8 — a static problem is sized as the script sizes it: one element, a fraction of the air box's largest
        // side, in every material. Pricing it by wavelength had a ten-layer electrostatic run at 37 GB against 4.9 GB used.
        var (lo, hi) = (p.Boundary.Min, p.Boundary.Max);
        double staticSize = GmshGeoWriter.StaticMaxElementSizeM(Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z)), settings);
        double SolidSize(Em3dSolid s) => p.IsStatic ? staticSize
            : GmshGeoWriter.MaxElementSizeM(byName[s.Material], GmshGeoWriter.SizingFrequencyHz(p), settings);
        double backgroundSize = p.IsStatic ? staticSize
            : GmshGeoWriter.MaxElementSizeM(background, GmshGeoWriter.SizingFrequencyHz(p), settings);
        return Em3dSizeEstimate.Palace(p, SolidSize, settings.ElementOrder, backgroundSize, settings.AdaptiveMaxIterations,
                                       GmshGeoWriter.RefinementSizing(p, SolidSize, backgroundSize, settings),
                                       direct: settings.LinearSolver == PalaceLinearSolver.Direct);
    }

    /// <summary>The fit check for a setup as the panel is about to run it: generates the problem and
    /// compares against this machine. The panel calls it before Simulate starts anything.</summary>
    public static Em3dMemoryVerdict PalaceMemoryVerdict(EmSetup setup, EmLayoutSource source)
    {
        if (source.Technology is null || !setup.Solver3D.Runs(PalaceSolver.Id)) return Em3dMemoryVerdict.Unknown;
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
                                                        PalaceSettings settings, long physicalBytes, Em3dMemoryScope? scope = null,
                                                        int? processes = null)
        => PalaceMemoryVerdict(problem, setup,
                               source.Technology is { } tech ? s => Em3dGenerator.Generate(s, source, tech) : null,
                               settings, physicalBytes, scope, processes);

    /// <summary>brief-em3d-42 — the same check, regenerating the half-padding remedy through
    /// <paramref name="regenerate"/> (null: no such remedy is priced).</summary>
    /// <param name="processes">brief-dfr27-5 R-dfr27-5-5 — the MPI processes the run will use: the estimate is scaled to them
    /// (<see cref="Em3dSizeEstimate.PalaceProcessFactor"/>) and fewer is offered when fewer would fit. Null: the count the
    /// per-unknown figures were measured on, and no such remedy.</param>
    public static Em3dMemoryVerdict PalaceMemoryVerdict(Em3dProblem problem, EmSetup setup,
                                                        Func<EmSetup, Em3dGenerationResult>? regenerate,
                                                        PalaceSettings settings, long physicalBytes, Em3dMemoryScope? scope = null,
                                                        int? processes = null)
    {
        // brief-em3d-22 — the volume estimate prices elements per WAVELENGTH, which a static solve does
        // not have; its check is the one after meshing, on Gmsh's own count.
        if (problem.IsStatic) return Em3dMemoryVerdict.Unknown;
        long? At(PalaceSettings s, Em3dProblem? q = null) => Scaled(EstimatePalace(q ?? problem, s)?.MemoryBytes, processes);
        long? estimate = At(settings);
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
            remedies.Add(new("the Draft preset (Palace.Quality: Draft)", At(asDraft)));
        // brief-dfr27-5 R-dfr27-5-5 — first-order elements on their own, keeping the preset's refinement and tolerances.
        if (settings.ElementOrder == 2)
            remedies.Add(new("first-order elements (Palace.ElementOrder: 1)", At(settings with { ElementOrder = 1 })));
        if (settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("no refinement passes (Palace.AdaptiveMaxIterations: 0)",
                             At(settings with { AdaptiveMaxIterations = 0 })));
        if (regenerate is not null)
        {
            var (lo, hi) = Em3dGenerator.ContentBounds(problem);
            var defaults = Em3dGenerator.DefaultFacePadding(setup, problem.Frequency.StartHz,
                               Math.Max(Math.Max(hi.X - lo.X, hi.Y - lo.Y), hi.Z - lo.Z), problem.PaddingHeightM,
                               problem.PaddingHeightBelowM);
            EmAirBoxFace Half(EmAirBoxFace? f, string face) => f?.PaddingPercent is { } pct
                ? new(null, f.Boundary, pct / 2)
                : new((f?.PaddingUm ?? defaults.For(face, face[0] switch { 'x' => hi.X - lo.X, 'y' => hi.Y - lo.Y, _ => hi.Z - lo.Z }) * 1e6) / 2,
                      f?.Boundary);
            var box = setup.AirBox ?? new EmAirBox();
            var smaller = setup.Clone();
            smaller.AirBox = new EmAirBox(Half(box.XMin, "xmin"), Half(box.XMax, "xmax"), Half(box.YMin, "ymin"), Half(box.YMax, "ymax"),
                                          Half(box.ZMin, "zmin"), Half(box.ZMax, "zmax"));
            var regenerated = regenerate(smaller);
            if (regenerated.Ok && regenerated.Problem is { } q)
                remedies.Add(new("an air box with half the padding on every side (AirBox)", At(settings, q)));
        }
        var iterative = IterativeRemedy(settings, At(settings with { LinearSolver = PalaceLinearSolver.Iterative }));
        if (iterative is not null) remedies.Add(iterative);
        remedies.AddRange(FitRemedies(e, processes, physicalBytes));
        return Em3dMemoryVerdict.Evaluate(e, physicalBytes, [.. remedies.OrderBy(r => r.EstimateBytes ?? long.MaxValue)],
            basis: "Before meshing, from the 3D model's volumes:", scope: scope);
    }

    /// <summary>brief-dfr27-5 R-dfr27-5-5 — <paramref name="bytes"/>, measured on <see cref="Em3dSizeEstimate.PalaceMeasuredProcesses"/>
    /// processes, on <paramref name="processes"/> (unchanged when that is null).</summary>
    private static long? Scaled(long? bytes, int? processes)
        => bytes is { } b && processes is { } n
            ? (long)Math.Round(b * Em3dSizeEstimate.PalaceProcessFactor(n) / Em3dSizeEstimate.PalaceProcessFactor(Em3dSizeEstimate.PalaceMeasuredProcesses))
            : bytes;

    /// <summary>
    /// brief-dfr27-5 R-dfr27-5-5 — the two remedies every Palace estimate past the machine can be priced for without a new
    /// model. FEWER PROCESSES: the most that fit, or one when none does — this run can take it itself, so the question offers
    /// it as a button; each rank carries its own preconditioner, and one rank measured 2.5 GB where eight took 4.3, at twice
    /// the time. A SMALLER CROP: the share of this crop's area that would fit, since the elements follow the volume.
    /// </summary>
    internal static IEnumerable<Em3dMemoryRemedy> FitRemedies(long estimate, int? processes, long physicalBytes)
    {
        if (physicalBytes <= 0 || estimate <= physicalBytes) yield break;
        if (processes is { } now && now > 1)
        {
            static double Factor(int n) => Em3dSizeEstimate.PalaceProcessFactor(n);
            int fits = 1;
            for (int n = now - 1; n >= 1; n--)
                if (estimate * Factor(n) / Factor(now) <= physicalBytes) { fits = n; break; }
            long at = (long)Math.Round(estimate * Factor(fits) / Factor(now));
            yield return new($"{fits} MPI process{(fits == 1 ? "" : "es")} instead of {now} (slower; the Cores setting)", at)
                         { Processes = fits };
        }
        double share = 0.9 * physicalBytes / estimate;
        yield return new($"a crop about {Math.Floor(share * 100).ToString("0", CultureInfo.InvariantCulture)} % of this one's area " +
                         "(Crop to New Cell; the mesh follows the volume)", (long)Math.Round(estimate * share)) { Sized = true };
    }

    /// <summary>
    /// R-em3d21-2 — the check once Gmsh has made the mesh: <paramref name="tetrahedra"/> as Gmsh
    /// reported creating them, through the measured unknowns and bytes per unknown. The remedies are the
    /// ones a mesh this size can be priced for without meshing again.
    /// </summary>
    public static Em3dMemoryVerdict MeshMemoryVerdict(long tetrahedra, PalaceSettings settings, long physicalBytes,
                                                      Em3dMemoryScope? scope = null, bool isStatic = false, int? processes = null)
    {
        if (settings.ElementOrder is not (1 or 2)) return Em3dMemoryVerdict.Unknown;
        // brief-dfr26-8 — a static solve's unknowns are scalar: priced as a field's, a ten-layer electrostatic run that used
        // 4.9 GB read several times that and would have been refused on a smaller machine.
        if (isStatic)
            return Em3dSizeEstimate.PalaceStaticMemoryBytesForMesh(tetrahedra, settings.ElementOrder, settings.AdaptiveMaxIterations) is { } se
                ? Em3dMemoryVerdict.Evaluate(se, physicalBytes, [],
                      basis: $"Gmsh made {tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra.", scope: scope)
                : Em3dMemoryVerdict.Unknown;
        bool direct = settings.LinearSolver == PalaceLinearSolver.Direct;
        long For(int order, int passes, bool asDirect = true)
            => Scaled(Em3dSizeEstimate.PalaceMemoryBytesForMesh(tetrahedra, order, passes, direct && asDirect), processes)!.Value;
        long e = For(settings.ElementOrder, settings.AdaptiveMaxIterations);
        var remedies = new List<Em3dMemoryRemedy>();
        if (settings.ElementOrder != 1 || settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("the Draft preset (Palace.Quality: Draft)", For(1, 0)));
        // brief-dfr27-5 R-dfr27-5-5 — first-order elements on their own.
        if (settings.ElementOrder == 2 && settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("first-order elements (Palace.ElementOrder: 1)", For(1, settings.AdaptiveMaxIterations)));
        if (settings.AdaptiveMaxIterations > 0)
            remedies.Add(new("no refinement passes (Palace.AdaptiveMaxIterations: 0)", For(settings.ElementOrder, 0)));
        var iterative = IterativeRemedy(settings, For(settings.ElementOrder, settings.AdaptiveMaxIterations, asDirect: false));
        if (iterative is not null) remedies.Add(iterative);
        remedies.AddRange(FitRemedies(e, processes, physicalBytes));
        return Em3dMemoryVerdict.Evaluate(e, physicalBytes, [.. remedies.OrderBy(r => r.EstimateBytes)],
            basis: $"Gmsh made {tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra.", scope: scope);
    }

    /// <summary>
    /// EMB-4 R-emb4-1 — once Gmsh has meshed: its count against the estimate <c>explain</c> printed before meshing, and
    /// Palace's memory at Gmsh's count for the linear solver <paramref name="settings"/> names (the one the run uses), on
    /// <paramref name="processes"/> MPI processes (null: the count the figures were measured on). Null at an element order
    /// or a static order with no measured memory figure.
    /// </summary>
    public static string? MeshedAgainstEstimate(long tetrahedra, Em3dPalaceEstimate? before, PalaceSettings settings,
                                                bool isStatic = false, int? processes = null)
    {
        if (settings.ElementOrder is not (1 or 2)) return null;
        long? bytes = isStatic
            ? Em3dSizeEstimate.PalaceStaticMemoryBytesForMesh(tetrahedra, settings.ElementOrder, settings.AdaptiveMaxIterations)
            : Em3dSizeEstimate.PalaceMemoryBytesForMesh(tetrahedra, settings.ElementOrder, settings.AdaptiveMaxIterations,
                                                        settings.LinearSolver == PalaceLinearSolver.Direct);
        if (Scaled(bytes, processes) is not { } b) return null;
        var c = CultureInfo.InvariantCulture;
        string against = before is { Tetrahedra: > 0 } e
            ? $" against the {e.Tetrahedra.ToString("N0", c)} estimated before meshing " +
              $"({((double)tetrahedra / e.Tetrahedra).ToString("0.0", c)}×)"
            : "";
        string solver = isStatic ? "" : settings.LinearSolver == PalaceLinearSolver.Direct
            ? " on the direct linear solver" : " on the iterative linear solver";
        return $"Gmsh made {tetrahedra.ToString("N0", c)} tetrahedra{against}; at that count Palace needs about " +
               $"{MachineMemory.Format(b)}{solver}" +
               (settings.AdaptiveMaxIterations > 0 ? " with refinement passes allowed" : "") + ".";
    }

    /// <summary>The direct linear solver's way out: the iterative one, at its own estimate, when the setup asks for direct.
    /// The run can take it itself (<see cref="Em3dMemoryRemedy.Iterative"/>), so the question offers it as a button.</summary>
    private static Em3dMemoryRemedy? IterativeRemedy(PalaceSettings settings, long? iterativeBytes)
        => settings.LinearSolver == PalaceLinearSolver.Direct && iterativeBytes is { } b
            ? new("the iterative linear solver (Palace.LinearSolver: Iterative; slower on some problems, far less memory)", b)
              { Iterative = true }
            : null;

    /// <summary>R-em3d21-5a — "Palace done in 2 min 27 s · peak memory 7.4 GB · …", every figure the
    /// parser read from Palace's log, and a field Palace did not print left out rather than guessed.</summary>
    public static string CompletionSummary(PalaceRunSummary s, PalaceQuality quality, TimeSpan wall, long? fieldBytes = null,
                                           string? fieldDir = null)
    {
        var parts = new List<string> { $"Palace done in {Duration(wall)}" };
        if (s.PeakMemoryBytes is { } m) parts.Add($"peak memory {MachineMemory.Format(m)} (Palace's own)");
        if (s.InitialElements is { } a)
            parts.Add(s.FinalElements is { } b && b != a ? $"{a:N0} → {b:N0} tetrahedra" : $"{a:N0} tetrahedra");
        if (s.Unknowns is { } u) parts.Add($"{u:N0} unknowns");
        if (s.RefinementPasses is { } r) parts.Add($"{r} refinement pass{(r == 1 ? "" : "es")}");
        // Designer feedback round 11 — Palace's adaptive sweep counts the frequencies it SOLVED to build its model, which is
        // not the requested point count (6 beside a 5-point sweep read as a miscount), so the clause says what they are.
        if (s.SweepSamples is { } n) parts.Add($"adaptive sweep built from {n} full solve{(n == 1 ? "" : "s")}");
        parts.Add($"preset {quality}");
        // R-em3d29-1d — what the saved fields cost on disk, so a user saving twenty frequencies sees it.
        // brief-dfr32-3 R-dfr32-3-2 — and where they are, so large files left after a run have an answer on screen.
        if (fieldBytes is > 0 and var f) parts.Add($"field files {MachineMemory.Format(f)}" + (fieldDir is null ? "" : $" in {fieldDir}"));
        if (!s.LogRecognised) parts.Add("Palace's log format was not recognised, so some figures are missing");
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>The total size of the field files Palace wrote under <paramref name="post"/>, or null
    /// when it wrote none.</summary>
    public static long? FieldFilesBytes(string post)
    {
        string dir = Path.Combine(post, PalaceConfigWriter.FieldDirectory);
        try
        {
            if (!Directory.Exists(dir)) return null;
            long n = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length;
            return n;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
                Em3dSphere s => s.Radius,
                _ => null,
            };
            if (radius is not { } r || !sigma.TryGetValue(solid.Material, out var m) || !(m.SigmaSm > 0)) continue;
            double delta = 1 / Math.Sqrt(Math.PI * problem.Frequency.StartHz * Mu0 * m.Mur * m.SigmaSm);
            // Under three skin depths the radius is not a curvature correction any more: Em3dThinMetalLoss warns of what that reads.
            if (r < 10 * delta && r >= Em3dThinMetalLoss.SkinDepths * delta) names.Add(solid.Name);
        }
        return names;
    }
}
