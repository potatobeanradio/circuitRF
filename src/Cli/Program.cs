using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;

// ── command dispatch ──────────────────────────────────────────────────────────

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

// --kits <dir> makes externally-provided devices work headlessly, the same way opening a workspace
// does in the GUI: point at a folder of installed kits and a netlist naming one resolves it. Taken
// out of the argument list here so every command gets it without repeating the parsing.
args = TakeKitFolders(args, out var kitFolders);

if (kitFolders.Count > 0)
    ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver(kitFolders));

return args[0].ToLowerInvariant() switch
{
    "sparam" => RunSparam(args[1..]),
    "dc"     => RunDc(args[1..]),
    "elab"   => RunElab(args[1..]),
    _        => PrintHelp()
};

// ── S-parameter analysis ──────────────────────────────────────────────────────

static int RunSparam(string[] args)
{
    string? input = null, output = null;
    bool    freqExplicit = false;
    double  start  = 1e9, stop = 10e9, step = 1e8;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--freq" when i + 1 < args.Length:
                freqExplicit = true;
                var parts = args[++i].Split(':');
                if (parts.Length == 3)
                {
                    start = ParseHz(parts[0]);
                    stop  = ParseHz(parts[1]);
                    step  = ParseHz(parts[2]);
                }
                break;
            case "-o" or "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            default:
                if (!args[i].StartsWith('-'))
                    input = args[i];
                break;
        }
    }

    if (input is null)
    {
        Console.Error.WriteLine("sparam: input .cnl file required");
        Console.Error.WriteLine("Usage: circuitrf sparam <file.cnl> [--freq start:stop:step] [-o out.sNp]");
        return 1;
    }
    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"File not found: {input}");
        return 1;
    }

    try
    {
        var (lib, tb) = CnlReader.ReadFile(input);
        var nl = new Elaborator(lib).Elaborate(tb);
        int shown = PrintWarnings(nl);

        // Prefer typed SParameterAnalysis from the netlist unless --freq was explicitly given.
        double[] freqs;
        var spa = tb.Analyses.OfType<SParameterAnalysis>().FirstOrDefault();
        if (spa is not null && !freqExplicit)
        {
            freqs = spa.Expand(nl.ResolvedGlobals);
            Console.Error.WriteLine(
                $"S-parameter analysis '{spa.Name}': {freqs.Length} points, " +
                $"{freqs[0]/1e9:G4}–{freqs[^1]/1e9:G4} GHz " +
                $"({spa.Sweeps.Count} segment(s))");
        }
        else
        {
            freqs = BuildFreqArray(start, stop, step);
            Console.Error.WriteLine(
                $"S-parameter analysis: {freqs.Length} points, " +
                $"{start/1e9:G4}–{stop/1e9:G4} GHz");
        }

        var ds  = SParameterEngine.Run(nl, freqs);

        // AGAIN, AFTER THE RUN — the engine reports what it finds while assembling and solving, long
        // after the pass above. Without this a real problem is written to nl.Warnings and printed by
        // nobody, which is exactly how a singular-matrix report went unseen here.
        PrintWarnings(nl, shown);

        var snp = RfCore.Data.DataSetBuilder.ToSnp(ds);

        var outPath = output ?? Path.ChangeExtension(input, $".s{snp.Ports}p");
        TouchstoneIO.WriteFile(snp, outPath);
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// ── DC operating point ────────────────────────────────────────────────────────

static int RunDc(string[] args)
{
    if (args.Length == 0 || args[0].StartsWith('-'))
    {
        Console.Error.WriteLine("dc: input .cnl file required");
        return 1;
    }
    var input = args[0];
    if (!File.Exists(input)) { Console.Error.WriteLine($"File not found: {input}"); return 1; }

    var settings = DcSettingsFrom(args);

    try
    {
        var (lib, tb) = CnlReader.ReadFile(input);
        var nl = new Elaborator(lib).Elaborate(tb);
        int shown = PrintWarnings(nl);

        var result = NonlinearDcEngine.Run(nl, settings);

        // AGAIN, AFTER THE RUN. Elaboration is not the only thing that has something to say: the DC
        // engine reports what it finds while building the system — a thermal node with no thermal
        // path, for one — and those are added during the run, long after the pass above.
        PrintWarnings(nl, shown);

        Console.Error.WriteLine(
            $"DC: {(result.Converged ? "converged" : "DID NOT CONVERGE")} in {result.Iterations} " +
            $"iteration(s), residual {result.FinalResidual:G3}");

        // Ground is not in the unknown vector; print it anyway so the listing reads as a complete
        // set of node voltages rather than one with a hole where node 0 should be.
        Console.WriteLine("Node voltages:");
        Console.WriteLine($"  {"0",-28} {0.0,14:G6}");
        for (int i = 1; i < nl.Nodes.Count; i++)
            Console.WriteLine($"  {nl.Nodes.NameOf(i),-28} {result.NodeVoltages[i - 1],14:G6}");

        PrintWorkerOutput();

        if (result.ProbeCurrents.Count > 0)
        {
            Console.WriteLine("Probe currents (A):");
            foreach (var (name, current) in result.ProbeCurrents.OrderBy(p => p.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {name,-28} {current,14:G6}");
        }

        return result.Converged ? 0 : 2;
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
}

// ── Elaboration dump (development tool) ──────────────────────────────────────

static int RunElab(string[] args)
{
    if (args.Length == 0 || !File.Exists(args[0]))
    {
        Console.Error.WriteLine("elab: input .cnl file required");
        return 1;
    }
    try
    {
        var (lib, tb) = CnlReader.ReadFile(args[0]);
        var nl = new Elaborator(lib).Elaborate(tb);
        PrintWarnings(nl);
        Console.WriteLine($"{nl.Components.Count} component(s), {nl.Nodes.Count} node(s)");
        foreach (var c in nl.Components)
        {
            var nodes  = string.Join(",", c.Nodes.Select(n => $"{n}({nl.Nodes.NameOf(n)})"));
            var params_ = string.Join(" ", c.Parameters.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"  {c.InstancePath,-24} {c.ComponentType,-6} [{nodes}] {params_}");
        }
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>
/// <summary>
/// Solver knobs a DC run can be given from the command line.
///
/// <para><b>Why <c>--max-iter</c> earns its place.</b> A circuit that will not converge grinds to the
/// cap and then reports — on a kit that was 279,000 iterations and two and a half minutes per
/// attempt, which is a debugging loop nobody iterates on. A converging solve takes single-digit
/// iterations, so a low cap costs a healthy circuit nothing and turns a failing one into a few
/// seconds. Diagnostic value, not a numerical one.</para>
/// </summary>
/// <summary>
/// Prints whatever the device workers wrote to their own error streams.
///
/// <para>Headless, the console is the only channel there is. A worker's log holds facts stated
/// nowhere else — how it classified each of a model's nodes, whether the data files it needs
/// opened, whether its own Jacobian agrees with its currents — and those were previously visible
/// only when something threw. A run that merely fails to converge is exactly when they are wanted.</para>
/// </summary>
static void PrintWorkerOutput()
{
    foreach (var (name, provider) in ExternalDeviceRegistry.Resolved)
    {
        if (provider is not DeviceWorkerProvider worker) continue;

        string log = worker.RecentErrorOutput;
        if (string.IsNullOrWhiteSpace(log)) continue;

        Console.Error.WriteLine($"--- worker output ({name}) ---");
        Console.Error.WriteLine(log);
    }
}

static AnalysisSettings DcSettingsFrom(string[] args)
{
    var d = AnalysisSettings.Default;
    int maxIter = d.NonlinearMaxIter, rampSteps = d.DcBiasRampSteps;
    double gmin = d.Gmin;

    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--max-iter" or "--maxiter" && int.TryParse(args[i + 1], out int n) && n > 0)
            maxIter = n;
        else if (args[i] == "--dc-steps" && int.TryParse(args[i + 1], out int k) && k > 0)
            rampSteps = k;
        else if (args[i] == "--gmin" &&
                 double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double g) && g > 0)
            gmin = g;
    }

    return new AnalysisSettings
    {
        NonlinearMaxIter = maxIter,
        DcBiasRampSteps  = rampSteps,
        Gmin             = gmin,
        ConductanceRegularization = RegularizationMode.Always,
    };
}

/// Pulls every <c>--kits &lt;dir&gt;</c> out of the argument list, returning what remains. Repeatable,
/// because a design may draw on kits installed in more than one place.
/// </summary>
static string[] TakeKitFolders(string[] args, out List<string> folders)
{
    folders = [];
    var rest = new List<string>(args.Length);

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "--kits" or "--kit" && i + 1 < args.Length)
        {
            folders.Add(args[++i]);
            continue;
        }
        rest.Add(args[i]);
    }

    return rest.ToArray();
}

/// <summary>
/// The CLI is headless, so the console IS its warnings channel — unlike the GUI, which drains
/// <see cref="ElaboratedNetlist.Warnings"/> into the Messages pane instead
/// (brief-housekeeping-tearoff-palette-repo.md R-hk-9/R-hk-10: <c>ElaboratedNetlist.AddWarning</c>
/// itself no longer writes to Console.Error, since that was leaking into every GUI run too).
/// </summary>
// Returns how many warnings have now been printed, so a caller can print again later — elaboration
// and the engine each add their own, at different times — without repeating what was already said,
// which would read as the same problem happening twice.
static int PrintWarnings(ElaboratedNetlist nl, int from = 0)
{
    for (; from < nl.Warnings.Count; from++)
        Console.Error.WriteLine($"[circuitRF] {nl.Warnings[from]}");
    return from;
}

static double ParseHz(string s)
{
    s = s.Trim();
    if (s.EndsWith("GHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3]) * 1e9;
    if (s.EndsWith("MHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3]) * 1e6;
    if (s.EndsWith("kHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3]) * 1e3;
    if (s.EndsWith("Hz",  StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^2]);
    return double.Parse(s);
}

static double[] BuildFreqArray(double start, double stop, double step)
{
    if (step <= 0) step = (stop - start) / 100;
    var list = new List<double>();
    for (double f = start; f <= stop + step * 1e-9; f += step)
        list.Add(f);
    return list.ToArray();
}

static int PrintHelp()
{
    Console.WriteLine("circuitRF — headless RF simulator");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  sparam <file.cnl> [--freq start:stop:step] [-o out.sNp]");
    Console.WriteLine("  dc     <file.cnl>   (DC operating point)");
    Console.WriteLine("  elab   <file.cnl>   (dump elaborated netlist)");
    Console.WriteLine();
    Console.WriteLine("Options (any command):");
    Console.WriteLine("  --kits <dir>        folder of installed kits, for externally-provided");
    Console.WriteLine("                      devices (ExtDevice Provider=...). Repeatable.");
    Console.WriteLine();
    Console.WriteLine("Frequency format: 1GHz, 100MHz, 1e9 (Hz bare)");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --freq 1GHz:3GHz:50MHz -o hero1.s4p");
    return 0;
}
