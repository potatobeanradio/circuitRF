using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Stability;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// What one evaluation is asked to do on top of the circuit itself (brief-tuneopt-2 R-to2-2). Every
/// member has the default Simulate uses, so <c>new CircuitEvaluationRequest()</c> is Simulate's run.
/// </summary>
public sealed record CircuitEvaluationRequest
{
    /// <summary><c>--set name=expr</c> overrides, applied to the bench's own variables before
    /// elaboration, so anything derived from one re-derives.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Tuned values by tunable key (<c>R1.R</c> → <c>75 Ohm</c>), applied through
    /// <see cref="TunableOverrides"/>. A key that resolves to nothing becomes a plan note (overview D3).</summary>
    public IReadOnlyDictionary<string, string>? Tunables { get; init; }

    /// <summary>The analyses to run, by name; each is promoted to the sweep chain that wraps it, as the
    /// panel card's own Run does. Null runs every enabled chain.</summary>
    public IReadOnlyList<string>? Analyses { get; init; }

    /// <summary>Whether the bench's <c>measure</c> lines are evaluated into the <c>measurements</c> group.</summary>
    public bool Measurements { get; init; } = true;

    /// <summary>Further expressions to evaluate in the measurement scope after the measurements (an
    /// optimizer's goals). Their values come back in <see cref="RunResult.Expressions"/> and are never
    /// added to the DataSet.</summary>
    public IReadOnlyList<string>? Expressions { get; init; }

    /// <summary>Cancellation and progress.</summary>
    public RunControl? Control { get; init; }

    /// <summary>One Monte Carlo trial's draws for the distribution calls the design holds (docs/design/yield.md
    /// §7–8), or null — Simulate's nominal. Every elaboration of this evaluation draws them, an engine's own
    /// re-elaboration included; a draw that cannot be made fails the evaluation.</summary>
    public IStatisticalDraws? Statistics { get; init; }

    /// <summary>Where a breadcrumb line goes as each analysis starts and ends — the GUI's crash
    /// reporter. Null discards them.</summary>
    public Action<string>? Breadcrumb { get; init; }

    /// <summary>brief-sar-10 R-sar10-2 — what a fields analysis does beyond its circuit solve: Simulate writes everything;
    /// a moving tuning knob writes local SAR and B1 only; an optimizer, yield or DOE evaluation writes nothing and computes
    /// each field measurement when an expression asks for it.</summary>
    public FieldsOutput Fields { get; init; } = FieldsOutput.Written;

    /// <summary>brief-sar-10 R-sar10-5 — a written recombination's results go to <c>&lt;analysis&gt; (&lt;label&gt;)</c> beside
    /// the analysis's own, so a re-run yield trial does not overwrite the nominal's. Null: the analysis's own folder.</summary>
    public string? FieldsLabel { get; init; }

    /// <summary>brief-sdl-6 R-sdl6-1 — the run-wide model switch: true runs every chain by EM (<c>--em on</c>), false every
    /// chain and instance by circuit models (<c>--em off</c>), null as each analysis and instance says.</summary>
    public bool? Em { get; init; }

    /// <summary>brief-sdl-6 D5 — the most uncached EM solves the run may start; more is a refusal before anything is
    /// solved, stating the count. Null: no limit (the GUI asks the user instead).</summary>
    public int? EmMaxSolves { get; init; }
}

/// <summary>
/// The one circuit run: elaborate, dispatch every analysis, assemble the grouped <see cref="DataSet"/>
/// and evaluate the measurements into it (brief-tuneopt-2). Simulate (<c>SchematicRunService</c>) is a
/// thin caller of this; tuning, the optimizer and the run verbs reach the same function rather than a
/// copy of it, because a second copy diverges from the first silently.
///
/// <para><b>Writes nothing.</b> The <c>run.npy</c> is the caller's (<c>ResultsWriter</c>), so a tune
/// step or an optimizer evaluation costs no disk I/O.</para>
///
/// <para>Never throws — engine exceptions are captured into <see cref="RunStatus.EngineError"/>.</para>
/// </summary>
public static class CircuitEvaluation
{
    /// <summary>
    /// Plan, then execute: one evaluation of <paramref name="circuit"/> as <paramref name="request"/>
    /// asks. The circuit is not modified and is not re-read; a second call with different tuned values
    /// elaborates again and reads nothing (R-to2-4).
    /// </summary>
    public static RunResult Evaluate(PreparedCircuit circuit, CircuitEvaluationRequest? request = null)
    {
        request ??= new CircuitEvaluationRequest();
        var plan   = Plan(circuit, request);
        if (request.EmMaxSolves is { } max && plan.HasEm)
        {
            var count = CountEmSolves(plan, request.Control);
            if (EmLimitRefusal(count, max) is { } over)
            {
                plan.Nl?.Dispose();
                return new RunResult(RunStatus.EngineError, over);
            }
        }
        var result = Execute(plan, request.Control, request.Measurements, request.Expressions ?? [],
                             request.Breadcrumb, request.Fields, request.FieldsLabel);
        // Simulate prints the plan's notes before the run and the result's after it; a caller of this
        // one call has only the result, so the plan's notes (a promotion, a tuned key that resolved
        // to nothing) lead it.
        return plan.Notes.Count == 0 ? result : result.WithLeadingNotes(plan.Notes);
    }

    /// <summary>
    /// Reads and elaborates the netlist and describes every analysis that will be dispatched, WITHOUT
    /// running any of them — Simulate's first half. See <see cref="Plan"/>.
    /// </summary>
    /// <param name="sourcePath">The document the netlist was written from — the schematic Simulate extracted — so a
    /// <c>model=em</c> analysis can find the cell whose layout it is (brief-sdl-6). Null: the netlist itself.</param>
    public static RunPlan Prepare(string netlistPath, string? baseDirectory = null,
                                  string? onlyAnalysisName = null, string? sourcePath = null)
    {
        var circuit = PreparedCircuit.FromFile(netlistPath, baseDirectory);
        if (sourcePath is not null) circuit.SourcePath = sourcePath;
        return Plan(circuit,
                    new CircuitEvaluationRequest { Analyses = onlyAnalysisName is null ? null : [onlyAnalysisName] });
    }

    /// <summary>
    /// Elaborates a copy of the circuit with the request's overrides and describes every analysis that
    /// will be dispatched, WITHOUT running any of them. Cheap relative to the run (one elaboration,
    /// against a sweep that re-elaborates per point), and the elaborated netlist is handed to
    /// <see cref="Execute"/> rather than being thrown away — so splitting the run in two costs nothing.
    /// Never throws.
    /// <para/>
    /// Naming analyses narrows the run to their CHAINS: each named analysis is <b>promoted to the
    /// outermost enabled sweep that wraps it</b> (<see cref="AnalysisChain.PromoteToRunnableTop"/>), so
    /// every parametric sweep the card belongs to runs and the other chains in the schematic do not.
    /// Naming the base analysis and naming the sweep over it therefore run the same thing — which is
    /// the point: a chain dispatched at its inner analysis drops the sweep axis and still produces a
    /// converged, plausible, complete-looking result, so the mistake is invisible. This is the CLI's own
    /// rule for <c>-a</c>, shared rather than restated.
    /// </summary>
    public static RunPlan Plan(PreparedCircuit circuit, CircuitEvaluationRequest request)
    {
        // brief-sdl-6 D9: the EM model provider is this layer's, so every caller of the one run function has it.
        ParametricEmProvider.Register();

        // ── 1. Read (done once, by PreparedCircuit) ────────────────────────────
        if (circuit.ReadError is { } readError || circuit.Lib is not { } srcLib || circuit.Tb is not { } srcTb)
            return new RunPlan(RunStatus.EngineError, circuit.ReadError ?? "Netlist read failed.");

        // ── 2. Any analysis at all? ────────────────────────────────────────────
        bool hasTyped      = srcTb.Analyses.Count > 0;
        bool hasRawSparam  = HasRawSparamDirective(srcTb);
        if (!hasTyped && !hasRawSparam)
            return new RunPlan(RunStatus.NoAnalysis,
                "No analysis defined — add one to run.");

        // ── 2b. This evaluation's own copy, with its overrides ────────────────
        //
        // ALWAYS a copy, values or none: a parametric sweep writes its swept variable into the
        // bench's GlobalVariables for each point, and two evaluations sharing one list would read
        // each other's points. TunableOverrides shares every object nothing changed, so the copy is
        // a handful of list copies.
        var notes = new List<string>();
        var tuned = TunableOverrides.Apply(
            srcTb, srcLib,
            request.Tunables ?? new Dictionary<string, string>(),
            request.Sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal),
            circuit.PartTunables);
        if (tuned.Refusal is { } refusal || tuned.TestBench is not { } tb || tuned.Library is not { } lib)
            return new RunPlan(RunStatus.EngineError, tuned.Refusal ?? "Tuned values could not be applied.");
        notes.AddRange(tuned.Notes);
        foreach (var (name, expr) in request.Sets)
            HbCircuitRun.ApplySet(tb, name, expr);
        tb.StatisticalDraws = request.Statistics;

        // brief-sdl-6: --em off puts every instance back on its circuit model before anything elaborates.
        if (request.Em == false)
        {
            // brief-sdl-10 D2 — a part with no circuit model cannot be run by circuit models: refused, never left open.
            if (EmModelSwitch.WithoutCircuitModel(tb, lib) is { Count: > 0 } emOnly)
                return new RunPlan(RunStatus.EngineError, EmModelSwitch.NoCircuitModelSentence(emOnly));
            (tb, lib) = EmModelSwitch.CircuitOnly(tb, lib);
        }

        // ── 3. Elaborate ───────────────────────────────────────────────────────
        ElaboratedNetlist nl;
        try
        {
            circuit.CountElaboration();
            // In COUNTING mode (brief-sdl-6 D5): an instance already set to EM is recorded, not solved, here — the
            // planning elaboration describes the run, and a solve must not start before the run's count has been taken.
            using (EmEvaluationScope.Begin(countOnly: true))
                nl = new Elaborator(lib) { BaseDirectory = circuit.BaseDirectory }.Elaborate(tb);
        }
        catch (Exception ex)
        {
            return new RunPlan(RunStatus.EngineError, $"Elaboration failed: {ex.Message}");
        }
        // A draw that could not be made is a trial that did not evaluate (yield overview D7): some parameter
        // paths fall back to the verbatim text when evaluation throws, so the list is the reliable signal.
        if (request.Statistics is not null && nl.StatisticalProblems.Count > 0)
        {
            nl.Dispose();
            return new RunPlan(RunStatus.EngineError,
                Statistics.StatisticsDiagnostics.TrialDrawFailed(string.Join("; ", nl.StatisticalProblems)).Render());
        }

        // ── 3b. wBond coupling audit (WB30 / WB30a, R-wbb2-4) ──────────────────
        //
        // Coupling is computed only WITHIN a wBond, so two components mean the mutual inductance
        // between their wires is silently zero. With CouplingDomain deferred to v2 this audit is the
        // whole of the v1 safety mechanism, and its only remedy is manual — which is exactly why it
        // has to fire from the run rather than sit as a library anyone could forget to call.
        //
        // WB-B built and tested the audit, but NOTHING in the product called it: it was reachable
        // only from a hand-constructed netlist in a test. That was harmless while a wBond could not
        // be placed at all; placing a SECOND one is the moment it becomes reachable by an ordinary
        // user, which is this phase. It reports and never refuses — two wBonds that genuinely do not
        // interact are a legitimate design.
        try
        {
            CircuitRF.Core.Devices.WBondCouplingAudit.AuditAndWarn(nl);
        }
        catch
        {
            // An audit is advisory. It must never be the reason a run that would otherwise have
            // produced results does not.
        }

        // ── 4. Plan each analysis ──────────────────────────────────────────────
        var planned   = new List<PlannedAnalysis>();
        var lines     = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tops      = new List<Analysis>();
        long sweepPoints = 0;

        if (request.Analyses is { } names)
        {
            // The chains of the named cards. Named rather than indexed because the netlist is
            // re-extracted for every run and an index into tb.Analyses is not the index the panel row
            // had; a name that no longer resolves is reported, never silently widened to a full run.
            foreach (var onlyAnalysisName in names)
            {
                var one = tb.Analyses.FirstOrDefault(
                    a => string.Equals(a.Name, onlyAnalysisName, StringComparison.OrdinalIgnoreCase));

                if (one is null)
                    return new RunPlan(RunStatus.NoAnalysis,
                        $"Run this analysis: '{onlyAnalysisName}' is not in this schematic.");
                if (!one.Enabled)
                    return new RunPlan(RunStatus.NoAnalysis,
                        $"Run this analysis: '{one.Name}' is disabled — enable it to run it.");
                if (!AnalysisChain.IsChainRunnable(one, tb))
                    return new RunPlan(RunStatus.NoAnalysis,
                        $"Run this analysis: '{one.Name}' has nothing to run — the analysis it sweeps is disabled.");

                // The enabled sweeps WRAPPING the card run too: dispatching the card alone drops their
                // axes, and a one-point loadpull looks exactly like the swept one the user asked for.
                var dispatched = AnalysisChain.PromoteToRunnableTop(one, tb);
                if (!ReferenceEquals(dispatched, one))
                    notes.Add(AnalysisChain.PromotionNote(one, dispatched));

                // Two names in one chain run that chain once.
                if (!tops.Any(t => ReferenceEquals(t, dispatched))) tops.Add(dispatched);
            }
        }
        else
        {
            // Which chains run at all — a root nobody sweeps, resolved past disabled outer sweeps,
            // whose base is enabled. AnalysisChain owns that rule, and the CLI's run verbs and the
            // promotion above resolve the same list, so one card's run cannot land on a chain the
            // Run button would not have dispatched.
            tops.AddRange(AnalysisChain.RunnableTops(tb));
        }

        // The shared body of the whole-list case and the named case, so the two can never describe or
        // name the same analysis differently. In particular the result NAME is the chain's base
        // analysis either way, which is what lets one card's results land in the same group a full run
        // would have written.
        foreach (var top in tops)
        {
            var resultName = DeduplicateName(
                top is ParametricSweepAnalysis psa ? RootInnerName(psa, tb) : top.Name, usedNames);

            // Describing resolves expressions, so it can fail the same way running would. That is not
            // this method's error to report: plan the analysis anyway with a neutral line, and let
            // Execute hit the same failure and report it per-analysis exactly as it always has.
            string desc; long units; bool selfTicks;
            try
            {
                (desc, units, selfTicks) = DescribePlanned(top, nl, tb);
            }
            catch (Exception ex)
            {
                desc = $"{top.GetType().Name} '{top.Name}': cannot be described before running ({ex.Message})";
                units = 1; selfTicks = false;
            }

            // brief-sdl-6 R-sdl6-1/2: what the chain runs on when it runs by EM — its own bench, built once here.
            EmSwitched? em = null;
            string? emRefusal = null;
            bool wholeByEm = request.Em != false && EmModelSwitch.IsEm(top, tb, request.Em);
            if (wholeByEm || (request.Em != false && EmModelSwitch.HasInstancesSetToEm(tb, lib)))
            {
                em = EmModelSwitch.Apply(tb, lib, circuit.SourcePath ?? circuit.FilesRead.FirstOrDefault(),
                                         EmModelSwitch.SetupOf(top, tb), circuit.BaseDirectory, out emRefusal, wholeByEm);
                if (em is not null && wholeByEm) desc += " — by EM";
            }

            planned.Add(new PlannedAnalysis(top, null, resultName, units, selfTicks) { Em = em, EmRefusal = emRefusal });
            lines.Add(desc);
            if (top is ParametricSweepAnalysis) sweepPoints += units;
        }

        foreach (var raw in tb.RawDirectives)
        {
            if (request.Analyses is not null) break;   // cards were asked for, and a raw line is not one
            if (raw.Kind != "analysis" || !IsSparamRaw(raw.RawLine)) continue;
            try
            {
                var (name, start, stop, step) = ParseSparamDirective(raw.RawLine);
                var freqs = BuildFreqArrayFromBounds(start, stop, step);
                planned.Add(new PlannedAnalysis(null, raw.RawLine,
                    DeduplicateName(name, usedNames), freqs.Length, SelfTicks: true));
                lines.Add($"S-param '{name}': {freqs.Length} pts, {start / 1e9:G4}–{stop / 1e9:G4} GHz");
            }
            catch (Exception ex)
            {
                var label = FirstToken(raw.RawLine);
                planned.Add(new PlannedAnalysis(null, raw.RawLine,
                    DeduplicateName(label, usedNames), 1, SelfTicks: false));
                lines.Add($"S-param '{label}': cannot be described before running ({ex.Message})");
            }
        }

        if (planned.Count == 0)
            return new RunPlan(RunStatus.NoAnalysis, "No supported analysis dispatched.");

        // Saturating, because a plan deep enough to overflow is one nobody is going to run and a
        // negative denominator is a worse thing to show than a very large one.
        long total = 0;
        foreach (var p in planned)
        {
            if (p.WorkUnits <= 0) continue;
            if (total > long.MaxValue - p.WorkUnits) { total = long.MaxValue; break; }
            total += p.WorkUnits;
        }

        return new RunPlan(RunStatus.Success, $"{planned.Count} analysis run(s) planned",
                           lines, total, notes)
        {
            Lib = lib, Tb = tb, Nl = nl, BaseDirectory = circuit.BaseDirectory, Analyses = planned,
            SweepPoints = sweepPoints,
        };
    }

    /// <summary>
    /// Runs the analyses a plan described, evaluating the measurements — Simulate's second half.
    /// </summary>
    public static RunResult Execute(RunPlan plan, RunControl? control = null, Action<string>? breadcrumb = null)
        => Execute(plan, control, measurements: true, expressions: [], breadcrumb);

    /// <summary>
    /// Runs the analyses a plan described. A failed plan is passed straight through as the run's own
    /// outcome. Never throws — engine exceptions are captured into EngineError, and a cancelled run
    /// comes back as <see cref="RunStatus.Cancelled"/> carrying no results at all (see the remark
    /// below). The plan's elaborated netlist is disposed on the way out: a device an external provider
    /// supplies holds an instance in a worker process until it is, and an optimizer runs thousands of
    /// evaluations.
    /// </summary>
    public static RunResult Execute(RunPlan plan, RunControl? control, bool measurements,
                                    IReadOnlyList<string> expressions, Action<string>? breadcrumb,
                                    FieldsOutput fieldsOutput = FieldsOutput.Written, string? fieldsLabel = null)
    {
        if (plan.Status != RunStatus.Success
            || plan.Nl is not { } nl || plan.Tb is not { } tb || plan.Lib is not { } lib)
            return new RunResult(plan.Status, plan.StatusMessage);

        try
        {
            return ExecuteCore(plan, nl, tb, lib, control, measurements, expressions, breadcrumb,
                               new FieldsRun(fieldsOutput, fieldsLabel));
        }
        finally
        {
            nl.Dispose();
        }
    }

    /// <summary>brief-sar-10 — one evaluation's fields analyses: how they run, what they wrote, and the recombined field each
    /// leaves for the field measurements, by result name — open until the measurements are evaluated, then disposed.</summary>
    private sealed class FieldsRun(FieldsOutput output, string? label) : IDisposable
    {
        public FieldsOutput Output { get; } = output;
        public string? Label { get; } = label;
        public List<FieldsArtifact> Artifacts { get; } = [];
        public Dictionary<string, IFieldMetrics> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            foreach (var m in Metrics.Values) (m as IDisposable)?.Dispose();
            Metrics.Clear();
        }
    }

    private static RunResult ExecuteCore(RunPlan plan, ElaboratedNetlist nl, TestBench tb, Library lib,
                                         RunControl? control, bool measurements,
                                         IReadOnlyList<string> expressions, Action<string>? breadcrumb, FieldsRun fieldsRun)
    {
        using var disposeFields = fieldsRun;
        var results     = new List<AnalysisResult>();
        var fields      = fieldsRun.Artifacts;
        var notes       = new List<string>();
        var errors      = new List<string>();
        var convergence = new List<AnalysisConvergence>();

        foreach (var pa in plan.Analyses)
        {
            try
            {
                control?.ThrowIfCancellationRequested();
                if (control is not null) control.Stage = pa.ResultName;

                // Breadcrumb, not a message: this is the line a crash report carries when the process
                // dies inside the engine with no exception to catch (the GUI's crash reporter).
                breadcrumb?.Invoke($"run: begin '{pa.ResultName}' ({pa.WorkUnits} work unit(s))");

                // An engine that reports its own progress gets the real control; everything else gets
                // a cancellation-only child so nothing inside it counts work units twice, and this
                // level ticks the whole analysis once it is done.
                var inner = pa.SelfTicks ? control : control?.Child();

                bool? converged = true;
                if (pa.EmRefusal is { } emRefusal) throw new InvalidOperationException(emRefusal);
                var ds = pa.Em is { } em && pa.Typed is not null
                    ? RunByEm(pa, em, nl, notes, plan.BaseDirectory, inner, out converged, fieldsRun)
                    : pa.Typed is not null
                    ? RunTypedAnalysis(pa.Typed, nl, tb, lib, notes, plan.BaseDirectory, inner, out converged, fieldsRun, pa.ResultName)
                    : RunRawSparam(pa.RawLine!, nl, inner);

                if (ds is not null)
                {
                    results.Add(new AnalysisResult(pa.ResultName, ds));
                    string? detail = null;
                    if (pa.Typed is ParametricSweepAnalysis) converged = SweepConverged(ds, out detail);
                    if (detail is not null) nl.AddWarning($"'{pa.ResultName}': {detail}");
                    convergence.Add(new AnalysisConvergence(pa.ResultName, converged, detail));
                }
                if (!pa.SelfTicks) control?.Tick(pa.WorkUnits);
                breadcrumb?.Invoke($"run: end '{pa.ResultName}'");
            }
            catch (OperationCanceledException)
            {
                // NOTHING is published on cancel, including analyses that finished first. A run is
                // one artifact — the grouped DataSet a Data Display opens — and half of one, silently
                // missing whichever analyses had not started, is worse than none. The user asked to
                // stop; stopping is the whole answer.
                return new RunResult(RunStatus.Cancelled, "Run cancelled.",
                                     warnings: DrainWarnings(nl), notes: DrainNotes(nl));
            }
            catch (Exception ex)
            {
                errors.Add($"'{pa.ResultName}': {ex.Message}");
                convergence.Add(new AnalysisConvergence(pa.ResultName, false, ex.Message));
            }
        }

        // ── 4b. Assemble the one grouped run DataSet (group per analysis + measurements) ──
        DataSet? grouped = null;
        IReadOnlyList<ExpressionOutcome> outcomes = [];
        if (results.Count > 0)
        {
            grouped = new DataSet();
            foreach (var r in results)
                foreach (var kv in r.Data.Cubes)
                    grouped.AddToGroup(r.Name, kv.Key, kv.Value);

            if ((measurements && tb.Measurements.Count > 0) || expressions.Count > 0)
            {
                try
                {
                    var analysisResults = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in results) analysisResults[r.Name] = r.Data;

                    var measDs     = new DataSet();
                    var measErrors = new MeasurementEvaluator(tb, nl, analysisResults, fields: fieldsRun.Metrics)
                        .EvaluateInto(measDs, expressions, measurements, out outcomes);
                    foreach (var kv in measDs.Cubes)
                        grouped.AddToGroup(DataSet.MeasurementsGroup, kv.Key, kv.Value);  // reached even if some failed
                    foreach (var e in measErrors) errors.Add($"measurements: {e}");
                }
                catch (Exception ex) { errors.Add($"measurements: {ex.Message}"); }  // safety net
            }
        }

        // ── 5. Build outcome ───────────────────────────────────────────────────
        // Drain elaboration + engine run-time warnings from the netlist.
        IReadOnlyList<string> nlWarnings = DrainWarnings(nl);
        IReadOnlyList<string> nlNotes    = DrainNotes(nl);

        if (errors.Count > 0 && results.Count == 0)
            return new RunResult(RunStatus.EngineError,
                string.Join("; ", errors), warnings: nlWarnings, notes: nlNotes, convergence: convergence);

        if (results.Count == 0)
            return new RunResult(RunStatus.NoAnalysis,
                "No supported analysis dispatched.", warnings: nlWarnings, notes: nlNotes, convergence: convergence);

        var allNotes = new List<string>(notes);
        if (errors.Count > 0)
            foreach (var e in errors) allNotes.Add($"Error — {e}");

        var summary = allNotes.Count > 0
            ? string.Join("; ", allNotes)
            : $"{results.Count} analysis run(s) complete";
        return new RunResult(RunStatus.Success, summary, results, nlWarnings, grouped, nlNotes,
                             convergence, outcomes, fields);
    }

    /// <summary>
    /// brief-sdl-6 — one chain run on the bench <see cref="EmModelSwitch"/> built: elaborated there, with every EM
    /// evaluation it makes (each sweep point's included) recorded by the scope, and the result stamped with what produced
    /// it (R-sdl6-3). The run's own netlist receives the diagnostics, so the Messages panel sees them as for any analysis.
    /// </summary>
    private static DataSet? RunByEm(PlannedAnalysis pa, EmSwitched em, ElaboratedNetlist nl, List<string> notes,
                                    string? baseDirectory, RunControl? control, out bool? converged, FieldsRun fieldsRun)
    {
        ParametricEmProvider.Register();
        using var scope = EmEvaluationScope.Begin(control: control);
        // D5 — a sweep elaborates (and solves) at each of its own points, and this netlist only collects diagnostics; so it
        // is elaborated in counting mode, else the bench's own values would be solved once more than the count said.
        ElaboratedNetlist emNlBuilt;
        using (pa.Typed is ParametricSweepAnalysis ? EmEvaluationScope.Begin(countOnly: true, control: control, estimate: false) : null)
            emNlBuilt = new Elaborator(em.Lib) { BaseDirectory = baseDirectory }.Elaborate(em.Tb);
        using var emNl = emNlBuilt;
        var ds = RunTypedAnalysis(pa.Typed!, emNl, em.Tb, em.Lib, notes, baseDirectory, control, out converged, fieldsRun, pa.ResultName);
        nl.MergeDiagnosticsFrom(emNl);
        if (em.PathA is not null) notes.Add(em.Describe(pa.ResultName));
        foreach (var line in scope.Records.SelectMany(r => r.Unreached).Distinct()) nl.AddWarningOnce("em-unreached:" + line, line);
        foreach (var line in scope.Records.SelectMany(r => r.PlanNotes).Distinct()) nl.AddWarningOnce("em-plan:" + line, line);
        // brief-sdl-7 R-sdl7-7: a sweep may be studying exactly where the copper joins or parts, so such points are solved —
        // and said, against the first point's connectivity.
        foreach (var line in EmConnectivityChanges(scope.Records)) nl.AddWarningOnce("em-connectivity:" + line, line);
        return ds is null ? null : EmResultStamp.Stamp(ds, em, scope.Records);
    }

    /// <summary>brief-sdl-7 R-sdl7-7 — one sentence per evaluation whose copper connects differently from the first
    /// evaluation of the same instance.</summary>
    internal static IEnumerable<string> EmConnectivityChanges(IReadOnlyList<EmEvaluationRecord> records)
    {
        var first = new Dictionary<string, EmEvaluationRecord>(StringComparer.Ordinal);
        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Connectivity is not { } sig) continue;
            if (!first.TryGetValue(r.InstancePath, out var f)) { first[r.InstancePath] = r; continue; }
            if (sig.DifferenceFrom(f.Connectivity!) is { } diff && said.Add(r.Key ?? r.At))
                yield return $"{r.Cell} at {(r.At.Length == 0 ? "its own values" : r.At)}: the copper connects differently from the " +
                             $"first point{(f.At.Length == 0 ? "" : $" ({f.At})")} — {diff}. Solved as drawn.";
        }
    }

    /// <summary>
    /// brief-sdl-6 D5 — the EM evaluations a plan will make, at every point, solving nothing: one count over every
    /// chain the plan runs by EM. <see cref="EmSolveCountResult.None"/> when it runs nothing by EM.
    /// </summary>
    /// <param name="estimate">Whether each unsolved geometry's cost is estimated; a probe of geometry and connectivity
    /// alone (brief-sdl-7) says no, since the estimate meshes the layout.</param>
    public static EmSolveCountResult CountEmSolves(RunPlan plan, RunControl? control = null, bool estimate = true)
    {
        var records = new List<EmEvaluationRecord>();
        foreach (var pa in plan.Analyses)
        {
            if (pa.EmRefusal is { } refusal)
                records.Add(new EmEvaluationRecord(pa.ResultName, "", null, false, refusal, null, []));
            else if (pa.Em is { } em && pa.Typed is not null)
                records.AddRange(EmSolveCount.CountRecords(em.Tb, em.Lib, pa.Typed, plan.BaseDirectory, control, estimate));
        }
        return EmSolveCount.Summarise(records);
    }

    /// <summary>The refusal a run that needs more uncached solves than <paramref name="max"/> gets — before any is solved;
    /// null when within it. A refusal any evaluation would meet is said first, since no limit makes it runnable.</summary>
    public static string? EmLimitRefusal(EmSolveCountResult count, int max)
    {
        if (count.Refusals.Count > 0) return string.Join(" ", count.Refusals);
        if (count.ToSolve <= max) return null;
        return $"{count.Sentence} That is more than the limit of {max}; raise it with --em-max-solves (MCP: emMaxSolves), " +
               "or narrow the sweep. Nothing was solved.";
    }

    private static IReadOnlyList<string> DrainWarnings(ElaboratedNetlist nl)
        => nl.Warnings.Count > 0 ? [.. nl.Warnings] : [];

    private static IReadOnlyList<string> DrainNotes(ElaboratedNetlist nl)
        => nl.Notes.Count > 0 ? [.. nl.Notes] : [];

    // ── Pre-flight description ────────────────────────────────────────────────

    /// <summary>
    /// One line describing what <paramref name="analysis"/> is about to do, plus its work-unit count
    /// and whether its engine reports its own progress. Resolves expressions (frequency lists, tone
    /// frequencies, grid sizes) but runs nothing.
    /// </summary>
    private static (string Description, long WorkUnits, bool SelfTicks) DescribePlanned(
        Analysis analysis, ElaboratedNetlist nl, TestBench tb)
    {
        switch (analysis)
        {
            case SParameterAnalysis spa:
            {
                var freqs = spa.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                return ($"S-param '{spa.Name}': {freqs.Length} pts, " +
                        $"{freqs[0] / 1e9:G4}–{freqs[^1] / 1e9:G4} GHz " +
                        $"({spa.Sweeps.Count} segment(s))", freqs.Length, true);
            }

            case EnvelopeAnalysis env:
            {
                var plan = CircuitRF.Engine.Envelope.EnvelopeRunner.Resolve(env, nl.ResolvedGlobals);
                string segs = plan.Segments == 0 ? "auto" : plan.Segments.ToString(CultureInfo.InvariantCulture);
                return ("Envelope '" + env.Name + "': " + (plan.Params.StopS * 1e6).ToString("G4", CultureInfo.InvariantCulture)
                        + " us, x" + plan.Params.Oversample + " oversampling, segments " + segs, 1, false);
            }

            case HarmonicBalanceAnalysis hba:
            {
                var p     = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                var sweep = p.HasSweep ? $", sweep {p.SweepVarName}" : "";
                return ($"HB '{hba.Name}': f0={p.ToneHz / 1e9:G4} GHz, K={p.MaxHarmonic}{sweep}", 1, false);
            }

            case LoadpullAnalysis lpa:
            {
                var p = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                return ($"Loadpull '{lpa.Name}': f0={p.ToneHz / 1e9:G4} GHz, " +
                        $"{p.Grid.Points.Count} grid pts", p.Grid.Points.Count, true);
            }

            case LoadpullPursuitAnalysis lppa:
            {
                var p = LoadpullPursuitEngine.Resolve(lppa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                return ($"Loadpull-pursuit '{lppa.Name}': f0={p.LpParams.ToneHz / 1e9:G4} GHz", 1, false);
            }

            case ParametricSweepAnalysis psa:
                // The WHOLE chain, not just this analysis: the sweeps below this one are run by
                // ParametricSweepEngine's own re-elaboration loop and are never dispatched here, so
                // describing the dispatched analysis alone reports one axis for a run with several.
                return (ParametricSweepRunSummary.Describe(psa, tb),
                        ParametricSweepRunSummary.TotalPoints(psa, tb), true);

            case DcAnalysis:
                return ($"DC '{analysis.Name}': operating point", 1, false);

            case FieldsAnalysis fa:
                return ($"Fields '{fa.Name}': '{fa.Block}' recombined at {fa.FreqExpr} {fa.FreqUnit}, driven from " +
                        $"{(fa.DriveExpr.Contains(',') ? "ports" : "port")} {fa.DriveExpr}", 1, false);

            default:
                return ($"Analysis type '{analysis.GetType().Name}' is not dispatched.", 0, false);
        }
    }

    // ── Typed analysis dispatch ───────────────────────────────────────────────

    /// <summary>
    /// The <c>NDF=yes</c> half of an S-parameter directive (brief-wsprobe-6 R-wsp6-2), or null when
    /// the knob is off — and when it is off the engine takes exactly the path it took before the
    /// knob existed.
    ///
    /// <para>The passive netlist is supplied as a FACTORY bound to this run's own library and
    /// testbench, because the frequency-parallel path needs one per worker; the engine calls it
    /// serially, before any worker starts, and only when some device actually needs it.</para>
    /// </summary>
    private static NdfRequest? NdfRequestFor(
        SParameterAnalysis spa, Library lib, TestBench tb, string? baseDirectory)
    {
        if (!Analysis.ParseNdf(spa.NdfExpr)) return null;

        var vars   = NdfPassivation.ParseList(spa.PassiveVarsExpr);
        var pars   = NdfPassivation.ParseList(spa.PassiveParamsExpr);
        return new NdfRequest
        {
            PassiveVars    = vars,
            PassiveParams  = pars,
            PassiveNetlist = () => NdfPassivation.BuildPassiveNetlist(lib, tb, baseDirectory, vars, pars),
        };
    }

    /// <summary>
    /// A sweep's verdict, from the per-point <c>Converged</c> cube it stacks (MC-25 R-mc25-4): false when any point did not
    /// converge, with <paramref name="detail"/> naming those points by their sweep values; true when every point did; null
    /// when the inner analysis carries no such cube (an S-parameter sweep has nothing to converge). Before this a sweep
    /// always reported null, which every reader took as converged — the Model Composer example's power sweep failed six
    /// rungs with nothing saying so.
    /// </summary>
    internal static bool? SweepConverged(DataSet ds, out string? detail)
    {
        detail = null;
        if (!ds.Contains("Converged") || ds["Converged"] is not { DataKind: DataKind.Real } cube) return null;
        var values = cube.RealValues;
        var failed = new List<string>();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != 0.0) continue;
            // Row-major over the sweep axes, the innermost last.
            var parts = new string[cube.Rank];
            for (int d = cube.Rank - 1, rest = i; d >= 0; d--)
            {
                var axis = cube.Axes[d];
                int at = rest % axis.Length;
                rest /= axis.Length;
                string v = axis.Labels?[at] ?? axis.Values[at].ToString("G6", CultureInfo.InvariantCulture);
                parts[d] = $"{axis.Name} = {v}{(string.IsNullOrEmpty(axis.Unit) || axis.Labels is not null ? "" : " " + axis.Unit)}";
            }
            failed.Add(string.Join(", ", parts));
        }
        if (failed.Count == 0) return true;
        const int Named = 8;
        detail = $"did not converge at {failed.Count} of {values.Length} sweep point(s): " +
                 string.Join("; ", failed.Take(Named)) + (failed.Count > Named ? $"; and {failed.Count - Named} more" : "") + ".";
        return false;
    }

    private static DataSet? RunTypedAnalysis(
        Analysis          analysis,
        ElaboratedNetlist nl,
        TestBench         tb,
        Library           lib,
        List<string>      notes,
        string?           baseDirectory,
        RunControl?       control,
        out bool?         converged,
        FieldsRun?        fields = null,
        string?           resultName = null)
    {
        // A linear analysis has nothing to converge; a sweep and a loadpull carry convergence per point,
        // in their data. The two single solves below say for themselves.
        converged = analysis is SParameterAnalysis ? true : null;
        switch (analysis)
        {
            case SParameterAnalysis spa:
                // The frequency-parallel overload (SP-P3): lib/tb/baseDirectory let it elaborate a
                // netlist per worker, which is what makes splitting the grid safe. Short grids fall
                // back to the serial path inside the engine.
                return SParameterEngine.Run(
                    nl, lib, tb, baseDirectory,
                    spa.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit),
                    AnalysisSettings.Default.WithMarginThreshold(
                        Analysis.ParseMarginThresholdDb(spa.MarginThresholdExpr)),
                    control,
                    ndf: NdfRequestFor(spa, lib, tb, baseDirectory));

            case EnvelopeAnalysis env:
            {
                // Parallel-in-time: every segment runs on its own netlist (device models and sparse solvers carry
                // per-instance scratch), so the runner is handed an elaborator rather than this netlist.
                var plan = CircuitRF.Engine.Envelope.EnvelopeRunner.Resolve(env, nl.ResolvedGlobals);
                bool first = true;
                object gate = new();
                ElaboratedNetlist Elab()
                {
                    lock (gate)
                    {
                        if (first) { first = false; return nl; }
                        return new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
                    }
                }
                return CircuitRF.Engine.Envelope.EnvelopeRunner.Run(plan, Elab, AnalysisSettings.Default, control,
                                                                    n => nl.AddWarningOnce("env-note:" + n, n));
            }

            case HarmonicBalanceAnalysis hba:
            {
                var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                // lib/baseDirectory only so a long WSProbe tickle grid can be split across workers
                // (brief-wsprobe-8 R-wsp8-9), each on its own netlist copy — the same reason the
                // S-parameter case above takes them.
                var run = new HbEngine(nl, tb, null, null, lib, baseDirectory).Run(p);
                converged = run.Converged;
                return run.DataSet;
            }

            case LoadpullAnalysis lpa:
            {
                var p = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                // Post-process: add the derived display metrics (Pout_dBm, Zin, IRL, AMPM) so the
                // Data Display renders the same contours as a measured .spl (loadpull-postprocessor.md).
                return RfCore.Loadpull.LoadpullPostProcessor.Enrich(
                    new LoadpullEngine(nl, tb).Run(p, control));
            }

            case LoadpullPursuitAnalysis lppa:
            {
                var lpEngine      = new LoadpullEngine(nl, tb);
                var pursuitEngine = new LoadpullPursuitEngine(lpEngine);
                var p             = LoadpullPursuitEngine.Resolve(lppa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                return pursuitEngine.Run(p, control: control);
            }

            case ParametricSweepAnalysis psa:
                // `nl` is the netlist this service elaborated to pick the analysis with; a sweep
                // stamps a fresh one per point and disposes it, so this is the only route from an
                // engine diagnostic raised inside a sweep to the Messages panel.
                return ParametricSweepEngine.Run(psa, lib, tb,
                    baseDirectory: baseDirectory, control: control, diagnosticsInto: nl);

            // brief-sar-9 — an EM block's per-port fields recombined by this circuit's waves; no EM run
            case FieldsAnalysis fa:
                return CircuitRF.Design.Em3d.FieldCosimulation.Run(fa, nl, notes, control, fields?.Artifacts,
                    fields?.Output ?? FieldsOutput.Written, fields?.Label,
                    fields is null ? null : m => fields.Metrics[resultName ?? fa.Name] = m);

            case DcAnalysis:
            {
                var dc = NonlinearDcEngine.Run(nl);
                converged = dc.Converged;
                notes.Add($"DC '{analysis.Name}': {(dc.Converged ? "converged" : "did NOT converge")} " +
                          $"in {dc.Iterations} iter, residual={dc.FinalResidual:G3}");
                return DcResultPacker.Pack(dc, nl);
            }

            default:
                notes.Add($"Analysis type '{analysis.GetType().Name}' not dispatched.");
                return null;
        }
    }

    // ── Raw S-param directive dispatch ────────────────────────────────────────

    private static bool HasRawSparamDirective(TestBench tb)
    {
        foreach (var raw in tb.RawDirectives)
            if (raw.Kind == "analysis" && IsSparamRaw(raw.RawLine))
                return true;
        return false;
    }

    private static DataSet? RunRawSparam(string rawLine, ElaboratedNetlist nl, RunControl? control)
    {
        if (!IsSparamRaw(rawLine)) return null;

        var (_, start, stop, step) = ParseSparamDirective(rawLine);
        return SParameterEngine.Run(nl, BuildFreqArrayFromBounds(start, stop, step), null, control);
    }

    private static bool IsSparamRaw(string rawLine)
    {
        foreach (var t in rawLine.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
            if (t.Equals("type=sparam", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Parses "Name type=sparam  start=1 GHz  stop=10 GHz  step=1 GHz" into components.
    /// The value token may be followed by an optional frequency-unit token.
    /// </summary>
    private static (string Name, double Start, double Stop, double Step)
        ParseSparamDirective(string rawLine)
    {
        var tokens = rawLine.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
        string name  = tokens.Length >= 1 ? tokens[0] : "SP";
        double start = 1e9, stop = 10e9, step = 1e8;

        for (int i = 1; i < tokens.Length; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq < 0) continue;

            string key    = tokens[i][..eq].ToLowerInvariant();
            string valStr = tokens[i][(eq + 1)..];

            // Value may be in the next token when token is "start=".
            if (valStr.Length == 0 && i + 1 < tokens.Length && tokens[i + 1].IndexOf('=') < 0)
                valStr = tokens[++i];

            if (!double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                continue;

            // Optional frequency-unit suffix token.
            string? unit = null;
            if (i + 1 < tokens.Length && IsFreqUnit(tokens[i + 1]))
            {
                unit = tokens[++i];
            }

            double hz = val * FreqUnitScale(unit);
            switch (key)
            {
                case "start": start = hz; break;
                case "stop":  stop  = hz; break;
                case "step":  step  = hz; break;
            }
        }

        return (name, start, stop, step);
    }

    private static bool IsFreqUnit(string s) =>
        s.Equals("GHz", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("MHz", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("kHz", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Hz",  StringComparison.OrdinalIgnoreCase);

    private static double FreqUnitScale(string? unit) => unit?.ToUpperInvariant() switch
    {
        "GHZ" => 1e9,
        "MHZ" => 1e6,
        "KHZ" => 1e3,
        "HZ"  => 1.0,
        _     => 1.0,
    };

    // ── Frequency array builders ──────────────────────────────────────────────

    private static double[] BuildFreqArray(FrequencySpec freq,
        IReadOnlyDictionary<string, Value>? globals = null)
        => freq.Expand(globals);

    private static double[] BuildFreqArrayFromBounds(double start, double stop, double step)
    {
        if (step <= 0) step = (stop - start) / 100;
        var list = new List<double>();
        for (double f = start; f <= stop + step * 1e-9; f += step)
            list.Add(f);
        return [.. list];
    }

    private static string FirstToken(string s)
    {
        int sp = s.IndexOf(' ');
        return sp < 0 ? s : s[..sp];
    }

    // Walks the chain (skipping disabled sweeps) to the base analysis and returns its name.
    private static string RootInnerName(ParametricSweepAnalysis sweep, TestBench tb)
    {
        Analysis? cur = sweep;
        var guard = 0;
        while (cur is ParametricSweepAnalysis ps && guard++ < 64)
            cur = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
        return cur?.Name ?? sweep.Name;
    }

    // Within-run duplicate-name guard: appends _2, _3, … until the name is unique.
    private static string DeduplicateName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        int n = 2;
        string candidate;
        do { candidate = $"{name}_{n++}"; } while (!used.Add(candidate));
        return candidate;
    }
}
