using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf yield sens &lt;path&gt;</c> (brief-sens-1 R-sens1-7, <c>cli.md</c> §25.10). The run is
/// <see cref="SensitivityRun"/>; what is here is the flag overrides of the file's <c>sensitivity</c> line, the report
/// and the ranked tables. A noun on the existing verb rather than a verb of its own: one run service, one set of output
/// conventions. Exit 0 done, 1 refused, 2 the nominal did not evaluate, 130 cancelled — there is no target, so no 3.
/// It writes nothing to the design.
/// </summary>
internal static partial class Yield
{
    /// <summary>The sensitivity line's settings as flags, and the text table's length.</summary>
    private sealed record SensFlags(string? Factors, double? Step, double? Sigma, bool? Ports, string? Responses, int? Parallel, int? Top);

    /// <summary>The text table's length when <c>--top</c> is not given; <c>--json</c> always carries every contributor.</summary>
    internal const int DefaultTop = 10;

    private static int RunSens(string input, string full, PreparedCircuit circuit, TestBench tb,
                               List<(string Name, string Expr)> sets, string? output, bool quiet,
                               List<string>? vars, List<string>? goals, SensFlags f)
    {
        // ── The file's setup, with this run's flags over it — handed over only when a flag changed it ──
        var setup = tb.Tuning?.Clone() ?? new TuningSetup();
        var sens = setup.Sensitivity?.Clone() ?? new SensitivitySettings();
        bool changed = false;
        void S(Action apply) { apply(); changed = true; }
        if (f.Factors is not null)
        {
            int k = Token(AnalysisDirectiveSchema.SensFactorTokens, f.Factors);
            if (k < 0) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--factors", f.Factors, "all, stat or opt"));
            S(() => sens.Factors = (SensitivityFactorSource)k);
        }
        if (f.Step is { } step)   S(() => sens.Step = step);
        if (f.Sigma is { } sigma) S(() => sens.Sigma = sigma);
        if (f.Ports is { } ports) S(() => sens.Ports = ports ? true : null);
        if (f.Responses is not null)
        {
            int k = Token(AnalysisDirectiveSchema.DoeResponseTokens, f.Responses);
            if (k < 0) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--responses", f.Responses, "goals or all"));
            S(() => sens.Responses = (DoeResponseSet)k);
        }
        if (f.Parallel is { } p) S(() => sens.Parallelism = p);
        if (changed) setup.Sensitivity = sens;
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── The run ──────────────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        var run = SensitivityRun.Create(circuit, new SensitivityOptions
        {
            Setup        = changed ? setup : null,
            Sets         = sets,
            Vars         = vars,
            Goals        = goals,
            Cancellation = ct,
            ResultPath   = output ?? SensitivityRun.ResultPathFor(full),
            Progress     = pr =>
            {
                string line = $"evaluation {pr.Done} of {pr.Total}";
                if (!quiet) Console.Error.WriteLine(line);
                observer?.Invoke(new RunProgress(line, pr.Done, pr.Total));
            },
        });
        if (run.Refusal is { } refused) return JsonRun.Fail(refused);
        foreach (var note in run.Notes) Report(note);
        Console.Error.WriteLine($"[circuitRF] sensitivity: {run.Describe()}");

        var result = run.Run();
        foreach (var note in result.Notes.Skip(run.Notes.Count)) Report(note);
        if (result.Outcome == SensitivityOutcome.Cancelled || ct.IsCancellationRequested)
        {
            if (result.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            JsonRun.Report(CliDiagnostics.YieldCancelled());
            return 130;
        }
        if (result.Outcome == SensitivityOutcome.Refused)
            return JsonRun.Fail(result.Refusal ?? CliDiagnostics.RunFailed(result.FinishReason));
        if (result.Outcome == SensitivityOutcome.NominalFailed && result.Refusal is { } nominal) JsonRun.Report(nominal);

        if (result.WrittenPath is { } written)
        {
            Console.WriteLine($"Wrote {written}");
            JsonRun.AddOutput(JsonRun.KindOf(written), written);
        }

        var report = ProjectSens(input, run, result) with { Output = result.WrittenPath };
        JsonRun.Sensitivity = report;
        PrintSens(report, f.Top ?? DefaultTop);
        return result.ExitCode;
    }

    /// <summary>The <c>result.sensitivity</c> projection of a run — what <c>--json</c> carries, every contributor.</summary>
    internal static SensitivityReportJson ProjectSens(string input, SensitivityRun run, SensitivityResult r)
    {
        var s = run.Settings;
        static List<SensitivityContributorJson> Contributors(SensitivityResponse resp)
            => [.. resp.Contributors.Select(c => new SensitivityContributorJson(
                   c.Factor.Key, c.Factor.Label, c.Factor.BasisText, Finite(c.Delta), c.Share,
                   SensitivityRun.StatusWord(c.Status), c.Factor.Plus, c.Factor.Minus, c.Reason))];
        return new SensitivityReportJson(
            input,
            r.Outcome switch
            {
                SensitivityOutcome.Finished      => "finished",
                SensitivityOutcome.Refused       => "refused",
                SensitivityOutcome.NominalFailed => "nominalFailed",
                _                                => "cancelled",
            },
            r.FinishReason,
            AnalysisDirectiveSchema.SensFactorTokens[(int)s.EffectiveFactors],
            s.EffectiveStep,
            s.EffectiveSigma,
            [.. r.Goals.Select(g => new SensitivityGoalJson(g.Name, Finite(g.Nominal), Finite(g.Spread),
                                                             g.Estimate is { } e ? Finite(e) : null, Contributors(g)))],
            [.. r.Measures.Select(m => new SensitivityMeasureJson(m.Name, m.Unit, Finite(m.Nominal), Finite(m.Spread), Contributors(m)))],
            [.. r.Notes.Select(n => n.Render())],
            r.Evaluations);
    }

    private static void PrintSens(SensitivityReportJson r, int top)
    {
        Console.WriteLine($"Sensitivity: {r.Document}");
        Console.WriteLine($"  factors={r.Factors} · step {G(r.Step)} % · sigma {G(r.Sigma)} · {r.Evaluations} simulations" +
                          (r.FinishReason.StartsWith("stopped", StringComparison.Ordinal) ? " · " + r.FinishReason : ""));
        void Ranked(string head, IReadOnlyList<SensitivityContributorJson> cs, string unit)
        {
            Console.WriteLine();
            Console.WriteLine(head);
            Table(["rank", "contributor", "basis", "Δ", "share", "status"],
                  cs.Take(top).Select((c, i) => new[]
                  {
                      (i + 1).ToString(CultureInfo.InvariantCulture), c.Label, c.Basis, G(c.Delta, unit),
                      (100 * c.Share).ToString("F1", CultureInfo.InvariantCulture) + " %", c.Status,
                  }));
            if (cs.Count > top) Console.WriteLine($"  … {cs.Count - top} more (--top {cs.Count}, or --json)");
        }
        foreach (var g in r.Goals)
            Ranked($"{g.Goal}  m₀ {G(g.Nominal)}  σ {G(g.Spread)}" + (g.Estimate is { } e ? $"  estimated pass {Pct(e)}" : "")
                   + "  (normalized margin: margin / scale)", g.Contributors, "");
        foreach (var m in r.Measures)
            Ranked($"{m.Measure}  nominal {G(m.Nominal, m.Unit)}  σ {G(m.Spread, m.Unit)}", m.Contributors, m.Unit);
    }
}
