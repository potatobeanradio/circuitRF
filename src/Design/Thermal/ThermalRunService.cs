// brief-em3d-74 R-em3d74-5 — a thermal setup's run: THE function the GUI's Simulate and `circuitrf em` both reach (through
// EmRunService.RunThreeDView — CLAUDE.md: an operation that lives in a view model is not a capability).
//
//   1. resolve the document and the setup, and refuse on exactly what `check` reports (C3dThermal's one validator);
//   2. find Gmsh — the refusal a Palace run gets, with the same install offer;
//   3. lower and mesh ONCE: a sweep variable never touches geometry (brief 73 §5b refused that already);
//   4. per sweep point: resolve the values, build sources and boundaries, solve — the previous point's field seeds Newton —
//      and read the probes and measures;
//   5. write the result: the DataSet as <results>/<key>.thermal.npy, and the run directory <results>/<key>.thermal/ KEPT
//      with the .geo, the .msh and postpro/paraview/thermal/thermal.pvd, one step per sweep point, the layout Palace writes.
//
// A cancelled run writes no .npy. The energy balance of every point is in the notes; a point that does not close it is a
// warning, since it means the solve is wrong, not the design.

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Results;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;
using CircuitRF.Thermal.Electrothermal;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Thermal;

public static partial class ThermalRunService
{
    /// <summary>The token a thermal result's name carries, as <c>palace</c> and <c>openems</c> do.</summary>
    public const string SolverToken = "thermal";

    /// <summary>The DataSet's group of probe, energy and limit cubes.</summary>
    public const string Group = "thermal";

    /// <summary>brief-em3d-75 R-em3d75-5 — the group a wire's T(s) is carried in (brief 77 writes it): one cube per wire element
    /// (<c>w1[3]</c>), over the sweep's axes and then arc length <c>s</c> in metres, °C. The viewer colours each wire by it.</summary>
    public const string WireGroup = "wires";

    /// <summary>The group the run's notes are carried in, as labels.</summary>
    public const string NotesGroup = "Notes";

    /// <summary>Samples along a line probe.</summary>
    public const int LineSamples = 101;

    /// <summary>R-em3d74-5e — every element size multiplied by this for the mesh-convergence check.</summary>
    public const double CheckScale = 0.7;

    /// <summary>A thermal result's stem: the planar stem and the token.</summary>
    public static string ResultKey(EmSetup setup) => EmRunService.ResolveResultKey(setup) + "." + SolverToken;

    /// <summary>The run directory, kept after the run.</summary>
    public static string RunDirectory(string resultsRoot, EmSetup setup) => Path.Combine(resultsRoot, ResultKey(setup));

    /// <summary>Where the DataSet lands with no override: <c>&lt;results&gt;/&lt;key&gt;.thermal.npy</c>.</summary>
    public static string NpyPath(string resultsRoot, EmSetup setup) => Path.Combine(resultsRoot, ResultKey(setup) + ".npy");

    /// <summary>Runs thermal setup <paramref name="setup"/> (as <see cref="C3dSetups.ForRun"/> names it) over
    /// <paramref name="document"/>. <c>setup.SnpOutputPathOverride</c>, when set, is where the DataSet goes (<c>-o</c>).</summary>
    /// <param name="circuitSets">brief-em3d-79 — <c>--set var=expr</c> for a setup driven from a circuit: applied to the
    /// circuit's globals before it is elaborated, as a run verb applies it.</param>
    public static EmRunResult Run(EmSetup setup, C3dDocument document, string documentPath, string? workspaceCws, string resultsRoot,
                                  CancellationToken ct = default, RunControl? control = null,
                                  IReadOnlyList<(string Name, string Expr)>? circuitSets = null)
    {
        // brief-em3d-98 R-em3d98-2 — the run directory's status.json: `running` once the run is about to mesh into it (a refusal
        // before then leaves the previous result and its record untouched), how it ended on every path after that.
        var record = new LegRecord();
        EmRunResult result;
        try { result = RunCore(setup, document, documentPath, workspaceCws, resultsRoot, ct, control, circuitSets, record); }
        catch (Exception e) when (record.Directory is not null)
        {
            C3dRunStatus.Finish(record.Directory, record.Started, e is OperationCanceledException ? C3dRunState.Cancelled : C3dRunState.Failed,
                                e is OperationCanceledException ? null : e.Message);
            throw;
        }
        if (record.Directory is { } dir)
        {
            var state = result.Status switch
            {
                EmRunStatus.Ok        => record.NotConverged is null ? C3dRunState.Complete : C3dRunState.NotConverged,
                EmRunStatus.Cancelled => C3dRunState.Cancelled,
                _                     => C3dRunState.Failed,
            };
            C3dRunStatus.Finish(dir, record.Started, state, state switch
            {
                C3dRunState.NotConverged => record.NotConverged,
                C3dRunState.Failed       => result.Error is { } why ? why.ReplaceLineEndings(" ") : null,
                _                        => null,
            });
        }
        return result;
    }

    /// <summary>brief-em3d-98 — where a run's status goes once it has begun using its directory, and whether it converged.</summary>
    private sealed class LegRecord
    {
        public string? Directory;
        public DateTime Started;
        public string? NotConverged;
    }

    private static EmRunResult RunCore(EmSetup setup, C3dDocument document, string documentPath, string? workspaceCws, string resultsRoot,
                                       CancellationToken ct, RunControl? control, IReadOnlyList<(string Name, string Expr)>? circuitSets,
                                       LegRecord record)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(document);
        if (control is { Token: var tk } && tk.CanBeCanceled) ct = tk;
        var notes = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        EmRunResult Stop(EmRunStatus status, Diagnostic d)
            => new(status, null, null, null, null, null, d.Render(), warnings, KernelName: "circuitRF thermal", Notes: notes,
                   Errors: errors, Diagnostic: d);
        EmRunResult Refuse(string text) => Stop(EmRunStatus.Refused, EmDiagnostics.Forwarded("thermal", text));

        try
        {
            var t = setup.Thermal ?? new CemThermal();
            string path = Path.GetFullPath(documentPath);

            // ── 1. the document and the setup: refused on check's own findings ──
            control?.BeginStage("resolving the thermal setup");
            var drawn = new C3dElaborator().Elaborate(document, path, workspaceCws, new C3dElaborationOptions(null,
                setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC));
            warnings.AddRange(drawn.Warnings);
            if (!drawn.Ok) { notes.AddRange(drawn.Notes); return Refuse(string.Join(" ", drawn.Refusals)); }
            // brief-em3d-93 — the places and the setup checked against what is DRAWN, so a reference to what is not modelled is
            // refused naming it; then the run solves the filtered elaboration, and nothing downstream filters again.
            var findings = C3dThermal.Places(document).Concat(C3dThermal.Places(document, drawn))
                                     .Concat(C3dThermal.Setup(setup.Name, setup, document, drawn, drawn.Resolution!))
                                     .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Render()).ToList();
            var e = C3dModelled.Filter(document, drawn);
            notes.AddRange(e.Notes);
            if (!e.Ok) return Refuse(string.Join(" ", e.Refusals));
            if (findings.Count > 0) return Refuse(string.Join(" ", findings));
            if (e.Technology is null) return Refuse("This 3D view's technology did not resolve, so no material states a thermal conductivity.");
            // brief-em3d-87 R-em3d87-2 — what this result is solved from, hashed now; kept beside it when the run succeeds.
            var inputs = C3dRunInputs.Take(document, path, e.FilesRead);

            // ── 2. Gmsh ──
            var gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh));
            if (!gmsh.Proceeds)
                return Stop(EmRunStatus.Refused, EmDiagnostics.SolverUnavailable(gmsh.Name, gmsh.Refusal!, Em3d.Install.SolverInstaller.OfferFor(gmsh)));
            notes.Add($"Solver: circuitRF thermal (steady conduction, second-order tetrahedra by default); mesher: Gmsh " +
                      $"{gmsh.Installation!.DescribeVersion()} at {gmsh.Installation.Path}.");
            // brief-em3d-135 R-em3d135-1e — an untested Gmsh runs, and the run says so among its Warnings.
            if (gmsh.Warning is { } untested) warnings.Add(untested);

            // ── brief-em3d-79: a setup driven from a circuit runs the circuit's HB first; its pins' currents become the ports' ──
            ThermalCircuitDrive? circuit = null;
            var link0 = (t.Currents ?? []).FirstOrDefault(c => c.FromCircuit is not null)?.FromCircuit;
            if (link0 is null && circuitSets is { Count: > 0 })
                warnings.Add($"--set {string.Join(", ", circuitSets.Select(x => x.Name))} was not applied: it sets a global of the circuit a " +
                             "thermal setup takes its currents from, and this setup takes none from a circuit.");
            if (link0 is { } link)
            {
                control?.BeginStage("running the circuit's harmonic balance");
                circuit = ThermalCircuitLink.Run(link, document, path, resultsRoot, circuitSets, control, out string? circuitWhy);
                if (circuit is null) return Refuse(circuitWhy!);
                inputs = inputs.With(C3dRunInputs.Take(document, path, circuit.FilesRead));
                if (!circuit.Converged.Any(c => c)) return Refuse($"The circuit's HB '{circuit.Analysis}' converged at no point, so no point has currents to solve.");
                notes.AddRange(circuit.Notes);
                notes.Add(CircuitNote(circuit));
                t = circuit.Setup(t);
                if (t.Currents is null)
                    warnings.Add($"No pin of '{circuit.Instance}' carries a current at any converged point of the circuit's HB '{circuit.Analysis}': " +
                                 "the run heats with the setup's own sources only.");
            }

            // ── 3. lower and mesh, once ──
            var lowering = ThermalLowerings.Build(document, e, t, 1, out string? why);
            if (lowering is null) return Refuse(why!);
            notes.AddRange(lowering.Notes);
            string runDir = RunDirectory(resultsRoot, setup);
            // brief-dfr36-1 — no status.json for a mesh: it is not a run's result, and beginning one drops the record of the result here.
            bool meshOnly = control?.MeshOnly == true;
            if (!meshOnly)
            {
                record.Started = C3dRunStatus.Begin(runDir);
                record.Directory = runDir;
            }
            var meshed = Mesh(runDir, lowering, gmsh.Installation.Path, control, ct, notes, out var mesh, out string? meshError);
            if (meshed.Cancelled) return Stop(EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
            if (meshed.Refused) return Refuse(meshed.Message!);
            if (mesh is null) return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed(meshError ?? meshed.Message ?? "the mesh could not be read."));
            // brief-dfr36-1 R-dfr36-1-2 — stopped here, after the mesh is read, with what a solve of it would need.
            if (meshOnly)
            {
                var sizing = new ThermalSolveOptions { Solver = SolverOf(t) };
                var size = ThermalSizeEstimate.Of(mesh.TetCount, mesh.NodeCount, sizing);
                notes.Add($"At {mesh.NodeCount:N0} unknowns a solve takes about {size.MemoryBytes / 1e6:N0} MB on the {size.Solver.ToString().ToLowerInvariant()} solver.");
                return new EmRunResult(EmRunStatus.Ok, null, null, null, null, null, null, warnings, KernelName: $"Gmsh {gmsh.Installation.DescribeVersion()}", Notes: notes,
                                       Errors: errors,
                                       MeshOnly: new Em3dMeshOnlyReport(mesh.TetCount, ThermalSizeEstimate.Of(lowering.Input, sizing).Elements, meshed.Reused, runDir,
                                                                        PalaceRun.GmshWarningLines(Path.Combine(runDir, PalaceRun.GmshLogFile))));
            }

            // ── the problem's fixed parts: conductivities and the mesh's measures ──
            var tech = e.Technology;
            bool kOfT = t.Balance?.KOfT ?? true;
            var (records, conductivity) = Conductivities(lowering, e, notes);
            var zero = new ThermalField(mesh, new double[mesh.NodeCount]);
            var probes = document.Probes.ToList();

            // ── the sweep ──
            var baseRes = e.Resolution!;
            string? sweepError = null;
            var axes = circuit is not null ? [.. circuit.Axes] : Axes(t, baseRes, out sweepError);
            if (axes is null) return Refuse(sweepError!);

            // ── brief-em3d-76 R-em3d76-3: a submodel's whole-model solution, and the points it carries ──
            Global? global = null;
            if (t.Submodel is { } sm)
            {
                var g = LoadGlobal(sm, document, path, workspaceCws, resultsRoot, e, notes, ct, control, out string? globalError);
                if (g is null) return globalError is null ? Stop(EmRunStatus.Cancelled, EmDiagnostics.Cancelled()) : Refuse(globalError);
                global = g;
                axes = g.Axes;
            }
            var points = Product(axes);

            var options = new ThermalSolveOptions
            {
                Solver = SolverOf(t),
                KOfT = kOfT,
                NewtonTolerance = t.Balance?.Tolerance ?? 1e-6,
                NewtonMaxIterations = t.Balance?.MaxIterations ?? 30,
                Cancellation = ct,
            };
            var assembly = new ThermalAssembly(mesh);
            notes.Add($"The mesh: {mesh.TetCount:N0} tetrahedra of order {mesh.Order}, {mesh.NodeCount:N0} nodes, " +
                      $"{lowering.Regions.Count} solid(s).");

            // ── brief-em3d-77: bond wires or port currents make it conductive balance ──
            bool sigmaOfT = t.Balance?.SigmaOfT ?? true;
            options = options with { SigmaOfT = sigmaOfT };
            notes.Add($"Conductive balance: σ(T) {(sigmaOfT ? "on" : "off (σ at 20 °C everywhere)")}, k(T) {(kOfT ? "on" : "off (k at its nominal value everywhere)")}.");
            ElectroRun? et = null;
            var runaway = new List<bool>();
            bool harmonics = (t.Currents ?? []).Any(c => c.Harmonics is { Count: > 0 });
            if (lowering.Wires.Count > 0 || lowering.PortContacts.Count > 0 || harmonics)
            {
                if (global is not null && (lowering.PortContacts.Count > 0 || harmonics))
                    return Refuse("A submodel carries no port currents in this version: run the currents in the whole-model setup.");
                (ThermalProblem?, C3dResolution?, string?) ThermalAt(IReadOnlyList<(string Var, double Value)> point)
                {
                    // a circuit-driven point's names are the circuit's, bound by the point itself — none is the document's
                    var sets = point.Where(q => !ThermalCircuitLink.IsReserved(q.Var))
                                    .Select(q => (q.Var, q.Value.ToString("R", CultureInfo.InvariantCulture))).ToList();
                    var r = sets.Count == 0 ? baseRes : C3dResolver.Resolve(document, C3dCell.Of(path), null, sets);
                    var pr = Problem(t, document, lowering, conductivity, mesh, zero, r, out string? err);
                    return (pr, r, err);
                }
                var first = circuit is not null ? circuit.Point(Array.IndexOf(circuit.Converged, true)) : points[0];
                et = Electro(lowering, e, t, mesh, ThermalAt, options, notes, first, out string? electroWhy, circuit is null ? null : circuit.Say,
                             C3dThermal.WireGroundPlaneZ(document, e, out _));
                if (et is null) return Refuse(electroWhy!);
                if (et.System.Notes.Count > 0) notes.AddRange(et.System.Notes);
                if (lowering.Wires.Count > 0)
                {
                    // the lowering's note, now with where each span element lies
                    notes.Remove(ThermalWireLowering.Note(lowering.Wires));
                    notes.Add(ThermalWireLowering.Note(lowering.Wires, et.System.WireHosting));
                }
            }
            var wireProbes = WireProbes(probes, lowering, e);

            // ── 4. every point ──
            var fields = new List<double[]>();
            var cutChecks = new List<CutFlux>();
            var reads = new List<Dictionary<string, ProbeRead>>();
            var measures = new List<Dictionary<string, double>>();
            var balances = new List<(double In, double Balance)>();
            var problemsAt = new List<ThermalProblem?>();                      // brief-em3d-80: what the small-signal step reads
            var resolutionsAt = new List<C3dResolution>();
            double[]? previous = null;
            var summary = new List<string>();
            var skipped = new bool[points.Count];
            for (int pi = 0; pi < points.Count; pi++)
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage($"solving point {pi + 1} of {points.Count}");
                // brief-em3d-79: the HB's sweep variables are the circuit's, never the document's — its scope is the base one
                var sets = circuit is not null ? [] : points[pi].Select(p => (p.Var, p.Value.ToString("R", CultureInfo.InvariantCulture))).ToList();
                var res = sets.Count == 0 ? baseRes : C3dResolver.Resolve(document, C3dCell.Of(path), null, sets);
                ThermalField? globalField = global is null ? null : new ThermalField(global.Mesh, global.Temperatures[pi]);
                var problem = Problem(t, document, lowering, conductivity, mesh, zero, res, out string? valueError, globalField);
                if (problem is null) return Refuse(At(axes, points[pi]) + valueError);
                problemsAt.Add(problem);
                resolutionsAt.Add(res);
                if (global is not null && SourceMismatch(t, global.Setup.Thermal!, document, lowering, res) is { } mismatch)
                    return Refuse(At(axes, points[pi]) + mismatch);
                ThermalSolution sol;
                ElectroPoint? ep = null;
                if (circuit is not null && !circuit.Converged[pi])
                {
                    // R-em3d79-3 — a point the HB did not converge at is skipped, and its neighbours still solve
                    skipped[pi] = true;
                    ep = et?.Skip(pi, axes.Count > 0 ? axes[^1].Values.Length : 0);
                    sol = ep?.Thermal ?? NanSolution(mesh);
                    runaway.Add(false);
                }
                else if (et is not null)
                {
                    try { ep = et.Solve(circuit is not null ? circuit.Point(pi) : points[pi], pi, axes.Count > 0 ? axes[^1].Values.Length : 0); }
                    catch (ElectrothermalException x) { return Refuse(At(axes, points[pi]) + x.Message); }
                    catch (InvalidOperationException x) { return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed(At(axes, points[pi]) + x.Message)); }
                    if (ep.Failure is not null)
                        return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed($"{At(axes, points[pi])}conductive balance did not converge: {ep.Failure}."));
                    sol = ep.Thermal;
                    runaway.Add(ep.Runaway);
                }
                else
                {
                    try { sol = ThermalSolver.Solve(problem, options with { InitialGuess = previous }, assembly); }
                    catch (FloatingRegionsException x)
                    {
                        var names = x.Regions.Select(r => r < lowering.Regions.Count ? $"'{lowering.Regions[r].Solid}'" : $"region {r}").Distinct();
                        return Refuse(At(axes, points[pi]) + $"{string.Join(", ", names)} touch{(x.Regions.Length == 1 ? "es" : "")} no fixed-temperature " +
                                      "or convection face through any solid, so no steady state exists: fix a face's temperature, give it a convection " +
                                      "condition, or join it to a solid that has one.");
                    }
                    catch (InvalidOperationException x) { return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed(At(axes, points[pi]) + x.Message)); }
                }
                // a point that did not converge (or is not finite) is no warm start for the next one
                if (!skipped[pi] && sol.Converged && sol.Temperature.All(double.IsFinite)) previous = sol.Temperature;
                fields.Add(sol.Temperature);
                balances.Add((sol.SourcePowerW, sol.BalanceRelative));
                var field = new ThermalField(mesh, sol.Temperature);
                var read = ReadProbes(probes, lowering, field, document.DbuPerMicron, wireProbes, ep?.Solution);
                reads.Add(read);
                measures.Add(Measures(t, res, read, errors, At(axes, points[pi]), lowering.SymmetryFactor));
                if (global is not null)
                {
                    var check = CutCheck(lowering, mesh, field, conductivity, globalField!, global.Conductivity, sol, kOfT);
                    cutChecks.Add(check);
                    string where0 = points.Count > 1 ? At(axes, points[pi]) : "";
                    summary.Add(where0 + check.Line);
                    if (check.Mismatch > CutMismatchLimit) warnings.Add(where0 + check.Warning);
                }

                string where = points.Count > 1 ? At(axes, points[pi]) : "";
                if (skipped[pi])
                {
                    summary.Add($"{where}{HbNotConverged}: the circuit's HB did not converge at this point, so it was skipped.");
                    continue;
                }
                if (ep is { Runaway: true })
                {
                    summary.Add($"{where}no steady state (thermal runaway): the point has no temperature.");
                    continue;
                }
                summary.Add($"{where}{sol.Unknowns:N0} unknowns, {Describe(sol, et is not null)}; energy balance {sol.BalanceRelative:G3} " +
                            $"({(et is null ? "sources" : et.Rf.Any ? "sources, Joule and RF heat" : "sources and Joule heat")} {sol.SourcePowerW:G6} W, out {sol.FixedHeatOutW:G6} W through fixed faces and " +
                            $"{sol.ConvectionHeatOutW:G6} W by convection).");
                // a conductive-balance point's notes begin with its system's, which the run's notes already carry once
                foreach (string n in sol.Notes) if (et is null || !et.System.Notes.Contains(n)) summary.Add(where + n);
                if (!(sol.BalanceRelative <= ThermalSolver.BalanceTolerance))
                    warnings.Add($"{where}The energy balance does not close: {sol.BalanceRelative:G3} of the heat is unaccounted for " +
                                 $"(tolerance {ThermalSolver.BalanceTolerance:G3}). The solve is not to be trusted at this point.");
                if (!sol.Converged)
                {
                    warnings.Add($"{where}Newton over k(T) did not converge; the point's temperatures are its last iterate.");
                    // brief-em3d-98 — the solver's own signal (ThermalSolution.Converged), never a criterion of ours
                    record.NotConverged ??= $"Newton over k(T) did not converge{(points.Count > 1 ? $" at {where.TrimEnd(':', ' ')}" : "")}: " +
                                            "the temperatures there are its last iterate.";
                }
                foreach (string held in Holds(lowering, records, mesh, sol.Temperature, kOfT)) if (!notes.Contains(held)) notes.Add(held);
            }
            notes.AddRange(summary.Count <= 12 ? summary : [.. summary.Take(6), $"… and {summary.Count - 6} more line(s) in the result's Notes."]);
            if (et?.RunawayLine is { } runawayLine) warnings.Add(runawayLine);
            if (skipped.Count(x => x) is var nskip and > 0)
                warnings.Add($"The circuit's HB did not converge at {nskip} of {points.Count} point(s) ({string.Join("; ", Enumerable.Range(0, points.Count).Where(i => skipped[i]).Select(i => At(axes, points[i]).TrimEnd(':', ' ')))}): " +
                             $"{(nskip == 1 ? "it was" : "they were")} skipped ({HbNotConverged}), and the points either side still solved.");

            // ── limits (D11) ──
            foreach (var p in probes)
            {
                if (p.LimitC is not { } lim) continue;
                // a runaway point has no temperature, but it is past every limit: it counts as the crossing
                int first = Enumerable.Range(0, reads.Count).FirstOrDefault(i => i < runaway.Count && runaway[i] ||
                                                                                 reads[i].TryGetValue(p.Name, out var v) && v.Max >= lim, -1);
                if (first >= 0 && first < runaway.Count && runaway[first])
                    warnings.Add($"Probe '{p.Name}' passes its limit of {lim:G6} °C by {At(axes, points[first]).TrimEnd(':', ' ')}" +
                                 $"{(points.Count > 1 ? "" : "the only point")}, where no steady state exists (thermal runaway).");
                else if (first >= 0)
                    warnings.Add($"Probe '{p.Name}' reaches its limit of {lim:G6} °C first at {At(axes, points[first]).TrimEnd(':', ' ')}" +
                                 $"{(axes.Count > 0 ? "" : "the only point")}: {reads[first][p.Name].Max:F3} °C.");
            }

            // ── the mesh-convergence check ──
            if (t.Mesh?.Check == true && points.Count > 0 && (et is not null || circuit is not null))
                // the check re-solves conduction alone: with wires, currents or a circuit's point it would compare a different problem
                notes.Add("The mesh-convergence check was not run: it re-solves conduction alone, and this setup's wires, currents or circuit " +
                          "would make the two solves different problems rather than two meshes of one.");
            else if (t.Mesh?.Check == true && points.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage("the mesh-convergence check");
                notes.AddRange(ConvergenceCheck(document, e, t, gmsh.Installation.Path, runDir, conductivity, points[0], path, reads[0],
                                                fields[0].Where(double.IsFinite).DefaultIfEmpty(0).Min(),
                                                options, control, ct, global is null ? null : new ThermalField(global.Mesh, global.Temperatures[0])));
            }

            // ── brief-em3d-79 R-em3d79-3a: where in the HB's sweep each limit is first crossed ──
            List<(string Name, DataCube Cube)>? crossings = null;
            if (circuit is not null)
            {
                crossings = LimitCrossings(probes, reads, axes, circuit, i => i < runaway.Count && runaway[i], out var sentences);
                notes.AddRange(sentences);
                summary.AddRange(sentences);
            }

            // ── brief-em3d-80: the Rth matrix, Z_th, the Foster networks and the pulse train ──
            // A point that ran away has no temperatures to linearise about or to pulse from: it is passed over as a skipped one is.
            // The step's solves are the conduction problem WITHOUT the wires, so a body reached only through bond wires has no
            // steady state there even though the run's own balance solved: said, never thrown out of the run.
            bool[] noField = [.. skipped.Select((sk, i) => sk || (i < runaway.Count && runaway[i]))];
            SmallSignalOutput? small;
            try
            {
                small = SmallSignal(new SmallSignalInput(setup, t, document, e, lowering, mesh, assembly, conductivity, problemsAt, resolutionsAt,
                                                         fields, noField, axes, options, kOfT, et is not null, resultsRoot), ct, control);
            }
            catch (FloatingRegionsException x)
            {
                var names = x.Regions.Select(r => r < lowering.Regions.Count ? $"'{lowering.Regions[r].Solid}'" : $"region {r}").Distinct();
                return Refuse($"Rth / Z_th / Pulse: {string.Join(", ", names)} touch{(x.Regions.Length == 1 ? "es" : "")} no fixed-temperature or " +
                              "convection face through any solid. The steady run reached it through bond wires, and the Rth, Z_th and pulse " +
                              "solves are conduction in the meshed solids only: give it a boundary of its own, or join it to a solid that has one.");
            }
            catch (InvalidOperationException x) { return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed("Rth / Z_th / Pulse: " + x.Message)); }
            if (small is { Refusal: { } smallWhy }) return Refuse(smallWhy);
            if (small is not null)
            {
                notes.AddRange(small.Notes);
                warnings.AddRange(small.Warnings);
                summary.AddRange(small.Notes);
            }

            // ── 5. the result ──
            var data = Build(axes, probes, reads, measures, balances, t, summary, runaway);
            foreach (var (g, n, c) in small?.Cubes ?? []) data.AddToGroup(g, n, c);
            if (et is not null) AddElectro(data, axes, et, lowering, runaway);
            if (circuit is not null) AddCircuit(data, axes, circuit, skipped, crossings!);
            if (cutChecks.Count == points.Count && cutChecks.Count > 0)
            {
                data.AddToGroup(Group, "Submodel:cut_W", Cube(axes, cutChecks.Select(c => c.SubmodelW), "W"));
                data.AddToGroup(Group, "Submodel:global_W", Cube(axes, cutChecks.Select(c => c.GlobalW), "W"));
                data.AddToGroup(Group, "Submodel:mismatch", Cube(axes, cutChecks.Select(c => c.Mismatch), "1"));
            }
            // a cancel after this line would leave a result on disk that the run then reports as cancelled (R-em3d74-5f)
            ct.ThrowIfCancellationRequested();
            string pvd;
            try
            {
                pvd = ThermalFieldFiles.Write(runDir, mesh, fields, [.. lowering.Regions.Select(r => r.Tag)]);
                WriteTemperatures(runDir, axes, points, fields);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            {
                return Stop(EmRunStatus.EngineError, EmDiagnostics.SolveFailed($"the temperature fields could not be written: {x.Message}"));
            }
            string? npy = WriteNpy(resultsRoot, setup, data, errors);
            try { inputs.KeepIn(runDir); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            { warnings.Add($"The run's inputs could not be kept, so its result cannot be called stale: {x.Message}"); }
            var outputs = new List<EmRunOutput>();
            if (npy is not null) outputs.Add(new EmRunOutput("npy", npy));
            outputs.Add(new EmRunOutput("fields", pvd));
            foreach (string f in small?.Files ?? []) outputs.Add(new EmRunOutput("foster", f));
            return new EmRunResult(EmRunStatus.Ok, data, null, null, npy, null, null, warnings,
                KernelName: "circuitRF thermal", Notes: notes, Errors: errors, Outputs: outputs);
        }
        catch (OperationCanceledException)
        {
            return Stop(EmRunStatus.Cancelled, EmDiagnostics.Cancelled());
        }
    }

    /// <summary>A probe's reading at one point: max, min and average (all one number for a point probe), and a line's
    /// samples.</summary>
    public sealed record ProbeRead(double Max, double Min, double Avg, double[]? Line = null)
    {
        /// <summary>The probe's own statistic (its <c>Stat</c>; the mean when it states none): what <c>T(probe)</c> reads and
        /// the plain <c>T:&lt;probe&gt;</c> cube carries.</summary>
        public C3dProbeStat Stat { get; init; } = C3dProbeStat.Avg;

        public double Stated => Stat switch { C3dProbeStat.Max => Max, C3dProbeStat.Min => Min, _ => Avg };
    }

    private static ThermalSolverKind SolverOf(CemThermal t)
        => t.Mesh?.Solver switch { ThermalMeshSolver.Direct => ThermalSolverKind.Direct, ThermalMeshSolver.Iterative => ThermalSolverKind.Iterative, _ => ThermalSolverKind.Auto };

    /// <summary>brief-dfr36-1 R-dfr36-1-3 — whether a run of thermal setup <paramref name="setup"/> would reuse the mesh in its folder. A
    /// setup driven from a circuit is Unknown: the ports its currents enter, and so the faces in its script, come from that circuit's
    /// harmonic balance, which nothing here runs.</summary>
    public static Em3dMeshCurrency MeshCurrency(EmSetup setup, C3dDocument document, string documentPath, string? workspaceCws, string resultsRoot,
                                                C3dElaborator? elaborator = null)
    {
        var t = setup.Thermal ?? new CemThermal();
        if ((t.Currents ?? []).Any(c => c.FromCircuit is not null)) return Em3dMeshCurrency.Unknown;
        var drawn = (elaborator ?? new C3dElaborator()).Elaborate(document, Path.GetFullPath(documentPath), workspaceCws, new C3dElaborationOptions(null,
            setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC));
        if (!drawn.Ok) return Em3dMeshCurrency.Stale;
        var e = C3dModelled.Filter(document, drawn);
        if (!e.Ok || e.Technology is null || ThermalLowerings.Build(document, e, t, 1, out _) is not { } lowering) return Em3dMeshCurrency.Stale;
        return PalaceRun.HasMeshOf(RunDirectory(resultsRoot, setup), lowering.Gmsh, null, out _) ? Em3dMeshCurrency.Current : Em3dMeshCurrency.Stale;
    }

    private static PalaceStep Mesh(string runDir, ThermalLowering lowering, string gmsh, RunControl? control, CancellationToken ct,
                                   List<string> notes, out ThermalMesh? mesh, out string? error)
    {
        mesh = null;
        error = null;
        control?.BeginStage("meshing with Gmsh");
        PalaceStep step;
        try { step = PalaceRun.Mesh(runDir, lowering.Gmsh, gmsh, control, ct); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            error = $"the mesh could not be staged in '{runDir}' ({x.Message}).";
            return new PalaceStep(false, false, false, error);
        }
        if (!step.Ok) return step;
        notes.Add(step.Reused ? $"The geometry script is unchanged since the last run, so its mesh was reused ({runDir})."
                              : $"Meshed with Gmsh in {runDir}.");
        try { mesh = ReadMesh(Path.Combine(runDir, GmshGeoWriter.MeshFile), lowering, out error); }
        catch (InvalidDataException x) { error = x.Message; }
        if (mesh is not null) mesh = Split(mesh, lowering, notes);
        return step;
    }

    /// <summary>
    /// brief-em3d-76 R-em3d76-1 — <paramref name="mesh"/> split at every contact the lowering gives a resistance, and the run
    /// note listing each contact in force: its two solids, R″, where the value came from, and the area it covers. An override
    /// whose two solids share no face in the mesh is said to apply nowhere.
    /// </summary>
    public static ThermalMesh Split(ThermalMesh mesh, ThermalLowering lowering, List<string> notes)
    {
        if (lowering.Contacts.Count == 0) return mesh;
        var split = ThermalInterfaces.Split(mesh, [.. lowering.Contacts.Select(c => new InterfaceResistance(c.RegionA, c.RegionB, c.ResistanceM2KW))],
                                            lowering.TagRegions);
        var lines = new List<string>();
        for (int i = 0; i < lowering.Contacts.Count; i++)
        {
            var c = lowering.Contacts[i];
            if (split.Faces[i] > 0)
                lines.Add($"'{c.SolidA}' | '{c.SolidB}': {G(c.ResistanceM2KW)} m²·°C/W from {c.Source}, over " +
                          $"{G(split.AreaM2[i] * 1e6)} mm² ({split.Faces[i]} face(s))");
            else if (c.Source.Contains("override", StringComparison.Ordinal))
                lines.Add($"'{c.SolidA}' | '{c.SolidB}': the override of {G(c.ResistanceM2KW)} m²·°C/W applies nowhere — the two share no face in the mesh");
        }
        if (lines.Count > 0)
            notes.Add($"Interface resistances in force ({split.CopiedNodes:N0} node(s) duplicated): " + string.Join("; ", lines) + ".");
        return split.Mesh;
    }

    /// <summary>The solver's mesh from Gmsh's file and the lowering's tag table: tetrahedra with their region, triangles with
    /// their tag, micrometres to metres, and the nodes no tetrahedron uses dropped.</summary>
    public static ThermalMesh? ReadMesh(string mshPath, ThermalLowering lowering, out string? error)
    {
        error = null;
        var raw = MshReader.Read(mshPath);
        int order = lowering.Input.Sizing.Order;
        if (raw.Order != order)
        {
            error = $"Gmsh wrote a mesh of order {raw.Order} where order {order} was asked for.";
            return null;
        }
        var regionOf = new Dictionary<int, int>();
        for (int i = 0; i < lowering.Regions.Count; i++) regionOf[lowering.Regions[i].Tag] = i;
        var region = new int[raw.TetCount];
        for (int k = 0; k < raw.TetCount; k++)
            if (!regionOf.TryGetValue(raw.TetPhysical[k], out region[k]))
            {
                error = $"The mesh holds a tetrahedron of group {raw.TetPhysical[k]}, which is no solid of the problem.";
                return null;
            }
        var nodes = new double[raw.Nodes.Length];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = raw.Nodes[i] * GmshGeoWriter.LengthUnitM;
        var (tris, tags) = OnSheets(nodes, order == 2 ? raw.TrianglesHigh : raw.Triangles, raw.TrianglePhysical, order == 2 ? 6 : 3, lowering);
        return new ThermalMesh(nodes, order, order == 2 ? raw.TetsHigh : raw.Tets, region, tris, tags).Compact(out _);
    }

    /// <summary>
    /// A sheet's group is recovered in the script by its BOUNDING BOX, so a sheet with a hole or a notch also collects the
    /// coplanar surfaces inside the box that are not the sheet — the hole's floor, the notch's — and heated them. The
    /// fragment imprinted the sheet's outline, so every triangle lies wholly on the sheet or wholly off it: its centroid, in
    /// the sheet's own frame, decides. Triangles of every other group pass unchanged.
    /// </summary>
    private static (int[] Triangles, int[] Tags) OnSheets(double[] nodes, int[] triangles, int[] tags, int per, ThermalLowering lowering)
    {
        var sheetOf = new Dictionary<int, Em3dSheet>();
        foreach (var s in lowering.Input.Sheets)
        {
            int tag = lowering.SheetSourceTags.TryGetValue(s.Name, out int a) ? a : lowering.PatchTags.TryGetValue(s.Name, out int b) ? b : 0;
            if (tag > 0) sheetOf[tag] = s.Sheet;
        }
        if (sheetOf.Count == 0) return (triangles, tags);
        var keptTris = new List<int>(triangles.Length);
        var keptTags = new List<int>(tags.Length);
        for (int t = 0; t < tags.Length; t++)
        {
            if (sheetOf.TryGetValue(tags[t], out var sheet))
            {
                double cx = 0, cy = 0, cz = 0;
                for (int k = 0; k < 3; k++)
                {
                    int n = triangles[t * per + k];
                    cx += nodes[3 * n]; cy += nodes[3 * n + 1]; cz += nodes[3 * n + 2];
                }
                if (!OnSheet(sheet, cx / 3, cy / 3, cz / 3)) continue;
            }
            for (int k = 0; k < per; k++) keptTris.Add(triangles[t * per + k]);
            keptTags.Add(tags[t]);
        }
        return ([.. keptTris], [.. keptTags]);
    }

    /// <summary>Whether world point (x, y, z), on the sheet's plane, lies inside its outline and outside every hole.</summary>
    internal static bool OnSheet(Em3dSheet sheet, double x, double y, double z)
    {
        double u = x, v = y;
        if (sheet.Frame is { } f)
        {
            double dx = x - f.Origin.X, dy = y - f.Origin.Y, dz = z - f.Origin.Z;
            u = dx * f.U.X + dy * f.U.Y + dz * f.U.Z;
            v = dx * f.V.X + dy * f.V.Y + dz * f.V.Z;
        }
        return Inside(sheet.Outline, u, v) && !sheet.Holes.Any(h => Inside(h, u, v));
    }

    private static bool Inside(IReadOnlyList<Point2> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (a, b) = (ring[i], ring[j]);
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The problem at one point: every value resolved in <paramref name="res"/>.</summary>
    private static ThermalProblem? Problem(CemThermal t, C3dDocument doc, ThermalLowering lowering, List<ThermalConductivity> k,
                                           ThermalMesh mesh, ThermalField zero, C3dResolution res, out string? error,
                                           ThermalField? global = null)
    {
        error = null;
        double Value(string text, string what, out string? err, ThermalQuantity q)
        {
            double v = C3dThermal.Evaluate(res, text, out err, q) ?? double.NaN;
            if (err is not null) err = $"{what} '{text}' does not resolve: {err}.";
            else if (!double.IsFinite(v)) err = $"{what} '{text}' is not a finite number.";
            return v;
        }
        var sheets = new List<SurfaceSource>();
        var volumes = new List<VolumeSource>();
        foreach (var h in doc.HeatSources.Where(h => h.Model))
        {
            string text = (t.Sources ?? []).FirstOrDefault(s => s.Name == h.Name)?.Power ?? h.Power ?? "0";
            double p = Value(text, $"Heat source '{h.Name}''s power", out error, ThermalQuantity.Power);
            if (error is not null) return null;
            if (lowering.SheetSourceTags.TryGetValue(h.Name, out int tag))
            {
                double area = zero.Surface(new HashSet<int> { tag })?.Measure ?? 0;
                if (!(area > 0)) { error = $"Heat source '{h.Name}' has no area in the mesh."; return null; }
                sheets.Add(new SurfaceSource(tag, h.Density == C3dHeatDensity.PerArea ? p : p / area));
            }
            else if (lowering.SolidSourceRegions.TryGetValue(h.Name, out int region))
            {
                double volume = zero.Region(region)?.Measure ?? 0;
                if (!(volume > 0)) { error = $"Heat source '{h.Name}' has no volume in the mesh."; return null; }
                volumes.Add(new VolumeSource(region, h.Density == C3dHeatDensity.PerVolume ? p : p / volume));
            }
        }
        var fixedT = new List<FixedTemperature>();
        var conv = new List<ConvectionCondition>();
        foreach (var b in t.Boundaries ?? [])
        {
            int? tag = b.Face == C3dThermal.ExposedFaces ? lowering.ExposedTag : lowering.FaceTag(b.Face, true);
            if (tag is not { } tg) continue;
            if (b.Kind == ThermalBoundaryKind.FixedT)
            {
                double tc = Value(b.TempC ?? "", $"The FixedT boundary on '{b.Face}': TempC", out error, ThermalQuantity.Temperature);
                if (error is not null) return null;
                fixedT.Add(new FixedTemperature(tg, tc));
            }
            else
            {
                double h = Value(b.H ?? "", $"The Convection boundary on '{b.Face}': H", out error, ThermalQuantity.Plain);
                if (error is not null) return null;
                double amb = Value(b.AmbientC ?? "", $"The Convection boundary on '{b.Face}': AmbientC", out error, ThermalQuantity.Temperature);
                if (error is not null) return null;
                conv.Add(new ConvectionCondition(tg, h, amb));
            }
        }
        // brief-em3d-76 R-em3d76-3a — a submodel's cut faces take the whole model's solution at each of their nodes
        var fields = new List<FixedField>();
        if (lowering.CutTag is { } cut && global is not null)
            fields.Add(new FixedField(cut, (x, y, z) => global.AtOrNearest(x, y, z) ?? double.NaN));
        return new ThermalProblem
        {
            Mesh = mesh, Conductivity = k, SurfaceSources = sheets, VolumeSources = volumes, Fixed = fixedT, Convection = conv, FixedFields = fields,
        };
    }

    /// <summary>Every probe's reading on <paramref name="field"/>; a wire probe's (brief-em3d-77) over its wires' 1D temperatures,
    /// feet included, from <paramref name="electro"/>.</summary>
    private static Dictionary<string, ProbeRead> ReadProbes(IReadOnlyList<C3dProbe> probes, ThermalLowering lowering, ThermalField field,
                                                            int dbuPerMicron, IReadOnlyDictionary<string, int[]>? wireProbes = null,
                                                            CircuitRF.Thermal.Nonlinear.ElectrothermalSolution? electro = null)
    {
        double m = 1e-6 / dbuPerMicron;
        var read = new Dictionary<string, ProbeRead>(StringComparer.Ordinal);
        ProbeRead? Stats(ThermalStats? s) => s is { } x ? new ProbeRead(x.MaxC, x.MinC, x.AvgC) : null;
        foreach (var p in probes)
        {
            ProbeRead? r = null;
            if (p.Point is { } pt && field.At(pt.X * m, pt.Y * m, pt.Z * m) is { } v) r = new ProbeRead(v, v, v);
            else if (p.Face is { Count: > 0 } faces && faces.Select(f => lowering.FaceTag(f, false)).ToList() is var tags && tags.All(t => t is not null))
                r = Stats(field.Surface(tags.Select(t => t!.Value).ToHashSet()));
            else if (p.Solid is { } solid && lowering.RegionOf(solid) is var ri and >= 0) r = Stats(field.Region(ri));
            else if (p.Spot is { } spot && lowering.FaceTag(spot.Face, false) is { } st)
                r = Stats(field.Spot(new HashSet<int> { st }, (spot.Center.X * m, spot.Center.Y * m, spot.Center.Z * m), spot.Diameter * m / 2));
            else if (p.Wire is not null && wireProbes is not null && wireProbes.TryGetValue(p.Name, out int[]? ws) && ws.Length > 0)
            {
                var all = ws.SelectMany(w => electro?.WireTemperature[w] ?? [double.NaN]).ToList();
                r = new ProbeRead(all.Max(), all.Min(), all.Average());
            }
            else if (p.Line is { } line)
            {
                var samples = field.Line((line.From.X * m, line.From.Y * m, line.From.Z * m), (line.To.X * m, line.To.Y * m, line.To.Z * m), LineSamples);
                var inside = samples.Where(double.IsFinite).ToList();
                if (inside.Count > 0) r = new ProbeRead(inside.Max(), inside.Min(), inside.Average(), samples);
                else r = new ProbeRead(double.NaN, double.NaN, double.NaN, samples);
            }
            if (r is not null) read[p.Name] = r with { Stat = p.Stat ?? C3dProbeStat.Avg };
        }
        return read;
    }

    /// <summary>R-em3d74-5d — each measure at one point: its probe calls replaced, in the parsed expression, by the probe's
    /// values, then evaluated by the one expression engine in the point's resolved scope. A measure that fails is a named
    /// error for that point.</summary>
    private static Dictionary<string, double> Measures(CemThermal t, C3dResolution res, Dictionary<string, ProbeRead> read,
                                                       List<string> errors, string where, int symmetryFactor = 1)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string measure in t.Measures ?? [])
        {
            int eq = measure.IndexOf('=');
            if (eq <= 0) continue;
            string name = measure[..eq].Trim();
            try
            {
                var ast = Rewrite(Parser.Parse(measure[(eq + 1)..].Trim()), read,
                                  res.IsDefined(C3dThermal.SymmetryFactorName) ? null : symmetryFactor);
                var v = res.EvaluateParsed(ast);
                values[name] = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
            }
            catch (ExpressionException x)
            {
                values[name] = double.NaN;
                errors.Add($"{where}The measure '{name}' could not be evaluated: {x.Message}");
            }
            catch (ProbeUnread x)
            {
                values[name] = double.NaN;
                errors.Add($"{where}The measure '{name}' could not be evaluated: the probe '{x.Probe}' read nothing (it lies outside " +
                           "every meshed solid, or on nothing a thermal run meshes).");
            }
        }
        return values;
    }

    /// <summary>A measure named a probe that read nothing at this point; the measure's own catch words it.</summary>
    private sealed class ProbeUnread(string probe) : Exception
    {
        public string Probe { get; } = probe;
    }

    /// <summary>The measure's probe calls replaced by their values, and — brief-em3d-76 R-em3d76-4b — <c>SymmetryFactor</c> by
    /// 2ⁿ (<paramref name="symmetry"/>; null where the document defines a variable of that name itself).</summary>
    private static Expr Rewrite(Expr e, Dictionary<string, ProbeRead> read, int? symmetry) => e switch
    {
        CallExpr { Args: [RefExpr { Name: var probe }] } c when C3dThermal.ProbeFunctions.Contains(c.Name, StringComparer.Ordinal)
            => new NumberExpr(read.TryGetValue(probe, out var r)
                ? c.Name switch { "Tmax" => r.Max, "Tmin" => r.Min, "Tavg" => r.Avg, _ => r.Stated }
                : throw new ProbeUnread(probe)),
        RefExpr { Name: C3dThermal.SymmetryFactorName } when symmetry is { } f => new NumberExpr(f),
        CallExpr c => c with { Args = [.. c.Args.Select(a => Rewrite(a, read, symmetry))] },
        UnaryExpr u => u with { Operand = Rewrite(u.Operand, read, symmetry) },
        BinaryExpr b => b with { Left = Rewrite(b.Left, read, symmetry), Right = Rewrite(b.Right, read, symmetry) },
        CompareExpr c => c with { Left = Rewrite(c.Left, read, symmetry), Right = Rewrite(c.Right, read, symmetry) },
        LogicExpr l => l with { Left = Rewrite(l.Left, read, symmetry), Right = Rewrite(l.Right, read, symmetry) },
        ConditionalExpr d => d with { Condition = Rewrite(d.Condition, read, symmetry), Then = Rewrite(d.Then, read, symmetry), Else = Rewrite(d.Else, read, symmetry) },
        _ => e,
    };

    /// <summary>A table held at its end somewhere in the solved field, per region, as its note.</summary>
    private static IEnumerable<string> Holds(ThermalLowering lowering, List<ThermalMaterialRecord?> records, ThermalMesh mesh, double[] t, bool kOfT)
    {
        if (!kOfT) yield break;
        int nn = mesh.NodesPerTet;
        var hi = new double[records.Count];
        var lo = new double[records.Count];
        Array.Fill(hi, double.NegativeInfinity);
        Array.Fill(lo, double.PositiveInfinity);
        for (int e = 0; e < mesh.TetCount; e++)
            for (int k = 0; k < nn; k++)
            {
                double v = t[mesh.Tets[nn * e + k]];
                int r = mesh.TetRegion[e];
                hi[r] = Math.Max(hi[r], v); lo[r] = Math.Min(lo[r], v);
            }
        for (int r = 0; r < records.Count; r++)
        {
            if (records[r]?.Material.ThermalKVsTemp is not { Count: > 0 } || double.IsInfinity(hi[r])) continue;
            foreach (double at in new[] { hi[r], lo[r] })
                if (ThermalProperties.ThermalKAt(records[r]!.Material, at)?.HeldNote is { } note)
                    yield return $"In '{lowering.Regions[r].Solid}': {note}";
        }
    }

    /// <summary>brief-em3d-77 — each wire probe's wires, by index into the lowering's: the wire of that name, every element of an
    /// array of that name (<c>w1</c> reads <c>w1[0]</c>, <c>w1[1]</c>, …), or every wire of a .wBond array of that name.</summary>
    private static Dictionary<string, int[]> WireProbes(IReadOnlyList<C3dProbe> probes, ThermalLowering lowering, C3dElaboration e)
    {
        var map = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var p in probes)
        {
            if (p.Wire is not { } name) continue;
            var arrays = e.Wires.Where(w => w.Array == name).Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
            map[p.Name] = [.. Enumerable.Range(0, lowering.Wires.Count).Where(i =>
            {
                string n = lowering.Wires[i].Name;
                return n == name || n.StartsWith(name + "[", StringComparison.Ordinal) || arrays.Contains(n);
            })];
        }
        return map;
    }

    private static string Describe(ThermalSolution s, bool balance = false)
    {
        string solver = s.Solver == ThermalSolverKind.Direct
            ? $"direct solver (relative residual {s.LinearResidual:G3})"
            : $"{(balance ? "BiCGStab + block AMG" : "PCG + AMG")}, {s.LinearIterations} iteration(s), {s.AmgLevels} level(s), relative residual {s.LinearResidual:G3}";
        return s.NewtonIterations > 0 ? $"{solver}, {s.NewtonIterations} Newton step(s) {(balance ? "of conductive balance" : "over k(T)")}" : solver;
    }

    private static List<List<(string Var, double Value)>> Product(List<(string Var, double[] Values, string Unit)> axes)
    {
        var points = new List<List<(string, double)>> { new() };
        foreach (var (v, values, _) in axes)
            points = [.. points.SelectMany(p => values.Select(x => new List<(string, double)>(p) { (v, x) }))];
        return points;
    }

    private static string At(List<(string Var, double[] Values, string Unit)> axes, List<(string Var, double Value)> point)
        => point.Count == 0 ? "" : string.Join(", ", point.Select(p => $"{p.Var} = {p.Value.ToString("G6", CultureInfo.InvariantCulture)}" +
                                     (axes.First(a => a.Var == p.Var).Unit is { Length: > 0 } u ? " " + u : ""))) + ": ";

    /// <summary>R-em3d74-5c — the DataSet: per probe statistic, a line's T(s), each measure, the energy balance, the limits,
    /// and the notes. Real, single-kind, °C; over the sweep's axes (a scalar with no sweep).</summary>
    private static DataSet Build(List<(string Var, double[] Values, string Unit)> axes, IReadOnlyList<C3dProbe> probes,
                                 List<Dictionary<string, ProbeRead>> reads, List<Dictionary<string, double>> measures,
                                 List<(double In, double Balance)> balances, CemThermal t, List<string> notes, IReadOnlyList<bool>? runaway = null)
    {
        var ds = new DataSet();
        Axis[] sweep = [.. axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        DataCube Cube(Func<int, double> at, string unit)
        {
            var v = new double[reads.Count];
            for (int i = 0; i < v.Length; i++) v[i] = at(i);
            return sweep.Length == 0 ? new DataCube([], v) { Unit = unit } : new DataCube(sweep, v) { Unit = unit };
        }
        double Get(int i, string probe, Func<ProbeRead, double> f) => reads[i].TryGetValue(probe, out var r) ? f(r) : double.NaN;
        foreach (var p in probes)
        {
            if (p.Line is not null)
            {
                var first = reads.Select(r => r.GetValueOrDefault(p.Name)?.Line).FirstOrDefault(l => l is not null);
                if (first is null) continue;
                var s = new Axis("distance", [.. Enumerable.Range(0, first.Length).Select(k => (double)k / (first.Length - 1))], "", null);
                // a point with no line read (a runaway, a skipped circuit point) has no temperature: NaN, never 0 °C
                var none = Enumerable.Repeat(double.NaN, first.Length).ToArray();
                var data = reads.SelectMany(r => r.GetValueOrDefault(p.Name)?.Line ?? none).ToArray();
                ds.AddToGroup(Group, $"T:{p.Name}(s)", new DataCube([.. sweep, s], data) { Unit = "°C" });
                ds.AddToGroup(Group, $"T:{p.Name}:max", Cube(i => Get(i, p.Name, r => r.Max), "°C"));
            }
            else if (p.Point is not null) ds.AddToGroup(Group, $"T:{p.Name}", Cube(i => Get(i, p.Name, r => r.Avg), "°C"));
            else
            {
                ds.AddToGroup(Group, $"T:{p.Name}:max", Cube(i => Get(i, p.Name, r => r.Max), "°C"));
                ds.AddToGroup(Group, $"T:{p.Name}:min", Cube(i => Get(i, p.Name, r => r.Min), "°C"));
                ds.AddToGroup(Group, $"T:{p.Name}:avg", Cube(i => Get(i, p.Name, r => r.Avg), "°C"));
                // the probe's own statistic, when it states one: what it reports over its place (R-em3d73-4b)
                if (p.Stat is not null) ds.AddToGroup(Group, $"T:{p.Name}", Cube(i => Get(i, p.Name, r => r.Stated), "°C"));
            }
            // D11 — every kind of probe with a limit is flagged in the result, not only in a warning sentence
            // a runaway point has no temperature and has crossed every limit (the warning and the crossing table say so too)
            if (p.LimitC is { } lim)
                ds.AddToGroup(Group, $"Limit:{p.Name}", Cube(i => runaway is not null && i < runaway.Count && runaway[i] || Get(i, p.Name, r => r.Max) >= lim ? 1 : 0, "1"));
        }
        ds.AddToGroup(Group, "Energy:balance", Cube(i => balances[i].Balance, "1"));
        ds.AddToGroup(Group, "Energy:in", Cube(i => balances[i].In, "W"));
        foreach (string measure in t.Measures ?? [])
        {
            int eq = measure.IndexOf('=');
            if (eq <= 0) continue;
            string name = measure[..eq].Trim();
            ds.AddToGroup(DataSet.MeasurementsGroup, name, Cube(i => measures[i].GetValueOrDefault(name, double.NaN), ""));
        }
        if (notes.Count > 0)
            ds.AddToGroup(NotesGroup, "Notes", new DataCube(
                [new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(k => (double)k)], "", [.. notes])], new double[notes.Count]));
        return ds;
    }

    private static string? WriteNpy(string resultsRoot, EmSetup setup, DataSet data, List<string> errors)
    {
        try
        {
            if (setup.SnpOutputPathOverride is { Length: > 0 } over)
            {
                string target = Path.ChangeExtension(Path.GetFullPath(over), ".npy");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) File.Delete(target);
                DataSetExporter.Export(data, target, ExportFormat.Npy);
                return target;
            }
            var written = ResultsWriter.WriteRun(Path.GetDirectoryName(Path.GetFullPath(resultsRoot).TrimEnd(Path.DirectorySeparatorChar))!,
                                                 ResultKey(setup), data);
            if (written.Error is { } err) errors.Add($"The thermal result could not be written to results/: {err}");
            return written.Written.Count > 0 ? written.Written[0] : null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            errors.Add($"The thermal result could not be written: {x.Message}");
            return null;
        }
    }

    /// <summary>R-em3d74-5e — mesh a second time with every size × 0.7, solve the first point, and say how far each probe
    /// moved. A report, not a refinement loop.</summary>
    private static IEnumerable<string> ConvergenceCheck(C3dDocument doc, C3dElaboration e, CemThermal t, string gmsh, string runDir,
                                                        List<ThermalConductivity> k, List<(string Var, double Value)> point, string path,
                                                        Dictionary<string, ProbeRead> coarse, double coarseMinC, ThermalSolveOptions options,
                                                        RunControl? control, CancellationToken ct, ThermalField? global = null)
    {
        var fine = ThermalLowerings.Build(doc, e, t, CheckScale, out string? why);
        if (fine is null) return [$"The mesh-convergence check could not be lowered: {why}"];
        var discard = new List<string>();
        var step = Mesh(Path.Combine(runDir, "check"), fine, gmsh, control, ct, discard, out var mesh, out string? error);
        if (step.Cancelled) throw new OperationCanceledException(ct);
        if (mesh is null) return [$"The mesh-convergence check could not mesh: {error ?? step.Message}"];
        var sets = point.Select(p => (p.Var, p.Value.ToString("R", CultureInfo.InvariantCulture))).ToList();
        var res = sets.Count == 0 ? e.Resolution! : C3dResolver.Resolve(doc, C3dCell.Of(path), null, sets);
        var zero = new ThermalField(mesh, new double[mesh.NodeCount]);
        var problem = Problem(t, doc, fine, k, mesh, zero, res, out string? valueError, global);
        if (problem is null) return [$"The mesh-convergence check could not be set up: {valueError}"];
        ThermalSolution sol;
        try { sol = ThermalSolver.Solve(problem, options with { InitialGuess = null }); }
        catch (InvalidOperationException x) { return [$"The mesh-convergence check could not be solved: {x.Message}"]; }
        var read = ReadProbes(doc.Probes, fine, new ThermalField(mesh, sol.Temperature), doc.DbuPerMicron);
        var lines = new List<string> { $"Mesh-convergence check: every element size × {CheckScale} ({mesh.TetCount:N0} tetrahedra), the first sweep point." };
        foreach (var (name, c) in coarse)
        {
            if (!read.TryGetValue(name, out var f)) continue;
            // the change as a share of the probe's RISE (above the coarse field's coolest point), not of its °C reading: the same
            // 0.1 K reads 10 % at 1 °C and 0.1 % at 100 °C, which says nothing about the mesh
            double a = c.Max, b = f.Max, rise = a - coarseMinC;
            lines.Add($"  probe '{name}' (max): {a:F4} → {b:F4} °C, changed by {b - a:+0.0000;-0.0000} °C" +
                      (rise > 0 ? $" ({100 * (b - a) / rise:+0.000;-0.000} % of its rise)." : "."));
        }
        return lines;
    }

    // ── brief-em3d-76 — what a run shares with the next: conductivities, the sweep, the kept temperatures ─────────────────

    /// <summary>The file in a thermal run directory holding every point's nodal temperatures, with the sweep they were solved
    /// at: what a submodel reads its cut faces' values from (R-em3d76-3a).</summary>
    public const string TemperaturesFile = "temperature.bin";

    /// <summary>R-em3d76-3b — above this pointwise mismatch of the flux across the cut faces, the run says the region is too
    /// small. Calibrated on S5 (RESOLVED): a region well clear of the source differs by under 1 %, one whose edge cuts the
    /// source's neighbourhood by well over 10 %.</summary>
    public const double CutMismatchLimit = 0.05;

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Each region's k: its material's (k(T) where a table states one), or an effective block's tensor.</summary>
    private static (List<ThermalMaterialRecord?> Records, List<ThermalConductivity> K) Conductivities(ThermalLowering lowering, C3dElaboration e,
                                                                                                    List<string> notes)
    {
        var records = new List<ThermalMaterialRecord?>();
        var conductivity = new List<ThermalConductivity>();
        for (int i = 0; i < lowering.Regions.Count; i++)
        {
            if (lowering.Effective.TryGetValue(i, out var block))
            {
                records.Add(null);
                conductivity.Add(block.Conductivity);
                continue;
            }
            var rec = ThermalMaterials.For(e, lowering.Regions[i].Solid, lowering.Regions[i].Material)!;
            if (rec.LookThroughNote is { } look && !notes.Contains(look)) notes.Add(look);
            records.Add(rec);
            var m = rec.Material;
            double nominal = ThermalProperties.ThermalKAt(m, 25)!.Value.Value;
            if (m.ThermalKTensor is { Length: 3 } t)
            {
                conductivity.Add(Tensor(m, t, nominal));
                string note = $"Material '{m.Name}' is anisotropic: k = {G(t[0])} / {G(t[1])} / {G(t[2])} W/(m·K) along x / y / z at 25 °C " +
                    "(ThermalKTensor, along the 3D view's axes; a rotated solid's tensor does not rotate with it)" +
                    (m.ThermalKVsTemp is { Count: > 0 } ? ", each component following its ThermalKVsTemp table." : ".");
                if (!notes.Contains(note)) notes.Add(note);
                continue;
            }
            conductivity.Add(m.ThermalKVsTemp is { Count: > 0 }
                ? ThermalConductivity.Varying(nominal, T => { var v = ThermalProperties.ThermalKAt(m, T)!.Value; return (v.Value, v.Slope); })
                : ThermalConductivity.Constant(nominal));
        }
        return (records, conductivity);
    }

    /// <summary>A material's ThermalKTensor as the solver's diagonal tensor: constant, or — with a ThermalKVsTemp table — every
    /// component scaled by the table's k(T)/k(25 °C), so the table states how k changes and the tensor how it is directed.</summary>
    internal static ThermalConductivity Tensor(TechMaterial m, double[] t, double k25)
    {
        if (m.ThermalKVsTemp is not { Count: > 0 }) return ThermalConductivity.Diagonal(t[0], t[1], t[2]);
        double n = Math.Max(t[0], Math.Max(t[1], t[2]));
        return ThermalConductivity.Varying(n, T =>
        {
            var v = ThermalProperties.ThermalKAt(m, T)!.Value;
            return (n * v.Value / k25, n * v.Slope / k25);
        }, (t[0] / n, t[1] / n, t[2] / n));
    }

    /// <summary>The setup's sweep axes, resolved; null with the reason.</summary>
    private static List<(string Var, double[] Values, string Unit)>? Axes(CemThermal t, C3dResolution res, out string? error)
    {
        error = null;
        var axes = new List<(string Var, double[] Values, string Unit)>();
        foreach (var s in t.Sweep ?? [])
        {
            double a = C3dThermal.Evaluate(res, s.Start, out string? ea) ?? double.NaN;
            double b = C3dThermal.Evaluate(res, s.Stop, out string? eb) ?? double.NaN;
            if (ea is not null || eb is not null) { error = $"The sweep of '{s.Var}' does not resolve: {ea ?? eb}."; return null; }
            int n = Math.Max(1, s.Points);
            axes.Add((s.Var, [.. Enumerable.Range(0, n).Select(i => n == 1 ? a : a + (b - a) * i / (n - 1))], UnitOf(res, s.Var)));
        }
        return axes;
    }

    private static string UnitOf(C3dResolution res, string variable)
        => res.Names.TryGetValue(variable, out var nm) && nm.Unit is { } u ? Units.BaseUnit(u) : "";

    private static DataCube Cube(List<(string Var, double[] Values, string Unit)> axes, IEnumerable<double> values, string unit)
    {
        var v = values.ToArray();
        Axis[] sweep = [.. axes.Select(a => new Axis(a.Var, a.Values, a.Unit))];
        return sweep.Length == 0 ? new DataCube([], v) { Unit = unit } : new DataCube(sweep, v) { Unit = unit };
    }

    /// <summary>Writes every point's nodal temperatures beside the run's mesh, with the sweep they belong to.</summary>
    private static void WriteTemperatures(string runDir, List<(string Var, double[] Values, string Unit)> axes,
                                          List<List<(string Var, double Value)>> points, List<double[]> fields)
    {
        string path = Path.Combine(runDir, TemperaturesFile);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("CRFT1");
        w.Write(axes.Count);
        foreach (var (v, values, unit) in axes)
        {
            w.Write(v); w.Write(unit); w.Write(values.Length);
            foreach (double x in values) w.Write(x);
        }
        w.Write(fields.Count);
        w.Write(fields.Count == 0 ? 0 : fields[0].Length);
        foreach (var f in fields) foreach (double x in f) w.Write(x);
        _ = points;
    }

    private sealed record StoredTemperatures(List<(string Var, double[] Values, string Unit)> Axes, List<double[]> Fields);

    private static StoredTemperatures? ReadTemperatures(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadString() != "CRFT1") return null;
            var axes = new List<(string Var, double[] Values, string Unit)>();
            int na = r.ReadInt32();
            for (int a = 0; a < na; a++)
            {
                string v = r.ReadString(), unit = r.ReadString();
                var values = new double[r.ReadInt32()];
                for (int i = 0; i < values.Length; i++) values[i] = r.ReadDouble();
                axes.Add((v, values, unit));
            }
            int np = r.ReadInt32(), nn = r.ReadInt32();
            var fields = new List<double[]>();
            for (int p = 0; p < np; p++)
            {
                var f = new double[nn];
                for (int i = 0; i < nn; i++) f[i] = r.ReadDouble();
                fields.Add(f);
            }
            return new StoredTemperatures(axes, fields);
        }
        catch (Exception x) when (x is IOException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A submodel's whole-model solution: the From setup, its mesh (split as it was solved), a field per point, its
    /// regions' k, and the sweep it was solved over.</summary>
    private sealed record Global(EmSetup Setup, ThermalMesh Mesh, List<double[]> Temperatures, List<ThermalConductivity> Conductivity,
                                 List<(string Var, double[] Values, string Unit)> Axes);

    /// <summary>
    /// R-em3d76-3a/-3c — the From setup's current result, solving it first when it has none solved from the inputs as they are now (and saying
    /// which was used). Null with <paramref name="error"/> the reason, or with <paramref name="error"/> null when cancelled.
    /// </summary>
    private static Global? LoadGlobal(CemThermalSubmodel sm, C3dDocument document, string path, string? workspaceCws, string resultsRoot,
                                      C3dElaboration e, List<string> notes, CancellationToken ct, RunControl? control, out string? error)
    {
        error = null;
        var (from, why) = C3dSetups.Select(document, sm.From);
        if (from is null) { error = $"The submodel's From setup '{sm.From}' is not a setup of this 3D view: {why}"; return null; }
        if (from.Problem3D != Em3dProblemType.Thermal || from.Thermal is null) { error = $"The submodel's From setup '{sm.From}' is not a thermal setup."; return null; }
        if (from.Thermal.Submodel is not null) { error = $"The submodel's From setup '{sm.From}' is itself a submodel; a submodel is cut from a whole-model setup."; return null; }
        var runnable = C3dSetups.ForRun(from, path);
        string dir = RunDirectory(resultsRoot, runnable);
        string bin = Path.Combine(dir, TemperaturesFile);
        // brief-em3d-87 R-em3d87-4 — reused only when its manifest matches the inputs now: the document AND every file it was
        // solved from. A result with no manifest (made before brief 87) is never reused; its time proved nothing about a layout.
        bool had = File.Exists(bin);
        var check = had && File.Exists(C3dRunDocument.InputsPathIn(dir)) ? C3dRunDocument.Check(dir, document, path, sm.From) : null;
        var stored = check is { Stale: false } ? ReadTemperatures(bin) : null;
        if (stored is not null)
            notes.Add($"Submodel: setup '{sm.From}''s result ({File.GetLastWriteTime(bin).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}) " +
                      "was solved from the model as it is now, so it was reused.");
        else
        {
            control?.BeginStage($"solving '{sm.From}' first");
            var r = Run(runnable, document, path, workspaceCws, resultsRoot, ct, control);
            if (r.Status == EmRunStatus.Cancelled) return null;
            if (r.Status != EmRunStatus.Ok) { error = $"The submodel's From setup '{sm.From}' did not run: {r.Error}"; return null; }
            notes.Add($"Submodel: setup '{sm.From}' was solved first: " + (check switch
            {
                { What: { } what } => $"{what} {check.Has} changed since its result.",
                _ when had => "its result records no inputs, so nothing says it is the model as it is now.",
                _ => "it had no result.",
            }));
            stored = ReadTemperatures(bin);
            if (stored is null) { error = $"Setup '{sm.From}' ran but left no {TemperaturesFile} in {dir}."; return null; }
        }
        var discard = new List<string>();
        var low = ThermalLowerings.Build(document, e, from.Thermal, 1, out string? lowError);
        if (low is null) { error = $"The submodel's From setup '{sm.From}' does not lower: {lowError}"; return null; }
        var mesh = ReadMesh(Path.Combine(dir, GmshGeoWriter.MeshFile), low, out string? meshError);
        if (mesh is null) { error = $"The mesh of setup '{sm.From}' could not be read: {meshError}"; return null; }
        mesh = Split(mesh, low, discard);
        if (stored.Fields.Count == 0 || stored.Fields.Any(f => f.Length != mesh.NodeCount))
        { error = $"Setup '{sm.From}''s stored temperatures do not match its mesh; run it again."; return null; }
        var (_, k) = Conductivities(low, e, discard);
        return new Global(from, mesh, stored.Fields, k, stored.Axes);
    }

    /// <summary>R-em3d76-3b — a source inside the submodel carrying a different power there than in the whole model, as the
    /// refusal; null when every one matches.</summary>
    private static string? SourceMismatch(CemThermal sub, CemThermal from, C3dDocument doc, ThermalLowering lowering, C3dResolution res)
    {
        foreach (var h in doc.HeatSources.Where(h => h.Model))
        {
            if (lowering.SourcesOutside.Contains(h.Name)) continue;
            string a = (sub.Sources ?? []).FirstOrDefault(s => s.Name == h.Name)?.Power ?? h.Power ?? "0";
            string b = (from.Sources ?? []).FirstOrDefault(s => s.Name == h.Name)?.Power ?? h.Power ?? "0";
            double pa = C3dThermal.Evaluate(res, a, out _, ThermalQuantity.Power) ?? double.NaN, pb = C3dThermal.Evaluate(res, b, out _, ThermalQuantity.Power) ?? double.NaN;
            if (!(Math.Abs(pa - pb) <= 1e-9 * Math.Max(Math.Abs(pa), Math.Abs(pb))))
                return $"Heat source '{h.Name}' carries {G(pa)} in this submodel and {G(pb)} in the whole model it is cut from: the sources " +
                       "inside the region must carry what they carry in the whole model, or its cut faces are fixed to the wrong answer.";
        }
        return null;
    }

    /// <summary>R-em3d76-3b's check at one point.</summary>
    private sealed record CutFlux(double SubmodelW, double GlobalW, double Mismatch, string Line, string Warning);

    /// <summary>
    /// R-em3d76-3b — the heat crossing the submodel's cut faces, in the submodel and in the whole model's solution over the
    /// same faces: the totals, and the POINTWISE mismatch Σ|q_sub − q_whole| / Σ|q_whole| over the faces' triangles. The
    /// totals agree by conservation whenever the heat leaves only through the cut; the pointwise figure is what says the fine
    /// detail changed the temperature at the region's edge.
    /// </summary>
    private static CutFlux CutCheck(ThermalLowering lowering, ThermalMesh mesh, ThermalField field, IReadOnlyList<ThermalConductivity> k,
                                    ThermalField global, IReadOnlyList<ThermalConductivity> gk, ThermalSolution sol, bool kOfT)
    {
        int cut = lowering.CutTag!.Value;
        var patches = ThermalField.Patches(mesh, cut);
        var qs = field.FluxAcross(patches, k, kOfT, out _);
        var qg = global.FluxAcross(patches, gk, kOfT, out int missed);
        double sub = sol.FixedHeatOutByTag.GetValueOrDefault(cut), whole = qg.Sum();
        double l1 = 0, norm = 0;
        for (int i = 0; i < qs.Length; i++) { l1 += Math.Abs(qs[i] - qg[i]); norm += Math.Abs(qg[i]); }
        double mismatch = norm > 0 ? l1 / norm : 0;
        string line = $"Cut faces: {G(sub)} W leave the submodel through them ({G(qs.Sum())} W by its own gradients); the whole model " +
                      $"carries {G(whole)} W across the same faces; pointwise the two differ by {(100 * mismatch).ToString("F2", CultureInfo.InvariantCulture)} %" +
                      (missed > 0 ? $" ({missed} point(s) of the faces lie outside the whole model's mesh and are not counted)." : ".");
        string warning = $"The heat crossing the submodel's cut faces differs from the whole model's by {(100 * mismatch).ToString("F1", CultureInfo.InvariantCulture)} % " +
                         $"pointwise (above {100 * CutMismatchLimit:G3} %): the region is too small — the fine detail changes the temperature at its " +
                         "edge, so the whole model's temperature there is not the submodel's. Enlarge the region.";
        return new CutFlux(sub, whole, mismatch, line, warning);
    }
}
