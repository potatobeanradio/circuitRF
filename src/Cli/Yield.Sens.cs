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
    private sealed record SensFlags(string? Factors, double? Step, double? Sigma, bool? Ports, string? Responses, int? Parallel, int? Top,
                                    string? Snp = null, string? SnpExclude = null, string? By = null, bool? Em = null);

    /// <summary>What the text table ranks (<c>--by</c>, brief-sens-2 R-sens2-6); <c>--json</c> carries all four.</summary>
    private static readonly string[] SensLevels = ["key", "entry", "port", "instance"];

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
        if (f.Snp is not null)
        {
            bool? on = f.Snp.ToLowerInvariant() switch { "on" or "1" => true, "off" or "0" => false, _ => null };
            if (on is null) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--snp", f.Snp, "on or off"));
            S(() => sens.Snp = on.Value ? null : false);
        }
        // Replaces the line's list for this run; an empty value clears it.
        if (f.SnpExclude is not null) S(() => sens.SnpExclude = f.SnpExclude.Trim().Length == 0 ? null : f.SnpExclude.Trim());
        string by = f.By?.ToLowerInvariant() ?? "entry";
        if (!SensLevels.Contains(by)) return JsonRun.Fail(CliDiagnostics.YieldFlagValue("--by", f.By!, "key, entry, port or instance"));
        if (changed) setup.Sensitivity = sens;
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        // ── The run ──────────────────────────────────────────────────────────────────
        var ct = RunHost.Cancellation;
        var observer = RunHost.Observer;
        var run = SensitivityRun.Create(circuit, new SensitivityOptions
        {
            Em           = f.Em,
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
        PrintSens(report, f.Top ?? DefaultTop, by);
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
        static List<SensitivityRollupJson> Rows(IReadOnlyList<SensitivityRollup> rows)
            => [.. rows.Select(x => new SensitivityRollupJson(x.Label, x.Instance, x.Port, Finite(x.Delta), x.Share, x.Factors))];
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
                                                             g.Estimate is { } e ? Finite(e) : null, Contributors(g),
                                                             Rows(g.Entries), g.Ports.Count > 0 ? Rows(g.Ports) : null,
                                                             g.Ports.Count > 0 ? true : null, Rows(g.Instances)))],
            [.. r.Measures.Select(m => new SensitivityMeasureJson(m.Name, m.Unit, Finite(m.Nominal), Finite(m.Spread), Contributors(m),
                                                                   Rows(m.Entries), m.Ports.Count > 0 ? Rows(m.Ports) : null,
                                                                   m.Ports.Count > 0 ? true : null, Rows(m.Instances)))],
            [.. r.Notes.Select(n => n.Render())],
            r.Evaluations,
            Excluded: r.Excluded.Count > 0 ? [.. r.Excluded.Select(x => new SensitivityExcludedJson(x.Instance, x.Factors))] : null);
    }

    private static void PrintSens(SensitivityReportJson r, int top, string by = "entry")
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
        // brief-sens-2 R-sens2-6: a roll-up level ranks its rows by share; port shares overlap, so the table says so.
        void Rolled(string head, IReadOnlyList<SensitivityRollupJson>? rows, string unit)
        {
            rows ??= [];
            Console.WriteLine();
            Console.WriteLine(head + (by == "port" ? "  (port shares overlap: S31 counts toward ports 3 and 1)" : ""));
            Table(["rank", by, "factors", "Δ", "share"],
                  rows.Take(top).Select((x, i) => new[]
                  {
                      (i + 1).ToString(CultureInfo.InvariantCulture), x.Label, x.Factors.ToString(CultureInfo.InvariantCulture),
                      G(x.Delta, unit), (100 * x.Share).ToString("F1", CultureInfo.InvariantCulture) + " %",
                  }));
            if (rows.Count > top) Console.WriteLine($"  … {rows.Count - top} more (--top {rows.Count}, or --json)");
        }
        void Response(string head, IReadOnlyList<SensitivityContributorJson> cs, IReadOnlyList<SensitivityRollupJson>? entries,
                      IReadOnlyList<SensitivityRollupJson>? ports, IReadOnlyList<SensitivityRollupJson>? instances, string unit)
        {
            switch (by)
            {
                case "key":      Ranked(head, cs, unit); break;
                case "port":     Rolled(head, ports, unit); break;
                case "instance": Rolled(head, instances, unit); break;
                default:
                    // An entry that is a single key reads exactly as the key table does; one with an SnP pair rolls up.
                    if (entries is null || entries.Count == cs.Count) Ranked(head, cs, unit);
                    else Rolled(head, entries, unit);
                    break;
            }
        }
        foreach (var g in r.Goals)
            Response($"{g.Goal}  m₀ {G(g.Nominal)}  σ {G(g.Spread)}" + (g.Estimate is { } e ? $"  estimated pass {Pct(e)}" : "")
                     + "  (normalized margin: margin / scale)", g.Contributors, g.Entries, g.Ports, g.Instances, "");
        foreach (var m in r.Measures)
            Response($"{m.Measure}  nominal {G(m.Nominal, m.Unit)}  σ {G(m.Spread, m.Unit)}", m.Contributors, m.Entries, m.Ports, m.Instances, m.Unit);
        // R-sens2-5a: a block left out on purpose is said, never read as one found insensitive.
        if (r.Excluded is { Count: > 0 } ex)
        {
            Console.WriteLine();
            Console.WriteLine("excluded: " + string.Join(", ", ex.Select(x => $"{x.Instance} ({x.Factors} factor{(x.Factors == 1 ? "" : "s")})")));
        }
    }
}
