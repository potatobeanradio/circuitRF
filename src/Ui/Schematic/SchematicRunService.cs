using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using RfCore.Data;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Outcome of a <see cref="SchematicRunService.RunNetlist"/> call.
/// </summary>
public enum RunStatus { Success, NoAnalysis, EngineError, Cancelled }

/// <summary>
/// A named DataSet produced by one analysis in a run.
/// </summary>
public sealed record AnalysisResult(string Name, DataSet Data);

/// <summary>
/// Result returned by <see cref="SchematicRunService.RunNetlist"/>: status, message, and
/// the collected per-analysis results (held for Phase 7 visualisation — not plotted here).
/// </summary>
public sealed class RunResult(
    RunStatus                        status,
    string                           statusMessage,
    IReadOnlyList<AnalysisResult>?   results  = null,
    IReadOnlyList<string>?           warnings = null,
    DataSet?                         grouped  = null,
    IReadOnlyList<string>?           notes    = null)
{
    public RunStatus                       Status        { get; } = status;
    public string                          StatusMessage { get; } = statusMessage;
    public IReadOnlyList<AnalysisResult>   Results       { get; } = results ?? [];

    /// <summary>
    /// Elaboration and engine run-time warnings drained from
    /// <see cref="CircuitRF.Core.Elaboration.ElaboratedNetlist.Warnings"/>.
    /// Non-empty even on <see cref="RunStatus.EngineError"/> when the run partially succeeded.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; } = warnings ?? [];

    /// <summary>
    /// What the run WORKED OUT and is reporting — drained from
    /// <see cref="CircuitRF.Core.Elaboration.ElaboratedNetlist.Notes"/>. Rendered at Info, because a
    /// resolution is not a complaint: a run that resolved everything correctly must not read as a
    /// run with problems, or the warnings that do need attention are harder to pick out for it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; } = notes ?? [];

    /// <summary>
    /// One grouped DataSet for the whole run (one group per analysis + "measurements" group).
    /// Null when no analyses produced results.
    /// </summary>
    public DataSet? GroupedResults { get; } = grouped;

    // Convenience: callers that only need the DataSets (unchanged from Phase 6e).
    public IReadOnlyList<DataSet> DataSets => Results.Select(r => r.Data).ToList();
}

/// <summary>
/// One analysis the run is going to dispatch, worked out by <see cref="SchematicRunService.Prepare"/>
/// before anything runs.
/// <para/>
/// <see cref="SelfTicks"/> distinguishes an engine that reports its OWN progress (a sweep per point,
/// an s-parameter per frequency, a loadpull per grid termination) from one that does not (a single HB
/// or DC solve, a pursuit search whose query count is decided by the search). The executor ticks
/// <see cref="WorkUnits"/> itself for the latter, so a run's total is reached either way.
/// </summary>
internal sealed record PlannedAnalysis(
    Analysis? Typed,
    string?   RawLine,
    string    ResultName,
    long      WorkUnits,
    bool      SelfTicks);

/// <summary>
/// What a run WILL do, worked out before any of it runs: the netlist read, elaborated once, and every
/// analysis that is going to be dispatched described in run order.
/// <para/>
/// <b>This exists so the user can read the plan and stop a wrong one before paying for it.</b> A
/// nested sweep can be tens of thousands of points and tens of minutes; reporting "11 pt(s) over VGS x
/// 101 pt(s) over VDS = 1,111 total pt(s)" only after those points have all been simulated is a
/// receipt, not a decision the user can act on.
/// <para/>
/// A failed plan (unreadable netlist, failed elaboration, nothing to run) carries its own status and
/// message; <see cref="SchematicRunService.Execute"/> passes it straight through, so a caller reports
/// exactly one failure whichever half produced it.
/// </summary>
public sealed class RunPlan
{
    public RunStatus Status        { get; }
    public string    StatusMessage { get; }

    /// <summary>One line per analysis that will run, in run order — the pre-flight description.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Total leaf work units across every planned analysis. 0 = nothing countable.</summary>
    public long TotalWorkUnits { get; }

    internal Library?          Lib           { get; init; }
    internal TestBench?        Tb            { get; init; }
    internal ElaboratedNetlist? Nl           { get; init; }
    internal string?           BaseDirectory { get; init; }
    internal IReadOnlyList<PlannedAnalysis> Analyses { get; init; } = [];

    internal RunPlan(RunStatus status, string message,
                     IReadOnlyList<string>? lines = null, long totalWorkUnits = 0)
    {
        Status         = status;
        StatusMessage  = message;
        Lines          = lines ?? [];
        TotalWorkUnits = totalWorkUnits;
    }
}

/// <summary>
/// Headless run service: reads a netlist.cnl → Elaborator → engine(s) → DataSet(s).
/// Mirrors the CLI engine chain exactly:
///   CnlReader.ReadFile → new Elaborator(lib).Elaborate(tb) → engine → DataSet
/// Called from WorkspaceViewModel.RunAnalysis after WriteNetlist writes the file.
/// Never throws — engine exceptions are captured into EngineError status.
/// </summary>
public static class SchematicRunService
{
    /// <summary>
    /// Reads and elaborates the netlist and describes every analysis that will be dispatched, WITHOUT
    /// running any of them. Cheap relative to the run (one parse plus one elaboration, against a sweep
    /// that re-elaborates per point), and the elaborated netlist is handed to
    /// <see cref="Execute"/> rather than being thrown away — so splitting the run in two costs nothing.
    /// Never throws.
    /// </summary>
    public static RunPlan Prepare(string netlistPath, string? baseDirectory = null)
    {
        // ── 1. Read ────────────────────────────────────────────────────────────
        Library  lib;
        TestBench tb;
        try
        {
            (lib, tb) = CnlReader.ReadFile(netlistPath);
        }
        catch (Exception ex)
        {
            return new RunPlan(RunStatus.EngineError, $"Netlist read failed: {ex.Message}");
        }

        // ── 2. Any analysis at all? ────────────────────────────────────────────
        bool hasTyped      = tb.Analyses.Count > 0;
        bool hasRawSparam  = HasRawSparamDirective(tb);
        if (!hasTyped && !hasRawSparam)
            return new RunPlan(RunStatus.NoAnalysis,
                "No analysis defined — add one to run.");

        // ── 3. Elaborate ───────────────────────────────────────────────────────
        ElaboratedNetlist nl;
        try
        {
            nl = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
        }
        catch (Exception ex)
        {
            return new RunPlan(RunStatus.EngineError, $"Elaboration failed: {ex.Message}");
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

        // A chain "root" is an analysis that no sweep references as its inner (the outermost level).
        // We dispatch exactly one effective top per chain; everything below runs via the engine.
        var referencedAsInner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
                referencedAsInner.Add(ps.InnerAnalysisName);

        foreach (var root in tb.Analyses)
        {
            if (referencedAsInner.Contains(root.Name)) continue;     // not a root — runs via its outer

            // Skip disabled OUTER sweeps to find the outermost thing that actually runs.
            var top = AnalysisChain.ResolveEffectiveTop(root, tb);
            if (top is null || !top.Enabled) continue;               // whole chain disabled
            if (!AnalysisChain.IsChainRunnable(top, tb)) continue;   // base analysis disabled → nothing runs

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

            planned.Add(new PlannedAnalysis(top, null, resultName, units, selfTicks));
            lines.Add(desc);
        }

        foreach (var raw in tb.RawDirectives)
        {
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

        return new RunPlan(RunStatus.Success, $"{planned.Count} analysis run(s) planned", lines, total)
        {
            Lib = lib, Tb = tb, Nl = nl, BaseDirectory = baseDirectory, Analyses = planned,
        };
    }

    /// <summary>
    /// Runs the analyses a <see cref="Prepare"/> call planned. A failed plan is passed straight
    /// through as the run's own outcome. Never throws — engine exceptions are captured into
    /// EngineError, and a cancelled run comes back as <see cref="RunStatus.Cancelled"/> carrying no
    /// results at all (see the remark below).
    /// </summary>
    public static RunResult Execute(RunPlan plan, RunControl? control = null)
    {
        if (plan.Status != RunStatus.Success
            || plan.Nl is not { } nl || plan.Tb is not { } tb || plan.Lib is not { } lib)
            return new RunResult(plan.Status, plan.StatusMessage);

        var results = new List<AnalysisResult>();
        var notes   = new List<string>();
        var errors  = new List<string>();

        foreach (var pa in plan.Analyses)
        {
            try
            {
                control?.ThrowIfCancellationRequested();
                if (control is not null) control.Stage = pa.ResultName;

                // Breadcrumb, not a message: this is the line a crash report carries when the process
                // dies inside the engine with no exception to catch (see Diagnostics/CrashReporter).
                Diagnostics.CrashReporter.Note(
                    $"run: begin '{pa.ResultName}' ({pa.WorkUnits} work unit(s))");

                // An engine that reports its own progress gets the real control; everything else gets
                // a cancellation-only child so nothing inside it counts work units twice, and this
                // level ticks the whole analysis once it is done.
                var inner = pa.SelfTicks ? control : control?.Child();

                var ds = pa.Typed is not null
                    ? RunTypedAnalysis(pa.Typed, nl, tb, lib, notes, plan.BaseDirectory, inner)
                    : RunRawSparam(pa.RawLine!, nl, inner);

                if (ds is not null) results.Add(new AnalysisResult(pa.ResultName, ds));
                if (!pa.SelfTicks) control?.Tick(pa.WorkUnits);
                Diagnostics.CrashReporter.Note($"run: end '{pa.ResultName}'");
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
            }
        }

        // ── 4b. Assemble the one grouped run DataSet (group per analysis + measurements) ──
        DataSet? grouped = null;
        if (results.Count > 0)
        {
            grouped = new DataSet();
            foreach (var r in results)
                foreach (var kv in r.Data.Cubes)
                    grouped.AddToGroup(r.Name, kv.Key, kv.Value);

            if (tb.Measurements.Count > 0)
            {
                try
                {
                    var analysisResults = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in results) analysisResults[r.Name] = r.Data;

                    var measDs    = new DataSet();
                    var measErrors = new MeasurementEvaluator(tb, nl, analysisResults).EvaluateInto(measDs);
                    foreach (var kv in measDs.Cubes)
                        grouped.AddToGroup("measurements", kv.Key, kv.Value);  // reached even if some failed
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
                string.Join("; ", errors), warnings: nlWarnings, notes: nlNotes);

        if (results.Count == 0)
            return new RunResult(RunStatus.NoAnalysis,
                "No supported analysis dispatched.", warnings: nlWarnings, notes: nlNotes);

        var allNotes = new List<string>(notes);
        if (errors.Count > 0)
            foreach (var e in errors) allNotes.Add($"Error — {e}");

        var summary = allNotes.Count > 0
            ? string.Join("; ", allNotes)
            : $"{results.Count} analysis run(s) complete";
        return new RunResult(RunStatus.Success, summary, results, nlWarnings, grouped, nlNotes);
    }

    /// <summary>
    /// Plan + execute in one call — the shape every caller had before the two halves were split, kept
    /// so a caller that has no use for the plan (a test, a headless driver) needs nothing new.
    /// </summary>
    public static RunResult RunNetlist(string netlistPath, string? baseDirectory = null,
                                       RunControl? control = null)
        => Execute(Prepare(netlistPath, baseDirectory), control);

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

            default:
                return ($"Analysis type '{analysis.GetType().Name}' is not dispatched.", 0, false);
        }
    }

    // ── Typed analysis dispatch ───────────────────────────────────────────────

    private static DataSet? RunTypedAnalysis(
        Analysis          analysis,
        ElaboratedNetlist nl,
        TestBench         tb,
        Library           lib,
        List<string>      notes,
        string?           baseDirectory,
        RunControl?       control)
    {
        switch (analysis)
        {
            case SParameterAnalysis spa:
                // The frequency-parallel overload (SP-P3): lib/tb/baseDirectory let it elaborate a
                // netlist per worker, which is what makes splitting the grid safe. Short grids fall
                // back to the serial path inside the engine.
                return SParameterEngine.Run(
                    nl, lib, tb, baseDirectory,
                    spa.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit), null, control);

            case HarmonicBalanceAnalysis hba:
            {
                var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
                return new HbEngine(nl, tb).Run(p).DataSet;
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
                return ParametricSweepEngine.Run(psa, lib, tb,
                    baseDirectory: baseDirectory, control: control);

            case DcAnalysis:
            {
                var dc = NonlinearDcEngine.Run(nl);
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
