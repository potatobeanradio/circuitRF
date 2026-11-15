using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf tran &lt;path.cnl|path.csch&gt;</c> — a bench's transient analyses, headless (brief-trn-1; <c>cli.md</c> §45).
///
/// <para><b>It owns no simulation.</b> The run is <see cref="CircuitEvaluation.Execute"/>, which dispatches the transient engine
/// exactly as Simulate does, and promotes a named analysis inside a <c>parametric_sweep</c> to the sweep (running the inner alone
/// would drop the sweep's axis). <c>--stop</c>, <c>--step</c>, <c>--method</c> and <c>--solver</c> REPLACE the directive (§5), so the run
/// is the GUI's run of the edited bench; <c>--set</c> overrides a global before elaboration.</para>
///
/// <para>stdout carries a summary per recorded net and IProbe (the value at Stop, the least and the most) and the run's counters; the
/// waveforms are in <c>-o</c>'s file, and in <c>--json</c> up to <see cref="JsonRun.InlineCubeLimit"/> values unless <c>--all</c>.</para>
///
/// <para><b>Exit codes:</b> 0 ran · 1 refused or failed · 130 cancelled.</para>
/// </summary>
internal static class Tran
{
    private const string Verb = "tran";

    public static int Run(string[] args)
    {
        string? input = null, output = null, stop = null, step = null, method = null, solver = null;
        var names = new List<string>();
        var sets = new List<(string Name, string Expr)>();
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "-a" or "--analysis" when hasValue: names.Add(args[++i]); break;
                case "-o" or "--output" or "--export" when hasValue: output = args[++i]; break;
                case "--stop" when hasValue: stop = args[++i]; break;
                case "--step" when hasValue: step = args[++i]; break;
                case "--method" when hasValue: method = args[++i]; break;
                case "--solver" when hasValue: solver = args[++i]; break;
                case "--all": all = true; break;
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
            Console.Error.WriteLine("Usage: circuitrf tran <file.cnl|.csch> [-a name]... [--set var=expr]... [--stop t] [--step t]");
            Console.Error.WriteLine("                      [--method trap|gear2] [--solver native|ngspice] [--all] [-o out.{mat,npy,txt}]");
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

        var trans = tb.Analyses.OfType<TransientAnalysis>().Where(t => t.Enabled).Select(t => t.Name).ToList();
        if (names.Count == 0) names = trans;
        if (names.Count == 0)
            return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, "the design declares no enabled transient analysis (analysis <name> type=tran Stop=...)."));
        foreach (string n in names)
            if (!trans.Contains(n, StringComparer.OrdinalIgnoreCase))
                return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, $"'{n}' is not an enabled transient analysis of this design" +
                                                                   (trans.Count > 0 ? $" (it has {string.Join(", ", trans)})." : ".")));

        // §5: an override replaces the directive, so every point of a sweep re-resolves it.
        if ((stop ?? step ?? method ?? solver) is not null)
            for (int i = 0; i < tb.Analyses.Count; i++)
            {
                if (tb.Analyses[i] is not TransientAnalysis t || !names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)) continue;
                tb.Analyses[i] = new TransientAnalysis(t.Name)
                {
                    Enabled = t.Enabled, Model = t.Model, EmSetup = t.EmSetup,
                    StopExpr = stop ?? t.StopExpr, StepExpr = step ?? t.StepExpr, StartExpr = t.StartExpr, MaxStepExpr = t.MaxStepExpr,
                    Method = method ?? t.Method, RelTolExpr = t.RelTolExpr, VTolExpr = t.VTolExpr, ITolExpr = t.ITolExpr,
                    InitialState = t.InitialState, IcExpr = t.IcExpr, Solver = solver ?? t.Solver, SaveSteps = t.SaveSteps,
                    MaxStepsExpr = t.MaxStepsExpr, SpiceOptions = t.SpiceOptions, SpiceLines = t.SpiceLines,
                    SpiceLineFreq = t.SpiceLineFreq, SpiceFit = t.SpiceFit,
                };
            }
        foreach (var (name, expr) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
        JsonRun.AllCubes = all;
        JsonRun.InlineCubeLimit = 10_000;

        var request = new CircuitEvaluationRequest { Sets = sets, Analyses = names, Control = RunHost.Control, Measurements = true };
        var plan = CircuitEvaluation.Plan(circuit, request);
        foreach (string line in plan.Lines) Console.Error.WriteLine($"[circuitRF] {line}");
        var result = CircuitEvaluation.Execute(plan, request.Control, request.Measurements, [], null);
        if (plan.Notes.Count > 0) result = result.WithLeadingNotes(plan.Notes);
        foreach (string w in result.Warnings) Console.Error.WriteLine($"[circuitRF] warning: {w}");
        if (result.Status == RunStatus.Cancelled) return 130;
        if (result.Status != RunStatus.Success || result.Results.Count == 0)
            return JsonRun.Fail(CliDiagnostics.RunFailed(result.StatusMessage));
        foreach (string note in result.Notes) Console.WriteLine(note);

        var grouped = result.GroupedResults ?? new DataSet();
        foreach (string group in grouped.Groups)
        {
            Console.WriteLine();
            Console.WriteLine($"{group}:");
            var cubes = grouped.CubesIn(group).ToDictionary(kv => kv.Key, kv => kv.Value);
            Summarise(cubes, "V", "V");
            Summarise(cubes, "I", "A");
            foreach (var (name, cube) in cubes.Where(kv => kv.Key.StartsWith("Tran.", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                CliEntry.PrintCube($"  {name}", cube, 24, false);
        }
        JsonRun.Data = grouped;
        JsonRun.Analysis = string.Join(", ", names);

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

    /// <summary>One line per signal of a [time, signal] cube: its value at Stop, its least and its most. A swept cube (more
    /// axes) is named and left to the file.</summary>
    private static void Summarise(Dictionary<string, DataCube> cubes, string name, string unit)
    {
        if (!cubes.TryGetValue(name, out var cube)) return;
        if (cube.Axes.Count != 2 || cube.Axes[0].Name != "time")
        {
            Console.WriteLine($"  {name}: [{string.Join(", ", cube.Axes.Select(a => $"{a.Name}({a.Values.Length})"))}] (in the exported file)");
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        int nt = cube.Axes[0].Values.Length, ns = cube.Axes[1].Values.Length;
        var labels = cube.Axes[1].Labels ?? [.. Enumerable.Range(0, ns).Select(i => i.ToString(inv))];
        Console.WriteLine(string.Format(inv, "  {0}  ({1} points, {2:G4} to {3:G4} s)     at Stop        min        max",
            name, nt, cube.Axes[0].Values[0], cube.Axes[0].Values[^1]));
        for (int s = 0; s < ns; s++)
        {
            double last = cube.RealValues[(nt - 1) * ns + s], lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int t = 0; t < nt; t++) { double v = cube.RealValues[t * ns + s]; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            Console.WriteLine(string.Format(inv, "    {0,-20} {1,12:G6} {2,10:G6} {3,10:G6} {4}", labels[s], last, lo, hi, unit));
        }
    }
}
