using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using CircuitRF.Engine.Mom;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using RfCore;
using RfCore.Data;
using RfCore.Export;

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

// A worker's own log, when CRF_WORKER_LOG asks for it. What a worker MEASURES — which nodes are
// free unknowns, which pins carry a temperature, whether the model's Jacobian matches its currents
// — decides how its device is stamped, and a measurement that lands differently on two machines
// produces no error on either: the device stamps cleanly, the numbers stay finite, and one machine
// simply will not converge. On stderr, so it never contaminates piped results.
ProcessDeviceWorkerTransport.Logged += log => Console.Error.WriteLine(
    string.IsNullOrWhiteSpace(log.Provider) ? $"worker: {log.Line}" : $"worker '{log.Provider}': {log.Line}");

return args[0].ToLowerInvariant() switch
{
    "sparam" => RunSparam(args[1..]),
    "dc"     => RunDc(args[1..]),
    "hb"     => RunHb(args[1..]),
    "lp"  or "loadpull"         => RunLoadpull(args[1..], pursuit: false),
    "lpp" or "loadpull_pursuit" or "pursuit"
                                => RunLoadpull(args[1..], pursuit: true),
    "em"     => RunEm(args[1..]),
    "convert" => CircuitRF.Cli.LayoutConvert.Run(args[1..]),
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
        var shown = PrintWarnings(nl);

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

        var ds  = SParameterEngine.Run(nl, lib, tb, baseDirectory: null, freqs);

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
        var shown = PrintWarnings(nl);

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

// ── Harmonic balance ──────────────────────────────────────────────────────────

/// <summary>
/// Runs the harmonic-balance analysis declared in a .cnl, single- or multi-tone, and — when the HB
/// is wrapped in a <c>parametric_sweep</c> — the whole sweep, exactly as the GUI dispatches it.
///
/// <para><b>Why this exists.</b> HB was reachable only from the GUI, so any HB question about a
/// netlist that is generated rather than drawn — a supplier kit's parts, a regression fixture — had
/// no headless answer. <c>sparam</c> and <c>dc</c> already had one.</para>
///
/// <para><b>What it prints.</b> Convergence, the spectrum axis with each product's frequency, the
/// interface-node voltage spectrum and any probe/port-current spectrum, and the TestBench's
/// measurements. Everything else goes to <c>--export</c>, since a spectrum × sweep is a data file,
/// not console output.</para>
/// </summary>
static int RunHb(string[] args)
{
    string? input = null, exportPath = null, analysisName = null;
    var     sets      = new List<(string Name, string Expr)>();
    int?    maxHarm   = null, maxIter = null, maxMixOrder = null;
    double? tol       = null;
    bool    diag      = false, allPoints = false;
    int     maxRows   = 24;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--analysis" or "-a" when i + 1 < args.Length:
                analysisName = args[++i];
                break;
            case "--export" or "-o" when i + 1 < args.Length:
                exportPath = args[++i];
                break;
            case "--set" when i + 1 < args.Length:
            {
                var kvText = args[++i];
                int eq = kvText.IndexOf('=');
                if (eq <= 0)
                {
                    Console.Error.WriteLine($"hb: --set expects name=expr, got '{kvText}'");
                    return 1;
                }
                sets.Add((kvText[..eq].Trim(), kvText[(eq + 1)..].Trim()));
                break;
            }
            case "--maxharm" when i + 1 < args.Length && int.TryParse(args[i + 1], out int k):
                maxHarm = k; i++;
                break;
            case "--maxmix" when i + 1 < args.Length && int.TryParse(args[i + 1], out int mo):
                maxMixOrder = mo; i++;
                break;
            case "--max-iter" or "--maxiter" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n):
                maxIter = n; i++;
                break;
            case "--tol" when i + 1 < args.Length && TryParseDouble(args[i + 1], out double t):
                tol = t; i++;
                break;
            case "--rows" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r):
                maxRows = r; i++;
                break;
            case "--all":
                allPoints = true;
                break;
            case "--diag":
                diag = true;
                break;
            default:
                if (!args[i].StartsWith('-')) input = args[i];
                break;
        }
    }

    if (input is null)
    {
        Console.Error.WriteLine("hb: input .cnl file required");
        Console.Error.WriteLine(
            "Usage: circuitrf hb <file.cnl> [-a name] [--set var=expr] [--maxharm K] [--maxmix M]");
        Console.Error.WriteLine(
            "                    [--tol t] [--max-iter N] [--rows N] [--all] [--diag] [-o out.{mat,npy,txt}]");
        return 1;
    }
    if (!File.Exists(input)) { Console.Error.WriteLine($"File not found: {input}"); return 1; }

    try
    {
        var (lib, tb) = CnlReader.ReadFile(input);

        // --set lands in the netlist's own variable scope rather than being pushed at the engine, so
        // an override participates in expression evaluation like any other global: everything derived
        // from it re-derives. Overriding Pavl_dbm has to move the source amplitude computed from it.
        foreach (var (name, expr) in sets)
        {
            tb.GlobalVariables.RemoveAll(v => v.Name == name);
            tb.GlobalVariables.Add(new Variable(name, expr));
            Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
        }

        var nl    = new Elaborator(lib).Elaborate(tb);
        var shown = PrintWarnings(nl);

        // Pick what to run: the named analysis, else the outermost runnable chain that bottoms out
        // in an HB. A parametric_sweep wrapping the HB must be dispatched at the SWEEP, not the HB —
        // running the inner directly silently drops the sweep axis and returns one point.
        var top = SelectTop(tb, analysisName, a => a is HarmonicBalanceAnalysis,
                            "HB", "analysis <name> type=hb ...", out string? why);
        if (top is null)
        {
            Console.Error.WriteLine($"hb: {why}");
            return 1;
        }

        var settings = SolverSettingsFrom(maxIter, diag);
        top = ApplyHbOverrides(tb, top, maxHarm, maxMixOrder, tol, maxIter);

        DataSet       ds;
        HbRunResult?  run = null;
        if (top is ParametricSweepAnalysis psa)
        {
            Console.Error.WriteLine(
                $"HB sweep '{psa.Name}': {psa.SweepValues.Length} point(s) over {psa.SweepVarName}");
            ds = ParametricSweepEngine.Run(psa, lib, tb, settings,
                                           baseDirectory: Path.GetDirectoryName(Path.GetFullPath(input)));
        }
        else
        {
            var hba = (HarmonicBalanceAnalysis)top;
            var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            Console.Error.WriteLine(
                $"HB '{hba.Name}': {DescribeTones(p)}, MaxHarm={p.MaxHarmonic}" +
                (p.IsMultiTone ? $", MaxMixOrder={p.MaxMixOrder}" : "") +
                $", tol={p.Tol:G3}");
            run = new HbEngine(nl, tb, settings).Run(p);
            ds  = run.DataSet;
        }

        // AGAIN, AFTER THE RUN — same reason as `dc`: the engine adds warnings while assembling and
        // solving, long after elaboration finished.
        PrintWarnings(nl, shown);
        PrintWorkerOutput();

        // Measurements are the point of an HB run on anything real (conversion loss, gain, IMn), and
        // they are evaluated over the whole result including the sweep axis — so run them here, the
        // same way the GUI does, rather than leaving the caller to redo the algebra on the export.
        // A measurement qualifies its cube accessor with the analysis name — and the GUI names a
        // swept result after the INNER analysis, not the sweep. Match that exactly, so the same
        // `measure` line works in both places rather than only in whichever one it was written for.
        var resultName = BaseOfChain(top, tb)?.Name ?? top.Name;
        var measDs     = EvaluateMeasurements(tb, nl, resultName, ds, run);

        Console.WriteLine($"Analysis: {top.Name}   ({input})");
        PrintHbDataSet(ds, maxRows, allPoints);
        if (measDs is { Cubes.Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("Measurements:");
            foreach (var (name, cube) in measDs.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                PrintCube($"  {name}", cube, maxRows, allPoints);
        }

        if (exportPath is not null)
        {
            var exportDs = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
            var format   = FormatFromExtension(exportPath);
            DataSetExporter.Export(exportDs, exportPath, format,
                new ExportOptions(Format: format), run?.LinearPayload);
            Console.WriteLine($"Wrote {exportPath}");
        }

        return Converged(ds) ? 0 : 2;
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
}

// ── Loadpull / loadpull-pursuit ───────────────────────────────────────────────

/// <summary>
/// The <c>lp</c> and <c>lpp</c> verbs. One function, because the two analyses differ only in which
/// directive they dispatch and which overrides apply: everything around that — chain selection,
/// <c>--set</c>, measurement evaluation, printing, export — is identical, and two copies of it would
/// be two places for the loadpull half and the pursuit half to drift apart.
///
/// <para><b>Both go through the same chain selection <c>hb</c> uses, and for the same reason.</b> A
/// frequency-swept loadpull is a <c>parametric_sweep</c> wrapping the loadpull directive; naming the
/// inner analysis runs one frequency and returns a result that looks converged, with the sweep axis
/// silently gone.</para>
///
/// <para><b>Overrides land in the TestBench directive, never at the engine.</b> That is what makes
/// them survive a sweep: <c>ParametricSweepEngine</c> re-elaborates and re-resolves the inner
/// directive at every sweep point, so an override handed straight to a freshly constructed engine
/// would be discarded on the first point.</para>
/// </summary>
static int RunLoadpull(string[] args, bool pursuit)
{
    string verb = pursuit ? "lpp" : "lp";
    string kind = pursuit ? "loadpull-pursuit" : "loadpull";

    string? input = null, exportPath = null, analysisName = null, gridPath = null, outGridPath = null;
    var     sets      = new List<(string Name, string Expr)>();
    int?    maxHarm   = null, maxIter = null;
    double? tol       = null, compression = null;
    double? pinStart  = null, pinStep = null, pinMax = null;
    bool    diag      = false, allPoints = false;
    int     maxRows   = 24;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--analysis" or "-a" when i + 1 < args.Length:
                analysisName = args[++i];
                break;
            case "--export" or "-o" when i + 1 < args.Length:
                exportPath = args[++i];
                break;
            case "--grid" when i + 1 < args.Length:
                // Made absolute HERE, because the directive's own Grid= was already resolved against
                // the .cnl's directory by the reader — a relative override left alone would silently
                // change which directory it is relative to.
                gridPath = Path.GetFullPath(args[++i]);
                break;
            case "--out-grid" or "--outgrid" when i + 1 < args.Length:
                outGridPath = Path.GetFullPath(args[++i]);
                break;
            case "--set" when i + 1 < args.Length:
            {
                var kvText = args[++i];
                int eq = kvText.IndexOf('=');
                if (eq <= 0)
                {
                    Console.Error.WriteLine($"{verb}: --set expects name=expr, got '{kvText}'");
                    return 1;
                }
                sets.Add((kvText[..eq].Trim(), kvText[(eq + 1)..].Trim()));
                break;
            }
            case "--pin" when i + 1 < args.Length:
            {
                var f = args[++i].Split(':');
                if (f.Length != 3 ||
                    !TryParseDouble(f[0], out double p0) ||
                    !TryParseDouble(f[1], out double p1) ||
                    !TryParseDouble(f[2], out double p2))
                {
                    Console.Error.WriteLine($"{verb}: --pin expects start:step:max in dBm, got '{args[i]}'");
                    return 1;
                }
                (pinStart, pinStep, pinMax) = (p0, p1, p2);
                break;
            }
            case "--compression" when i + 1 < args.Length && TryParseDouble(args[i + 1], out double cdb):
                compression = cdb; i++;
                break;
            case "--maxharm" when i + 1 < args.Length && int.TryParse(args[i + 1], out int k):
                maxHarm = k; i++;
                break;
            case "--max-iter" or "--maxiter" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n):
                maxIter = n; i++;
                break;
            case "--tol" when i + 1 < args.Length && TryParseDouble(args[i + 1], out double t):
                tol = t; i++;
                break;
            case "--rows" when i + 1 < args.Length && int.TryParse(args[i + 1], out int r):
                maxRows = r; i++;
                break;
            case "--all":
                allPoints = true;
                break;
            case "--diag":
                diag = true;
                break;
            default:
                if (!args[i].StartsWith('-')) input = args[i];
                break;
        }
    }

    // Refused rather than ignored: a Γ grid silently not applied is a run that answers a different
    // question and reports nothing about it.
    if (pursuit && gridPath is not null)
    {
        Console.Error.WriteLine(
            "lpp: --grid does not apply to a pursuit — a pursuit SEARCHES for its terminations rather " +
            "than reading a grid. Use --out-grid to say where the terminations it finds are written.");
        return 1;
    }
    if (!pursuit && outGridPath is not null)
    {
        Console.Error.WriteLine("lp: --out-grid applies to lpp (the pursuit writes a grid; a loadpull reads one).");
        return 1;
    }

    if (input is null)
    {
        Console.Error.WriteLine($"{verb}: input .cnl file required");
        Console.Error.WriteLine(
            $"Usage: circuitrf {verb} <file.cnl> [-a name] [--set var=expr] " +
            (pursuit ? "[--out-grid out.gam] " : "[--grid grid.gam] ") +
            "[--pin start:step:max]");
        Console.Error.WriteLine(
            "                     [--compression dB] [--maxharm K] [--tol t] [--max-iter N] " +
            "[--rows N] [--all] [--diag]");
        Console.Error.WriteLine(
            "                     [-o out.{mat,npy,txt" + (pursuit ? "" : ",spl,lpcwave") + "}]");
        return 1;
    }
    if (!File.Exists(input)) { Console.Error.WriteLine($"File not found: {input}"); return 1; }

    try
    {
        var (lib, tb) = CnlReader.ReadFile(input);

        // Same rule as `hb`: an override joins the netlist's own variable scope so everything derived
        // from it re-derives, rather than being pushed at one engine that reads it once.
        foreach (var (name, expr) in sets)
        {
            tb.GlobalVariables.RemoveAll(v => v.Name == name);
            tb.GlobalVariables.Add(new Variable(name, expr));
            Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
        }

        var nl    = new Elaborator(lib).Elaborate(tb);
        var shown = PrintWarnings(nl);

        var top = SelectTop(
            tb, analysisName,
            pursuit ? a => a is LoadpullPursuitAnalysis : a => a is LoadpullAnalysis,
            kind,
            pursuit ? "analysis <name> type=loadpull_pursuit ..." : "analysis <name> type=loadpull ...",
            out string? why);
        if (top is null)
        {
            Console.Error.WriteLine($"{verb}: {why}");
            return 1;
        }

        top = ApplyLoadpullOverrides(tb, top, gridPath, outGridPath, maxHarm, tol, maxIter,
                                     compression, pinStart, pinStep, pinMax);

        var     settings = SolverSettingsFrom(maxIter, diag);
        DataSet ds;

        if (top is ParametricSweepAnalysis psa)
        {
            Console.Error.WriteLine(
                $"{kind} sweep '{psa.Name}': {psa.SweepValues.Length} point(s) over {psa.SweepVarName}");
            ds = ParametricSweepEngine.Run(psa, lib, tb, settings,
                                           baseDirectory: Path.GetDirectoryName(Path.GetFullPath(input)));
        }
        else if (pursuit)
        {
            var lppa = (LoadpullPursuitAnalysis)top;
            var pp   = LoadpullPursuitEngine.Resolve(lppa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            Console.Error.WriteLine(
                $"Loadpull-pursuit '{lppa.Name}': f0={pp.LpParams.ToneHz / 1e9:G6} GHz, " +
                $"{pp.SearchMethod}, {(pp.UsePae ? "PAE" : "DE")} at {pp.LpParams.Compression:G3} dB " +
                $"compression, Zsource OBO {pp.ZsourceOBoDB:G3} dB");
            ds = new LoadpullPursuitEngine(new LoadpullEngine(nl, tb, settings)).Run(pp);
        }
        else
        {
            var lpa = (LoadpullAnalysis)top;
            var p   = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            Console.Error.WriteLine(
                $"Loadpull '{lpa.Name}': f0={p.ToneHz / 1e9:G6} GHz, {p.Grid.Points.Count} grid point(s) " +
                $"({(p.SweepLoad ? "load" : "source")} tuner, harmonic {p.TuneHarm}), " +
                $"Pin {p.PinStartDbm:G3}:{p.PinStepDb:G3}:{p.PinMaxDbm:G3} dBm to {p.Compression:G3} dB " +
                $"{(p.UseGt ? "Gt" : "Gp")} compression");

            // Enriched exactly as the GUI enriches it (SchematicRunService), so the derived display
            // metrics — Pout_dBm, Zin, IRL, AMPM — are present in a headless export too. Without this
            // a .npy written here and one written by the GUI would not carry the same cubes.
            ds = RfCore.Loadpull.LoadpullPostProcessor.Enrich(
                     new LoadpullEngine(nl, tb, settings).Run(p));
        }

        PrintWarnings(nl, shown);
        PrintWorkerOutput();

        var resultName = BaseOfChain(top, tb)?.Name ?? top.Name;
        var measDs     = EvaluateMeasurements(tb, nl, resultName, ds, null);

        Console.WriteLine($"Analysis: {top.Name}   ({input})");
        PrintLoadpullDataSet(ds, maxRows, allPoints);
        if (measDs is { Cubes.Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("Measurements:");
            foreach (var (name, cube) in measDs.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                PrintCube($"  {name}", cube, maxRows, allPoints);
        }

        if (exportPath is not null)
        {
            var exportDs = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
            if (!ExportLoadpull(exportDs, exportPath)) return 1;
            Console.WriteLine($"Wrote {exportPath}");
        }

        return LoadpullExitCode(ds);
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
}

/// <summary>
/// Writes a loadpull result. <c>.spl</c> and <c>.lpcwave</c> go through the loadpull writers rather
/// than <see cref="DataSetExporter"/> — those two formats are the loadpull interchange the Data
/// Display itself reads back, so a headless run can produce a file the GUI opens as a measured
/// surface. Everything else is the ordinary <c>.mat</c>/<c>.npy</c>/<c>.txt</c> path.
/// </summary>
static bool ExportLoadpull(DataSet ds, string path)
{
    string ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext is not (".spl" or ".lpcwave"))
    {
        var format = FormatFromExtension(path);
        DataSetExporter.Export(ds, path, format, new ExportOptions(Format: format));
        return true;
    }

    // The writers take the GROUP holding the loadpull cubes. A plain run puts them at the top level;
    // a swept one leaves them in the sweep's own group, so the group is searched for rather than
    // assumed — an unfound group would otherwise surface as "no frequency blocks", which describes
    // the symptom and not the cause.
    string? group = null;
    foreach (var g in ds.Groups)
        if (ds.CubesIn(g).ContainsKey("GammaLoad")) { group = g; break; }

    if (group is null)
    {
        Console.Error.WriteLine(
            $"Cannot write {ext}: this result carries no loadpull surface (no GammaLoad cube). " +
            "A pursuit that found no optimum has no follow-on grid to export — use .npy/.mat to " +
            "keep what it did produce.");
        return false;
    }

    if (ext == ".spl") RfCore.Loadpull.SplWriter.WriteSpl(ds, path, group);
    else               RfCore.Loadpull.LpcwaveWriter.WriteLpcwave(ds, path, group);
    return true;
}

/// <summary>
/// <c>2</c> when the run produced nothing usable — every grid point failed to converge, or a pursuit
/// found neither optimum. <b>Not</b> the <c>hb</c> verb's rule: a loadpull grid in which SOME points
/// do not converge is a normal, useful result (the edge of a Γ grid routinely will not), and failing
/// the whole run on it would make the exit code useless in a script.
/// </summary>
static int LoadpullExitCode(DataSet ds)
{
    bool sawGrid = false, sawLive = false, sawPursuit = false, sawOptimum = false;

    foreach (var group in ds.Groups)
    {
        var cubes = ds.CubesIn(group);

        if (cubes.TryGetValue("StopCode", out var stop) && stop.DataKind == DataKind.Real)
        {
            sawGrid = true;
            // 2 = NonConvergence, 3 = NoConvergedSeed (LoadpullEngine's own wire encoding).
            foreach (var c in stop.RealValues) if (c < 1.5) { sawLive = true; break; }
        }

        foreach (var key in (string[])["MXP_Converged", "MXE_Converged"])
            if (cubes.TryGetValue(key, out var conv) && conv.DataKind == DataKind.Real)
            {
                sawPursuit = true;
                if (conv.RealValues.Any(v => v != 0.0)) sawOptimum = true;
            }
    }

    if (sawGrid    && !sawLive)    return 2;
    if (sawPursuit && !sawOptimum && !sawGrid) return 2;
    return 0;
}

/// <summary>
/// Applies the command-line overrides by REPLACING the loadpull / pursuit directive in the TestBench,
/// for the reason <see cref="ApplyHbOverrides"/> gives: the directive fields are <c>init</c>-only and
/// the netlist is the one source both the engine and the sweep engine read. Returns the refreshed
/// top, since the object it named may have just been replaced.
/// </summary>
static Analysis ApplyLoadpullOverrides(
    TestBench tb, Analysis top, string? gridPath, string? outGridPath,
    int? maxHarm, double? tol, int? maxIter, double? compression,
    double? pinStart, double? pinStep, double? pinMax)
{
    if (gridPath is null && outGridPath is null && maxHarm is null && tol is null && maxIter is null &&
        compression is null && pinStart is null && pinStep is null && pinMax is null)
        return top;

    string? R(double? v) => v?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    string? I(int?    v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    for (int i = 0; i < tb.Analyses.Count; i++)
    {
        switch (tb.Analyses[i])
        {
            case LoadpullAnalysis l:
                tb.Analyses[i] = new LoadpullAnalysis(l.Name)
                {
                    ToneExpr             = l.ToneExpr,
                    ToneUnit             = l.ToneUnit,
                    LoadTunerName        = l.LoadTunerName,
                    SourceTunerName      = l.SourceTunerName,
                    GridPath             = gridPath ?? l.GridPath,
                    PinStartExpr         = R(pinStart)   ?? l.PinStartExpr,
                    PinStepExpr          = R(pinStep)    ?? l.PinStepExpr,
                    PinMaxExpr           = R(pinMax)     ?? l.PinMaxExpr,
                    MaxHarmonicExpr      = I(maxHarm)    ?? l.MaxHarmonicExpr,
                    SweepExpr            = l.SweepExpr,
                    TuneHarmExpr         = l.TuneHarmExpr,
                    CompressionExpr      = R(compression) ?? l.CompressionExpr,
                    GainTypeExpr         = l.GainTypeExpr,
                    TickleExpr           = l.TickleExpr,
                    MaxIterExpr          = I(maxIter)    ?? l.MaxIterExpr,
                    FFTOverSampleExpr    = l.FFTOverSampleExpr,
                    TolExpr              = R(tol)        ?? l.TolExpr,
                    DriveSteppingExpr    = l.DriveSteppingExpr,
                    GuardHarmonicExpr    = l.GuardHarmonicExpr,
                    ContinuityMarginExpr = l.ContinuityMarginExpr,
                    SourceDirectory      = l.SourceDirectory,
                    Enabled              = l.Enabled,
                };
                break;

            case LoadpullPursuitAnalysis p:
                tb.Analyses[i] = new LoadpullPursuitAnalysis(p.Name)
                {
                    ToneExpr                  = p.ToneExpr,
                    ToneUnit                  = p.ToneUnit,
                    LoadTunerName             = p.LoadTunerName,
                    SourceTunerName           = p.SourceTunerName,
                    PinStartExpr              = R(pinStart)   ?? p.PinStartExpr,
                    PinStepExpr               = R(pinStep)    ?? p.PinStepExpr,
                    PinMaxExpr                = R(pinMax)     ?? p.PinMaxExpr,
                    MaxHarmonicExpr           = I(maxHarm)    ?? p.MaxHarmonicExpr,
                    SweepExpr                 = p.SweepExpr,
                    TuneHarmExpr              = p.TuneHarmExpr,
                    CompressionExpr           = R(compression) ?? p.CompressionExpr,
                    GainTypeExpr              = p.GainTypeExpr,
                    TickleExpr                = p.TickleExpr,
                    MaxIterExpr               = I(maxIter)    ?? p.MaxIterExpr,
                    FFTOverSampleExpr         = p.FFTOverSampleExpr,
                    TolExpr                   = R(tol)        ?? p.TolExpr,
                    DriveSteppingExpr         = p.DriveSteppingExpr,
                    GuardHarmonicExpr         = p.GuardHarmonicExpr,
                    ContinuityMarginExpr      = p.ContinuityMarginExpr,
                    EffTypeExpr               = p.EffTypeExpr,
                    ZsourceOBOExpr            = p.ZsourceOBOExpr,
                    SearchMethodExpr          = p.SearchMethodExpr,
                    OutputGridPath            = outGridPath ?? p.OutputGridPath,
                    Vswr1Expr                 = p.Vswr1Expr,
                    Vswr1ResolutionExpr       = p.Vswr1ResolutionExpr,
                    Vswr2Expr                 = p.Vswr2Expr,
                    Vswr2ResolutionExpr       = p.Vswr2ResolutionExpr,
                    KeepNonconvergingExpr     = p.KeepNonconvergingExpr,
                    NonconvergentVswrExpr     = p.NonconvergentVswrExpr,
                    CreateLoadpullResultExpr  = p.CreateLoadpullResultExpr,
                    LoadpullResultZsourceExpr = p.LoadpullResultZsourceExpr,
                    SourceDirectory           = p.SourceDirectory,
                    Enabled                   = p.Enabled,
                };
                break;
        }
    }

    return tb.Analyses.First(a => a.Name == top.Name);
}

// ── Loadpull result printing ──────────────────────────────────────────────────

/// <summary>
/// Prints a loadpull or pursuit result: the optima first when there are any, then one row per Γ grid
/// point carrying that point's OUTCOME and its figures of merit at the drive it stopped at.
///
/// <para><b>A row per grid point, not a cube dump, is the readable form here.</b> A loadpull's cubes
/// are [gridPoint × pinStep] — a 61-point grid driven up in 1 dB steps is a 61 × 30 table per FOM,
/// and eight of those scroll a terminal without answering the question anybody runs a loadpull to
/// ask, which is where the good terminations are and whether the drive was high enough to find them.
/// The full cubes are still one <c>--all</c> away.</para>
/// </summary>
static void PrintLoadpullDataSet(DataSet ds, int maxRows, bool allPoints)
{
    foreach (var group in ds.Groups)
    {
        var cubes = ds.CubesIn(group);
        if (cubes.Count == 0) continue;
        if (ds.Groups.Count > 1 || group != DataSet.DefaultGroup)
            Console.WriteLine($"[{(group == DataSet.DefaultGroup ? "(default)" : group)}]");

        if (cubes.ContainsKey("MXP_Converged") || cubes.ContainsKey("MXE_Converged"))
            PrintPursuitOptima(cubes);

        if (cubes.ContainsKey("StopCode"))
            PrintLoadpullGrid(cubes, maxRows, allPoints);

        if (!allPoints) continue;

        foreach (var (name, cube) in cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            PrintCube($"  {name}", cube, maxRows, allPoints);
        }
    }
}

/// <summary>
/// The pursuit's answer: the maximum-power and maximum-efficiency terminations it converged on.
/// A non-converged optimum is printed as such rather than being omitted — the pursuit still publishes
/// the last termination it looked at, and a blank line there reads as "the search found nothing" when
/// what happened is "nothing it tried reached compression".
/// </summary>
static void PrintPursuitOptima(IReadOnlyDictionary<string, DataCube> cubes)
{
    double S(string name) => cubes.TryGetValue(name, out var c) && c.DataKind == DataKind.Real && c.RealValues.Length > 0
        ? c.RealValues[0] : double.NaN;

    void One(string tag, string title, string valueLabel, string valueUnit)
    {
        bool converged = S($"{tag}_Converged") != 0.0;
        double z_re = S($"{tag}_ZRe"), z_im = S($"{tag}_ZIm");
        // MXE_Eff is published as a FRACTION (the engine's internal DE/PAE unit); the follow-on grid's
        // own Efficiency/PAE columns are percent, so printing the optimum unscaled next to them reads
        // as a 0.7 % efficiency beside a 70 % grid.
        // The Zload of a NON-converged optimum is still the last termination the search looked at, and
        // worth printing. Its criterion is not: the engine publishes 0 there, and "Pout=0 dBm" next to
        // "DID NOT converge" reads as a measured zero rather than as an absent number.
        double value = tag == "MXP" ? S("MXP_PoutDbm") : S("MXE_Eff") * 100.0;
        string shown = converged ? $"{value:G5} {valueUnit}" : "—";
        Console.WriteLine(
            $"  {title,-26} {(converged ? "converged" : "DID NOT converge")}   " +
            $"{valueLabel}={shown}   " +
            $"Zload={FormatOhms(z_re, z_im)}" +
            (S($"{tag}_HasZsource") != 0.0
                ? $"   Zsource={FormatOhms(S($"{tag}_ZsourceRe"), S($"{tag}_ZsourceIm"))}"
                : ""));
    }

    Console.WriteLine("  Pursuit optima:");
    One("MXP", "MXP (max power)",      "Pout", "dBm");
    One("MXE", "MXE (max efficiency)", "Eff",  "%");

    double queried = S("CacheCount"), unscorable = S("UnscorableCount"), recomm = S("RecommTermCount");
    if (!double.IsNaN(queried))
        Console.WriteLine(
            $"  {(int)queried} termination(s) queried" +
            (unscorable > 0 ? $", {(int)unscorable} could not be scored (never reached compression)" : "") +
            (recomm    > 0 ? $", {(int)recomm} recommended termination(s)" : ""));
    Console.WriteLine();
}

/// <summary>
/// One row per Γ grid point: where it was, how it stopped, and its FOMs at the LAST converged,
/// non-tickle drive step — which is the compression point when the point compressed, and the highest
/// drive it managed otherwise. Reading a fixed drive index instead would report the FOMs of whatever
/// rung happened to sit there, mixing compressed and uncompressed points in one column.
/// </summary>
static void PrintLoadpullGrid(IReadOnlyDictionary<string, DataCube> cubes, int maxRows, bool allPoints)
{
    // The engine's own StopCode encoding (LoadpullEngine.BuildLoadpullDataSet), mirrored here for the
    // same reason LoadpullRunSummary mirrors it: it is a wire value in a published DataCube, and a
    // shared enum could renumber it under a .npy written by an older build.
    static string StopName(double c) => c switch
    {
        1 => "compressed",
        2 => "no converge",
        3 => "no seed",
        _ => "max drive",
    };

    var stop = cubes["StopCode"];
    if (stop.DataKind != DataKind.Real) return;

    // A SWEPT loadpull prepends one axis per nesting level, so the grid axis is not necessarily the
    // first — and the whole result is not one grid. Found by NAME rather than by position: taking the
    // last axis would silently read a two-frequency run as one grid of twice the size, whose rows are
    // labelled with the wrong terminations for half of it.
    int gridDim = -1;
    for (int d = 0; d < stop.Axes.Count; d++)
        if (stop.Axes[d].Name == "gridPoint") { gridDim = d; break; }
    if (gridDim < 0) gridDim = stop.Axes.Count - 1;
    if (gridDim < 0) return;

    int  nGrid = stop.Axes[gridDim].Length;
    long outer = 1;
    for (int d = 0; d < gridDim; d++) outer *= stop.Axes[d].Length;
    var outerAxes = stop.Axes.Take(gridDim).ToList();

    var codes = stop.RealValues;
    bool haveGamma = cubes.TryGetValue("GammaLoad", out var gammaCube) && gammaCube.DataKind == DataKind.Complex;
    bool haveZ     = cubes.TryGetValue("ZLoad",     out var zCube)     && zCube.DataKind     == DataKind.Complex;

    // Every FOM is [outer… x gridPoint x pinStep]; the pin axis length comes from whichever is present.
    var conv = Get("Converged");
    if (conv is null) return;
    int nPin = conv.Axes.Count > 0 ? conv.Axes[^1].Length : 1;
    if (nPin <= 0 || conv.RealValues.Length < outer * nGrid * nPin) return;

    // Both spellings, because both reach here: a plain loadpull is Enriched (Pout_dBm / Gt_dB /
    // Efficiency-in-%) while a PURSUIT's follow-on grid is not, and carries the engine's raw names
    // with DE and PAE still as fractions. Reading only one set prints a table of em-dashes for the
    // other — which looks like a run that produced no figures of merit rather than like a naming
    // mismatch.
    var    tickle   = Get("IsTickle");
    var    pavl     = Get("PavlDbm");
    var    pout     = Get("Pout_dBm")   ?? Get("Pout");
    var    gt       = Get("Gt_dB")      ?? Get("Gt");
    var    de       = Get("Efficiency") ?? Get("DE");
    var    pae      = Get("PAE");
    double effScale = cubes.ContainsKey("Efficiency") ? 1.0 : 100.0;

    int compressed = codes.Count(c => c == 1), notConv = codes.Count(c => c is 2 or 3);
    int maxDrive   = codes.Length - compressed - notConv;

    Console.WriteLine(
        $"  Grid: {nGrid} point(s)" + (outer > 1 ? $" x {outer} sweep point(s)" : "") +
        $" — {compressed} reached compression" +
        (maxDrive > 0 ? $", {maxDrive} stopped at max drive" : "") +
        (notConv  > 0 ? $", {notConv} did not converge" : ""));
    if (compressed == 0)
        Console.WriteLine("  Nothing reached compression — raise --pin's max (or the directive's PinMax).");

    for (long o = 0; o < outer; o++)
    {
        Console.WriteLine();
        if (outerAxes.Count > 0)
            Console.WriteLine($"  [{RowLabel(OuterAxesPlusOne(outerAxes), o)}]");

        Console.WriteLine($"    {"#",3}  {"GammaLoad",-20}{"ZLoad (ohm)",-22}{"stop",-13}" +
                          $"{"Pavl",9}{"Pout",9}{"Gt",8}{"DE%",8}{"PAE%",8}");

        int shown = allPoints ? nGrid : Math.Min(nGrid, maxRows);
        for (int g = 0; g < shown; g++)
        {
            long flat = o * nGrid + g;                 // index into the [outer x grid] cubes
            long fomBase = flat * nPin;                // index into the [outer x grid x pin] cubes

            // The last converged, non-tickle drive step for this point.
            int at = -1;
            for (int k = nPin - 1; k >= 0; k--)
            {
                long idx = fomBase + k;
                if (conv.RealValues[idx] == 0.0) continue;
                if (tickle is not null && tickle.RealValues[idx] != 0.0) continue;
                at = k;
                break;
            }

            string gam = haveGamma && flat < gammaCube!.ComplexValues.Length
                ? FormatGamma(gammaCube.ComplexValues[flat]) : "";
            string z = haveZ && flat < zCube!.ComplexValues.Length
                ? FormatOhms(zCube.ComplexValues[flat].Real, zCube.ComplexValues[flat].Imaginary) : "";

            Console.WriteLine(
                $"    {g,3}  {gam,-20}{z,-22}{StopName(codes[flat]),-13}" +
                $"{Cell(pavl, fomBase, at),9}{Cell(pout, fomBase, at),9}{Cell(gt, fomBase, at),8}" +
                $"{Cell(de, fomBase, at, effScale),8}{Cell(pae, fomBase, at, effScale),8}");
        }
        if (shown < nGrid)
            Console.WriteLine($"    … {nGrid - shown} more point(s) — use --all or --rows N");
    }
    Console.WriteLine();

    DataCube? Get(string name)
        => cubes.TryGetValue(name, out var c) && c.DataKind == DataKind.Real ? c : null;

    // RowLabel labels every axis BUT the last, so the outer axes are handed to it with one throwaway
    // axis appended rather than being re-implemented here.
    static IReadOnlyList<Axis> OuterAxesPlusOne(List<Axis> outerAxes)
        => [.. outerAxes, new Axis("_", [0.0])];

    static string Cell(DataCube? cube, long fomBase, int at, double scale = 1.0)
    {
        if (cube is null || at < 0) return "—";
        long idx = fomBase + at;
        if (idx >= cube.RealValues.Length) return "—";
        double v = cube.RealValues[idx] * scale;
        return double.IsNaN(v) ? "—" : v.ToString("F2");
    }
}

static string FormatGamma(Complex g)
    => $"{g.Magnitude:F4} ∠{g.Phase * 180.0 / Math.PI,7:F1}";

static string FormatOhms(double re, double im)
    => double.IsNaN(re) ? "—" : $"{re:F2}{(im >= 0 ? "+" : "-")}j{Math.Abs(im):F2}";

// ── electromagnetic extraction ────────────────────────────────────────────────

/// <summary>
/// `circuitrf em &lt;setup.cem&gt;` — runs one EM setup and writes what the Simulate button writes.
///
/// <para><b>This verb owns no EM logic.</b> It resolves two paths, calls
/// <see cref="EmSetupResolver"/> and <see cref="EmRunService"/>, and reports. Everything that decides
/// an answer — which kernel runs, how the geometry is meshed, what is refused — lives in
/// CircuitRF.Design and src/Engine/Mom and is the same code the GUI drives. That is the whole point
/// of brief-cli-em-verb.md: a headless run and a Simulate must produce the same file, and they do
/// because there is only one implementation of every step between them.</para>
/// </summary>
static int RunEm(string[] args)
{
    string? input = null, output = null, workspace = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o" or "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            case "--workspace" when i + 1 < args.Length:
                workspace = args[++i];
                break;
            default:
                if (!args[i].StartsWith('-')) input = args[i];
                break;
        }
    }

    if (input is null)
    {
        Console.Error.WriteLine("em: input .cem file required");
        Console.Error.WriteLine("Usage: circuitrf em <setup.cem> [-o out.sNp] [--workspace <file.cws>]");
        return 1;
    }
    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"File not found: {input}");
        return 1;
    }

    string cemPath = Path.GetFullPath(input);

    EmSetup setup;
    try
    {
        setup = EmSetupPersistence.LoadFromFile(cemPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not read '{cemPath}': {ex.Message}");
        return 1;
    }

    // R-emcli-5 — a WALK-UP, not a flag. The .cem's own ancestor .cws is what LayoutRef is relative
    // to, exactly as it is in the GUI; with no workspace above it the reference falls back to the
    // .cem's own directory, which is already-specified behaviour rather than a headless special case.
    // --workspace overrides the walk, for a .cem being run from outside its own tree.
    string? cwsPath = workspace is null
        ? WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cemPath))
        : Path.GetFullPath(workspace);

    if (workspace is not null && !File.Exists(cwsPath!))
    {
        Console.Error.WriteLine($"Workspace file not found: {cwsPath}");
        return 1;
    }

    Console.Error.WriteLine(cwsPath is null
        ? $"[circuitRF] no workspace above '{Path.GetFileName(cemPath)}' — references resolve " +
          "against its own directory"
        : $"[circuitRF] workspace: {cwsPath}");

    var resolution = EmSetupResolver.Resolve(cemPath, setup.LayoutRef, cwsPath, new TechnologyCache());

    // The resolver's diagnostics are warnings about the SETUP, not the run's own notes, and they go
    // out before anything long starts — "the technology did not resolve" is the sentence that
    // explains a refusal three lines later.
    foreach (var d in resolution.Diagnostics)
        Console.Error.WriteLine($"warning: {d}");

    if (resolution.LayoutPath is { } lp) Console.Error.WriteLine($"[circuitRF] layout: {lp}");
    if (resolution.TechnologyPath is { } tp) Console.Error.WriteLine($"[circuitRF] technology: {tp}");

    // R-emcli-7 — with no -o the run writes where the GUI writes, and that is not a default this file
    // gets to choose: EmRunService.ResolveSnpPath is a PREDICTABLE path by design so a schematic's
    // SnP reference stays valid across re-runs, and a headless run that minted a different filename
    // would orphan every one of them. -o moves the Touchstone and nothing else — and it goes in
    // through the setup's own override field, the one the panel writes, so there is no second naming
    // rule to keep in step.
    if (output is not null) setup.SnpOutputPathOverride = Path.GetFullPath(output);

    // The GUI's results root is <workspace>/results, falling back to the scratch recovery session
    // when no workspace is open. Headless there is no recovery session, so a loose .cem falls back to
    // its OWN directory — the same fallback its LayoutRef already uses, rather than a third rule.
    string resultsBase = cwsPath is { } cws ? Path.GetDirectoryName(cws)! : Path.GetDirectoryName(cemPath)!;
    string resultsRoot = Path.Combine(resultsBase, "results");

    EmRunResult result;
    try
    {
        result = EmRunService.Run(setup, resolution.Source, resultsRoot, default, EmProgressToStderr());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    // R-emcli-6 — THREE lists, kept apart, because they ask three different things of the reader.
    // Notes are the run explaining itself, warnings are things to act on, errors are things the user
    // asked for and did not get. Flattening them into one list is the exact defect the split was
    // introduced to fix, and it is just as wrong on a terminal as it was in the Messages region.
    foreach (var n in result.Notes ?? []) Console.Error.WriteLine($"note: {n}");
    foreach (var w in result.Warnings)     Console.Error.WriteLine($"warning: {w}");
    foreach (var e in result.Errors ?? []) Console.Error.WriteLine($"error: {e}");

    // R-emcli-8 — a refusal stays a refusal. Each status carries a written explanation of what is
    // wrong with THIS setup; collapsing them into "EM failed" throws away the only part a user can
    // act on.
    if (result.Status != EmRunStatus.Ok)
    {
        Console.Error.WriteLine($"{DescribeEmStatus(result.Status)}: {result.Error}");
        return result.Status == EmRunStatus.Cancelled ? 130 : 1;
    }

    Console.WriteLine($"EM setup:  {(setup.Name.Length > 0 ? setup.Name : Path.GetFileNameWithoutExtension(cemPath))}");
    Console.WriteLine($"Kernel:    {result.KernelName} ({result.Kind})");

    if (result.Data is { } ds)
    {
        // Every group, not just the default one: an EM DataSet carries S alongside a diagnostics
        // group ("tline" or "planar"), and which of them is the default is the writer's business.
        var freqAxis = ds.Cubes.Values
            .Concat(ds.Groups.SelectMany(g => ds.CubesIn(g).Values))
            .SelectMany(c => c.Axes)
            .FirstOrDefault(a => a.Name.Contains("freq", StringComparison.OrdinalIgnoreCase));
        if (freqAxis is not null)
            Console.WriteLine($"Points:    {freqAxis.Length}");
    }

    if (result.SnpPath is { } snp) Console.WriteLine($"Wrote {snp}");
    if (result.NpyPath is { } npy) Console.WriteLine($"Wrote {npy}");

    return 0;
}

/// <summary>The run's own progress, on stderr — §3.1's split, so `circuitrf em x.cem > summary.txt`
/// still shows a full-wave sweep moving. A de-embedded point costs tens of seconds at the shipping
/// mesh, so a run reporting nothing is indistinguishable from a hung one.</summary>
static RunControl EmProgressToStderr()
{
    string lastLine = "";
    return new RunControl
    {
        Progress = new Progress<RunProgress>(p =>
        {
            string line = p.Total > 0
                ? $"[{p.Completed}/{p.Total}] {p.Stage}"
                : $"[{p.Completed}] {p.Stage}";
            // Adaptive sampling has no honest denominator and reports the same stage repeatedly; only
            // a CHANGED line is worth a terminal row.
            if (line == lastLine) return;
            lastLine = line;
            Console.Error.WriteLine(line);
        }),
    };
}

static string DescribeEmStatus(EmRunStatus status) => status switch
{
    EmRunStatus.Refused     => "Refused",
    EmRunStatus.NoLayout    => "No layout",
    EmRunStatus.EngineError => "Engine error",
    EmRunStatus.Cancelled   => "Cancelled",
    _                       => "Failed",
};

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

        // A headless run reaches here without ever throwing, so the exception paths' explanation
        // would never be printed. The one failure worth translating looks, in raw dyld text, exactly
        // like a broken file — and it is not one.
        string? diagnosis = WorkerOutputDiagnosis.Explain(log);
        if (diagnosis is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(diagnosis);
        }
    }
}

// ── HB helpers ────────────────────────────────────────────────────────────────

/// <summary>
/// Chooses which analysis a run verb dispatches — shared by <c>hb</c>, <c>lp</c> and <c>lpp</c>,
/// which differ only in the base analysis type they are looking for.
///
/// <para>The rule that matters: a <c>parametric_sweep</c> wrapping the analysis must be dispatched
/// AT THE SWEEP. Naming the inner analysis runs one point and silently loses the sweep axis, which
/// looks like a converged result rather than like a mistake — so an inner name is promoted to its
/// outermost enabled wrapper rather than being honoured literally. A frequency-swept loadpull is
/// exactly this shape, so the rule is not HB-specific and neither is this function.</para>
/// </summary>
/// <param name="isBase">True for the analysis type this verb runs.</param>
/// <param name="kindLabel">What to call it in a message ("HB", "loadpull", "loadpull-pursuit").</param>
/// <param name="directiveHint">The directive to suggest when the netlist declares none.</param>
static Analysis? SelectTop(TestBench tb, string? requested, Func<Analysis, bool> isBase,
                           string kindLabel, string directiveHint, out string? why)
{
    why = null;

    // Names referenced as somebody's inner are not chain roots.
    var inner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var a in tb.Analyses)
        if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
            inner.Add(ps.InnerAnalysisName);

    // Roots whose chain bottoms out in an HB and actually runs.
    var candidates = new List<Analysis>();
    foreach (var root in tb.Analyses)
    {
        if (inner.Contains(root.Name)) continue;
        var top = AnalysisChain.ResolveEffectiveTop(root, tb);
        if (top is null || !top.Enabled) continue;
        if (!AnalysisChain.IsChainRunnable(top, tb)) continue;
        if (BaseOfChain(top, tb) is { } base_ && isBase(base_)) candidates.Add(top);
    }

    if (requested is not null)
    {
        var named = tb.Analyses.FirstOrDefault(a => a.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (named is null)
        {
            why = $"no analysis named '{requested}'. Declared: " +
                  (tb.Analyses.Count > 0 ? string.Join(", ", tb.Analyses.Select(a => a.Name)) : "(none)");
            return null;
        }
        // Promote to the outermost chain that contains it, so -a HB1 still runs SW1's sweep.
        var owner = candidates.FirstOrDefault(c => ChainContains(c, named.Name, tb));
        if (owner is not null && !ReferenceEquals(owner, named))
            Console.Error.WriteLine(
                $"[circuitRF] '{named.Name}' is the inner analysis of '{owner.Name}' — running '{owner.Name}' " +
                $"so the sweep axis is not lost.");
        return owner ?? named;
    }

    if (candidates.Count == 0)
    {
        why = tb.Analyses.Any(isBase)
            ? $"the netlist declares a {kindLabel} analysis but its chain is disabled."
            : $"the netlist declares no {kindLabel} analysis ({directiveHint}).";
        return null;
    }
    if (candidates.Count > 1)
        Console.Error.WriteLine(
            $"[circuitRF] {candidates.Count} {kindLabel} chains declared ({string.Join(", ", candidates.Select(c => c.Name))}); " +
            $"running '{candidates[0].Name}'. Use -a <name> to pick another.");
    return candidates[0];
}

/// The base (non-sweep) analysis a chain bottoms out in.
static Analysis? BaseOfChain(Analysis top, TestBench tb)
{
    Analysis? a = top;
    for (int guard = 0; a is ParametricSweepAnalysis ps && guard < 64; guard++)
        a = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
    return a;
}

static bool ChainContains(Analysis top, string name, TestBench tb)
{
    Analysis? a = top;
    for (int guard = 0; a is not null && guard < 64; guard++)
    {
        if (a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (a is not ParametricSweepAnalysis ps) return false;
        a = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
    }
    return false;
}

/// <summary>
/// Applies command-line HB overrides by REPLACING each HB directive in the TestBench — the directive
/// fields are <c>init</c>-only, and the netlist is the single source the engine and the sweep engine
/// both read, so an override has to land there to be seen by both. Returns the refreshed top (the
/// object it named may have just been replaced).
/// </summary>
static Analysis ApplyHbOverrides(TestBench tb, Analysis top,
                                 int? maxHarm, int? maxMixOrder, double? tol, int? maxIter)
{
    if (maxHarm is null && maxMixOrder is null && tol is null && maxIter is null) return top;

    for (int i = 0; i < tb.Analyses.Count; i++)
    {
        if (tb.Analyses[i] is not HarmonicBalanceAnalysis h) continue;
#pragma warning disable CS0618   // deprecated Sweep= fields are copied verbatim, not interpreted
        tb.Analyses[i] = new HarmonicBalanceAnalysis(h.Name)
        {
            ToneExpr          = h.ToneExpr,
            ToneUnit          = h.ToneUnit,
            NumFreqsExpr      = h.NumFreqsExpr,
            ToneExprs         = h.ToneExprs,
            ToneUnits         = h.ToneUnits,
            MaxMixOrderExpr   = maxMixOrder?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                ?? h.MaxMixOrderExpr,
            MaxHarmonicExpr   = maxHarm?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                ?? h.MaxHarmonicExpr,
            FFTOverSampleExpr = h.FFTOverSampleExpr,
            TolExpr           = tol?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                                ?? h.TolExpr,
            DriveSteppingExpr = h.DriveSteppingExpr,
            GuardHarmonicExpr = h.GuardHarmonicExpr,
            LambdaExpr        = h.LambdaExpr,
            MaxIterExpr       = maxIter?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                ?? h.MaxIterExpr,
            Enabled           = h.Enabled,
            SweepVarName      = h.SweepVarName,
            SweepStartExpr    = h.SweepStartExpr,
            SweepStopExpr     = h.SweepStopExpr,
            SweepStepExpr     = h.SweepStepExpr,
        };
#pragma warning restore CS0618
    }

    return tb.Analyses.First(a => a.Name == top.Name);
}

static AnalysisSettings SolverSettingsFrom(int? maxIter, bool diag)
{
    var d = AnalysisSettings.Default;
    return new AnalysisSettings
    {
        HbMaxIter            = maxIter ?? d.HbMaxIter,
        NonlinearMaxIter     = maxIter ?? d.NonlinearMaxIter,
        HbConsoleDiagnostics = diag,
        ConductanceRegularization = RegularizationMode.Always,
    };
}

static string DescribeTones(HbAnalysisParams p)
    => p.IsMultiTone
        ? "tones " + string.Join(", ", p.ToneFreqsHz.Select(f => $"{f / 1e9:G6} GHz"))
        : $"f0={p.ToneHz / 1e9:G6} GHz";

/// <summary>
/// Evaluates the TestBench's measurements against the run, exactly as the GUI does — including the
/// back-solver, so a measurement may name a LINEAR-interior node (a port behind an N-port, say) and
/// not only the nonlinear interface nodes that appear in the <c>V</c> cube.
/// </summary>
static DataSet? EvaluateMeasurements(TestBench tb, ElaboratedNetlist nl, string analysisName,
                                     DataSet ds, HbRunResult? run)
{
    if (tb.Measurements.Count == 0) return null;

    var results = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase) { [analysisName] = ds };

    Dictionary<string, CircuitRF.Core.Expressions.ILinearBackSolver>? solvers = null;
    if (run?.BackSolver is not null)
        solvers = new Dictionary<string, CircuitRF.Core.Expressions.ILinearBackSolver>(StringComparer.OrdinalIgnoreCase)
            { [analysisName] = run.BackSolver };

    var measDs = new DataSet();
    var errors = new MeasurementEvaluator(tb, nl, results, solvers).EvaluateInto(measDs);
    foreach (var e in errors) Console.Error.WriteLine($"[circuitRF] measurement: {e}");
    return measDs;
}

/// Combines the analysis cubes and the measurement cubes into one DataSet for export, mirroring the
/// GUI's grouped run DataSet (analysis group + "measurements" group).
static DataSet MergeForExport(DataSet ds, DataSet measDs)
{
    var merged = new DataSet();
    foreach (var group in ds.Groups)
        foreach (var (name, cube) in ds.CubesIn(group))
            merged.AddToGroup(group, name, cube);
    foreach (var (name, cube) in measDs.Cubes)
        merged.AddToGroup(DataSet.MeasurementsGroup, name, cube);
    return merged;
}

static ExportFormat FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".npy"          => ExportFormat.Npy,
    ".txt" or ".tsv" => ExportFormat.Tsv,
    _               => ExportFormat.Mat,
};

/// <summary>Reads the run's own Converged scalar; a run without one (a sweep) counts as converged.</summary>
static bool Converged(DataSet ds)
{
    foreach (var group in ds.Groups)
    {
        var cubes = ds.CubesIn(group);
        if (!cubes.TryGetValue("Converged", out var c) || c.DataKind != DataKind.Real) continue;
        foreach (var v in c.RealValues) if (v == 0.0) return false;
    }
    return true;
}

// ── HB result printing ────────────────────────────────────────────────────────

/// <summary>
/// Prints an HB result to the console: convergence, the spectrum axis with each product's frequency,
/// then every cube worth reading as a table. Internal cubes (<c>__</c>-prefixed) are skipped —
/// they carry label metadata the axes already show.
/// </summary>
static void PrintHbDataSet(DataSet ds, int maxRows, bool allPoints)
{
    foreach (var group in ds.Groups)
    {
        var cubes = ds.CubesIn(group);
        if (cubes.Count == 0) continue;
        if (ds.Groups.Count > 1 || group != DataSet.DefaultGroup)
            Console.WriteLine($"[{(group == DataSet.DefaultGroup ? "(default)" : group)}]");

        // Convergence and the tone plan first — a spectrum nobody can trust is worth reading first.
        if (cubes.TryGetValue("Converged", out var conv) && conv.DataKind == DataKind.Real)
        {
            var vals = conv.RealValues;
            int bad  = vals.Count(v => v == 0.0);
            Console.WriteLine(bad == 0
                ? $"  Converged: yes ({vals.Length} solve(s))"
                : $"  Converged: NO — {bad} of {vals.Length} solve(s) did not converge");
        }
        if (cubes.TryGetValue("Residual", out var resid) && resid.DataKind == DataKind.Real)
            Console.WriteLine($"  Residual:  {resid.RealValues.Max():G3} (worst)");
        if (cubes.TryGetValue("ToneFreqs", out var tones) && tones.DataKind == DataKind.Real)
            Console.WriteLine($"  Tones:     {string.Join(", ", tones.RealValues.Select(f => $"{f / 1e9:G6} GHz"))}");

        foreach (var (name, cube) in cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            if (name is "Converged" or "Residual" or "ToneFreqs" or "MetaMixOrder") continue;
            PrintCube($"  {name}", cube, maxRows, allPoints);
        }
    }
}

/// <summary>
/// Prints one cube as a table: the LAST axis across the columns, everything before it down the rows,
/// with axis labels where the axis carries them. A complex cube prints magnitude and phase — the
/// spectrum is what is being read, and a raw re/im pair is not readable as one.
/// </summary>
static void PrintCube(string label, DataCube cube, int maxRows, bool allPoints)
{
    if (cube.Rank == 0)
    {
        Console.WriteLine($"{label} = {(cube.DataKind == DataKind.Real
            ? cube.RealValues[0].ToString("G6")
            : FormatComplex(cube.ComplexValues[0]))}");
        return;
    }

    var  axes    = cube.Axes;
    var  last    = axes[^1];
    int  cols    = last.Length;
    long rows    = 1;
    for (int d = 0; d < axes.Count - 1; d++) rows *= axes[d].Length;
    bool complex = cube.DataKind == DataKind.Complex;

    Console.WriteLine($"{label}  [{string.Join(" x ", axes.Select(a => $"{a.Name}:{a.Length}"))}]" +
                      (complex ? "  (mag ∠deg)" : ""));

    // Column headers: the last axis's labels, else its values (Hz axes read as GHz).
    var colHead = new string[cols];
    for (int c = 0; c < cols; c++)
        colHead[c] = last.Labels is { } lb && c < lb.Length
            ? lb[c]
            : last.Unit == "Hz" ? $"{last.Values[c] / 1e9:G5}G" : last.Values[c].ToString("G5");

    int width   = complex ? 22 : 13;
    int shownCols = allPoints ? cols : Math.Min(cols, 12);
    Console.WriteLine("    " + new string(' ', 20) +
                      string.Concat(colHead.Take(shownCols).Select(h => Pad(h, width))) +
                      (shownCols < cols ? $"  … +{cols - shownCols}" : ""));

    long shownRows = allPoints ? rows : Math.Min(rows, maxRows);
    var  complexData = complex ? cube.ComplexValues : null;
    var  realData    = complex ? null : cube.RealValues;

    for (long r = 0; r < shownRows; r++)
    {
        var rowHead = RowLabel(axes, r);
        var cells   = new System.Text.StringBuilder();
        for (int c = 0; c < shownCols; c++)
        {
            long idx = r * cols + c;
            cells.Append(Pad(complex ? FormatComplex(complexData![idx]) : realData![idx].ToString("G6"), width));
        }
        Console.WriteLine("    " + Pad(rowHead, 20) + cells);
    }
    if (shownRows < rows)
        Console.WriteLine($"    … {rows - shownRows} more row(s) — use --all or --rows N");

    static string Pad(string s, int w) => s.Length >= w ? s[..(w - 1)] + " " : s.PadRight(w);
}

/// Row label for a flattened multi-index over every axis but the last.
static string RowLabel(IReadOnlyList<Axis> axes, long row)
{
    int lead = axes.Count - 1;
    if (lead == 0) return "";

    var idx = new int[lead];
    for (int d = lead - 1; d >= 0; d--)
    {
        idx[d] = (int)(row % axes[d].Length);
        row   /= axes[d].Length;
    }

    var parts = new string[lead];
    for (int d = 0; d < lead; d++)
    {
        var a = axes[d];
        parts[d] = a.Labels is { } lb && idx[d] < lb.Length
            ? lb[idx[d]]
            : $"{a.Name}={a.Values[idx[d]]:G5}";
    }
    return string.Join(" ", parts);
}

static string FormatComplex(Complex z)
{
    double mag = z.Magnitude;
    if (mag == 0.0) return "0";
    return $"{mag:G5} ∠{z.Phase * 180.0 / Math.PI,7:F1}";
}

static bool TryParseDouble(string s, out double v)
    => double.TryParse(s, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out v);

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
/// <summary>
/// Prints everything the run has to say that is not its result: first the NOTES — what circuitRF
/// worked out and is reporting — then the warnings. Two cursors rather than one, because the two
/// lists fill independently and a single index into either would re-print or skip the other.
/// </summary>
static (int Notes, int Warnings) PrintWarnings(ElaboratedNetlist nl, (int Notes, int Warnings) from = default)
{
    for (; from.Notes < nl.Notes.Count; from.Notes++)
        Console.Error.WriteLine($"[circuitRF] {nl.Notes[from.Notes]}");

    for (; from.Warnings < nl.Warnings.Count; from.Warnings++)
        Console.Error.WriteLine($"[circuitRF] {nl.Warnings[from.Warnings]}");

    return from;
}

// Invariant, always. A command line is a machine-readable interface, like the file formats and the
// expression language: `2.5GHz` must mean the same thing on every machine, so a script, a Makefile or
// a CI job written in one country keeps working in another. This is the CLI's only unqualified
// floating-point parse (brief-localization-groundwork.md §2.3).
static double ParseHz(string s)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    s = s.Trim();
    if (s.EndsWith("GHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3], inv) * 1e9;
    if (s.EndsWith("MHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3], inv) * 1e6;
    if (s.EndsWith("kHz", StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^3], inv) * 1e3;
    if (s.EndsWith("Hz",  StringComparison.OrdinalIgnoreCase)) return double.Parse(s[..^2], inv);
    return double.Parse(s, inv);
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
    Console.WriteLine("  hb     <file.cnl>   (harmonic balance; runs the sweep if one wraps it)");
    Console.WriteLine("  lp     <file.cnl>   (loadpull over the directive's Gamma grid)");
    Console.WriteLine("  lpp    <file.cnl>   (loadpull pursuit: searches for MXP / MXE)");
    Console.WriteLine("  em     <file.cem>   (electromagnetic extraction of the layout it names)");
    Console.WriteLine("  elab   <file.cnl>   (dump elaborated netlist)");
    Console.WriteLine("  convert <in> -o <out>  (layout interchange: any format to any other)");
    Console.WriteLine();
    Console.WriteLine("hb options:");
    Console.WriteLine("  -a, --analysis <name>   which analysis to run (default: the only HB chain)");
    Console.WriteLine("  --set <var=expr>        override a global variable. Repeatable.");
    Console.WriteLine("  --maxharm K             override MaxHarm");
    Console.WriteLine("  --maxmix M              override MaxMixOrder (multi-tone)");
    Console.WriteLine("  --tol t, --max-iter N   override Tol / MaxIter");
    Console.WriteLine("  --rows N, --all         how much of each table to print");
    Console.WriteLine("  --diag                  engine convergence diagnostics to stderr");
    Console.WriteLine("  -o, --export <path>     .mat / .npy / .txt (extension picks the format)");
    Console.WriteLine();
    Console.WriteLine("lp / lpp options:  (-a, --set, --maxharm, --tol, --max-iter, --rows, --all, --diag, -o as above)");
    Console.WriteLine("  --pin start:step:max    override the drive ladder, dBm");
    Console.WriteLine("  --compression dB        override the compression target");
    Console.WriteLine("  --grid <file.gam>       lp only  — override the Gamma grid read");
    Console.WriteLine("  --out-grid <file.gam>   lpp only — override where found terminations are written");
    Console.WriteLine("  -o out.spl / .lpcwave   lp only  — the loadpull interchange formats");
    Console.WriteLine();
    Console.WriteLine("em options:");
    Console.WriteLine("  -o, --output <path>     where the Touchstone goes. Default: the same file");
    Console.WriteLine("                          Simulate writes, so a schematic's SnP reference holds.");
    Console.WriteLine("  --workspace <file.cws>  the workspace paths resolve against. Default: the");
    Console.WriteLine("                          nearest .cws above the .cem, then its own directory.");
    Console.WriteLine();
    Console.WriteLine("convert options:");
    Console.WriteLine("  formats: clay | gdsii | dxf | gerber | board — inferred from the paths");
    Console.WriteLine("           (.clay .gds .dxf .kicad_pcb; a FOLDER is a Gerber file set)");
    Console.WriteLine("  -o, --output <path>     the file to write, or the FOLDER for gerber / clay");
    Console.WriteLine("  --from f, --to f        say the format when the path does not");
    Console.WriteLine("  --cell <name>           which cell, when the source holds several");
    Console.WriteLine("  --name <stem>           what to call the written file set (gerber)");
    Console.WriteLine("  --list-cells            report what the input holds, write nothing");
    Console.WriteLine("  --tech <file.ctech>     the technology to convert against");
    Console.WriteLine("  --keep-cells <dir>      keep the cells an import produced");
    Console.WriteLine("  --dxf-version AC1032    AC1015 | AC1018 | AC1032   --dxf-units <n>");
    Console.WriteLine("  --drill-units mm|inch   --drill-format <int>:<dec>");
    Console.WriteLine("  --drill-zeros leading|trailing");
    Console.WriteLine("  --accept-inferred-drill-format   proceed on a guessed Excellon format");
    Console.WriteLine();
    Console.WriteLine("Options (any command):");
    Console.WriteLine("  --kits <dir>        folder of installed kits, for externally-provided");
    Console.WriteLine("                      devices (ExtDevice Provider=...). Repeatable.");
    Console.WriteLine();
    Console.WriteLine("Frequency format: 1GHz, 100MHz, 1e9 (Hz bare)");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --freq 1GHz:3GHz:50MHz -o hero1.s4p");
    Console.WriteLine("Example: circuitrf hb hero5.cnl --set Pavl_dbm=0 -o hero5.txt");
    Console.WriteLine("Example: circuitrf lp hero3.cnl --pin -20:1:15 -o hero3.spl");
    Console.WriteLine("Example: circuitrf lpp hero3B.cnl --out-grid found.gam -o hero3B.npy");
    Console.WriteLine("Example: circuitrf em  Amp.cem -o /tmp/amp.s2p");
    Console.WriteLine("Example: circuitrf convert Filter.dxf -o gerbers/");
    Console.WriteLine("Example: circuitrf convert fab/ -o board.kicad_pcb");
    return 0;
}
