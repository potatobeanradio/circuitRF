using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Statistics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf yield center &lt;path&gt;</c> (brief-yield-11 R-ya11-8, <c>cli.md</c> §25.8). The run is
/// <see cref="CenteringRun"/>; what is here is the flag overrides of the file's <c>center</c> and <c>statistics</c>
/// lines, the report, and the one opt-in write — <c>--save-preset</c>, the centred nominals, to a <c>.csch</c> after a
/// history checkpoint (yield overview D12).
/// </summary>
internal static partial class Yield
{
    /// <summary>The flags <c>center</c> reads beyond the shared ones: the center line's settings, and the statistics
    /// line's that still apply (the target, the interval, the non-converged policy, the kit switches).</summary>
    private sealed record CenterFlags(
        string? Algorithm, int? Trials, int? Verify, int? MaxIter, int? MaxEvals, string? Time, double? Width,
        int? Parallel, int? Seed, double? Target, double? Confidence, int Sampling, int NonConverged, string? Save,
        bool? Process, bool? Mismatch, double? SigmaScale, int Scope, int Surrogate);

    private static int RunCenter(string input, string full, PreparedCircuit circuit, TestBench tb,
                                 List<(string Name, string Expr)> sets, string? presetName, string? output, bool quiet,
                                 List<string>? vars, List<string>? goals, CenterFlags f)
    {
        // ── The file's setup, with this run's flags over it — handed over only when a flag changed it ──
        var setup = tb.Tuning?.Clone() ?? new TuningSetup();
        var center = setup.Centering?.Clone() ?? new CenteringSettings();
        var st = setup.Statistics?.Clone() ?? new StatisticsSettings();
        bool centerChanged = false, statChanged = false;
        void C(Action apply) { apply(); centerChanged = true; }
        void S(Action apply) { apply(); statChanged = true; }
        if (f.Algorithm is not null) C(() => center.Algorithm = f.Algorithm);
        if (f.Trials is not null)    C(() => center.Trials = f.Trials);
        if (f.Verify is not null)    C(() => center.Verify = f.Verify);
        if (f.MaxIter is not null)   C(() => center.MaxIterations = f.MaxIter);
        if (f.MaxEvals is not null)  C(() => center.MaxEvaluations = f.MaxEvals);
        if (f.Time is not null)      C(() => center.TimeLimit = f.Time);
        if (f.Width is not null)     C(() => center.Width = f.Width);
        if (f.Parallel is not null)  C(() => center.Parallelism = f.Parallel);
        if (f.Seed is not null)      C(() => center.Seed = f.Seed);
        if (f.Surrogate >= 0)        C(() => center.Surrogate = f.Surrogate == 0 ? null : (CenteringSurrogate)f.Surrogate);
        if (f.Target is not null)     S(() => st.Target = f.Target);
        if (f.Confidence is not null) S(() => st.Confidence = f.Confidence);
        if (f.Sampling >= 0)          S(() => st.Sampling = (StatSampling)f.Sampling);
        if (f.NonConverged >= 0)      S(() => st.NonConverged = (NonConvergedPolicy)f.NonConverged);
        if (f.Save is not null)       S(() => st.Save = f.Save.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : f.Save.ToLowerInvariant());
        if (f.Process is not null)    S(() => st.Process = f.Process);
        if (f.Mismatch is not null)   S(() => st.Mismatch = f.Mismatch);
        if (f.SigmaScale is not null) S(() => st.SigmaScale = f.SigmaScale);
        if (f.Scope >= 0)             S(() => st.Scope = (OptimizerScope)f.Scope);
        if (centerChanged) setup.Centering = center;
        if (statChanged) setup.Statistics = st;
        bool changed = centerChanged || statChanged;
        if (vars is not null)
        {
            if (NarrowVariables(setup, vars) is { } refusal) return JsonRun.Fail(refusal);
            changed = true;
        }
        if (goals is not null)
        {
            if (NarrowGoals(setup, goals) is { } refusal) return JsonRun.Fail(refusal);
            changed = true;
        }
        if (presetName is not null && TuningPresets.NameProblem(tb.Tuning, -1, presetName) is { } presetProblem)
            return JsonRun.Fail(CliDiagnostics.YieldSaveName("--save-preset", presetProblem));
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── The run ──────────────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        var used = (changed ? setup : tb.Tuning ?? setup).Centering ?? new CenteringSettings();
        int maxIter = used.MaxIterations ?? OptimizationRun.DefaultMaxIterations;
        var run = CenteringRun.Create(circuit, new CenteringOptions
        {
            Setup        = changed ? setup : null,
            Sets         = sets,
            Cancellation = ct,
            ResultPath   = output ?? StatisticalRun.ResultPathFor(full),
            Progress     = p =>
            {
                string line = CenterProgressLine(p);
                if (!quiet) Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, p.Iteration, maxIter));
            },
        });
        if (run.Refusal is { } refused) return JsonRun.Fail(refused);
        foreach (var note in run.Notes) Report(note);
        int reported = run.Notes.Count;   // before Run(): result.Notes is the same list, so a later count skips all
        if (run.Estimate() is { } estimate) Console.Error.WriteLine($"[circuitRF] centering: {run.Algorithm}, {estimate.Variables} designable " +
                                                                    $"value(s), {run.Entries.Count} toleranced, {run.Goals.Count} yield goal(s) — {estimate.Estimate}");
        var result = run.Run();
        foreach (var note in result.Notes.Skip(reported)) Report(note);

        if (result.Outcome == CenteringOutcome.Cancelled || ct.IsCancellationRequested)
        {
            if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            JsonRun.Report(CliDiagnostics.YieldCancelled());
            return 130;
        }
        if (result.Outcome == CenteringOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed(result.FinishReason));
        if (result.Outcome == CenteringOutcome.NoneEvaluated && result.Refusal is { } none) JsonRun.Report(none);

        if (result.WrittenPath is { } written)
        {
            Console.WriteLine($"Wrote {written}");
            JsonRun.AddOutput(JsonRun.KindOf(written), written);
        }

        int exit = result.ExitCode;
        string? saved = null;
        if (presetName is not null && result.BestValues.Count > 0)
        {
            if (SaveCenteredPreset(full, presetName.Trim(), result.BestValues) is { } failed) { JsonRun.Report(failed); exit = 1; }
            else saved = presetName.Trim();
        }

        var report = ProjectCenter(input, run, result) with { Output = result.WrittenPath, SavedPreset = saved };
        JsonRun.Center = report;
        PrintCenter(report);
        return exit;
    }

    /// <summary><c>--save-preset</c>: the centred nominals as a new preset — <see cref="TuningPresets.LockIn"/>, the
    /// Optimizer's own Lock in — after a checkpoint, every other byte as the file's persistence writes it.</summary>
    private static Diagnostic? SaveCenteredPreset(string cschPath, string name, IReadOnlyDictionary<string, string> values)
    {
        if (Optimize.CheckpointBefore(cschPath, $"yield center --save-preset {name}") is { } failed) return failed;
        var (model, view, cellName) = SchematicPersistence.LoadFromFile(cschPath);
        var (next, _) = TuningPresets.LockIn(model.Tuning, values, DateTime.UtcNow, null, name);
        model.Tuning = next;
        SchematicPersistence.SaveToFile(cschPath, model, cellName, view.PanX, view.PanY, view.Zoom);
        Console.WriteLine($"Saved preset \"{name}\" (the centred nominals) to {cschPath}");
        JsonRun.AddOutput("schematic", cschPath);
        return null;
    }

    /// <summary>One iteration, as stderr prints it and an MCP progress notification carries it.</summary>
    internal static string CenterProgressLine(CenteringProgress p)
        => p.Stage == "verify"
            ? $"verifying the start and the best point · {p.Evaluations} simulations so far"
            : $"iteration {p.Iteration} · best yield {Pct(p.BestYield)} · smooth {G(Finite(p.BestObjective))} · {p.Evaluations} simulations" +
              (p.RSquared is { Count: > 0 } r2 ? $" · fit R² {G(r2.Values.Min())}" : "");

    private static CenterReportJson ProjectCenter(string input, CenteringRun run, CenteringResult r)
    {
        var c = run.Settings;
        static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> m)
            => new(m.ToDictionary(), StringComparer.Ordinal);
        var v = r.Verification;
        return new CenterReportJson(
            input, CenterOutcomeWord(r.Outcome), r.FinishReason, r.Algorithm, c.EffectiveTrials, c.EffectiveVerify,
            c.EffectiveWidth, c.EffectiveSeed, r.Iterations, r.Evaluations, r.VerifyEvaluations,
            Sorted(r.Start?.Values ?? new Dictionary<string, string>()), Sorted(r.BestValues),
            [.. r.Railed.Select(x => x.Key + (x.End == RailEnd.Min ? "@min" : "@max") + (x.Against is { } a ? $" (against {a})" : ""))],
            r.Start is { Failed: false } s ? Estimate(s.Yield) : null,
            r.Best is { } b ? Estimate(b.Yield) : null,
            r.Start is { Failed: false } s2 ? Finite(s2.Objective) : null,
            r.Best is { } b2 ? Finite(b2.Objective) : null,
            v is null ? null : new CenterVerificationJson(v.Seed, v.Trials, Estimate(v.Start), Estimate(v.Best), v.WithinOverlap, v.Sentence),
            run.Target is { } tp ? tp / 100 : null,
            [.. r.History.Select(h => new CenterIterationJson(h.Iteration, Finite(h.BestYield), Finite(h.BestObjective), h.Evaluations,
                                                              h.RSquared is null ? null : new SortedDictionary<string, double>(h.RSquared.ToDictionary(), StringComparer.Ordinal)))])
        {
            Surrogate      = r.Surrogate == CenteringSurrogate.None ? null : AnalysisDirectiveSchema.SurrogateTokens[(int)r.Surrogate],
            SwitchedBackAt = r.SwitchedBackAt,
        };
    }

    private static string CenterOutcomeWord(CenteringOutcome o) => o switch
    {
        CenteringOutcome.Finished      => "finished",
        CenteringOutcome.BelowTarget   => "belowTarget",
        CenteringOutcome.Refused       => "refused",
        CenteringOutcome.NoneEvaluated => "noneEvaluated",
        _                              => "cancelled",
    };

    private static void PrintCenter(CenterReportJson r)
    {
        Console.WriteLine($"Design centering: {r.Document}");
        Console.WriteLine($"  {r.Algorithm} · {r.Trials} common trials (seed {r.Seed}) · {r.Iterations} iterations · " +
                          $"{r.Evaluations} simulations · {r.FinishReason}");
        if (r.Surrogate is { } surrogate)
            Console.WriteLine($"  surrogate {surrogate}: candidates scored on {QuadraticSurrogate.VirtualTrials} virtual trials of the fit" +
                              (r.SwitchedBackAt is { } at ? $", then on simulated trials after iteration {at}" : ""));
        if (r.Verification is { } v)
        {
            string verdict = r.Target is { } tg
                ? $" · target {Pct(tg)}: {(r.Outcome == "belowTarget" ? "NOT MET" : "met")}" : "";
            Console.WriteLine($"Verified: {v.Sentence}{verdict}");
        }
        if (r.StartYield is { } sy && r.BestYield is { } by)
            Console.WriteLine($"{(r.Surrogate is null ? "On the common trials" : "Searched (not verified)")}: yield {Pct(sy.Yield)} → {Pct(by.Yield)} · " +
                              $"smooth {G(r.StartObjective)} → {G(r.BestObjective)}");

        if (r.BestValues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Centred nominals:");
            Table(["key", "start", "centred", ""],
                  r.BestValues.Select(kv => new[]
                  {
                      kv.Key, r.StartValues.TryGetValue(kv.Key, out var s) ? s : "", kv.Value,
                      string.Join(", ", r.Railed.Where(x => x.StartsWith(kv.Key + "@", StringComparison.Ordinal))
                                               .Select(x => "railed at " + x[(kv.Key.Length + 1)..])),
                  }));
        }
        if (r.History.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Yield vs iteration ({(r.Surrogate is null ? "common trials" : "virtual trials, simulated after a switch back")}):");
            foreach (var h in r.History)
                Console.WriteLine($"  {h.Iteration,4}  yield {Pct(h.BestYield),-8} smooth {G(h.BestObjective),-10} {h.Evaluations} simulations" +
                                  (h.RSquared is { Count: > 0 } r2 ? "  R² " + string.Join(", ", r2.Select(kv => $"{kv.Key} {G(kv.Value)}")) : ""));
        }
    }
}
