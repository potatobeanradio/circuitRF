using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Results;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf opt &lt;path.csch|path.cnl&gt;</c> — the Optimizer window's run, headless
/// (brief-tuneopt-11, overview D14/D15; <c>cli.md</c> §24).
///
/// <para><b>It owns no optimization.</b> The run is <see cref="OptimizationRun"/> in
/// <c>src/Design/Optimization</c>, the object the Optimizer panel drives, over the same
/// <see cref="PreparedCircuit"/> Simulate prepares; the best point's full results come out of
/// <see cref="CircuitEvaluation.Evaluate"/>, as the panel's finish re-evaluates it. What is here is
/// argument parsing, the flag overrides of the file's <c>optimize</c> settings, the two narrowing
/// flags, reporting, and the one opt-in write.</para>
///
/// <para><b>It writes nothing to the design's values (D14).</b> It reports them — as the text the
/// schematic would hold, so a caller can write one into the file. <c>--save-preset</c> is the one
/// write: a preset added to a <c>.csch</c>'s tuning block, after a history checkpoint when the
/// workspace keeps history, and every other byte of the file left as it was.</para>
///
/// <para><b>Exit codes (D15):</b> 0 every enabled goal met · 3 finished with a goal unmet · 1 refused ·
/// 2 no evaluation converged · 130 cancelled, writing nothing. 3 exists so a script can tell "ran and
/// missed the spec" from "could not run".</para>
/// </summary>
internal static class Optimize
{
    private const string Verb = "opt";

    public static int Run(string[] args)
    {
        string? input = null, output = null, historyPath = null, presetName = null;
        string? algorithm = null, cost = null, analyses = null, time = null;
        int? maxIter = null, maxEvals = null, parallel = null, seed = null;
        List<string>? vars = null, goals = null;
        string? corners = null, em = null;
        int? emBudget = null;
        bool snap = false, sensitivity = false, showIterations = false, apply = false;
        var sets = new List<(string Name, string Expr)>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "--algorithm" when hasValue:  algorithm = args[++i]; break;
                case "--cost" when hasValue:       cost = args[++i]; break;
                case "--analyses" when hasValue:   analyses = args[++i]; break;
                case "--time" when hasValue:       time = args[++i]; break;
                case "--max-iter" when hasValue:   if (!Int(a, args[++i], out maxIter, out int r1)) return r1; break;
                case "--max-evals" when hasValue:  if (!Int(a, args[++i], out maxEvals, out int r2)) return r2; break;
                case "--parallel" when hasValue:   if (!Int(a, args[++i], out parallel, out int r3)) return r3; break;
                case "--seed" when hasValue:       if (!Int(a, args[++i], out seed, out int r4)) return r4; break;
                case "--vars" when hasValue:       vars = List(args[++i]); break;
                case "--goals" when hasValue:      goals = List(args[++i]); break;
                case "--corners" when hasValue:    corners = args[++i]; break;
                case "--snap":                     snap = true; break;
                // brief-sdl-7: the optimize line's em= and emBudget=, and Apply (R-sdl7-8).
                case "--em" when hasValue:         em = args[++i].Trim().ToLowerInvariant(); break;
                case "--em-budget" when hasValue:  if (!Int(a, args[++i], out emBudget, out int r5)) return r5; break;
                case "--apply":                    apply = true; break;
                case "--sensitivity":              sensitivity = true; break;
                case "--show-iterations":          showIterations = true; break;
                case "-o" or "--output" when hasValue: output = args[++i]; break;
                case "--history" when hasValue:    historyPath = args[++i]; break;
                case "--save-preset" when hasValue: presetName = args[++i]; break;
                case "--set" when hasValue:
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed(Verb, kv));
                    sets.Add((kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    break;
                }
                default:
                    if (a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.RunUnknownOption(Verb, a));
                    if (input is not null) return JsonRun.Fail(CliDiagnostics.RunMultipleInputs(Verb, a));
                    input = a;
                    break;
            }
        }

        if (input is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.InputRequired(Verb, ".csch or .cnl"));
            Usage();
            return code;
        }
        JsonRun.InputPath = input;
        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

        var kind = DocumentKinds.Classify(input);
        if (kind is not (DocumentKind.Netlist or DocumentKind.Schematic))
            return JsonRun.Fail(CliDiagnostics.RunWrongDocumentKind(Verb, input, DocumentKinds.Name(kind)));
        if (presetName is not null && kind != DocumentKind.Schematic)
            return JsonRun.Fail(CliDiagnostics.OptSavePresetNeedsSchematic(input));
        if (apply && kind != DocumentKind.Schematic)
            return JsonRun.Fail(CliDiagnostics.OptApplyNeedsSchematic(input));
        foreach (var (flag, path) in new[] { ("-o", output), ("--history", historyPath) })
            if (path is not null && !path.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
                return JsonRun.Fail(CliDiagnostics.OptOutputNotNpy(flag, path));

        // ── 1. The circuit, prepared as Simulate prepares it ──────────────────
        string full = Path.GetFullPath(input);
        var circuit = kind == DocumentKind.Schematic
            ? CircuitSource.PrepareSchematic(full)
            : PreparedCircuit.FromFile(full, Path.GetDirectoryName(full));
        if (circuit.ReadError is { } readError || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
            return JsonRun.Fail(CliDiagnostics.RunFailed(circuit.ReadError ?? "the design could not be read"));

        // ── 2. The file's setup, with this run's flags over it ────────────────
        var setup = tb.Tuning?.Clone() ?? new TuningSetup();
        var settings = setup.Optimizer ??= new OptimizerSettings();
        if (algorithm is not null) settings.Algorithm = algorithm;
        if (maxIter is not null)   settings.MaxIterations = maxIter;
        if (maxEvals is not null)  settings.MaxEvaluations = maxEvals;
        if (parallel is not null)  settings.Parallelism = parallel;
        if (seed is not null)      settings.Seed = seed;
        if (time is not null)
        {
            // A bare number is seconds; a unit may follow, as the optimize line's timelimit= takes one.
            string text = double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? time + " s" : time;
            if (TuningValidator.TimeLimitSeconds(text) is null)
                return JsonRun.Fail(CliDiagnostics.OptFlagValue("--time", time, TuningValidator.TimeLimitSpelling));
            settings.TimeLimit = text;
        }
        if (cost is not null)
        {
            if (cost is not ("lsq" or "minimax")) return JsonRun.Fail(CliDiagnostics.OptFlagValue("--cost", cost, "lsq or minimax"));
            settings.Cost = cost == "minimax" ? OptimizerCost.Minimax : OptimizerCost.LeastSquares;
        }
        if (analyses is not null)
        {
            if (analyses is not ("goals" or "all")) return JsonRun.Fail(CliDiagnostics.OptFlagValue("--analyses", analyses, "goals or all"));
            settings.Scope = analyses == "all" ? OptimizerScope.All : OptimizerScope.GoalAnalyses;
        }

        if (corners is not null)
        {
            // none, all or names — the optimize line's corners= (brief-yield-7 R-ya7-6); a name the design lacks is
            // the run's own refusal, which lists the enabled corners.
            var names = List(corners);
            if (names.Count == 0 || names.Any(n => n.Contains(' ')))
                return JsonRun.Fail(CliDiagnostics.OptFlagValue("--corners", corners, "none, all or corner names separated by commas"));
            settings.Corners = corners.Trim().ToLowerInvariant() switch
            {
                "none" => null,
                "all"  => "all",
                _      => string.Join(',', names),
            };
        }

        if (em is not null)
        {
            int at = Array.IndexOf([.. AnalysisDirectiveSchema.EmModeTokens], em);
            if (at < 0) return JsonRun.Fail(CliDiagnostics.OptFlagValue("--em", em, string.Join(", ", AnalysisDirectiveSchema.EmModeTokens)));
            settings.Em = (OptimizerEmMode)at;
        }
        if (emBudget is not null) settings.EmBudget = emBudget;

        var catalog = TunableCatalog.FromNetlist(tb, lib, parts: circuit.PartTunables);
        if (vars is not null && NarrowVariables(setup, catalog, vars) is { } varRefusal) return JsonRun.Fail(varRefusal);
        if (goals is not null && NarrowGoals(setup, goals) is { } goalRefusal) return JsonRun.Fail(goalRefusal);

        if (presetName is not null && TuningPresets.NameProblem(tb.Tuning, -1, presetName) is { } nameProblem)
            return JsonRun.Fail(CliDiagnostics.OptPresetName(nameProblem));

        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── 3. The run ────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        int enabledGoals = setup.Goals.Count(g => g.Enabled);
        // The final result is the answer; each iteration is reported only when asked for — a line on
        // stderr, an entry in the document's perIteration and, over the protocol, a progress
        // notification. Unasked, a long run would hand a client hundreds of lines it did not want.
        var perIteration = new List<OptimizeIterationJson>();
        // The run statistical corners came from, when it is beside the design: its recorded z-vectors replay as they
        // stand (brief-yield-7 R-ya7-3), as yield corners replays them.
        DataSet? recorded = null;
        string yieldFile = Design.Statistics.StatisticalRun.ResultPathFor(full);
        if (settings.CornerNames is not { Count: 0 } && File.Exists(yieldFile))
            try { recorded = DataSetImporter.Import(yieldFile).DataSet; } catch (Exception) { /* drawn afresh, with its note */ }

        var run = OptimizationRun.Create(circuit, new OptimizationOptions
        {
            Setup         = setup,
            Recorded      = recorded,
            // brief-sdl-7 D4 — the budget in force is said once the run has checked its start: a 3D EM view's lower
            // default is only known then.
            EmBudgetKnown = (mode, budget) =>
                Console.Error.WriteLine($"[circuitRF] by EM ({AnalysisDirectiveSchema.EmModeTokens[(int)mode]}): at most {budget} " +
                                        $"EM solve{(budget == 1 ? "" : "s")}; geometries already in the EM cache are free"),
            Sets          = sets,
            Cancellation  = ct,
            SnapAndPolish = snap,
            Progress      = !showIterations ? null : p =>
            {
                string line = ProgressLine(p, enabledGoals);
                Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, p.Iteration, 0));
                perIteration.Add(new OptimizeIterationJson(
                    p.Iteration, p.Evaluations, Finite(p.BestCost), p.Goals.Count(g => g.Met), p.Failures, p.Infeasible,
                    p.Stage, p.BestValues.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)));
            },
        });
        if (run.Refusal is { } refusal) return JsonRun.Fail(refusal);
        foreach (var note in run.Notes) Report(note);

        Console.Error.WriteLine($"[circuitRF] optimizing {run.Variables!.Coordinates.Count} variable(s) against " +
                                $"{enabledGoals} goal(s) with {run.AlgorithmId}" +
                                (run.EvaluationsPerPoint > 1
                                    ? $" at {string.Join(", ", run.CornerNames)} ({run.EvaluationsPerPoint} evaluations per point)" : ""));
        var result = run.Run();
        foreach (var note in result.Notes.Skip(run.Notes.Count)) Report(note);

        if (result.Outcome == OptimizationOutcome.Cancelled)
        {
            JsonRun.Report(CliDiagnostics.OptCancelled());
            return 130;
        }
        if (result.Outcome == OptimizationOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed(result.FinishReason));

        SensitivityReport? sens = null;
        if (sensitivity && result.BestPoint is not null)
        {
            try { sens = run.Sensitivity(null, ct); }
            catch (OperationCanceledException) { JsonRun.Report(CliDiagnostics.OptCancelled()); return 130; }
        }

        // ── 4. Files: the best point's full results, the history, the preset ──
        int exit = result.ExitCode;
        if (result.Outcome == OptimizationOutcome.NoConvergence && result.Refusal is { } nc) JsonRun.Report(nc);

        if (output is not null && result.BestValues.Count > 0)
        {
            // As the run evaluated: an em=off run's best point is written by circuit models, not solved by EM afterwards.
            var rr = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = result.BestValues, Sets = sets, Em = run.EvaluatesEm });
            if (ct.IsCancellationRequested) { JsonRun.Report(CliDiagnostics.OptCancelled()); return 130; }
            var data = rr.Status == RunStatus.Success && rr.GroupedResults is { } grouped ? grouped : new DataSet();
            if (rr.Status != RunStatus.Success) JsonRun.Report(CliDiagnostics.RunFailed(rr.StatusMessage));
            Write(TunedProvenance.Stamp(Merge(data, result.History), result.BestValues), output);
        }
        if (historyPath is not null && result.History is { } history) Write(history, historyPath);

        // brief-sdl-7 R-sdl7-8: Apply — only an EM-verified best, into the schematic and its layout, after a checkpoint.
        EmApplyResult? applied = null;
        if (apply && result.BestValues.Count > 0)
        {
            if (result.EmMode == OptimizerEmMode.Off) JsonRun.Report(CliDiagnostics.OptApplyNotEm());
            else if (CheckpointBefore(full, "opt --apply") is { } failed) { JsonRun.Report(failed); exit = 1; }
            else
            {
                applied = EmApply.Apply(full, result.BestValues, result.BestEmKey, result.EmSetup);
                Console.Error.WriteLine($"[circuitRF] {applied.Sentence}");
                if (!applied.Succeeded) exit = 1;   // brief-sdl-10: nothing laid out to check is a success
                else JsonRun.AddOutput("schematic", full);
            }
        }

        string? saved = null;
        if (presetName is not null && result.BestValues.Count > 0)
        {
            if (SavePreset(full, presetName.Trim(), result) is { } failed) { JsonRun.Report(failed); exit = 1; }
            else saved = presetName.Trim();
        }

        // ── 5. The report ─────────────────────────────────────────────────────
        var report = Project(input, run, result, catalog, sens, saved, showIterations ? perIteration : null, applied);
        JsonRun.Optimize = report;
        Print(report);
        return exit;
    }

    // ── flags ──────────────────────────────────────────────────────────────────

    private static bool Int(string flag, string text, out int? value, out int refusal)
    {
        refusal = 0;
        value   = null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0) { value = v; return true; }
        refusal = JsonRun.Fail(CliDiagnostics.OptFlagValue(flag, text, "a positive whole number"));
        return false;
    }

    private static List<string> List(string text)
        => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary><c>--vars</c>: only these of the file's opt-enabled entries are optimized; the others
    /// keep their ranges (a complex value's ranges hold together, D18) and stay at the design's value.</summary>
    private static Diagnostic? NarrowVariables(TuningSetup setup, TunableCatalog catalog, List<string> keys)
    {
        var optimized = setup.Variables.Where(e => e.Opt).Select(e => e.Key).ToList();
        foreach (var key in keys)
        {
            if (optimized.Contains(key, StringComparer.Ordinal)) continue;
            // A whole complex value is never a variable: name its parts (D18).
            if (catalog.PartsOf(key).Count > 0 || optimized.Any(k => TunableKey.TryParse(k, out var tk) && tk.Part is not null && tk.Whole.ToString() == key))
                return CliDiagnostics.OptVarsWholeComplex(key, string.Join(", ",
                    new[] { ComplexPart.Real, ComplexPart.Imag, ComplexPart.Mag, ComplexPart.Phase }.Select(p => TunableKey.PartKey(key, p))));
            return CliDiagnostics.OptVarsNotOptimized(key, optimized.Count == 0 ? "none" : string.Join(", ", optimized));
        }
        foreach (var e in setup.Variables.Where(e => e.Opt && !keys.Contains(e.Key, StringComparer.Ordinal))) e.Opt = false;
        return null;
    }

    /// <summary><c>--goals</c>: only these of the file's enabled goals count.</summary>
    private static Diagnostic? NarrowGoals(TuningSetup setup, List<string> names)
    {
        var enabled = setup.Goals.Where(g => g.Enabled).Select(g => g.Name).ToList();
        foreach (var name in names)
            if (!enabled.Contains(name, StringComparer.Ordinal))
                return CliDiagnostics.OptGoalsUnknown(name, enabled.Count == 0 ? "none" : string.Join(", ", enabled));
        foreach (var g in setup.Goals.Where(g => g.Enabled && !names.Contains(g.Name, StringComparer.Ordinal))) g.Enabled = false;
        return null;
    }

    private static void Report(Diagnostic d)
    {
        string prefix = d.Severity switch { DiagnosticSeverity.Warning => "warning: ", DiagnosticSeverity.Error => "", _ => "note: " };
        Console.Error.WriteLine(prefix + d.Render());
        JsonRun.Note(d);
    }

    /// <summary>One iteration, as stderr prints it and an MCP progress notification carries it.</summary>
    internal static string ProgressLine(OptimizationProgress p, int enabledGoals)
    {
        var parts = new List<string>
        {
            $"iter {p.Iteration}",
            $"{p.Evaluations} evals",
            $"best cost {(double.IsFinite(p.BestCost) ? p.BestCost.ToString("G4", CultureInfo.InvariantCulture) : "—")}",
            $"goals met {p.Goals.Count(g => g.Met)}/{enabledGoals}",
        };
        if (p.Failures > 0)   parts.Add($"{p.Failures} failed");
        if (p.Infeasible > 0) parts.Add($"{p.Infeasible} infeasible");
        if (p.Stage is { } s) parts.Add(s);
        return string.Join(" · ", parts);
    }

    // ── files ──────────────────────────────────────────────────────────────────

    /// <summary>The best point's results with the run's <c>opt</c> history group beside them.</summary>
    private static DataSet Merge(DataSet data, DataSet? history)
    {
        if (history is null) return data;
        foreach (var group in history.Groups)
            foreach (var (name, cube) in history.CubesIn(group))
                data.AddToGroup(group, name, cube);
        return data;
    }

    private static void Write(DataSet ds, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        DataSetExporter.Export(ds, path, ExportFormat.Npy, new ExportOptions(Format: ExportFormat.Npy));
        Console.WriteLine($"Wrote {path}");
        JsonRun.AddOutput(JsonRun.KindOf(path), path);
    }

    /// <summary>
    /// <c>--save-preset</c> (D14): the best values as a new preset in the schematic's tuning block —
    /// <see cref="TuningPresets.LockIn"/>, the Optimizer's own Lock in — after a checkpoint when the
    /// workspace keeps a history. The file is re-read and re-written through its own persistence, so
    /// every other byte is what that persistence writes for it.
    /// </summary>
    private static Diagnostic? SavePreset(string cschPath, string name, OptimizationResult result)
    {
        if (CheckpointBefore(cschPath, $"opt --save-preset {name}") is { } failed) return failed;

        var (model, view, cellName) = SchematicPersistence.LoadFromFile(cschPath);
        var (next, _) = TuningPresets.LockIn(model.Tuning, result.BestValues, DateTime.UtcNow, result.BestCost, name);
        model.Tuning = next;
        SchematicPersistence.SaveToFile(cschPath, model, cellName, view.PanX, view.PanY, view.Zoom);
        Console.WriteLine($"Saved preset \"{name}\" to {cschPath}");
        JsonRun.AddOutput("schematic", cschPath);
        return null;
    }

    /// <summary>A history checkpoint before a headless write to <paramref name="cschPath"/>, when its workspace
    /// keeps a history — what <c>opt --save-preset</c> and <c>yield --save-preset|--save-corner</c> take first.
    /// The checkpoint's own error when it could not be taken; null otherwise.</summary>
    internal static Diagnostic? CheckpointBefore(string cschPath, string intent)
        => WorkspaceCheckpoints.BeforeWrite(cschPath, intent);

    // ── the report ─────────────────────────────────────────────────────────────

    private static OptimizeReportJson Project(string input, OptimizationRun run, OptimizationResult r,
                                              TunableCatalog catalog, SensitivityReport? sens, string? saved,
                                              IReadOnlyList<OptimizeIterationJson>? perIteration, EmApplyResult? applied = null)
    {
        var rows = new List<OptimizeVariableJson>();
        foreach (var c in run.Variables!.Coordinates)
        {
            var t = catalog.Find(c.Key);
            string start = t?.ValueText ?? "";
            string? best = null, whole = null, wholeStart = null;
            if (r.BestValues.TryGetValue(c.ValueKey, out var bestText))
            {
                if (c.Part is { } part)
                {
                    whole = bestText;
                    if (ComplexValue.TryParse(bestText, out var z, out _, out _))
                        best = c.Text(ComplexValue.Get(z, part, t?.Value));
                }
                else best = bestText;
            }
            if (c.Part is not null && t is not null) wholeStart = ComplexValue.Format(t.Whole, t.WholeUnit, t.Form);
            var railed = r.Railed.FirstOrDefault(x => x.Key == c.Key);
            if (c.VendorPart is { } vp)
            {
                // A part's row reads as parts: the chosen part number, and the candidates' value span as its range.
                best = best is null ? null : vp.CandidateOfCell(best)?.Part ?? best;
                rows.Add(new OptimizeVariableJson(c.Key, start, best, vp.Candidates[0].ValueText, vp.Candidates[^1].ValueText));
                continue;
            }
            rows.Add(new OptimizeVariableJson(c.Key, start, best, c.Text(c.Min), c.Text(c.Max),
                railed is null ? null : railed.End == RailEnd.Min ? "min" : "max", railed?.Against, whole, wholeStart));
        }

        var goals = r.Goals.Select(g => new OptimizeGoalJson(g.Name, g.Met, Finite(g.WorstValue), g.WorstAt, g.Axis,
                                                               Finite(g.Margin), g.Corner,
            g.PerCorner is null ? null : [.. g.PerCorner.Select(c => new OptimizeGoalCornerJson(c.Corner, c.Met, Finite(c.WorstValue),
                                                                                                   c.WorstAt, Finite(c.Margin)))])).ToList();
        bool across = run.EvaluationsPerPoint > 1 || run.CornerNames[0] != Design.Statistics.CornerRun.NominalName;

        return new OptimizeReportJson(
            input, r.Algorithm, r.Stages, OutcomeWord(r.Outcome), r.FinishReason, r.BestCost,
            r.Iterations, r.Evaluations, r.Failures, r.Infeasible, r.CacheHits, rows, goals,
            r.Snap is { } s ? new OptimizeSnapJson(s.Snapped, s.Neighbours, s.CostBefore, s.CostSnapped, s.CostAfter, s.Polished,
                                                   s.Parts > 0 ? s.Parts : null) : null,
            sens is null ? null : new OptimizeSensitivityJson(sens.Cost,
                [.. sens.Variables.Select(v => new OptimizeSensitivityVariableJson(v.Key, v.PerRange, v.Share))],
                [.. sens.Goals.Select(g => new OptimizeSensitivityGoalJson(g.Goal, g.MostSensitive, g.PerRange))]),
            saved, perIteration,
            across ? run.CornerNames : null, across ? run.EvaluationsPerPoint : null,
            r.Parts.Count == 0 ? null : [.. r.Parts.Select(p => new OptimizePartJson(
                p.Key, catalog.Find(p.Key)?.ValueText ?? "", p.Part, p.ValueText, p.Description,
                Design.VendorLibraries.VendorLibraryRefs.RefFor(p.Library, p.File), p.Changed))],
            r.EmMode == OptimizerEmMode.Off ? null : new OptimizeEmJson(
                AnalysisDirectiveSchema.EmModeTokens[(int)r.EmMode], run.EmBudget, r.EmBudgetUsed, r.StoppedByBudget, r.BestEmKey,
                [.. r.EmSolves.Select(s => new OptimizeEmSolveJson(s.Values, s.Key, s.Cached, s.Failed, Finite(s.Cost), s.Met,
                    s.Goals.ToDictionary(g => g.Name, g => Finite(g.Margin), StringComparer.Ordinal)))],
                [.. r.ConnectedRanges.Select(c => new OptimizeConnectedRangeJson(c.Key, TunableValue.Format(c.Lo, c.Unit),
                    TunableValue.Format(c.Hi, c.Unit), TunableValue.Format(c.Min, c.Unit), TunableValue.Format(c.Max, c.Unit), c.Whole, c.Sentence))],
                applied is null ? null : new OptimizeApplyJson(applied.Written, applied.NotWritten, applied.LayoutKey, applied.KeyMatches,
                                                               applied.LayoutLines, applied.Sentence)));
    }

    private static double? Finite(double v) => double.IsFinite(v) ? v : null;

    private static string OutcomeWord(OptimizationOutcome o) => o switch
    {
        OptimizationOutcome.GoalsMet      => "goalsMet",
        OptimizationOutcome.GoalsUnmet    => "goalsUnmet",
        OptimizationOutcome.Refused       => "refused",
        OptimizationOutcome.NoConvergence => "noConvergence",
        _                                 => "cancelled",
    };

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static void Print(OptimizeReportJson r)
    {
        Console.WriteLine($"Optimization: {r.Algorithm}   ({r.Document})");
        if (r.Stages.Count > 0) Console.WriteLine($"Stages:    {string.Join(" → ", r.Stages)}");
        Console.WriteLine($"Finished:  {r.FinishReason}");
        if (r.Corners is { } corners)
            Console.WriteLine($"Corners:   {string.Join(", ", corners)}   ({r.EvaluationsPerPoint} evaluations per point)");
        Console.WriteLine($"Best cost: {(r.BestCost is { } c ? G(c) : "—")}   " +
                          $"({r.Iterations} iterations, {r.Evaluations} evaluations" +
                          (r.Failures > 0 ? $", {r.Failures} failed" : "") +
                          (r.Infeasible > 0 ? $", {r.Infeasible} infeasible" : "") + ")");

        Console.WriteLine();
        Console.WriteLine("Variables:");
        Table(["key", "start", "best", "min", "max", "railed"],
              r.Variables.Select(v => new[]
              {
                  v.Key, v.Start, v.Best ?? "—", v.Min, v.Max,
                  v.Railed is null ? "" : v.RailedAgainst is null ? v.Railed : $"{v.Railed} of {v.RailedAgainst}",
              }));
        // A complex value's parts compose ONE value: say what it is, start → best (D18).
        foreach (var w in r.Variables.Where(v => v.WholeStart is not null).GroupBy(v => v.Key[(v.Key.IndexOf('(') + 1)..^1]))
            Console.WriteLine($"  {w.Key}  {w.First().WholeStart} → {w.First().Whole ?? "—"}");

        Console.WriteLine();
        Console.WriteLine("Goals:");
        // Across corners every column is the binding corner's (brief-yield-7 R-ya7-2).
        string[] header = r.Corners is null ? ["name", "met", "value", "at", "margin"] : ["name", "met", "value", "at", "margin", "binding"];
        Table(header,
              r.Goals.Select(g => new[]
              {
                  g.Name, g.Met ? "yes" : "NO", g.Value is { } v ? G(v) : "—",
                  g.At is { } at ? $"{g.Axis} = {G(at)}" : "", g.Margin is { } m ? G(m) : "—", g.Corner ?? "",
              }.Take(header.Length).ToArray()));

        if (r.Parts is { Count: > 0 } parts)
        {
            // What to write back: each instance's Name becomes the part, File the file it is in (R-vl4-4a).
            Console.WriteLine();
            Console.WriteLine("Parts:");
            Table(["key", "start", "chosen", "value", "description"],
                  parts.Select(p => new[] { p.Key, p.Start, p.Part + (p.Changed ? "" : " (unchanged)"), p.Value, p.Description ?? "" }));
        }
        if (r.Snap is { } s)
            Console.WriteLine(s.IdealParts is { } ideal
                ? $"\nSnap: {s.Snapped} value(s) snapped, cost with ideal components for the {ideal} part(s) {G(s.CostBefore)}" +
                  $" → with the real parts {G(s.CostSnapped)}" + (s.Polished ? $" → {G(s.CostAfter)} after polishing" : "")
                : $"\nSnap: {s.Snapped} value(s) snapped, cost {G(s.CostBefore)} → {G(s.CostSnapped)}" +
                  (s.Polished ? $" → {G(s.CostAfter)} after polishing" : ""));
        if (r.Em is { } em)
        {
            // brief-sdl-7: the EM solves, the best one marked — what was written back is always one of these.
            Console.WriteLine();
            Console.WriteLine($"EM ({em.Mode}): {em.Solved} of at most {em.Budget} solve{(em.Budget == 1 ? "" : "s")}" +
                              (em.StoppedByBudget ? ", stopped at the budget" : "") + (em.BestKey is { } k ? $"; best geometry {k}" : ""));
            int n = 0;
            Table(["#", "values", "geometry", "cost", "met"],
                  em.Solves.Select(s => new[]
                  {
                      (++n).ToString(CultureInfo.InvariantCulture), string.Join(", ", s.Values.Select(kv => $"{kv.Key}={kv.Value}")),
                      (s.Key ?? "—") + (s.Cached ? " (cached)" : ""), s.Cost is { } c ? G(c) : "failed", s.Met ? "yes" : "no",
                  }));
            foreach (var range in em.ConnectedRanges) Console.WriteLine("  " + range.Sentence);
            if (em.Applied is { } ap) Console.WriteLine("Applied: " + ap.Sentence);
        }
        if (r.Sensitivity is { } sens)
        {
            Console.WriteLine();
            Console.WriteLine($"Sensitivity at the best point (cost {G(sens.Cost)}):");
            Table(["key", "per range", "share"],
                  sens.Variables.Select(v => new[] { v.Key, G(v.PerRange), $"{v.Share * 100:F1} %" }));
        }
    }

    private static void Table(string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        var width = header.Select((h, i) => Math.Max(h.Length, all.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        string Line(string[] cells) => "  " + string.Join("  ", cells.Select((c, i) => c.PadRight(width[i]))).TrimEnd();
        Console.WriteLine(Line(header));
        foreach (var row in all) Console.WriteLine(Line(row));
    }

    private static void Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf opt <file.csch|file.cnl> [--algorithm id] [--max-iter n] [--max-evals n] [--time s]");
        Console.Error.WriteLine("                     [--cost lsq|minimax] [--analyses goals|all] [--parallel n] [--seed n] [--set var=expr]");
        Console.Error.WriteLine("                     [--vars key,key] [--goals name,name] [--corners all|none|c,c] [--snap] [--sensitivity]");
        Console.Error.WriteLine("                     [--show-iterations] [--em off|correct|direct] [--em-budget n] [--apply]");
        Console.Error.WriteLine("                     [-o out.npy] [--history out.npy] [--save-preset name]");
    }
}
