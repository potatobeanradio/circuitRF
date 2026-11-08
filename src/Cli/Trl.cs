using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf trl &lt;path.cnl|path.csch&gt;</c> — a bench's TRL calibrations, headless (brief-de-4; <c>cli.md</c> §29): three SnP blocks
/// holding the measured Thru, Reflect and Line, solved for the two fixtures, written as the two Touchstone files the directive names.
///
/// <para><b>It owns no calibration.</b> The run is <see cref="CircuitEvaluation.Execute"/>, which dispatches <see cref="TrlRun"/>
/// exactly as Simulate does. <c>--fixture-a</c>/<c>--fixture-b</c> REPLACE the directive (<c>cli.md</c> §5), so the run that
/// writes the files is the one the GUI would make of the edited bench. There is no <c>-o</c> for the fixtures: they are the
/// directive's two files. <c>-o</c> exports the DataSet (SA, SB, LineDeg, LineLossDb, Gamma, Usable).</para>
///
/// <para><b>Exit codes:</b> 0 every TRL analysis ran, its usable-band warning included · 1 refused or failed · 130 cancelled.</para>
/// </summary>
internal static class Trl
{
    private const string Verb = "trl";

    public static int Run(string[] args)
    {
        string? input = null, output = null, fixtureA = null, fixtureB = null;
        var names = new List<string>();
        var sets = new List<(string Name, string Expr)>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "-a" or "--analysis" when hasValue: names.Add(args[++i]); break;
                case "-o" or "--output" or "--export" when hasValue: output = args[++i]; break;
                case "--fixture-a" when hasValue: fixtureA = args[++i]; break;
                case "--fixture-b" when hasValue: fixtureB = args[++i]; break;
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
            int code = JsonRun.Fail(CliDiagnostics.InputRequired(Verb, ".cnl or .csch"));
            Console.Error.WriteLine("Usage: circuitrf trl <file.cnl|.csch> [-a name]... [--set var=expr]... [--fixture-a path] [--fixture-b path] [-o out.{mat,npy,txt}]");
            return code;
        }
        JsonRun.InputPath = input;
        if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));
        var kind = DocumentKinds.Classify(input);
        if (kind is not (DocumentKind.Netlist or DocumentKind.Schematic))
            return JsonRun.Fail(CliDiagnostics.RunWrongDocumentKind(Verb, input, DocumentKinds.Name(kind)));

        string full = Path.GetFullPath(input);
        var circuit = kind == DocumentKind.Schematic
            ? CircuitSource.PrepareSchematic(full)
            : PreparedCircuit.FromFile(full, Path.GetDirectoryName(full));
        if (circuit.ReadError is { } readError || circuit.Tb is not { } tb)
            return JsonRun.Fail(CliDiagnostics.RunFailed(circuit.ReadError ?? "the design could not be read"));

        var trls = tb.Analyses.OfType<TrlAnalysis>().Where(t => t.Enabled).Select(t => t.Name).ToList();
        if (names.Count == 0) names = trls;
        if (names.Count == 0)
            return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, "the design declares no enabled TRL analysis " +
                                                                "(analysis <name> type=trl thru=<SnP> reflect=<SnP> line=<SnP> reflectType=open|short)."));
        foreach (string n in names)
            if (!trls.Contains(n, StringComparer.OrdinalIgnoreCase))
                return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, $"'{n}' is not an enabled TRL analysis of this design" +
                                                                   (trls.Count > 0 ? $" (it has {string.Join(", ", trls)})." : ".")));
        if ((fixtureA ?? fixtureB) is not null && names.Count > 1)
            return JsonRun.Fail(CliDiagnostics.RunFailed("--fixture-a and --fixture-b name one calibration's files; name it with -a."));

        // §5: an override replaces the directive, so the run is the GUI's run of the edited bench.
        var written = new List<TrlAnalysis>();
        for (int i = 0; i < tb.Analyses.Count; i++)
        {
            if (tb.Analyses[i] is not TrlAnalysis t || !names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if ((fixtureA ?? fixtureB) is not null)
                tb.Analyses[i] = t = new TrlAnalysis(t.Name)
                {
                    Enabled = t.Enabled, Thru = t.Thru, Reflect = t.Reflect, Line = t.Line, ReflectType = t.ReflectType,
                    FixtureA = fixtureA ?? t.FixtureA, FixtureB = fixtureB ?? t.FixtureB,
                };
            written.Add(t);
        }
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        var request = new CircuitEvaluationRequest { Sets = sets, Analyses = names, Control = RunHost.Control, Measurements = true };
        var plan = CircuitEvaluation.Plan(circuit, request);
        foreach (string line in plan.Lines) Console.Error.WriteLine($"[circuitRF] {line}");
        var result = CircuitEvaluation.Execute(plan, request.Control, request.Measurements, [], null);
        if (plan.Notes.Count > 0) result = result.WithLeadingNotes(plan.Notes);
        foreach (string w in result.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
        if (result.Status == RunStatus.Cancelled) return 130;
        if (result.Status != RunStatus.Success || result.Results.Count == 0)
            return JsonRun.Fail(CliDiagnostics.RunFailed(result.StatusMessage));
        var failed = result.Convergence.Where(c => c.Converged == false && c.Detail is not null).Select(c => $"'{c.Name}': {c.Detail}").ToList();
        if (failed.Count > 0) return JsonRun.Fail(CliDiagnostics.RunFailed(string.Join("; ", failed)));
        foreach (string note in result.Notes) Console.WriteLine(note);

        var grouped = new DataSet();
        foreach (var r in result.Results)
        {
            Console.WriteLine();
            Console.WriteLine($"{r.Name}:");
            foreach (var (name, cube) in r.Data.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                CliEntry.PrintCube($"  {name}", cube, 24, false);
                grouped.AddToGroup(r.Name, name, cube);
            }
        }
        JsonRun.Data = grouped;
        JsonRun.Analysis = string.Join(", ", names);
        // The two files each calibration wrote, resolved as the run resolved them (an SnP's File's rule).
        foreach (var t in written)
            foreach (string stored in new[] { t.FixtureAPath, t.FixtureBPath })
                JsonRun.AddOutput("touchstone", Path.GetFullPath(Elaborator.ResolveFileReference(stored, circuit.BaseDirectory)));

        if (output is not null)
        {
            var format = CliEntry.FormatFromExtension(output);
            CliEntry.EnsureOutputDirectory(output);
            DataSetExporter.Export(grouped, output, format, new ExportOptions(Format: format));
            Console.WriteLine($"Wrote {output}");
            JsonRun.AddOutput(JsonRun.KindOf(output), output);
        }
        return 0;
    }
}
