// ================================================================
//  CliEntry.cs — the CLI's dispatch, as a function anyone in this assembly can call.
//
//  This file WAS Program.cs's top-level statements and their local functions, moved wholesale
//  (brief-automation-5-protocol-adapter.md). Nothing in the verbs changed; what changed is that
//  they are now reachable from something other than the process entry point.
//
//  ── Why the move was necessary ────────────────────────────────────────────────────────────────
//
//  R-aut-13: nothing may be reachable through the protocol server that is not reachable through the
//  CLI, and vice versa. The cheapest way to make that TRUE BY CONSTRUCTION rather than by care is
//  for the adapter to call the verb, not to re-implement it — and a local function of a top-level
//  program is private to `<Main>$` and callable by nobody. So `serve` translates a tool call into an
//  argument vector and hands it to `Run`, which is the same function `Program.cs` hands the real
//  command line to. The parity gate (ServeProtocolAdapterTests) then compares two documents that
//  came out of one code path, which is what makes a drift between them impossible rather than
//  merely tested for.
//
//  ── What a second caller costs, and what is done about it ─────────────────────────────────────
//
//  `Run` was written once-per-process and is now called repeatedly. Three pieces of state outlive a
//  call and each is handled here rather than left for a caller to remember:
//
//    * JsonRun's collectors — reset by `serve` before every call (JsonRun.Reset).
//    * The device-worker log subscription — an event handler, so a second subscription would print
//      every worker line twice. Hooked once (_workerLogHooked).
//    * Kit resolvers — ExternalDeviceRegistry.AddResolver has no remove, so the same --kits folder
//      set is added once and remembered (_kitsAdded).
// ================================================================

using System.Linq;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Stability;
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
using RfCore.Loadpull;

namespace CircuitRF.Cli;

public static class CliEntry
{
    /// <summary>Whether the device-worker log event has been subscribed. See Run.</summary>
    private static bool _workerLogHooked;

    /// <summary>This invocation's <c>--kits</c> folders, as given. Read by <c>serve --print-config</c>,
    /// which has to write them into the configuration it prints and never sees them as arguments,
    /// because they are taken before dispatch.</summary>
    internal static IReadOnlyList<string> KitFolders { get; private set; } = [];

    /// <summary>
    /// This invocation's <c>--workspace</c>, as a workspace ROOT: the workspace a LOOSE schematic is
    /// run from, as a window's workspace is in the GUI (<c>SchematicEditModel.HostWorkspaceRoot</c>).
    /// Null without the flag. Reset on every Run, so a <c>serve</c> call never inherits another's.
    /// </summary>
    internal static string? HostWorkspace { get; private set; }

    /// <summary>The <c>--kits</c> folder sets already given a resolver. See Run.</summary>
    private static readonly HashSet<string> _kitsAdded = [];

    /// <summary>
    /// Dispatches one invocation and returns its exit code — the whole CLI, minus the process.
    /// </summary>
    public static int Run(string[] args)
    {
// `--version` first, and plainly: the one line an installer's own smoke check and an agent that has
// just installed circuitRF both read to learn which build answered (R-aut13-1). It is the VERSION
// file's contents, through the assembly attribute every project is stamped with, and nothing else —
// no document, because a caller asking which version this is has not yet decided how to talk to it.
if (args.Length > 0 && args[0] == VersionFlag)
{
    Console.WriteLine(JsonRun.Version());
    return 0;
}

// The glyph outlines a label is flattened with — the ONE thing that has to be installed before any
// verb runs (brief-render-1-render-layer-below-the-firewall.md).
//
// CircuitRF.Render has a [ModuleInitializer] that does this, and it is not enough here: .NET loads
// an assembly on first use, and `convert` reaches GerberExport in CircuitRF.Design without ever
// naming a CircuitRF.Render type — so the module would never load and the initializer would never
// run. LayoutTextOutline would then fall back to SKTypeface.Default and a label flattened by
// `convert` would be a different SHAPE from the same label flattened by the application, which
// ConvertCliVerbTests compares byte for byte. This call is what loads the module, and it is
// greppable in a way a load-order accident is not. Idempotent; costs nothing.
CircuitRF.Render.RenderTypefaceInstaller.Install();

// ── command dispatch ──────────────────────────────────────────────────────────

// --json, --only, --group and AUT-9's --at/--range/--interp/--format/--summary are taken FIRST, and
// before the empty-argument check, so that
// `circuitrf --json` with no verb still produces a document rather than a bare exit code — a caller
// must never have to tell "no output" apart from "output I could not parse" (R-aut1-3). Taking them
// here also means no verb's own argument loop has to learn about them, and `convert`'s
// unknown-option refusal cannot trip over one.
args = JsonRun.TakeFlags(args);

// --kits <dir> makes externally-provided devices work headlessly, the same way opening a workspace
// does in the GUI: point at a folder of installed kits and a netlist naming one resolves it. Taken
// out of the argument list here so every command gets it without repeating the parsing.
args = TakeKitFolders(args, out var kitFolders);
KitFolders = kitFolders;

// --trust-kit <dir> lets ONE run execute a kit's PCell scripts to rebuild its generated cells
// (brief-generated-cells-2 R-gc2-2, cli.md §21). Taken here, like --kits, so every geometry verb
// honours it without learning it — and reset on every Run, so a `serve` call never inherits a grant.
args = CircuitRF.Cli.GeneratedCells.TakeTrustFlags(args);

if (args.Length == 0)
{
    PrintHelp();
    // Recorded, NOT written to stderr: nothing is written there today for a bare invocation, and
    // R-aut0-3 says a script watching stderr must not notice this brief landing.
    JsonRun.Note(CliDiagnostics.NoCommand());
    return JsonRun.Finish(1);
}

// Added ONCE per folder set. ExternalDeviceRegistry has no remove, so a server calling Run
// repeatedly with the same --kits would stack an identical resolver per call.
if (kitFolders.Count > 0 && _kitsAdded.Add(string.Join('\0', kitFolders)))
    ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver(kitFolders));

// A worker's own log, when CRF_WORKER_LOG asks for it. What a worker MEASURES — which nodes are
// free unknowns, which pins carry a temperature, whether the model's Jacobian matches its currents
// — decides how its device is stamped, and a measurement that lands differently on two machines
// produces no error on either: the device stamps cleanly, the numbers stay finite, and one machine
// simply will not converge. On stderr, so it never contaminates piped results.
//
// Hooked once: this is an EVENT, and a second Run in the same process would otherwise print every
// worker line twice — a duplicate that reads as the worker having said it twice.
if (!_workerLogHooked)
{
    _workerLogHooked = true;
    ProcessDeviceWorkerTransport.Logged += log => Console.Error.WriteLine(
        string.IsNullOrWhiteSpace(log.Provider) ? $"worker: {log.Line}" : $"worker '{log.Provider}': {log.Line}");
}

JsonRun.Verb = args[0].ToLowerInvariant();

// AUT-9 R-aut9-9: a narrowing flag whose SPELLING is wrong is refused here, before the verb runs
// at all. The axis NAMES cannot be checked until a result exists (JsonRun.BuildPayload does that);
// `--at freq` with no value can be, and making a caller wait for a solve to learn it would be
// gratuitous.
if (JsonRun.Malformed is { } bad)
    return JsonRun.Finish(JsonRun.Fail(CliDiagnostics.NarrowingMalformed(bad.Option, bad.Text)));

// --workspace <dir | .cws>: the workspace a loose schematic (a Known File outside the workspace
// folder) is run from, so its cell references resolve as they do in that workspace's window. Taken
// here, like --kits, so every verb that reads a schematic honours it without learning it — except
// `em`, which parses the same flag for the same purpose itself, and `convert`, whose --workspace is
// the workspace imported layers are grafted onto.
HostWorkspace = null;
if (JsonRun.Verb is not ("em" or "convert"))
{
    args = TakeHostWorkspace(args, out string? hostGiven, out string? hostRoot);
    if (hostGiven is not null && hostRoot is null)
        return JsonRun.Finish(JsonRun.Fail(CliDiagnostics.HostWorkspaceNotAWorkspace(hostGiven)));
    HostWorkspace = hostRoot;
}

// The verb's function, or null for a word that is not one. ONE table: IsVerb below asks the same
// question of the same switch, so the installed executable's decision "is this a command line or a
// document to open?" cannot drift from what Run will actually do with it (R-aut13-1).
int exit = Dispatch(JsonRun.Verb) is { } run ? run(args[1..]) : UnknownVerb(args[0]);
// Judged after the verb, because only the run knows which kits' modules it asked an interpreter for.
CircuitRF.Cli.GeneratedCells.ReportUnusedPythonModules();
return JsonRun.Finish(exit);
    }

    /// <summary>
    /// The flag that prints the application version and exits 0 — the one argument the CLI
    /// accepts first that is not a verb (brief-automation-13-installed-cli.md R-aut13-1).
    /// </summary>
    public const string VersionFlag = "--version";

    /// <summary>
    /// Whether <paramref name="firstArgument"/> starts a command line rather than naming a document.
    ///
    /// <para><b>The one question src/Ui asks,</b> as the first statement of the circuitRF
    /// executable's <c>Main</c>: true hands the whole argument vector to <see cref="Run"/>, false
    /// starts the GUI. It is answered by <see cref="Dispatch"/> — the switch Run dispatches on — so
    /// no list of verbs exists anywhere else, and src/Ui never names one.</para>
    ///
    /// <para><b>The WHOLE argument is compared, never its file name.</b> What a double-click delivers
    /// is a full path (Windows' <c>"%1"</c>, Linux's <c>%F</c>) or an Apple Event with no argument at
    /// all, so a document literally named <c>check</c> arrives as <c>/home/x/check</c> or
    /// <c>C:\x\check</c> and is never a verb. Case follows Run, which lower-cases the verb.</para>
    /// </summary>
    public static bool IsVerb(string firstArgument) =>
        firstArgument == VersionFlag || Dispatch(firstArgument.ToLowerInvariant()) is not null;

    /// <summary>The function each verb runs, or null for a word that is not a verb.</summary>
    private static Func<string[], int>? Dispatch(string verb) => verb switch
    {
    "sparam" => RunSparam,
    "dc"     => RunDc,
    "hb"     => RunHb,
    "env" or "envelope" => EnvelopeVerb.Run,
    "lp"  or "loadpull"         => a => RunLoadpull(a, pursuit: false),
    "lpp" or "loadpull_pursuit" or "pursuit"
                                => a => RunLoadpull(a, pursuit: true),
    "em"     => RunEm,
    // brief-sar-9: a schematic's fields analyses — an EM block's per-port fields recombined by the circuit, no EM run. It
    // owns no recombination: the run is CircuitEvaluation's, which dispatches FieldCosimulation as Simulate does.
    "fields" => CircuitRF.Cli.Fields.Run,
    // The Optimizer window's run, headless (brief-tuneopt-11). It owns no optimization: the run is
    // src/Design/Optimization's OptimizationRun, the object the panel drives. Writes nothing to the
    // design's values; --save-preset is its one opt-in write.
    "opt"    => CircuitRF.Cli.Optimize.Run,
    // Monte Carlo and yield, headless (brief-yield-5): ONE verb with nouns. It owns no statistics — the run is
    // src/Design/Statistics' StatisticalRun, the object the Yield panel drives, and it writes nothing to the
    // design's values but the two opt-in writes.
    "yield"  => CircuitRF.Cli.Yield.Run,
    // The Model Composer, headless (brief-modelcomposer-13): ONE verb with nouns. It owns no modelling — every noun calls
    // the src/Design/Modeling function the window's command calls, so both write the same bytes.
    "model"  => CircuitRF.Cli.Model.Run,
    // The whole railRF window, with no display (brief-railrf-10-cli-verb.md). It owns no analysis:
    // every number comes out of src/Design/RailRf and every pixel out of CircuitRF.Render, which is
    // what lets a BOARD be gated in CI rather than only looked at (railrf.md §5).
    "rail"   => CircuitRF.Cli.Rail.Run,
    // The Smith Chart tool's answer, with no display (brief-smith-10-cli-verb.md). It owns no
    // arithmetic and no rendering: every number is src/Design/Smith's and every pixel is
    // CircuitRF.Render's, which is what makes a matching network gateable in CI rather than only
    // draggable in a window (smith-chart.md §9, P3).
    "smith"  => CircuitRF.Cli.Smith.Run,
    // The Filter Assistant, with no display. It owns no second synthesis: the ladder is
    // FilterLadder's and the preferred values are FilterValues', the same two the window calls.
    "filter" => CircuitRF.Cli.FilterVerb.Run,
    // The S-Parameter Viewer, with no display. It owns no reading and no drawing: the file is read
    // through the TouchstoneSource the window reads it through, the plots are SParameterViewerPreset's
    // and the page is RenderDataDisplay's.
    "snp"    => CircuitRF.Cli.Snp.Run,
    "convert" => CircuitRF.Cli.LayoutConvert.Run,
    // R-aut3-13: `new` is ONE verb with a noun, not three — the surface has a standing cost, and
    // adding `new schematic` later is a noun rather than a fourth top-level verb.
    "new"    => CircuitRF.Cli.Authoring.RunNew,
    "import" => CircuitRF.Cli.Authoring.RunImport,
    // R-aut4-1/R-aut4-6: neither runs an analysis and neither writes. They are what closes a
    // headless client's loop — it writes a document, asks whether the document is sound, and asks
    // what circuitRF made of it, without paying for a run.
    "check"   => CircuitRF.Cli.Check.Run,
    // RC-3's one headless spelling, and the only one this brief adds: the safety net has to be
    // reachable from a process with no src/Ui in it, because §1.2's agent is out of process.
    // RC-5 adds `list` and `restore`, RC-7 `commit` (R-rc0-19).
    "history" => CircuitRF.Cli.History.Run,
    "explain" => CircuitRF.Cli.Explain.Run,
    // R-lvs11-1c: LVS is its own verb and is NOT a mode of `check`. `check` must stay cheap enough
    // to call after every edit and stops at elaboration; a comparison on a real board is seconds,
    // and a `check` that had become slow is a `check` people stop running. It owns no comparison —
    // every finding comes out of `LvsRun.Run`, which is what the GUI panel calls.
    "lvs"     => CircuitRF.Cli.Lvs.Run,
    // Trace Impedance Analysis: every trace on the chosen copper layers against a target Z0, as the
    // layout editor's Impedance Analysis runs it. Owns no analysis and no page — see its header.
    "impedance" => CircuitRF.Cli.Impedance.Run,
    // The Layout Review (brief-review-2): the built-in checks a person reviewing a board runs by eye,
    // beside DRC and separate from it. Owns no check — every finding comes out of LayoutReview.Run,
    // which the Review panel calls.
    "review"    => CircuitRF.Cli.Review.Run,
    // Create Schematic from Artwork, headless (brief-artsch-7). It owns no recognition — every decision is
    // ArtworkRecognition's in src/Design, the function the GUI command calls — and writes nothing unless asked.
    "recognize" or "recognise" => CircuitRF.Cli.Recognize.Run,
    // brief-em3d-24 R-em3d24-7 (owner decision D1: `solver`, one verb with nouns). The install assistant's
    // headless spelling: a build machine installs a 3D solver the way the GUI does, from the same recipe,
    // and the verb holds none of the logic — see its header.
    "solver"    => CircuitRF.Cli.Solver.Run,
    // The one output the command line did not have: a picture (brief-render-2-render-verb.md). It
    // owns no rendering — every pixel comes out of the same CircuitRF.Render the application draws
    // each frame with, which is the whole reason RND-1 put that project below the firewall.
    "render"  => CircuitRF.Cli.Render.Run,
    // The inverse of a run verb: the DataSet a run wrote, loaded back through the same two readers
    // the GUI's own source library uses (brief-automation-5-protocol-adapter.md §3's `read`).
    "read"    => CircuitRF.Cli.ReadBack.Run,
    // R-aut11-1, and by a wide margin the most valuable verb in the automation series: the
    // extraction the GUI's own Simulate performs, as a document. Without it nothing headless could
    // simulate a design anyone had actually drawn.
    "netlist" => CircuitRF.Cli.Netlist.Run,
    // R-aut11-2: one picture out of a result file, with no `.cdd` to hand-author first. It builds
    // the document `render` consumes and hands it to `render`'s own half, so there is one plotting
    // path rather than two.
    "plot"    => CircuitRF.Cli.PlotVerb.Run,
    // R-aut11-3: what is HERE. The server already knew how to read every one of these documents;
    // it simply never offered to enumerate them, so locating a workspace meant searching the
    // filesystem outside the surface entirely.
    "find"    => CircuitRF.Cli.Find.Run,
    // What a client may WRITE, before it writes it (brief-automation-6-reference-and-components.md).
    // The one verb here that takes no path at all: a catalogue is about no document, which is also
    // why it is not a mode of `explain`.
    "reference" => CircuitRF.Cli.Reference.Run,
    // The protocol adapter. A VERB, not a second executable — R-aut5-1: a second apphost means a
    // second CrfRenameApphost, a second set of literals across five packaging files, and a second
    // thing a platform script can silently omit.
    "serve"   => CircuitRF.Cli.Serve.ServeVerb.Run,
    "elab"   => RunElab,
    _        => null,
};


// Recorded rather than printed, and the exit code is left alone: an unrecognised verb prints help on
// stdout and exits 0 today, and changing either of those is a behaviour change this brief does not
// get to make (R-aut0-3).
static int UnknownVerb(string verb)
{
    JsonRun.Note(CliDiagnostics.UnknownVerb(verb));
    return PrintHelp();
}

// ── S-parameter analysis ──────────────────────────────────────────────────────

static int RunSparam(string[] args)
{
    string? input = null, output = null;
    bool    freqExplicit = false;
    double  start  = 1e9, stop = 10e9, step = 1e8;
    var     sets   = new List<(string Name, string Expr)>();

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
            case "--set" when i + 1 < args.Length:
                if (!TryTakeSet("sparam", args[++i], sets, out int setRefusal)) return setRefusal;
                break;
            default:
                // An unrecognised flag is REFUSED, not dropped. See CliDiagnostics.RunUnknownOption:
                // dropping it also fed its VALUE to the line below as the input path.
                if (args[i].StartsWith('-'))
                    return JsonRun.Fail(CliDiagnostics.RunUnknownOption("sparam", args[i]));
                input = args[i];
                break;
        }
    }

    if (input is null)
    {
        int code = JsonRun.Fail(CliDiagnostics.InputRequired("sparam", ".cnl"));
        Console.Error.WriteLine("Usage: circuitrf sparam <file.cnl|.csch> [--freq start:stop:step] [--set var=expr] [-o out.sNp]");
        return code;
    }
    JsonRun.InputPath = input;
    if (!File.Exists(input))
        return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

    try
    {
        // R-aut11-1: a `.csch` is extracted IN MEMORY, through the one extraction `circuitrf netlist`
        // writes; any other document kind is a refusal naming what the path holds.
        if (CircuitSource.ReadRunInput("sparam", input, out int kindRefusal) is not { } source)
            return kindRefusal;
        var (lib, tb) = source;
        ApplySets(tb, sets);
        string? refBase = CircuitSource.ReferenceBaseOf(input);
        var nl = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
        var shown = PrintWarnings(nl);

        // A parametric_sweep wrapping the S-parameter analysis is dispatched at the SWEEP, as `hb`, `lp`
        // and `lpp` dispatch theirs: running the inner alone silently drops the sweep axis and returns
        // one point (designer report, round 26). --freq names no directive, so it keeps the plain run.
        if (!freqExplicit &&
            SelectTop(tb, null, a => a is SParameterAnalysis, "S-parameter", "analysis <name> type=sparam ...",
                      out _) is ParametricSweepAnalysis sweep)
            return RunSparamSweep(sweep, lib, tb, nl, shown, refBase, input, output);

        // Prefer typed SParameterAnalysis from the netlist unless --freq was explicitly given.
        double[] freqs;
        var spa = tb.Analyses.OfType<SParameterAnalysis>().FirstOrDefault();
        if (spa is not null && !freqExplicit)
        {
            JsonRun.Analysis = spa.Name;
            // With the globals that carry their own unit, as Simulate expands it: without them a bound
            // written as `F1` with F1 = 2 GHz took the site unit a second time (2e18 Hz).
            freqs = spa.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
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

        // R-wsp9-3: MarginThreshold= is a property of the DIRECTIVE, and the engine reads it off
        // the settings, so it has to be mapped in here — a run with no typed analysis (--freq alone)
        // keeps the default.
        var settings = spa is null
            ? null
            : AnalysisSettings.Default.WithMarginThreshold(
                  Analysis.ParseMarginThresholdDb(spa.MarginThresholdExpr));

        // brief-wsprobe-6 R-wsp6-2: NDF= is a property of the DIRECTIVE too, and the passive
        // assembly may need a second elaboration — which is why the request carries a factory bound
        // to this run's own library and testbench rather than a netlist.
        NdfRequest? ndfReq = null;
        if (spa is not null && Analysis.ParseNdf(spa.NdfExpr))
        {
            var pv = NdfPassivation.ParseList(spa.PassiveVarsExpr);
            var pp = NdfPassivation.ParseList(spa.PassiveParamsExpr);
            ndfReq = new NdfRequest
            {
                PassiveVars    = pv,
                PassiveParams  = pp,
                PassiveNetlist = () => NdfPassivation.BuildPassiveNetlist(lib, tb, null, pv, pp),
            };
            Console.Error.WriteLine(
                "NDF: on" +
                (pv.Length > 0 ? $", PassiveVars={string.Join(",", pv)}" : "") +
                (pp.Length > 0 ? $", PassiveParams={string.Join(",", pp)}" : ""));
        }

        var ds  = SParameterEngine.Run(nl, lib, tb, baseDirectory: refBase, freqs, settings,
                                       control: RunHost.Control, ndf: ndfReq);

        // AGAIN, AFTER THE RUN — the engine reports what it finds while assembling and solving, long
        // after the pass above. Without this a real problem is written to nl.Warnings and printed by
        // nobody, which is exactly how a singular-matrix report went unseen here.
        PrintWarnings(nl, shown);

        // R-wsp1-12(a): one line per WSProbe after the S summary, and the label ↔ idx pairs in the
        // document — the idx depends on the other probes, so it is reported, never left to be guessed.
        PrintWsProbes(ds, nl, announceNoPorts: true);

        // R-wsp6-2: the whole point of the knob, on one line — and the property findings beside it,
        // because a count read off a sweep that has not reached its asymptote is not a count.
        PrintNdf(ds, nl, freqs);

        // R-wsp1-11: the TestBench's measure lines, through the one evaluator the GUI uses, so a
        // measurement of a probe output (SP1.ZG("P1")) answers the same headlessly as when opened.
        var measDs = spa is not null ? EvaluateMeasurements(tb, nl, spa.Name, ds, run: null) : null;
        var exportDs = measDs is null ? ds : MergeForExport(ds, measDs);

        JsonRun.Data = exportDs;

        // R-aut9-1. The tool schema says the extension picks the format, and for `sparam` it did
        // not: every -o wrote Touchstone whatever it was called, so `-o out.npy` produced a file
        // beginning "! NOTE:" that `read` and `render --data` then refused as not-a-.npy — an
        // accurate refusal of a file this verb had itself misnamed. The extension is honoured now,
        // through the same DataSetExporter every other run verb writes through, and an extension
        // that names no format this verb can write is a refusal that lists the ones it can.
        if (output is not null && !IsTouchstonePath(output))
        {
            if (SparamDataFormat(output) is not { } format)
                return JsonRun.Fail(CliDiagnostics.SparamUnsupportedExportFormat(
                    output, Path.GetExtension(output)));

            EnsureOutputDirectory(output);
            DataSetExporter.Export(exportDs, output, format, new ExportOptions(Format: format));
            Console.WriteLine($"Wrote {output}");
            JsonRun.AddOutput(JsonRun.KindOf(output), output);
            return 0;
        }

        // A port-less probe run has no S (R-wsp1-6); a Touchstone of it would be a lie, so the
        // refusal names the spellings that carry the wsp cubes.
        if (!ds.Contains("S"))
            return JsonRun.Fail(CliDiagnostics.SparamNoSParameters(output ?? Path.ChangeExtension(input, ".sNp")));

        if (RfCore.Data.DataSetBuilder.MissingPortNumbers(ds["S"]).Length > 0)
            return JsonRun.Fail(CliDiagnostics.SparamGappedPorts(
                output ?? Path.ChangeExtension(input, $".s{ds["S"].Axes[1].Length}p"),
                RfCore.Data.DataSetBuilder.GappedPortsRefusal(ds["S"])));

        var snp = RfCore.Data.DataSetBuilder.ToSnp(ds);

        var outPath = output ?? Path.ChangeExtension(input, $".s{snp.Ports}p");
        EnsureOutputDirectory(outPath);
        TouchstoneIO.WriteFile(snp, outPath);
        Console.WriteLine($"Wrote {outPath}");
        JsonRun.AddOutput("touchstone", outPath);
        return 0;
    }
    catch (Exception ex)
    {
        return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
    }
}

/// <summary>
/// <c>sparam</c> on a chain topped by a parametric_sweep: <see cref="ParametricSweepEngine"/>, the measurements over
/// the whole result named after the inner analysis (as the GUI names a swept result), and an export through
/// <see cref="DataSetExporter"/>. A Touchstone file has no axis for the sweep, so it is refused rather than written
/// from one point.
/// </summary>
static int RunSparamSweep(ParametricSweepAnalysis sweep, Library lib, TestBench tb, ElaboratedNetlist nl,
                          (int Notes, int Warnings) shown, string? refBase, string input, string? output)
{
    if (output is null || IsTouchstonePath(output))
        return JsonRun.Fail(CliDiagnostics.SparamSweepNeedsDataFormat(sweep.Name, output ?? Path.ChangeExtension(input, ".npy")));
    if (SparamDataFormat(output) is not { } format)
        return JsonRun.Fail(CliDiagnostics.SparamUnsupportedExportFormat(output, Path.GetExtension(output)));

    Console.Error.WriteLine(
        $"S-parameter sweep '{sweep.Name}': {sweep.SweepValues.Length} point(s) over {sweep.SweepVarName}");
    var ds = ParametricSweepEngine.Run(sweep, lib, tb, settings: null, baseDirectory: refBase,
                                       control: RunHost.Control, diagnosticsInto: nl);
    PrintWarnings(nl, shown);
    JsonRun.Analysis = sweep.Name;

    var inner = BaseOfChain(sweep, tb);
    var measDs = inner is not null ? EvaluateMeasurements(tb, nl, inner.Name, ds, run: null) : null;
    var exportDs = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
    JsonRun.Data = exportDs;

    EnsureOutputDirectory(output);
    DataSetExporter.Export(exportDs, output, format, new ExportOptions(Format: format));
    Console.WriteLine($"Wrote {output}");
    JsonRun.AddOutput(JsonRun.KindOf(output), output);
    return 0;
}

// ── DC operating point ────────────────────────────────────────────────────────

static int RunDc(string[] args)
{
    if (args.Length == 0 || args[0].StartsWith('-'))
        return JsonRun.Fail(CliDiagnostics.InputRequired("dc", ".cnl"));

    var input = args[0];

    // dc reads its path positionally and hands the REST to DcSettingsFrom, which looks only for the
    // three flags it knows. So an unrecognised flag was neither read nor reported — `dc x.cnl --set
    // Vg=1` ran without the override and said nothing. Scanned here, where the whole argument list
    // is still in one piece.
    var sets = new List<(string Name, string Expr)>();
    string? output = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith('-')) return JsonRun.Fail(CliDiagnostics.RunMultipleInputs("dc", args[i]));
        if (args[i] == "--set" && i + 1 < args.Length)
        {
            if (!TryTakeSet("dc", args[++i], sets, out int setRefusal)) return setRefusal;
            continue;
        }
        if (args[i] is "-o" or "--output" && i + 1 < args.Length)
        {
            output = args[++i];
            if (SparamDataFormat(output) is null)
                return JsonRun.Fail(CliDiagnostics.DcUnsupportedExportFormat(output, Path.GetExtension(output)));
            continue;
        }
        if (args[i] is not ("--max-iter" or "--maxiter" or "--dc-steps" or "--gmin"))
            return JsonRun.Fail(CliDiagnostics.RunUnknownOption("dc", args[i]));
        i++;   // its value
    }

    JsonRun.InputPath = input;
    if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

    var settings = DcSettingsFrom(args);

    try
    {
        if (CircuitSource.ReadRunInput("dc", input, out int kindRefusal) is not { } source)
            return kindRefusal;
        var (lib, tb) = source;
        ApplySets(tb, sets);
        string? refBase = CircuitSource.ReferenceBaseOf(input);
        var nl = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
        var shown = PrintWarnings(nl);

        // A parametric_sweep wrapping the DC analysis is dispatched at the SWEEP, as `sparam`, `hb`,
        // `lp` and `lpp` dispatch theirs (brief MC-30 R-mc30-1): an I–V family is exactly this shape,
        // and running the inner alone returned one operating point with the sweep axis gone. Chain
        // selection is consulted only when some sweep bottoms out in a DC, so an unswept bench —
        // even one declaring two DC directives — prints exactly what it always printed.
        if (tb.Analyses.OfType<ParametricSweepAnalysis>().Any(ps => BaseOfChain(ps, tb) is DcAnalysis) &&
            SelectTop(tb, null, a => a is DcAnalysis, "DC", "analysis <name> type=dc ...",
                      out _) is ParametricSweepAnalysis sweep)
            return RunDcSweep(sweep, lib, tb, nl, shown, refBase, settings, output);

        var result = NonlinearDcEngine.Run(nl, settings);

        // The document's `result` is DcResultPacker's DataSet of THIS DcResult — the same packer the
        // GUI and the sweep engine use, so a `dc` cube read headlessly is the cube read anywhere
        // else. Both it and the table below read the one DcResult, so they cannot disagree about a
        // number; what they cannot share is a selection step, because the table has none (it prints
        // every node and every probe).
        var dcDs = DcResultPacker.Pack(result, nl);

        // The bench's `measure` lines, through the evaluator every other run verb and the GUI use. A
        // measurement names its DC result by the analysis that declares it (DC1.V("mid")), as it does
        // in the window; a netlist with no DC directive has no such name, so "DC" stands in.
        string dcName = tb.Analyses.OfType<DcAnalysis>().FirstOrDefault()?.Name ?? "DC";
        var dcMeas = EvaluateMeasurements(tb, nl, dcName, dcDs, run: null);
        JsonRun.Data = dcMeas is { Cubes.Count: > 0 } ? MergeForExport(dcDs, dcMeas) : dcDs;

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
        // Through ResultRows, so a VProbe's own name is listed here exactly as it is in the
        // exported cube — a probe that reports in the GUI and not headlessly is worse than none.
        var (dcRows, dcNames) = nl.Nodes.ResultRows(excludeInternal: false);
        for (int i = 0; i < dcRows.Length; i++)
            Console.WriteLine($"  {dcNames[i],-28} {result.NodeVoltages[dcRows[i] - 1],14:G6}");

        PrintWorkerOutput();

        if (result.ProbeCurrents.Count > 0)
        {
            Console.WriteLine("Probe currents (A):");
            foreach (var (name, current) in result.ProbeCurrents.OrderBy(p => p.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {name,-28} {current,14:G6}");
        }

        if (dcMeas is { Cubes.Count: > 0 })
        {
            Console.WriteLine("Measurements:");
            foreach (var (name, cube) in dcMeas.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                PrintCube($"  {name}", cube, maxRows: 24, allPoints: false);
        }

        if (output is not null)
        {
            var format = SparamDataFormat(output)!.Value;
            EnsureOutputDirectory(output);
            DataSetExporter.Export(JsonRun.Data, output, format, new ExportOptions(Format: format));
            Console.WriteLine($"Wrote {output}");
            JsonRun.AddOutput(JsonRun.KindOf(output), output);
        }

        return result.Converged ? 0 : 2;
    }
    catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message)); }
}

/// <summary>
/// <c>dc</c> on a chain topped by a parametric_sweep: <see cref="ParametricSweepEngine"/> with the verb's own solver
/// settings, the measurements over the whole result named after the inner analysis, a per-point convergence count on
/// stderr, and an export through <see cref="DataSetExporter"/> when <c>-o</c> is given. A family has no one table of
/// node voltages to print, so stdout carries the measurements and the file carries the cubes.
/// </summary>
static int RunDcSweep(ParametricSweepAnalysis sweep, Library lib, TestBench tb, ElaboratedNetlist nl,
                      (int Notes, int Warnings) shown, string? refBase, AnalysisSettings settings, string? output)
{
    Console.Error.WriteLine(
        $"DC sweep '{sweep.Name}': {sweep.SweepValues.Length} point(s) over {sweep.SweepVarName}");
    var ds = ParametricSweepEngine.Run(sweep, lib, tb, settings, baseDirectory: refBase,
                                       control: RunHost.Control, diagnosticsInto: nl);
    PrintWarnings(nl, shown);
    JsonRun.Analysis = sweep.Name;

    var inner = BaseOfChain(sweep, tb);
    var measDs = inner is not null ? EvaluateMeasurements(tb, nl, inner.Name, ds, run: null) : null;
    var exportDs = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
    JsonRun.Data = exportDs;

    int total = 0, failed = 0;
    if (ds.Contains("Converged"))
        foreach (double v in ds["Converged"].RealValues) { total++; if (v == 0.0) failed++; }
    Console.Error.WriteLine(failed == 0
        ? $"DC: converged at all {total} point(s)"
        : $"DC: DID NOT CONVERGE at {failed} of {total} point(s)");

    if (measDs is { Cubes.Count: > 0 })
    {
        Console.WriteLine("Measurements:");
        foreach (var (name, cube) in measDs.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            PrintCube($"  {name}", cube, maxRows: 24, allPoints: false);
    }

    if (output is not null)
    {
        var format = SparamDataFormat(output)!.Value;
        EnsureOutputDirectory(output);
        DataSetExporter.Export(exportDs, output, format, new ExportOptions(Format: format));
        Console.WriteLine($"Wrote {output}");
        JsonRun.AddOutput(JsonRun.KindOf(output), output);
    }
    else
        Console.Error.WriteLine("[circuitRF] the swept cubes are written only by -o out.{npy,mat,txt}.");

    return failed == 0 ? 0 : 2;
}

// ── --set, for the verbs that read it in their own loops ─────────────────────

/// <summary>One <c>--set name=expr</c>, split and recorded, or the verb's malformed-set refusal.
/// The spelling and the refusal are <c>hb</c>'s, so every run verb takes the same thing.</summary>
static bool TryTakeSet(string verb, string kvText, List<(string Name, string Expr)> sets, out int refusal)
{
    refusal = 0;
    int eq = kvText.IndexOf('=');
    if (eq <= 0) { refusal = JsonRun.Fail(CliDiagnostics.SetMalformed(verb, kvText)); return false; }
    sets.Add((kvText[..eq].Trim(), kvText[(eq + 1)..].Trim()));
    return true;
}

/// <summary>Applies each override to the test bench's own variable scope BEFORE elaboration, through
/// the function <c>hb</c> uses — so anything derived from the variable re-derives, including a sweep
/// whose bounds are written in terms of it.</summary>
static void ApplySets(TestBench tb, List<(string Name, string Expr)> sets)
{
    foreach (var (name, expr) in sets)
    {
        HbCircuitRun.ApplySet(tb, name, expr);
        Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
    }
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
                    return JsonRun.Fail(CliDiagnostics.SetMalformed("hb", kvText));
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
                if (args[i].StartsWith('-'))
                    return JsonRun.Fail(CliDiagnostics.RunUnknownOption("hb", args[i]));
                input = args[i];
                break;
        }
    }

    if (input is null)
    {
        int code = JsonRun.Fail(CliDiagnostics.InputRequired("hb", ".cnl"));
        Console.Error.WriteLine(
            "Usage: circuitrf hb <file.cnl|.csch> [-a name] [--set var=expr] [--maxharm K] [--maxmix M]");
        Console.Error.WriteLine(
            "                    [--tol t] [--max-iter N] [--rows N] [--all] [--diag] [-o out.{mat,npy,txt}]");
        return code;
    }
    JsonRun.InputPath = input;
    if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

    try
    {
        if (CircuitSource.ReadRunInput("hb", input, out int kindRefusal) is not { } source)
            return kindRefusal;
        var (lib, tb) = source;

        // --set lands in the netlist's own variable scope rather than being pushed at the engine, so
        // an override participates in expression evaluation like any other global: everything derived
        // from it re-derives. Overriding Pavl_dbm has to move the source amplitude computed from it.
        foreach (var (name, expr) in sets)
        {
            HbCircuitRun.ApplySet(tb, name, expr);
            Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
        }

        string? refBase = CircuitSource.ReferenceBaseOf(input);
        var nl    = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
        var shown = PrintWarnings(nl);

        // Pick what to run: the named analysis, else the outermost runnable chain that bottoms out
        // in an HB. A parametric_sweep wrapping the HB must be dispatched at the SWEEP, not the HB —
        // running the inner directly silently drops the sweep axis and returns one point.
        var top = SelectTop(tb, analysisName, a => a is HarmonicBalanceAnalysis,
                            "HB", "analysis <name> type=hb ...", out string? why);
        if (top is null)
            return JsonRun.Fail(CliDiagnostics.NoAnalysis("hb", why!));

        var settings = SolverSettingsFrom(maxIter, diag);
        top = ApplyHbOverrides(tb, top, maxHarm, maxMixOrder, tol, maxIter);

        // brief-em3d-79: the dispatch is HbCircuitRun.Solve — the function a thermal setup driven from this circuit
        // calls too, so the two cannot run the chain differently. The announcement is the line this verb always printed.
        var solved = HbCircuitRun.Solve(lib, tb, nl, top, settings, refBase,
                                        RunHost.Control, Console.Error.WriteLine);
        DataSet      ds  = solved.Data;
        HbRunResult? run = solved.Run;

        // AGAIN, AFTER THE RUN — same reason as `dc`: the engine adds warnings while assembling and
        // solving, long after elaboration finished.
        PrintWarnings(nl, shown);
        PrintWorkerOutput();

        // R-wsp1-12(a) and brief-wsprobe-5's run summary: the same probe line the S-parameter verb
        // prints, over `ssfreq` instead of `freq`. Without it a headless HB run reported every wsp
        // cube and no label ↔ idx map to read them by, and the margin minimum the threshold note is
        // measured against was nowhere on stdout or in --json.
        PrintWsProbes(ds, nl, announceNoPorts: false);

        // Measurements are the point of an HB run on anything real (conversion loss, gain, IMn), and
        // they are evaluated over the whole result including the sweep axis — so run them here, the
        // same way the GUI does, rather than leaving the caller to redo the algebra on the export.
        // A measurement qualifies its cube accessor with the analysis name — and the GUI names a
        // swept result after the INNER analysis, not the sweep. Match that exactly, so the same
        // `measure` line works in both places rather than only in whichever one it was written for.
        var measDs     = EvaluateMeasurements(tb, nl, solved.ResultName, ds, run);

        // The chain that ACTUALLY ran, after SelectTop's promotion — a caller that asked for an
        // inner analysis and got its wrapper can see that from the document alone, which matters
        // because the difference between the two is a whole sweep axis (cli.md §4).
        JsonRun.Analysis = top.Name;

        // ONE merged DataSet, used by both the export and the document, so a `.npy` written here and
        // the JSON printed beside it can never disagree about which cubes the run produced.
        var fullDs = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
        JsonRun.Data = fullDs;

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
            var format = FormatFromExtension(exportPath);
            EnsureOutputDirectory(exportPath);
            DataSetExporter.Export(fullDs, exportPath, format,
                new ExportOptions(Format: format), run?.LinearPayload);
            Console.WriteLine($"Wrote {exportPath}");
            JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
        }

        return Converged(ds) ? 0 : 2;
    }
    catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message)); }
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
                    return JsonRun.Fail(CliDiagnostics.SetMalformed(verb, kvText));
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
                    return JsonRun.Fail(CliDiagnostics.PinMalformed(verb, args[i]));
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
                if (args[i].StartsWith('-'))
                    return JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i]));
                input = args[i];
                break;
        }
    }

    // Refused rather than ignored: a Γ grid silently not applied is a run that answers a different
    // question and reports nothing about it.
    if (pursuit && gridPath is not null)
        return JsonRun.Fail(CliDiagnostics.GridNotForPursuit());
    if (!pursuit && outGridPath is not null)
        return JsonRun.Fail(CliDiagnostics.OutGridNotForLoadpull());

    if (input is null)
    {
        int code = JsonRun.Fail(CliDiagnostics.InputRequired(verb, ".cnl"));
        Console.Error.WriteLine(
            $"Usage: circuitrf {verb} <file.cnl|.csch> [-a name] [--set var=expr] " +
            (pursuit ? "[--out-grid out.gam] " : "[--grid grid.gam] ") +
            "[--pin start:step:max]");
        Console.Error.WriteLine(
            "                     [--compression dB] [--maxharm K] [--tol t] [--max-iter N] " +
            "[--rows N] [--all] [--diag]");
        Console.Error.WriteLine(
            "                     [-o out.{mat,npy,txt" + (pursuit ? "" : ",spl,lpcwave") + "}]");
        return code;
    }
    JsonRun.InputPath = input;
    if (!File.Exists(input)) return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

    try
    {
        if (CircuitSource.ReadRunInput(verb, input, out int kindRefusal) is not { } source)
            return kindRefusal;
        var (lib, tb) = source;

        // Same rule as `hb`: an override joins the netlist's own variable scope so everything derived
        // from it re-derives, rather than being pushed at one engine that reads it once.
        foreach (var (name, expr) in sets)
        {
            tb.GlobalVariables.RemoveAll(v => v.Name == name);
            tb.GlobalVariables.Add(new Variable(name, expr));
            Console.Error.WriteLine($"[circuitRF] set {name} = {expr}");
        }

        string? refBase = CircuitSource.ReferenceBaseOf(input);
        var nl    = new Elaborator(lib) { BaseDirectory = refBase }.Elaborate(tb);
        var shown = PrintWarnings(nl);

        var top = SelectTop(
            tb, analysisName,
            pursuit ? a => a is LoadpullPursuitAnalysis : a => a is LoadpullAnalysis,
            kind,
            pursuit ? "analysis <name> type=loadpull_pursuit ..." : "analysis <name> type=loadpull ...",
            out string? why);
        if (top is null)
            return JsonRun.Fail(CliDiagnostics.NoAnalysis(verb, why!));

        top = ApplyLoadpullOverrides(tb, top, gridPath, outGridPath, maxHarm, tol, maxIter,
                                     compression, pinStart, pinStep, pinMax);

        var     settings = SolverSettingsFrom(maxIter, diag);
        DataSet ds;

        if (top is ParametricSweepAnalysis psa)
        {
            Console.Error.WriteLine(
                $"{kind} sweep '{psa.Name}': {psa.SweepValues.Length} point(s) over {psa.SweepVarName}");
            ds = ParametricSweepEngine.Run(psa, lib, tb, settings,
                                           baseDirectory: refBase,
                                           control: RunHost.Control,
                                           // Every point elaborates a netlist of its own and throws it
                                           // away; without this the warnings printed below are those of
                                           // a netlist nothing ever stamped.
                                           diagnosticsInto: nl);
        }
        else if (pursuit)
        {
            var lppa = (LoadpullPursuitAnalysis)top;
            var pp   = LoadpullPursuitEngine.Resolve(lppa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
            Console.Error.WriteLine(
                $"Loadpull-pursuit '{lppa.Name}': f0={pp.LpParams.ToneHz / 1e9:G6} GHz, " +
                $"{pp.SearchMethod}, {(pp.UsePae ? "PAE" : "DE")} at {pp.LpParams.Compression:G3} dB " +
                $"compression, Zsource OBO {pp.ZsourceOBoDB:G3} dB");
            ds = new LoadpullPursuitEngine(new LoadpullEngine(nl, tb, settings)).Run(pp, control: RunHost.Control);
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
                     new LoadpullEngine(nl, tb, settings).Run(p, RunHost.Control));
        }

        PrintWarnings(nl, shown);
        PrintWorkerOutput();

        var resultName = BaseOfChain(top, tb)?.Name ?? top.Name;
        var measDs     = EvaluateMeasurements(tb, nl, resultName, ds, null);

        JsonRun.Analysis = top.Name;

        // R-aut1-5 — under --json a loadpull's DEFAULT document is the same one-row-per-grid-point
        // summary the terminal prints, and --all still means every cube. The cubes are
        // [gridPoint x pinStep] and there are eight of them; the summary is the useful projection,
        // not a terminal compromise.
        JsonRun.SummaryIsTheDefault = true;
        JsonRun.AllCubes            = allPoints;

        var fullDs   = measDs is { Cubes.Count: > 0 } ? MergeForExport(ds, measDs) : ds;
        JsonRun.Data = fullDs;

        // AUT-9 R-aut9-4/5/6 — what the shape of the result says about the run, said out loud. A
        // human reads all three off the log; a client reading `status: ok` and a document does not,
        // and one of them wrote up a working component as defective on that evidence.
        ReportLoadpullFindings(ds);

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
            if (!ExportLoadpull(fullDs, exportPath)) return 1;
            Console.WriteLine($"Wrote {exportPath}");
            JsonRun.AddOutput(JsonRun.KindOf(exportPath), exportPath);
        }

        // The pursuit's own follow-on grid, when it wrote one. It is a file the run produced, so it
        // belongs in `outputs` beside the export rather than being findable only from the directive.
        if (outGridPath is not null && File.Exists(outGridPath))
            JsonRun.AddOutput("gamma-grid", outGridPath);

        return LoadpullExitCode(ds);
    }
    catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message)); }
}

/// <summary>
/// Reports AUT-9's three loadpull findings, on stderr and in the document alike.
///
/// <para><b>The decisions are all in <see cref="RfCore.Loadpull.LoadpullRunFindings"/></b>, which
/// reads the cubes the run published and returns values — this only spells them. A rule that lived
/// here would be one the GUI and the protocol adapter could not reach (R-aut1-9).</para>
/// </summary>
static void ReportLoadpullFindings(DataSet ds)
{
    foreach (var f in RfCore.Loadpull.LoadpullRunFindings.For(ds))
    {
        var d = f.Kind switch
        {
            RfCore.Loadpull.LoadpullFindingKind.DeviceInert =>
                CliDiagnostics.LoadpullDeviceInert(f.DriveSteps),
            RfCore.Loadpull.LoadpullFindingKind.NothingConverged =>
                CliDiagnostics.LoadpullNothingConverged(
                    f.Attempted, f.DriveSteps,
                    string.Join(", ", f.StopCodes.OrderBy(k => k.Key, StringComparer.Ordinal)
                                                 .Select(k => $"{k.Key} {k.Value}"))),
            _ => CliDiagnostics.LoadpullTickleGap(f.TickleDbm, f.FirstDriveDbm),
        };
        Console.Error.WriteLine($"warning: {d.Render()}");
        JsonRun.Note(d);
    }
}

/// <summary>
/// Makes sure a <c>-o</c> path's folder exists before anything is written to it.
///
/// <para><b>Because the GUI's own writer does.</b> <c>ResultsWriter.WriteRun</c> creates
/// <c>&lt;workspace&gt;/results</c> on the way past, which is why Simulate works on a workspace that
/// has never been run; a headless <c>-o results/Cell.npy</c> on the same workspace failed with
/// <i>"could not find a part of the path"</i>, so the documented command for reproducing a run was
/// the one command that needed the folder to already be there. `netlist`, `render` and `plot` each
/// do this already — the run verbs are the ones that did not.</para>
///
/// <para>Failure is deliberately not reported here: the write that follows is inside a try/catch
/// that names the file, and a folder that cannot be created is the same problem reported once
/// rather than twice.</para>
/// </summary>
internal static void EnsureOutputDirectory(string path)
{
    try
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
    }
    catch { /* the write below reports it, by name */ }
}

/// <summary>
/// Writes a loadpull result. <c>.spl</c> and <c>.lpcwave</c> go through the loadpull writers rather
/// than <see cref="DataSetExporter"/> — those two formats are the loadpull interchange the Data
/// Display itself reads back, so a headless run can produce a file the GUI opens as a measured
/// surface. Everything else is the ordinary <c>.mat</c>/<c>.npy</c>/<c>.txt</c> path.
/// </summary>
static bool ExportLoadpull(DataSet ds, string path)
{
    EnsureOutputDirectory(path);
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
        JsonRun.Report(CliDiagnostics.NoLoadpullSurface(ext));
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

        // R-aut1-1 — BOTH of these read through LoadpullResultSummary, which is also what the
        // --json document reads. The selections below (which drive step, which cube spelling, what
        // scale) are load-bearing and used to live only in these printers; sharing them is what
        // stops the table and the document from being able to disagree.
        if (LoadpullResultSummary.SummarizePursuit(cubes) is { } optima)
            PrintPursuitOptima(optima);

        if (LoadpullResultSummary.SummarizeGrid(cubes) is { } grid)
            PrintLoadpullGrid(grid, maxRows, allPoints);

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
static void PrintPursuitOptima(LoadpullPursuitSummary s)
{
    // MXE's criterion is published as a FRACTION (the engine's internal DE/PAE unit) while the
    // follow-on grid's own Efficiency/PAE columns are percent, so the summary carries the ×100 as
    // ValueScale and it is applied HERE, on the way to the screen. A document emits the raw number
    // and the scale beside it (R-aut1-2).
    // The Zload of a NON-converged optimum is still the last termination the search looked at, and
    // worth printing. Its criterion is not: the engine publishes 0 there, and "Pout=0 dBm" next to
    // "DID NOT converge" reads as a measured zero rather than as an absent number.
    void One(PursuitOptimum o, string title, string valueLabel)
    {
        // ConsoleUnit, not ValueUnit — the terminal prints the SCALED number, so it must print the
        // scaled number's unit (AUT-9 R-aut9-3 split the two apart; before that these were one
        // field and the printed line was the one that happened to be right).
        string shown = o.Converged ? $"{o.Value * o.ValueScale:G5} {o.ConsoleUnit}" : "—";
        Console.WriteLine(
            $"  {title,-26} {(o.Converged ? "converged" : "DID NOT converge")}   " +
            $"{valueLabel}={shown}   " +
            $"Zload={FormatOhms(o.ZRe, o.ZIm)}" +
            (o.HasZsource ? $"   Zsource={FormatOhms(o.ZsourceRe, o.ZsourceIm)}" : ""));
    }

    Console.WriteLine("  Pursuit optima:");
    One(s.Mxp, "MXP (max power)",      "Pout");
    One(s.Mxe, "MXE (max efficiency)", "Eff");

    if (!double.IsNaN(s.Queried))
        Console.WriteLine(
            $"  {(int)s.Queried} termination(s) queried" +
            (s.Unscorable  > 0 ? $", {(int)s.Unscorable} could not be scored (never reached compression)" : "") +
            (s.Recommended > 0 ? $", {(int)s.Recommended} recommended termination(s)" : ""));
    Console.WriteLine();
}

/// <summary>
/// Formats the grid summary: one row per Γ grid point, showing where it was, how it stopped, and its
/// FOMs at the drive step the summary chose.
///
/// <para><b>The choosing is no longer done here.</b> Which drive step each point is read at, which
/// of two cube spellings a column comes from and whether efficiency arrives as a fraction or a
/// percent are all <see cref="LoadpullResultSummary.SummarizeGrid"/>'s answers now, because
/// <c>--json</c> has to give the same ones (R-aut1-1). What is left in this function is the part
/// that is genuinely about a terminal: column widths, the em dash, and how many rows fit.</para>
/// </summary>
static void PrintLoadpullGrid(LoadpullGridSummary s, int maxRows, bool allPoints)
{
    Console.WriteLine(
        $"  Grid: {s.GridPoints} point(s)" + (s.OuterPoints > 1 ? $" x {s.OuterPoints} sweep point(s)" : "") +
        $" — {s.Compressed} reached compression" +
        (s.MaxDrive     > 0 ? $", {s.MaxDrive} stopped at max drive" : "") +
        (s.NotConverged > 0 ? $", {s.NotConverged} did not converge" : ""));
    if (s.Compressed == 0)
        Console.WriteLine("  Nothing reached compression — raise --pin's max (or the directive's PinMax).");

    for (long o = 0; o < s.OuterPoints; o++)
    {
        Console.WriteLine();
        if (s.OuterAxes.Count > 0)
            Console.WriteLine($"  [{RowLabel(OuterAxesPlusOne(s.OuterAxes), o)}]");

        Console.WriteLine($"    {"#",3}  {"GammaLoad",-20}{"ZLoad (ohm)",-22}{"stop",-13}" +
                          $"{"Pavl",9}{"Pout",9}{"Gt",8}{"DE%",8}{"PAE%",8}");

        int shown = allPoints ? s.GridPoints : Math.Min(s.GridPoints, maxRows);
        for (int g = 0; g < shown; g++)
        {
            var row = s.Rows[(int)(o * s.GridPoints + g)];

            string gam = row.GammaLoad is { } gl ? FormatGamma(gl) : "";
            string z   = row.ZLoad     is { } zl ? FormatOhms(zl.Real, zl.Imaginary) : "";

            Console.WriteLine(
                $"    {g,3}  {gam,-20}{z,-22}{LoadpullResultSummary.StopName(row.StopCode),-13}" +
                $"{Cell(row, s, "Pavl"),9}{Cell(row, s, "Pout"),9}{Cell(row, s, "Gt"),8}" +
                $"{Cell(row, s, "Efficiency"),8}{Cell(row, s, "PAE"),8}");
        }
        if (shown < s.GridPoints)
            Console.WriteLine($"    … {s.GridPoints - shown} more point(s) — use --all or --rows N");
    }
    Console.WriteLine();

    // RowLabel labels every axis BUT the last, so the outer axes are handed to it with one throwaway
    // axis appended rather than being re-implemented here.
    static IReadOnlyList<Axis> OuterAxesPlusOne(IReadOnlyList<Axis> outerAxes)
        => [.. outerAxes, new Axis("_", [0.0])];

    // The scale is the SUMMARY's, applied here on the way to a column headed "DE%" — the value in
    // the row is the engine's own. An absent cube, an absent drive step and a NaN measurement are
    // all one em dash, which is what they all are: not a number.
    static string Cell(LoadpullGridRow row, LoadpullGridSummary s, string column)
    {
        for (int i = 0; i < s.Columns.Count; i++)
        {
            if (s.Columns[i].Column != column) continue;
            double v = row.Fom[i] * s.Columns[i].Scale;
            return double.IsNaN(v) ? "—" : v.ToString("F2");
        }
        return "—";
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
    string? input = null, output = null, workspace = null, solverName = null, setupName = null;
    // brief-sar-12 R-sar12-3 — `em x.c3d --bands <sweep>`: every band's setup, made from the sweep's template, run in turn.
    string? bandsName = null;
    Em3dSolverChoice? solver = null;
    string? referenceName = null;
    bool force = false;
    // brief-em3d-136 R-em3d136-4c — where Palace solves. Headless there is no preference to read, so a build machine
    // says what it means; the default is the CPU.
    var device = CircuitRF.Design.Em3d.PalaceDevice.Cpu;
    // brief-emi-9 R-emi9-4 — and where EMerge factorises: the same flag, whose `auto` (the default) is the GUI's Automatic.
    var emergeDevice = CircuitRF.Design.Em3d.EmergeDevice.Auto;
    // brief-emi-10 R-emi10-4 — a lower memory ceiling for this run's solver processes than this machine's memory; no flag is
    // the machine's.
    long? memoryLimit = null;
    var emSets = new List<(string Name, string Expr)>();
    // AA-1 — `em --component "SPIRAL N=3 W=10 um"`: one built-in component's drawn part, extracted.
    string? component = null, componentFreq = null;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--component" when i + 1 < args.Length:
                component = args[++i];
                break;
            case "--freq" when i + 1 < args.Length:
                componentFreq = args[++i];
                break;
            case "-o" or "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            // brief-em3d-7 R-em3d7-1b (owner decision D1) — the 3D solver for THIS run, overriding the
            // setup's Solver3D in memory only. Nothing is written back: the .cem is not saved here.
            // brief-em3d-10 — `both`, on one generated problem, and the comparison. brief-emi-1 — the words are the registry's.
            // brief-emi-6 R-emi6-5 — or a comma list of them, or `compare` (the setup's own set); read once the setup is,
            // since `compare` means the setup's.
            case "--solver" when i + 1 < args.Length:
                solverName = args[++i];
                break;
            case "--reference" when i + 1 < args.Length:
                referenceName = args[++i];
                break;
            case "--workspace" when i + 1 < args.Length:
                workspace = args[++i];
                break;
            case "--device" when i + 1 < args.Length:
            {
                string deviceName = args[++i];
                if (CircuitRF.Design.Em3d.EmergeDevices.Parse(deviceName) is not { } parsed)
                    return JsonRun.Fail(CliDiagnostics.EmUnknownDevice(deviceName));
                // `auto` is the CPU for Palace, whose own default it is; EMerge's Automatic chooses by the problem.
                device = parsed == CircuitRF.Design.Em3d.EmergeDevice.Gpu ? CircuitRF.Design.Em3d.PalaceDevice.Gpu : CircuitRF.Design.Em3d.PalaceDevice.Cpu;
                emergeDevice = parsed;
                break;
            }
            case "--memory-limit" when i + 1 < args.Length:
            {
                string text = args[++i];
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gb))
                    return JsonRun.Fail(CliDiagnostics.EmMemoryLimit($"--memory-limit takes a number of GB, not '{text}'."));
                if (CircuitRF.Design.Em3d.Em3dMemoryLimit.Refusal(gb, CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes) is { } badLimit)
                    return JsonRun.Fail(CliDiagnostics.EmMemoryLimit(badLimit));
                memoryLimit = CircuitRF.Design.Em3d.Em3dMemoryLimit.Bytes(gb);
                break;
            }
            // brief-em3d-21 R-em3d21-2b — start a Palace run whose memory estimate is past 150 % of this
            // machine's. The panel asks instead; headless there is nobody to ask.
            // brief-emi-10 — and a run started this way keeps no memory ceiling; the disk floor still holds.
            case "--force":
                force = true;
                break;
            // brief-em3d-42 R-em3d42-6 — which of a .c3d's embedded setups to run (render --view's pattern).
            case "--setup" when i + 1 < args.Length:
                setupName = args[++i];
                break;
            case "--bands" when i + 1 < args.Length:
                bandsName = args[++i];
                break;
            // brief-em3d-79 R-em3d79-4 — a thermal setup driven from a circuit: a global of the CIRCUIT, set before it is
            // elaborated, as `hb` sets it.
            case "--set" when i + 1 < args.Length:
            {
                var kvText = args[++i];
                int eq = kvText.IndexOf('=');
                if (eq <= 0)
                    return JsonRun.Fail(CliDiagnostics.SetMalformed("em", kvText));
                emSets.Add((kvText[..eq].Trim(), kvText[(eq + 1)..].Trim()));
                break;
            }
            default:
                if (args[i].StartsWith('-'))
                    return JsonRun.Fail(CliDiagnostics.RunUnknownOption("em", args[i]));
                input = args[i];
                break;
        }
    }

    // The scratch folder Settings ▸ Solvers names, read now so a change there reaches the next run (an MCP server runs many
    // in one process), and said: on stdout below and in --json's scratch block, refused run or not, so a caller can tell
    // which disk a run filled.
    {
        string? scratchChosen = CircuitRF.Design.ScratchFolder.FromPreferences();
        CircuitRF.Design.ScratchFolder.Chosen = scratchChosen;
        string scratchRoot = CircuitRF.Design.ScratchFolder.Root;
        JsonRun.Scratch = new ScratchJson(scratchRoot, scratchChosen is null ? "system" : "preference",
                                          Directory.Exists(scratchRoot), Path.GetTempPath());
    }

    // A GPU asked of a run that starts no Palace is refused rather than ignored: the flag would otherwise say a run
    // used a GPU that nothing was ever going to use.
    if (component is not null && device == CircuitRF.Design.Em3d.PalaceDevice.Gpu)
        return JsonRun.Fail(CliDiagnostics.EmDeviceWithoutPalace("a component's drawn part, extracted by circuitRF's own planar solver"));
    if (component is not null)
        return EmComponent.Run(component, input, output, componentFreq,
                               (result, setup, path) => ReportEmRun(result, setup, path), EmProgressToStderr());
    if (componentFreq is not null)
        return JsonRun.Fail(CliDiagnostics.EmFreqWithoutComponent());

    if (input is null)
    {
        int code = JsonRun.Fail(CliDiagnostics.InputRequired("em", ".cem"));
        Console.Error.WriteLine("Usage: circuitrf em <setup.cem | view.c3d> [--setup <name> | --bands <sweep>] [-o out.sNp] [--workspace <file.cws>] [--solver " + string.Join("|", CircuitRF.Design.Em3d.Em3dSolvers.CliWords) + "|<solver,solver,...>] [--reference <solver>] [--device auto|cpu|gpu] [--memory-limit <GB>] [--force] [--set var=expr]");
        Console.Error.WriteLine("       circuitrf em [<workspace dir | .cws | .ctech>] --component \"SPIRAL N=3 W=10 um ...\" -o out.sNp [--freq start:stop:step]");
        return code;
    }
    JsonRun.InputPath = input;
    if (!File.Exists(input))
        return JsonRun.Fail(CliDiagnostics.FileNotFound(input));

    string cemPath = Path.GetFullPath(input);

    // brief-em3d-42 R-em3d42-6 — a .c3d runs one of its own embedded setups: the one there is, or --setup's.
    CircuitRF.Design.ThreeD.C3dDocument? threeD = null;
    string? threeDPath = null;
    bool isC3d = DocumentKinds.Classify(cemPath) == DocumentKind.ThreeD;
    string? embeddedName = null;      // brief-em3d-98 — the .c3d's own name for the setup run
    EmSetup setup;
    CircuitRF.Design.ThreeD.C3dBandSweep? bandSweep = null;
    var bandSetups = new List<EmSetup>();
    if (bandsName is not null && (!isC3d || setupName is not null || output is not null))
        return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(cemPath, !isC3d
            ? "--bands names a band sweep of a .c3d; a .cem is one setup."
            : "--bands runs every band's own setup, each writing where its Simulate writes; it takes neither --setup nor -o."));
    if (isC3d)
    {
        try { threeD = CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(cemPath); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.SetupUnreadable(cemPath, ex.Message)); }
        if (bandsName is not null)
        {
            bandSweep = threeD.BandSweeps?.FirstOrDefault(b => b.Name == bandsName);
            if (bandSweep is null)
                return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(cemPath, $"This 3D view has no band sweep named '{bandsName}'" +
                    (threeD.BandSweeps is { Count: > 0 } all ? $"; it has {string.Join(", ", all.Select(b => $"'{b.Name}'"))}." : ".")));
            var (made, bandWhy) = CircuitRF.Design.ThreeD.C3dBandSweeps.Make(threeD, bandSweep);
            if (bandWhy is not null) return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(cemPath, bandWhy));
            bandSetups.AddRange(made);
            setup = CircuitRF.Design.ThreeD.C3dSetups.ForRun(made[0], cemPath);
            embeddedName = null;
        }
        else
        {
            var (embedded, why) = CircuitRF.Design.ThreeD.C3dSetups.Select(threeD, setupName);
            if (embedded is null) return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(cemPath, why!));
            setup = CircuitRF.Design.ThreeD.C3dSetups.ForRun(embedded, cemPath);
            embeddedName = embedded.Name;
        }
        threeDPath = cemPath;
    }
    else
    {
        if (setupName is not null) return JsonRun.Fail(CliDiagnostics.EmSetupOnCem(Path.GetFileName(cemPath)));
        try
        {
            setup = EmSetupPersistence.LoadFromFile(cemPath);
        }
        catch (Exception ex)
        {
            return JsonRun.Fail(CliDiagnostics.SetupUnreadable(cemPath, ex.Message));
        }
    }

    if (referenceName is not null && solverName is null)
        return JsonRun.Fail(CliDiagnostics.EmUnknownSolver("", "--reference names a comparison's reference, and needs --solver naming the comparison."));
    if (solverName is not null)
    {
        var (parsed, why) = CircuitRF.Design.Em3d.Em3dSolvers.ParseCliSolvers(solverName, referenceName, setup.Solver3D);
        if (parsed is null) return JsonRun.Fail(CliDiagnostics.EmUnknownSolver(solverName, why));
        solver = parsed;
    }

    // brief-em3d-73 — nor a thermal one an EM one: the thermal solver is circuitRF's own.
    if (solver is not null && setup.IsThermal)
        return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(cemPath, isC3d
            ? CircuitRF.Design.ThreeD.C3dThermal.RunRefusal(setup.Name) + " --solver names an EM solver, which a thermal setup never uses."
            : CircuitRF.Design.ThreeD.C3dThermal.CemRefusal));

    // brief-em3d-21 R-em3d21-6a — --solver chooses between 3D solvers; it never makes a planar setup 3D.
    if (solver is not null && !setup.Is3D)
        return JsonRun.Fail(CliDiagnostics.EmSolverOnPlanar(solverName!, Path.GetFileName(cemPath)));
    if (solver is { } chosen) setup.Solver3D = chosen;
    if (device == CircuitRF.Design.Em3d.PalaceDevice.Gpu &&
        (setup.IsThermal || !(CircuitRF.Design.Em3d.Em3dSolvers.Any(setup.Solver3D, c => c.Gpu) ||
                                CircuitRF.Design.Em3d.Em3dSolvers.Of(setup.Solver3D).Any(d => d.Id == CircuitRF.Design.Em3d.EmergeSolver.Id))))
        return JsonRun.Fail(CliDiagnostics.EmDeviceWithoutPalace(
            $"'{Path.GetFileName(cemPath)}'" + (setup.IsThermal ? ", a thermal setup"
                : setup.Is3D ? " on " + CircuitRF.Design.Em3d.Em3dSolvers.Names(CircuitRF.Design.Em3d.Em3dSolvers.Of(setup.Solver3D))
                : ", a planar setup")));

    // R-emcli-5 — a WALK-UP, not a flag. The .cem's own ancestor .cws is what LayoutRef is relative
    // to, exactly as it is in the GUI; with no workspace above it the reference falls back to the
    // .cem's own directory, which is already-specified behaviour rather than a headless special case.
    // --workspace overrides the walk, for a .cem being run from outside its own tree.
    string? cwsPath = workspace is null
        ? WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cemPath))
        : Path.GetFullPath(workspace);

    if (workspace is not null && !File.Exists(cwsPath!))
        return JsonRun.Fail(CliDiagnostics.WorkspaceNotFound(cwsPath!));

    Console.Error.WriteLine(cwsPath is null
        ? $"[circuitRF] no workspace above '{Path.GetFileName(cemPath)}' — references resolve " +
          "against its own directory"
        : $"[circuitRF] workspace: {cwsPath}");

    // brief-em3d-42 R-em3d42-5b — a .cem whose LayoutRef names a .c3d elaborates that document.
    if (!isC3d && CircuitRF.Design.ThreeD.C3dSetups.IsThreeDView(EmSetupResolver.ResolveLayoutPath(cemPath, setup.LayoutRef, cwsPath))
        is true)
    {
        threeDPath = EmSetupResolver.ResolveLayoutPath(cemPath, setup.LayoutRef, cwsPath)!;
        if (!File.Exists(threeDPath)) return JsonRun.Fail(CliDiagnostics.FileNotFound(threeDPath));
        try { threeD = CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(threeDPath); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.SetupUnreadable(threeDPath, ex.Message)); }
    }
    var resolution = threeD is null
        ? EmSetupResolver.Resolve(cemPath, setup.LayoutRef, cwsPath, new TechnologyCache())
        : new EmSetupResolution(null, threeDPath, null, []);

    // The resolver's diagnostics are warnings about the SETUP, not the run's own notes, and they go
    // out before anything long starts — "the technology did not resolve" is the sentence that
    // explains a refusal three lines later.
    foreach (var d in resolution.Diagnostics)
    {
        Console.Error.WriteLine($"warning: {d}");
        JsonRun.Note(CliDiagnostics.EmSetupWarning(d));
    }

    if (resolution.LayoutPath is { } lp) Console.Error.WriteLine(threeD is null ? $"[circuitRF] layout: {lp}" : $"[circuitRF] 3D view: {lp}");
    if (resolution.TechnologyPath is { } tp) Console.Error.WriteLine($"[circuitRF] technology: {tp}");

    // R-emcli-7 — with no -o the run writes where the GUI writes, and that is not a default this file
    // gets to choose: EmRunService.ResolveSnpPath is a PREDICTABLE path by design so a schematic's
    // SnP reference stays valid across re-runs, and a headless run that minted a different filename
    // would orphan every one of them. -o moves the Touchstone and nothing else — and it goes in
    // through the setup's own override field, the one the panel writes, so there is no second naming
    // rule to keep in step.
    if (output is not null)
    {
        EnsureOutputDirectory(output);
        setup.SnpOutputPathOverride = Path.GetFullPath(output);
    }

    // The GUI's results root, as a headless run has it (ResultsRoot — the one rule `render --field` reads by too).
    string resultsRoot = CircuitRF.Cli.ResultsRoot.For(cemPath, cwsPath);

    // brief-em3d-79 — --set reaches a thermal setup's circuit only; an EM run has none to apply it to, so say so
    if (emSets.Count > 0 && !setup.IsThermal)
        Console.Error.WriteLine($"[circuitRF] --set {string.Join(", ", emSets.Select(x => x.Name))} is not applied: it sets a global of " +
                                "the circuit a thermal setup takes its currents from, and this setup is an EM one.");

    // R-gc2-2/3: the setup's layout, with every placed PCell rebuilt into the workspace's own folder
    // as Simulate's workspace would have it — or a refusal, since an S-parameter file solved without
    // a part's metal is an answer about a different circuit.
    int? emCellRefusal = null;
    using var emGenerated = resolution.Source is { } emSource
        ? CircuitRF.Cli.GeneratedCells.Prepare(emSource.View, emSource.AbsolutePath, mayWrite: true, out emCellRefusal)
        : null;
    if (emCellRefusal is { } emRefused) return emRefused;

    // brief-em3d-98 R-em3d98-7 — the GUI asks before replacing a complete, current result; a headless run never asks. It says
    // so in one line and runs, so a build log shows a re-run that changed nothing.
    if (embeddedName is not null && threeD is not null &&
        CircuitRF.Design.ThreeD.C3dSolveStatus.AllCurrent(
            CircuitRF.Design.ThreeD.C3dSolveStatus.OfRun(setup, embeddedName, threeD, threeDPath!, resultsRoot)))
        Console.Error.WriteLine($"note: the result for '{embeddedName}' was already current; running again");

    // brief-sar-12 — a band sweep runs each band's setup exactly as one setup runs, in turn, and then prints the band table.
    if (bandSweep is not null)
    {
        foreach (var band in bandSetups)
        {
            var bandRun = CircuitRF.Design.ThreeD.C3dSetups.ForRun(band, cemPath);
            Console.Error.WriteLine($"[circuitRF] band {band.Name}");
            EmRunResult bandResult;
            try
            {
                bandResult = EmRunService.RunThreeDView(bandRun, threeD!, threeDPath!, DocumentKinds.AncestorCws(threeDPath!), resultsRoot,
                                                        RunHost.Cancellation, EmProgressToStderr(), confirmMemory: force ? _ => true : null,
                                                        device: device, memoryCeilingBytes: memoryLimit, emergeDevice: emergeDevice);
            }
            catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message)); }
            if (ReportEmRun(bandResult, bandRun, cemPath) is var code and not 0) return code;
        }
        foreach (string line in CircuitRF.Design.ThreeD.C3dBandSweeps.TableText(
                     bandSweep, CircuitRF.Design.ThreeD.C3dBandSweeps.Table(threeD!, bandSweep, cemPath, resultsRoot)))
            Console.WriteLine(line);
        return 0;
    }

    EmRunResult result;
    try
    {
        result = threeD is not null
            ? EmRunService.RunThreeDView(setup, threeD, threeDPath!, DocumentKinds.AncestorCws(threeDPath!), resultsRoot,
                                         RunHost.Cancellation, EmProgressToStderr(), confirmMemory: force ? _ => true : null,
                                         fromCem: !isC3d, circuitSets: emSets, device: device, memoryCeilingBytes: memoryLimit, emergeDevice: emergeDevice)
            : EmRunService.Run(setup, resolution.Source, resultsRoot, RunHost.Cancellation, EmProgressToStderr(),
                               confirmMemory: force ? _ => true : null, device: device, memoryCeilingBytes: memoryLimit, emergeDevice: emergeDevice);
    }
    catch (Exception ex)
    {
        return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
    }

    return ReportEmRun(result, setup, cemPath);
}

/// <summary>
/// What every EM run prints and returns once it has a result — the setup's own run above, and
/// <c>em --component</c>'s (<see cref="EmComponent"/>), so a part extracted headlessly is reported in
/// exactly the words a drawn layout's run is.
/// </summary>
static int ReportEmRun(EmRunResult result, EmSetup setup, string cemPath)
{
    // R-emcli-6 — THREE lists, kept apart, because they ask three different things of the reader.
    // Notes are the run explaining itself, warnings are things to act on, errors are things the user
    // asked for and did not get. Flattening them into one list is the exact defect the split was
    // introduced to fix, and it is just as wrong on a terminal as it was in the Messages region.
    // The three lists carry STRINGS, not diagnostics — only the top-level refusal has a coded form
    // (EmRunResult.Diagnostic). Each is wrapped with an id naming which of the three it came from,
    // rather than having arguments invented by parsing the sentence back apart (R-aut1-7).
    //
    // EM-SEV R-emsev-1 — and they are printed WORST FIRST. A run of this shape produces of order
    // thirty-five lines; a user reading a terminal reads the first few and the last few, and until
    // now the ones saying part of their circuit was not solved were somewhere in the middle at the
    // weight of the core count. Grouping is only half the answer — the order is the other half.
    // Designer feedback round 11 — a sentence naming a file (the memory warning's .wslconfig) carries its path
    // beside it in EmRunResult.MessageFiles, which the Messages panel shows as a link; a terminal prints it.
    string WithFile(string s) => result.MessageFiles?.GetValueOrDefault(s) is { } f ? $"{s} ({f})" : s;
    // brief-dfr27-5 R-dfr27-5-3 — a line the run already printed as it went is not printed twice; --json still has every one.
    var printed = EmStreamed.Value ?? [];
    EmStreamed.Value = null;
    void Print(string line) { if (!printed.Remove(line)) Console.Error.WriteLine(line); }
    foreach (var e in result.Errors ?? [])
    { Print($"error: {WithFile(e)}");   JsonRun.Note(CliDiagnostics.EmRunError(WithFile(e))); }
    foreach (var w in result.Warnings)
    { Print($"warning: {WithFile(w)}"); JsonRun.Note(CliDiagnostics.EmRunWarning(WithFile(w))); }
    foreach (var n in result.Notes ?? [])
    { Print($"note: {WithFile(n)}");    JsonRun.Note(CliDiagnostics.EmRunNote(WithFile(n))); }

    // R-emcli-8 — a refusal stays a refusal. Each status carries a written explanation of what is
    // wrong with THIS setup; collapsing them into "EM failed" throws away the only part a user can
    // act on.
    if (result.Status != EmRunStatus.Ok)
    {
        // brief-em3d-10 R-em3d10-4 — a run through both solvers keeps what one of them produced when
        // the other fails or is stopped. Those files were written and are listed, exit code or not.
        foreach (var o in result.Outputs ?? [])
        { Console.WriteLine($"Wrote {o.Path}"); JsonRun.AddOutput(o.Kind, o.Path); }
        Console.Error.WriteLine($"{DescribeEmStatus(result.Status)}: {result.Error}");
        // The refusal's own coded form, which EmRunService has carried alongside the string since
        // brief-localization-groundwork's R-loc-5 and which the CLI has until now discarded. The
        // string stays exactly as it was: it is the contract cli.md §8 promises.
        if (result.Diagnostic is { } refusal) JsonRun.Note(refusal);
        return result.Status == EmRunStatus.Cancelled ? 130 : 1;
    }

    Console.WriteLine($"EM setup:  {(setup.Name.Length > 0 ? setup.Name : Path.GetFileNameWithoutExtension(cemPath))}");
    // A 3D run has no planar kernel; its KernelName is the solver and version (brief-em3d-7).
    Console.WriteLine(setup.Is3D || setup.IsThermal ? $"Solver:    {result.KernelName}" : $"Kernel:    {result.KernelName} ({result.Kind})");
    if ((setup.Is3D || setup.IsThermal) && JsonRun.Scratch is { } scratch)
        Console.WriteLine($"Scratch:   {scratch.Folder} ({(scratch.Source == "preference" ? "Settings ▸ Solvers" : "system temp folder")})");

    JsonRun.Data = result.Data;

    // brief-em3d-74 — a thermal run's probes and measures on stdout (the last sweep point); --json carries every cube.
    if (setup.IsThermal && result.Data is { } thermalSet) { PrintThermal(thermalSet); PrintRth(thermalSet); }

    // brief-em3d-22 R-em3d22-3c/4c — a static run's result is a matrix, printed; --json carries the cube.
    if (setup.IsStatic3D && result.Data is { } matrixSet) PrintStaticMatrix(matrixSet, setup);
    // brief-em3d-23 R-em3d23-4c — an eigenmode run's modes, printed; --json carries the cubes.
    if (setup.Is3D && setup.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Eigenmode && result.Data is { } modeSet)
        PrintModes(modeSet);

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

    // BOTH files, because they are not redundant (cli.md §8.2): the Touchstone is the network and
    // the .npy carries the diagnostics group that makes a wrong answer diagnosable.
    // A run through both solvers writes two of each and the comparison (R-em3d10-5); Outputs lists
    // them all, and SnpPath/NpyPath are then not the whole story.
    if (result.Outputs is { } outputs)
        foreach (var o in outputs) { Console.WriteLine($"Wrote {o.Path}"); JsonRun.AddOutput(o.Kind, o.Path); }
    else
    {
        if (result.SnpPath is { } snp) { Console.WriteLine($"Wrote {snp}"); JsonRun.AddOutput("touchstone", snp); }
        if (result.NpyPath is { } npy) { Console.WriteLine($"Wrote {npy}"); JsonRun.AddOutput("npy", npy); }
    }

    // brief-dfr31-4 — the setup's Create SnP schematic, made by the run service exactly as Simulate makes it.
    if (result.SnpSchematic is { } made)
    {
        if (made.Outcome is EmSnpSchematicOutcome.Created or EmSnpSchematicOutcome.Repointed)
        {
            Console.WriteLine($"{made.Sentence} ({made.SchematicPath})");
            JsonRun.AddOutput("schematic", made.SchematicPath!);
        }
        else if (made.Outcome == EmSnpSchematicOutcome.NotCreated)
        { Console.Error.WriteLine($"warning: {made.Sentence}"); JsonRun.Note(CliDiagnostics.EmRunWarning(made.Sentence)); }
        else
        { Console.Error.WriteLine($"note: {made.Sentence}");    JsonRun.Note(CliDiagnostics.EmRunNote(made.Sentence)); }
    }

    return 0;
}

/// <summary>
/// brief-em3d-74 — a thermal run's readings on stdout: each probe statistic and measure at the sweep's LAST point (the only
/// one without a sweep), with how many points there were. The whole sweep is in the .npy and in --json.
/// </summary>
static void PrintThermal(RfCore.Data.DataSet data)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    foreach (string group in new[] { CircuitRF.Design.Thermal.ThermalRunService.Group, RfCore.Data.DataSet.MeasurementsGroup }
                                 .Where(data.Groups.Contains))
        foreach (var (name, cube) in data.CubesIn(group))
        {
            if (cube.DataKind != RfCore.Data.DataKind.Real || name.EndsWith("(s)", StringComparison.Ordinal)) continue;
            double[] v = cube.RealValues;
            if (v.Length == 0) continue;
            string at = v.Length > 1 ? $"   (last of {v.Length} points)" : "";
            Console.WriteLine($"{name + ":",-28} {v[^1].ToString("G6", inv)} {cube.Unit}{at}");
        }
}

/// <summary>
/// brief-em3d-80 R-em3d80-1d — a thermal run's Rth matrix on stdout, K/W, rows (the rise) and columns (the watt) labelled with
/// the sources' names; --json carries the cube.
/// </summary>
static void PrintRth(RfCore.Data.DataSet data)
{
    string group = CircuitRF.Design.Thermal.ThermalRunService.SmallSignalGroup;
    if (!data.Groups.Contains(group) || !data.CubesIn(group).TryGetValue(CircuitRF.Design.Thermal.ThermalRunService.RthCube, out var cube) ||
        cube.Axes.Count != 2 || cube.Axes[0].Labels is not { } names) return;
    int n = names.Length;
    double[] v = cube.RealValues;
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    Console.WriteLine("Rth matrix (°C/W): the rise of each row's source per watt in each column's");
    int width = Math.Max(10, names.Max(x => x.Length) + 2);
    Console.WriteLine(new string(' ', width) + string.Concat(names.Select(x => x.PadLeft(width))));
    for (int i = 0; i < n; i++)
        Console.WriteLine(("  " + names[i]).PadRight(width) + string.Concat(Enumerable.Range(0, n).Select(j => v[i * n + j].ToString("G5", inv).PadLeft(width))));
}

/// <summary>
/// brief-em3d-22 — the capacitance or inductance matrix on stdout, in one engineering unit chosen so the
/// largest entry reads between 1 and 1000, rows and columns labelled with the terminals' names.
/// </summary>
static void PrintStaticMatrix(RfCore.Data.DataSet data, EmSetup setup)
{
    bool es = setup.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Electrostatic;
    string cubeName = es ? CircuitRF.Engine.Em3d.Em3dStaticResult.CapacitanceCube : CircuitRF.Engine.Em3d.Em3dStaticResult.InductanceCube;
    if (!data.Cubes.TryGetValue(cubeName, out var cube) || cube.Axes.Count != 2 || cube.Axes[0].Labels is not { } names) return;
    int n = names.Length;
    double[] v = cube.RealValues;
    double max = v.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max();
    string[] prefixes = ["f", "p", "n", "µ", "m", ""];
    int e = max > 0 ? Math.Clamp((int)Math.Floor(Math.Log10(max) / 3), -5, 0) : -5;
    double scale = Math.Pow(1000, -e);
    string unit = prefixes[e + 5] + (es ? "F" : "H");
    // brief-dfr31-2 — with Other metal at Ground and the ground box blank, the ground is every piece not in a terminal.
    string ground = setup.Ground3D is { Length: > 0 } g ? g
        : es && setup.UnassignedConductors3D == CircuitRF.Design.Layout.Em.EmUnassignedConductors.Ground
            ? "every body not in a terminal"
            : "the ground-reference conductors";
    Console.WriteLine($"{(es ? "Maxwell capacitance" : "Inductance")} ({unit}), ground = {ground}");
    int width = Math.Max(10, names.Max(x => x.Length) + 2);
    Console.WriteLine(new string(' ', width) + string.Concat(names.Select(x => x.PadLeft(width))));
    for (int i = 0; i < n; i++)
        Console.WriteLine(("  " + names[i]).PadRight(width) + string.Concat(Enumerable.Range(0, n).Select(j =>
            // Three decimals: at two, a coupling of a few femtofarads to a neighbour printed as -0.00 pF.
            (v[i * n + j] * scale).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).PadLeft(width))));
}

/// <summary>
/// brief-em3d-23 R-em3d23-4c — the modes on stdout: mode, f, Q (and Q with the ports' loading removed when
/// there are ports), and where Palace wrote it, each mode's largest energy participation by region — which is
/// what says where a mode lives.
/// </summary>
static void PrintModes(RfCore.Data.DataSet data)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    if (!data.Cubes.TryGetValue(CircuitRF.Engine.Em3d.Em3dEigenResult.FrequencyCube, out var f) ||
        !data.Cubes.TryGetValue(CircuitRF.Engine.Em3d.Em3dEigenResult.QCube, out var q)) return;
    data.Cubes.TryGetValue(CircuitRF.Engine.Em3d.Em3dEigenResult.UnloadedQCube, out var qu);
    data.Cubes.TryGetValue(CircuitRF.Engine.Em3d.Em3dEigenResult.ParticipationCube, out var part);
    var modes = f.Axes[0].Values;
    string[]? domains = part?.Axes[1].Labels;
    Console.WriteLine("Mode         f (GHz)           Q" + (qu is null ? "" : "   Q unloaded") + (domains is null ? "" : "   Most energy in"));
    for (int i = 0; i < modes.Length; i++)
    {
        string line = $"{((int)modes[i]).ToString(inv),4}  {(f.RealValues[i] / 1e9).ToString("0.000000", inv),14}  {Q(q.RealValues[i]),10}";
        if (qu is not null) line += $"  {Q(qu.RealValues[i]),11}";
        if (part is not null && domains is not null)
        {
            int n = domains.Length, best = 0;
            for (int k = 1; k < n; k++) if (part.RealValues[i * n + k] > part.RealValues[i * n + best]) best = k;
            line += $"   {domains[best]} ({(100 * part.RealValues[i * n + best]).ToString("0", inv)} %)";
        }
        Console.WriteLine(line);
    }

    string Q(double v) => double.IsPositiveInfinity(v) ? "lossless" : double.IsNaN(v) ? "—" : v.ToString("0.0", inv);
}

/// <summary>The run's own progress, on stderr — §3.1's split, so `circuitrf em x.cem > summary.txt`
/// still shows a full-wave sweep moving. A de-embedded point costs tens of seconds at the shipping
/// mesh, so a run reporting nothing is indistinguishable from a hung one.</summary>
static RunControl EmProgressToStderr()
{
    string lastLine = "";
    // brief-dfr27-5 R-dfr27-5-3 — a run's notes and warnings print the moment they are known (openEMS's time estimate before a
    // run of hours, not after it); ReportEmRun then prints only the ones that did not.
    var streamed = EmStreamed.Value = new List<string>();
    // The host's observer, when there is one, sees EVERY observation — the de-duplication below is a
    // terminal concern (a repeated row is noise on a screen and information to a progress bar), and
    // the host's token is what makes a long run cancellable (R-aut5-8). With no host both are absent
    // and this is the same control it always was, writing the same bytes to stderr.
    var observer = RunHost.Observer;
    return new RunControl
    {
        Token    = RunHost.Cancellation,
        Progress = new Progress<RunProgress>(p =>
        {
            observer?.Invoke(p);

            string line = p.Total > 0
                ? $"[{p.Completed}/{p.Total}] {p.Stage}"
                : $"[{p.Completed}] {p.Stage}";
            // Adaptive sampling has no honest denominator and reports the same stage repeatedly; only
            // a CHANGED line is worth a terminal row.
            if (line == lastLine) return;
            lastLine = line;
            Console.Error.WriteLine(line);
        }),
        Notes = n =>
        {
            string text = n.File is { } f ? $"{n.Text} ({f})" : n.Text;
            lock (streamed) streamed.Add((n.Warning ? "warning: " : "note: ") + text);
            Console.Error.WriteLine((n.Warning ? "warning: " : "note: ") + text);
        },
    };
}

/// <summary>brief-dfr27-5 — the lines the current EM run printed as it went (<see cref="EmProgressToStderr"/>), per thread so
/// a host running several verbs keeps them apart.</summary>
static class EmStreamed
{
    [ThreadStatic] public static List<string>? Value;
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
    if (args.Length > 0) JsonRun.InputPath = args[0];

    // One condition and one sentence, as before: `elab` has always said "input required" for a
    // missing file too. Splitting that into a not-found refusal would be a better message and a
    // change to stderr, which this brief does not get to make (R-aut0-3).
    if (args.Length == 0 || !File.Exists(args[0]))
        return JsonRun.Fail(CliDiagnostics.InputRequired("elab", ".cnl"));
    try
    {
        if (CircuitSource.ReadRunInput("elab", args[0], out int kindRefusal) is not { } source)
            return kindRefusal;
        var (lib, tb) = source;
        var nl = new Elaborator(lib) { BaseDirectory = CircuitSource.ReferenceBaseOf(args[0]) }.Elaborate(tb);
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
    catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message)); }
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
        JsonRun.Note(CliDiagnostics.WorkerOutput(name, log));

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
    // The rule itself lives in ChainSelector (src/Design/Circuit/ChainSelection.cs), because
    // `explain --analysis` has to report the same decision without making it. What stays here is
    // the two sentences a RUN writes to stderr — unchanged, character for character, because a
    // script watching stderr must not be able to tell this moved (R-aut0-3).
    var sel = ChainSelector.Select(tb, requested, isBase, kindLabel, directiveHint);
    why = sel.Why;

    if (sel.PromotedFrom is { } from && sel.Selected is { } owner)
        Console.Error.WriteLine($"[circuitRF] {ChainSelector.PromotionNote(from, owner)}");
    else if (requested is null && sel.Candidates.Count > 1)
        Console.Error.WriteLine($"[circuitRF] {ChainSelector.AmbiguityNote(sel, kindLabel)}");

    return sel.Selected;
}

/// The base (non-sweep) analysis a chain bottoms out in.
static Analysis? BaseOfChain(Analysis top, TestBench tb) => ChainSelector.BaseOfChain(top, tb);


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

static AnalysisSettings SolverSettingsFrom(int? maxIter, bool diag) => HbCircuitRun.Settings(maxIter, diag);


/// <summary>
/// Evaluates the TestBench's measurements against the run, exactly as the GUI does — including the
/// back-solver, so a measurement may name a LINEAR-interior node (a port behind an N-port, say) and
/// not only the nonlinear interface nodes that appear in the <c>V</c> cube.
/// </summary>
static DataSet? EvaluateMeasurements(TestBench tb, ElaboratedNetlist nl, string analysisName,
                                     DataSet ds, HbRunResult? run)
{
    // brief-em3d-79: the evaluation is HbCircuitRun.Measure's, shared with the thermal circuit link; what stays here is
    // reporting each failure the way this verb always has.
    var measDs = HbCircuitRun.Measure(tb, nl, analysisName, ds, run, out var errors);
    foreach (var e in errors)
    {
        Console.Error.WriteLine($"[circuitRF] measurement: {e}");
        JsonRun.Note(CliDiagnostics.MeasurementFailed(e));
    }
    return measDs;
}

/// Combines the analysis cubes and the measurement cubes into one DataSet for export, mirroring the
/// GUI's grouped run DataSet (analysis group + "measurements" group).
static DataSet MergeForExport(DataSet ds, DataSet measDs) => HbCircuitRun.Merge(ds, measDs);

internal static ExportFormat FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".npy"          => ExportFormat.Npy,
    ".txt" or ".tsv" => ExportFormat.Tsv,
    _               => ExportFormat.Mat,
};

/// <summary>Whether this path names a Touchstone file — <c>.s1p</c>, <c>.s2p</c>, … <c>.s99p</c> —
/// read through <see cref="TouchstoneIO.ParsePortsFromExtension"/> so the CLI and the reader agree
/// about what one is called.</summary>
static bool IsTouchstonePath(string path) => TouchstoneIO.ParsePortsFromExtension(path) is not null;

/// <summary>
/// The cube-file format <c>sparam</c>'s <c>-o</c> names, or null for an extension it cannot write.
/// Unlike <see cref="FormatFromExtension"/> there is NO default: `sparam` writing a <c>.mat</c>
/// because it did not recognise <c>.s2p_old</c> is the defect R-aut9-1 is about, one extension
/// along.
/// </summary>
static ExportFormat? SparamDataFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".npy"           => ExportFormat.Npy,
    ".mat"           => ExportFormat.Mat,
    ".txt" or ".tsv" => ExportFormat.Tsv,
    _                => null,
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
internal static void PrintCube(string label, DataCube cube, int maxRows, bool allPoints)
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

/// <summary>
/// The WSProbes of a run, one line each — <c>WSProbe GATE idx=1  H0(f_lo)=… ZG(f_lo)=…</c> — and
/// the same label ↔ idx pairs into the <c>--json</c> document (brief-wsprobe-1 R-wsp1-12(a)).
/// Silent for a run with none, so an unprobed run prints exactly what it always printed.
///
/// <para><b>Both run verbs.</b> <c>sparam</c>'s probe axis is <c>freq</c> and <c>hb</c>'s is the
/// small-signal <c>ssfreq</c> of brief-wsprobe-5, and the summary is the same line over either —
/// R-wsp5's "the run summary prints the per-operating-point minimum", and R-wsp1-12(a)'s label ↔
/// idx map, which a caller cannot guess. The axis is read off the probe's OWN cubes rather than
/// passed in, so a swept run (the sweep axis prepended by <c>ParametricSweepEngine</c>) still
/// names the frequency the minimum sits at.</para>
/// </summary>
/// <param name="announceNoPorts">
/// Print the <c>S-parameters: none (no ports)</c> line when the run produced no <c>S</c>. Only the
/// S-parameter verb has that story to tell — under harmonic balance a run with no ports is
/// ordinary and the line would be noise.
/// </param>
static void PrintWsProbes(DataSet ds, ElaboratedNetlist nl, bool announceNoPorts)
{
    if (nl.WspProbes.Count == 0 || !ds.Contains("__WspProbes")) return;

    var probes = nl.WspProbes;
    if (announceNoPorts && !ds.Contains("S"))
        Console.WriteLine($"S-parameters: none (no ports); WSProbe outputs: {probes.Count} probe(s)");

    // The probe axis, from the probe's own cubes: `freq` under S-parameters, `ssfreq` under
    // harmonic balance. Last-axis by construction — the sweep engine prepends, never appends.
    double[] freqs = ProbeAxis(ds, probes[0].Label);
    if (freqs.Length == 0) return;

    var rows = new List<WsProbeJson>(probes.Count);
    foreach (var probe in probes)
    {
        var h0 = (Complex)ds[$"H0:{probe.Label}"][0];
        var zg = (Complex)ds[$"ZG:{probe.Label}"][0];
        var line = new StringBuilder(
            $"WSProbe {probe.Label} idx={probe.Idx}  H0({freqs[0] / 1e9:G4} GHz)={FormatComplex(h0)}  " +
            $"ZG({freqs[0] / 1e9:G4} GHz)={FormatComplex(zg)}");

        // R-wsp9-2: each margin's minimum over the sweep and where it sits. In dB on the line
        // (20·log10, overview D-16) and LINEAR in --json, because dB is a display convention and a
        // document carries the number.
        var (yMin, yHz) = MarginMinimum(ds, $"SM_Y0:{probe.Label}", freqs);
        var (hMin, hHz) = MarginMinimum(ds, $"SM_H0:{probe.Label}", freqs);
        if (yMin is not null) line.Append($"  SM_Y0 min {MarginDb(yMin.Value)} @ {yHz!.Value / 1e9:G6} GHz");
        if (hMin is not null) line.Append($"  SM_H0 min {MarginDb(hMin.Value)} @ {hHz!.Value / 1e9:G6} GHz");
        Console.WriteLine(line.ToString());

        rows.Add(new WsProbeJson(probe.Label, probe.Idx, yMin, yHz, hMin, hHz));
    }
    JsonRun.Wsprobes = rows;
}

/// <summary>
/// The NDF summary of an <c>NDF=yes</c> run: <c>NDF: N right-half-plane pole(s)</c>, the unrounded
/// encirclement it was rounded from, and the <c>ndf.*</c> property findings the engine raised
/// (brief-wsprobe-6 R-wsp6-2). Silent on a run that carried no NDF.
/// </summary>
static void PrintNdf(DataSet ds, ElaboratedNetlist nl, double[] freqs)
{
    if (!ds.Contains("NDF") || !ds.Contains("NDF_poles")) return;

    int    poles = (int)ds["NDF_poles"].RealValues[0];
    var    enc   = ds["NDF_enc"].RealValues;
    double net   = enc.Length > 0 ? enc[^1] : 0.0;
    var    ndf   = ds["NDF"].ComplexValues;

    Console.WriteLine(
        $"NDF: {poles} right-half-plane pole(s)  " +
        $"(net clockwise encirclement {net:G4}; NDF({freqs[^1] / 1e9:G4} GHz)={FormatComplex(ndf[^1])})");

    var findings = FindingKeys(nl);
    foreach (var f in findings) Console.WriteLine($"  NDF finding: {f}");

    JsonRun.Ndf = new NdfReportJson(
        poles, net,
        AtFmax: [ndf[^1].Real, ndf[^1].Imaginary],
        AtFmin: [ndf[0].Real,  ndf[0].Imaginary],
        Findings: findings.Count > 0 ? findings : null);
}

/// <summary>
/// The <c>ndf.*</c> diagnostic KEYS a run raised, deduplicated and in order — the keys rather than
/// the sentences, because the sentences are already printed as warnings and a JSON consumer wants
/// something it can branch on.
///
/// <para>Read from the START of each message only. Matching a key ANYWHERE in the text picks up the
/// ones a message quotes in its own advice — the counter-clockwise note tells the reader to go and
/// look at the passivation notes, and that sentence was being reported as a passivation note.</para>
/// </summary>
static List<string> FindingKeys(ElaboratedNetlist nl)
{
    var keys = new List<string>();
    foreach (string w in nl.Warnings)
    {
        if (!w.StartsWith("ndf.", StringComparison.Ordinal)) continue;
        int end = 0;
        while (end < w.Length && (char.IsLetterOrDigit(w[end]) || w[end] is '.' or '-')) end++;
        string key = w[..end].TrimEnd('.');
        if (!keys.Contains(key)) keys.Add(key);
    }
    return keys;
}

/// <summary>
/// The probe's own sweep axis — <c>freq</c> under S-parameters, <c>ssfreq</c> under the
/// harmonic-balance small-signal sweep of brief-wsprobe-5. Taken from the LAST axis of one of the
/// probe's cubes, which is that axis in both cases: <c>ParametricSweepEngine</c> prepends its
/// sweep variable, so a drive-swept run's <c>SM_Y0:GATE</c> is <c>{Pin, ssfreq}</c> and the
/// frequency is still innermost. Empty when the run carried none of the probe's cubes.
/// </summary>
static double[] ProbeAxis(DataSet ds, string label)
{
    foreach (string name in new[] { $"SM_Y0:{label}", $"SM_H0:{label}", $"H0:{label}", $"ZG:{label}" })
    {
        if (!ds.Contains(name)) continue;
        var cube = ds[name];
        if (cube.Rank == 0) continue;
        return cube.Axis(cube.Rank - 1).Values;
    }
    return [];
}

/// <summary>The smallest non-NaN value of a margin cube and the frequency it sits at, or two nulls
/// when the run carried no such cube (or every point is NaN — a degenerate probe).
///
/// <para>The cube may carry a sweep axis in front of the frequency one (a drive-swept HB run), so
/// the minimum is taken over EVERY point and the frequency read out modulo the frequency axis —
/// which is R-wsp5's per-operating-point minimum reported as one number for the run.</para></summary>
static (double? Min, double? Hz) MarginMinimum(DataSet ds, string cube, double[] freqs)
{
    if (!ds.Contains(cube) || freqs.Length == 0) return (null, null);
    var v = ds[cube].RealValues;
    int best = -1;
    for (int k = 0; k < v.Length; k++)
    {
        if (double.IsNaN(v[k])) continue;
        if (best < 0 || v[k] < v[best]) best = k;
    }
    return best < 0 ? (null, null) : (v[best], freqs[best % freqs.Length]);
}

/// <summary>A margin in dB for the summary line: <c>20·log10</c> (overview D-16), with a
/// typographic minus so it reads as the paper prints it.</summary>
static string MarginDb(double linear)
{
    if (linear <= 0.0) return "\u2212inf dB";
    string s = $"{20.0 * Math.Log10(linear):F1}";
    return (s.StartsWith('-') ? "\u2212" + s[1..] : s) + " dB";
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

/// <summary>
/// Pulls <c>--workspace &lt;dir | .cws&gt;</c> out of the argument list, returning what remains.
/// <paramref name="root"/> is the workspace root it names, or null when the flag is absent or names
/// no workspace (then <paramref name="given"/> is what was written, for the refusal). The last one
/// given wins.
/// </summary>
static string[] TakeHostWorkspace(string[] args, out string? given, out string? root)
{
    given = null;
    root  = null;
    var rest = new List<string>(args.Length);

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--workspace" && i + 1 < args.Length)
        {
            given = args[++i];
            continue;
        }
        rest.Add(args[i]);
    }

    if (given is not null)
    {
        string full = Path.GetFullPath(given);
        string? dir = string.Equals(Path.GetFileName(full), ".cws", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full)
            : full;
        if (dir is not null && File.Exists(Path.Combine(dir, ".cws")))
            root = WorkspaceRootFinder.Normalize(dir);
    }
    return rest.ToArray();
}

/// <summary>
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
    {
        Console.Error.WriteLine($"[circuitRF] {nl.Notes[from.Notes]}");
        JsonRun.Note(CliDiagnostics.ElaborationNote(nl.Notes[from.Notes]));
    }

    for (; from.Warnings < nl.Warnings.Count; from.Warnings++)
    {
        Console.Error.WriteLine($"[circuitRF] {nl.Warnings[from.Warnings]}");
        JsonRun.Note(CliDiagnostics.ElaborationWarning(nl.Warnings[from.Warnings]));
    }

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
    Console.WriteLine("  sparam <file.cnl|.csch> [--freq start:stop:step] [-o out.sNp]");
    Console.WriteLine("           (NDF=yes on the directive adds the normalized determinant function");
    Console.WriteLine("            and the network's right-half-plane pole count)");
    Console.WriteLine("  dc     <file.cnl|.csch> [-o out.{npy,mat,txt}]   (DC operating point; runs the sweep if one wraps it)");
    Console.WriteLine("  hb     <file.cnl|.csch>   (harmonic balance; runs the sweep if one wraps it)");
    Console.WriteLine("  lp     <file.cnl|.csch>   (loadpull over the directive's Gamma grid)");
    Console.WriteLine("  lpp    <file.cnl|.csch>   (loadpull pursuit: searches for MXP / MXE)");
    Console.WriteLine("  em     <file.cem>   (electromagnetic extraction of the layout it names)");
    Console.WriteLine("  opt    <file.cnl|.csch>   (optimize: the tune, goal and optimize lines; exit 3 = a goal unmet)");
    Console.WriteLine("  yield  mc|estimate|trial <file.cnl|.csch>   (Monte Carlo / yield; exit 3 = below --target)");
    Console.WriteLine("  model  new|import|deembed|fit|check-charge|explain|export|validate|report <file.cmc>");
    Console.WriteLine("           (the Model Composer: a FET model or a passive cell from data; exit 3 = a check failed)");
    Console.WriteLine("  rail   <file.crail> (railRF: the DC drop, the ranked breakdown, the vias)");
    Console.WriteLine("  smith  <file.csmith> (the matching cascade: the reading, and the walk node by node)");
    Console.WriteLine("  snp    <file.sNp>    (the S-Parameter Viewer: what the file states, and its Smith, dB and phase plots)");
    Console.WriteLine("  elab   <file.cnl|.csch>   (dump elaborated netlist)");
    Console.WriteLine("  netlist <path.csch> [-o out.cnl]  (the extraction Simulate performs)");
    Console.WriteLine("  convert <in> -o <out>  (layout interchange: any format to any other)");
    Console.WriteLine("  new workspace <dir>    (a workspace, with a technology copied in)");
    Console.WriteLine("  new cell <ws> <name>   (a cell folder with its view files)");
    Console.WriteLine("  import part <file>     (a footprint and its symbol, as a cell)");
    Console.WriteLine("  import schematic <file> (a .kicad_sch, EDIF or binary .DSN design, as a new cell)");
    Console.WriteLine("  check   <path>         (is it well formed, does it resolve, is it sound)");
    Console.WriteLine("  explain <path>         (what did circuitRF resolve it to, and by which walk)");
    Console.WriteLine("  lvs     <path>         (does the artwork implement the drawing?)");
    Console.WriteLine("  recognize <file.clay> [-o x.cnl] [--into new:<name>]  (a board's artwork as a circuit)");
    Console.WriteLine("  impedance <layout> [-o report.pdf]  (every trace's Z0 against a target, and its return path)");
    Console.WriteLine("  render  <path> -o out.svg  (a schematic, symbol or layout as a picture)");
    Console.WriteLine("  read    <path>         (a result file as cubes, or a document as its own text)");
    Console.WriteLine("  plot    <result> -o out.svg --trace cube=S,i=2,j=1,y=db20 (one picture, no .cdd)");
    Console.WriteLine("  find    <root>         (what is here: workspaces, cells, views, analyses)");
    Console.WriteLine("  reference [topic] [type]  (what a caller may WRITE: the prose pages, and the");
    Console.WriteLine("                          generated component catalogue. Takes no path.)");
    Console.WriteLine($"  solver list            ({string.Join(", ", CircuitRF.Design.Em3d.Em3dSolvers.Tools.Select(t => CircuitRF.Design.Em3d.SolverDiscovery.For(t).Name))}: found where, which version, what they can do)");
    Console.WriteLine($"  solver install <{string.Join("|", Solver.ToolIds)}> [--yes]  (from its own upstream; without --yes,");
    Console.WriteLine("                          prints what it would fetch and where it would go, and exits 1)");
    Console.WriteLine("  serve   --root <dir>   (a protocol server on stdin/stdout, for an external client)");
    Console.WriteLine("  --version              (the version of this build, and nothing else)");
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
    Console.WriteLine("rail options:");
    Console.WriteLine("  --rail NAME             which rail. Omitted runs them ALL, in dependency order.");
    Console.WriteLine("  --fast (default) / --accurate   which of the two readings of the geometry");
    Console.WriteLine("  --no-escalate           keep Fast's spreading-copper refusal instead of meshing that rail");
    Console.WriteLine("  --source REFDES.PIN=<3.7V,50mOhm,10nH | file.s1p>   repeatable");
    Console.WriteLine("  --load REFDES.PIN[=<120mA>]     repeatable. No current = an OBSERVATION port.");
    Console.WriteLine("  --target-drop 80mV      --target-z 2.5mOhm    --mask [PORT=]<file>");
    Console.WriteLine("  --aggressor NAME=<2.2MHz>[xN]   repeatable");
    Console.WriteLine("  --reference <layer>     the reference return. railRF never infers one.");
    Console.WriteLine("  --extent as-imported|filled|infinite   (the last two are OPTIMISTIC)");
    Console.WriteLine("  --rows N, --all         how much of the ranked breakdown to print");
    Console.WriteLine("  -o out.{csv,npy,mat,txt,svg,pdf}   .svg/.pdf is the report page");
    Console.WriteLine();
    Console.WriteLine("smith options:");
    Console.WriteLine("  --at <freq>             evaluate here instead of the design frequency.");
    Console.WriteLine("                          Outside the generator table's span is a refusal.");
    Console.WriteLine("  --sweep                 force the document's swept band on");
    Console.WriteLine("  -o out.s1p              the load reflection coefficient as Touchstone");
    Console.WriteLine("  -o out.{svg,pdf,png}    the chart, drawn by the renderer the window draws with");
    Console.WriteLine("  --size WxH  --scale N | --dpi N  --background opaque|transparent  --dark");
    Console.WriteLine();
    Console.WriteLine("snp options:");
    Console.WriteLine("  --params S11,S21|all|reflections|default   the terms plotted. Default: every term up to");
    Console.WriteLine("                          4 ports, else the reflections and each S(i+1,i).");
    Console.WriteLine("  -o out.{svg,pdf,png}    the viewer's four plots, drawn by the renderer the window draws with");
    Console.WriteLine("  -o out.cdd              the same plots as a Data Display");
    Console.WriteLine("  --size WxH  --scale N | --dpi N  --background opaque|transparent  --dark");
    Console.WriteLine("                          Exit 1 when the file has a defect, after writing.");
    Console.WriteLine();
    Console.WriteLine("lvs options:   <path> is a cell folder, a workspace, a .clay or a .csch");
    Console.WriteLine("  --flat                  every placed cell as a leaf; nothing is descended into");
    Console.WriteLine("  --flatten-cell <name>   flatten this one cell. Repeatable.");
    Console.WriteLine("  --testbench             compare the fixture too (ports, sources, tuners)");
    Console.WriteLine("  --no-reduce             do not collapse series/parallel/jumpers");
    Console.WriteLine("  --set <var=expr>        override a global before elaboration. Repeatable.");
    Console.WriteLine("  --severity warning|error   what decides the exit code. Default error;");
    Console.WriteLine("                          warnings are reported either way.");
    Console.WriteLine("  -o report.txt           the human report. With no -o nothing is written.");
    Console.WriteLine();
    Console.WriteLine("impedance options:   <layout> is a .clay or a cell folder holding one");
    Console.WriteLine("  --target 50             the target Z0, ohms. Default 50.");
    Console.WriteLine("  --tol 10                the tolerance, percent either side. Default 10.");
    Console.WriteLine("  --warn 20               the warning band, percent either side; wider than --tol. Default 20.");
    Console.WriteLine("  --max-freq 6GHz         a stretch under λ/20 here warns rather than fails; test pads are judged here. Unit required.");
    Console.WriteLine("  --severity warning|fail what decides the exit code. Default fail; warnings are");
    Console.WriteLine("                          reported either way.");
    Console.WriteLine("  --layers \"A,B\"          the copper layers, by name. Default every copper layer.");
    Console.WriteLine("  --max-width <um>        the widest copper read as a trace. Default from the stackup.");
    Console.WriteLine("  -o report.pdf           the report the layout editor exports. Exit 0 none fail,");
    Console.WriteLine("                          1 any fail, 130 cancelled (the finished layers are written).");
    Console.WriteLine();
    Console.WriteLine("convert options:");
    Console.WriteLine("  formats: clay | gdsii | oasis | dxf | gerber | board — inferred from the paths");
    Console.WriteLine("           (.clay .gds .oas .dxf .kicad_pcb; a FOLDER is a Gerber file set)");
    Console.WriteLine("  -o, --output <path>     the file to write, or the FOLDER for gerber / clay");
    Console.WriteLine("  --from f, --to f        say the format when the path does not");
    Console.WriteLine("  --cell <name>           which cell, when the source holds several");
    Console.WriteLine("  --name <stem>           what to call the written file set (gerber)");
    Console.WriteLine("  --list-cells            report what the input holds, write nothing");
    Console.WriteLine("  --tech <file.ctech>     the technology to convert against");
    Console.WriteLine("  --keep-cells <dir>      keep the cells an import produced");
    Console.WriteLine("  --engine native|gdstk   the GDSII reader or writer for a gdsii end (default native)");
    Console.WriteLine("  --oas-compression 6     an oasis target's CBLOCK level, 0 (stored) to 9");
    Console.WriteLine("  --oas-validation crc32  none | crc32 | checksum32");
    Console.WriteLine("  --oas-standard-properties   write OASIS's standard properties");
    Console.WriteLine("                          (oasis and --engine gdstk need the gdstk worker)");
    Console.WriteLine("  --dxf-version AC1032    AC1015 | AC1018 | AC1032   --dxf-units <n>");
    Console.WriteLine("  --drill-units mm|inch   --drill-format <int>:<dec>");
    Console.WriteLine("  --drill-zeros leading|trailing");
    Console.WriteLine("  --accept-inferred-drill-format   proceed on a guessed Excellon format");
    Console.WriteLine("  --arc-tolerance <len>   a gerber source's arc flattening, with its unit,");
    Console.WriteLine("                          1nm to 100um (default 1um)");
    Console.WriteLine("  --open-archives         look inside an archive when a Gerber folder holds");
    Console.WriteLine("                          no artwork of its own (unpacked to a temporary");
    Console.WriteLine("                          folder and deleted again; never done unasked)");
    Console.WriteLine();
    Console.WriteLine("new / import options:");
    Console.WriteLine("  new workspace <dir> [--name N] [--tech <id>|none]");
    Console.WriteLine("                          <dir> IS the workspace, or is its parent when");
    Console.WriteLine("                          --name is given. --tech defaults to the same entry");
    Console.WriteLine("                          the New Workspace dialog pre-selects.");
    Console.WriteLine("  new cell <workspace-or-dir> <cellName> [--views schematic,symbol,layout]");
    Console.WriteLine("                          --views defaults to schematic, which is what the");
    Console.WriteLine("                          GUI's own New Cell creates.");
    Console.WriteLine("  import part <file-or-folder> --into <workspace-or-dir>");
    Console.WriteLine("  --cell N, --variant V    which part, when the source holds several");
    Console.WriteLine("  --list-parts            report what the source holds, create nothing");
    Console.WriteLine("  --tech <file.ctech>     the technology the layers reconcile against");
    Console.WriteLine("  --add-layers            write the part's new layers into that technology");
    Console.WriteLine("  import schematic <file> --into <workspace-or-dir>");
    Console.WriteLine("  --cell N                the new cell's name. Default: the file's stem");
    Console.WriteLine("  --port <ref-or-net>     make a connector or a net a circuitRF Port (repeat; none by default)");
    Console.WriteLine("  --symbol-cell <ref>     keep a part as the source's own symbol, not a native R/L/C (repeat)");
    Console.WriteLine("  --codepage N            an EDIF or binary .DSN file's text encoding. Default 1252");
    Console.WriteLine("  --list                  report the pages, the parts and the port candidates; write nothing");
    Console.WriteLine("                          Exit 1 when the connectivity check is not matched (the cell is written).");
    Console.WriteLine();
    Console.WriteLine("check options:");
    Console.WriteLine("  <path>                  a workspace, a cell folder, or one .csch .csym");
    Console.WriteLine("                          .clay .ctech .cem .cnl .wasm — the kind is inferred");
    Console.WriteLine("  --recursive, -r         descend a plain folder (a workspace always does)");
    Console.WriteLine("  --severity warning|error  what decides the exit code. Default: error —");
    Console.WriteLine("                          warnings are still reported and still exit 0.");
    Console.WriteLine("                          Runs no analysis and writes nothing.");
    Console.WriteLine();
    Console.WriteLine("explain options:");
    Console.WriteLine("  --expr \"<expression>\"   evaluate it in the design's resolved scope");
    Console.WriteLine("  --set <var=expr>        override a global first, exactly as a run verb does");
    Console.WriteLine("  --analysis [<name>]     every runnable chain, which one dispatches, and");
    Console.WriteLine("                          whether a named inner analysis would be promoted");
    Console.WriteLine("  --ref <relative-ref>    what that reference resolves to from this document");
    Console.WriteLine("                          With none of the three: the document's own walks —");
    Console.WriteLine("                          its workspace, its layout, its technology.");
    Console.WriteLine();
    Console.WriteLine("render options:");
    Console.WriteLine("  <path>                  a .csch .csym .clay, a cell folder (--view), or a");
    Console.WriteLine("                          workspace (--cell). A document belonging to no");
    Console.WriteLine("                          workspace renders on the fallback palette.");
    Console.WriteLine("  -o <out.svg|.pdf|.png>  REQUIRED — the extension picks the format.");
    Console.WriteLine("                          --format svg|pdf|png overrides it.");
    Console.WriteLine("  --fit                   the whole document (default), with --margin <f>");
    Console.WriteLine("  --window x0,y0,x1,y1    an explicit world rectangle. On a LAYOUT every");
    Console.WriteLine("                          coordinate carries a unit (500um, 0.5mm) — a bare");
    Console.WriteLine("                          number is refused, never guessed.");
    Console.WriteLine("  --center x,y --span w   a centre and a width; height follows the aspect");
    Console.WriteLine("  --size WxH              device pixels (png) or points (svg/pdf). 1600x1200");
    Console.WriteLine("  --scale n / --dpi n     png only; --dpi is relative to 96");
    Console.WriteLine("  --layers a,b            layout only — render only these");
    Console.WriteLine("  --hide-layers a,b       layout only — render everything except these");
    Console.WriteLine("  --detail full|screen|<px>  full (default) pins exact stored geometry;");
    Console.WriteLine("                          screen engages the LOD tiers as a canvas would");
    Console.WriteLine("  --theme name|f.ccolor   --variant light|dark   --grid   --no-rulers");
    Console.WriteLine("  --background opaque|transparent");
    Console.WriteLine();
    Console.WriteLine("read options:");
    Console.WriteLine("  <path>                  a .npy or Touchstone result, read back as cubes, or");
    Console.WriteLine("                          one of circuitRF's own documents, returned verbatim.");
    Console.WriteLine("                          --only / --group / --at / --range / --result narrow");
    Console.WriteLine("                          a result. Writes nothing.");
    Console.WriteLine();
    Console.WriteLine("reference options:");
    Console.WriteLine("  <no arguments>          the topic list, with each topic's size in bytes");
    Console.WriteLine("  <topic>                 that page, as its own text");
    Console.WriteLine("  components              the catalogue: every .cnl type token, its nets and");
    Console.WriteLine("                          its parameters, generated from the live registries");
    Console.WriteLine("  components <TYPE>       just that primitive");
    Console.WriteLine("                          Reads no file, runs nothing and writes nothing.");
    Console.WriteLine();
    Console.WriteLine("serve options:");
    Console.WriteLine("  --root <dir>            REQUIRED. Every path a client names resolves under");
    Console.WriteLine("                          it; one that escapes is refused, never clamped.");
    Console.WriteLine("                          stdout carries the protocol and nothing else, so");
    Console.WriteLine("                          progress and notes stay on stderr as always.");
    Console.WriteLine("                          --kits applies to every call the server serves.");
    Console.WriteLine("  --print-config          start nothing: print the JSON an MCP client needs to");
    Console.WriteLine("                          start this server (stdout), and the Claude Code");
    Console.WriteLine("                          one-liner for it (stderr).");
    Console.WriteLine();
    Console.WriteLine("Options (any command):");
    Console.WriteLine("  --kits <dir>        folder of installed kits, for externally-provided");
    Console.WriteLine("                      devices (ExtDevice Provider=...). Repeatable.");
    Console.WriteLine("  --workspace <dir>   the workspace a loose .csch (one outside any workspace");
    Console.WriteLine("                      folder) is run from: a cell reference that names nothing");
    Console.WriteLine("                      is looked for there. em and convert read their own.");
    Console.WriteLine("  --json              one JSON document on stdout and nothing else; progress,");
    Console.WriteLine("                      notes and errors stay on stderr. A failed run still");
    Console.WriteLine("                      emits a document — the failure is the payload.");
    Console.WriteLine("  --only a,b          narrow the document's cubes to these");
    Console.WriteLine("  --group g,h         narrow the document's groups to these");
    Console.WriteLine("  --at axis=value     narrow by AXIS: --at freq=2GHz. Nearest grid point, and");
    Console.WriteLine("                      the document says which one it returned.");
    Console.WriteLine("  --interp            make every --at interpolate between the bracketing");
    Console.WriteLine("                      points instead. Never the default: it returns a number");
    Console.WriteLine("                      the run did not compute.");
    Console.WriteLine("  --range axis=lo:hi  keep a band of an axis: --range freq=1GHz:3GHz");
    Console.WriteLine("  --result full|summary   summary returns the shape, units and extents and no");
    Console.WriteLine("                      values. The shape comes back either way.");
    Console.WriteLine("  --summary           report notes as counts by severity. Warnings and errors");
    Console.WriteLine("                      still travel in full; stderr is untouched.");
    Console.WriteLine();
    Console.WriteLine("Frequency format: 1GHz, 100MHz, 1e9 (Hz bare)");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --freq 1GHz:3GHz:50MHz -o hero1.s4p");
    Console.WriteLine("Example: circuitrf hb hero5.cnl --set Pavl_dbm=0 -o hero5.txt");
    Console.WriteLine("Example: circuitrf lp hero3.cnl --pin -20:1:15 -o hero3.spl");
    Console.WriteLine("Example: circuitrf lpp hero3B.cnl --out-grid found.gam -o hero3B.npy");
    Console.WriteLine("Example: circuitrf em  Amp.cem -o /tmp/amp.s2p");
    Console.WriteLine("Example: circuitrf convert Filter.dxf -o gerbers/");
    Console.WriteLine("Example: circuitrf convert fab/ -o board.kicad_pcb");
    Console.WriteLine("Example: circuitrf read run.npy --at freq=2GHz --only S --json");
    Console.WriteLine("Example: circuitrf sparam hero1.cnl --result summary --json");
    Console.WriteLine("Example: circuitrf reference netlist");
    Console.WriteLine("Example: circuitrf reference components MLIN --json");
    Console.WriteLine("Example: circuitrf new workspace ~/designs/Amp --tech pcb-4layer_FR-4_62mil_1oz");
    Console.WriteLine("Example: circuitrf new cell ~/designs/Amp Stage1 --views schematic,symbol");
    Console.WriteLine("Example: circuitrf import part parts/ --into ~/designs/Amp --cell SOT-23");
    Console.WriteLine("Example: circuitrf read results/Amp_em.npy --only S --json");
    Console.WriteLine("Example: circuitrf render Stage1/layout/Stage1.clay -o stage1.svg");
    Console.WriteLine("Example: circuitrf render Amp --cell Stage1 --view schematic -o s1.png --scale 2");
    Console.WriteLine("Example: circuitrf render board.clay -o crop.png --window 0um,0um,500um,300um");
    Console.WriteLine("Example: circuitrf serve --root ~/designs");
    return 0;
}

}
