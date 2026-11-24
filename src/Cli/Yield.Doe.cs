using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Statistics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf yield doe &lt;path&gt;</c> (brief-yield-14 R-ya14-8, <c>cli.md</c> §25.9). The run is
/// <see cref="DoeRun"/>; what is here is the flag overrides of the file's <c>doe</c> line, the report and the effects
/// tables. A noun on the existing verb rather than a verb of its own: it runs the same evaluator, shares the flags,
/// the output conventions and the exit codes — 0, 1, 2, 130; with no target there is no 3. It writes nothing to the
/// design.
/// </summary>
internal static partial class Yield
{
    /// <summary>The doe line's settings as flags.</summary>
    private sealed record DoeFlags(string? Design, int? Resolution, string? Factors, string? Levels, int? Centre,
                                   string? Responses, int? Parallel);

    private static int RunDoe(string input, string full, PreparedCircuit circuit, TestBench tb,
                              List<(string Name, string Expr)> sets, string? output, bool quiet,
                              List<string>? goals, bool optimum, DoeFlags f)
    {
        // ── The file's setup, with this run's flags over it — handed over only when a flag changed it ──
        var setup = tb.Tuning?.Clone() ?? new TuningSetup();
        var doe = setup.Doe?.Clone() ?? new DoeSettings();
        bool changed = false;
        void D(Action apply) { apply(); changed = true; }
        if (f.Design is not null)
        {
            int k = Token(AnalysisDirectiveSchema.DoeDesignTokens, f.Design);
            if (k < 0) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--design", f.Design, "full2, frac, pb or ccf"));
            D(() => doe.Design = (DoeDesignKind)k);
        }
        if (f.Resolution is { } res) D(() => doe.Resolution = res);
        if (f.Factors is not null)
        {
            int k = Token(AnalysisDirectiveSchema.DoeFactorTokens, f.Factors);
            if (k < 0) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--factors", f.Factors, "opt or stat"));
            D(() => doe.Factors = (DoeFactorSource)k);
        }
        if (f.Levels is not null)
        {
            string lv = f.Levels.Trim().ToLowerInvariant();
            if (lv != "range" && DoeSettings.SigmaOf(lv) is null)
                return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--levels", f.Levels, "range, or sigma:k with k above zero"));
            D(() => doe.Levels = lv);
        }
        if (f.Centre is { } c) D(() => doe.Centre = c);
        if (f.Responses is not null)
        {
            int k = Token(AnalysisDirectiveSchema.DoeResponseTokens, f.Responses);
            if (k < 0) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--responses", f.Responses, "goals or all"));
            D(() => doe.Responses = (DoeResponseSet)k);
        }
        if (f.Parallel is { } p) D(() => doe.Parallelism = p);
        // A --factors flag moves the levels' default with it, as a doe line read afresh would.
        if (f.Factors is not null && f.Levels is null && doe.Levels == DoeSettings.DefaultLevels(doe.EffectiveFactors == DoeFactorSource.Opt ? DoeFactorSource.Stat : DoeFactorSource.Opt))
            doe.Levels = null;
        if (changed) setup.Doe = doe;
        // Two flags the design itself can make meaningless — checked once the file's doe line is under them, before
        // anything runs (R-ya16-5, R-ya16-7).
        if (f.Resolution is not null && doe.EffectiveDesign != DoeDesignKind.Frac)
            return JsonRun.Fail(CliDiagnostics.YieldCornerFlag("--resolution", "yield doe --design frac"));
        if (optimum && doe.EffectiveFactors != DoeFactorSource.Opt)
            return JsonRun.Fail(CliDiagnostics.YieldCornerFlag("--optimum", "yield doe --factors opt"));
        if (goals is not null)
        {
            if (NarrowGoals(setup, goals) is { } refusal) return JsonRun.Fail(refusal);
            changed = true;
        }
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── The run ──────────────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        var run = DoeRun.Create(circuit, new DoeOptions
        {
            Setup        = changed ? setup : null,
            Sets         = sets,
            Cancellation = ct,
            ResultPath   = output ?? DoeRun.ResultPathFor(full),
            Progress     = pr =>
            {
                string line = $"run {pr.Done} of {pr.Total}";
                if (!quiet) Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, pr.Done, pr.Total));
            },
        });
        if (run.Refusal is { } refused) return JsonRun.Fail(refused);
        foreach (var note in run.Notes) Report(note);
        int reported = run.Notes.Count;   // before Run(): result.Notes is the same list, so a later count skips all
        Console.Error.WriteLine($"[circuitRF] design of experiments: {run.Describe()} — {run.DistinctRuns} simulation(s)");

        var result = run.Run();
        foreach (var note in result.Notes.Skip(reported)) Report(note);
        if (result.Outcome == DoeOutcome.Cancelled || ct.IsCancellationRequested)
        {
            if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            JsonRun.Report(CliDiagnostics.YieldCancelled());
            return 130;
        }
        if (result.Outcome == DoeOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed(result.FinishReason));
        if (result.Outcome == DoeOutcome.NoneEvaluated && result.Refusal is { } none) JsonRun.Report(none);

        // The optimum before the write is reported: cancelling during it is cancelling the run, which writes nothing
        // (R-ya16-4) — the file the design wrote is removed on that path as on the run's own.
        int exit = result.ExitCode;
        DoeOptimum? best = null;
        if (optimum && result.Outcome == DoeOutcome.Finished)
        {
            try { best = run.ModelOptimum(result, ct); }
            catch (OperationCanceledException) { best = null; }
            if (best is null || ct.IsCancellationRequested)
            {
                if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
                JsonRun.Report(CliDiagnostics.YieldCancelled());
                return 130;
            }
            if (best.Refusal is { } why) { JsonRun.Report(why); exit = 1; }
            else if (best.ConfirmationReason is { } failed) Report(failed);
        }

        if (result.WrittenPath is { } written)
        {
            Console.WriteLine($"Wrote {written}");
            JsonRun.AddOutput(JsonRun.KindOf(written), written);
        }

        var report = ProjectDoe(input, run, result, best) with { Output = result.WrittenPath };
        JsonRun.Doe = report;
        PrintDoe(report);
        return exit;
    }

    private static DoeReportJson ProjectDoe(string input, DoeRun run, DoeResult r, DoeOptimum? o)
    {
        var d = run.Settings;
        return new DoeReportJson(
            input,
            r.Outcome switch { DoeOutcome.Finished => "finished", DoeOutcome.Refused => "refused", DoeOutcome.NoneEvaluated => "noneEvaluated", _ => "cancelled" },
            r.FinishReason,
            AnalysisDirectiveSchema.DoeDesignTokens[(int)d.EffectiveDesign],
            run.Describe(),
            run.Plan?.Resolution,
            run.Plan?.Generators ?? [],
            AnalysisDirectiveSchema.DoeFactorTokens[(int)d.EffectiveFactors],
            d.EffectiveLevels,
            [.. run.Factors.Select(x => new DoeFactorJson(x.Letter, x.Key, x.Low, x.Centre, x.High))],
            r.Records.Count,
            r.Records.Count(x => x.Evaluated),
            r.Evaluations,
            [.. r.Responses.Select(x => new DoeResponseJson(
                x.Name,
                x.Kind switch { DoeResponseKind.Worst => "worst", DoeResponseKind.Margin => "margin", _ => "measure" },
                x.Goal,
                x.Fit is { } fit ? Finite(fit.Intercept) : null,
                x.Fit is { } f2 ? Finite(f2.RSquared) : null,
                x.Fit is { } f3 ? Finite(f3.Pse) : null,
                x.Fit is { } f4 ? Finite(f4.Margin) : null,
                x.Fit?.Curvature is { } cv ? Finite(cv) : null,
                x.Fit?.CurvatureActive == true,
                x.Fit is null ? [] : [.. x.Fit.Effects.Select(e => new DoeEffectJson(
                    e.Term, e.Effect, e.Coefficient, e.Active, e.Aliases))]))],
            o is null || o.Refusal is not null ? null : new DoeOptimumJson(
                o.Algorithm,
                new SortedDictionary<string, string>(o.Values.ToDictionary(), StringComparer.Ordinal),
                o.Coded,
                Finite(o.PredictedObjective),
                o.Confirmation == PointStatus.Evaluated,
                o.ConfirmationReason?.Render(),
                [.. o.Goals.Select(g => new DoeGoalPredictionJson(
                    g.Goal, g.PredictedValue is { } pv ? Finite(pv) : null, Finite(g.PredictedMargin),
                    g.SimulatedValue is { } sv ? Finite(sv) : null, g.SimulatedMargin is { } sm ? Finite(sm) : null, g.Met))]));
    }

    private static void PrintDoe(DoeReportJson r)
    {
        Console.WriteLine($"Design of experiments: {r.Document}");
        Console.WriteLine($"  {r.Description} · factors={r.Factors} levels={r.Levels} · {r.Evaluated} of {r.Runs} runs evaluated · " +
                          $"{r.Evaluations} simulations");
        if (r.FactorList.Count > 0)
        {
            Console.WriteLine();
            Table(["", "factor", "low (-1)", "centre", "high (+1)"],
                  r.FactorList.Select(x => new[] { x.Letter, x.Key, x.Low, x.Centre, x.High }));
        }
        foreach (var resp in r.Responses)
        {
            Console.WriteLine();
            if (resp.Effects.Count == 0) { Console.WriteLine($"Effects of {resp.Name}: not analysed"); continue; }
            Console.WriteLine($"Effects of {resp.Name} (* active: |effect| above Lenth's margin {G(resp.LenthMargin)}; " +
                              $"R² {G(resp.RSquared)}; mean at the centre of the design {G(resp.Intercept)}):");
            Table(["term", "effect", "", "aliased with"],
                  resp.Effects.OrderByDescending(e => Math.Abs(e.Effect)).Select(e => new[]
                  {
                      e.Term, G(e.Effect), e.Active ? "*" : "",
                      e.Aliases.Count == 0 ? "" : e.Aliases.Count <= 6 ? string.Join(", ", e.Aliases)
                                                : string.Join(", ", e.Aliases.Take(6)) + $", … {e.Aliases.Count - 6} more",
                  }));
            if (resp.Curvature is { } cv)
                Console.WriteLine($"  curvature (cube mean − centre mean) {G(cv)}{(resp.CurvatureActive ? " — larger than the margin: fit a quadratic (--design ccf)" : "")}");
        }
        if (r.Optimum is { } o)
        {
            Console.WriteLine();
            Console.WriteLine($"Model optimum ({o.Algorithm} on the fitted model), confirmed by simulation{(o.Confirmed ? "" : " — the confirmation did not evaluate")}:");
            Table(["key", "value"], o.Values.Select(kv => new[] { kv.Key, kv.Value }));
            Console.WriteLine();
            Table(["goal", "predicted", "simulated", "predicted margin", "simulated margin", "met"],
                  o.Goals.Select(g => new[]
                  {
                      g.Goal, G(g.PredictedValue), G(g.SimulatedValue), G(g.PredictedMargin), G(g.SimulatedMargin),
                      g.Met is { } m ? (m ? "yes" : "no") : "—",
                  }));
        }
    }
}
