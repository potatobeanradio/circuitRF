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
    double  start  = 1e9, stop = 10e9, step = 1e8;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--freq" when i + 1 < args.Length:
                // --freq start:stop:step (Hz) or start:stop:N (N points)
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

        // Build frequency array
        var freqs = BuildFreqArray(start, stop, step);
        Console.Error.WriteLine(
            $"S-parameter analysis: {freqs.Length} points, " +
            $"{start/1e9:G4}–{stop/1e9:G4} GHz");

        var snp = SParameterEngine.Run(nl, freqs);

        // Write Touchstone
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

// ── DC analysis (stub — Phase 3 will add nonlinear; linear DC works now) ──────

static int RunDc(string[] args)
{
    if (args.Length == 0 || args[0].StartsWith('-'))
    {
        Console.Error.WriteLine("dc: input .cnl file required");
        return 1;
    }
    var input = args[0];
    if (!File.Exists(input)) { Console.Error.WriteLine($"File not found: {input}"); return 1; }

    try
    {
        var (lib, tb) = CnlReader.ReadFile(input);
        var nl = new Elaborator(lib).Elaborate(tb);
        Console.Error.WriteLine($"DC analysis: {nl.Components.Count} components (linear)");
        // Phase 3 will add the nonlinear solve; for now, just reports the netlist
        Console.WriteLine("(DC solve not yet implemented — Phase 3)");
        return 0;
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
    Console.WriteLine("  dc     <file.cnl>   (Phase 3 — linear netlist dump for now)");
    Console.WriteLine("  elab   <file.cnl>   (dump elaborated netlist)");
    Console.WriteLine();
    Console.WriteLine("Frequency format: 1GHz, 100MHz, 1e9 (Hz bare)");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --freq 1GHz:3GHz:50MHz -o hero1.s4p");
    return 0;
}
