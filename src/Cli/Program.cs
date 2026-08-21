using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
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

return args[0].ToLowerInvariant() switch
{
    "sparam" => RunSparam(args[1..]),
    "dc"     => RunDc(args[1..]),
    "hb"     => RunHb(args[1..]),
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
        var top = SelectHbTop(tb, analysisName, out string? why);
        if (top is null)
        {
            Console.Error.WriteLine($"hb: {why}");
            return 1;
        }

        var settings = HbSettingsFrom(maxIter, diag);
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

// ── HB helpers ────────────────────────────────────────────────────────────────

/// <summary>
/// Chooses which analysis the <c>hb</c> verb runs.
///
/// <para>The rule that matters: a <c>parametric_sweep</c> wrapping an HB must be dispatched AT THE
/// SWEEP. Naming the inner HB runs one point and silently loses the sweep axis, which looks like a
/// converged result rather than like a mistake — so an inner name is promoted to its outermost
/// enabled wrapper rather than being honoured literally.</para>
/// </summary>
static Analysis? SelectHbTop(TestBench tb, string? requested, out string? why)
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
        if (BaseOfChain(top, tb) is HarmonicBalanceAnalysis) candidates.Add(top);
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
        why = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Any()
            ? "the netlist declares an HB analysis but its chain is disabled."
            : "the netlist declares no harmonic-balance analysis (analysis <name> type=hb ...).";
        return null;
    }
    if (candidates.Count > 1)
        Console.Error.WriteLine(
            $"[circuitRF] {candidates.Count} HB chains declared ({string.Join(", ", candidates.Select(c => c.Name))}); " +
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

static AnalysisSettings HbSettingsFrom(int? maxIter, bool diag)
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
    Console.WriteLine("  hb     <file.cnl>   (harmonic balance; runs the sweep if one wraps it)");
    Console.WriteLine("  elab   <file.cnl>   (dump elaborated netlist)");
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
    Console.WriteLine("Options (any command):");
    Console.WriteLine("  --kits <dir>        folder of installed kits, for externally-provided");
    Console.WriteLine("                      devices (ExtDevice Provider=...). Repeatable.");
    Console.WriteLine();
    Console.WriteLine("Frequency format: 1GHz, 100MHz, 1e9 (Hz bare)");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --freq 1GHz:3GHz:50MHz -o hero1.s4p");
    Console.WriteLine("Example: circuitrf hb hero5.cnl --set Pavl_dbm=0 -o hero5.txt");
    return 0;
}
