// brief-emi-4 / brief-emi-5 — EMerge's part of Em3dRunService: its leg of a run. Configure finds EMerge (finishing its faster
// solver first when that is due, EMI-3) and resolves its settings; Prepare lowers the problem (no process); Execute writes the
// input, runs the driver, checks the mesh's memory the moment it exists, and reads the arrays it writes into the S DataSet and
// Touchstone, or the eigenmode table.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static partial class Em3dRunService
{
    /// <summary>The diagnostic source an EMerge memory refusal carries.</summary>
    public const string EmergeMemoryRefusalSource = "emerge-memory";

    /// <summary>brief-dfr28-3 — the source of the driver's refusal of a solve that is not finite (a degenerate mesh element).</summary>
    public const string EmergeSolveRefusalSource = "emerge-solve";

    internal sealed class EmergeLeg : SolverLeg
    {
        private EmergeSettings? _settings;
        private EmergeLowering? _lowering;
        private EmergeFieldRequest? _fields;
        private SolverInstallation? _emerge;
        private long? _gpuFrom;

        public EmergeLeg() : base(EmergeSolver.Descriptor) { }

        public override void Configure(RunContext run)
        {
            var (readiness, log, setup) = (run.Readiness, run.Log, run.Setup);
            var found = readiness.Single(r => r.Tool == SolverTool.Emerge);
            log.Notes.Add($"Solver: EMerge {found.Installation!.DescribeVersion()} at {found.Installation.Path}.");
            // An untested EMerge runs, and the run says so among its Warnings (R-emi2-6 as the owner amended it).
            if (found.Warning is { } untested) log.Warnings.Add(untested);
            // R-emi3-3 — the faster solver is finished here when it is due, and the run uses what that leaves.
            _emerge = EmergeAccelerator.BeforeRun(found.Installation, log.Notes, log.Warnings);
            if (setup.Problem3D == Em3dProblemType.Eigenmode) log.Notes.Add(EmergeAccelerator.EigenmodeNote);
            // brief-emi-9 R-emi9-3 — where it factorises: the machine's preference, the problem and what is installed.
            bool gpu = _emerge.Emerge?.Gpu == true;
            _gpuFrom = EmergeDevicePolicy.GpuFromUnknowns(setup.Problem3D, gpu, run.EmergeDevice);
            if (run.EmergeDevice == EmergeDevice.Gpu && setup.Problem3D == Em3dProblemType.Eigenmode)
                log.Notes.Add(EmergeAccelerator.EigenmodeGpuNote);
            else if (run.EmergeDevice == EmergeDevice.Gpu && !gpu)
                log.Warnings.Add("Device GPU was asked for, but EMerge's GPU sparse solver is not installed here, so this run solves on " +
                                 "the CPU. " + (EmergeAccelerator.GpuSentence(_emerge) ?? ""));
            _settings = EmergeSettings.Resolve(setup.Emerge);
            if (_settings.Problems().Concat(EmergeFields.Problems(setup.Emerge)).ToList() is { Count: > 0 } bad)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("emerge-settings", string.Join(" ", bad)));
        }

        public override void Prepare(Em3dProblem problem, RunContext run)
        {
            if (Stop is not null) return;
            _fields = problem.PlaneWave is null ? EmergeFields.Request(run.Setup, problem) : null;
            // brief-hzn-12 — a scattering run saves no field files in this version: its result is the cross-section.
            if (problem.PlaneWave is not null && run.Setup.Emerge?.SaveFieldsGHz is { Count: > 0 })
                run.Log.Notes.Add("A scattering run on EMerge saves no field files in this version, so EMerge.SaveFieldsGHz is not read.");
            _lowering = EmergeScriptWriter.Write(problem, _settings!, _fields);
            run.Log.Notes.AddRange(_lowering.Notes);
            if (_lowering.Refusal is { } refused)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("emerge-lowering", refused));
            else if (EmergeScriptWriter.RadiationPatternNote(run.Setup, problem) is { } noPattern)
                run.Log.Notes.Add(noPattern);
        }

        public override Leg Execute(Em3dProblem problem, string snpBase, RunContext run)
            => ExecuteEmerge(problem, snpBase, run, _settings!, _lowering!, _emerge!, _gpuFrom);
    }

    /// <summary>
    /// brief-emi-9 R-emi9-3 — runs <paramref name="attempt"/> with <paramref name="gpuFrom"/>, and once more on the CPU (null) when
    /// <see cref="EmergeDevicePolicy.RerunOnCpu"/> says so: an Auto run on the GPU whose adaptive sweep stopped at its cap. Returns
    /// the attempt that is kept and, after a re-run, the Note that names both sample counts. At most one re-run.
    /// </summary>
    internal static (T Result, string? Note) WithCpuFallback<T>(EmergeDevice preference, long? gpuFrom,
                                                               Func<long?, (T Result, EmergeRunFacts? Facts)> attempt)
    {
        var first = attempt(gpuFrom);
        if (gpuFrom is null || first.Facts is not { } gpu || !EmergeDevicePolicy.RerunOnCpu(preference, gpu)) return (first.Result, null);
        var second = attempt(null);
        string cpu = second.Facts is not { } c ? "and that run did not finish"
            : c.Converged == false ? $"where it also stopped at its limit, at {Count(c.Samples)} frequencies"
            : $"where it converged with {Count(c.Samples)}";
        return (second.Result, $"EMerge's adaptive sweep on the GPU stopped at its limit of {Count(gpu.Samples)} frequencies without " +
                               $"converging (estimated error {(gpu.Error is { } e ? PalaceStageTracker.Sci(e) : "unreported")}), so circuitRF " +
                               $"solved it again on the CPU, {cpu}. This result is the CPU's.");
    }

    /// <summary>brief EMG-1 R-emg1-4 — what a run says when the CPU that replaced an untrustworthy GPU solve could not finish either:
    /// a refusal, with no result, because what the GPU produced is not one.</summary>
    internal const string LeftTheGpuSentence =
        "EMerge's GPU solver could not solve this problem accurately in single precision, so circuitRF moved the sweep to the CPU, " +
        "and the CPU could not finish it. ";

    /// <summary>brief-emw-3, brief EMG-1 — the note of a sweep the driver moved to the CPU, and why; null when it stayed.</summary>
    internal static string? GpuStallNote(EmergeGpuStall? stall) => stall switch
    {
        null => null,
        { Unusable: true, Residual: { } residual } =>
            $"A solve on EMerge's GPU solver missed its equations by {PalaceStageTracker.Sci(residual)} (‖Ax − b‖/‖b‖, against a limit of " +
            "1.0e-3), so circuitRF solved the sweep on the CPU, on the same mesh. This result is the CPU's. Set EMerge's Device to CPU in " +
            "Settings ▸ Solvers to start there.",
        { Unusable: true } =>
            "EMerge's GPU solver found its single-precision factorisation of this problem unusable, so circuitRF solved " +
            "the sweep on the CPU, on the same mesh. This result is the CPU's. Set EMerge's Device to CPU in Settings ▸ " +
            "Solvers to start there.",
        _ => $"EMerge's adaptive sweep on the GPU stopped improving: its estimated error was no lower after {Count(stall.Samples)} " +
             $"frequencies than its best ({(stall.BestError is { } be ? PalaceStageTracker.Sci(be) : "unreported")}, at {Count(stall.BestAt)}), " +
             "so circuitRF solved it again on the CPU, on the same mesh. This result is the CPU's. The GPU's single precision " +
             "can leave an adaptive sweep's samples too noisy to fit; set EMerge's Device to CPU in Settings ▸ Solvers to start there.",
    };

    private static Leg ExecuteEmerge(Em3dProblem problem, string snpBase, RunContext run, EmergeSettings settings,
                                     EmergeLowering lowering, SolverInstallation emerge, long? gpuFrom)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        Leg Failed(string reason) => Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(reason));
        var (setup, log, control) = (run.Setup, run.Log, run.Control);
        var wall = Stopwatch.StartNew();
        bool eigen = problem.Type == Em3dProblemType.Eigenmode;
        long physical = MachineMemory.PhysicalBytes;

        string dir = RunDirectory(run.ResultsRoot, setup, Me);
        var tier = emerge.Emerge?.Tier ?? EmergeSpeedTier.Unknown;
        long? peak = null;

        // One run of the driver: a failed leg, or what its run.json says. brief-emi-9 — run twice at most (WithCpuFallback).
        (Leg? Failed, EmergeRunFacts? Facts) Attempt(long? from)
        {
            try
            {
                // An earlier run's arrays would be read as this one's if this one stopped before writing its own.
                Directory.CreateDirectory(dir);
                foreach (string old in new[] { EmergeResults.FrequencyFile, EmergeResults.SFile, EmergeResults.Z0File,
                                               EmergeResults.GammaFile, EmergeResults.ModesFile, EmergeResults.QFile, EmergeResults.RunFile,
                                               EmergeTerminalS.TVFile, EmergeTerminalS.WFile, EmergeTerminalS.GpFile, EmergeTerminalS.VFile,
                                               EmergeScattering.FarFile, EmergeScattering.ThetaFile, EmergeScattering.PhiFile,
                                               EmergeScattering.MonostaticFile })
                    File.Delete(Path.Combine(dir, old));
                EmergeFields.Clear(dir);
                EmergeScriptWriter.WriteInto(lowering, dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (Failed($"EMerge's input could not be written to '{dir}' ({e.Message})."), null);
            }

            // R-emi5-3 — the memory check on the mesh, the moment the driver has made it: a refusal stops the process tree.
            string? tooBig = null;
            var tracker = new EmergeStageTracker(control, eigen)
            {
                OnMeshed = mesh =>
                {
                    if (run.Memory.Admit(EmergeMemory.Verdict(mesh, settings, tier, eigen, physical), log) is not { } refusal) return;
                    tooBig = refusal;
                    throw new OperationCanceledException();
                },
            };
            var psi = EmergeRun.StartInfo(emerge.Path, dir,
                                          EmergeThreads.Environment(run.MaxCores, PhysicalCores.Count, Environment.ProcessorCount), from);
            PalaceRun.ProcessRun ran;
            // brief-emi-10 — meshing is EMerge's own until it reports its mesh, and it is the stage a runaway happens in.
            var limits = run.Limits(physical);
            try { ran = EmergeRun.Solve(psi, control, run.Ct, tracker, physical, limits); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (Failed($"EMerge could not be started in '{dir}' ({e.Message})."), null);
            }
            if (tooBig is not null) return (Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(EmergeMemoryRefusalSource, tooBig)), null);
            if (ran.Cancelled) return (Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled()), null);
            if (ran.StartFailure is { } start) return (Failed($"EMerge could not be started ({start})."), null);
            if (ran.MemoryNote is not null) log.Warnings.Add(ran.MemoryNote);
            string logPath = Path.Combine(dir, EmergeResults.LogFile);
            if (ran.Stopped is { } stopped)
                return (StoppedLeg(Me, "EMerge", stopped, limits, stopped.Meshing ? [] : run.Memory.Last?.Remedies ?? [], logPath,
                                   tracker.LeftTheGpuUnusable ? LeftTheGpuSentence : null), null);
            // brief-emw-3 R-emw3-4 — a terminal face whose modes cannot be converted is refused by name, as before the solve
            // brief-dfr28-3 R-dfr28-3-1 — and a solve whose matrix or solution is not finite, at the first frequency, by its element
            if (ran.ExitCode != 0 && EmergeLog.DriverRefusal(ran.Tail, out string? cause) is { } refusal)
                return (Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded(
                    cause == EmergeLog.NonFiniteSolveCause ? EmergeSolveRefusalSource : TerminalPortsSource, refusal)), null);
            if (ran.ExitCode != 0)
                return (Failed((tracker.LeftTheGpuUnusable ? LeftTheGpuSentence : "") + (EmergeLog.DriverError(ran.Tail) is { } said
                    ? $"EMerge stopped: {said} Its log is {logPath}."
                    : $"EMerge exited with code {ran.ExitCode}: {PalaceRun.Quote(ran.Tail, "Error")}. Its log is {logPath}.")), null);
            peak = ran.PeakResident;
            return (null, EmergeResults.ReadFacts(dir));
        }

        var (attempt, rerun) = WithCpuFallback(run.EmergeDevice, gpuFrom, from => { var r = Attempt(from); return (r, r.Facts); });
        var (failed, read) = attempt;
        if (failed is not null) return failed;
        var facts = read!;
        if (rerun is not null) log.Notes.Add(rerun);
        log.Warnings.AddRange(facts.Warnings);
        control?.BeginStage(ReadingLabel);
        string version = facts.Version ?? emerge.Release ?? "(version unreported)";
        if (problem.PlaneWave is not null)
            return FinishEmergeScattered(problem, setup, run.Paths(Me).NpyPath, dir, facts, tier, version, peak, wall.Elapsed, log);
        return eigen
            ? FinishEmergeEigen(problem, setup, run.Paths(Me).NpyPath, dir, facts, tier, version, peak, wall.Elapsed, log)
            : FinishEmergeDriven(problem, setup, run, dir, settings, lowering, facts, tier, version,
                                 peak, wall, log, run.Dc);
    }

    private static Leg FinishEmergeDriven(Em3dProblem problem, EmSetup setup, RunContext run, string dir,
                                          EmergeSettings settings, EmergeLowering lowering, EmergeRunFacts facts, EmergeSpeedTier tier,
                                          string version, long? peak, Stopwatch wall, RunLog log, Em3dDcResult? dc = null)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        var ports = problem.Ports.OrderBy(p => p.Number).ToList();
        var sweep = EmergeResults.ReadDriven(dir, ports.Count, out string? readError);
        if (sweep is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(readError!));
        if (EmergeResults.NoFieldFrequencies(sweep) is { Length: > 0 } empty)
            return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(
                $"EMerge returned no field at {(empty.Length == sweep.S.Length ? "any" : $"{empty.Length} of {sweep.S.Length}")} " +
                "of its frequencies: every port reads a short and nothing passes between ports (S = −1 on the diagonal, 0 elsewhere), " +
                "which is what a solve that failed without saying so leaves behind. No .sNp was written. EMerge's log is " +
                $"{Path.Combine(dir, EmergeResults.LogFile)}."));
        var requested = FrequenciesHz(problem.Frequency);
        if (sweep.FrequenciesHz.Length != requested.Length ||
            sweep.FrequenciesHz.Where((f, k) => Math.Abs(f - requested[k]) > 1e-9 * Math.Abs(requested[k])).Any())
            return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(
                $"EMerge's {EmergeResults.FrequencyFile} does not list the {requested.Length} frequencies the sweep asked for."));
        // brief-emw-2 R-emw2-4 — a wave port's S is referred to its mode (Z_PV per frequency); a lumped port's to its own Z0
        var wave = ports.Where(p => p.Kind == Em3dPortKind.Wave).ToList();
        Complex[][] modeZ = [.. sweep.Z0.Select(row => wave.Select(w => row[ports.IndexOf(w)]).ToArray())];
        bool lumpedMoved = sweep.Z0.Any(row => ports.Where((p, i) => p.Kind == Em3dPortKind.Lumped &&
                                                                     (row[i] - p.Z0).Magnitude > 1e-9 * Math.Max(1, p.Z0.Magnitude)).Any());
        // brief-emw-3 R-emw3-5 — with a terminal group, EMerge's S is MODAL on that face: every port goes through the conversion
        // (a one-entry face reduces to the renormalisation from its Z_PV), from S and the faces' own modes
        (WavePortModalSamples Samples, WavePortTerminalOutcome Outcome, EmergeModalArrays Arrays)? terminal = null;
        if (Em3dPortMap.HasTerminals(ports))
        {
            var arrays = EmergeTerminalS.Read(dir, requested.Length, ports.Count, out string? arraysError);
            if (arrays is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(arraysError!));
            var faces = WavePortTerminalS.Faces(ports);
            var raw = sweep.S;
            // a one-terminal port's moved reference plane, in its mode's own normalisation, before the conversion (a group's is refused)
            var shifted = ports.Any(p => p.ReferencePlane.ShiftM != 0) ? WavePortReferences.Shift(raw, ports, sweep.Gamma) : raw;
            var samples = new WavePortModalSamples((double[])requested.Clone(),
                [.. Enumerable.Range(0, requested.Length).Select(k => EmergeTerminalS.Sample(shifted[k], arrays.TV[k], arrays.W[k], sweep.Gamma[k], faces))]);
            var outcome = WavePortTerminalS.Convert(samples, ports, ModalRobinForm.SharedWavenumber, out string? terminalError);
            if (outcome is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(terminalError!));
            terminal = (samples, outcome, arrays);
            sweep = sweep with { FrequenciesHz = (double[])requested.Clone(), S = outcome.S.S,
                                 Z0 = [.. sweep.Z0.Select(_ => ports.Select(p => p.Z0).ToArray())] };
            modeZ = [.. samples.Samples.Select(x => wave.Select(w => new Complex(x.ZPv[ports.IndexOf(w)], 0)).ToArray())];
            log.Notes.AddRange(WavePortTerminalS.RouteNotes(ports, outcome, "EMerge"));
            var (checkNotes, checkWarnings) = WavePortTerminalS.Checks(ports, outcome, WavePortTerminalS.Lossless(problem), "EMerge",
                                                                       powerConvention: false, offFace: false);
            log.Notes.AddRange(checkNotes);
            log.Warnings.AddRange(checkWarnings);
            var (vNotes, vWarnings) = EmergeTerminalS.MeasuredChecks(ports, requested, raw, arrays);
            log.Notes.AddRange(vNotes);
            log.Warnings.AddRange(vWarnings);
            log.Notes.AddRange(WavePortReferences.ShiftNotes(wave));
            if (EmergeFields.Request(setup, problem) is not null) log.Notes.Add(EmergeTerminalS.FieldsNote(ports));
            SecondModeVerdicts(facts, problem, log);
        }
        else
        {
            sweep = EmergeResults.AtPortReferences(sweep with { FrequenciesHz = (double[])requested.Clone() }, ports, out _);
            if (lumpedMoved)
                log.Notes.Add("EMerge referred a port's S-parameters to another impedance than the port's own; the .sNp states one real " +
                              "reference per port, so the whole matrix was renormalised to each port's Z0.");
            if (wave.Count > 0)
            {
                log.Notes.Add(WavePortReferences.Note("EMerge", "EMerge's own mode solve, along the port's voltage path", wave, modeZ));
                log.Notes.AddRange(WavePortReferences.ShiftNotes(wave));
                SecondModeVerdicts(facts, problem, log);
            }
        }

        string? notConverged = null;
        // brief-emw-3 — a GPU sweep the driver moved was solved again on the CPU in the same run, without waiting for its cap
        if (GpuStallNote(facts.GpuStalled) is { } moved) log.Notes.Add(moved);
        if (facts.Adaptive)
        {
            log.Notes.Add($"EMerge's adaptive sweep solved the full problem at {Count(facts.Samples)} frequencies and fitted all " +
                          $"{requested.Length} requested points from them (estimated error " +
                          $"{(facts.Error is { } e ? PalaceStageTracker.Sci(e) : "unreported")}, tolerance {PalaceStageTracker.Sci(settings.Tolerance)} on |S|).");
            if (facts.Converged == false)
            {
                notConverged = $"EMerge's adaptive sweep stopped at its limit of {settings.MaxSamples} frequencies with estimated error " +
                               $"{(facts.Error is { } e2 ? PalaceStageTracker.Sci(e2) : "unreported")}, above its tolerance " +
                               $"{PalaceStageTracker.Sci(settings.Tolerance)}.";
                log.Warnings.Add(notConverged + " Raise EMerge.MaxSamples, or loosen EMerge.Tolerance." +
                                 // brief-emi-9 — only a forced GPU run gets here on the GPU: an Auto one was solved again on the CPU.
                                 (facts.Device == EmergeDevice.Gpu
                                     ? " It factorised on the GPU because Device is GPU; with Device Auto, circuitRF solves such a sweep again on the CPU."
                                     : ""));
            }
        }

        var data = EmergeResults.BuildDataSet(sweep, ports, facts);
        if (terminal is { } t)
        {
            WavePortTerminalS.AddDiagnostics(data, EmergeResults.Group, t.Samples, t.Outcome, ports);
            data.AddToGroup(EmergeResults.Group, EmergeTerminalS.ModalGpCube, EmergeTerminalS.GramCube(sweep.FrequenciesHz, ports, t.Arrays.Gp));
        }
        if (wave.Count > 0)
        {
            var values = new Complex[modeZ.Length * wave.Count];
            for (int k = 0; k < modeZ.Length; k++)
                for (int i = 0; i < wave.Count; i++) values[k * wave.Count + i] = modeZ[k][i];
            data.AddToGroup(EmergeResults.Group, WaveModeImpedanceCube, new RfCore.Data.DataCube(
                [new RfCore.Data.Axis("f", sweep.FrequenciesHz, "Hz"), new RfCore.Data.Axis("Port", [.. wave.Select(p => (double)p.Number)], "")],
                values) { Unit = "Ohm" });
        }
        // brief-emi-7 — the saved fields' collections, and the radiation pattern at their frequencies
        var fields = EmergeFields.ReadSteps(dir);
        WriteFieldCollections(dir, fields, null, log);
        if (setup.RadiationPattern && File.Exists(Path.Combine(dir, EmergeFields.FarFieldFile)) &&
            EmergeFields.PublishPattern(dir, fields, ports, setup, data, log.Notes) is { } patternError)
            log.Warnings.Add($"No radiation pattern from EMerge: {patternError}");
        // brief-emdc-2 R-emdc2-3 — the run's 0 Hz row, before either file is written
        var dcLines = SpliceDc(data, problem, dc);
        var published = PublishDriven(run, Me, problem, data, version, facts.Device?.ToString().ToLowerInvariant(),
                                      new EmResultMesh(facts.Tetrahedra, "tetrahedra", facts.Unknowns),
                                      (stagedBase, stamp) => EmergeResults.WriteSnp(data, stagedBase, setup, problem, lowering, settings, facts, version,
                                                                                    wall.Elapsed, peak, DateTimeOffset.Now, dcLines, stamp));

        // R-emi5-4c — the one line a user reads first of what the run cost, the last thing the run says.
        log.Notes.Add(EmergeCompletionSummary(version, tier, facts, peak, setup, wall.Elapsed, eigenmode: false, EmergeFields.Bytes(dir)));
        return new Leg(Me, EmRunStatus.Ok, null, data, published.Npy, published.Snp, "EMerge " + version, EmergeComparisonFacts(problem, settings, facts),
                       NotConverged: notConverged, RecordPath: published.Record);
    }

    /// <summary>
    /// brief-emw-2 R-emw2-3a — what the driver's second-mode check found, as Palace's run says it: a face that keeps a second mode
    /// at the sweep's top is a warning, a check that could not be made a note, and a clean one a note.
    /// </summary>
    private static void SecondModeVerdicts(EmergeRunFacts facts, Em3dProblem problem, RunLog log)
    {
        string top = Fmt(problem.Frequency.StopHz / 1e9);
        if (facts.SecondModes.Count == 0)
        {
            log.Notes.Add("Whether a wave port's second mode propagates was not checked: EMerge's run did not report it.");
            return;
        }
        foreach (var m in facts.SecondModes.Where(m => m.Error is not null))
            log.Notes.Add($"Whether port {m.Port}'s second mode propagates was not checked: {m.Error}.");
        string? Group(EmergeSecondMode m) => problem.Ports.FirstOrDefault(p => p.Number == m.Port)?.FaceGroupLabel;
        foreach (var m in facts.SecondModes.Where(m => m.Propagating))
            log.Warnings.Add(Group(m) is { } label
                ? $"Port '{label}''s face supports a propagating mode past its {m.Own} terminals' own at {Fmt(m.FrequencyHz / 1e9)} GHz " +
                  $"(EMerge's β = {Fmt(m.Second!.Value.Real)} rad/m, the terminals' first mode's {Fmt(m.Betas[0].Real)}), so its S-parameters " +
                  "are those of its terminals' modes alone and power the extra mode carries is not in them. Make the port region smaller " +
                  "(the Ports3D Width and Height), or lower the sweep's top."
                : $"Port {m.Port}'s wave-port face supports a SECOND propagating mode at {Fmt(m.FrequencyHz / 1e9)} GHz " +
                  $"(EMerge's β = {Fmt(m.Second!.Value.Real)} rad/m, the first mode's {Fmt(m.Betas[0].Real)}), so its S-parameters are " +
                  "those of the first mode alone and power the second carries is not in them. Make the port region smaller " +
                  "(the Ports3D Width and Height), or lower the sweep's top.");
        var clean = facts.SecondModes.Where(m => m.Error is null && !m.Propagating).ToList();
        if (clean.Count > 0)
            log.Notes.Add($"No second mode propagates at {top} GHz, the top of the sweep, on the face of " +
                          $"{(clean.Count == 1 ? "port " : "ports ")}{string.Join(", ", clean.Select(m => Group(m) is { } l ? $"'{l}'" : m.Port.ToString(CultureInfo.InvariantCulture)))} " +
                          (clean.Any(m => m.Own > 1)
                              ? "(EMerge, asked for each face's modes and more, kept none past a face's own that propagates: it drops a mode that carries no power)."
                              : "(EMerge, asked for two modes, kept no second one that propagates: it drops a mode that carries no power)."));
    }

    /// <summary>
    /// brief-emi-6 R-emi6-4a — what a driven EMerge run makes expected of a difference from another solver: its lumped-port model
    /// (when the problem has a lumped port), the conductors it gives a surface impedance and does not solve inside, and the error
    /// its adaptive sweep reached.
    /// </summary>
    internal static Em3dComparisonFacts EmergeComparisonFacts(Em3dProblem problem, EmergeSettings settings, EmergeRunFacts facts)
    {
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        return Em3dComparisonFacts.None with
        {
            UniformSheetPorts = problem.Ports.Any(p => p.Kind == Em3dPortKind.Lumped),
            SurfaceImpedanceSolids = [.. problem.Solids.Where(s => s.Role == Em3dRole.Conductor && materials.TryGetValue(s.Material, out var m)
                                                                  && !EmergeLoweringRules.IsPerfect(m))
                                                       .Select(s => s.Name)],
            AdaptiveFit = facts.Adaptive && facts.Samples is { } n && facts.Error is { } e ? (n, e, settings.Tolerance) : null,
            AdaptiveUnconverged = facts.Adaptive && facts.Converged == false,
        };
    }

    /// <summary>R-emi5-4b — the modes, as Palace's eigenmode run reports them (f, Q), with what EMerge does not report left out.</summary>
    private static Leg FinishEmergeEigen(Em3dProblem problem, EmSetup setup, string npyTarget, string dir, EmergeRunFacts facts,
                                         EmergeSpeedTier tier, string version, long? peak, TimeSpan wall, RunLog log)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        var read = EmergeResults.ReadModes(dir, out string? error);
        if (read is not { } arrays) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        var modes = EmergeResults.FilterModes(arrays.Frequencies, arrays.Q, problem.EigenmodeTargetHz);
        if (modes.Kept.Count < problem.EigenmodeCount)
            log.Warnings.Add($"EMerge found {modes.Kept.Count} mode(s) at or above {Fmt(modes.FloorHz / 1e9)} GHz where " +
                             $"{problem.EigenmodeCount} were asked for.");

        // brief-emi-7 R-emi7-3 — participation and each lumped port's external Q, from the driver's integrals over each mode
        var fields = EmergeFields.ReadSteps(dir);
        WriteFieldCollections(dir, fields, modes.Sources, log);
        var extras = EmergeEigenExtras(modes, fields);
        var notes = new List<string>
        {
            "f is each mode's resonant frequency (the real part of EMerge's complex eigenfrequency). Q is EMerge's Q, Re f / 2 Im f: " +
            "the LOADED Q, with every loss in the problem, each lumped port's resistance included.",
        };
        if (extras.QExt is not null)
            notes.Add("Q_ext is each lumped port's external Q, ω·R·∫ε|E|² / |V|²: the mode's stored energy over the power the port's " +
                      "resistance takes at the mode's own voltage across it, computed by circuitRF from EMerge's mode field. " +
                      "Q_unloaded = 1/(1/Q − Σ 1/Q_ext) is the Q with the ports' loading taken out.");
        if (extras.Participation is not null)
            notes.Add("Participation is the fraction of each mode's electric energy in each material (∫Re(ε)|E|² over that material's " +
                      "cells over the whole mesh's), computed by circuitRF from EMerge's mode field: where the mode lives.");
        if (extras.QExt is null && extras.Participation is null)
            notes.Add("EMerge's run reported no mode field, so this result has no Q_unloaded and no participation.");
        if (modes.Dropped > 0)
            notes.Add($"{modes.Dropped} of EMerge's {arrays.Frequencies.Length} solution(s) were below {Fmt(modes.FloorHz / 1e9)} GHz " +
                      $"({EmergeResults.ModeFloorMargin * 100:0} % under the search start, {Fmt(problem.EigenmodeTargetHz / 1e9)} GHz) " +
                      "and were dropped: EMerge's eigensolver looks either side of the start, where Palace reports only modes above " +
                      "it, and near 0 Hz it can return solutions that are not resonances.");
        log.Notes.AddRange(notes);

        var data = Em3dEigenResult.Build(modes.Kept, extras.Ports, extras.QExt, extras.Domains, extras.Participation, notes);
        void Scalar(string name, double? v)
        {
            if (v is { } x) data.AddToGroup(EmergeResults.Group, name, RfCore.Data.DataCube.Scalar(x));
        }
        Scalar("Tetrahedra", facts.Tetrahedra);
        Scalar("DegreesOfFreedom", facts.Unknowns);
        log.Notes.Add($"EMerge found {modes.Kept.Count} mode(s) at or above {Fmt(modes.FloorHz / 1e9)} GHz on {Count(facts.Tetrahedra)} " +
                      $"tetrahedra, {Count(facts.Unknowns)} unknowns.");

        string? npyPath = WriteNpy(npyTarget, data, log);
        log.Notes.Add(EmergeCompletionSummary(version, tier, facts, peak, setup, wall, eigenmode: true, EmergeFields.Bytes(dir)));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "EMerge " + version);
    }

    /// <summary>brief-hzn-12 R-hzn12-4 — a scattering run's radar cross-section, and what EMerge built of the wave.</summary>
    private static Leg FinishEmergeScattered(Em3dProblem problem, EmSetup setup, string npyTarget, string dir, EmergeRunFacts facts,
                                             EmergeSpeedTier tier, string version, long? peak, TimeSpan wall, RunLog log)
    {
        const Em3dSolverId Me = EmergeSolver.Id;
        var wave = problem.PlaneWave!;
        var requested = FrequenciesHz(problem.Frequency);
        var data = EmergeScattering.Read(dir, requested, wave, out string? error);
        if (data is null) return Leg.Failed(Me, EmRunStatus.EngineError, EmDiagnostics.SolveFailed(error!));
        log.Notes.AddRange(EmergeScattering.Notes(problem, EmergeScattering.ReadBack(dir)));
        void Scalar(string name, double? v)
        {
            if (v is { } x) data.AddToGroup(EmergeResults.Group, name, RfCore.Data.DataCube.Scalar(x));
        }
        Scalar("Tetrahedra", facts.Tetrahedra);
        Scalar("DegreesOfFreedom", facts.Unknowns);
        log.Notes.Add(EmergeScattering.Headline(data));
        string? npyPath = WriteNpy(npyTarget, data, log);
        log.Notes.Add(EmergeCompletionSummary(version, tier, facts, peak, setup, wall, eigenmode: false));
        return new Leg(Me, EmRunStatus.Ok, null, data, npyPath, null, "EMerge " + version);
    }

    /// <summary>brief-emi-7 R-emi7-3 — the reported modes' participation [mode, material] and lumped ports' external Q
    /// [mode, port], each null when the driver reported nothing to compute it from.</summary>
    internal static (IReadOnlyList<int> Ports, double[,]? QExt, IReadOnlyList<string> Domains, double[,]? Participation)
        EmergeEigenExtras(EmergeModes modes, EmergeFieldFacts fields)
    {
        var byMode = fields.Steps.Where(s => s.Mode is not null).ToDictionary(s => s.Mode!.Value);
        var steps = modes.Sources.Select(k => byMode.GetValueOrDefault(k)).ToList();
        if (steps.Count == 0 || steps.Any(s => s?.Energy is null)) return ([], null, [], null);
        var energies = steps.Select(s => s!.Energy!).ToList();
        var columns = EmergeFields.EnergyColumns(energies, fields.MaterialNames.Count);
        double[,]? participation = columns.Length > 0 ? EmergeFields.Participation(energies, columns) : null;
        string[] domains = [.. columns.Select(c => fields.MaterialNames[c])];

        double[,]? qExt = null;
        var ports = fields.PortNumbers;
        if (ports.Count > 0 && fields.PortR.Count == ports.Count && steps.All(s => s!.PortV?.Count == ports.Count))
        {
            qExt = new double[steps.Count, ports.Count];
            for (int i = 0; i < steps.Count; i++)
                for (int k = 0; k < ports.Count; k++)
                    qExt[i, k] = EmergeFields.ExternalQ(modes.Kept[i].FrequencyHz, energies[i].Sum(), steps[i]!.PortV![k], fields.PortR[k]);
        }
        return (qExt is null ? [] : ports, qExt, domains, participation);
    }

    /// <summary>
    /// R-emi5-4c — "EMerge done in 6 s · EMerge 3.0.0b2 · fast sparse solver · 15,124 tetrahedra · 109,566 unknowns · adaptive
    /// sweep built from 11 full solves · peak memory 2.6 GB · preset Standard.", every figure one the run read, and one it did not
    /// read left out rather than guessed.
    /// </summary>
    public static string EmergeCompletionSummary(string version, EmergeSpeedTier tier, EmergeRunFacts facts, long? peakBytes,
                                                 EmSetup setup, TimeSpan wall, bool eigenmode, long? fieldBytes = null)
    {
        var parts = new List<string> { $"EMerge done in {Duration(wall)}", $"EMerge {version}" };
        if (eigenmode) parts.Add("eigenmode solve on one core");
        // brief-emi-9 — the device the run USED, as its run.json reports it
        else if (facts.Device == EmergeDevice.Gpu) parts.Add("GPU sparse solver");
        else if (tier == EmergeSpeedTier.Fast) parts.Add("fast sparse solver");
        else if (tier == EmergeSpeedTier.Baseline) parts.Add("baseline sparse solver, one core");
        if (facts.Tetrahedra is { } t) parts.Add($"{t.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra");
        if (facts.Unknowns is { } u) parts.Add($"{u.ToString("N0", CultureInfo.InvariantCulture)} unknowns");
        if (!eigenmode && facts.Samples is { } n)
            parts.Add(facts.Adaptive ? $"adaptive sweep built from {n} full solve{(n == 1 ? "" : "s")}" : $"{n} frequencies solved");
        if (peakBytes is { } m) parts.Add($"peak memory {MachineMemory.Format(m)}");
        parts.Add($"preset {setup.Emerge?.Quality ?? PalaceQuality.Standard}");
        // brief-emi-7 — what the saved fields cost on disk, as Palace's line reports it (R-em3d29-1d)
        if (fieldBytes is > 0 and var f) parts.Add($"field files {MachineMemory.Format(f)}");
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>brief-emi-7 — the .pvd collections of the steps the driver wrote; a failure costs the plots, not the result.</summary>
    private static void WriteFieldCollections(string dir, EmergeFieldFacts fields, IReadOnlyList<int>? modeSources, RunLog log)
    {
        try { EmergeFields.WriteCollections(dir, fields, modeSources); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warnings.Add($"EMerge's field files could not be indexed in '{dir}' ({e.Message}); the result is unaffected, but no field plot can read them.");
        }
    }
}
