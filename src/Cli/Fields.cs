using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf fields &lt;path.cnl|path.csch&gt;</c> — a schematic's <c>fields</c> analyses, headless (brief-sar-9; <c>cli.md</c> §26):
/// an EM Field Data block's per-port fields, named by its <c>.cemr</c> and kept by a 3D run with <c>Sar.PerPortFields</c>, recombined with the waves
/// the circuit delivers to its ports, into SAR and B1 at one frequency — with no EM run.
///
/// <para><b>It owns no recombination.</b> The run is <see cref="CircuitEvaluation.Evaluate"/>, which dispatches
/// <c>FieldCosimulation</c> exactly as Simulate does, so a capacitor changed with <c>--set</c> is the run the GUI makes. What is
/// here is argument parsing, reporting and the export. The volumes and records are written beside the field set by the run
/// itself, and named in the notes.</para>
///
/// <para><b>Exit codes:</b> 0 every fields analysis ran · 1 refused or failed · 130 cancelled.</para>
/// </summary>
internal static class Fields
{
    private const string Verb = "fields";

    public static int Run(string[] args)
    {
        string? input = null, output = null;
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
            Console.Error.WriteLine("Usage: circuitrf fields <file.cnl|.csch> [-a name]... [--set var=expr]... [-o out.{mat,npy,txt}]");
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

        // Every fields analysis unless named: running the bench's S-parameter or HB analyses too is not what was asked.
        var fields = tb.Analyses.OfType<FieldsAnalysis>().Where(f => f.Enabled).Select(f => f.Name).ToList();
        if (names.Count == 0) names = fields;
        if (names.Count == 0)
            return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, "the design declares no enabled fields analysis (analysis <name> type=fields block=<instance> freq=<f> …)."));
        foreach (string n in names)
            if (!fields.Contains(n, StringComparer.OrdinalIgnoreCase))
                return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, $"'{n}' is not an enabled fields analysis of this design" +
                                                                   (fields.Count > 0 ? $" (it has {string.Join(", ", fields)})." : ".")));
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");

        var result = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Sets = sets, Analyses = names, Control = RunHost.Control, Measurements = true,
        });
        foreach (string w in result.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
        if (result.Status == RunStatus.Cancelled) return 130;
        if (result.Status != RunStatus.Success || result.Results.Count == 0)
            return JsonRun.Fail(CliDiagnostics.RunFailed(result.StatusMessage));
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
        // An analysis that threw is a convergence entry with its message (CircuitEvaluation never throws).
        var failed = result.Convergence.Where(c => c.Converged == false && c.Detail is not null).Select(c => $"'{c.Name}': {c.Detail}").ToList();
        if (failed.Count > 0) return JsonRun.Fail(CliDiagnostics.RunFailed(string.Join("; ", failed)));

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
