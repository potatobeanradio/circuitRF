using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-8 R-hzn8-3 — <c>em &lt;file.hzn&gt; --design D --solver hertzon [--setup S] [--sweep W] [--set var=value …] [-o out.sNp]</c>:
/// the call Run in Hertzon makes, headless.
///
/// <para>Argument checks, refusals and reporting only. Finding hertzon-cli is <see cref="HertzonDiscovery"/>, the process and
/// the exit-code sentences are <see cref="HertzonRun"/>, and the result is <see cref="HertzonResults"/> — the same three the
/// window's Run in Hertzon calls. Hertzon solves the FILE; an unsaved design in a window is not what this runs.</para>
///
/// <para>Exit codes: 0 done; 1 a refusal or any way the run failed, including a run that did not converge (its Touchstone is
/// still written and read, and <c>converged</c> says false); 130 cancelled. Hertzon's own summary fields ride in the result
/// document's <c>hertzon</c> object.</para>
/// </summary>
internal static class HznRunVerb
{
    /// <summary>True when the arguments name Hertzon as the solver.</summary>
    internal static bool Claims(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--solver" && string.Equals(args[i + 1], "hertzon", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Diagnostic Refuse(string id, string sentence) => Diagnostic.Create(id, DiagnosticSeverity.Error, "{sentence}", ("sentence", sentence));

    private static readonly string[] OwnSolversOnly = ["--device", "--memory-limit", "--cores", "--force", "--bands", "--reference", "--component",
                                                       "--component-ports", "--dry-run", "--mesh-only", "--adopt", "--freq", "--workspace"];

    internal static int Run(string[] args)
    {
        string? input = null, output = null, design = null, setup = null, sweep = null;
        var sets = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--solver" when i + 1 < args.Length: i++; break;
                case "-o" or "--output" when i + 1 < args.Length: output = args[++i]; break;
                case "--design" when i + 1 < args.Length: design = args[++i]; break;
                case "--setup" when i + 1 < args.Length: setup = args[++i]; break;
                case "--sweep" when i + 1 < args.Length: sweep = args[++i]; break;
                case "--set" when i + 1 < args.Length:
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed("em", kv));
                    sets.Add(new(kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    break;
                }
                case var a when OwnSolversOnly.Contains(a):
                    return JsonRun.Fail(Refuse("em.hertzon.option", $"{a} belongs to circuitRF's own solvers; Hertzon takes --design, --setup, --sweep, --set and -o."));
                case var a when a.StartsWith('-'):
                    return JsonRun.Fail(CliDiagnostics.RunUnknownOption("em", a));
                default: input = args[i]; break;
            }
        }

        if (input is null) return JsonRun.Fail(CliDiagnostics.InputRequired("em", ".hzn"));
        JsonRun.InputPath = input;
        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));
        string project = Path.GetFullPath(input);
        if (!string.Equals(Path.GetExtension(project), ".hzn", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(Refuse("em.hertzon.not-hzn", "--solver hertzon runs a Hertzon project (.hzn); " + Path.GetFileName(project) + " is not one."));

        var ready = HertzonDiscovery.Shared.Check();
        if (!ready.Ready) return JsonRun.Fail(Refuse("em.hertzon.unavailable", ready.Refusal!));
        var cli = ready.Installation!;

        Console.Error.WriteLine($"[circuitRF] {cli.Label}, command-line schema {cli.Schema}");
        var described = HertzonRun.Describe(cli, project);
        if (!described.Ok) return JsonRun.Fail(Refuse("em.hertzon.describe", described.Error!));

        var threeD = described.Designs.Where(d => d.IsThreeD).ToList();
        HertzonDesign? chosen;
        if (design is null)
        {
            if (threeD.Count != 1)
                return JsonRun.Fail(Refuse("em.hertzon.design", threeD.Count == 0
                    ? $"{Path.GetFileName(project)} has no 3D design to solve."
                    : $"{Path.GetFileName(project)} has {threeD.Count} 3D designs; name one with --design: {string.Join(", ", threeD.Select(d => d.Name))}."));
            chosen = threeD[0];
        }
        else
        {
            chosen = described.Designs.FirstOrDefault(d => d.Name == design);
            if (chosen is null)
                return JsonRun.Fail(Refuse("em.hertzon.design", $"{Path.GetFileName(project)} has no design '{design}'; it has: {string.Join(", ", described.Designs.Select(d => d.Name))}."));
            if (!chosen.IsThreeD)
                return JsonRun.Fail(Refuse("em.hertzon.design", $"Design '{design}' is a circuit design; Hertzon's command line solves 3D designs here."));
        }

        string resultsFolder = HertzonResults.ResultsFolder(project);
        string touchstone = output is { Length: > 0 } o ? Path.GetFullPath(o) : HertzonResults.TouchstonePath(resultsFolder, chosen.Name, chosen.Rows);
        var request = new HertzonRunRequest(project, chosen.Name, setup, sweep, sets, touchstone);

        double lastPercent = -10;
        var result = HertzonRun.Solve(cli, request, e =>
        {
            switch (e.Kind)
            {
                case HertzonEventKind.Progress when e.Percent is { } pct:
                    if (pct - lastPercent < 5 && pct < 100) return;
                    lastPercent = pct;
                    Console.Error.WriteLine($"[{pct.ToString("0", CultureInfo.InvariantCulture),3}%] {e.Message}");
                    break;
                case HertzonEventKind.Pass:
                    Console.Error.WriteLine($"      pass {e.Pass}: {e.Tetrahedra?.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra" +
                                            (e.DeltaS is { } d ? $", delta S {d.ToString("0.####", CultureInfo.InvariantCulture)}" : ""));
                    break;
                case HertzonEventKind.Log when e.Level is "warning" or "error":
                    Console.Error.WriteLine($"{e.Level}: {e.Message}");
                    break;
                case HertzonEventKind.Quiet:
                    Console.Error.WriteLine("note: " + e.Message);
                    break;
            }
        }, RunHost.Cancellation);

        JsonRun.Hertzon = Summary(cli, project, chosen.Name, result, touchstone, sets);

        if (result.Outcome == HertzonOutcome.Cancelled)
        {
            Console.Error.WriteLine(result.Sentence);
            JsonRun.Report(Refuse("em.hertzon.cancelled", result.Sentence!));
            return 130;
        }
        if (!result.HasResult) return JsonRun.Fail(Refuse("em.hertzon.failed", result.Sentence ?? "Hertzon did not produce a result."));

        string written = result.TouchstonePath!;
        Console.WriteLine($"Wrote {written}");
        JsonRun.AddOutput("touchstone", written);
        try
        {
            var data = HertzonResults.Build(written, result);
            JsonRun.Data = data;
            string baseDir = Path.GetDirectoryName(resultsFolder)!;
            if (HertzonResults.WriteNpy(baseDir, chosen.Name, data, out string? npyError) is { } npy)
            {
                Console.WriteLine($"Wrote {npy}");
                JsonRun.AddOutput("npy", npy);
            }
            else if (npyError is not null) JsonRun.Note(Diagnostic.Create("em.hertzon.npy", DiagnosticSeverity.Warning, "{sentence}",
                                                                          ("sentence", "The results file could not be written: " + npyError)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            return JsonRun.Fail(Refuse("em.hertzon.read", $"Hertzon wrote {written}, which circuitRF could not read: {ex.Message}"));
        }

        Console.WriteLine($"{cli.Label}: {result.Passes?.ToString(CultureInfo.InvariantCulture) ?? "?"} adaptive pass(es), " +
                          $"{result.Tetrahedra?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} tetrahedra, " +
                          $"{result.Seconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} s" + (result.Cached ? " (answered from Hertzon's kept solution)" : ""));
        if (result.Outcome == HertzonOutcome.NotConverged)
            return JsonRun.Fail(Refuse("em.hertzon.not-converged", result.Sentence!));
        return 0;
    }

    private static JsonObject Summary(HertzonInstallation cli, string project, string design, HertzonRunResult r, string touchstone, List<KeyValuePair<string, string>> sets)
    {
        var vars = new JsonObject();
        foreach (var kv in sets) vars[kv.Key] = kv.Value;
        return new JsonObject
        {
            ["solver"] = "hertzon",
            ["hertzonVersion"] = cli.Version,
            ["cliSchema"] = cli.Schema,
            ["project"] = project,
            ["design"] = design,
            ["setup"] = r.Setup,
            ["solution"] = r.Solution,
            ["variables"] = vars,
            ["exitCode"] = r.ExitCode,
            ["outcome"] = r.Outcome.ToString(),
            ["converged"] = r.Converged,
            ["passes"] = r.Passes,
            ["tetrahedra"] = r.Tetrahedra,
            ["seconds"] = r.Seconds,
            ["cached"] = r.Cached,
            ["touchstone"] = r.TouchstonePath ?? touchstone,
        };
    }
}
