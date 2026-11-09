using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf osc &lt;path.cnl|path.csch&gt;</c> — a design's oscillator analysis, headless (brief NZ-9; <c>cli.md</c> §41):
/// harmonic balance with the fundamental frequency as an unknown, at the circuit's OscProbe.
///
/// <para><b>It owns no solve.</b> The run is <see cref="CircuitEvaluation.Execute"/>, which dispatches the oscillator engine
/// exactly as Simulate does, and a <c>parametric_sweep</c> wrapping the analysis runs the whole sweep (naming the inner
/// analysis is promoted to its wrapper, as for <c>hb</c>). <c>--fguess</c>, <c>--maxharm</c>, <c>--tol</c>, <c>--aguess</c>
/// and <c>--probe</c> REPLACE the directive (<c>cli.md</c> §5), and <c>--phase-noise</c> turns its <c>PhaseNoise</c> on (brief
/// NZ-10); <c>--set var=expr</c> overrides a global before elaboration.</para>
///
/// <para><b>Exit codes:</b> 0 the oscillation was found · 1 refused (no start-up, no probe) or failed · 130 cancelled.</para>
/// </summary>
internal static class Osc
{
    private const string Verb = "osc";
    private const string Hint = "analysis <name> type=osc Fguess=<f> Unit=GHz";

    public static int Run(string[] args)
    {
        string? input = null, output = null, name = null, fguess = null, maxHarm = null, tol = null, aguess = null, probe = null;
        bool phaseNoise = false;
        var sets = new List<(string Name, string Expr)>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "-a" or "--analysis" when hasValue: name = args[++i]; break;
                case "-o" or "--output" or "--export" when hasValue: output = args[++i]; break;
                case "--fguess" when hasValue: fguess = args[++i]; break;
                case "--maxharm" when hasValue: maxHarm = args[++i]; break;
                case "--tol" when hasValue: tol = args[++i]; break;
                case "--aguess" when hasValue: aguess = args[++i]; break;
                case "--probe" when hasValue: probe = args[++i]; break;
                case "--phase-noise": phaseNoise = true; break;
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
            Console.Error.WriteLine("Usage: circuitrf osc <file.cnl|.csch> [-a name] [--set var=expr]... [--fguess f] [--maxharm k] [--tol t]");
            Console.Error.WriteLine("                     [--aguess a] [--probe X1] [--phase-noise] [-o out.{mat,npy,txt}]");
            Console.Error.WriteLine("  --fguess is in the directive's Unit (GHz unless it says otherwise).");
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

        // The chain to run: the named analysis, else the outermost runnable chain that bottoms out in an oscillator — a
        // parametric_sweep wrapping it is dispatched at the SWEEP, since running the inner alone drops the sweep axis.
        var sel = ChainSelector.Select(tb, name, a => a is OscillatorAnalysis, "oscillator", Hint);
        if (sel.PromotedFrom is { } from && sel.Selected is { } owner)
            Console.Error.WriteLine($"[circuitRF] {ChainSelector.PromotionNote(from, owner)}");
        else if (name is null && sel.Candidates.Count > 1)
            Console.Error.WriteLine($"[circuitRF] {ChainSelector.AmbiguityNote(sel, "oscillator")}");
        if (sel.Selected is not { } top) return JsonRun.Fail(CliDiagnostics.NoAnalysis(Verb, sel.Why!));

        // §5: an override replaces the directive, so the run is the GUI's run of the edited bench.
        if (((fguess ?? maxHarm ?? tol ?? aguess ?? probe) is not null || phaseNoise)
            && ChainSelector.BaseOfChain(top, tb) is OscillatorAnalysis o)
        {
            int i = tb.Analyses.IndexOf(o);
            tb.Analyses[i] = new OscillatorAnalysis(o.Name)
            {
                Enabled = o.Enabled, Model = o.Model, EmSetup = o.EmSetup,
                Probe = probe ?? o.Probe, FguessExpr = fguess ?? o.FguessExpr, FstartExpr = o.FstartExpr, FstopExpr = o.FstopExpr,
                Unit = o.Unit, MaxHarmonicExpr = maxHarm ?? o.MaxHarmonicExpr, FFTOverSampleExpr = o.FFTOverSampleExpr,
                TolExpr = tol ?? o.TolExpr, AguessExpr = aguess ?? o.AguessExpr,
                PhaseNoise = phaseNoise || o.PhaseNoise, PnStartExpr = o.PnStartExpr, PnStopExpr = o.PnStopExpr,
                PnNptsExpr = o.PnNptsExpr, PnLog = o.PnLog, PnPortExpr = o.PnPortExpr,
            };
            // Fstart/Fstop left blank follow the new Fguess; stated ones stay as written.
        }
        foreach (var (n, e) in sets) Console.Error.WriteLine($"[circuitRF] set {n} = {e}");

        var request = new CircuitEvaluationRequest { Sets = sets, Analyses = [top.Name], Control = RunHost.Control, Measurements = true };
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
        // The run's own summary — the Fosc/Aosc line Simulate shows in its status bar.
        Console.Error.WriteLine($"[circuitRF] {result.StatusMessage}");
        foreach (string note in result.Notes) Console.WriteLine(note);

        // The run's grouped DataSet: the analysis's group and, when the bench declares measure lines, `measurements` — the GUI's.
        var grouped = result.GroupedResults ?? new DataSet();
        foreach (string group in grouped.Groups)
        {
            Console.WriteLine();
            Console.WriteLine($"{group}:");
            foreach (var (cubeName, cube) in grouped.CubesIn(group).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (!cubeName.StartsWith("__", StringComparison.Ordinal)) CliEntry.PrintCube($"  {cubeName}", cube, 24, false);
        }
        JsonRun.Data = grouped;
        JsonRun.Analysis = top.Name;

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
