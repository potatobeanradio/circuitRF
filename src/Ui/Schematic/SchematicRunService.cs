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
public enum RunStatus { Success, NoAnalysis, EngineError }

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
    IReadOnlyList<string>?           warnings = null)
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

    // Convenience: callers that only need the DataSets (unchanged from Phase 6e).
    public IReadOnlyList<DataSet> DataSets => Results.Select(r => r.Data).ToList();
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
    /// Runs all analyses declared in the netlist at <paramref name="netlistPath"/>.
    /// Returns Success with DataSets, NoAnalysis when nothing is declared,
    /// or EngineError when an engine exception occurs.  Never throws.
    /// </summary>
    public static RunResult RunNetlist(string netlistPath)
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
            return new RunResult(RunStatus.EngineError, $"Netlist read failed: {ex.Message}");
        }

        // ── 2. Any analysis at all? ────────────────────────────────────────────
        bool hasTyped      = tb.Analyses.Count > 0;
        bool hasRawSparam  = HasRawSparamDirective(tb);
        if (!hasTyped && !hasRawSparam)
            return new RunResult(RunStatus.NoAnalysis,
                "No analysis defined — add one to run.");

        // ── 3. Elaborate ───────────────────────────────────────────────────────
        ElaboratedNetlist nl;
        try
        {
            nl = new Elaborator(lib).Elaborate(tb);
        }
        catch (Exception ex)
        {
            return new RunResult(RunStatus.EngineError, $"Elaboration failed: {ex.Message}");
        }

        // ── 4. Dispatch each analysis ──────────────────────────────────────────
        var results   = new List<AnalysisResult>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes     = new List<string>();
        var errors    = new List<string>();

        // Names that are wrapped as the inner of a parametric sweep — run only via their sweep.
        var innerOfSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
                innerOfSweep.Add(ps.InnerAnalysisName);

        foreach (var analysis in tb.Analyses)
        {
            if (!analysis.Enabled) continue;              // disabled — in tb for chain lookup only
            if (innerOfSweep.Contains(analysis.Name)) continue; // runs only via its wrapping sweep

            try
            {
                var ds = RunTypedAnalysis(analysis, nl, tb, lib, notes);
                if (ds is not null)
                {
                    var resultName = analysis is ParametricSweepAnalysis psa
                        ? RootInnerName(psa, tb)
                        : analysis.Name;
                    results.Add(new AnalysisResult(DeduplicateName(resultName, usedNames), ds));
                }
            }
            catch (Exception ex)
            {
                errors.Add($"'{analysis.Name}': {ex.Message}");
            }
        }

        foreach (var raw in tb.RawDirectives)
        {
            if (raw.Kind != "analysis") continue;
            try
            {
                var rawName = FirstToken(raw.RawLine);
                if (TryRunRawSparam(raw.RawLine, nl, notes, out var ds) && ds is not null)
                    results.Add(new AnalysisResult(DeduplicateName(rawName, usedNames), ds));
            }
            catch (Exception ex)
            {
                var label = FirstToken(raw.RawLine);
                errors.Add($"'{label}': {ex.Message}");
            }
        }

        // ── 5. Build outcome ───────────────────────────────────────────────────
        // Drain elaboration + engine run-time warnings from the netlist.
        IReadOnlyList<string> nlWarnings = nl.Warnings.Count > 0
            ? [.. nl.Warnings]
            : [];

        if (errors.Count > 0 && results.Count == 0)
            return new RunResult(RunStatus.EngineError,
                string.Join("; ", errors), warnings: nlWarnings);

        if (results.Count == 0)
            return new RunResult(RunStatus.NoAnalysis,
                "No supported analysis dispatched.", warnings: nlWarnings);

        var allNotes = new List<string>(notes);
        if (errors.Count > 0)
            foreach (var e in errors) allNotes.Add($"Error — {e}");

        var summary = allNotes.Count > 0
            ? string.Join("; ", allNotes)
            : $"{results.Count} analysis run(s) complete";
        return new RunResult(RunStatus.Success, summary, results, nlWarnings);
    }

    // ── Typed analysis dispatch ───────────────────────────────────────────────

    private static DataSet? RunTypedAnalysis(
        Analysis          analysis,
        ElaboratedNetlist nl,
        TestBench         tb,
        Library           lib,
        List<string>      notes)
    {
        switch (analysis)
        {
            case SParameterAnalysis spa:
            {
                var freqs = spa.Expand(nl.ResolvedGlobals);
                notes.Add($"S-param '{spa.Name}': {freqs.Length} pts, " +
                          $"{freqs[0] / 1e9:G4}–{freqs[^1] / 1e9:G4} GHz " +
                          $"({spa.Sweeps.Count} segment(s))");
                return SParameterEngine.Run(nl, freqs);
            }

            case HarmonicBalanceAnalysis hba:
            {
                var p     = HbEngine.Resolve(hba, nl.ResolvedGlobals);
                var sweep = p.HasSweep ? $", sweep {p.SweepVarName}" : "";
                notes.Add($"HB '{hba.Name}': f0={p.ToneHz / 1e9:G4} GHz, K={p.MaxHarmonic}{sweep}");
                return new HbEngine(nl, tb).Run(p).DataSet;
            }

            case LoadpullAnalysis lpa:
            {
                var p = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals);
                notes.Add($"Loadpull '{lpa.Name}': f0={p.ToneHz / 1e9:G4} GHz, " +
                          $"{p.Grid.Points.Count} grid pts");
                return new LoadpullEngine(nl, tb).Run(p);
            }

            case LoadpullPursuitAnalysis lppa:
            {
                var lpEngine      = new LoadpullEngine(nl, tb);
                var pursuitEngine = new LoadpullPursuitEngine(lpEngine);
                var p             = LoadpullPursuitEngine.Resolve(lppa, nl.ResolvedGlobals);
                notes.Add($"Loadpull-pursuit '{lppa.Name}': f0={p.LpParams.ToneHz / 1e9:G4} GHz");
                return pursuitEngine.Run(p);
            }

            case ParametricSweepAnalysis psa:
            {
                notes.Add($"Parametric sweep '{psa.Name}': {psa.SweepValues.Length} pt(s) over {psa.SweepVarName}");
                return ParametricSweepEngine.Run(psa, lib, tb);
            }

            case DcAnalysis:
                notes.Add($"DC analysis '{analysis.Name}': not wired in-app yet — use CLI dc command.");
                return null;

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

    private static bool TryRunRawSparam(
        string            rawLine,
        ElaboratedNetlist nl,
        List<string>      notes,
        out DataSet?      ds)
    {
        ds = null;
        if (!IsSparamRaw(rawLine)) return false;

        var (name, start, stop, step) = ParseSparamDirective(rawLine);
        var freqs = BuildFreqArrayFromBounds(start, stop, step);
        notes.Add($"S-param '{name}': {freqs.Length} pts, {start / 1e9:G4}–{stop / 1e9:G4} GHz");
        ds = SParameterEngine.Run(nl, freqs);
        return true;
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

    // Walks InnerAnalysisName down to the first non-sweep analysis and returns its name.
    private static string RootInnerName(ParametricSweepAnalysis sweep, TestBench tb)
    {
        Analysis? cur = sweep;
        var guard = 0;
        while (cur is ParametricSweepAnalysis ps && guard++ < 64)
            cur = tb.Analyses.FirstOrDefault(a => a.Name == ps.InnerAnalysisName);
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
