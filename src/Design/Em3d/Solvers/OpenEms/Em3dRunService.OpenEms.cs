// brief-emi-1 — openEMS's part of Em3dRunService: its leg of a run (settings, the SAR group, the grid and lowering, one run
// per port) and everything only an openEMS run does. Moved here unchanged from Em3dRunService.cs; the descriptor that names
// it is OpenEmsSolver.cs beside it.
//
// openEMS's writers and readers (CsxcadWriter, OpenEmsRun, OpenEmsFarField, OpenEmsSar* …) predate the solver folders and
// stay in src/Design/Em3d/.

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
    /// <summary>
    /// brief-em3d-116 R-em3d116-3 — what openEMS's lowering would refuse of <paramref name="problem"/>'s wave ports (a hollow
    /// waveguide; a current loop with no room), through the planner and the placement the run's writer uses; null when it
    /// builds them all. <c>check</c> reports it, so a setup that checks clean is not refused by its run for this.
    /// </summary>
    public static string? OpenEmsWavePortRefusal(Em3dProblem problem, OpenEmsGridSettings gridSettings)
    {
        if (!problem.HasWavePorts || gridSettings.Problems().Count > 0) return null;
        var grid = FdtdGrid.Build(problem, gridSettings, long.MaxValue);
        return grid.WavePorts.Refusal ?? FdtdWavePorts.Place(problem, grid.WavePorts, grid).Refusal;
    }

    /// <summary>brief-emi-1 — openEMS's leg of one run: openEMS found, its grid and run sections resolved (with the SAR plan
    /// when the setup asks for one), the grid placed and the model written, then one run per port.</summary>
    internal sealed class OpenEmsLeg : SolverLeg
    {
        private OpenEmsGridSettings? _grid;
        private OpenEmsRunSettings? _run;
        private OpenEmsPlan? _plan;

        public OpenEmsLeg() : base(OpenEmsSolver.Descriptor) { }

        public override void Configure(RunContext run)
        {
            var (readiness, log, setup) = (run.Readiness, run.Log, run.Setup);
            var o = readiness.Single(r => r.Tool == SolverTool.OpenEms).Installation!;
            log.Notes.Add($"Solver: openEMS {o.DescribeVersion()} at {o.Path}.");
            // brief-sar-1 — an untested openEMS runs, and the run says so among its Warnings, as Gmsh's does.
            if (readiness.Single(r => r.Tool == SolverTool.OpenEms).Warning is { } untested) log.Warnings.Add(untested);
            // brief-em3d-136 §6 — the device is Palace's setting; openEMS is unaffected by it.
            if (run.Device == PalaceDevice.Gpu) log.Notes.Add("Device GPU applies to Palace; openEMS runs on the CPU.");
            _grid = CemOpenEms.ResolveGrid(setup.OpenEms);
            // brief-sar-6 R-sar6-1 — a Sar group turns CellConstantMaterial on unless the section says otherwise (SAR-3).
            _run  = CemOpenEms.ResolveRun(setup.OpenEms, setup.RequestsSar);
            if (_grid.Problems().Concat(_run.Problems()).ToList() is { Count: > 0 } bad)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-settings", string.Join(" ", bad)));
        }

        // brief-sar-6 R-sar6-1 — the Sar group, resolved against the problem: openEMS's run, a Cartesian grid.
        public override Stop? AdmitSar(Em3dProblem problem, Em3dGenerationResult generated, RunContext run)
        {
            var setup = run.Setup;
            var sarSettings = SarSettings.Resolve(setup.Sar!);
            // brief-sar-8b R-sar8b-4 — on a cylindrical grid, local SAR and B1 only: a cube is Cartesian, and the run says so.
            if (_grid!.Cylindrical is not null)
            {
                var (onCylinder, cylinderRefusal) = Em3dSar.OnCylindricalGrid(sarSettings, setup.Sar!);
                if (onCylinder is null) return new(EmRunStatus.Refused, EmDiagnostics.Forwarded(SarSource, cylinderRefusal));
                sarSettings = onCylinder;
                run.Log.Notes.Add(Em3dSar.CylindricalNote);
            }
            var (sarPlan, sarRefusal) = Em3dSar.Resolve(sarSettings, problem, generated.Phantoms);
            if (sarPlan is null) return new(EmRunStatus.Refused, EmDiagnostics.Forwarded(SarSource, sarRefusal));
            _run = _run! with { Sar = sarPlan };
            return null;
        }

        public override void Prepare(Em3dProblem problem, RunContext run)
        {
            // brief-em3d-74 R-em3d74-4b — openEMS meshes on its own grid: a mesh region is Gmsh's, and said to be ignored.
            foreach (var region in problem.MeshRegions)
                run.Log.Notes.Add($"openEMS ignores mesh region '{region.Name}': it sizes the Gmsh mesh Palace and a thermal run use, " +
                                  "and openEMS builds its own grid.");
            if (Stop is not null) return;
            _plan = PrepareOpenEms(problem, _grid!, _run!, run.Readiness, run.Control, run.Log, out var stop,
                                   FarFieldFrequencies(run.Setup, problem));
            Stop = stop;
            // brief-dfr27-5 R-dfr27-5-2 — how long it will take, before anything starts: asked about past an hour.
            if (_plan is { } plan &&
                run.Memory.AdmitTime(OpenEmsTimeEstimate.Verdict(plan.Grid, plan.Lowering.Ports.Count,
                                                                 OpenEmsTimeEstimate.Cores(run.MaxCores)), run.Log) is { } declined)
                Stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-time", declined));
        }

        public override Leg Execute(Em3dProblem problem, string snpBase, RunContext run)
            => ExecuteOpenEms(_plan!, problem, run.Setup, run.ResultsRoot, snpBase, run.Ct, run.Control, run.MaxCores, run.Log,
                              run.Limits(MachineMemory.PhysicalBytes), run, run.Dc);
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
                                               IReadOnlyList<SolverReadiness> readiness, RunControl? control, RunLog log, out Stop? stop,
                                               IReadOnlyList<double>? farFieldHz = null)
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
        log.Notes.AddRange(grid.Snaps.Select(m => m.Sentence));

        var lowering = CsxcadWriter.Write(problem, grid, gridSettings, runSettings, farFieldHz);
        if (!lowering.Ok) { stop = new(EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-lowering", lowering.Refusal)); return null; }
        log.Notes.AddRange(lowering.Notes);
        int n = lowering.Ports.Count;
        // brief-sar-6 R-sar6-2 — what the SAR dumps will take on disk, before anything is written.
        if (runSettings.Sar is { } sar)
        {
            var (diskNote, diskWarning) = Em3dSar.DiskNote(sar, grid, n);
            log.Notes.Add(diskNote);
            if (diskWarning is not null) log.Warnings.Add(diskWarning);
        }
        var s0 = grid.Smallest;
        if (grid.Cylinder is { } cyl)
            log.Notes.Add($"openEMS cylindrical grid about {cyl.Describe()}: {grid.X.Lines.Count:N0} ρ × {cyl.AzimuthCells:N0} α × " +
                          $"{grid.Z.Lines.Count:N0} {FdtdGrid.AxisName(cyl.Axis)} = {grid.Cells:N0} cells; ρ from {FdtdGrid.FormatLength(cyl.RhoMinM)} " +
                          $"to {FdtdGrid.FormatLength(cyl.RhoMaxM)}; α in {cyl.AzimuthCells} cells, set by {cyl.AzimuthSetBy}; smallest arc " +
                          $"{FdtdGrid.FormatLength(cyl.SmallestArcM)} at ρ = {FdtdGrid.FormatLength(cyl.SmallestArcAtM)}. openEMS runs once per port: " +
                          $"{n} run{(n == 1 ? "" : "s")}, each up to {lowering.MaxTimeSteps:N0} time steps.");
        else
            log.Notes.Add($"openEMS grid: {grid.X.Lines.Count:N0} × {grid.Y.Lines.Count:N0} × {grid.Z.Lines.Count:N0} = {grid.Cells:N0} " +
                          $"cells; smallest cell {FdtdGrid.FormatLength(s0.SmallestCellM)} on {FdtdGrid.AxisName(s0.Axis)}, set by " +
                          $"{string.Join("; ", s0.SmallestCellFeatures.Select(f => f.Describe(s0.Axis)))}. openEMS runs once per port: " +
                          $"{n} run{(n == 1 ? "" : "s")}, each up to {lowering.MaxTimeSteps:N0} time steps.");

        return new OpenEmsPlan(gridSettings, runSettings, grid, lowering,
                               readiness.Single(r => r.Tool == SolverTool.OpenEms).Installation!);
    }

    private static Leg ExecuteOpenEms(OpenEmsPlan plan, Em3dProblem problem, EmSetup setup, string resultsRoot, string snpBase,
                                      CancellationToken ct, RunControl? control, int? maxCores, RunLog log,
                                      ProcessLimits? limits, RunContext run, Em3dDcResult? dc = null)
    {
        const Em3dSolverId Me = OpenEmsSolver.Id;
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
        // brief-em3d-116 — a wave port's source sits behind its face, its feed's length farther from every other port.
        diagonal += grid.WavePorts.Feeds.Sum(f => f.ExtensionM);
        double slowest = problem.Materials.Select(m => Math.Sqrt(Math.Max(1, m.Epsr) * Math.Max(1, m.Mur))).DefaultIfEmpty(1).Max();
        double settleS = grid.ExcitationS + 2 * diagonal * slowest / 299_792_458.0;

        // ── One run per port (R-em3d9-3a) ─────────────────────────────────────────────────────
        int? threads = maxCores is { } c ? EmSolveCores.Sanitise(c) : null;
        var runs = new List<OpenEmsPortRun>();
        for (int k = 0; k < n; k++)
        {
            // brief-em3d-65 R-em3d65-3c — every kernel solid's file, checked immediately before openEMS is started.
            string portDir = Path.Combine(runDir, OpenEmsRun.PortDirectory(lowering.Ports[k]));
            if (OpenEmsRun.CheckKernelFiles(portDir, lowering, lowering.PortFiles[k]) is { } damaged)
                return Leg.Failed(Me, EmRunStatus.Refused, EmDiagnostics.Forwarded("openems-kernel-file", damaged));
            OpenEmsPortRun r;
            try
            {
                r = OpenEmsRun.RunPort(runDir, lowering, k, openEms.Path, threads, runSettings.EndCriterionDb,
                                       grid.ExcitationS, control, ct, settleS, limits, grid.Cells, grid.TimeStepEstimateS);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed($"openEMS could not be run in '{runDir}' ({e.Message}).");
            }
            if (r.Cancelled) return Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
            if (r.Stopped is { } stopped)
                return StoppedLeg(Me, "openEMS", stopped, limits ?? ProcessLimits.For(MachineMemory.PhysicalBytes, null), [],
                                  Path.Combine(portDir, OpenEmsRun.LogFile));
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
        // brief-em3d-116 R-em3d116-2e — each wave-port terminal's line, as it measured itself in its own run.
        log.Notes.AddRange(LineNotes(runs, lowering, freqs, data));

        // ── brief-em3d-31 R-em3d31-2 — the radiation pattern, from the surface each port's run dumped ─────
        var efficiencies = new List<(double Hz, int Port, double Efficiency)>();
        if (lowering.FarField is { } surface)
        {
            if (OpenEmsPattern(surface, result, lowering, runDir, setup, maxCores, control, ct, log.Notes, data, efficiencies) is { } patternError)
            {
                if (ct.IsCancellationRequested) return Leg.Failed(Me, EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
                return Failed(patternError);
            }
        }

        // ── brief-sar-6 — SAR and B1 from each port's raw dump, normalised to the power that port accepted ─────────────
        if (runSettings.Sar is { } sarPlan)
        {
            if (SarDrives(sarPlan, runs, lowering, ports) is not { } drives)
                return Failed("the ports' accepted power at the SAR frequencies could not be formed from their probes.");
            var sar = OpenEmsSarResults.Process(sarPlan, problem, runDir, drives, efficiencies.Count > 0 ? efficiencies : null, openEms,
                                                maxCores, control, ct, grid.Cylinder);
            if (sar.Error is { } sarError) return Failed(sarError);
            log.Notes.AddRange(sar.Notes);
            log.Warnings.AddRange(sar.Warnings);
            OpenEmsSarResults.AddTo(data, sar.Records, sarPlan.Settings);
        }

        // ── brief-sar-9 R-sar9-1 — every port's fields, staged with the rest of the result (brief-cosim-pattern-1 R-cp1-3): writing
        // them takes minutes, and a failure or a cancel here leaves the previous result's set whole ────────
        var paths = run.Paths(Me);
        using var staging = EmResultStaging.Begin(paths);
        PortFieldManifest? fieldManifest = null;
        if (runSettings.Sar is { Settings.PerPortFields: true } fieldPlan)
        {
            string folder = staging.StagedFields;
            string stamp = staging.Stamp;
            var (manifest, fieldError) = PortFieldSetWriter.Write(
                folder, stamp, Path.GetFileName(paths.SnpPath(ports.Count)), setup.Name ?? "", fieldPlan, problem, runDir,
                lowering.Ports, [.. ports.Select(p => p.Name)], [.. ports.Select(p => p.Z0)], [.. runs.Select(r => r.Probes!)],
                efficiencies.Count > 0 ? efficiencies : null, control, ct, grid.Cylinder);
            if (fieldError is not null) return Failed(fieldError);
            fieldManifest = manifest;
            log.Notes.Add($"Per-port fields kept in '{Path.GetFileName(folder)}': {manifest!.Ports.Count} port{(manifest.Ports.Count == 1 ? "" : "s")} at " +
                          $"{string.Join(", ", manifest.Frequencies.Select(f => MaterialDispersion.Hz(f.Hz)))}, " +
                          $"{MachineMemory.Format(manifest.TotalBytes)}; a schematic's fields analysis recombines them with no new EM run.");
        }

        // brief-emdc-2 R-emdc2-3 — the run's 0 Hz row, before either file is written
        var dcLines = SpliceDc(data, problem, dc);
        var published = PublishDriven(run, Me, problem, data, openEms.DescribeVersion(), "cpu", new EmResultMesh(grid.Cells, "cells", null),
                                      (stagedBase, stamp) => WriteOpenEmsSnp(data, stagedBase, setup, problem, grid, plan.GridSettings, runSettings, lowering,
                                                                             openEms, runs, dcLines, stamp),
                                      staging, fieldManifest);

        var comparisonFacts = new Em3dComparisonFacts(lowering.DielectricFitHz, lowering.PecSolids, lowering.SubCellWires,
                                                      [.. runs.Where(r => !r.Converged).Select(r => r.Port)]);
        // brief-em3d-98 — openEMS's own signal: each port's run converged or stopped at the step limit (OpenEmsPortRun.Converged).
        var open = runs.Where(r => !r.Converged).ToList();
        string? notConverged = open.Count == 0 ? null
            : $"openEMS stopped at its step limit {(open.Count == 1 ? $"exciting port {open[0].Port}" : $"exciting ports {string.Join(", ", open.Select(r => r.Port))}")}" +
              (open.Where(r => !double.IsNaN(r.DecayDb)).Select(r => r.DecayDb).DefaultIfEmpty(double.NaN).Max() is var worst && !double.IsNaN(worst)
                   ? $" at {Db(worst)} against {Db(runSettings.EndCriterionDb)}" : "") + ": not converged.";
        return new Leg(Me, EmRunStatus.Ok, null, data, published.Npy, published.Snp, "openEMS " + openEms.DescribeVersion(), comparisonFacts,
                       NotConverged: notConverged, RecordPath: published.Record);
    }

    /// <summary>brief-sar-6 — the source a SAR refusal is forwarded under.</summary>
    public const string SarSource = "em3d-sar";

    /// <summary>
    /// brief-sar-6 R-sar6-3 — each driven port's own U and I at each SAR frequency, from the probes S was formed from
    /// (FdtdPortTransform.Solve at those frequencies): what each dump is normalised by. Null when the transform cannot be formed.
    /// </summary>
    internal static List<SarPortDrive>? SarDrives(Em3dSarPlan plan, IReadOnlyList<OpenEmsPortRun> runs, CsxcadLowering lowering,
                                                  IReadOnlyList<Em3dPort> ports)
    {
        double[] hz = [.. plan.Settings.FrequenciesHz];
        var at = FdtdPortTransform.Solve([.. runs.Select(r => r.Probes!)], hz, lowering.Ports, [.. ports.Select(p => p.Z0)]);
        if (at.Error is not null) return null;
        var drives = new List<SarPortDrive>();
        for (int k = 0; k < lowering.Ports.Count; k++)
            for (int i = 0; i < hz.Length; i++)
                drives.Add(new SarPortDrive(lowering.Ports[k], hz[i], at.U[i][k, k], at.I[i][k, k]));
        return drives;
    }

    /// <summary>brief-em3d-116 — the cubes of each wave-port terminal's measured line in the openEMS group.</summary>
    public const string LineZCube = "LineZ", LineEpsEffCube = "LineEpsEff";

    /// <summary>
    /// brief-em3d-116 R-em3d116-2e — each wave-port terminal's characteristic impedance and effective permittivity, measured at
    /// its reference plane in the run that excited it, as notes ("Terminal P1: the line measured Z 50.4 Ω, ε_eff 2.20.", at
    /// the band's middle point) and as <see cref="LineZCube"/>/<see cref="LineEpsEffCube"/> over the sweep. A coupled
    /// terminal's are seen with the other terminals passive, which the note says; nothing compares them.
    /// </summary>
    internal static IReadOnlyList<string> LineNotes(IReadOnlyList<OpenEmsPortRun> runs, CsxcadLowering lowering, double[] freqs, DataSet data)
    {
        var notes = new List<string>();
        var measured = new List<(int Port, FdtdLineMeasure M)>();
        for (int k = 0; k < runs.Count; k++)
        {
            if (runs[k].Line is not { } l) continue;
            var m = FdtdLineProbes.Measure(l.Ua, l.U, l.Uc, l.Ia, l.Ib, l.SpacingM, freqs);
            measured.Add((runs[k].Port, m));
            int mid = freqs.Length / 2;
            var names = lowering.Probes.FirstOrDefault(p => p.Port == runs[k].Port);
            string label = names?.Terminal ?? $"P{runs[k].Port}";
            notes.Add($"Terminal {label}: the line measured Z {Fmt(m.Z[mid].Real)} Ω, ε_eff {m.EpsEff[mid].ToString("F2", CultureInfo.InvariantCulture)}" +
                      (freqs.Length > 1 ? $" at {Fmt(freqs[mid] / 1e9)} GHz" : "") +
                      (names?.Group is { } g ? $", with the other terminals of '{g}' passive" : "") + ".");
        }
        if (measured.Count > 0)
        {
            var portAxis = new Axis("Port", [.. measured.Select(m => (double)m.Port)], "");
            var f = new Axis("f", freqs, "Hz");
            var z = new System.Numerics.Complex[freqs.Length * measured.Count];
            var e = new double[freqs.Length * measured.Count];
            for (int i = 0; i < freqs.Length; i++)
                for (int j = 0; j < measured.Count; j++)
                {
                    z[i * measured.Count + j] = measured[j].M.Z[i];
                    e[i * measured.Count + j] = measured[j].M.EpsEff[i];
                }
            data.AddToGroup(OpenEmsGroup, LineZCube, new DataCube([f, portAxis], z) { Unit = "Ohm" });
            data.AddToGroup(OpenEmsGroup, LineEpsEffCube, new DataCube([f, portAxis], e));
        }
        return notes;
    }

    /// <summary>
    /// brief-em3d-31 — the openEMS pattern: for each port's run and each frequency, the surface's dumps scaled
    /// into the port DFT's convention and divided by the driven port's own voltage (OpenEmsFarField's header),
    /// transformed (NearToFarField) and handed to the stage with ½·Re(V·I*) at that port and its published S_kk.
    /// Null on success; otherwise the error the leg fails with.
    /// </summary>
    internal static string? OpenEmsPattern(Nf2ffSurface surface, FdtdPortResult result, CsxcadLowering lowering, string runDir,
                                          EmSetup setup, int? maxCores, RunControl? control, CancellationToken ct, ICollection<string> notes, DataSet data,
                                          List<(double Hz, int Port, double Efficiency)>? efficiencies = null)
    {
        var grid = surface.Hemisphere ? PlanarFarFieldGrid.Hemisphere() : FarFieldStage.Sphere();
        var freqs = result.FrequenciesHz;
        int n = lowering.Ports.Count;
        control?.BeginStage("far field (openEMS surface transform)", freqs.Length * n, "patterns");
        var points = new Em3dPatternPoint[freqs.Length * n];
        long freed = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < n; k++)
        {
            string dir = Path.Combine(runDir, OpenEmsRun.PortDirectory(lowering.Ports[k]));
            for (int i = 0; i < freqs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                Complex u = result.U[i][k, k], c = result.I[i][k, k];
                if (!(u.Magnitude > 0))
                    return $"Port {lowering.Ports[k]}'s own voltage is zero at {Fmt(freqs[i] / 1e9)} GHz in the run that excited it, so " +
                           "its radiation pattern has nothing to be referred to.";
                var samples = OpenEmsFarField.ReadSamples(dir, surface, freqs[i], 0.5 / u, out string? readError);
                if (samples is null) return readError;
                var pattern = NearToFarField.Transform(samples, freqs[i], grid, lowering.Ports[k], surface.Floor, surface.FloorZ,
                                                       maxCores, ct);
                double accepted = 0.5 * (c / u).Real;       // ½·Re(V·I*) at |V| = 1 V
                points[i * n + k] = new Em3dPatternPoint(pattern, accepted, result.S[i][k, k]);
                // brief-sar-6 R-sar6-6 — the power budget's radiated share, at each sweep point
                efficiencies?.Add((freqs[i], lowering.Ports[k], accepted > 0 ? pattern.RadiatedPowerW / accepted : double.NaN));
                control?.TickStage();
            }
            freed += OpenEmsFarField.DeletePhaseSnapshots(dir);
        }
        notes.AddRange(Em3dRadiation.Publish(data, "openEMS (FDTD)", grid, lowering.Ports, freqs, points,
                                             setup.ReferenceInputPowerDbm, surface.Hemisphere));
        notes.Add($"The radiation pattern's surface transform took {watch.Elapsed.TotalSeconds:F1} s for {points.Length} pattern(s). " +
                      $"openEMS's 21 fixed-phase snapshots of each surface dump were removed afterwards ({freed / 1e6:F1} MB); the " +
                      "magnitude and phase files that carry the field exactly are kept in each port's run directory.");
        return null;
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
        Em3dPortMap.AddTo(ds, OpenEmsGroup, ports);
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
                                           SolverInstallation openEms, IReadOnlyList<OpenEmsPortRun> runs,
                                           IReadOnlyList<string>? dcLines = null, string? stamp = null)
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
        // Which numbered port is which named port, first and in the spelling other tools read (Port[N] = name).
        var lines = new List<string>(Em3dPortMap.NameLines(problem.Ports));
        foreach (var p in problem.Ports.OrderBy(p => p.Number))
            lines.Add($"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{Ascii(p.Name)}' from '{Ascii(p.NegativeObject)}' " +
                      $"to '{Ascii(p.PositiveObject)}', " +
                      // brief-em3d-116 — a wave port's terminal: a fed line, measured by probes and referred to Z0 directly
                      (p.Kind == Em3dPortKind.Wave ? "wave (fed line, voltage and current probes at the reference plane)" : "lumped") +
                      $", {R(p.Z0.Real)} Ohm");
        lines.AddRange(Em3dPortMap.Lines(problem.Ports));     // brief-em3d-93 — which document port each is, when one was off
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
        lines.AddRange(dcLines ?? []);                                // brief-emdc-2 R-emdc2-4

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
                DateTimeOffset.Now, stamp));

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
}
